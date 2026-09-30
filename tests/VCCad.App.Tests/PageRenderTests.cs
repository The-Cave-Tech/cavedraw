using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using VCCad.App.Controls;
using VCCad.App.ViewModels;
using VCCad.App.Views;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// A rendered page must be the page: the artwork where the model puts it, and paper to the page box
/// on every side.
///
/// Issue #40. `document.renderPage` exists so a page can be compared against another PDF engine's
/// raster of the same page, and it was producing an image whose content sat about 120 px higher than
/// the model said and whose bottom 171 px was the application's dark canvas background. Every such
/// comparison then measured the renderer's own displacement instead of the artwork - which is worse
/// than useless, because it reads as a fidelity problem with the document.
/// </summary>
public class PageRenderTests
{
    private const double PageWidth = 841.8897637795276;
    private const double PageHeight = 595.2755905511812;

    /// <summary>
    /// The same question on a sheet the size of a real pattern page, portrait - which is where the
    /// displacement actually showed. A4 landscape renders correctly, so the fault depends on the page
    /// being rendered, and a test that only used the default document would have missed it.
    /// </summary>
    [AvaloniaFact]
    public void ALargeSheetIsAlsoRenderedWhereTheModelPutsIt()
    {
        byte[] png = RenderDocumentWith(new Size2D(2383.94, 3350.27), new Rect2D(100, 100, 200, 200));

        using var stream = new MemoryStream(png);
        using var bitmap = new Bitmap(stream);
        byte[] pixels = Read(bitmap, out int stride, out _);

        (int left, int top, int right, int bottom) = InkBounds(
            pixels, stride, bitmap.PixelSize.Width, bitmap.PixelSize.Height);

        Assert.True(Math.Abs(left - 100) <= 2 && Math.Abs(top - 100) <= 2 &&
                    Math.Abs(right - 300) <= 2 && Math.Abs(bottom - 300) <= 2,
            $"the artwork should be at 100,100 to 300,300 but is at {left},{top} to {right},{bottom}");
    }

    [AvaloniaFact]
    public void ARenderedPageHasPaperToItsEdgesAndArtworkWhereTheModelPutsIt()
    {
        byte[] png = RenderDefaultDocumentWith(
            new Rect2D(100, 100, 200, 200));

        using var stream = new MemoryStream(png);
        using var bitmap = new Bitmap(stream);

        // 72 dpi is one pixel per point, so the page is its own size in pixels.
        Assert.Equal((int)Math.Round(PageWidth), bitmap.PixelSize.Width);
        Assert.Equal((int)Math.Round(PageHeight), bitmap.PixelSize.Height);

        byte[] pixels = Read(bitmap, out int stride, out int width);

        // The rectangle was placed at 100,100 to 300,300 in model space, which is pixel space here.
        // Measured as a bounding box rather than a single pixel, because the failure that matters is
        // "the whole page is displaced", and the box says by how much.
        (int left, int top, int right, int bottom) = InkBounds(
            pixels, stride, bitmap.PixelSize.Width, bitmap.PixelSize.Height);

        Assert.True(Math.Abs(left - 100) <= 2 && Math.Abs(top - 100) <= 2 &&
                    Math.Abs(right - 300) <= 2 && Math.Abs(bottom - 300) <= 2,
            $"the artwork should be at 100,100 to 300,300 but is at {left},{top} to {right},{bottom}");

        // And paper everywhere else - the corners especially, which is where a displaced render
        // shows the canvas background instead.
        foreach ((int x, int y) in new[]
                 {
                     (5, 5), (width - 6, 5), (5, bitmap.PixelSize.Height - 6),
                     (width - 6, bitmap.PixelSize.Height - 6), (400, 400), (400, 580),
                 })
        {
            Assert.True(IsPaper(pixels, stride, x, y),
                $"({x},{y}) should be paper, was {Describe(pixels, stride, x, y)}");
        }
    }

