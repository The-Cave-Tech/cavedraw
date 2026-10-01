using System.Globalization;
using System.Text.RegularExpressions;
using VCCad.Core.Model;
using VCCad.Core.Selection;
using VCCad.Core.Svg;
using VCCad.Geometry;
using VCCad.Pdf;
using Xunit;

namespace VCCad.Pdf.Tests;

/// <summary>
/// Text inside a transformed group is exported in that group's frame (issue #164).
///
/// The exporter wrote every block through <c>AllTextItems(artboard)</c> with the identity frame, and
/// <c>PaintItem</c> returns early for a <see cref="TextItem"/>, so a group's transform reached paths and images and
/// never text: the block was written at its own local origin, unscaled. Since **every** SVG import carries a
/// unit-conversion group at its root, this is every imported document with text in it, not only one an author put a
/// `<g>` around.
///
/// The assertions are on the **exported geometry**, read back through the vector import path.
/// <see cref="PdfImporter.Import(byte[])"/> cannot answer this question: our export carries the lossless model as a
/// sidecar and the importer prefers it, so it hands back the model it was handed - group transform and all - and
/// reports the same numbers whatever the content stream says. <c>TryImportVector</c> reads the page the way an
/// outside reader does, which is the only reading that can disagree with the exporter.
///
/// The quantity asserted is the **baseline**, because that is the placement a PDF text matrix actually carries and
/// it reads back exactly. The block's top-left is one ascent above it, and the exporter and the importer each take
/// that ascent from a different place (the model's <see cref="TextRun.PlacedAscentEm"/> against the embedded
/// programme's <c>/Ascent</c>), so the re-imported top-left carries a constant offset that has nothing to do with
/// the group frame. It is reported in the failure message rather than asserted away.
/// </summary>
public class GroupTransformTextExportTests
{
    // 200x200 user units at 0.75 pt per unit is a 150x150 pt page, which is the root group's own transform.
    private const string Header = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"200\" height=\"200\">";

    /// <summary>
    /// The figure the issue measured: `translate(50,50) scale(2)` over a 10pt line at x=0 y=10.
    ///
    /// Its local baseline is (0,10) user units, and the frame above it is the explicit group composed with the
    /// root unit-conversion group: `(1.5, 0, 0, 1.5, 37.5, 37.5)`, so the baseline lands at (37.5, 52.5) pt and the
    /// 10pt face is set at 15pt.
    /// </summary>
    private const string TranslatedAndScaled = Header +
        "<g transform=\"translate(50,50) scale(2)\"><text x=\"0\" y=\"10\" font-size=\"10\">Hi</text></g></svg>";

    /// <summary>
    /// The case that reaches every imported document: the text's only frame is the root unit-conversion group the
    /// reader creates for a file measured in user units - `scale(0.75)`. The same baseline lands at (0, 7.5) pt and
    /// the 10pt face is set at 7.5pt.
    /// </summary>
    private const string InTheRootGroup = Header +
        "<text x=\"0\" y=\"10\" font-size=\"10\">Hi</text></svg>";

    /// <summary>
    /// A group that turns as well as moves: `translate(40,40) rotate(90)`.
    ///
    /// This is the case a frame that carried only the translation and the scale would still get wrong - the
    /// composition's third and fourth coefficients are the ones that bring the rotation, and a text matrix is where
    /// they have to land. The local baseline (0,10) maps to (22.5, 30) pt and the face stays 7.5pt, because a
    /// rotation does not scale.
    /// </summary>
    private const string Rotated = Header +
        "<g transform=\"translate(40,40) rotate(90)\"><text x=\"0\" y=\"10\" font-size=\"10\">Hi</text></g></svg>";

