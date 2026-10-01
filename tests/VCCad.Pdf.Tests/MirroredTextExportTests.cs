using System.Globalization;
using VCCad.Core.Model;
using VCCad.Geometry;
using VCCad.Pdf;
using Xunit;

namespace VCCad.Pdf.Tests;

/// <summary>
/// A mirrored text block exports mirrored (issue #171).
///
/// <see cref="TextItem.MirrorX"/> and <see cref="TextItem.MirrorY"/> are state, not baked geometry
/// (AGENTS.md §9 - a run keeps its full string and one glyph id per character), and the canvas draws them by
/// placing the block as <c>R(S(p - origin)) + origin</c> (<c>CanvasWorkspace.PaintText</c>). The PDF project never
/// mentioned either member, so a flipped block left the exporter as the unflipped one: the canvas flipped it and
/// the file lost it. A flip is expressible in a text matrix - a negative <c>a</c> or <c>d</c> is exactly a scale of
/// that axis - so this is content the file can carry, not a case to report at export.
///
/// The assertions are on the **exported geometry**, read back through the vector import path.
/// <see cref="PdfImporter.Import(byte[])"/> cannot answer this question: our export carries the lossless model as a
/// sidecar and the importer prefers it, so it hands back the model it was handed - mirror flags and all - and
/// reports the same numbers whatever the content stream says (#170). <c>TryImportVector</c> reads the page the way
/// an outside reader does.
///
/// **What each half of the mirror reads as, and why.** The importer rebuilds a block from the text matrix:
/// <c>RotationRadians</c> comes from the matrix's first column and the top-left from the baseline plus one ascent
/// along the text's own up axis. A horizontal flip shows up as the recovered run reading the other way - the same
/// glyphs, the advance on the flipped side - and that is asserted directly. A vertical flip is a *different*
/// reading: the flip's fixed point is the block's top-left, so a one-line block comes back at the same top-left
/// whatever <c>d</c>'s sign is, and the recovered item carries no mirror flag to say otherwise. What does move is
/// the **line stack** - a flipped block's later lines are above the origin - so the vertical cases below use two
/// lines and assert the stack. Both are glyph positions on the flipped side; neither is invented by the test.
/// </summary>
public class MirroredTextExportTests
{
    /// <summary>The block every figure is built at, far enough from the page corner that a flip is unmistakable.</summary>
    private static readonly Point2D BlockOrigin = new(20, 30);

    private const double BlockSize = 12.0;

    /// <summary>`TextItem.LineSpacing`'s default, which is what the two-line figures stack by.</summary>
    private const double LineHeight = BlockSize * 1.2;

    /// <summary>
    /// A document with one text block at <see cref="BlockOrigin"/>, optionally mirrored, optionally inside a group.
    /// </summary>
    private static CadDocument Document(bool mirrorX, bool mirrorY, string text, AffineTransform? group = null)
    {
        CadDocument document = CadDocument.CreateDefault("mirrored text");

        var block = new TextItem
        {
            Name = "Block",
            Origin = BlockOrigin,
            Color = ColorRgb.Black,
            MirrorX = mirrorX,
            MirrorY = mirrorY,
        };
        block.Runs.Add(new TextRun { Text = text, FontFamily = "Nimbus Sans", FontSize = BlockSize });

        if (group is { } transform)
        {
            var frame = new ArtGroup { Name = "frame", Transform = transform };
            frame.AddItem(block);
            document.Artboards[0].Layers[0].AddItem(frame);
        }
        else
        {
            document.Artboards[0].Layers[0].AddItem(block);
        }

        return document;
    }

    /// <summary>
    /// The text blocks an outside reader finds on the exported page - one per line the exporter set, in stream
    /// order. This is the whole point of the test: it is the page, not the sidecar.
    /// </summary>
    private static List<TextItem> Exported(CadDocument document)
    {
        byte[] pdf = PdfDocumentExporter.Export(document);
        Assert.True(PdfImporter.TryImportVector(pdf, out CadDocument? back) && back is not null,
            "the exported PDF did not come back through the vector import");
        return back!.AllItems().OfType<TextItem>().ToList();
    }

