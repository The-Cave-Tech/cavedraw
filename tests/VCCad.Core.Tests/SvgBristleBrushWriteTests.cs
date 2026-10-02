using System.Globalization;
using System.Xml.Linq;
using VCCad.Core.Model;
using VCCad.Core.Svg;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// **The SVG half of the honouring step of a bristle brush: the bristles reach the file** (issue #103).
///
/// A bristle brush is the one kind whose answer is a set of **strokes** rather than artwork placed somewhere, so it
/// reaches a renderer as the outline those strokes cover. `SvgWriter` writes a stroke natively unless it has a
/// width profile or an outline effect - a bristle brush is neither - so before this the file drew the stroke's own
/// four-point line and none of the bristles, which is the whole picture lost with every round trip still correct.
///
/// The assertion is on the **written bytes**: the `d` the file states is the loops `StrokeOutlineBuilder.Plan`
/// answers with - the same answer the canvas fills and the PDF writer fills - so a writer that drew the line
/// instead, or drew a different bundle, cannot satisfy it. The colour jitter's half is asserted as one `<path>`
/// per bristle with its own `fill`, because an SVG element carries one fill and merging them would paint the
/// bundle the stroke's colour.
/// </summary>
public class SvgBristleBrushWriteTests
{
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";

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

    /// <summary>Every path element of the file that states a fill, which is how a filled outline is written.</summary>
    private static List<XElement> Filled(XDocument svg)
        => svg.Descendants(Svg + "path")
            .Where(e => e.Attribute("fill") is not null && (string?)e.Attribute("fill") != "none")
            .ToList();

    /// <summary>
    /// **The bristles are written as the outline they cover.** The written `d` carries every loop the plan answers
    /// with, including points well off the line the stroke would have drawn - so a writer that wrote the stroke's
    /// own path, or that dropped the bundle, cannot pass.
    /// </summary>
    [Fact]
    public void ABristleBrushWritesTheOutlineItsBristlesCover()
    {
        CadDocument document = CadDocument.CreateDefault();
        var spec = new BristleBrushSpec(Count: 5, Spread: 1.0, Randomness: 0.0, Thickness: 2.0);
        BrushSpec brush = BrushSpec.Bristle("Scrub", 40, spec);
        PathItem path = Line(document, new Point2D(40, 400), new Point2D(300, 400), brush);

        SvgWriteResult result = SvgWriter.WriteResult(document);
        Assert.Empty(result.Missing);

        StrokeRenderPlan plan = StrokeOutlineBuilder.Plan(path, path.Stroke);
        Assert.Equal(5, plan.Outlines.Count);

        List<XElement> filled = Filled(XDocument.Parse(result.Svg));
        XElement bundle = Assert.Single(filled);

        string data = (string)filled[0].Attribute("d")!;
        foreach (Point2D point in plan.Outlines.SelectMany(loop => loop))
        {
            Assert.Contains($"{Num(point.X)} {Num(point.Y)}", data, StringComparison.Ordinal);
        }

        // The bundle is forty across, so the file holds ink well off the four-point line.
        Assert.Contains(plan.Outlines.SelectMany(loop => loop), p => Math.Abs(p.Y - 400.0) > 15.0);
        Assert.Equal("#000000", (string?)bundle.Attribute("fill"));
    }

    /// <summary>
    /// **Without the brush the same document writes the line and none of the bundle.** The bristles are in the file
    /// because the stroke carries the brush, so the same path with no brush has no point from the plan's loops in
    /// its `d`.
    /// </summary>
    [Fact]
    public void AStrokeWithNoBristleBrushWritesNoneOfTheBundle()
    {
        CadDocument document = CadDocument.CreateDefault();
        var spec = new BristleBrushSpec(Count: 5, Spread: 1.0, Randomness: 0.0, Thickness: 2.0);
        BrushSpec brush = BrushSpec.Bristle("Scrub", 40, spec);

        // The plan's answer for the brushed path, which the bare document must not write.
        CadDocument withBrush = CadDocument.CreateDefault();
        PathItem brushed = Line(withBrush, new Point2D(40, 400), new Point2D(300, 400), brush);
        Point2D edge = StrokeOutlineBuilder.Plan(brushed, brushed.Stroke).Outlines
            .SelectMany(loop => loop)
            .OrderByDescending(p => Math.Abs(p.Y - 400.0))
            .First();

        Line(document, new Point2D(40, 400), new Point2D(300, 400), brush: null);
        SvgWriteResult result = SvgWriter.WriteResult(document);
        Assert.Empty(result.Missing);

        string data = (string?)Filled(XDocument.Parse(result.Svg)).SingleOrDefault()?.Attribute("d") ?? string.Empty;
        Assert.DoesNotContain(Num(edge.Y), data, StringComparison.Ordinal);
    }

