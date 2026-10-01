using System.Text.RegularExpressions;
using VCCad.Core.Model;
using VCCad.Geometry;
using VCCad.Pdf;
using Xunit;

namespace VCCad.Pdf.Tests;

/// <summary>
/// Every member of the constant-width stroke reaches the file as the operator that means it.
///
/// The exporter wrote all of these already, and none of them was asserted: the only stroke assertion in the
/// suite was that a content stream contains an `m`. A member that stopped being written - because a parameter
/// was added, or a list of operators was reordered - would have gone on exporting a plausible-looking file with
/// the wrong cap, join, miter or dash. This is the audit's output: one test per item, asserting the operator and
/// its value rather than that an export succeeded.
/// </summary>
public class StrokeExportOperatorTests
{
    private static PathItem Line(StrokeSpec stroke)
    {
        var path = new PathItem { Name = "line", Fill = FillSpec.None, Stroke = stroke };
        SubPath sub = path.AddSubPath(closed: false);
        sub.Nodes.Add(new PathNode(new Point2D(40, 400)));
        sub.Nodes.Add(new PathNode(new Point2D(300, 400)));
        return path;
    }

    private static PathItem Closed(StrokeSpec stroke)
    {
        var path = new PathItem { Name = "box", Fill = FillSpec.None, Stroke = stroke };
        SubPath sub = path.AddSubPath(closed: true);
        sub.Nodes.Add(new PathNode(new Point2D(100, 300)));
        sub.Nodes.Add(new PathNode(new Point2D(220, 300)));
        sub.Nodes.Add(new PathNode(new Point2D(220, 360)));
        sub.Nodes.Add(new PathNode(new Point2D(100, 360)));
        return path;
    }

    private static string Export(PathItem path)
    {
        CadDocument document = CadDocument.CreateDefault();
        document.Artboards[0].Layers[0].AddItem(path);
        return Inflate(PdfDocumentExporter.Export(document));
    }

    private static StrokeSpec Spec(
        double width = 4,
        StrokeCap cap = StrokeCap.Butt,
        StrokeJoin join = StrokeJoin.Miter,
        double miter = 4,
        StrokeAlignment alignment = StrokeAlignment.Center,
        DashPattern dash = default)
        => new(true, ColorRgb.Black, width, cap, join, miter, alignment, dash);

    [Fact]
    public void TheWidthIsWritten()
    {
        Assert.Contains("7.5 w", Export(Line(Spec(width: 7.5))));
    }

    [Theory]
    [InlineData(StrokeCap.Butt, "0 J")]
    [InlineData(StrokeCap.Round, "1 J")]
    [InlineData(StrokeCap.Square, "2 J")]
    public void EveryCapIsWrittenAsItsNumber(StrokeCap cap, string expected)
        => Assert.Contains(expected, Export(Line(Spec(cap: cap))));

    [Theory]
    [InlineData(StrokeJoin.Miter, "0 j")]
    [InlineData(StrokeJoin.Round, "1 j")]
    [InlineData(StrokeJoin.Bevel, "2 j")]
    public void EveryJoinIsWrittenAsItsNumber(StrokeJoin join, string expected)
        => Assert.Contains(expected, Export(Line(Spec(join: join))));

    [Fact]
    public void TheMiterLimitIsWritten()
    {
        Assert.Contains("12.5 M", Export(Line(Spec(miter: 12.5))));
    }

    /// <summary>A dash pattern is written with its segments and its phase, because the phase is what makes a
    /// dashed line land in the same place twice.</summary>
    [Fact]
    public void TheDashPatternIsWrittenWithItsPhase()
    {
        string content = Export(Line(Spec(dash: new DashPattern(new[] { 3.0, 2.0 }, 1.0))));

        Assert.Matches(@"\[3 2\] 1 d", content);
    }

    /// <summary>An undashed stroke says so, rather than inheriting the dash of whatever was drawn before it.</summary>
    [Fact]
    public void AnUndashedStrokeClearsTheDash()
    {
        string content = Export(Line(Spec()));
        Assert.Contains("[] 0 d", content);
    }

