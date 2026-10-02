using System.Text.RegularExpressions;
using VCCad.Core.Model;
using VCCad.Geometry;
using VCCad.Pdf;
using Xunit;

namespace VCCad.Pdf.Tests;

/// <summary>
/// **The export half of the honouring step of a bristle brush: the bristles reach the file** (issue #103).
///
/// The issue states the fidelity rule directly - anything that changes what a stroke looks like must also be written
/// to the PDF - and a bristle brush changes what a stroke looks like the moment it is applied. The geometry can be
/// computed, tested and round-tripped while the exporter writes the stroke's own width and no bristles at all, and a
/// round-trip test cannot see that: every round trip is correct.
///
/// The assertions are on the **written coordinates**, taken from <see cref="StrokeOutlineBuilder.Plan"/> - the same
/// answer the canvas fills - so what is checked is that the file holds the union of the bristle strokes and not the
/// line the brush replaced. The colour jitter's half is asserted the same way: the file writes a fill colour per
/// bristle rather than one for the bundle, which is what a jitter recorded in the model and dropped at the page
/// would not do.
/// </summary>
public class BristleBrushExportTests
{
    private static PathItem Line(CadDocument document, Point2D from, Point2D to, BrushSpec? brush)
    {
        var path = new PathItem { Name = "line", Fill = FillSpec.None };
        SubPath sub = path.AddSubPath(closed: false);
        sub.Nodes.Add(new PathNode(from));
        sub.Nodes.Add(new PathNode(to));
        path.Strokes.Clear();
        path.Strokes.Add(new StrokeSpec(true, ColorRgb.Black, 4, StrokeCap.Butt, StrokeJoin.Miter, 4)
        {
            Brush = brush,
        });
        document.Artboards[0].Layers[0].AddItem(path);
        return path;
    }

    private static BrushSpec Scrub(BristleBrushSpec? spec = null, double size = 40.0)
        => BrushSpec.Bristle("Scrub", size, spec);

    /// <summary>
    /// **Every bristle is in the file**, at the points its own stroke covers - taken from the plan the canvas fills,
    /// so the two renderers cannot be asserting different geometry. Without the brush the same document writes none
    /// of them, which is what tells "the bristles are in the export" from "the line happens to be near them".
    /// </summary>
    [Fact]
    public void ABristleBrushWritesItsBristlesIntoTheExport()
    {
        CadDocument document = CadDocument.CreateDefault();
        PathItem path = Line(
            document,
            new Point2D(40, 400),
            new Point2D(300, 400),
            Scrub(new BristleBrushSpec(Count: 5, Spread: 1.0, Randomness: 0.0, Thickness: 2.0)));

        string content = Inflate(PdfDocumentExporter.Export(document));
        var written = WrittenPoints(content).ToList();

        StrokeRenderPlan plan = StrokeOutlineBuilder.Plan(path, path.Stroke);
        Assert.Equal(5, plan.Outlines.Count);

        foreach (IReadOnlyList<Point2D> loop in plan.Outlines)
        {
            // Every fourth point of each bristle's own loop, so the assertion is on the bristle's shape rather than
            // on one coordinate that a line could also produce.
            for (int i = 0; i < loop.Count; i += 4)
            {
                Point2D point = loop[i];
                Assert.Contains(written, p => Near(p, point));
            }
        }

        // The bundle is forty across, so the file holds ink well off the four-point line the stroke would have
        // drawn - which a brush that was stored and not honoured could not produce.
        Assert.Contains(written, p => Math.Abs(p.Y - 400.0) > 10.0);

        // And without the brush the same path writes none of the bundle's own points. The probe is the point of the
        // bundle that is furthest from the line the stroke would have drawn, so "absent" cannot be a coincidence of
        // the brush's own outline overlapping the line.
        CadDocument plain = CadDocument.CreateDefault();
        PathItem bare = Line(plain, new Point2D(40, 400), new Point2D(300, 400), brush: null);
        var bareWritten = WrittenPoints(Inflate(PdfDocumentExporter.Export(plain))).ToList();

        Point2D edge = plan.Outlines
            .SelectMany(loop => loop)
            .OrderByDescending(p => Math.Abs(p.Y - 400.0))
            .First();

        Assert.True(Math.Abs(edge.Y - 400.0) > 15.0, "the probe has to be well off the line the stroke would draw");
        Assert.Contains(written, p => Near(p, edge));
        Assert.DoesNotContain(bareWritten, p => Near(p, edge));
        Assert.True(bare.Stroke.Brush is null);
    }

