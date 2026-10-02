using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using VCCad.App.Automation;
using VCCad.App.Controls;
using VCCad.App.Fonts;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Core.Text;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// What a block with two coloured runs looks like on the canvas: each run in its own colour.
///
/// This is measured in pixels because that is the only place the defect ever showed. The model held
/// <see cref="TextRun.Color"/> and answered <see cref="TextItem.ColourOf"/> correctly, the sidecar
/// round-tripped it and the reader set it — and the painter took the *block's* brush once, before the
/// run loop, so every run of a multi-coloured line came out the block's colour. Model-level assertions
/// cannot see that: they all say the two colours are there.
///
/// The read is a **differential** one: the darkest pixel inside each run's own box is sampled and the
/// two are compared. A threshold test would not bite here, because the page itself renders at 224 — a
/// "is it dark" test is satisfied by the paper, and it was satisfied before the fix too.
///
/// The face is never named: the runs are placed from <see cref="TextLayoutEngine"/> and the boxes are
/// taken from it, so the test says "the painter drew each run in the colour <see cref="TextItem.ColourOf"/>
/// gives it" whichever family answers for the block on this machine.
/// </summary>
public class TextColourPaintTests
{
    /// <summary>Two window pixels per model unit, so a glyph is tens of pixels wide.</summary>
    private const double Zoom = 2.0;

    private const double FontSize = 40.0;

    /// <summary>Two wide, ink-heavy letters per run, so a run's box holds plenty of the run's own paint.</summary>
    private const string Glyphs = "HH";

    private static readonly ColorRgb Red = new(1.0, 0.0, 0.0);

    private static readonly ColorRgb Blue = new(0.0, 0.0, 1.0);

    /// <summary>
    /// **A block whose second run states its own colour draws that run in it.**
    ///
    /// The block's colour is red and only the second run names blue, which is exactly the shape the SVG
    /// reader produces from `<text fill="#ff0000">red<tspan fill="#0000ff">blue</tspan></text>`. Against
    /// the painter this replaces, both runs draw from the block's brush: the blue run's darkest pixel is
    /// red, and both assertions below fail — the first by a channel difference of 255.
    /// </summary>
    [AvaloniaFact]
    public void ARunsOwnColourReachesTheCanvas()
    {
        WithRealMetrics(() =>
        {
            (Sampled first, Sampled second, string summary) = Paint();

            Assert.True(first.IsRed,
                $"the first run should draw in the block's red.\n  sampled {first.Summary()}\n  {summary}");
            Assert.True(second.IsBlue,
                $"the second run should draw in its own blue.\n  sampled {second.Summary()}\n  {summary}");

            // The differential half: the two runs are painted with different ink.
            Assert.True(first.R != second.R || first.G != second.G || first.B != second.B,
                $"the two runs are painted with the same colour.\n  first  {first.Summary()}\n" +
                $"  second {second.Summary()}\n  {summary}");
        });
    }

