using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// Object → Outline Stroke draws the geometry the shared builder plans, so the command cannot drift from the
/// canvas, the PDF exporter and the SVG writer.
///
/// These are the three things the expander used to read for itself - the dash, the width profile and the outline
/// effects. Each is asserted against the builder's own answer rather than against a hand-written expectation,
/// which is what makes the comparison survive a change to the builder: the test cannot agree with the expander
/// and disagree with the renderers.
///
/// A second expander is invisible while every one of them is right. It is wrong the moment one of them learns
/// something, and the symptom is a command that draws a different picture from the canvas it was run on.
/// </summary>
public class StrokeExpanderOutlineParityTests
{
    private static PathItem Line(double width = 10)
    {
        var path = new PathItem { Name = "line", Fill = FillSpec.None };
        SubPath sub = path.AddSubPath(closed: false);
        sub.AppendNode(new Point2D(0, 0));
        sub.AppendNode(new Point2D(100, 0));
        path.Stroke = new StrokeSpec(true, ColorRgb.Black, width, StrokeCap.Butt, StrokeJoin.Miter, 4);
        return path;
    }

    /// <summary>
    /// The expanded contours, in order, against the builder's loops **point for point**.
    ///
    /// Point for point rather than by bounds: a band of the right size in the wrong place, or a solid band where
    /// the builder cut dashes, is exactly what a second expander produces.
    /// </summary>
    private static void AssertSameContours(PathItem expanded, IReadOnlyList<IReadOnlyList<Point2D>> loops)
    {
        Assert.Equal(loops.Count, expanded.SubPaths.Count);

        for (int i = 0; i < loops.Count; i++)
        {
            List<Point2D> anchors = expanded.SubPaths[i].Nodes.Select(n => n.Anchor).ToList();
            Assert.Equal(loops[i].Count, anchors.Count);

            for (int j = 0; j < loops[i].Count; j++)
            {
                Assert.Equal(loops[i][j].X, anchors[j].X, 9);
                Assert.Equal(loops[i][j].Y, anchors[j].Y, 9);
            }
        }
    }

    /// <summary>The geometry itself, so "this is not the same shape" is a comparison rather than a claim.</summary>
    private static string Describe(PathItem path)
        => string.Join("|", path.SubPaths.Select(sub => string.Join(
            ";", sub.Nodes.Select(n => FormattableString.Invariant($"{n.Anchor.X:F4},{n.Anchor.Y:F4}")))));

    private static List<Point2D> Anchors(PathItem path)
        => path.SubPaths[0].Nodes.Select(n => n.Anchor).ToList();

    /// <summary>
    /// A dashed stroke expands to the dashes: three inked runs of 20pt on a 100pt line at 20/20, each the
    /// geometry the builder cuts, and not the single solid band the expander drew before.
    /// </summary>
    [Fact]
    public void ADashedStrokeExpandsToTheDashedOutline()
    {
        PathItem dashed = Line();
        dashed.Stroke = dashed.Stroke with { Dash = new DashPattern(new[] { 20.0, 20.0 }) };

        PathItem expanded = StrokeExpander.Expand(dashed)!;

        // The builder's own answer for the dashes. `Plan` returns a **stroked** plan here - a constant-width
        // stroke reaches the renderer's pen, and the pen is what dashes it - so the builder's outline is the
        // plan's dash geometry, and comparing with `plan.Outlines` would only compare with nothing.
        IReadOnlyList<IReadOnlyList<Point2D>> expected = StrokeOutlineBuilder.Outline(dashed, dashed.Stroke);
        Assert.Equal(3, expected.Count);
        AssertSameContours(expanded, expected);

        // And the dashes are real, not merely more contours: each run is the on-interval's length.
        Assert.All(expanded.SubPaths, sub =>
            Assert.Equal(20.0, sub.Nodes.Max(n => n.Anchor.X) - sub.Nodes.Min(n => n.Anchor.X), 6));

        // The undashed expansion of the same path is one band, and a different shape.
        PathItem solid = StrokeExpander.Expand(Line())!;
        Assert.Single(solid.SubPaths);
        Assert.NotEqual(Describe(solid), Describe(expanded));
    }

    /// <summary>
    /// A width profile expands to the profile's widths: 20pt at the start, tapering to nothing at the far end,
    /// where the same path with no profile is 10pt wide throughout.
    /// </summary>
    [Fact]
    public void AWidthProfileExpandsToTheProfilesWidths()
    {
        PathItem tapered = Line();
        tapered.Stroke = tapered.Stroke with { WidthProfile = WidthProfileSpec.Taper(20, 0) };

        StrokeRenderPlan plan = StrokeOutlineBuilder.Plan(tapered, tapered.Stroke);
        Assert.True(plan.IsOutline);

        PathItem expanded = StrokeExpander.Expand(tapered)!;
        AssertSameContours(expanded, plan.Outlines);

        // The profile really is applied, and this is the measurement that says so: the fat end is 20 wide, the
        // far end has closed to the point the profile asks for, and nothing is wider than the profile allows.
        List<Point2D> anchors = Anchors(expanded);
        Assert.Contains(anchors, p => p.NearlyEquals(new Point2D(0, -10), 1e-9));
        Assert.Contains(anchors, p => p.NearlyEquals(new Point2D(0, 10), 1e-9));
        Assert.Contains(anchors, p => p.NearlyEquals(new Point2D(100, 0), 1e-9));
        Assert.DoesNotContain(anchors, p => Math.Abs(p.Y) > 10.0 + 1e-9);

        // The constant-width expansion is 10 wide at the far end, which is what the profile replaces.
        Assert.Contains(
            Anchors(StrokeExpander.Expand(Line())!),
            p => p.NearlyEquals(new Point2D(100, 5), 1e-9));
    }

    /// <summary>
    /// An outline effect expands to the effected outline: the zig-zag's points, which are off the plain band's
    /// edges, at the geometry the builder hands the renderers to fill.
    /// </summary>
    [Fact]
    public void AnOutlineEffectExpandsToTheEffectedOutline()
    {
        PathItem jagged = Line();
        jagged.Stroke = jagged.Stroke with
        {
            Effects = new EffectStack(new[] { OutlineEffectSpec.ZigZag(4.0) }),
        };

        StrokeRenderPlan plan = StrokeOutlineBuilder.Plan(jagged, jagged.Stroke);
        Assert.True(plan.IsOutline);

        PathItem expanded = StrokeExpander.Expand(jagged)!;
        AssertSameContours(expanded, plan.Outlines);

        // The effect really is applied: the plain band is a rectangle whose edges are at y = ±5, and a zig-zag
        // puts points off those edges. Without the effect the expansion is the four corners and nothing else.
        List<Point2D> anchors = Anchors(expanded);
        PathItem plain = StrokeExpander.Expand(Line())!;
        Assert.Equal(4, plain.SubPaths[0].Nodes.Count);
        Assert.True(anchors.Count > plain.SubPaths[0].Nodes.Count);
        Assert.Contains(anchors, p => Math.Abs(Math.Abs(p.Y) - 5.0) > 1e-6);

        Assert.NotEqual(Describe(plain), Describe(expanded));
    }
}