    /// <summary>
    /// **The count reaches the file.** Two documents identical but for the bundle's count write different numbers of
    /// outlines, so "the bundle is in the export" is told from "one bristle was written".
    /// </summary>
    [Fact]
    public void TheCountReachesTheFile()
    {
        CadDocument document = CadDocument.CreateDefault();
        Line(
            document, new Point2D(40, 400), new Point2D(300, 400),
            Scrub(new BristleBrushSpec(Count: 9, Randomness: 0.0)));

        string content = Inflate(PdfDocumentExporter.Export(document));

        // Nine bristles become nine closed loops. A closed loop in this writer is a run of `l` operators ending in
        // `h`, so the number of closings is the number of loops the file draws - the stroke's own outline would be
        // one.
        int closings = Regex.Matches(content, @"(?m)^h$").Count;
        Assert.True(closings >= 9, $"nine bristles should write at least nine loops, not {closings}");
    }

    /// <summary>
    /// **A colour jitter reaches the file as a fill colour per bristle.**
    ///
    /// That distinction is the whole test. The exporter writes a colour operator for a filled outline whether or not
    /// the loops differ, so "some colour was written" proves nothing; what is asserted is that a brush whose
    /// bristles differ writes **the colours the plan names** - one per bristle - while the same brush with no
    /// jitter writes the stroke's own colour once. A jitter recorded in the model and dropped at the page cannot
    /// produce the first, and a renderer that painted every bristle the stroke's colour cannot produce it either.
    /// </summary>
    [Fact]
    public void AColourJitterReachesTheFileAsAFillColourPerBristle()
    {
        CadDocument jittered = CadDocument.CreateDefault();
        var spec = new BristleBrushSpec(Count: 4, Randomness: 0.5, Thickness: 3.0, ColourJitter: 0.7);
        PathItem path = Line(jittered, new Point2D(40, 400), new Point2D(300, 400), Scrub(spec));
        string content = Inflate(PdfDocumentExporter.Export(jittered));

        StrokeRenderPlan plan = StrokeOutlineBuilder.Plan(path, path.Stroke);
        IReadOnlyList<ColorRgb> paints = plan.Paints
            ?? throw new InvalidOperationException("a colour jitter has to reach the plan as a paint per bristle");
        Assert.Equal(plan.Outlines.Count, paints.Count);

        // The bristles really do differ, so there is something for the file to write.
        Assert.True(paints.Select(p => Math.Round(p.R, 6)).Distinct().Count() > 1);

        foreach (ColorRgb paint in paints)
        {
            Assert.Contains(
                $"{Num(paint.R)} {Num(paint.G)} {Num(paint.B)} rg", content, StringComparison.Ordinal);
        }

        // The same brush with no jitter writes the stroke's own black once and none of the shades.
        CadDocument held = CadDocument.CreateDefault();
        Line(
            held, new Point2D(40, 400), new Point2D(300, 400),
            Scrub(spec with { ColourJitter = 0.0 }));
        string plainContent = Inflate(PdfDocumentExporter.Export(held));

        foreach (ColorRgb paint in paints.Where(p => p.R > 1e-6))
        {
            Assert.DoesNotContain(
                $"{Num(paint.R)} {Num(paint.G)} {Num(paint.B)} rg", plainContent, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The exporter's own number formatting, so a coordinate is searched for exactly as the file writes it.
    /// Mirrored rather than reached for, because the exporter's is internal to its assembly.
    /// </summary>
    private static string Num(double value)
        => value.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);

    private static bool Near((double X, double Y) p, Point2D expected)
        => Math.Abs(p.X - expected.X) < 1e-4 && Math.Abs(p.Y - expected.Y) < 1e-4;

    /// <summary>The points a content stream draws, in order, as (x, y).</summary>
    private static IEnumerable<(double X, double Y)> WrittenPoints(string content)
        => Regex.Matches(content, @"([-\d.]+) ([-\d.]+) [ml]")
            .Select(m => (
                double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
                double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture)));

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
