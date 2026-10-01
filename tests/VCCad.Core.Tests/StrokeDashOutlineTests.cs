using System.Text.RegularExpressions;
using VCCad.Core.Model;
using VCCad.Core.Svg;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// A dash that survives the stroke becoming an outline.
///
/// The dash is part of the **stroke**, so when a width profile or an outline effect turns that stroke into the
/// region it covers, the region is the dashes - not the whole line with the gaps quietly filled in. Every
/// assertion here is on points, because "the dash was considered" is not a test: an outline that mentions the
/// dash and draws a solid band is exactly the defect this is here to catch.
/// </summary>
public class StrokeDashOutlineTests
{
    /// <summary>An open line along +X, which makes the arc length of a point its x coordinate.</summary>
    private static PathItem Line(double length)
    {
        var path = new PathItem { Name = "line", Fill = FillSpec.None };
        SubPath sub = path.AddSubPath(closed: false);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(length, 0)));
        return path;
    }

    /// <summary>A closed square, so the dash has to run round the seam as well as along the edges.</summary>
    private static PathItem Square(double side)
    {
        var path = new PathItem { Name = "square", Fill = FillSpec.None };
        SubPath sub = path.AddSubPath(closed: true);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(side, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(side, side)));
        sub.Nodes.Add(new PathNode(new Point2D(0, side)));
        return path;
    }

    private static StrokeSpec Spec(
        double width,
        DashPattern dash = default,
        StrokeCap cap = StrokeCap.Butt,
        WidthProfileSpec? profile = null,
        EffectStack? effects = null)
        => new(true, ColorRgb.Black, width, cap, StrokeJoin.Miter, 4,
            StrokeAlignment.Center, dash, profile, effects);

    /// <summary>Where a point sits inside the 6-on/3-off pattern of a line, as a distance into its period.</summary>
    private static double IntoPeriod(double x) => x - (Math.Floor(x / 9.0) * 9.0);

    /// <summary>
    /// **The outline of a dashed stroke is its dashes.** With a width profile the stroke has to be drawn as the
    /// region it covers; adding a dash must cut that region into the dashes and leave the gaps empty, rather than
    /// drawing one solid band. The extents are asserted exactly, because "several loops" would also be true of a
    /// dash drawn in the wrong place.
    /// </summary>
    [Fact]
    public void ADashCutsTheOutlineIntoTheDashesItWouldDraw()
    {
        PathItem path = Line(100);
        StrokeSpec solid = Spec(8, profile: WidthProfileSpec.Constant(8));
        StrokeSpec dashed = solid with { Dash = new DashPattern(new[] { 6.0, 3.0 }) };

        IReadOnlyList<IReadOnlyList<Point2D>> whole = StrokeOutlineBuilder.Outline(path, solid);
        IReadOnlyList<IReadOnlyList<Point2D>> dashes = StrokeOutlineBuilder.Outline(path, dashed);

        // Undashed: one band the length of the line...
        IReadOnlyList<Point2D> band = Assert.Single(whole);
        Assert.Equal(0.0, band.Min(p => p.X), 6);
        Assert.Equal(100.0, band.Max(p => p.X), 6);

        // ...and dashed: the twelve dashes a 6-on/3-off pattern leaves, each a band of its own.
        Assert.Equal(12, dashes.Count);
        for (int i = 0; i < dashes.Count; i++)
        {
            double from = i * 9.0;
            double to = Math.Min(from + 6.0, 100.0);

            Assert.Equal(from, dashes[i].Min(p => p.X), 6);
            Assert.Equal(to, dashes[i].Max(p => p.X), 6);
            Assert.Equal(-4.0, dashes[i].Min(p => p.Y), 6);
            Assert.Equal(4.0, dashes[i].Max(p => p.Y), 6);
        }
    }

    /// <summary>
    /// The gaps are gaps: no point of the outline falls inside an off interval, and no dash spans more than the
    /// ink it stands for. The extents alone cannot say this - a band drawn solid covers the dashes and the gaps
    /// alike - and the point test alone cannot either, because a solid band's points are all at its two ends.
    /// </summary>
    [Fact]
    public void TheGapsBetweenTheDashesCarryNoInk()
    {
        PathItem path = Line(100);
        StrokeSpec stroke = Spec(8, new DashPattern(new[] { 6.0, 3.0 }), profile: WidthProfileSpec.Constant(8));

        IReadOnlyList<IReadOnlyList<Point2D>> dashes = StrokeOutlineBuilder.Outline(path, stroke);

        Assert.All(dashes, loop => Assert.True(
            loop.Max(p => p.X) - loop.Min(p => p.X) <= 6.0 + 1e-6,
            $"a dash cannot be longer than the ink it stands for: {loop.Max(p => p.X) - loop.Min(p => p.X)}"));

        Assert.DoesNotContain(
            dashes.SelectMany(loop => loop),
            p => IntoPeriod(p.X) > 6.0 + 1e-6 && IntoPeriod(p.X) < 9.0 - 1e-6);
    }

    /// <summary>
    /// **A taper is measured along the whole path, not restarted at each dash.** Handing every dash the whole
    /// profile unchanged would draw each one from the fat end, so a stroke that tapers to nothing would still be
    /// ten points wide in its last dash - a wrong answer that looks entirely plausible on a stroke of constant
    /// width, which is why the profile here actually varies.
    /// </summary>
    [Fact]
    public void AProfileIsMeasuredAlongTheWholePathRatherThanRestartedAtEachDash()
    {
        PathItem path = Line(100);
        StrokeSpec stroke = Spec(8, new DashPattern(new[] { 6.0, 3.0 }), profile: WidthProfileSpec.Taper(20, 0));

        IReadOnlyList<IReadOnlyList<Point2D>> dashes = StrokeOutlineBuilder.Outline(path, stroke);

        Assert.Equal(12, dashes.Count);

        // The first dash is at the fat end: the profile is 20 across at t = 0, ten either side.
        Assert.Equal(10.0, dashes[0].Max(p => p.Y), 6);

        // The last dash sits at x = 99..100, where a taper of 20 to nothing is all but closed. A profile that
        // restarted at each dash would have it as wide as the first one.
        Assert.True(
            dashes[^1].Max(p => p.Y) < 0.05,
            $"the last dash must carry the profile at the end of the path, not its fat end: " +
            $"{dashes[^1].Max(p => p.Y)}");
    }

    /// <summary>
    /// **A dash ends in the stroke's cap.** Dashing a path makes two ends per dash where there was one end per
    /// path, and a stroke ends in its cap, so a dashed round-capped line drawn with square ends is the dash right
    /// and the stroke it came from wrong. The extents pin all three caps exactly.
    /// </summary>
    [Theory]
    [InlineData(StrokeCap.Butt, 0.0, 20.0)]
    [InlineData(StrokeCap.Square, -4.0, 24.0)]
    [InlineData(StrokeCap.Round, -4.0, 24.0)]
    public void ADashEndsInTheStrokesCap(StrokeCap cap, double from, double to)
    {
        PathItem path = Line(100);
        StrokeSpec stroke = Spec(
            8,
            new DashPattern(new[] { 20.0, 10.0 }),
            cap,
            WidthProfileSpec.Constant(8));

        IReadOnlyList<Point2D> first = StrokeOutlineBuilder.Outline(path, stroke)[0];

        Assert.Equal(from, first.Min(p => p.X), 6);
        Assert.Equal(to, first.Max(p => p.X), 6);

        // A round cap reaches the same distance as a square one but is curved, so the point furthest along the
        // line is on the end's axis; a square cap's is on a corner. Without this the two would be
        // indistinguishable by extent alone.
        Point2D furthest = first.OrderByDescending(p => p.X).First();
        Assert.Equal(cap == StrokeCap.Round ? 0.0 : 4.0, Math.Abs(furthest.Y), 6);
    }

    /// <summary>
    /// **A dash that spans a closed subpath's seam is one dash.** The walk starts and ends at the same point, so
    /// the piece before the seam and the piece after it are continuous and have to be joined: drawn as two, the
    /// dash grows a pair of caps in its middle.
    /// </summary>
    [Fact]
    public void ADashThatSpansAClosedSubpathsSeamIsOneDash()
    {
        PathItem path = Square(100);
        StrokeSpec stroke = Spec(
            8,
            new DashPattern(new[] { 30.0, 13.0 }, offset: 5.0),
            profile: WidthProfileSpec.Constant(8));

        IReadOnlyList<IReadOnlyList<Point2D>> dashes = StrokeOutlineBuilder.Outline(path, stroke);

        // Nine dashes round a 400-long perimeter with a 43-long period - and the one wrapping the seam counted
        // once rather than twice.
        Assert.Equal(9, dashes.Count);

        // The wrapping dash is the only one that runs along the last edge and then along the first: the others
        // cross a corner, or run along one edge only.
        int wrapping = dashes.Count(loop =>
            loop.Any(p => Math.Abs(p.X) < 5.0 && p.Y > 10.0 && p.Y < 90.0) &&
            loop.Any(p => Math.Abs(p.Y) < 5.0 && p.X > 10.0 && p.X < 90.0));

        Assert.Equal(1, wrapping);
    }

    /// <summary>
    /// A dash pattern longer than the loop inks the whole closed subpath, and a whole loop has no ends: capping it
    /// would put two round bulges in the middle of a line that was never broken. The assertion is that the result
    /// is the **undashed outline exactly**, which is the only thing "nothing in the pattern turned the ink off"
    /// can mean - and the extents would not catch it, because a cap at the seam lands inside the band's own
    /// corners.
    /// </summary>
    [Fact]
    public void ADashLongerThanAClosedLoopDrawsTheWholeLoop()
    {
        PathItem path = Square(100);
        StrokeSpec dashed = Spec(
            8,
            new DashPattern(new[] { 500.0, 100.0 }),
            StrokeCap.Round,
            WidthProfileSpec.Constant(8));

        IReadOnlyList<Point2D> inked = Assert.Single(StrokeOutlineBuilder.Outline(path, dashed));
        IReadOnlyList<Point2D> whole = Assert.Single(
            StrokeOutlineBuilder.Outline(path, dashed with { Dash = DashPattern.None }));

        Assert.Equal(whole.Count, inked.Count);
        for (int i = 0; i < whole.Count; i++)
        {
            Assert.Equal(whole[i].X, inked[i].X, 6);
            Assert.Equal(whole[i].Y, inked[i].Y, 6);
        }
    }

    /// <summary>
    /// **The dash scales with the renderer, like the width.** Both are lengths of the stroke measured in the same
    /// units, so a renderer that asks for twice the size asks for a dash twice as long - and a dash pattern that
    /// ignored the scale would double the number of dashes on every scaled export.
    /// </summary>
    [Fact]
    public void TheDashScalesWithTheRendererLikeTheWidth()
    {
        PathItem path = Line(100);
        StrokeSpec stroke = Spec(8, new DashPattern(new[] { 6.0, 3.0 }), profile: WidthProfileSpec.Constant(8));

        IReadOnlyList<IReadOnlyList<Point2D>> dashes = StrokeOutlineBuilder.Outline(path, stroke, scale: 2.0);

        // A 12-on/6-off pattern: six dashes, the last clipped by the end of the line.
        Assert.Equal(6, dashes.Count);
        Assert.Equal(0.0, dashes[0].Min(p => p.X), 6);
        Assert.Equal(12.0, dashes[0].Max(p => p.X), 6);
        Assert.Equal(-8.0, dashes[0].Min(p => p.Y), 6);

        // The line itself is not scaled - the exporter places the geometry with its own matrix - so only the
        // widths and the dash lengths double.
        Assert.Equal(90.0, dashes[^1].Min(p => p.X), 6);
        Assert.Equal(100.0, dashes[^1].Max(p => p.X), 6);
    }

    /// <summary>
    /// **The case the issue was filed with**: a dash on a stroke with an outline effect. The effect reshapes the
    /// dashes - it does not get to fill the gaps - so every dash survives the roughen and stays where it was.
    /// </summary>
    [Fact]
    public void AnOutlineEffectReshapesEachDashRatherThanFillingTheGaps()
    {
        PathItem path = Line(100);
        var rough = new EffectStack(new[] { OutlineEffectSpec.Roughen(0.5, seed: 5) });
        StrokeSpec stroke = Spec(
            8,
            new DashPattern(new[] { 6.0, 3.0 }),
            profile: WidthProfileSpec.Constant(8),
            effects: rough);

        StrokeRenderPlan plan = StrokeOutlineBuilder.Plan(path, stroke);

        Assert.True(plan.IsOutline);
        Assert.Equal(12, plan.Outlines.Count);

        // A half-unit roughen cannot reach more than half a unit into a three-unit gap, so the gaps are still
        // empty - which is what a solid outline, however roughened, could not say.
        Assert.DoesNotContain(
            plan.Outlines.SelectMany(loop => loop),
            p => IntoPeriod(p.X) > 6.6 && IntoPeriod(p.X) < 8.4);
    }

    /// <summary>
    /// **The compatibility half.** A dash on a stroke with no profile and no effect is still a stroke: the
    /// renderers have a dash operator for that and it is the better answer, so nothing here may turn it into
    /// geometry.
    /// </summary>
    [Fact]
    public void APlainDashedStrokeIsStillStrokedNatively()
    {
        PathItem path = Line(100);
        StrokeSpec stroke = Spec(8, new DashPattern(new[] { 6.0, 3.0 }));

        StrokeRenderPlan plan = StrokeOutlineBuilder.Plan(path, stroke);

        Assert.False(plan.IsOutline);
        Assert.Equal(8.0, plan.Width, 6);
        Assert.Empty(plan.Outlines);
    }

    /// <summary>
    /// **The dash reaches the exported file, not only the plan.** The SVG writer turns a stroke SVG cannot carry
    /// into geometry, and being an outline is not enough: the file has to hold the dashes. Both strokes below are
    /// outlines, so the only difference is in the `d` attribute - one filled band against twelve filled dashes.
    /// </summary>
    [Fact]
    public void ADashedProfileExportsAsTheDashes()
    {
        var solid = Spec(8, profile: WidthProfileSpec.Constant(8));
        var dashed = solid with { Dash = new DashPattern(new[] { 6.0, 3.0 }) };

        string inked = Svg(Line(100), dashed);

        Assert.DoesNotContain("stroke-width", inked, StringComparison.Ordinal);

        // Every loop starts with an "M", so the subpath count is the dash count: one band undashed, twelve
        // dashes under the 6-on/3-off pattern of a 100-long line.
        Assert.Equal(1, Regex.Matches(Svg(Line(100), solid), "M ").Count);
        Assert.Equal(12, Regex.Matches(inked, "M ").Count);
    }

    /// <summary>One path in a document on an origin artboard, as SVG.</summary>
    private static string Svg(PathItem path, StrokeSpec stroke)
    {
        path.Stroke = stroke;
        CadDocument document = CadDocument.CreateDefault();
        document.Artboards[0].Layers[0].AddItem(path);
        return SvgWriter.Write(document);
    }
}