    /// <summary>
    /// The bottom strip specifically: this is the part that came out as the dark canvas background,
    /// because the render covered the content extent rather than the page box.
    ///
    /// The very last row is allowed to carry the page's own edge - a 595.28-point page in a 595-pixel
    /// bitmap has its border on that row - so what is asserted is that the strip is *not the canvas
    /// background*, which is what the band was.
    /// </summary>
    [AvaloniaFact]
    public void TheBottomOfThePageIsPaperNotTheCanvasBackground()
    {
        byte[] png = RenderDefaultDocumentWith(new Rect2D(100, 100, 50, 50));

        using var stream = new MemoryStream(png);
        using var bitmap = new Bitmap(stream);
        byte[] pixels = Read(bitmap, out int stride, out int width);

        for (int y = bitmap.PixelSize.Height - 1; y > bitmap.PixelSize.Height - 30; y--)
        {
            (byte r, byte g, byte b) = Describe(pixels, stride, width / 2, y);
            Assert.True(g > 120,
                $"row {y} should be paper, not the canvas background, was ({r},{g},{b})");
        }
    }

    private static byte[] RenderDefaultDocumentWith(Rect2D bounds)
        => RenderDocumentWith(null, bounds);

    private static byte[] RenderDocumentWith(Size2D? pageSize, Rect2D bounds)
    {
        // The canvas renders whatever the view model holds, so the artwork has to go into THAT
        // document. Drawing into a separate one renders an empty page, and every assertion about
        // where the artwork landed then passes for the wrong reason.
        var viewModel = new EditorViewModel();
        CadDocument document = viewModel.Document;

        if (pageSize is { } size)
        {
            Artboard artboard = document.Artboards[0];
            artboard.Width = size.Width;
            artboard.Height = size.Height;
        }

        Layer layer = document.Artboards[0].Layers[0];

        var path = new PathItem();
        SubPath sub = path.AddSubPath(closed: true);
        sub.AppendNode(new Point2D(bounds.X, bounds.Y));
        sub.AppendNode(new Point2D(bounds.Right, bounds.Y));
        sub.AppendNode(new Point2D(bounds.Right, bounds.Bottom));
        sub.AppendNode(new Point2D(bounds.X, bounds.Bottom));
        path.Fill = FillSpec.Solid(ColorRgb.Black);
        path.Stroke = StrokeSpec.None;
        layer.AddItem(path);

        var workspace = new CanvasWorkspace();
        workspace.AttachEditor(viewModel);

        var window = new Window { Width = 900, Height = 700, Content = workspace };
        window.Show();
        for (int i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        workspace.InvalidateVisual();

        // The host sets this when the window is created; there is no host here.
        PageRenderer.Workspace = workspace;
        byte[]? png = PageRenderer.Render(document, 0, 72);
        Assert.NotNull(png);
        window.Close();
        return png!;
    }

    private static byte[] Read(Bitmap bitmap, out int stride, out int width)
    {
        width = bitmap.PixelSize.Width;
        stride = width * 4;
        var buffer = new byte[stride * bitmap.PixelSize.Height];
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(
            buffer, System.Runtime.InteropServices.GCHandleType.Pinned);
        bitmap.CopyPixels(
            new PixelRect(0, 0, bitmap.PixelSize.Width, bitmap.PixelSize.Height),
            handle.AddrOfPinnedObject(), buffer.Length, stride);
        handle.Free();
        return buffer;
    }

    /// <summary>The bounding box of everything darker than paper, which is where the artwork is.</summary>
    private static (int Left, int Top, int Right, int Bottom) InkBounds(
        byte[] pixels, int stride, int width, int height)
    {
        int left = width, top = height, right = -1, bottom = -1;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (!IsDark(pixels, stride, x, y))
                {
                    continue;
                }

                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
            }
        }

        return (left, top, right, bottom);
    }

    private static (byte R, byte G, byte B) Describe(byte[] pixels, int stride, int x, int y)
    {
        int at = (y * stride) + (x * 4);
        // RenderTargetBitmap saves BGRA.
        return (pixels[at + 2], pixels[at + 1], pixels[at]);
    }

    private static bool IsDark(byte[] pixels, int stride, int x, int y)
    {
        (byte r, byte g, byte b) = Describe(pixels, stride, x, y);
        return r < 60 && g < 60 && b < 60;
    }

    /// <summary>Paper: near white, and definitely not the app's dark canvas background.</summary>
    private static bool IsPaper(byte[] pixels, int stride, int x, int y)
    {
        (byte r, byte g, byte b) = Describe(pixels, stride, x, y);
        return r > 240 && g > 240 && b > 240;
    }
}