    /// <summary>
    /// Where a recovered block's glyphs run: the baseline it places them on, and the point its advance reaches.
    ///
    /// Both are worked out from the recovered item by the rule the item itself states - its rotation and its
    /// <see cref="TextRun.PlacedAscentEm"/> - so the reading is the importer's, not the exporter's restated.
    /// </summary>
    private sealed record Placement(Point2D Baseline, Point2D AdvanceEnd, Point2D Advance);

    private static Placement Placed(TextItem text)
    {
        TextRun run = Assert.Single(text.Runs);
        double depth = run.PlacedAscentEm * run.FontSize;
        double cos = Math.Cos(text.RotationRadians);
        double sin = Math.Sin(text.RotationRadians);
        var baseline = new Point2D(text.Origin.X - (sin * depth), text.Origin.Y + (cos * depth));
        double advance = run.AdvanceWidth ?? 0.0;
        var vector = new Point2D(advance * cos, advance * sin);
        return new Placement(
            baseline, new Point2D(baseline.X + vector.X, baseline.Y + vector.Y), vector);
    }

    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Where(string what, Placement placement) =>
        $"{what}: baseline ({Number(placement.Baseline.X)}, {Number(placement.Baseline.Y)}) pt, " +
        $"advance ({Number(placement.Advance.X)}, {Number(placement.Advance.Y)}) pt, " +
        $"end ({Number(placement.AdvanceEnd.X)}, {Number(placement.AdvanceEnd.Y)}) pt";

    /// <summary>
    /// **A horizontal flip puts the run's glyphs on the other side of the block's origin.**
    ///
    /// The block's top-left is the flip's fixed point, so the baseline stays where it was and the advance runs the
    /// other way: the recovered span is the mirror image of the unflipped span about the origin's x. Against the
    /// exporter this replaces, the mirrored export re-imports as the unflipped one - both numbers below are the
    /// source's own.
    /// </summary>
    [Fact]
    public void AHorizontallyMirroredBlockExportsItsGlyphsOnTheFlippedSide()
    {
        if (!StandardFontFixture.Available) { return; }

        Placement plain = Placed(Assert.Single(Exported(Document(false, false, "Hi"))));
        Placement flipped = Placed(Assert.Single(Exported(Document(true, false, "Hi"))));

        Assert.True(plain.Advance.X > 1e-6,
            $"{Where("unmirrored", plain)}; the unflipped run has to advance along +x for this test to mean " +
            "anything.");

        Assert.True(flipped.Advance.X < -1e-6,
            $"{Where("mirrored", flipped)}. A block mirrored across its own vertical axis has to advance the other " +
            $"way; the exported page still runs it to the right, so the file has lost the mirror.");

        // The two spans are mirror images, not merely opposite: same length, reflected about the origin.
        Assert.True(Math.Abs(flipped.AdvanceEnd.X - ((2 * BlockOrigin.X) - plain.AdvanceEnd.X)) < 1e-2,
            $"{Where("unmirrored", plain)}, {Where("mirrored", flipped)}. The flipped span is not the mirror image " +
            $"of the unflipped one about x = {Number(BlockOrigin.X)}.");
    }

