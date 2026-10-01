using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using VCCad.App.Controls;
using VCCad.App.ViewModels;
using VCCad.App.Views;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;
using Xunit.Abstractions;

namespace VCCad.App.Tests;

/// <summary>
/// Issue #131. The Inkscape comparison harness, checked three ways: that it says "no difference"
/// when there is none, that it says "a difference" when there is one, and what number it actually
/// reports for a page both renderers draw.
///
/// The first two are what make the third worth reading. A harness that returns "identical" for
/// everything passes a calibration test perfectly and measures nothing; a harness that returns
/// "different" for everything fails one for the same reason. Both halves are therefore pinned here,
/// and they run everywhere because they use the editor's own renderer on both sides.
///
/// The calibration against Inkscape skips cleanly when Inkscape is not installed — and says so
/// through the test output, because a skipped calibration that looks green is how a fidelity claim
/// outlives the machine that made it.
/// </summary>
public class InkscapeComparisonTests
{
    private readonly ITestOutputHelper _output;

    public InkscapeComparisonTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// The comparison's own zero: the same document rendered twice must measure as nothing at all.
    ///
    /// This is the half that catches a broken decode — a byte order read wrong, a stride assumed, an
    /// alpha channel compared where one side is premultiplied — which shows up as a small non-zero
    /// difference on every comparison and would otherwise be filed as a renderer disagreement.
    /// </summary>
    [AvaloniaFact]
    public void TheSameDocumentRenderedTwiceMeasuresNoDifference()
    {
        CadDocument document = CalibrationDocument();

        byte[] first = Render(document, 0, 72);
        byte[] second = Render(document, 0, 72);

        RenderComparison comparison = InkscapeComparison.Compare(first, second);
        _output.WriteLine("same document twice: " + comparison);

        Assert.True(comparison.SizesMatch, comparison.ToString());
        Assert.Equal(0.0, comparison.MeanAbsoluteError);
        Assert.Equal(0L, comparison.DifferingPixels);
    }

    /// <summary>
    /// And its one: move the rectangle 60 pt and the measure must move with it.
    ///
    /// The floor is a measured one rather than a round number — a 60x40 pt rectangle at 72 dpi is
    /// 2400 px of ink that leaves one place and arrives at another, twice over, on an A4 landscape
    /// sheet of 501,000 px. Anything that reports zero here is not looking at the pixels.
    /// </summary>
    [AvaloniaFact]
    public void AMovedShapeIsReportedAsADifference()
    {
        byte[] before = Render(CalibrationDocument(rectX: 100), 0, 72);
        byte[] after = Render(CalibrationDocument(rectX: 160), 0, 72);

        RenderComparison comparison = InkscapeComparison.Compare(before, after);
        _output.WriteLine("rectangle moved 60 pt: " + comparison);

        Assert.True(comparison.SizesMatch, comparison.ToString());
        Assert.True(comparison.DifferingPixels > 4000,
            $"a 60x40 pt rectangle moving should have changed thousands of pixels: {comparison}");
        Assert.True(comparison.MeanAbsoluteError > 1.0,
            $"moving a solid black rectangle 60 pt should move the mean by more than 1/255: {comparison}");
    }

    /// <summary>
    /// A changed colour is a difference too, and a subtler one than a move: the ink stays where it
    /// is and only the value changes. A harness that compared coverage rather than colour would
    /// call these two pages identical.
    /// </summary>
    [AvaloniaFact]
    public void AChangedColourIsReportedAsADifference()
    {
        byte[] black = Render(CalibrationDocument(fill: ColorRgb.Black), 0, 72);
        byte[] blue = Render(CalibrationDocument(fill: ColorRgb.FromBytes(0x33, 0x66, 0xCC)), 0, 72);

        RenderComparison comparison = InkscapeComparison.Compare(black, blue);
        _output.WriteLine("fill black -> #3366cc: " + comparison);

        Assert.True(comparison.SizesMatch, comparison.ToString());
        Assert.True(comparison.DifferingPixels > 2000,
            $"recolouring a 60x40 pt rectangle should have changed its own 2400 pixels: {comparison}");
        Assert.True(comparison.MeanAbsoluteError > 0.4, comparison.ToString());
    }

