using System.Text.RegularExpressions;
using VCCad.Core.Model;
using VCCad.Geometry;
using VCCad.Pdf;
using Xunit;

namespace VCCad.Pdf.Tests;

/// <summary>
/// A path with more than one stroke writes every one of them, bottom to top.
///
/// This is the test the appearance stack was really about. A path can now carry a stack, and every reader that
/// quietly kept reading the bottom stroke - the painter, the exporter, the dump, the hit-test - would look
/// perfectly correct on an ordinary document and lose artwork on exactly the drawings that use a stack. These
/// assert the file, because the file is where "the export disagrees with the canvas" shows up.
/// </summary>
public class MultiStrokeExportTests
{
    private static PathItem Line(params StrokeSpec[] strokes)
    {
        var path = new PathItem { Name = "line", Fill = FillSpec.None };
        SubPath sub = path.AddSubPath(closed: false);
        sub.Nodes.Add(new PathNode(new Point2D(40, 400)));
        sub.Nodes.Add(new PathNode(new Point2D(300, 400)));

        path.Strokes.Clear();
        path.Strokes.AddRange(strokes);
        return path;
    }

    private static StrokeSpec Stroke(double width, double r, double g, double b)
        => new(true, new ColorRgb(r, g, b), width, StrokeCap.Butt, StrokeJoin.Miter, 4);

    private static string Export(PathItem path)
    {
        CadDocument document = CadDocument.CreateDefault();
        document.Artboards[0].Layers[0].AddItem(path);
        return Inflate(PdfDocumentExporter.Export(document));
    }

    [Fact]
    public void EveryStrokeIsWritten()
    {
        string content = Export(Line(Stroke(8, 1, 0, 0), Stroke(2, 0, 0, 1)));

        Assert.Contains("1 0 0 RG", content);
        Assert.Contains("0 0 1 RG", content);
        Assert.Contains("8 w", content);
        Assert.Contains("2 w", content);
    }

    /// <summary>Bottom to top, so the stack reads the same on the page as it does in the model.</summary>
    [Fact]
    public void TheStrokesAreWrittenBottomToTop()
    {
        string content = Export(Line(Stroke(8, 1, 0, 0), Stroke(2, 0, 0, 1)));

        int red = content.IndexOf("1 0 0 RG", StringComparison.Ordinal);
        int blue = content.IndexOf("0 0 1 RG", StringComparison.Ordinal);

        Assert.True(red >= 0 && blue >= 0, "both strokes state their colour");
        Assert.True(red < blue, $"the bottom stroke is written first (red at {red}, blue at {blue})");
    }

    /// <summary>Each stroke states its own width, rather than inheriting the one before it.</summary>
    [Fact]
    public void EachStrokeStatesItsOwnWidth()
    {
        string content = Export(Line(Stroke(12, 1, 0, 0), Stroke(3, 0, 1, 0)));

        int wide = content.IndexOf("12 w", StringComparison.Ordinal);
        int narrow = content.IndexOf("3 w", StringComparison.Ordinal);

        Assert.True(wide >= 0 && narrow >= 0, "both widths are stated");
        Assert.True(wide < narrow, "each stroke sets its width immediately before it is drawn");
    }

    /// <summary>An invisible stroke in the stack is skipped, and does not disturb the ones around it.</summary>
    [Fact]
    public void AnInvisibleStrokeIsSkipped()
    {
        string content = Export(Line(Stroke(8, 1, 0, 0), StrokeSpec.None, Stroke(2, 0, 0, 1)));

        Assert.Contains("1 0 0 RG", content);
        Assert.Contains("0 0 1 RG", content);
        Assert.DoesNotContain("1 w", content);
    }

    /// <summary>
    /// And an ordinary single-stroke path is written exactly as it was before the stack existed - one colour,
    /// one width. Everything else in this suite is that case, which is the point.
    /// </summary>
    [Fact]
    public void ASingleStrokeIsWrittenOnce()
    {
        string content = Export(Line(Stroke(5, 0, 0, 0)));

        Assert.Equal(1, Regex.Matches(content, @"(?<![\d.])0 0 0 RG").Count);
        Assert.Contains("5 w", content);
    }

    // ---------------------------------------------------------------- per-stroke opacity

    /// <summary>
    /// **A translucent stroke reaches the page.** PDF has no per-colour alpha, so an opacity only exists if an
    /// ExtGState was allocated for it and the stroke switches to it with `gs`. Both halves are asserted, because
    /// either one missing is the same defect: the stroke is drawn at full strength and nothing says so.
    ///
    /// The ExtGState body is an **uncompressed object** and only the content stream is deflated, so the value is
    /// read from the raw file and the switch from the inflated one. Looking for `ca` in the inflated text finds
    /// nothing and reads as a missing feature.
    ///
    /// This is the half a round-trip test cannot see. Storing an opacity, serialising it and reading it back are
    /// all correct while the step that *honours* it never runs - which is the shape three separate bugs took in
    /// this repository, and the reason the assertion is on the operators rather than on the model.
    /// </summary>
    [Fact]
    public void ATranslucentStrokeIsWrittenThroughAnAlphaState()
    {
        StrokeSpec faint = Stroke(8, 1, 0, 0) with { Opacity = 0.25 };
        StrokeSpec solid = Stroke(2, 0, 0, 1);
        byte[] pdf = PdfDocumentExporter.Export(Document(Line(faint, solid)));

        Assert.Contains("/ca 0.25", Raw(pdf), StringComparison.Ordinal);
        Assert.Contains("/GS", Raw(pdf), StringComparison.Ordinal);
        Assert.Matches(@"/(GS\d+) gs", Inflate(pdf));
    }

