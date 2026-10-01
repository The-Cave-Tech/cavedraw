using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using VCCad.App.Controls;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// A stroke's **raster effects**, drawn by the real canvas.
///
/// A blur, a shadow and a glow are raster operations with no vector form, so the canvas has to rasterise the stroke
/// and run the filter engine over those pixels. The engine's own tests cover the arithmetic; what is in question
/// here is whether anything calls it - the effects were model-only, set and saved and changing nothing on screen.
///
/// The measurement is **mid-tones and the darkest pixel**, not ink. The canvas paints a pasteboard and a white page
/// under the artwork, so every pixel has alpha and counting "pixels with ink" says nothing; what a blur does that a
/// hard edge does not is eat into the solid core, so the darkest pixel is the signal and the ramp is the witness.
/// </summary>
public class RasterEffectCanvasTests
{
    private static (Window Window, EditorViewModel ViewModel) Host()
    {
        (Window window, _, EditorViewModel viewModel) = HostWithWorkspace();
        return (window, viewModel);
    }

    private static (Window Window, CanvasWorkspace Workspace, EditorViewModel ViewModel) HostWithWorkspace()
    {
        var viewModel = new EditorViewModel();
        var workspace = new CanvasWorkspace();
        workspace.AttachEditor(viewModel);

        var window = new Window { Width = 600, Height = 500, Content = workspace };
        window.Show();
        Settle();
        return (window, workspace, viewModel);
    }

    private static void Settle()
    {
        for (int i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    /// <summary>A thin black outline and nothing else - the one stroke a blur has to soften.
    ///
    /// Its rectangle is in the artboard's **own** coordinates, because that is the space a path is stored in; a test
    /// that measured it from the artboard's corner would only work while the page sits at the document origin.</summary>
    private static PathItem StrokedRectangle(
        EditorViewModel viewModel, double x = 60, double y = 60, double width = 300, double height = 220)
    {
        Artboard board = viewModel.Document.Artboards[0];

        PathItem rect = PathFactory.CreateRectangle("outline", new Rect2D(x, y, width, height));
        rect.Fill = FillSpec.None;
        rect.Strokes.Clear();
        rect.Strokes.Add(StrokeSpec.Hairline(ColorRgb.Black) with { Width = 4 });
        board.Layers[0].AddItem(rect);

        Settle();
        return rect;
    }

    private static void SetEffects(PathItem path, params RasterEffectSpec[] effects)
    {
        path.Strokes[0] = path.Strokes[0] with
        {
            RasterEffects = effects.Length == 0 ? null : new RasterEffectStack(effects),
        };
        path.NotifyStrokesChanged();
        Settle();
    }

    /// <summary>What the canvas looks like: how many pixels are dark, how many are part-dark, and the darkest.</summary>
    private static (int Dark, int Mid, double Darkest) Measure(Window window)
    {
        byte[] pixels = Pixels(window);

        int dark = 0, mid = 0;
        double darkest = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            // Premultiplied BGRA, so a black pixel is zero on every channel and the white page is full scale: the
            // luminance below is "how much light comes out", which is what a blur redistributes.
            double luminance = ((pixels[i] / 255.0) + (pixels[i + 1] / 255.0) + (pixels[i + 2] / 255.0)) / 3.0;
            if (luminance < 0.5)
            {
                dark++;
            }

            if (luminance is > 0.15 and < 0.85)
            {
                mid++;
            }

            darkest = Math.Max(darkest, 1.0 - luminance);
        }

        return (dark, mid, darkest);
    }

    /// <summary>
    /// **A stroke's blur softens the line the canvas draws.**
    ///
    /// The stroke is thin enough that a blur of its own radius eats into the core: the darkest pixel is no longer
    /// fully black, which no hard edge can be and no model assertion can show.
    /// </summary>
    [AvaloniaFact]
    public void AStrokesBlurSoftensTheLine()
    {
        (Window window, EditorViewModel viewModel) = Host();
        PathItem rect = StrokedRectangle(viewModel);

        (int darkBefore, int midBefore, double darkestBefore) = Measure(window);
        Assert.True(darkBefore > 200, $"the outline should have drawn: {darkBefore} dark pixels");
        Assert.Equal(1.0, darkestBefore, 2);

        SetEffects(rect, RasterEffectSpec.Blur(6));

        (int darkAfter, int midAfter, double darkestAfter) = Measure(window);

        Assert.True(darkestAfter < darkestBefore - 0.05,
            $"a blur should eat into the core: {darkestBefore} before, {darkestAfter} after " +
            $"({darkBefore}/{midBefore} dark/mid before, {darkAfter}/{midAfter} after)");
        Assert.True(midAfter > midBefore,
            $"a blur should lay down a ramp: {midBefore} mid-tones before, {midAfter} after");
    }

    /// <summary>
    /// And it is the effect that did it: clearing it restores the sharp line exactly, which keeps the assertions
    /// above from passing for some other reason.
    /// </summary>
    [AvaloniaFact]
    public void ClearingAStrokesBlurRestoresTheSharpLine()
    {
        (Window window, EditorViewModel viewModel) = Host();
        PathItem rect = StrokedRectangle(viewModel);

        (int dark, int mid, double darkest) = Measure(window);

        SetEffects(rect, RasterEffectSpec.Blur(6));
        double blurredDarkest = Measure(window).Darkest;
        Assert.True(blurredDarkest < darkest - 0.05);

        SetEffects(rect);

        (int darkAfter, int midAfter, double darkestAfter) = Measure(window);
        Assert.Equal(dark, darkAfter);
        Assert.Equal(mid, midAfter);
        Assert.Equal(darkest, darkestAfter, 3);
    }

    /// <summary>
    /// **A raster effect works on a page that is not at the document origin.**
    ///
    /// A path's geometry is stored in its artboard's own coordinates and drawn at the artboard's offset, so an effect
    /// region measured from the stored box lands beside the artwork whenever the page sits anywhere else - which is
    /// every page of a multi-page document. The symptom is not a soft edge in the wrong place: the stroke is drawn
    /// outside its own bitmap, and the painter that returns "handled" leaves the line off the page entirely.
    ///
    /// The halo is read **where it has to be** - just above and just below the line, in window coordinates taken from
    /// the canvas itself - so an effect that is drawn but displaced by the canvas pan fails here too. Counting red
    /// pixels anywhere does not catch that, and a displaced effect is still a wrong picture.
    /// </summary>
    [AvaloniaFact]
    public void ARasterEffectDrawsOnAPageAwayFromTheOrigin()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel) = HostWithWorkspace();
        Artboard board = viewModel.Document.Artboards[0];
        board.X = 400;
        board.Y = 300;
        Settle();