    /// <summary>
    /// **The colour `text.update` gives a run reaches the canvas.** <see cref="TextRun.Color"/> has been on the
    /// model and honoured by the painter since #161, while no operation could set one - so a person could import a
    /// multi-coloured line and never make one. The assertion is on the pixels, because that is the only place the
    /// gap ever showed: the member already round-tripped, so a model or round-trip assertion would have passed
    /// before the operation existed.
    ///
    /// Both runs start on the block's red, so the second run's blue is the whole change. The differential half is
    /// kept: the two runs are painted with different ink afterwards, which a painter that took the block's brush
    /// once cannot produce.
    /// </summary>
    [AvaloniaFact]
    public void TheRunColourTheOperationSetsReachesTheCanvas()
    {
        WithRealMetrics(() =>
        {
            var viewModel = new EditorViewModel();
            var workspace = new CanvasWorkspace();
            workspace.AttachEditor(viewModel);

            var window = new Window { Width = 900, Height = 700, Content = workspace };
            window.Show();
            Settle();

            workspace.ZoomTo(Zoom);

            var text = new TextItem { Name = "run-colour", Origin = new Point2D(120, 250), Color = Red };
            text.Runs.Add(new TextRun { Text = Glyphs, FontSize = FontSize });
            text.Runs.Add(new TextRun { Text = Glyphs, FontSize = FontSize });
            viewModel.Document.Artboards[0].Layers[0].AddItem(text);
            viewModel.SelectObject(text);

            Rect2D bounds = text.LocalBounds();
            workspace.CenterOn(new Point2D(bounds.X + (bounds.Width / 2), bounds.Y + (bounds.Height / 2)));
            workspace.InvalidateVisual();
            Settle();

            try
            {
                WriteableBitmap plain = Frame(window);
                Assert.True(SampleRun(text, workspace, plain, 0).IsRed,
                    "the first run should draw in the block's red before the edit.");
                Assert.True(SampleRun(text, workspace, plain, 1).IsRed,
                    "the second run should draw in the block's red before the edit.");

                var context = new AutomationContext { ViewModel = viewModel };
                EditorOperations.Invoke(
                    context, "text.update",
                    JsonSerializer.SerializeToElement(new { runColor = new[] { 0, 0, 255 }, runIndex = 1 }));

                workspace.InvalidateVisual();
                Settle();

                WriteableBitmap painted = Frame(window);
                Sampled first = SampleRun(text, workspace, painted, 0);
                Sampled second = SampleRun(text, workspace, painted, 1);

                Assert.True(first.IsRed, $"the first run keeps the block's red.\n  sampled {first.Summary()}");
                Assert.True(second.IsBlue, $"the second run draws in its own blue.\n  sampled {second.Summary()}");

                // The differential half: the two runs are painted with different ink.
                Assert.True(first.R != second.R || first.G != second.G || first.B != second.B,
                    $"the two runs are painted with the same colour.\n  first  {first.Summary()}\n" +
                    $"  second {second.Summary()}");
            }
            finally
            {
                window.Close();
            }
        });
    }

    // ------------------------------------------------------------------
    // Rendering and sampling
    // ------------------------------------------------------------------

    /// <summary>One sampled pixel, with the question the test asks of it.</summary>
    private sealed record Sampled(int X, int Y, int R, int G, int B)
    {
        /// <summary>The red channel dominates by a margin no antialiasing produces.</summary>
        public bool IsRed => R - Math.Max(G, B) >= 60;

        /// <summary>The blue channel dominates by the same margin.</summary>
        public bool IsBlue => B - Math.Max(R, G) >= 60;

        public string Summary() => $"({R},{G},{B}) at ({X},{Y})";
    }

    /// <summary>The frame the window has rendered, which the headless platform can be asked for directly.</summary>
    private static WriteableBitmap Frame(Window window)
    {
        WriteableBitmap? frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        return frame!;
    }

    /// <summary>
    /// The darkest pixel inside one run's own box in an already-rendered frame.
    ///
    /// The scan is the run's box from the layout, so the samples cannot be taken from the same run by accident,
    /// and it stays well inside the white page — the pasteboard behind it is dark, and a scan that reached it
    /// would read as ink.
    /// </summary>
    private static Sampled SampleRun(TextItem text, CanvasWorkspace workspace, WriteableBitmap frame, int runIndex)
    {
        TextLayout layout = TextLayoutEngine.Compute(text);
        Point origin = workspace.ModelToWindow(text.Origin + text.ArtboardOffset());
        TextRunBox box = layout.Runs.Single(b => b.Run == runIndex);
        TextLine line = layout.Lines[box.Line];

        // Inset by a few pixels at each edge so a neighbour's antialiasing cannot be picked up as this run's
        // ink. The vertical band is the line box, which is where the glyphs are.
        var region = new PixelRect(
            (int)Math.Ceiling(origin.X + (box.X * Zoom)) + 3,
            (int)Math.Ceiling(origin.Y + (line.Top * Zoom)) + 2,
            (int)Math.Floor(box.Width * Zoom) - 6,
            (int)Math.Floor(line.Height * Zoom) - 4);

        return Darkest(Pixels(frame), frame.PixelSize.Width * 4, frame.PixelSize, region,
            frame.Format == PixelFormat.Bgra8888);
    }

