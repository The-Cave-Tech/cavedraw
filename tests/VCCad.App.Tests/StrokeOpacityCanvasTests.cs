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
/// A stroke's own **opacity** is drawn on the canvas, not merely stored.
///
/// This is the half a round-trip test cannot see, and the failure it hides is the quietest one in the repository:
/// the model holds the value, the sidecar writes it, the reader returns it, the operations report it - and the
/// painter never applies it, so the stroke is drawn at full strength while every other test stays green. The
/// canvas is a real renderer here rather than a mock, so the assertion is on **pixels**: the same path, the same
/// colour, the same width, differing only in the stroke's opacity, must put different amounts of ink on the page.
///
/// The measurement is of ink coverage - how many pixels a stroke darkens below a threshold - because that is
/// what a translucent stroke changes on a light page. Reading the alpha channel instead would measure the
/// renderer's compositing rather than the stroke's contribution to the picture.
/// </summary>
public class StrokeOpacityCanvasTests
{
    private static (Window Window, EditorViewModel ViewModel) Host()
    {
        var viewModel = new EditorViewModel();
        var workspace = new CanvasWorkspace();
        workspace.AttachEditor(viewModel);

        var window = new Window { Width = 600, Height = 500, Content = workspace };
        window.Show();
        Settle();
        return (window, viewModel);
    }

    private static void Settle()
    {
        for (int i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    /// <summary>A thick horizontal line in the middle of the page, stroke only.</summary>
    private static PathItem Line(EditorViewModel viewModel, StrokeSpec stroke)
    {
        Artboard board = viewModel.Document.Artboards[0];

        var path = new PathItem { Name = "line", Fill = FillSpec.None };
        SubPath sub = path.AddSubPath(closed: false);
        sub.Nodes.Add(new PathNode(new Point2D(board.X + 80, board.Y + 100)));
        sub.Nodes.Add(new PathNode(new Point2D(board.X + 260, board.Y + 100)));
        path.Strokes.Clear();
        path.Strokes.Add(stroke);

        board.Layers[0].AddItem(path);
        Settle();
        return path;
    }

    private static StrokeSpec Stroke() => new(true, ColorRgb.Black, 16, StrokeCap.Butt, StrokeJoin.Miter, 4);

    /// <summary>How many pixels the render darkens below mid grey - the ink the stroke put down.</summary>
    private static int InkPixels(Window window)
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

        int ink = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            double luminance = ((pixels[i] / 255.0) + (pixels[i + 1] / 255.0) + (pixels[i + 2] / 255.0)) / 3.0;
            if (luminance < 0.5)
            {
                ink++;
            }
        }

        return ink;
    }

    /// <summary>
    /// **A stroke at half opacity covers less of the page than the same stroke opaque.** Nothing else about the
    /// document changes, so the difference can only come from the stroke's opacity reaching the pen.
    ///
    /// The comparison is between two renderings rather than against a pixel count, because how many pixels a
    /// 16-wide line covers depends on the platform's anti-aliasing - but that a half-opacity stroke covers
    /// **fewer** of them, for the same geometry, is a property of the opacity alone.
    /// </summary>
    [AvaloniaFact]
    public void ATranslucentStrokePutsDownLessInkThanAnOpaqueOne()
    {
        (Window opaqueWindow, EditorViewModel opaqueModel) = Host();
        Line(opaqueModel, Stroke());
        int opaqueInk = InkPixels(opaqueWindow);

        (Window faintWindow, EditorViewModel faintModel) = Host();
        Line(faintModel, Stroke() with { Opacity = 0.35 });
        int faintInk = InkPixels(faintWindow);

        Assert.True(opaqueInk > 0, "the opaque stroke draws something at all, so the measurement is meaningful");
        Assert.True(
            faintInk < opaqueInk,
            $"a stroke at 0.35 covers less ink than the opaque one: {faintInk} vs {opaqueInk}");
    }

    /// <summary>
    /// **And a stroke that states no opacity draws exactly what it drew before this existed.** The member is
    /// absent, so the painter has to treat it as fully opaque - a painter that read the missing value as zero
    /// would draw nothing at all, which is why this is asserted separately from the comparison above.
    /// </summary>
    [AvaloniaFact]
    public void AStrokeThatStatesNoOpacityDrawsAtFullStrength()
    {
        (Window statedWindow, EditorViewModel statedModel) = Host();
        Line(statedModel, Stroke() with { Opacity = 1.0 });
        int statedInk = InkPixels(statedWindow);

        (Window silentWindow, EditorViewModel silentModel) = Host();
        Line(silentModel, Stroke());
        int silentInk = InkPixels(silentWindow);

        Assert.True(statedInk > 0, "an explicitly opaque stroke draws");
        Assert.Equal(statedInk, silentInk);
    }

    /// <summary>
    /// **The stroke's opacity multiplies with the item's, rather than either replacing the other.** A path at
    /// 50% whose stroke is at 50% is a quarter covered; a painter that let one win would draw it at a half.
    ///
    /// The three renderings are ordered by construction, so this pins the composition without depending on the
    /// absolute value of any single one of them.
    /// </summary>
    [AvaloniaFact]
    public void AStrokeOpacityAndAPathOpacityCompose()
    {
        (Window plainWindow, EditorViewModel plainModel) = Host();
        PathItem plain = Line(plainModel, Stroke());
        int plainInk = InkPixels(plainWindow);

        (Window pathWindow, EditorViewModel pathModel) = Host();
        PathItem dampedPath = Line(pathModel, Stroke());
        dampedPath.Opacity = 0.5;
        Settle();
        int pathInk = InkPixels(pathWindow);

        (Window bothWindow, EditorViewModel bothModel) = Host();
        PathItem both = Line(bothModel, Stroke() with { Opacity = 0.5 });
        both.Opacity = 0.5;
        Settle();
        int bothInk = InkPixels(bothWindow);

        Assert.True(plainInk > pathInk, $"a half-opacity path covers less: {plainInk} vs {pathInk}");
        Assert.True(pathInk > bothInk, $"an opaque stroke on a half-opacity path covers less than half of both: " +
            $"{pathInk} vs {bothInk}");
    }
}