    /// <summary>
    /// **A vertical flip puts the block's lines above its origin.**
    ///
    /// The line stack is the observable half of this flip: the importer rebuilds each line's top-left from its
    /// baseline and the ascent along the recovered up axis, and a vertical flip is exactly what reverses that lift.
    /// Against the exporter this replaces, the second line of the mirrored block re-imports *below* the first -
    /// the unflipped picture.
    /// </summary>
    [Fact]
    public void AVerticallyMirroredBlockExportsItsLinesAboveTheOrigin()
    {
        if (!StandardFontFixture.Available) { return; }

        List<TextItem> plain = Exported(Document(false, false, "Hi\nHo"));
        List<TextItem> flipped = Exported(Document(false, true, "Hi\nHo"));

        Assert.True(plain.Count == 2 && flipped.Count == 2,
            $"expected two blocks each (one per display line); the page re-imported {plain.Count} unflipped and " +
            $"{flipped.Count} mirrored.");

        Assert.True(plain[1].Origin.Y > plain[0].Origin.Y + 1.0,
            $"the unflipped lines are not stacked downward: {Number(plain[0].Origin.Y)} then " +
            $"{Number(plain[1].Origin.Y)}.");

        Assert.True(flipped[1].Origin.Y < flipped[0].Origin.Y - 1.0,
            $"the mirrored block's second line re-imported at y = {Number(flipped[1].Origin.Y)}, the first at " +
            $"y = {Number(flipped[0].Origin.Y)}. A block mirrored across its own horizontal axis stacks its lines " +
            $"the other way, above y = {Number(BlockOrigin.Y)}.");

        // The stack is the mirror image of the unflipped one about the origin, not merely "somewhere above".
        double wanted = (2 * BlockOrigin.Y) - plain[1].Origin.Y;
        Assert.True(Math.Abs(flipped[1].Origin.Y - wanted) < 1e-2,
            $"the mirrored second line is at y = {Number(flipped[1].Origin.Y)}; the mirror image of the unflipped " +
            $"y = {Number(plain[1].Origin.Y)} about y = {Number(BlockOrigin.Y)} is y = {Number(wanted)} " +
            $"(line height {Number(LineHeight)}).");
    }

    /// <summary>
    /// **Both axes at once.** The run advances to the other side *and* the lines stack upward - the two readings
    /// above, together, on one block. A rule that only knew one sign would pass one half and fail the other.
    /// </summary>
    [Fact]
    public void ABlockMirroredOnBothAxesExportsFlippedOnBoth()
    {
        if (!StandardFontFixture.Available) { return; }

        List<TextItem> plain = Exported(Document(false, false, "Hi\nHo"));
        List<TextItem> flipped = Exported(Document(true, true, "Hi\nHo"));

        Assert.True(plain.Count == 2 && flipped.Count == 2,
            $"expected two blocks each; the page re-imported {plain.Count} unflipped and {flipped.Count} mirrored.");

        Placement plainFirst = Placed(plain[0]);
        Placement flippedFirst = Placed(flipped[0]);

        Assert.True(flippedFirst.Advance.X < -1e-6,
            $"{Where("both axes", flippedFirst)}. The horizontal half of the flip is missing: the run still advances " +
            "along +x.");

        Assert.True(flipped[1].Origin.Y < flipped[0].Origin.Y - 1.0,
            $"the block mirrored on both axes re-imported its lines at y = {Number(flipped[0].Origin.Y)} then " +
            $"y = {Number(flipped[1].Origin.Y)}. The vertical half of the flip is missing: the stack still runs " +
            "downward.");

        Assert.True(Math.Abs(flippedFirst.AdvanceEnd.X - ((2 * BlockOrigin.X) - plainFirst.AdvanceEnd.X)) < 1e-2,
            $"{Where("unflipped", plainFirst)}, {Where("both axes", flippedFirst)}. The flipped span is not the " +
            "mirror image of the unflipped one.");
    }

