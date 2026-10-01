using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using VCCad.Core.Model;
using VCCad.Core.Raster;

namespace VCCad.App.Controls;

/// <summary>
/// Draws a filtered object by rasterising it, running the filter over the pixels, and drawing the result.
///
/// Filters **are** raster operations - there is no way to express a blur, a composite's arithmetic or a blend as
/// vector geometry - so an object that has one cannot be drawn with the same <c>DrawGeometry</c> calls as one that
/// does not. It is rendered into an offscreen bitmap over the filter's region, the engine runs, and the bitmap is
/// drawn in its place.
///
/// **The region is the picture.** It is computed from the object's bounds and the filter's own `x`/`y`/`width`/
/// `height`, and the object is drawn into it at the canvas's own scale - so a blur is measured in model units and
/// comes out the same size relative to the artwork whatever the zoom, which is the whole point of applying the
/// filter in model space rather than after the fact.
/// </summary>
internal static class FilterRenderer
{
    /// <summary>What the caller draws: the filtered bitmap, the model rectangle to put it in, and the
    /// renderer-supplied inputs the graph read that this caller could not honestly produce.</summary>
    internal readonly record struct Result(
        WriteableBitmap Bitmap, Rect Destination, IReadOnlyList<string> Unsupplied);

    /// <summary>
    /// Renders <paramref name="paint"/> over the filter's region, filters it, and returns the bitmap and where it
    /// belongs.
    ///
    /// <paramref name="world"/> is the canvas transform in force - model units to screen - so the offscreen render
    /// lines up pixel for pixel with what the canvas would have drawn. <paramref name="scale"/> is that transform's
    /// scale, and is what the engine uses to measure the blur and the offsets in pixels.
    /// </summary>
    public static Result? Render(
        FilterSpec filter,
        Rect bounds,
        Matrix world,
        double scale,
        Action<DrawingContext> paint)
        => Render(new[] { filter }, bounds, world, scale, paint);