    /// <summary>
    /// **A colour jitter is written as one path per bristle with its own fill.** Two documents identical but for the
    /// jitter: the jittered one writes a path whose colour is each bristle's own, and the unjittered one writes the
    /// stroke's colour once for the whole bundle. A writer that merged the loops would paint every bristle black,
    /// which is exactly the jitter recorded in the model and dropped at the file.
    /// </summary>
    [Fact]
    public void AColourJitterIsWrittenAsOnePathPerBristleWithItsOwnFill()
    {
        CadDocument document = CadDocument.CreateDefault();
        var spec = new BristleBrushSpec(
            Count: 4, Spread: 1.0, Randomness: 0.5, Thickness: 3.0, Stiffness: 1.0, ColourJitter: 0.7);
        BrushSpec brush = BrushSpec.Bristle("Scrub", 40, spec);
        PathItem path = Line(document, new Point2D(40, 400), new Point2D(300, 400), brush);

        SvgWriteResult result = SvgWriter.WriteResult(document);
        Assert.Empty(result.Missing);

        StrokeRenderPlan plan = StrokeOutlineBuilder.Plan(path, path.Stroke);
        IReadOnlyList<ColorRgb> paints = plan.Paints
            ?? throw new InvalidOperationException("a colour jitter has to reach the plan as a paint per bristle");

        List<XElement> filled = Filled(XDocument.Parse(result.Svg));
        Assert.Equal(paints.Count, filled.Count);

        // The bristles really do differ, so there is something for the file to write.
        Assert.True(paints.Select(p => Math.Round(p.R, 6)).Distinct().Count() > 1);

        foreach (ColorRgb paint in paints)
        {
            Assert.Contains(filled, element => (string?)element.Attribute("fill") == Hex(paint));
        }

        // The same brush with no jitter writes one path for the whole bundle, in the stroke's own colour.
        CadDocument held = CadDocument.CreateDefault();
        Line(
            held, new Point2D(40, 400), new Point2D(300, 400),
            BrushSpec.Bristle("Scrub", 40, spec with { ColourJitter = 0.0 }));

        SvgWriteResult plain = SvgWriter.WriteResult(held);
        Assert.Empty(plain.Missing);

        List<XElement> oneBundle = Filled(XDocument.Parse(plain.Svg));
        Assert.Single(oneBundle);
        Assert.Equal("#000000", (string?)oneBundle[0].Attribute("fill"));
    }

    /// <summary>
    /// **A `currentColor` stroke keeps the keyword when the brush does not jitter, and the jittered case says why
    /// it cannot.** Each bristle's fill is a shade of the stroke's colour resolved in the model
    /// (`StrokeOutlineBuilder`), so no element can carry the keyword: the colours round-trip exactly and only their
    /// provenance does not. That is a loss to declare, not a defect to hide - `SvgWriteResult.Missing` is the same
    /// honest boundary the class comment describes, and this is its first use for a brush.
    /// </summary>
    [Fact]
    public void AColourJitterCannotKeepCurrentColorAndDeclaresIt()
    {
        var spec = new BristleBrushSpec(
            Count: 4, Spread: 1.0, Randomness: 0.5, Thickness: 3.0, Stiffness: 1.0, ColourJitter: 0.0);

        // The control: no jitter, so one path carries the whole bundle and the keyword survives on it.
        CadDocument held = CadDocument.CreateDefault();
        PathItem plain = Line(
            held, new Point2D(40, 400), new Point2D(300, 400),
            BrushSpec.Bristle("Scrub", 40, spec));
        plain.Stroke = plain.Stroke with { FromCurrentColor = true, Color = ColorRgb.Black };

        SvgWriteResult plainResult = SvgWriter.WriteResult(held);
        Assert.Contains("currentColor", plainResult.Svg, StringComparison.Ordinal);
        Assert.DoesNotContain(
            plainResult.Missing, reason => reason.Contains("currentColor", StringComparison.Ordinal));

        // The jitter: the keyword cannot be written for any bristle, and the writer names the loss.
        CadDocument jittered = CadDocument.CreateDefault();
        PathItem stitched = Line(
            jittered, new Point2D(40, 400), new Point2D(300, 400),
            BrushSpec.Bristle("Scrub", 40, spec with { ColourJitter = 0.7 }));
        stitched.Stroke = stitched.Stroke with { Color = ColorRgb.Black, FromCurrentColor = true };

        SvgWriteResult jitteredResult = SvgWriter.WriteResult(jittered);
        Assert.DoesNotContain("currentColor", jitteredResult.Svg, StringComparison.Ordinal);
        Assert.Contains(
            jitteredResult.Missing, reason => reason.Contains("currentColor", StringComparison.Ordinal));
    }

    /// <summary>The file's own number, so a coordinate is searched for exactly as it is written. Mirrored from the writer.</summary>
    private static string Num(double value)
        => value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>The file's own hex, so a colour is searched for exactly as it is written. Mirrored from the writer.</summary>
    private static string Hex(ColorRgb colour)
        => $"#{Byte(colour.R):x2}{Byte(colour.G):x2}{Byte(colour.B):x2}";

    private static int Byte(double value)
        => (int)Math.Round(Math.Clamp(value, 0.0, 1.0) * 255.0);
}