    /// <summary>
    /// Rendering at a different resolution is a size mismatch, not a content difference, and the
    /// measure has to say which — otherwise every dpi mistake reads as a fidelity gap.
    /// </summary>
    [AvaloniaFact]
    public void ARenderAtAnotherResolutionIsReportedAsASizeMismatch()
    {
        byte[] at72 = Render(CalibrationDocument(), 0, 72);
        byte[] at144 = Render(CalibrationDocument(), 0, 144);

        RenderComparison comparison = InkscapeComparison.Compare(at72, at144);
        _output.WriteLine("72 dpi vs 144 dpi: " + comparison);

        Assert.False(comparison.SizesMatch, comparison.ToString());
        Assert.NotEqual(0, at72.Length);
    }

    /// <summary>
    /// The calibration: one page, both renderers, the same document.
    ///
    /// The document is deliberately the simplest thing that still has an edge and a join — a solid
    /// rectangle and a 2 pt diagonal — because those are where a vector renderer is most likely to
    /// agree and a disagreement there is therefore about the renderer rather than about a feature
    /// the SVG writer cannot express. The tolerances are the numbers this machine measured, with
    /// headroom for a different Skia or Cairo build; the assertion carries the measurement so drift
    /// is visible in the failure rather than hidden behind a boolean.
    ///
    /// **The measured gap, named.** The whole-page agreement is 0.53/255 mean, but almost all of that
    /// is not artwork: the editor's renderer draws the artboard's frame — a one-pixel grey border,
    /// (184,184,186) along the top and bottom and (140,140,144) down the sides — because it renders
    /// through the same <c>CanvasWorkspace</c> the person sees, and Inkscape's export over white has
    /// no such border. That is 2,870 of the 3,372 differing pixels. Cropping that one-pixel ring
    /// leaves **502 pixels out of 498,120** (0.101%) differing, every one of them on the
    /// anti-aliased edge of the rectangle or the stroke, and a mean of **0.026/255**. So: VCCad and
    /// Inkscape agree on the artwork to within anti-aliasing, and the frame is chrome rather than a
    /// fidelity defect — but it is in the image and a caller has to say whether it wants it.
    /// </summary>
    [AvaloniaFact]
    public void VccadAndInkscapeAgreeOnASimpleFilledRectangleAndStroke()
    {
        if (!InkscapeAvailable())
        {
            return;
        }

        CadDocument document = CalibrationDocument();
        InkscapeComparison.RenderPair pair = InkscapeComparison.RenderBoth(document, 0, 72, Render);
        RenderComparison whole = InkscapeComparison.Compare(pair.VccadPng, pair.InkscapePng);
        RenderComparison artwork = InkscapeComparison.Compare(pair.VccadPng, pair.InkscapePng, inset: 1);

        _output.WriteLine("VCCad vs Inkscape, 72 dpi, whole page: " + whole);
        _output.WriteLine("VCCad vs Inkscape, 72 dpi, page frame cropped: " + artwork);

        Assert.True(whole.SizesMatch,
            $"the two renderers must rasterise the page to the same pixel grid: {whole}");

        // Measured: mean|delta| 0.5322/255 over the whole page, 0.6% of pixels differing.
        Assert.True(whole.MeanAbsoluteError < 1.0,
            $"VCCad and Inkscape disagree about the whole page by more than 1/255 of mean channel " +
            $"difference: {whole}");
        Assert.True(whole.DifferingProportion < 0.02,
            $"more than 2% of the whole page differs by more than the tolerance: {whole}");

        // Measured: mean|delta| 0.0263/255 with the frame cropped, 0.101% of pixels differing, all
        // of them on an anti-aliased edge (max|delta| 55, well inside a half-covered pixel).
        Assert.True(artwork.MeanAbsoluteError < 0.1,
            $"with the editor's page frame cropped, the two renderers should agree on the artwork " +
            $"to well under 0.1/255: {artwork}");
        Assert.True(artwork.DifferingProportion < 0.005,
            $"more than 0.5% of the artwork area differs by more than the tolerance — the " +
            $"disagreement has left the anti-aliased edges: {artwork}");

        // And the gap is named rather than merely tolerated: the difference between the whole page
        // and the cropped page IS the frame. When the page render stops carrying the artboard's
        // chrome — the right outcome for a comparison against a PDF engine — this assertion is what
        // will notice, and it should be turned into a positive one then.
        Assert.True(whole.DifferingPixels - artwork.DifferingPixels > 2000,
            $"the editor's one-pixel page frame should account for thousands of differing pixels " +
            $"(measured 2,870 of 3,372); whole: {whole}; cropped: {artwork}");
    }

