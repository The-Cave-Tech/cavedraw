using System.Text.RegularExpressions;
using VCCad.Core.Model;
using VCCad.Geometry;
using VCCad.Pdf;
using Xunit;

namespace VCCad.Pdf.Tests;

/// <summary>
/// A hatch reaches the file as the line art the canvas draws.
///
/// A PDF has no need to carry a hatch as a native pattern: the lines clipped to the path are what the file
/// should contain. So the assertion is not "something was written" but "the same segments were written" - the
/// exporter and the renderer share one clipping implementation, and this is what says so.
/// </summary>
public class HatchExportTests
{
    private static byte[] Export(PathItem path)
    {
        CadDocument document = CadDocument.CreateDefault();
        document.Artboards[0].Layers[0].AddItem(path);
        return PdfDocumentExporter.Export(document);
    }

    private static PathItem Hatched(HatchSpec hatch, out int expectedSegments)
    {
        var path = new PathItem { Name = "hatched", Fill = FillSpec.WithHatch(hatch) };
        SubPath sub = path.AddSubPath(closed: true);
        sub.Nodes.Add(new PathNode(new Point2D(20, 20)));
        sub.Nodes.Add(new PathNode(new Point2D(200, 20)));
        sub.Nodes.Add(new PathNode(new Point2D(200, 160)));
        sub.Nodes.Add(new PathNode(new Point2D(20, 160)));

        expectedSegments = HatchGenerator.Segments(
            hatch, PathFlattener.Flatten(path), path.Fill.Rule, path.BoundingBox()).Count;
        return path;
    }

    [Fact]
    public void TheHatchIsWrittenAsClippedLines()
    {
        PathItem path = Hatched(HatchSpec.Single(0, 20), out int expected);
        Assert.True(expected > 0, "the sample hatch has lines");

        string content = Inflate(Export(path));

        // Every line is a move and a line-to, and they are stroked.
        MatchCollection lines = Regex.Matches(content, @"(?m)^[\d.-]+ [\d.-]+ m [\d.-]+ [\d.-]+ l$");
        Assert.Equal(expected, lines.Count);

        // Stroked, not filled, and inside a clip - the shape is the clip, which is what keeps the hatch off the
        // parts of the page the object does not cover.
        Assert.Contains("S", content);
        Assert.Matches(@"(?<![A-Za-z])W\*?(?![A-Za-z])", content);
    }

    /// <summary>A path with no hatch writes no line art, so an ordinary export is unchanged.</summary>
    [Fact]
    public void APathWithNoHatchWritesNoLineArt()
    {
        var path = new PathItem { Name = "plain", Fill = FillSpec.Solid(ColorRgb.Black) };
        SubPath sub = path.AddSubPath(closed: true);
        sub.Nodes.Add(new PathNode(new Point2D(20, 20)));
        sub.Nodes.Add(new PathNode(new Point2D(200, 20)));
        sub.Nodes.Add(new PathNode(new Point2D(200, 160)));
        sub.Nodes.Add(new PathNode(new Point2D(20, 160)));

        string content = Inflate(Export(path));
        Assert.DoesNotMatch(@"(?m)^[\d.-]+ [\d.-]+ m [\d.-]+ [\d.-]+ l$", content);
    }

    /// <summary>
    /// Inflates the content streams so the operators can be read. The indexes come from the Latin-1 view of the
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