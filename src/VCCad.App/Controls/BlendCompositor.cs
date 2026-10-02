using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using SkiaSharp;
using VCCad.Core.Model;

namespace VCCad.App.Controls;

/// <summary>
/// Draws an item whose colour is **composited with what is already on the page** rather than laid over it.
///
/// A blend mode is not a property of the paint, it is a property of the **compositing step**: SVG's
/// `mix-blend-mode` and PDF's `/BM` both mean "combine the whole shape with the backdrop". Avalonia's
/// <see cref="DrawingContext"/> has no blend-mode primitive, so the shape is rasterised into its own transparent
/// bitmap and that bitmap is composited through Skia, which is what the picture needs and what the other two
/// renderers do.
///
/// **Rasterising the item first is the definition, not an implementation detail.** Compositing each draw call
/// separately blends a translucent shape with itself: two strokes crossing inside one item would multiply their
/// own overlap and the item's paint would blend twice. A layer isolates the item from itself and blends the
/// finished picture once, which is exactly what `SaveLayer` states on the Skia side.
/// </summary>
internal static class BlendCompositor
{
    /// <summary>
    /// A region larger than this is not a picture anyone asked for, and would be an allocation to fail on rather
    /// than to draw - the same guard the filter renderer makes. The caller falls back to painting unblended, so
    /// the artwork stays visible.
    /// </summary>
    internal const int Limit = 8192;

    /// <summary>
    /// The Skia lease feature, asked for **by name** rather than by type.
    ///
    /// The feature belongs to the Skia rendering backend, and the editor shell is deliberately not compiled
    /// against it: Avalonia picks its backend at start-up, and a control that referenced `Avalonia.Skia` would
    /// make one backend the shell's own dependency. The canvas already carries a direct SkiaSharp reference (the
    /// gradient painter needs one), so the blending call itself is a compile-time call and only the *lease* is
    /// resolved at run time. A backend that does not offer it leaves the item drawn normally rather than crashing.
    /// </summary>
    private const string LeaseFeatureName = "Avalonia.Skia.ISkiaSharpApiLeaseFeature";

    private static readonly Type? LeaseFeatureType =
        Type.GetType($"{LeaseFeatureName}, Avalonia.Skia", throwOnError: false);

    private static readonly MethodInfo? LeaseMethod = LeaseFeatureType?.GetMethod("Lease", Type.EmptyTypes);

    /// <summary>
    /// Renders <paramref name="paint"/> into an offscreen bitmap covering <paramref name="frameBounds"/> and
    /// composites it into <paramref name="context"/> with <paramref name="mode"/>.
    ///
    /// <paramref name="frameBounds"/> and <paramref name="scale"/> are in the frame **the caller's context is
    /// drawing in** - the canvas transform and every enclosing group's push are already in force - so the
    /// destination rectangle is stated in those same units and needs no further mapping.
    ///
    /// Answers false when the offscreen route cannot be taken, and then nothing has been drawn and the caller is
    /// expected to paint normally.
    /// </summary>
    internal static bool TryComposite(
        DrawingContext context,
        Rect frameBounds,
        double scale,
        BlendMode mode,
        Action<DrawingContext> paint)
    {
        if (mode == BlendMode.Normal || scale <= 0.01 ||
            frameBounds.Width <= 0 || frameBounds.Height <= 0 ||
            !double.IsFinite(frameBounds.Width) || !double.IsFinite(frameBounds.Height))
        {
            return false;
        }

        int width = (int)Math.Ceiling(frameBounds.Width * scale);
        int height = (int)Math.Ceiling(frameBounds.Height * scale);

        if (width <= 0 || height <= 0 || width > Limit || height > Limit)
        {
            return false;
        }

        // The origin is snapped to a whole **frame** unit so the same item always rasterises at the same offset:
        // a bitmap whose origin followed the pan would resample the artwork a fraction of a pixel differently on
        // every scroll, and two renders of one document would stop agreeing.
        var origin = new Point(Math.Floor(frameBounds.X), Math.Floor(frameBounds.Y));
        var destination = new Rect(origin, new Size(width / scale, height / scale));

        var target = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        using (DrawingContext offscreen = target.CreateDrawingContext())
        using (offscreen.PushTransform(
            Matrix.CreateTranslation(-origin.X, -origin.Y) * Matrix.CreateScale(scale, scale)))
        {
            paint(offscreen);
        }

        byte[]? pixels = Read(target, width, height);
        if (pixels is null)
        {
            return false;
        }

        context.Custom(new BlendOperation(pixels, width, height, destination, mode));
        return true;
    }

