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
