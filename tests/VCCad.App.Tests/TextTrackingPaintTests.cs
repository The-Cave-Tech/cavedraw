using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using VCCad.App.Controls;
using VCCad.App.Fonts;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Core.Text;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// What a run with <c>letter-spacing</c> looks like on the canvas: the room it asks for goes
/// **between** the glyphs.
///
/// This is measured in pixels because that is the only place the defect ever showed. Every model-level
/// number is right either way — the layout, the bounds, the caret and the export all go through
/// <see cref="TextRun.Advances"/>, which adds the tracking to the pen. The painter alone took the
/// tracking as a *scale*: it sized a run by <c>box.Width / FormattedText.Width</c>, and
/// <c>FormattedText.Width</c> is the face's own advance with no tracking in it, so the glyphs were
/// stretched wider than the face instead of being spaced further apart.
///
/// The span of the run is the same either way, so an assertion on where the glyphs *are* cannot tell
/// the two apart — the pen positions coincide. What differs is a glyph's own width, and that is what
/// these tests measure, from two renders of the same text (one tracked, one not) compared against the
/// model's own advance for it.
///
/// The face is never named here: every expected number is taken from the installed measurer
/// (<see cref="TextMeasurement"/>), so the test says "the painter drew what the model said" whichever
/// family happens to answer for the block on this machine. A real measurer is installed for the
/// duration, because with none the model falls back to a per-character estimate of 0.6 em while the
/// painter shapes the real face — and then even an untracked run is drawn scaled, for reasons that
/// have nothing to do with tracking.
/// </summary>
public class TextTrackingPaintTests
{
    /// <summary>Two window pixels per model unit, so a glyph is tens of pixels wide and a shape
    /// change of a pixel or two is a real change rather than antialiasing.</summary>
    private const double Zoom = 2.0;

    private const double FontSize = 40.0;

    /// <summary>Four identical glyphs, so consecutive ink groups are the same shape and the distance
    /// between two of them is the pen advance exactly — the left side bearing cancels.</summary>
    private const string Glyphs = "IIII";

    private const double Tracking = 20.0;

    /// <summary>
    /// A tracked run is spaced, not stretched: every glyph keeps the width it has untracked, the pen
    /// advances by the face's own advance plus the tracking, and the run's drawn width is therefore
    /// the face's own width plus the tracking between its glyphs.
    ///
    /// Against the painter this replaced, the glyphs come out scaled by the tracking ratio — with four
    /// glyphs, their own advance and a 20pt tracking at 40pt, roughly 2.8x as wide — so each of the
    /// three statements below fails loudly, and the first one fails by an order of magnitude more than
    /// its tolerance.
    /// </summary>
    [AvaloniaFact]
    public void ATrackedRunAddsTheTrackingBetweenItsGlyphsInsteadOfStretchingThem()
    {
        WithRealMetrics(() =>
        {
            Painted plain = Paint(tracking: 0);
            Painted tracked = Paint(tracking: Tracking);

            Assert.True(plain.Groups.Count == 4,
                $"the four letters should be four ink groups, were {plain.Groups.Count}");
            Assert.True(tracked.Groups.Count == 4,
                $"the four letters should be four ink groups, were {tracked.Groups.Count}");

            // 1. **A glyph is as wide as it is untracked.** This is the assertion the defect fails:
            //    stretched by the tracking ratio, each letter is ~2.8x its own width.
            for (int i = 0; i < 4; i++)
            {
                double plainGlyph = plain.Groups[i].End - plain.Groups[i].Start + 1;
                double trackedGlyph = tracked.Groups[i].End - tracked.Groups[i].Start + 1;
                Assert.True(Math.Abs(trackedGlyph - plainGlyph) <= 1.5,
                    $"glyph {i} changed shape: {trackedGlyph}px with tracking, {plainGlyph}px without");
            }

            // 2. **The gaps grew, and by exactly the tracking.** Both renders put a glyph one advance
            //    after the last; the tracked one adds the tracking to that advance.
            Assert.Equal((plain.FaceAdvance + Tracking) * Zoom, tracked.Spacing(), 1.5);
            Assert.Equal(plain.FaceAdvance * Zoom, plain.Spacing(), 1.5);

            // 3. **The drawn run is the face's own width plus the tracking.** First glyph to last: the
            //    three gaps each grew by the tracking, the glyphs did not.
            Assert.Equal(3 * Tracking * Zoom, tracked.InkWidth() - plain.InkWidth(), 2.0);

            // 4. And the first glyph starts where the face puts it. Stretching moves it away from the
            //    pen in proportion — the one glyph that is not moved by any extra room between glyphs.
            Assert.Equal(plain.OffsetFromPen(0), tracked.OffsetFromPen(0), 1.0);
        });
    }

    /// <summary>
    /// A run with **no** tracking draws nothing but the face's own advances.
    ///
    /// This is the half that says the fix did not become "space everything": every drawn glyph sits on
    /// the caret position the model computed for its character, so the residual between ink and caret
    /// — the face's left side bearing — is the same for all four. A painter that started adding room
    /// between glyphs, by a default or by picking up some other run's tracking, moves each glyph
    /// further from its caret as the line goes on, and this test fails on the second one.
    ///
    /// It passes both before and after the change to the tracked path, which is the point: the pixels
    /// of an untracked run are not the fix's to move.
    /// </summary>
    [AvaloniaFact]
    public void AnUntrackedRunDrawsNothingButTheFacesOwnAdvances()
    {
        WithRealMetrics(() =>
        {
            Painted plain = Paint(tracking: 0);

            Assert.True(plain.Groups.Count == 4,
                $"the four letters should be four ink groups, were {plain.Groups.Count}");

            // Every glyph lands on its own caret: same left side bearing at each of the four.
            for (int i = 1; i < 4; i++)
            {
                Assert.Equal(plain.OffsetFromPen(0), plain.OffsetFromPen(i), 1.5 / Zoom);
            }

            // And the pen advance between two of them is the face's own, with nothing added.
            Assert.Equal(plain.FaceAdvance * Zoom, plain.Spacing(), 1.5);
        });
    }