    /// <summary>
    /// The text of an SVG, and the same text as the exported PDF hands it back to a reader.
    ///
    /// Both halves come from the file that was written rather than from the model: the "exported" half is what
    /// <see cref="PdfImporter"/>'s vector path recovers from the page, so it cannot agree with the exporter by
    /// construction.
    /// </summary>
    private static (TextItem Source, TextItem Exported) RoundTrip(string svg)
    {
        CadDocument document = SvgReader.Read(svg).Document;
        TextItem source = Assert.Single(document.AllItems().OfType<TextItem>());

        byte[] pdf = PdfDocumentExporter.Export(document);
        Assert.True(PdfImporter.TryImportVector(pdf, out CadDocument? back) && back is not null,
            "the exported PDF did not come back through the vector import");
        TextItem exported = Assert.Single(back!.AllItems().OfType<TextItem>());
        return (source, exported);
    }

    /// <summary>
    /// Where a block's own text matrix puts it: the baseline, in model points, with the block's rotation folded in.
    ///
    /// `Origin` is the top-left and a PDF text matrix is set on the baseline, so the baseline is one ascent down the
    /// text's own up axis - the same arithmetic <c>WriteText</c> does. It is worked out from whichever item is
    /// handed in, so the source block and the re-imported one are read by the same rule.
    /// </summary>
    private static (Point2D Baseline, double Size) Placed(TextItem text)
    {
        TextRun run = Assert.Single(text.Runs);
        double depth = run.PlacedAscentEm * run.FontSize;
        double cos = Math.Cos(text.RotationRadians);
        double sin = Math.Sin(text.RotationRadians);
        return (
            new Point2D(text.Origin.X - (sin * depth), text.Origin.Y + (cos * depth)),
            run.FontSize);
    }

    /// <summary>
    /// **The exported geometry is the frame's, not the block's own.** Against the exporter this replaces, the
    /// vector import recovers the untransformed baseline and the untransformed size.
    /// </summary>
    private static void AssertPlaced(string what, Point2D wantedBaseline, double wantedSize, TextItem exported)
    {
        (Point2D baseline, double size) = Placed(exported);

        Assert.True(
            Math.Abs(baseline.X - wantedBaseline.X) < 1e-2 &&
            Math.Abs(baseline.Y - wantedBaseline.Y) < 1e-2 &&
            Math.Abs(size - wantedSize) < 1e-3,
            $"{what}: wanted the baseline at ({Number(wantedBaseline.X)}, {Number(wantedBaseline.Y)}) pt " +
            $"in a {Number(wantedSize)} pt face; the exported PDF re-imports at " +
            $"({Number(baseline.X)}, {Number(baseline.Y)}) pt in a {Number(size)} pt face " +
            $"(block top-left {Number(exported.Origin.X)}, {Number(exported.Origin.Y)}).");
    }

    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>
    /// Text in a translated-and-scaled group lands where the group puts it, at the group's scale.
    ///
    /// The expected baseline is also taken from <see cref="SelectionEngine.ToWorld"/> - the composition the canvas
    /// and the selection engine use - so the same test states the canvas-versus-PDF agreement for a **text** figure
    /// rather than only for the rect <c>TheCanvasAndTheExportedPdfFillOnePlan</c> uses (#159).
    /// </summary>
    [Fact]
    public void TextInATranslatedAndScaledGroupIsExportedInThatFrame()
    {
        if (!StandardFontFixture.Available) { return; }

        (TextItem source, TextItem exported) = RoundTrip(TranslatedAndScaled);

        // The model's own composition, worked out before the export is read, and the same numbers the scheme above
        // predicts - so "the canvas's frame" and "the transformed baseline" are not two claims that could drift.
        Point2D canvas = SelectionEngine.ToWorld(source).Transform(Placed(source).Baseline);
        Assert.Equal(37.5, canvas.X, 6);
        Assert.Equal(52.5, canvas.Y, 6);

        AssertPlaced("text in a translated-and-scaled group", canvas, 15.0, exported);
    }