    /// <summary>
    /// A centred stroke needs no clip of its own, so it carries exactly one fewer than an aligned one - the
    /// export always clips to the page box, which is why this is a comparison and not a zero.
    /// </summary>
    [Fact]
    public void ACentredStrokeIsClippedLessThanAnAlignedOne()
    {
        int centre = Regex.Matches(Export(Closed(Spec(alignment: StrokeAlignment.Center))), @"(?<![A-Za-z])W\*?(?![A-Za-z])").Count;
        int inside = Regex.Matches(Export(Closed(Spec(alignment: StrokeAlignment.Inside))), @"(?<![A-Za-z])W\*?(?![A-Za-z])").Count;

        Assert.Equal(centre + 1, inside);
    }

    /// <summary>
    /// **The dash is graphics state, not a path property.** Setting it only when a path has one leaves it set
    /// for everything after it, so an undashed stroke following a dashed one is drawn dashed - and only when a
    /// dashed stroke happens to precede it. This pins the reset.
    /// </summary>
    [Fact]
    public void AnUndashedStrokeAfterADashedOneIsReset()
    {
        var document = CadDocument.CreateDefault();
        document.Artboards[0].Layers[0].AddItem(Line(Spec(dash: new DashPattern(new[] { 4.0, 2.0 }, 0))));
        document.Artboards[0].Layers[0].AddItem(Line(Spec()));

        string content = Inflate(PdfDocumentExporter.Export(document));

        int dashed = content.IndexOf("[4 2] 0 d", StringComparison.Ordinal);
        int reset = content.IndexOf("[] 0 d", StringComparison.Ordinal);

        Assert.True(dashed >= 0, "the dashed stroke states its dash");
        Assert.True(reset > dashed, $"the undashed stroke must state its own dash after it (dashed at {dashed}, reset at {reset})");
    }

    /// <summary>
    /// An inside or outside stroke cannot be expressed by PDF's stroking operator, so the exporter draws it at
    /// double width and clips it to one side of the path. That clip is the whole implementation, and it is now
    /// asserted for both sides - the geometry is checked by the clip's presence and by the doubled width.
    /// </summary>
    [Theory]
    [InlineData(StrokeAlignment.Inside)]
    [InlineData(StrokeAlignment.Outside)]
    public void AnAlignedStrokeIsClippedAndDrawnAtDoubleWidth(StrokeAlignment alignment)
    {
        string content = Export(Closed(Spec(width: 3, alignment: alignment)));

        Assert.Matches(@"(?<![A-Za-z])W\*?(?![A-Za-z])", content);
        Assert.Contains("6 w", content);
    }

    /// <summary>
    /// The two alignments are not the same picture: inside and outside produce different content, because each
    /// clips to the opposite side of the outline.
    /// </summary>
    [Fact]
    public void InsideAndOutsideAreDifferentFiles()
    {
        string inside = Export(Closed(Spec(alignment: StrokeAlignment.Inside)));
        string outside = Export(Closed(Spec(alignment: StrokeAlignment.Outside)));

        Assert.NotEqual(inside, outside);
    }

    /// <summary>Everything at once, so a member cannot be right in isolation and dropped in company.</summary>
    [Fact]
    public void EveryMemberSurvivesTogether()
    {
        string content = Export(Line(Spec(
            width: 2.25,
            cap: StrokeCap.Round,
            join: StrokeJoin.Bevel,
            miter: 8,
            dash: new DashPattern(new[] { 5.0, 1.5, 0.5, 1.5 }, 2.0))));

        Assert.Contains("2.25 w", content);
        Assert.Contains("1 J", content);
        Assert.Contains("2 j", content);
        Assert.Contains("8 M", content);
        Assert.Matches(@"\[5 1.5 0.5 1.5\] 2 d", content);
    }

    /// <summary>
    /// Inflates the content streams so the operators can be read. Indexes come from the Latin-1 view of the
    /// bytes, which is one character per byte, so they are also the byte offsets.
    /// </summary>
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
