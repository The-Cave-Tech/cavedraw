using System.Text.RegularExpressions;
using VCCad.Core.Model;
using VCCad.Geometry;
using VCCad.Pdf;
using Xunit;

namespace VCCad.Pdf.Tests;

/// <summary>
/// A variable-width stroke is written as the region it covers, filled.
///
/// PDF has one width per stroke, so there is no variable-width stroke to write. The assertion that matters is
/// therefore not "the profile appears in the file" - it cannot - but that the stroke is written as a **filled**
/// region in the stroke's colour, with the stroke's own width nowhere in it.
/// </summary>
public class WidthProfileExportTests
{
    private static PathItem Line(WidthProfileSpec? profile)
    {
        var path = new PathItem { Name = "line", Fill = FillSpec.None };
        SubPath sub = path.AddSubPath(closed: false);
        sub.Nodes.Add(new PathNode(new Point2D(40, 400)));
        sub.Nodes.Add(new PathNode(new Point2D(300, 400)));
        path.Stroke = new StrokeSpec(true, new ColorRgb(1, 0, 0), 8,
            StrokeCap.Butt, StrokeJoin.Miter, 4, StrokeAlignment.Center, default, profile);
        return path;
    }

    private static string Export(PathItem path)
    {
        CadDocument document = CadDocument.CreateDefault();
        document.Artboards[0].Layers[0].AddItem(path);
        return Inflate(PdfDocumentExporter.Export(document));
    }

    /// <summary>A taper is filled, in the stroke's colour, and is not stroked at its nominal width.</summary>
    [Fact]
    public void AProfileIsWrittenAsAFilledOutlineNotAStroke()
    {
        string content = Export(Line(WidthProfileSpec.Taper(20, 0)));

        // The colour is stated as a **fill** operator, because the outline is filled.
        Assert.Contains("1 0 0 rg", content);
        Assert.Contains("\nf\n", content);

        // And the ordinary stroke path is not taken: no stroke colour operator, and no stroke of the nominal
        // width 8 - which is what a reader that ignored the profile would have written.
        Assert.DoesNotContain("1 0 0 RG", content);
        Assert.DoesNotContain("8 w", content);
    }

    /// <summary>
    /// The outline is the region the stroke covers, so its extent is the widest part of the profile and not the
    /// stroke's own width. A 20-wide taper is 20 across at its fat end, ten either side of the centreline.
    ///
    /// Asserted as the **exact set of points** rather than by looking for a coordinate somewhere in the stream.
    /// The looser version of this test passed while the outline was somewhere else entirely, because a content
    /// stream contains numbers from more than one source and one of them happened to match.
    /// </summary>
    [Fact]
    public void TheOutlineIsAsWideAsTheProfile()
    {
        string content = Export(Line(WidthProfileSpec.Taper(20, 0)));

        // The path runs from (40,400) to (300,400). The taper is 20 across at that end and closes to nothing at
        // the far end, so the four points are the band's corners.
        Assert.Equal(
            new[] { (40.0, 390.0), (300.0, 400.0), (300.0, 400.0), (40.0, 410.0) },
            WrittenPoints(content).ToArray());
    }

    /// <summary>The points a content stream draws, in order, as (x, y).</summary>
    private static IEnumerable<(double X, double Y)> WrittenPoints(string content)
        => Regex.Matches(content, @"([-\d.]+) ([-\d.]+) [ml]")
            .Select(m => (double.Parse(m.Groups[1].Value), double.Parse(m.Groups[2].Value)));

    /// <summary>An ordinary stroke is written exactly as before: the profile is additive, not a replacement.</summary>
    [Fact]
    public void AStrokeWithoutAProfileIsStillStroked()
    {
        string content = Export(Line(profile: null));

        Assert.Contains("1 0 0 RG", content);
        Assert.Contains("8 w", content);
        Assert.DoesNotContain("1 0 0 rg", content);
    }

    /// <summary>An empty profile says nothing about width, so it is an ordinary stroke rather than a blank one.</summary>
    [Fact]
    public void AnEmptyProfileIsAnOrdinaryStroke()
    {
        string content = Export(Line(new WidthProfileSpec("Empty", Array.Empty<WidthPoint>())));

        Assert.Contains("1 0 0 RG", content);
        Assert.Contains("8 w", content);
    }

    /// <summary>
    /// **The same outline reaches the PDF as reaches the canvas.** The exported points are the shared builder's,
    /// in the same coordinates - so the two renderers are drawing one geometry rather than two that agree by
    /// luck. The assertion is over the whole set, so a point from anywhere else in the stream cannot satisfy it.
    /// </summary>
    [Fact]
    public void TheWrittenOutlineIsTheSharedBuildersGeometry()
    {
        PathItem path = Line(WidthProfileSpec.Constant(10));
        string content = Export(path);

        IReadOnlyList<Point2D> shared = StrokeOutlineBuilder.Outline(path, path.Stroke)[0];
        var written = WrittenPoints(content).ToArray();

        Assert.Equal(shared.Count, written.Length);
        for (int i = 0; i < shared.Count; i++)
        {
            Assert.Equal(shared[i].X, written[i].X, 6);
            Assert.Equal(shared[i].Y, written[i].Y, 6);
        }
    }

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