    /// <summary>
    /// **A mirrored block inside a transformed group is flipped in that group's frame**, which is the composition
    /// #164 established rather than a second rule: the mirror is a scale of the text space, so it goes in the same
    /// six numbers the group's translation and scale already land in.
    ///
    /// The group is `translate(50,50) scale(2)`, so the block's origin maps to (90,110) pt and the 12pt face is set
    /// at 24pt; the flip is then reflected about x = 90, the group's image of the origin - not about the block's
    /// own x = 20, which is what applying the mirror in document space would give.
    /// </summary>
    [Fact]
    public void AMirroredBlockInsideATransformedGroupIsFlippedInThatGroupsFrame()
    {
        if (!StandardFontFixture.Available) { return; }

        var group = new AffineTransform(2, 0, 0, 2, 50, 50);

        TextItem plainBlock = Assert.Single(Exported(Document(false, false, "Hi", group)));
        TextItem flippedBlock = Assert.Single(Exported(Document(true, false, "Hi", group)));
        Placement plain = Placed(plainBlock);
        Placement flipped = Placed(flippedBlock);

        // The frame itself, so a flipped number cannot be blamed on a group that was never composed.
        Point2D wantedOrigin = new((2 * BlockOrigin.X) + 50, (2 * BlockOrigin.Y) + 50);
        Assert.True(Math.Abs(plainBlock.Origin.X - wantedOrigin.X) < 1e-2 &&
                    Math.Abs(plainBlock.Origin.Y - wantedOrigin.Y) < 1e-2 &&
                    Math.Abs(plainBlock.Runs[0].FontSize - (2 * BlockSize)) < 1e-3,
            $"{Where("unmirrored in group", plain)}, size {Number(plainBlock.Runs[0].FontSize)}pt. The group's frame " +
            $"was not composed: the block's top-left should re-import at ({Number(wantedOrigin.X)}, " +
            $"{Number(wantedOrigin.Y)}) pt in a {Number(2 * BlockSize)}pt face.");

        Assert.True(flipped.Advance.X < -1e-6,
            $"{Where("mirrored in group", flipped)}. The block is mirrored, so inside the group's frame its glyphs " +
            "have to run the other way.");

        Assert.True(
            Math.Abs(flipped.AdvanceEnd.X - ((2 * wantedOrigin.X) - plain.AdvanceEnd.X)) < 1e-2,
            $"{Where("unmirrored in group", plain)}, {Where("mirrored in group", flipped)}. The flipped span is not " +
            $"the mirror image about x = {Number(wantedOrigin.X)} - the group's image of the origin - so the mirror " +
            "was applied in the wrong frame.");
    }

    /// <summary>
    /// **A mirror inside a *rotated* group is flipped along the group's own axis**, which is what tells a composed
    /// mirror apart from one applied in document space: the group turns the block a quarter turn, so the advance
    /// runs down the page, and the mirror reverses that direction rather than turning it into the other axis.
    /// </summary>
    [Fact]
    public void AMirroredBlockInARotatedGroupIsFlippedAlongTheGroupsOwnAxis()
    {
        if (!StandardFontFixture.Available) { return; }

        var group = new AffineTransform(0, 1, -1, 0, 40, 40);

        Placement plain = Placed(Assert.Single(Exported(Document(false, false, "Hi", group))));
        Placement flipped = Placed(Assert.Single(Exported(Document(true, false, "Hi", group))));

        Assert.True(Math.Abs(plain.Advance.X) < 1e-6 && plain.Advance.Y > 1e-6,
            $"{Where("unmirrored in the rotated group", plain)}. The group turns the block a quarter turn, so its " +
            "advance has to run down the page.");

        Assert.True(Math.Abs(flipped.Advance.X) < 1e-6 && flipped.Advance.Y < -1e-6,
            $"{Where("mirrored in the rotated group", flipped)}. The mirror has to reverse the group's own axis - " +
            "the advance still runs down the page, so the flip was dropped or applied in the wrong frame.");

        // Reversed, not merely elsewhere: the same vector, negated.
        Assert.True(Math.Abs(flipped.Advance.X + plain.Advance.X) < 1e-2 &&
                    Math.Abs(flipped.Advance.Y + plain.Advance.Y) < 1e-2,
            $"the mirrored advance ({Number(flipped.Advance.X)}, {Number(flipped.Advance.Y)}) is not the reverse of " +
            $"the unmirrored one ({Number(plain.Advance.X)}, {Number(plain.Advance.Y)}).");
    }