    // ------------------------------------------------------------------
    // Rendering and measurement
    // ------------------------------------------------------------------

    /// <summary>One render of <see cref="Glyphs"/> at a fixed tracking, and what can be measured off it.</summary>
    private sealed record Painted(
        IReadOnlyList<(int Start, int End)> Groups,
        double FaceAdvance,
        IReadOnlyList<double> CaretX,
        int PenX)
    {
        /// <summary>The distance between consecutive glyphs' ink, in window pixels.</summary>
        public double Spacing() => (Groups[^1].Start - Groups[0].Start) / (double)(Groups.Count - 1);

        /// <summary>First ink column to last, in window pixels.</summary>
        public double InkWidth() => Groups[^1].End - Groups[0].Start + 1;

        /// <summary>Where a glyph's ink starts relative to the pen, in model units.</summary>
        public double OffsetFromPen(int index) => ((Groups[index].Start - PenX) / Zoom) - CaretX[index];

        /// <summary>Everything this file measures, so a failure states the whole picture.</summary>
        public string Summary()
        {
            string widths = string.Join(", ", Groups.Select(g => $"{g.End - g.Start + 1}px"));
            string offsets = string.Join(", ",
                Enumerable.Range(0, Groups.Count).Select(i => OffsetFromPen(i).ToString("F2")));
            return $"glyph widths [{widths}], glyph pitch {Spacing():F1}px " +
                   $"(the face's advance is {FaceAdvance * Zoom:F1}px), " +
                   $"drawn width {InkWidth():F1}px, offsets from the pen [{offsets}] model units";
        }
    }

    /// <summary>
    /// Draws one block of <see cref="Glyphs"/> with the given tracking and reads the glyphs' ink out of
    /// the frame.
    ///
    /// The zoom is set explicitly and the view centred on the block: the fit that runs on the first
    /// layout depends on the window and would put a different number of model units in a pixel each
    /// time. The scan region is a band around the block, well inside the white page — the pasteboard
    /// behind the page is dark, and a scan that reached it would read it as ink.
    /// </summary>
    private static Painted Paint(double tracking)
    {
        var viewModel = new EditorViewModel();
        var workspace = new CanvasWorkspace();
        workspace.AttachEditor(viewModel);

        var window = new Window { Width = 900, Height = 700, Content = workspace };
        window.Show();
        Settle();

        // The fit that ran on the first layout is overridden here, before the block is added and the
        // view is centred: from this point the zoom is ours, so no later layout pass moves it.
        workspace.ZoomTo(Zoom);

        var text = new TextItem { Name = "tracked", Origin = new Point2D(200, 300), Color = ColorRgb.Black };
        var run = new TextRun { Text = Glyphs, FontSize = FontSize, LetterSpacing = tracking };
        text.Runs.Add(run);
        viewModel.Document.Artboards[0].Layers[0].AddItem(text);

        workspace.CenterOn(text.Origin);
        workspace.InvalidateVisual();
        Settle();

        try
        {
            WriteableBitmap? frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);

            // The face's advance with no tracking in it, straight from the measurer the layout uses.
            double faceAdvance = TextMeasurement.Advances(run)[0];
            TextLayout layout = TextLayoutEngine.Compute(text);

            Point pen = workspace.ModelToWindow(text.Origin + text.ArtboardOffset());
            var region = new PixelRect(
                (int)Math.Floor(pen.X) - 6,
                (int)Math.Floor(pen.Y) - 10,
                (int)Math.Ceiling(layout.Width * Zoom) + 30,
                (int)Math.Ceiling(FontSize * Zoom) + 40);

            byte[] pixels = Pixels(frame!);
            int stride = frame!.PixelSize.Width * 4;
            return new Painted(
                InkColumns(pixels, stride, frame.PixelSize, region),
                faceAdvance,
                layout.CaretX,
                (int)Math.Round(pen.X));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Groups of consecutive columns holding ink, inside <paramref name="region"/>.</summary>
    private static List<(int Start, int End)> InkColumns(
        byte[] pixels, int stride, PixelSize size, PixelRect region)
    {
        int left = Math.Max(0, region.X);
        int right = Math.Min(size.Width, region.Right);
        int top = Math.Max(0, region.Y);
        int bottom = Math.Min(size.Height, region.Bottom);

        var groups = new List<(int Start, int End)>();
        int start = -1;

        for (int x = left; x < right; x++)
        {
            bool any = false;
            for (int y = top; y < bottom && !any; y++)
            {
                int at = (y * stride) + (x * 4);

                // The ink is black on a white page, so the three channels agree and their order does
                // not matter. The threshold is well below the page and well above the pasteboard.
                any = pixels[at] + pixels[at + 1] + pixels[at + 2] < 300;
            }

            if (any && start < 0)
            {
                start = x;
            }
            else if (!any && start >= 0)
            {
                groups.Add((start, x - 1));
                start = -1;
            }
        }

        if (start >= 0)
        {
            groups.Add((start, right - 1));
        }

        return groups;
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
    /// installs and what the layout and the painter must agree through. It is process-wide static
    /// state, so it is put back.
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