        PathItem rect = StrokedRectangle(viewModel, x: 60, y: 60, width: 80, height: 60);

        // The line's world box: the rectangle is stored in the artboard's coordinates, so the page's origin is added.
        Assert.Equal(460.0, rect.WorldBounds().X, 1);
        Assert.Equal(360.0, rect.WorldBounds().Y, 1);

        byte[] sharp = Pixels(window);

        SetEffects(rect, RasterEffectSpec.Glow(RasterEffectKind.OuterGlow, 8, new ColorRgb(1, 0, 0)));
        byte[] lit = Pixels(window);

        int rise = Reddish(lit, 40) - Reddish(sharp, 40);
        Assert.True(rise > 100, $"the glow should land on the page: the red count rose by {rise}");

        // Five points beyond the top and the bottom edge, midway along: inside the halo's reach, outside the line.
        Assert.True(IsRed(lit, workspace.ModelToWindow(new Point2D(500, 355)), 25),
            "the halo should reach above the line");
        Assert.True(IsRed(lit, workspace.ModelToWindow(new Point2D(500, 425)), 25),
            "the halo should reach below the line");
    }

    /// <summary>
    /// **An outer glow reaches past the stroke, where a hard edge has nothing.**
    ///
    /// Read by **colour**, not by luminance: the glow is red and everything the canvas draws without it - the white
    /// page, the black line - is grey, so a pixel whose red channel runs ahead of its green can only have come from
    /// the effect. Luminance alone cannot tell the two apart here, because a dark halo also moves mid-tones *out* of
    /// the band, which is how the first version of this test failed while the glow was being drawn correctly.
    /// </summary>
    [AvaloniaFact]
    public void AnOuterGlowPaintsOutsideTheStroke()
    {
        (Window window, EditorViewModel viewModel) = Host();
        PathItem rect = StrokedRectangle(viewModel);

        // The canvas chrome is not red, but it is not all grey either, so the halo is read as a rise rather than
        // from zero.
        int bare = Reddish(Pixels(window), 40);

        SetEffects(rect, RasterEffectSpec.Glow(RasterEffectKind.OuterGlow, 8, new ColorRgb(1, 0, 0)));

        int halo = Reddish(Pixels(window), 40);
        Assert.True(halo > bare + 200,
            $"an outer glow should lay down a red halo: {bare} reddish pixels before, {halo} after");

        // The line is still under the halo rather than replaced by it.
        (int darkAfter, _, double darkestAfter) = Measure(window);
        Assert.True(darkAfter > 200, $"the stroke should still draw: {darkAfter} dark pixels");
        Assert.Equal(1.0, darkestAfter, 2);
    }

    /// <summary>The window as premultiplied BGRA, which is what the canvas actually put on the screen.</summary>
    private static byte[] Pixels(Window window)
    {
        var target = new RenderTargetBitmap(new PixelSize(600, 500), new Vector(96, 96));
        target.Render(window);

        const int Width = 600, Height = 500;
        int stride = Width * 4;
        byte[] pixels = new byte[stride * Height];
        System.Runtime.InteropServices.GCHandle handle = System.Runtime.InteropServices.GCHandle.Alloc(
            pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            target.CopyPixels(new PixelRect(0, 0, Width, Height), handle.AddrOfPinnedObject(), pixels.Length, stride);
        }
        finally
        {
            handle.Free();
        }

        return pixels;
    }

    /// <summary>How many pixels carry the red tint - premultiplied BGRA with an opaque page, so red ahead of green
    /// by <paramref name="margin"/> is ink nothing grey could have put there.</summary>
    private static int Reddish(byte[] pixels, int margin)
    {
        int count = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            // B, G, R, A.
            if (pixels[i + 2] - pixels[i + 1] > margin)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Whether one window point carries the red tint, read from an already-captured frame.</summary>
    private static bool IsRed(byte[] pixels, Point point, int margin)
    {
        int x = (int)Math.Round(point.X);
        int y = (int)Math.Round(point.Y);
        if (x < 0 || y < 0 || x >= 600 || y >= 500)
        {
            return false;
        }

        int i = ((y * 600) + x) * 4;
        return pixels[i + 2] - pixels[i + 1] > margin;
    }
}