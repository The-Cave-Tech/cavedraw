using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using VCCad.Core.Model;
using VCCad.Core.Selection;
using VCCad.Geometry;
using VCCad.Pdf;
using Xunit;

namespace VCCad.Pdf.Tests;

/// <summary>
/// A mirrored text block survives the round trip with its **absolute** geometry intact (issue #171).
///
/// The exporter half (<c>a87a03b</c>) writes the mirror into the text matrix, and re-importing it is the other
/// half: the importer decomposes a text matrix into one uniform <c>Scale</c>, one <c>RotationRadians</c> and
/// the block's top-left, and a reflection - a matrix whose determinant has the opposite sign to the page's own
/// y-flip - is none of those three. It is <see cref="TextItem.MirrorX"/> / <see cref="TextItem.MirrorY"/>,
/// which the content importer never set, so a flipped block re-imported *turned* instead and landed a whole
/// ascent away from the baseline the file states.
///
/// **Why a difference between two blocks cannot see this.** <c>MirroredTextExportTests</c> compares a flipped
/// block against a plain one, and a constant displacement of the whole block cancels out of a difference. The
/// defect is exactly such a displacement - two ascents, <c>2 * 8.748 pt</c>, in the vertical case - so those
/// tests are green while the picture is wrong. Every assertion here is therefore *absolute*: the re-imported
/// block's placement is compared against **the file's own <c>Tm</c> operator**, read back out of the decoded
/// page content stream, and the recovered item is composed the way the canvas composes it
/// (<c>CanvasWorkspace.PaintText</c>: mirror first, then rotation, about the block's origin) through
/// <see cref="SelectionEngine.ToWorld"/>.
///
/// That reference is deliberate. Restating the exporter's arithmetic here would only prove it agrees with
/// itself; the <c>Tm</c> in the file is what any other reader sees, so "the recovered block draws where the
/// file puts the text" is the claim under test, and it is the claim the defect broke.
/// </summary>
public class MirroredTextImportTests
{
    /// <summary>The block every figure is built at, far enough from the page corner that a flip is unmistakable.</summary>
    private static readonly Point2D BlockOrigin = new(20, 30);

    private const double BlockSize = 12.0;

    /// <summary>`TextItem.LineSpacing`'s default, which is what the two-line figures stack by.</summary>
    private const double LineHeight = BlockSize * 1.2;

    /// <summary>
    /// How far a re-imported baseline may sit from the one the file states, in points.
    ///
    /// The round trip is exact to about 3e-5 pt here, and the defect this file exists for displaces a baseline
    /// by two ascents - 18.77 pt at this size - so the tolerance is nowhere near the thing it has to catch.
    /// </summary>
    private const double Tolerance = 0.01;