    /// <summary>
    /// The operators a document with **no mirrored text** writes, pinned at the export this fix replaced
    /// (`ebddbb4`).
    ///
    /// The text of this document is carried by a programme the test supplies
    /// (<see cref="TextRun.EmbeddedFont"/> + <see cref="TextRun.RawCodes"/>), so the codes the exporter writes and
    /// the ascent it places them on are the test's own: whether the machine has URW fonts decides whether the test
    /// can run, never what it compares. Both text paths are covered - the block of two runs is written as one text
    /// object, the block inside the group takes the per-run path - so both <c>Tm</c> sites are in the string.
    /// </summary>
    private static readonly string NoMirrorOperators = string.Join("\n", new[]
    {
        "1 0 0 -1 0 595.275591 cm",
        "0 0 0 rg",
        "BT",
        "1 0 0 -1 20 39.6 Tm",
        "/FE1 12 Tf",
        "[<4869>] TJ",
        "[<486F>] TJ",
        "ET",
        "0 0 0 rg",
        "BT",
        "/FE1 12 Tf",
        "2 0 0 -2 90 229.2 Tm",
        "<4869486F> Tj",
        "ET",
    }) + "\n\n";

    /// <summary>
    /// **A document with no mirrored text exports byte-identical operators.**
    ///
    /// The mirror is a pair of signs with both sides +1 when nothing is flipped, so it has to leave the operators
    /// it composes into untouched - not "close enough", the same characters. A `-1` that leaked into an unflipped
    /// matrix, or a rounding change in the composition, shows up here and nowhere else in this file.
    /// </summary>
    [Fact]
    public void ADocumentWithNoMirroredTextWritesTheOperatorsItAlwaysDid()
    {
        if (!StandardFontFixture.Available) { return; }

        byte[]? program = StandardFontFiles.TryReadProgram(new StandardFace(StandardFontKind.Sans, false, false));
        if (program is null) { return; }

        var font = new EmbeddedFont
        {
            Format = EmbeddedFontFormat.TrueType,
            Program = program,
            BaseFont = "VCCadTestSans",
            FamilyName = "VCCadTestSans",
            FirstChar = 32,
            Widths = Enumerable.Repeat(600.0, 95).ToArray(),
            Ascent = 800,
        };

        CadDocument document = CadDocument.CreateDefault("no mirror");

        // Two runs, no newline: the pass-through path writes the whole block as one text object with one `Tm`.
        var oneObject = new TextItem { Name = "one", Origin = new Point2D(20, 30), Color = ColorRgb.Black };
        oneObject.Runs.Add(Embedded("Hi", font));
        oneObject.Runs.Add(Embedded("Ho", font));
        document.Artboards[0].Layers[0].AddItem(oneObject);

        // A newline in the run's text keeps it out of the one-object path, so this block takes the per-run path -
        // the other `Tm` site - with the codes still the test's own. The group gives that path a frame to compose.
        var perRun = new TextItem { Name = "two", Origin = new Point2D(20, 80), Color = ColorRgb.Black };
        perRun.Runs.Add(Embedded("Hi\nHo", font));
        var frame = new ArtGroup { Name = "frame", Transform = new AffineTransform(2, 0, 0, 2, 50, 50) };
        frame.AddItem(perRun);
        document.Artboards[0].Layers[0].AddItem(frame);

        string operators = PdfDrawing.Of(PdfDocumentExporter.Export(document));
        Assert.True(operators == NoMirrorOperators,
            "a document with no mirrored text has to export the operators it always did.\n" +
            $"expected:\n{NoMirrorOperators}\nactual:\n{operators}");
    }

    private static TextRun Embedded(string text, EmbeddedFont font) => new()
    {
        Text = text,
        FontFamily = "VCCadTestSans",
        FontSize = BlockSize,
        EmbeddedFont = font,
        RawCodes = text.Replace("\n", string.Empty),
    };
}