    /// <summary>The rendered pixels, premultiplied BGRA - the layout Skia reads them back in.</summary>
    private static byte[]? Read(RenderTargetBitmap target, int width, int height)
    {
        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        GCHandle handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            target.CopyPixels(new PixelRect(0, 0, width, height), handle.AddrOfPinnedObject(), pixels.Length, stride);
        }
        catch (NotSupportedException)
        {
            return null;
        }
        finally
        {
            handle.Free();
        }

        return pixels;
    }

    /// <summary>
    /// The blend itself, performed by Skia because nothing above it can express one.
    ///
    /// Avalonia renders an <see cref="ICustomDrawOperation"/> **inline**, on the same surface and in the same order
    /// as the drawing around it, so the canvas the lease hands back already holds everything painted before this
    /// item - which is what a blend mode combines with, and which cannot be reconstructed from the model.
    /// </summary>
    private sealed class BlendOperation : ICustomDrawOperation
    {
        private readonly byte[] _pixels;
        private readonly int _width;
        private readonly int _height;
        private readonly BlendMode _mode;

        public BlendOperation(byte[] pixels, int width, int height, Rect destination, BlendMode mode)
        {
            _pixels = pixels;
            _width = width;
            _height = height;
            _mode = mode;
            Bounds = destination;
        }

        public Rect Bounds { get; }

        public void Dispose()
        {
        }

        public bool HitTest(Point p) => false;

        public bool Equals(ICustomDrawOperation? other) => false;

        public void Render(ImmediateDrawingContext context)
        {
            if (LeaseFeatureType is null || LeaseMethod is null ||
                context.TryGetFeature(LeaseFeatureType) is not { } feature ||
                LeaseMethod.Invoke(feature, null) is not { } lease)
            {
                return;
            }

            try
            {
                if (lease.GetType().GetProperty("SkCanvas")?.GetValue(lease) is not SKCanvas canvas)
                {
                    return;
                }

                GCHandle handle = GCHandle.Alloc(_pixels, GCHandleType.Pinned);
                try
                {
                    var info = new SKImageInfo(_width, _height, SKColorType.Bgra8888, SKAlphaType.Premul);
                    using SKPixmap pixmap = new(info, handle.AddrOfPinnedObject(), _width * 4);
                    using SKImage image = SKImage.FromPixels(pixmap);

                    Rect destination = Bounds;
                    using var paint = new SKPaint
                    {
                        BlendMode = ToSkia(_mode),
                        IsAntialias = true,
                        FilterQuality = SKFilterQuality.High,
                    };

                    // The layer is what makes the blend a **group** operation: the source is composited into its
                    // own buffer first, so the item cannot blend with itself, and the buffer is then combined with
                    // the backdrop under the paint's mode. A null bounds takes the current clip, which is the
                    // item's own clip - so a blended item is clipped exactly as an unblended one is.
                    canvas.SaveLayer(paint);
                    canvas.DrawImage(
                        image,
                        SKRect.Create(
                            (float)destination.X,
                            (float)destination.Y,
                            (float)destination.Width,
                            (float)destination.Height),
                        paint);
                    canvas.Restore();
                }
                finally
                {
                    handle.Free();
                }
            }
            finally
            {
                (lease as IDisposable)?.Dispose();
            }
        }
    }

    /// <summary>The blend mode the file named, as Skia names it.</summary>
    internal static SKBlendMode ToSkia(BlendMode mode) => mode switch
    {
        BlendMode.Multiply => SKBlendMode.Multiply,
        BlendMode.Screen => SKBlendMode.Screen,
        BlendMode.Darken => SKBlendMode.Darken,
        BlendMode.Lighten => SKBlendMode.Lighten,
        BlendMode.Overlay => SKBlendMode.Overlay,
        BlendMode.ColorDodge => SKBlendMode.ColorDodge,
        BlendMode.ColorBurn => SKBlendMode.ColorBurn,
        BlendMode.HardLight => SKBlendMode.HardLight,
        BlendMode.SoftLight => SKBlendMode.SoftLight,
        BlendMode.Difference => SKBlendMode.Difference,
        BlendMode.Exclusion => SKBlendMode.Exclusion,
        BlendMode.Hue => SKBlendMode.Hue,
        BlendMode.Saturation => SKBlendMode.Saturation,
        BlendMode.Color => SKBlendMode.Color,
        BlendMode.Luminosity => SKBlendMode.Luminosity,
        _ => SKBlendMode.SrcOver,
    };
}