    /// <summary>
    /// And the harness's discrimination, measured through Inkscape rather than through our own
    /// renderer on both sides: rasterising the moved document must come out different from
    /// rasterising the original, so the comparison is not blind to a change that happens after the
    /// SVG export.
    /// </summary>
    [AvaloniaFact]
    public void TheInkscapeSideOfTheHarnessSeesAMovedShape()
    {
        if (!InkscapeAvailable())
        {
            return;
        }

        string? inkscape = InkscapeComparison.LocateInkscape();
        byte[] before = InkscapeComparison.RasteriseWithInkscape(
            VCCad.Core.Svg.SvgWriter.Write(CalibrationDocument(rectX: 100), 0), 72, inkscape);
        byte[] after = InkscapeComparison.RasteriseWithInkscape(
            VCCad.Core.Svg.SvgWriter.Write(CalibrationDocument(rectX: 160), 0), 72, inkscape);

        RenderComparison comparison = InkscapeComparison.Compare(before, after);
        _output.WriteLine("Inkscape only, rectangle moved 60 pt: " + comparison);

        Assert.True(comparison.SizesMatch, comparison.ToString());
        Assert.True(comparison.DifferingPixels > 4000,
            $"Inkscape's own rendering of the moved document should differ: {comparison}");
    }

    private bool InkscapeAvailable()
    {
        string? inkscape = InkscapeComparison.LocateInkscape();
        if (inkscape is null)
        {
            _output.WriteLine(
                "skip: no Inkscape on this machine (set VCCAD_INKSCAPE to its executable). " +
                "The calibration against a second renderer was not measured in this run.");
            return false;
        }

        _output.WriteLine("using " + inkscape);
        return true;
    }

    /// <summary>
    /// A default page with one filled rectangle and one stroked diagonal — the smallest document
    /// that has a fill edge, a stroke edge and a join for two rasterisers to agree about.
    /// </summary>
    private static CadDocument CalibrationDocument(
        double rectX = 100, ColorRgb? fill = null, string name = "calibration")
    {
        CadDocument document = CadDocument.CreateDefault(name);
        Layer layer = document.Artboards[0].Layers[0];

        var rectangle = new PathItem();
        SubPath box = rectangle.AddSubPath(closed: true);
        box.AppendNode(new Point2D(rectX, 100));
        box.AppendNode(new Point2D(rectX + 60, 100));
        box.AppendNode(new Point2D(rectX + 60, 140));
        box.AppendNode(new Point2D(rectX, 140));
        rectangle.Fill = FillSpec.Solid(fill ?? ColorRgb.Black);
        rectangle.Stroke = StrokeSpec.None;
        layer.AddItem(rectangle);

        var line = new PathItem();
        SubPath diagonal = line.AddSubPath(closed: false);
        diagonal.AppendNode(new Point2D(rectX, 200));
        diagonal.AppendNode(new Point2D(rectX + 120, 320));
        line.Fill = FillSpec.None;
        line.Stroke = StrokeSpec.Hairline(ColorRgb.Black) with { Width = 2.0 };
        layer.AddItem(line);

        return document;
    }

    /// <summary>
    /// Renders one page with the editor's own renderer, exactly as <c>document.renderPage</c> does:
    /// through a live workspace whose active document is the document being rendered, at 72 dpi for
    /// one pixel per point.
    /// </summary>
    private static byte[] Render(CadDocument document, int page, double dpi)
    {
        var viewModel = new EditorViewModel();

        // The canvas draws the view model's ACTIVE document, so the artwork has to be that exact
        // document object. Rendering one the workspace does not hold gives an empty page, and every
        // comparison then measures the empty-page background instead of the artwork.
        viewModel.AddDocument(document);

        var workspace = new CanvasWorkspace();
        workspace.AttachEditor(viewModel);

        var window = new Window { Width = 1200, Height = 900, Content = workspace };
        window.Show();
        try
        {
            for (int i = 0; i < 3; i++)
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            }

            workspace.InvalidateVisual();

            // The host sets this when the window is created; there is no host here.
            PageRenderer.Workspace = workspace;
            byte[]? png = PageRenderer.Render(document, page, dpi);
            Assert.NotNull(png);
            return png!;
        }
        finally
        {
            PageRenderer.Workspace = null;
            window.Close();
        }
    }
}
