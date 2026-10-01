using System.Text.RegularExpressions;
using VCCad.App.Controls;
using VCCad.Core.Model;
using VCCad.Geometry;
using VCCad.Pdf;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// **The same outline reaches the PDF as reaches the canvas.**
///
/// Both renderers go through <see cref="StrokeOutlineBuilder"/>, so it is tempting to conclude they agree because
/// they call one function. That is not the property worth testing: each *then* consumes the plan its own way -
/// the canvas adds the artboard's origin because it paints inside the world transform, the exporter passes a
/// scale for the widths and a matrix for the placement - and those two consumptions are where an outline can
/// come out a different shape, or in a different place, from the one the person saw.
///
/// So the assertion is on the geometry the two renderers actually draw: the canvas's loops, and the points the
/// exporter writes into the content stream, compared point for point. The stroke carries an outline effect, a
/// width profile and a dash, because those are the three things a stroke can have that a plain one does not, and
/// a disagreement about any of them has to show up here.
/// </summary>
public class StrokeOutlineAgreementTests
{
    private static PathItem Line()
    {
        var path = new PathItem { Name = "line", Fill = FillSpec.None };
        SubPath sub = path.AddSubPath(closed: false);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(100, 0)));
        return path;
    }

    /// <summary>
    /// An effected, profiled, dashed stroke: the canvas and the PDF must plot the same points.
    ///
    /// The document sits on an artboard at the origin on purpose. Path coordinates are already relative to the
    /// artboard, so on an artboard at (0,0) the canvas's artboard offset is zero and the two coordinate systems
    /// are the same one - which is the only way this test can be a point-for-point comparison rather than a
    /// comparison that quietly tolerates a translation.
    /// </summary>
    [Fact]
    public void TheCanvasAndThePdfPlotTheSameOutlineForAnEffectedDashedProfile()
    {
        CadDocument document = CadDocument.CreateDefault();
        PathItem path = Line();
        document.Artboards[0].Layers[0].AddItem(path);

        path.Stroke = new StrokeSpec(
            true,
            new ColorRgb(0, 0, 0),
            8,
            StrokeCap.Butt,
            StrokeJoin.Miter,
            4,
            StrokeAlignment.Center,
            new DashPattern(new[] { 6.0, 3.0 }, 1.0),
            WidthProfileSpec.Constant(20),
            new EffectStack(new[] { OutlineEffectSpec.Roughen(3, seed: 5) }));

        IReadOnlyList<IReadOnlyList<Point2D>> canvas = CanvasWorkspace.ProfileLoops(path, path.Stroke);
        IReadOnlyList<Point2D> loop = Assert.Single(canvas);

        string content = Inflate(PdfDocumentExporter.Export(document));
        (double X, double Y)[] written = WrittenPoints(content).ToArray();

        // A roughen of three on a twenty-wide band: four points, and the two renderers have to put every one of
        // them in the same place. Asserting the count as well as the coordinates matters - a writer that dropped
        // the loop's last point would otherwise pass on the points it did keep.
        Assert.Equal(loop.Count, written.Length);
        for (int i = 0; i < loop.Count; i++)
        {
            Assert.Equal(loop[i].X, written[i].X, 6);
            Assert.Equal(loop[i].Y, written[i].Y, 6);
        }

        // And the effect reached the geometry rather than being carried and ignored: an unroughened 20-wide band
        // on this line is flat at y = -10 and y = +10, so a point away from both proves the roughen is in the
        // coordinates the canvas draws and the file records.
        Assert.Contains(loop, p => Math.Abs(Math.Abs(p.Y) - 10.0) > 1e-6);

        // The dash is deliberately part of the stroke and deliberately not part of the geometry. Once a stroke is
        // an outline it is a **region**, and dashing a region is not dashing the line it came from - both renderers
        // ignore it, which is why the two still agree. No dash operator reaches the file, and the area above is
        // the same with the dash as without it.
        Assert.DoesNotContain(" d\n", content);
    }

    /// <summary>The points a content stream draws, in order, as (x, y).</summary>
    private static IEnumerable<(double X, double Y)> WrittenPoints(string content)
        => Regex.Matches(content, @"([-\d.]+) ([-\d.]+) [ml]")
            .Select(m => (double.Parse(m.Groups[1].Value), double.Parse(m.Groups[2].Value)));

    /// <summary>
    /// The Flate streams of an export, decompressed, concatenated. The content stream is one of them; the
    /// assertion above is over the points in it, not over the file's bytes.
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
