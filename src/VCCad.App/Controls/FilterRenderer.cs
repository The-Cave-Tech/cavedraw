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
    /// <summary>What the caller draws: the filtered bitmap, and the model rectangle to put it in.</summary>
    internal readonly record struct Result(WriteableBitmap Bitmap, Rect Destination);

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
    /// </summary>
    public static Result? Render(
        IReadOnlyList<FilterSpec> filters,
        Rect bounds,
        Matrix world,
        double scale,
        Action<DrawingContext> paint)
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

        (int regionX, int regionY, int width, int height) = FilterEngine.RegionPixels(
            filter, new Geometry.Rect2D(bounds.X, bounds.Y, bounds.Width, bounds.Height), scale);

        // A region larger than anyone would want to allocate is a file with a wild filter region rather than a
        // request to draw it; falling back to unfiltered keeps the artwork visible.
        const int Limit = 8192;
        if (width <= 0 || height <= 0 || width > Limit || height > Limit)
        {
            return null;
        }

        var target = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        using (DrawingContext context = target.CreateDrawingContext())
        {
            // The bitmap is the artwork on **its own pixel grid**, so the canvas transform's scale is applied and
            // its translation is not. The region is an absolute box in model units, so a transform that carried the
            // pan would shift the artwork inside a bitmap that the pan already positions when it is drawn, and the
            // effect then lands a whole pan away from the line it belongs to. The pan is therefore dropped here and
            // the region's own model rectangle is subtracted instead.
            Matrix grid = new(world.M11, world.M12, world.M21, world.M22, 0, 0);
            using (context.PushTransform(
                Matrix.CreateTranslation(-(regionX / scale), -(regionY / scale)) * grid))
            {
                paint(context);
            }
        }

        FilterBuffer source = ToBuffer(target, width, height);

        // Each filter in turn, with the previous result as its source - which is what makes a chain of effects
        // compose in the order the model keeps them.
        FilterBuffer filtered = source;
        foreach (FilterSpec step in filters)
        {
            filtered = new FilterEngine(step, scale).EvaluateInPlace(filtered);
        }

        WriteableBitmap bitmap = ToBitmap(filtered);
        // The region in model units, which is where the caller draws it: the canvas's own transform is what puts it
        // on the page, so subtracting the pan here would apply it twice.
        var destination = new Rect(
            regionX / scale,
            regionY / scale,
            width / scale,
            height / scale);

        return new Result(bitmap, destination);
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