    /// <summary>
    /// The root `scale(0.75)` group an SVG import produces carries text too - the case that affects every imported
    /// document rather than only one with an author's `<g>`.
    /// </summary>
    [Fact]
    public void TextInTheRootUnitConversionGroupOfAnSvgImportIsExportedInThatFrame()
    {
        if (!StandardFontFixture.Available) { return; }

        (TextItem source, TextItem exported) = RoundTrip(InTheRootGroup);

        Point2D canvas = SelectionEngine.ToWorld(source).Transform(Placed(source).Baseline);
        Assert.Equal(0.0, canvas.X, 6);
        Assert.Equal(7.5, canvas.Y, 6);

        AssertPlaced("text in the root unit-conversion group", canvas, 7.5, exported);
    }

    /// <summary>
    /// A **rotated** group turns the text with it, and does not scale it.
    ///
    /// A placement that carried the frame's translation and scale but not its rotation would put this block at the
    /// right distance in the wrong direction, which is why the assertion is on both components of the baseline.
    /// The composition lands in the text matrix's third and fourth coefficients - the same six numbers a `cm`
    /// writes - so a rotation needs no separate rule and nothing about it is left unexpressed (issue #164, point 4).
    /// </summary>
    [Fact]
    public void TextInARotatedGroupIsTurnedByThatGroup()
    {
        if (!StandardFontFixture.Available) { return; }

        (TextItem source, TextItem exported) = RoundTrip(Rotated);

        Point2D canvas = SelectionEngine.ToWorld(source).Transform(Placed(source).Baseline);
        Assert.Equal(22.5, canvas.X, 6);
        Assert.Equal(30.0, canvas.Y, 6);

        AssertPlaced("text in a rotated group", canvas, 7.5, exported);
    }

    /// <summary>The operators of the page, decoded.</summary>
    private static string Operators(CadDocument document) => PdfDrawing.Of(PdfDocumentExporter.Export(document));

    /// <summary>
    /// A block's own clips are in the block's own frame, so they go through the group frame with it.
    ///
    /// The clip is stated in the block's local coordinates - <c>AppendClip</c> is handed the frame its item is
    /// placed in - so writing it at the identity frame while the text moved would clip the moved text with an
    /// unmoved outline, which is a different picture rather than a missing one.
    /// </summary>
    [Fact]
    public void ATextClipInsideAGroupIsWrittenInThatGroupFrame()
    {
        if (!StandardFontFixture.Available) { return; }

        CadDocument document = CadDocument.CreateDefault("Clipped text in a group");

        var text = new TextItem { Name = "Labelled", Origin = new Point2D(10, 20), Color = ColorRgb.Black };
        text.Runs.Add(new TextRun { Text = "AB", FontFamily = "Nimbus Sans", FontSize = 12 });

        // Deliberately not on the page corner and not on any round number: an unmoved clip and a moved one differ
        // by the group's own 50pt translation and its doubling, and these four numbers appear nowhere else.
        var clip = new ClipSpec();
        var sub = new SubPath { IsClosed = true };
        sub.Nodes.Add(new PathNode(new Point2D(5.5, 6.5)));
        sub.Nodes.Add(new PathNode(new Point2D(7.5, 6.5)));
        sub.Nodes.Add(new PathNode(new Point2D(7.5, 8.5)));
        sub.Nodes.Add(new PathNode(new Point2D(5.5, 8.5)));
        clip.SubPaths.Add(sub);
        text.Clips.Add(clip);

        var group = new ArtGroup { Name = "moved", Transform = new AffineTransform(2, 0, 0, 2, 50, 50) };
        group.AddItem(text);
        document.Artboards[0].Layers[0].AddItem(group);

        List<double> numbers = Regex
            .Matches(Operators(document), @"-?\d+(?:\.\d+)?")
            .Select(m => double.Parse(m.Value, CultureInfo.InvariantCulture))
            .ToList();

        // 2*5.5+50 = 61, 2*6.5+50 = 63, 2*7.5+50 = 65, 2*8.5+50 = 67.
        foreach (double wanted in new[] { 61.0, 63.0, 65.0, 67.0 })
        {
            Assert.True(numbers.Any(n => Math.Abs(n - wanted) < 1e-6),
                $"the clip corner {Number(wanted)} is not in the page: the outline was written in the block's own " +
                "frame rather than the group's.");
        }
    }
}