    /// <summary>
    /// **The alpha state is allocated from the whole stack, not from the bottom stroke.**
    ///
    /// The bug this pins is one layer below the stack work: the exporter collected the alphas it would need by
    /// reading `path.Stroke`, so a stack whose *bottom* stroke was opaque and whose top one was translucent
    /// produced no bucket for the translucent one, `HasTransparency` was false, and no `gs` was written at all.
    /// The stroke was drawn at full strength and the exporter believed it had honoured the opacity.
    ///
    /// The bottom stroke is deliberately the opaque one, because that is the arrangement the old reading gets
    /// wrong: with the translucent stroke first it would happen to work.
    /// </summary>
    [Fact]
    public void TheAlphaStatesComeFromEveryStrokeNotOnlyTheBottomOne()
    {
        StrokeSpec solid = Stroke(8, 1, 0, 0);
        StrokeSpec faint = Stroke(2, 0, 0, 1) with { Opacity = 0.4 };
        byte[] pdf = PdfDocumentExporter.Export(Document(Line(solid, faint)));

        Assert.Contains("/ca 0.4", Raw(pdf), StringComparison.Ordinal);

        // **Each stroke switches to its own state**, read as `/GSn gs` sitting between that stroke's colour and
        // its width. The two states must differ: the bottom stroke is opaque and the top one is at 0.4, and a
        // reader that took the bottom stroke's opacity for the whole path would write the same state twice.
        //
        // The bite of this test is the line above, though: collecting alphas from `path.Stroke` finds only the
        // opaque bottom stroke, so `HasTransparency` is false and **no `/ca 0.4` is written at all**. The two
        // assertions are different halves of the same defect, which is why both are kept.
        string content = Inflate(pdf);
        Match first = Regex.Match(content, @"1 0 0 RG\s+0 J\s+0 j\s+4 M\s+\[\] 0 d\s+/(GS\d+) gs\s+8 w");
        Match second = Regex.Match(content, @"0 0 1 RG\s+0 J\s+0 j\s+4 M\s+\[\] 0 d\s+/(GS\d+) gs\s+2 w");

        Assert.True(first.Success, "the bottom stroke states its own alpha state before its width");
        Assert.True(second.Success, "the top stroke states its own alpha state before its width");
        Assert.NotEqual(first.Groups[1].Value, second.Groups[1].Value);
    }

    /// <summary>
    /// **An opacity and a colour alpha multiply, rather than one winning.**
    ///
    /// They are different questions - how transparent the paint is, and how much of the stroke is drawn - and a
    /// stroke that asks for both at half strength is a quarter covered. An exporter that used the larger of the
    /// two, or that let the colour's alpha replace the stroke's, would draw this at a half and look plausible.
    /// </summary>
    [Fact]
    public void AStrokeOpacityAndAColourAlphaMultiply()
    {
        StrokeSpec faint = new(true, new ColorRgb(0, 0, 1, 0.5), 4, StrokeCap.Butt, StrokeJoin.Miter, 4)
        {
            Opacity = 0.5,
        };

        string raw = Raw(PdfDocumentExporter.Export(Document(Line(faint))));

        Assert.Contains("/ca 0.25", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("/ca 0.5 ", raw, StringComparison.Ordinal);
    }

    /// <summary>
    /// **An ordinary opaque stroke writes no `gs` at all**, so the change costs nothing on the documents that do
    /// not use it - the no-member-at-its-default rule, asserted on the content stream rather than on the sidecar.
    ///
    /// The assertion is on the **switch**, not on the `/ExtGState` dictionary: the exporter always allocates the
    /// opaque bucket, so a document with no transparency still carries a one-entry resource. What must not happen
    /// is a stroke switching to it, because that would be writing a member the document does not state - and the
    /// switch is what a reader acts on.
    /// </summary>
    [Fact]
    public void AnOpaqueStrokeWritesNoAlphaState()
    {
        byte[] pdf = PdfDocumentExporter.Export(Document(Line(Stroke(5, 0, 0, 0))));

        Assert.DoesNotMatch(@"\d+ gs", Inflate(pdf));
    }

    private static CadDocument Document(PathItem path)
    {
        CadDocument document = CadDocument.CreateDefault();
        document.Artboards[0].Layers[0].AddItem(path);
        return document;
    }

    /// <summary>The file as text, for the parts that are not inside a deflated stream.</summary>
    private static string Raw(byte[] pdf) => System.Text.Encoding.Latin1.GetString(pdf);

    /// <summary>Inflates the content streams so the operators can be read.</summary>
    private static string Inflate(byte[] pdf)
    {
        string latin = System.Text.Encoding.Latin1.GetString(pdf);
        var builder = new System.Text.StringBuilder();

        foreach (Match m in Regex.Matches(latin, @"(?<!end)stream\r?\n"))
        {
            int start = m.Index + m.Length;
            int end = latin.IndexOf("endstream", start, StringComparison.Ordinal);
            if (end < 0)
            {
                break;
            }

            try
            {
                using var input = new MemoryStream(pdf, start, end - start);
                using var zlib = new System.IO.Compression.ZLibStream(
                    input, System.IO.Compression.CompressionMode.Decompress);
                using var reader = new StreamReader(zlib, System.Text.Encoding.UTF8);
                builder.Append(reader.ReadToEnd());
            }
            catch (Exception)
            {
                // Not a Flate stream.
            }
        }

        return builder.ToString();
    }
}