    /// <summary>A document with one text block at <see cref="BlockOrigin"/>, optionally mirrored, optionally in a group.</summary>
    private static CadDocument Document(bool mirrorX, bool mirrorY, string text, AffineTransform? group = null)
    {
        CadDocument document = CadDocument.CreateDefault("mirrored text import");

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
    /// The text matrices the exported page states, in stream order - the file's own answer to "where is this
    /// line and which way do its glyphs run".
    ///
    /// The exporter sets one <c>Tm</c> per text object, in the **model** frame: the page's own
    /// <c>1 0 0 -1 0 H cm</c> is a separate operator, so <c>e f</c> is the baseline in the same coordinates the
    /// model stores. That is what makes this an absolute reference and not a second copy of the exporter.
    /// </summary>
    private static List<(Point2D Baseline, Vector2D Advance)> FileBaselines(string operators)
    {
        var found = new List<(Point2D, Vector2D)>();

        foreach (Match match in Regex.Matches(operators, TmOperator))
        {
            double a = Number(match, "a");
            double b = Number(match, "b");
            double e = Number(match, "e");
            double f = Number(match, "f");

            // A text matrix's first column is where text space's +x goes, which is the direction the glyphs
            // advance in.
            found.Add((new Point2D(e, f), new Vector2D(a, b)));
        }

        return found;
    }

    private static readonly string TmOperator =
        @"(?<a>-?\d+(?:\.\d+)?) (?<b>-?\d+(?:\.\d+)?) (?<c>-?\d+(?:\.\d+)?) " +
        @"(?<d>-?\d+(?:\.\d+)?) (?<e>-?\d+(?:\.\d+)?) (?<f>-?\d+(?:\.\d+)?) Tm";

    private static double Number(Match match, string group)
        => double.Parse(match.Groups[group].Value, CultureInfo.InvariantCulture);

    /// <summary>Where the canvas draws a recovered block's glyphs, from the block's own members.</summary>
    private static (Point2D Baseline, Vector2D Advance) CanvasPlacement(TextItem text)
    {
        TextRun run = Assert.Single(text.Runs);
        double depth = run.PlacedAscentEm * run.FontSize;
        double cos = Math.Cos(text.RotationRadians);
        double sin = Math.Sin(text.RotationRadians);

        // `CanvasWorkspace.PaintText` places the block as `origin + R(rot) * S(XSign, YSign) * (p - origin)`:
        // the layout's baseline is one ascent down the block's OWN y axis, and the glyphs advance along its
        // own x axis. Both signs are the mirror the importer is supposed to have recovered.
        Point2D baselineLocal = new(
            text.Origin.X - (sin * depth * text.YSign),
            text.Origin.Y + (cos * depth * text.YSign));
        var advanceLocal = new Vector2D(text.XSign * cos, text.XSign * sin);

        AffineTransform toWorld = SelectionEngine.ToWorld(text);
        return (toWorld.Transform(baselineLocal), toWorld.Transform(advanceLocal));
    }

    /// <summary>
    /// **The claim under test, for every line the block was set as.** The exported page and the re-imported
    /// model have to agree about both halves of a block's placement: the baseline it sits on, and the way its
    /// glyphs run. Both are read from the re-imported item through the canvas's own composition, so a mirror
    /// the importer dropped shows up here as a baseline a whole ascent out and an advance pointing the wrong
    /// way - never as a difference that cancels.
    /// </summary>
    private static void AssertOnTheFilesSecondReading(CadDocument document)
    {
        byte[] pdf = PdfDocumentExporter.Export(document);
        List<(Point2D Baseline, Vector2D Advance)> stated = FileBaselines(PdfDrawing.Of(pdf));

        Assert.True(stated.Count > 0, "the exported page states no text matrix at all.");

        Assert.True(PdfImporter.TryImportVector(pdf, out CadDocument? back) && back is not null,
            "the exported PDF did not come back through the vector import.");

        List<TextItem> recovered = back!.AllItems().OfType<TextItem>().ToList();
        Assert.True(recovered.Count == stated.Count,
            $"the page states {stated.Count} text placement(s) and the importer rebuilt {recovered.Count} " +
            "block(s).");

        for (int i = 0; i < recovered.Count; i++)
        {
            (Point2D baseline, Vector2D advance) = CanvasPlacement(recovered[i]);
            (Point2D wanted, Vector2D wantedAdvance) = stated[i];

            Assert.True(
                Math.Abs(baseline.X - wanted.X) < Tolerance && Math.Abs(baseline.Y - wanted.Y) < Tolerance,
                $"block {i} re-imports with its baseline at ({Number(baseline.X)}, {Number(baseline.Y)}) pt " +
                $"but the file states ({Number(wanted.X)}, {Number(wanted.Y)}) pt - {Number(baseline.Y - wanted.Y)} " +
                $"pt out in y. The mirror did not survive the round trip: MirrorX={recovered[i].MirrorX}, " +
                $"MirrorY={recovered[i].MirrorY}, rotation {Number(recovered[i].RotationRadians)} rad.");

            // Direction, not length: the file's first column also carries the group scale, which the
            // importer folds into the font size instead.
            Assert.True(
                Cross(wantedAdvance, advance) < Tolerance && Dot(wantedAdvance, advance) > 0,
                $"block {i} re-imports with its glyphs advancing ({Number(advance.X)}, {Number(advance.Y)}) " +
                $"while the file runs them ({Number(wantedAdvance.X)}, {Number(wantedAdvance.Y)}). The mirror " +
                "was recovered on the wrong axis, or turned into a rotation.");
        }
    }

    private static double Cross(Vector2D a, Vector2D b) => (a.X * b.Y) - (a.Y * b.X);

    private static double Dot(Vector2D a, Vector2D b) => (a.X * b.X) + (a.Y * b.Y);

    /// <summary>
    /// The re-imported first line, and the file's baseline for it, as numbers a message can carry.
    /// </summary>
    private static string Where(TextItem text, Point2D fileBaseline)
    {
        (Point2D baseline, Vector2D advance) = CanvasPlacement(text);
        return $"re-imported baseline ({Number(baseline.X)}, {Number(baseline.Y)}) pt, advance " +
               $"({Number(advance.X)}, {Number(advance.Y)}); the file states ({Number(fileBaseline.X)}, " +
               $"{Number(fileBaseline.Y)}) pt";
    }

    private static string Number(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>Exports, re-imports, and hands back the file's baselines plus the recovered items.</summary>
    private static (List<(Point2D Baseline, Vector2D Advance)> Stated, List<TextItem> Recovered) RoundTrip(
        CadDocument document)
    {
        byte[] pdf = PdfDocumentExporter.Export(document);
        List<(Point2D Baseline, Vector2D Advance)> stated = FileBaselines(PdfDrawing.Of(pdf));

        Assert.True(PdfImporter.TryImportVector(pdf, out CadDocument? back) && back is not null,
            "the exported PDF did not come back through the vector import.");

        return (stated, back!.AllItems().OfType<TextItem>().ToList());
    }

    /// <summary>
    /// **Mirrored horizontally.** The flip is about the block's own vertical axis, so the baseline does not
    /// move and the glyphs run the other way: the ink lies to the *left* of the origin where the unflipped
    /// block's lies to the right.
    ///
    /// Against the importer this replaces, the block came back with the same baseline but the glyphs running
    /// left to right and the origin lifted a whole ascent - the file's own matrix read as a half-turn instead
    /// of a reflection.
    /// </summary>
    [Fact]
    public void AHorizontallyMirroredBlockReimportsWithItsGlyphsOnTheFlippedSide()
    {
        if (!StandardFontFixture.Available) { return; }

        (List<(Point2D Baseline, Vector2D Advance)> stated, List<TextItem> recovered) =
            RoundTrip(Document(true, false, "Hi"));

        TextItem block = Assert.Single(recovered);
        (Point2D baseline, Vector2D advance) = CanvasPlacement(block);

        Assert.True(advance.X < -1e-6,
            $"{Where(block, stated[0].Baseline)}. A block mirrored across its own vertical axis draws its " +
            "glyphs to the left of the origin; the re-imported one still runs them to the right.");

        // Absolute, not a difference between two blocks: this is the file's own baseline.
        Assert.True(Math.Abs(baseline.Y - stated[0].Baseline.Y) < Tolerance,
            $"{Where(block, stated[0].Baseline)}. The flipped block's baseline is not on the baseline the " +
            "file states - the mirror was recovered as a rotation and the block moved by an ascent.");

        AssertOnTheFilesSecondReading(Document(true, false, "Hi"));
    }

    /// <summary>
    /// **Mirrored vertically, one line.** The flip is about the block's own horizontal axis, so the line lands
    /// *above* its origin where the unflipped one sits below: the ink is on the other side of the origin.
    ///
    /// This is the case the issue measured. The file states a baseline at y = 20.61 pt for an origin at
    /// y = 30 pt; the importer this replaces placed it at 39.38 pt - two ascents (2 * 8.748) away.
    /// </summary>
    [Fact]
    public void AVerticallyMirroredBlockReimportsWithItsInkAboveTheOrigin()
    {
        if (!StandardFontFixture.Available) { return; }

        (List<(Point2D Baseline, Vector2D Advance)> stated, List<TextItem> recovered) =
            RoundTrip(Document(false, true, "Hi"));

        TextItem block = Assert.Single(recovered);
        (Point2D baseline, Vector2D advance) = CanvasPlacement(block);

        Assert.True(baseline.Y < block.Origin.Y - 1.0,
            $"{Where(block, stated[0].Baseline)}. A block mirrored across its own horizontal axis puts its " +
            $"baseline above its origin (y = {Number(block.Origin.Y)}); the re-imported one is below it.");

        Assert.True(advance.X > 1e-6,
            $"{Where(block, stated[0].Baseline)}. A vertical flip leaves the glyphs running left to right; " +
            "the re-imported run has them going the other way, so the wrong axis was flipped.");

        Assert.True(Math.Abs(baseline.Y - stated[0].Baseline.Y) < Tolerance,
            $"{Where(block, stated[0].Baseline)}. The re-imported baseline is not where the file puts it.");

        AssertOnTheFilesSecondReading(Document(false, true, "Hi"));
    }

    /// <summary>
    /// **Mirrored vertically, two lines - the measured case.** A one-line block reads the same at its top-left
    /// whatever the flip's sign, so the line *stack* is the half of a vertical mirror that can only be right
    /// if the mirror was recovered: a flipped block's later lines are **above** the origin, at
    /// <c>origin - lineHeight</c>, and the file says so itself.
    ///
    /// Every one of these baselines is absolute. Before the fix the pair re-imported at 39.38 and 24.98 pt
    /// where the file states 20.61 and 6.21 pt - an 18.77 pt displacement per line, which is why comparing the
    /// block against an unflipped one could not see it.
    /// </summary>
    [Fact]
    public void AVerticallyMirroredTwoLineBlockReimportsItsLinesAboveTheOrigin()
    {
        if (!StandardFontFixture.Available) { return; }

        (List<(Point2D Baseline, Vector2D Advance)> stated, List<TextItem> recovered) =
            RoundTrip(Document(false, true, "Hi\nHo"));

        Assert.True(recovered.Count == 2 && stated.Count == 2,
            $"expected two lines; the page states {stated.Count} and the importer rebuilt {recovered.Count}.");

        for (int i = 0; i < 2; i++)
        {
            (Point2D baseline, _) = CanvasPlacement(recovered[i]);
            Assert.True(Math.Abs(baseline.Y - stated[i].Baseline.Y) < Tolerance,
                $"line {i}: {Where(recovered[i], stated[i].Baseline)}. Every line of a mirrored block has to " +
                "land on the baseline the file states.");
        }

        Assert.True(recovered[1].Origin.Y < recovered[0].Origin.Y - 1.0,
            $"a mirrored block stacks its lines upward, but they re-imported at y = " +
            $"{Number(recovered[0].Origin.Y)} then {Number(recovered[1].Origin.Y)}.");

        Assert.True(Math.Abs(stated[1].Baseline.Y - (stated[0].Baseline.Y - LineHeight)) < Tolerance,
            "the file's own second baseline is not one line height above the first, so this fact would not " +
            "pin the stack.");
    }

    /// <summary>
    /// **Both axes at once.** The glyphs run to the other side *and* the lines stack upward - the two readings
    /// above on one block, and the case whose determinant is positive again, so a rule that only knew the
    /// determinant's sign without the half-turn it leaves in the rotation would flip this one twice.
    /// </summary>
    [Fact]
    public void ABlockMirroredOnBothAxesReimportsFlippedOnBoth()
    {
        if (!StandardFontFixture.Available) { return; }

        (List<(Point2D Baseline, Vector2D Advance)> stated, List<TextItem> recovered) =
            RoundTrip(Document(true, true, "Hi\nHo"));

        Assert.True(recovered.Count == 2 && stated.Count == 2,
            $"expected two lines; the page states {stated.Count} and the importer rebuilt {recovered.Count}.");

        for (int i = 0; i < 2; i++)
        {
            (Point2D baseline, Vector2D advance) = CanvasPlacement(recovered[i]);
            Assert.True(advance.X < -1e-6,
                $"{Where(recovered[i], stated[i].Baseline)}. The horizontal half of the flip is missing.");
            Assert.True(Math.Abs(baseline.Y - stated[i].Baseline.Y) < Tolerance,
                $"{Where(recovered[i], stated[i].Baseline)}. The vertical half of the flip is missing, or the " +
                "recovered rotation does not carry the half-turn a double flip needs.");
        }

        Assert.True(recovered[1].Origin.Y < recovered[0].Origin.Y - 1.0,
            $"the block mirrored on both axes re-imported its lines at y = {Number(recovered[0].Origin.Y)} " +
            $"then {Number(recovered[1].Origin.Y)}; the stack still runs downward.");
    }

    /// <summary>
    /// **Inside a scaling group.** The group is `translate(50,50) scale(2)`, so the block's own axes are the
    /// page's but twice as long, and the mirror is about the group's image of the origin (90, 110) pt - not
    /// about x = 20, which is what applying the mirror in document space would give.
    ///
    /// Both lines are asserted against the file, and the group's frame is asserted on its own first, so a
    /// number that came out right cannot be blamed on a group that was never composed.
    /// </summary>
    [Fact]
    public void AMirroredBlockInsideAScalingGroupReimportsInThatGroupsFrame()
    {
        if (!StandardFontFixture.Available) { return; }

        var group = new AffineTransform(2, 0, 0, 2, 50, 50);

        (List<(Point2D Baseline, Vector2D Advance)> stated, List<TextItem> recovered) =
            RoundTrip(Document(true, false, "Hi", group));

        TextItem block = Assert.Single(recovered);
        Point2D wantedOrigin = new((2 * BlockOrigin.X) + 50, (2 * BlockOrigin.Y) + 50);

        Assert.True(Math.Abs(block.Origin.X - wantedOrigin.X) < Tolerance &&
                    Math.Abs(block.Origin.Y - wantedOrigin.Y) < Tolerance &&
                    Math.Abs(block.Runs[0].FontSize - (2 * BlockSize)) < 1e-3,
            $"the group's frame was not composed: the block re-imported at " +
            $"({Number(block.Origin.X)}, {Number(block.Origin.Y)}) in a {Number(block.Runs[0].FontSize)}pt " +
            $"face, not ({Number(wantedOrigin.X)}, {Number(wantedOrigin.Y)}) in {Number(2 * BlockSize)}pt.");

        (Point2D baseline, Vector2D advance) = CanvasPlacement(block);
        Assert.True(advance.X < -1e-6,
            $"{Where(block, stated[0].Baseline)}. Inside the group's frame the mirrored glyphs have to run " +
            "the other way.");

        Assert.True(Math.Abs(baseline.Y - stated[0].Baseline.Y) < Tolerance,
            $"{Where(block, stated[0].Baseline)}. The mirrored block is not on the baseline the file states, " +
            "so the mirror was recovered in the wrong frame or not at all.");

        AssertOnTheFilesSecondReading(Document(false, true, "Hi", group));
    }

    /// <summary>
    /// **Mirrored horizontally inside a rotated group - the orthogonal case.** The group turns the block a
    /// quarter turn, so the group's own x axis is the page's y axis: a horizontal flip in the group's frame
    /// sends the glyphs *down* the page, and the block's own <see cref="TextItem.MirrorX"/> has to come back as
    /// the recovered <see cref="TextItem.MirrorY"/>. A rule that read the sign off the page rather than off the
    /// composed matrix gets this one backwards, and a rule that applied the mirror in document space points the
    /// glyphs along the wrong page axis altogether.
    /// </summary>
    [Fact]
    public void AHorizontallyMirroredBlockInsideARotatedGroupReimportsAlongTheGroupsOwnAxis()
    {
        if (!StandardFontFixture.Available) { return; }

        var group = new AffineTransform(0, 1, -1, 0, 40, 40);

        (List<(Point2D Baseline, Vector2D Advance)> stated, List<TextItem> recovered) =
            RoundTrip(Document(true, false, "Hi", group));

        TextItem block = Assert.Single(recovered);
        (Point2D baseline, Vector2D advance) = CanvasPlacement(block);

        Assert.True(Math.Abs(advance.Y) > 1e-6,
            $"{Where(block, stated[0].Baseline)}. The group turns the block a quarter turn, so its advance " +
            "has to run down the page, not across it.");

        Assert.True(Math.Abs(baseline.X - stated[0].Baseline.X) < Tolerance &&
                    Math.Abs(baseline.Y - stated[0].Baseline.Y) < Tolerance,
            $"{Where(block, stated[0].Baseline)}. The mirror has to be read back in the group's own frame; " +
            "the recovered baseline is not the one the file states.");

        AssertOnTheFilesSecondReading(Document(true, false, "Hi", group));
    }

    /// <summary>
    /// **Mirrored vertically inside the same rotated group**, where the group's y axis is the page's x axis, and
    /// **both axes together** in it. The pair is what makes the orthogonal case a fact rather than a reading of
    /// the code: the two flips land on the two page axes, and a rule that only knew one of them would pass one
    /// of these and fail the other.
    /// </summary>
    [Fact]
    public void AVerticallyMirroredBlockInsideARotatedGroupReimportsAcrossThePagesAxis()
    {
        if (!StandardFontFixture.Available) { return; }

        var group = new AffineTransform(0, 1, -1, 0, 40, 40);

        (List<(Point2D Baseline, Vector2D Advance)> stated, List<TextItem> recovered) =
            RoundTrip(Document(false, true, "Hi", group));

        TextItem block = Assert.Single(recovered);
        (Point2D baseline, Vector2D advance) = CanvasPlacement(block);

        Assert.True(Math.Abs(advance.Y) > 1e-6,
            $"{Where(block, stated[0].Baseline)}. The group turns the block a quarter turn, so its advance " +
            "has to run down the page.");

        Assert.True(Math.Abs(baseline.X - stated[0].Baseline.X) < Tolerance &&
                    Math.Abs(baseline.Y - stated[0].Baseline.Y) < Tolerance,
            $"{Where(block, stated[0].Baseline)}. The vertical flip in the group's frame puts the baseline " +
            "across the page; the recovered one is not where the file states.");

        AssertOnTheFilesSecondReading(Document(false, true, "Hi", group));
        AssertOnTheFilesSecondReading(Document(true, true, "Hi", group));
    }

    /// <summary>
    /// **The sign convention, read off a text matrix this project did not write.**
    ///
    /// Every fact above round-trips through our own exporter, and that is exactly what makes them a weak test
    /// of the *convention*: the exporter writes its page content inside <c>1 0 0 -1 0 H cm</c>, so the matrix
    /// the importer composes has the opposite handedness to a page written the ordinary way. A rule tuned to
    /// our own files - "the determinant is negative, therefore mirrored" - would be backwards on anyone
    /// else's. These four matrices are stated directly in a minimal page with no <c>cm</c> at all, so the
    /// determinant's sign is the file's own, and the recovered block is checked against the matrix rather
    /// than against an export.
    ///
    /// The reference is the matrix itself: a text matrix's <c>e f</c> is the baseline in page coordinates
    /// (y up, so the model's y is the page height minus it) and its first column is where text space's +x
    /// goes (in model terms, <c>(a, -b)</c>).
    /// </summary>
    [Theory]
    [InlineData("unmirrored", 1.0, 0.0, 0.0, 1.0)]
    [InlineData("mirrored horizontally", -1.0, 0.0, 0.0, 1.0)]
    [InlineData("mirrored vertically", 1.0, 0.0, 0.0, -1.0)]
    [InlineData("both axes", -1.0, 0.0, 0.0, -1.0)]
    public void AForeignTextMatrixIsReadBackOnTheBaselineItStates(
        string what, double a, double b, double c, double d)
    {
        const double PageHeight = 792.0;
        byte[] pdf = MinimalPdf(string.Create(CultureInfo.InvariantCulture,
            $"BT /F1 1 Tf {a} {b} {c} {d} 100 700 Tm (Hi) Tj ET"));

        Assert.True(PdfImporter.TryImportVector(pdf, out CadDocument? back) && back is not null,
            $"the {what} page did not import at all.");

        TextItem block = Assert.Single(back!.AllItems().OfType<TextItem>());
        (Point2D baseline, Vector2D advance) = CanvasPlacement(block);

        var wanted = new Point2D(100, PageHeight - 700);
        var wantedAdvance = new Vector2D(a, -b);

        Assert.True(Math.Abs(baseline.X - wanted.X) < Tolerance && Math.Abs(baseline.Y - wanted.Y) < Tolerance,
            $"a {what} text matrix at (100, 700) re-imports with its baseline at " +
            $"({Number(baseline.X)}, {Number(baseline.Y)}) pt, not ({Number(wanted.X)}, {Number(wanted.Y)}) pt " +
            $"(MirrorX={block.MirrorX}, MirrorY={block.MirrorY}, rotation " +
            $"{Number(block.RotationRadians)} rad).");

        Assert.True(Cross(wantedAdvance, advance) < Tolerance && Dot(wantedAdvance, advance) > 0,
            $"a {what} text matrix runs its glyphs ({Number(a)}, {Number(-b)}) on the page, and the " +
            $"re-imported block runs them ({Number(advance.X)}, {Number(advance.Y)}).");
    }

    /// <summary>
    /// A one-page, one-font PDF whose content is exactly <paramref name="content"/>, with **no** <c>cm</c>
    /// operator in it, so a text matrix in it is stated in the page's own coordinates.
    /// </summary>
    private static byte[] MinimalPdf(string content)
    {
        var widths = new StringBuilder();
        for (int code = 32; code <= 126; code++)
        {
            widths.Append("600 ");
        }

        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                + "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding "
                + $"/FirstChar 32 /LastChar 126 /Widths [{widths.ToString().Trim()}] >>",
        };

        var builder = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(builder.Length);
            builder.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        int xref = builder.Length;
        builder.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets)
        {
            builder.Append($"{offset:0000000000} 00000 n \n");
        }

        builder.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\n");
        builder.Append($"startxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(builder.ToString());
    }

    /// <summary>
    /// **A document with no mirrored text writes the same drawing operators it always did.**
    ///
    /// The mirror is a pair of signs that are both +1 when nothing is flipped, so it must compose into the
    /// text matrix as nothing at all - and a `* -1` that leaked into an unflipped matrix, or a rounding change
    /// in the composition, shows up here and nowhere else.
    ///
    /// The hash is over the **decoded page content stream**, taken through <see cref="PdfDrawing"/>: the whole
    /// file varies through `/CreationDate` and through the document sidecar, so hashing the file would answer
    /// yes to every change and prove nothing. The stream is reached through the page tree, so the ICC profile,
    /// the XMP packet and the sidecar cannot be mistaken for drawing.
    ///
    /// The numbers are the test's own: the runs carry an embedded programme whose ascent is 800, and the
    /// strings are written as the raw codes given here, so no metric of any installed font reaches the stream.
    /// This is the **pass-through** path - two runs, no newline, written as one text object.
    /// </summary>
    [Fact]
    public void AnUnmirroredDocumentHashesTheContentStreamItAlwaysDid()
    {
        if (!StandardFontFixture.Available) { return; }

        EmbeddedFont font = TestFont();
        CadDocument document = CadDocument.CreateDefault("no mirror");

        var block = new TextItem { Name = "one", Origin = new Point2D(20, 30), Color = ColorRgb.Black };
        block.Runs.Add(Embedded("Hi", font));
        block.Runs.Add(Embedded("Ho", font));
        document.Artboards[0].Layers[0].AddItem(block);

        Assert.Equal(NoMirrorOneObjectHash, ContentStreamHash(document));
    }

    /// <summary>
    /// The same no-op fact for the **per-run** path, where every run gets its own <c>Tm</c> - the other place a
    /// sign is composed - and inside a group, so that matrix is composed through a frame as well as with the
    /// block's own mirror. A block with a newline in its run keeps off the one-object path.
    /// </summary>
    [Fact]
    public void AnUnmirroredDocumentOnThePerRunPathHashesTheContentStreamItAlwaysDid()
    {
        if (!StandardFontFixture.Available) { return; }

        EmbeddedFont font = TestFont();
        CadDocument document = CadDocument.CreateDefault("no mirror");

        var block = new TextItem { Name = "two", Origin = new Point2D(20, 80), Color = ColorRgb.Black };
        block.Runs.Add(Embedded("Hi\nHo", font));
        var frame = new ArtGroup { Name = "frame", Transform = new AffineTransform(2, 0, 0, 2, 50, 50) };
        frame.AddItem(block);
        document.Artboards[0].Layers[0].AddItem(frame);

        Assert.Equal(NoMirrorPerRunHash, ContentStreamHash(document));
    }

    /// <summary>SHA-256 of the decoded page content stream of an exported document, lower-case hex.</summary>
    private static string ContentStreamHash(CadDocument document)
    {
        string operators = PdfDrawing.Of(PdfDocumentExporter.Export(document));
        return Convert.ToHexString(SHA256.HashData(Encoding.Latin1.GetBytes(operators))).ToLowerInvariant();
    }

    /// <summary>The drawing of an unmirrored document whose two runs share one text object (`a87a03b`).</summary>
    private const string NoMirrorOneObjectHash = "a46c69f46fa3d5f4f9eb10d2642b2e885cf0c607ed9a367e1a93912b7ae232db";

    /// <summary>The drawing of an unmirrored document whose runs get a `Tm` each, inside a group (`a87a03b`).</summary>
    private const string NoMirrorPerRunHash = "076a26eb02fadc209ef39e9cd66f7dd8b52c0ea0657b7b7564be84028bfdeedd";

    /// <summary>
    /// The programme every fact above embeds. It is a real face read from the machine (so the exporter has
    /// something to embed) - whether one is installed decides whether the test can run, never what it compares.
    /// </summary>
    private static EmbeddedFont TestFont()
    {
        byte[] program = StandardFontFiles.TryReadProgram(new StandardFace(StandardFontKind.Sans, false, false))!;
        return new EmbeddedFont
        {
            Format = EmbeddedFontFormat.TrueType,
            Program = program,
            BaseFont = "VCCadTestSans",
            FamilyName = "VCCadTestSans",
            FirstChar = 32,
            Widths = Enumerable.Repeat(600.0, 95).ToArray(),
            Ascent = 800,
        };
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
