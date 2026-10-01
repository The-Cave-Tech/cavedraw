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
/// An outline effect on a stroke that has **no width profile** is drawn.
///
/// The canvas asked a narrower question than the exporters: it branched on `HasWidthProfile` alone, while the PDF
/// exporter and the SVG writer both ask `StrokeOutlineBuilder.Plan(...).IsOutline`. A stroke with an outline effect
/// and no profile is an outline - that is what the effect means - so the canvas drew an ordinary pen stroke with
/// the effect ignored while the file filled the effected outline. The same document, two different pictures, and
/// nothing reported it: the effect was silently invisible on screen.
///
/// These tests render the real canvas, because the guard being wrong is invisible to a test that calls the outline
/// builder directly - the builder was always right; it was the branch that never reached it.
/// </summary>
public class OutlineEffectCanvasTests
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

    /// <summary>A short horizontal line in the middle of the page, stroke only.</summary>
    private static PathItem Line(EditorViewModel viewModel, StrokeSpec stroke)
    {
        Artboard board = viewModel.Document.Artboards[0];

        var path = new PathItem { Name = "line", Fill = FillSpec.None };
        SubPath sub = path.AddSubPath(closed: false);
        sub.Nodes.Add(new PathNode(new Point2D(board.X + 80, board.Y + 100)));
        sub.Nodes.Add(new PathNode(new Point2D(board.X + 200, board.Y + 100)));
        path.Strokes.Clear();
        path.Strokes.Add(stroke);

        board.Layers[0].AddItem(path);
        Settle();
        return path;
    }

    private static StrokeSpec Plain(EffectStack? effects = null)
        => new(true, ColorRgb.Black, 4, StrokeCap.Butt, StrokeJoin.Miter, 4)
        {
            Effects = effects ?? new EffectStack(Array.Empty<OutlineEffectSpec>()),
        };

    /// <summary>How much ink the canvas actually puts down - the whole point when the question is "was it drawn".</summary>
    private static int DarkPixels(Window window)
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

        int dark = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            double luminance = ((pixels[i] / 255.0) + (pixels[i + 1] / 255.0) + (pixels[i + 2] / 255.0)) / 3.0;
            if (luminance < 0.5)
            {
                dark++;
            }
        }

        return dark;
    }

    /// <summary>
    /// **An offset path with no width profile widens the stroke on screen.** Without the fix the canvas drew the
    /// plain 4-wide line and the effect changed nothing, which is exactly the failure this pins.
    /// </summary>
    [AvaloniaFact]
    public void AnEffectWithoutAProfileChangesThePicture()
    {
        (Window window, EditorViewModel viewModel) = Host();
        PathItem path = Line(viewModel, Plain());

        int plain = DarkPixels(window);

        path.Stroke = Plain(new EffectStack(new[] { OutlineEffectSpec.OffsetPath(6) }));
        Settle();

        int effected = DarkPixels(window);

        Assert.True(effected > plain,
            $"an offset path should widen the line: {plain} dark pixels plain, {effected} with the effect");
    }

    /// <summary>And a stroke with neither a profile nor an effect still draws as a pen stroke, unchanged.</summary>
    [AvaloniaFact]
    public void APlainStrokeIsStillDrawnAsAPenStroke()
    {
        (Window window, EditorViewModel viewModel) = Host();
        Line(viewModel, Plain());

        int once = DarkPixels(window);
        Settle();
        int again = DarkPixels(window);

        Assert.True(once > 0, "the plain line should be drawn at all");
        Assert.Equal(once, again);
    }
}