    /// <summary>
    /// The same, for a **chain** of filters applied in order.
    ///
    /// A stroke's raster effects are several effects that each compose with the artwork and with each other, and the
    /// model keeps them in order because the order is the picture. So they are applied one after another, each
    /// treating the previous result as its source. The first filter's region decides the canvas, since it is the one
    /// that says how far the result can spread.
    ///
    /// <paramref name="fillPaint"/> and <paramref name="strokePaint"/> draw the shape in its **fill alone** and its
    /// **stroke alone**, which is what SVG's `FillPaint` and `StrokePaint` inputs are and what the pixels painted
    /// together cannot be taken apart into. They are the caller's, because only it knows how the object was painted;
    /// one left out is **not invented** - the engine reads that input as transparent and names it in
    /// <see cref="Result.Unsupplied"/>. Only the ones a graph actually reads are rasterised, so a filter that reads
    /// neither costs no extra allocation.
    ///
    /// <paramref name="objectBounds"/> is the shape's own box in model units, which is what an
    /// `objectBoundingBox` primitive length is a fraction of. Left out, the engine measures the alpha's extent
    /// instead - right for a shape that fills what it draws and wrong for one that does not.
    /// </summary>
    public static Result? Render(
        IReadOnlyList<FilterSpec> filters,
        Rect bounds,
        Matrix world,
        double scale,
        Action<DrawingContext> paint,
        Action<DrawingContext>? fillPaint = null,
        Action<DrawingContext>? strokePaint = null,
        Rect? objectBounds = null)
    {
        if (filters.Count == 0)
        {
            return null;
        }

        FilterSpec filter = filters[0];

        if (scale <= 0.01 || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return null;
        }

        Geometry.Rect2D sourceBounds = new(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        (int regionX, int regionY, int width, int height) = FilterEngine.RegionPixels(
            filter, sourceBounds, scale);

        // A region larger than anyone would want to allocate is a file with a wild filter region rather than a
        // request to draw it; falling back to unfiltered keeps the artwork visible.
        const int Limit = 8192;
        if (width <= 0 || height <= 0 || width > Limit || height > Limit)
        {
            return null;
        }

        // The bitmap is the artwork on **its own pixel grid**, so the canvas transform's scale is applied and
        // its translation is not. The region is an absolute box in model units, so a transform that carried the
        // pan would shift the artwork inside a bitmap that the pan already positions when it is drawn, and the
        // effect then lands a whole pan away from the line it belongs to. The pan is therefore dropped here and
        // the region's own model rectangle is subtracted instead.
        Matrix grid = new(world.M11, world.M12, world.M21, world.M22, 0, 0);

        FilterBuffer Rasterise(Action<DrawingContext> draw)
        {
            var target = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
            using (DrawingContext context = target.CreateDrawingContext())
            using (context.PushTransform(
                Matrix.CreateTranslation(-(regionX / scale), -(regionY / scale)) * grid))
            {
                draw(context);
            }

            return ToBuffer(target, width, height);
        }

        FilterBuffer source = Rasterise(paint);

        // The source inputs the graph reads, and only those: a fill-only picture of a shape that is not painted
        // from a reachable closure is impossible to invent, and one nobody reads is an allocation for nothing.
        FilterBuffer? fill = fillPaint is not null && Reads(filters, "FillPaint") ? Rasterise(fillPaint) : null;
        FilterBuffer? stroke = strokePaint is not null && Reads(filters, "StrokePaint") ? Rasterise(strokePaint) : null;

        // `BackgroundImage` is deliberately absent: it is the picture **behind** the object, which this renderer
        // never composes - it draws one item at a time. A graph that reads it therefore reads transparent black and
        // is named in Result.Unsupplied, which is what SVG directs a viewer with no backdrop and is honest, where a
        // made-up buffer would be a picture the file did not ask for.
        FilterSources sources = fill is null && stroke is null
            ? FilterSources.None
            : new FilterSources { FillPaint = fill, StrokePaint = stroke };

        // Each filter in turn, with the previous result as its source - which is what makes a chain of effects
        // compose in the order the model keeps them.
        FilterBuffer filtered = source;
        var unsupplied = new List<string>();
        foreach (FilterSpec step in filters)
        {
            var engine = new FilterEngine(step, scale);
            filtered = engine.EvaluateInPlace(filtered, sources, objectBounds is { } box
                ? new Geometry.Rect2D(box.X, box.Y, box.Width, box.Height)
                : null);

            foreach (string name in engine.UnsuppliedSourceInputs)
            {
                if (!unsupplied.Contains(name, StringComparer.Ordinal))
                {
                    unsupplied.Add(name);
                }
            }
        }

        WriteableBitmap bitmap = ToBitmap(filtered);
        // The region in model units, which is where the caller draws it: the canvas's own transform is what puts it
        // on the page, so subtracting the pan here would apply it twice.
        var destination = new Rect(
            regionX / scale,
            regionY / scale,
            width / scale,
            height / scale);

        return new Result(bitmap, destination, unsupplied);
    }

    /// <summary>Whether any filter in the chain reads this renderer-supplied input.</summary>
    private static bool Reads(IReadOnlyList<FilterSpec> filters, string name)
    {
        foreach (FilterSpec filter in filters)
        {
            foreach (FilterPrimitive primitive in filter.Primitives)
            {
                if (string.Equals(primitive.Input, name, StringComparison.Ordinal) ||
                    string.Equals(primitive.Input2, name, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// The rendered pixels as a filter buffer.
    ///
    /// Avalonia's render targets are **premultiplied** BGRA, and the engine works on straight colour - so the alpha
    /// is divided back out here. Skipping this is not a visible bug on opaque artwork and is a wrong picture
    /// everywhere else, which is exactly where a shadow lives.
    /// </summary>
    private static FilterBuffer ToBuffer(RenderTargetBitmap target, int width, int height)
    {
        var buffer = new FilterBuffer(width, height);
        int stride = width * 4;
        byte[] pixels = new byte[stride * height];

        System.Runtime.InteropServices.GCHandle handle =
            System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            target.CopyPixels(new PixelRect(0, 0, width, height), handle.AddrOfPinnedObject(), pixels.Length, stride);
        }
        finally
        {
            handle.Free();
        }

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = (y * stride) + (x * 4);
                float a = pixels[i + 3] / 255f;
                buffer.Set(
                    x,
                    y,
                    a > 0.0001f ? pixels[i + 2] / 255f / a : 0f,
                    a > 0.0001f ? pixels[i + 1] / 255f / a : 0f,
                    a > 0.0001f ? pixels[i] / 255f / a : 0f,
                    a);
            }
        }

        return buffer;
    }

    /// <summary>The filtered buffer as a drawable bitmap, premultiplied again for the renderer.</summary>
    private static WriteableBitmap ToBitmap(FilterBuffer buffer)
    {
        var bitmap = new WriteableBitmap(
            new PixelSize(buffer.Width, buffer.Height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);

        using (ILockedFramebuffer locked = bitmap.Lock())
        {
            // Packed for the framebuffer's own stride, because a locked framebuffer may pad its rows - writing
            // width*4 bytes per row would shear the picture on any width where it does.
            byte[] pixels = new byte[locked.RowBytes * buffer.Height];
            for (int y = 0; y < buffer.Height; y++)
            {
                int row = y * locked.RowBytes;
                for (int x = 0; x < buffer.Width; x++)
                {
                    (float r, float g, float b, float a) = buffer.Get(x, y);
                    a = Math.Clamp(a, 0f, 1f);
                    pixels[row + (x * 4) + 0] = Channel(b * a);
                    pixels[row + (x * 4) + 1] = Channel(g * a);
                    pixels[row + (x * 4) + 2] = Channel(r * a);
                    pixels[row + (x * 4) + 3] = Channel(a);
                }
            }

            System.Runtime.InteropServices.Marshal.Copy(pixels, 0, locked.Address, pixels.Length);
        }

        return bitmap;
    }

    private static byte Channel(float value) => (byte)Math.Round(Math.Clamp(value, 0f, 1f) * 255f);
}