    /// <summary>
    /// Draws a block of two runs — the first the block's red, the second its own blue — and reads the
    /// darkest pixel inside each run's own box.
    ///
    /// The zoom is set explicitly and the view centred on the block, so the fit that runs on the first
    /// layout cannot put a different number of model units in a pixel. The scan is the run's box from the
    /// layout, so the two samples cannot be taken from the same run by accident, and it stays well inside
    /// the white page — the pasteboard behind it is dark, and a scan that reached it would read as ink.
    /// </summary>
    private static (Sampled First, Sampled Second, string Summary) Paint()
    {
        var viewModel = new EditorViewModel();
        var workspace = new CanvasWorkspace();
        workspace.AttachEditor(viewModel);

        var window = new Window { Width = 900, Height = 700, Content = workspace };
        window.Show();
        Settle();

        workspace.ZoomTo(Zoom);

        var text = new TextItem { Name = "two-colour", Origin = new Point2D(120, 250), Color = Red };
        text.Runs.Add(new TextRun { Text = Glyphs, FontSize = FontSize });
        text.Runs.Add(new TextRun { Text = Glyphs, FontSize = FontSize, Color = Blue });
        viewModel.Document.Artboards[0].Layers[0].AddItem(text);

        Rect2D bounds = text.LocalBounds();
        workspace.CenterOn(new Point2D(bounds.X + (bounds.Width / 2), bounds.Y + (bounds.Height / 2)));
        workspace.InvalidateVisual();
        Settle();

        try
        {
            WriteableBitmap frame = Frame(window);

            Sampled first = SampleRun(text, workspace, frame, 0);
            Sampled second = SampleRun(text, workspace, frame, 1);

            string summary =
                $"block {Glyphs}{Glyphs} at {FontSize}pt, zoom {Zoom:F1}: " +
                $"run 0 {first.Summary()}, run 1 {second.Summary()}";

            return (first, second, summary);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The darkest pixel inside a region — the pixel with the most of the run's own ink in it.
    ///
    /// The channel order is taken from the bitmap rather than assumed. A rendered frame is not a
    /// <c>RenderTargetBitmap</c>: the two have been seen to differ, and reading one as the other swaps red and
    /// blue — which reports a colour defect where there is none, and hides one where there is.
    /// </summary>
    private static Sampled Darkest(byte[] pixels, int stride, PixelSize size, PixelRect region, bool bgra)
    {
        int left = Math.Max(0, region.X);
        int right = Math.Min(size.Width, region.Right);
        int top = Math.Max(0, region.Y);
        int bottom = Math.Min(size.Height, region.Bottom);

        int best = int.MaxValue;
        var found = new Sampled(left, top, 255, 255, 255);

        for (int y = top; y < bottom; y++)
        {
            for (int x = left; x < right; x++)
            {
                int at = (y * stride) + (x * 4);
                int r = bgra ? pixels[at + 2] : pixels[at];
                int g = pixels[at + 1];
                int b = bgra ? pixels[at] : pixels[at + 2];

                if (r + g + b < best)
                {
                    best = r + g + b;
                    found = new Sampled(x, y, r, g, b);
                }
            }
        }

        return found;
    }

    private static byte[] Pixels(WriteableBitmap bitmap)
    {
        int stride = bitmap.PixelSize.Width * 4;
        var buffer = new byte[stride * bitmap.PixelSize.Height];
        GCHandle handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(
                new PixelRect(0, 0, bitmap.PixelSize.Width, bitmap.PixelSize.Height),
                handle.AddrOfPinnedObject(),
                buffer.Length,
                stride);
        }
        finally
        {
            handle.Free();
        }

        return buffer;
    }

    /// <summary>
    /// Runs a test with the real Avalonia measurer installed, which is what the running application
    /// installs and what the layout and the painter must agree through. It is process-wide static state,
    /// so it is put back.
    /// </summary>
    private static void WithRealMetrics(Action body)
    {
        ITextMetrics? previous = TextMeasurement.Current;
        TextMeasurement.Current = new AvaloniaTextMetrics();
        try
        {
            body();
        }
        finally
        {
            TextMeasurement.Current = previous;
        }
    }

    private static void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }
}
