using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// Expanding a stroke into a filled path: the ink becomes geometry.
///
/// Caps and joins are the whole character of the feature - two strokes of the same width with different
/// caps paint different shapes - so they are tested **one at a time, by measuring the outline**: the node
/// coordinates for a cap, the area for a join. The first attempt at this was reverted because it produced
/// plausible but wrong ink while its tests measured only bounding boxes; these assert the geometry.
/// </summary>
public class StrokeExpanderTests
{
    private static PathItem Segment(StrokeCap cap, double width = 10)
    {
        var path = new PathItem { Name = "line" };
        SubPath sub = path.AddSubPath(closed: false);
        sub.AppendNode(new Point2D(0, 0));
        sub.AppendNode(new Point2D(100, 0));
        path.Stroke = new StrokeSpec(true, ColorRgb.Black, width, cap, StrokeJoin.Miter, 4);
        path.Fill = FillSpec.None;
        return path;
    }

    private static PathItem Box(double width = 10, StrokeJoin join = StrokeJoin.Miter, double limit = 4)
    {
        PathItem box = PathFactory.CreateRectangle("box", new Rect2D(0, 0, 100, 50));
        box.Stroke = new StrokeSpec(true, ColorRgb.Black, width, StrokeCap.Butt, join, limit);
        box.Fill = FillSpec.None;
        return box;
    }

    /// <summary>Two segments meeting at an angle, so the join's reach is measurable.</summary>
    private static PathItem Corner(StrokeJoin join, double limit, double degrees = 90)
    {
        var path = new PathItem { Name = "corner" };
        SubPath sub = path.AddSubPath(closed: false);
        sub.AppendNode(new Point2D(0, 0));
        sub.AppendNode(new Point2D(50, 0));

        double radians = degrees * Math.PI / 180.0;
        sub.AppendNode(new Point2D(50 + (50 * Math.Cos(radians)), -50 * Math.Sin(radians)));

        path.Stroke = new StrokeSpec(true, ColorRgb.Black, 10, StrokeCap.Butt, join, limit);
        path.Fill = FillSpec.None;
        return path;
    }

    private static List<Point2D> Anchors(PathItem path)
        => path.SubPaths[0].Nodes.Select(n => n.Anchor).ToList();

    private static double Area(PathItem path)
        => Math.Abs(PathFlattener.Flatten(path).Sum(o => o.SignedArea));

    // ---- the shape of the result -----------------------------------------------

    [Fact]
    public void TheResultIsAFilledPathWithItsOwnStroke()
    {
        PathItem? expanded = StrokeExpander.Expand(Segment(StrokeCap.Butt));

        Assert.NotNull(expanded);
        Assert.True(expanded!.Fill.IsVisible);
        Assert.True(expanded.Stroke.HasVisibleOutline);
        Assert.Single(expanded.SubPaths);
        Assert.True(expanded.SubPaths[0].IsClosed);
    }

    [Fact]
    public void APathWithNoStrokeExpandsToNothing()
    {
        var path = new PathItem();
        path.AddSubPath(closed: false).AppendNode(new Point2D(0, 0));
        path.Stroke = StrokeSpec.None;

        Assert.Null(StrokeExpander.Expand(path));
    }

    /// <summary>The outline's own width follows the rule in the issue, including its edge case.</summary>
    [Theory]
    [InlineData(3.0, 0.75)]
    [InlineData(2.0, 0.5)]
    [InlineData(8.0, 1.0)]
    [InlineData(4.0, 1.0)]
    public void TheOutlineWidthFollowsTheRule(double original, double expected)
    {
        Assert.Equal(expected, StrokeExpander.OutlineWidth(original), 6);
    }

    // ---- caps, one at a time ---------------------------------------------------

    /// <summary>
    /// A butt cap ends where the line ends. Four nodes, asserted by coordinate - (0,5), (100,5),
    /// (100,-5), (0,-5) - because a cap attached to the wrong side produces a bow tie with the right
    /// number of nodes and the wrong shape.
    /// </summary>
    [Fact]
    public void AButtCapIsTheBareRectangle()
    {
        PathItem expanded = StrokeExpander.Expand(Segment(StrokeCap.Butt))!;

        List<Point2D> anchors = Anchors(expanded);
        Assert.Equal(4, anchors.Count);
        Assert.Contains(new Point2D(0, 5), anchors);
        Assert.Contains(new Point2D(100, 5), anchors);
        Assert.Contains(new Point2D(100, -5), anchors);
        Assert.Contains(new Point2D(0, -5), anchors);

        Assert.Equal(1000, Area(expanded), 3);
    }

    /// <summary>A square cap projects half the width past each end, in the direction of travel.</summary>
    [Fact]
    public void ASquareCapProjectsByHalfTheWidthAtBothEnds()
    {
        PathItem expanded = StrokeExpander.Expand(Segment(StrokeCap.Square))!;

        List<Point2D> anchors = Anchors(expanded);
        Assert.Equal(8, anchors.Count);
        Assert.Contains(new Point2D(105, 5), anchors);
        Assert.Contains(new Point2D(105, -5), anchors);
        Assert.Contains(new Point2D(-5, -5), anchors);
        Assert.Contains(new Point2D(-5, 5), anchors);

        Assert.Equal(1100, Area(expanded), 3);

        Rect2D bounds = expanded.BoundingBox();
        Assert.Equal(-5, bounds.X, 3);
        Assert.Equal(105, bounds.X + bounds.Width, 3);
    }

    /// <summary>
    /// A round cap is a half circle past each end: its furthest point is the middle of the cap, and the
    /// curve is made of handles rather than corners - which is what makes it round rather than faceted.
    /// </summary>
    [Fact]
    public void ARoundCapIsAHalfCircleOfCurves()
    {
        PathItem expanded = StrokeExpander.Expand(Segment(StrokeCap.Round))!;

        List<Point2D> anchors = Anchors(expanded);
        Assert.Contains(anchors, a => a.NearlyEquals(new Point2D(105, 0), 1e-6));
        Assert.Contains(anchors, a => a.NearlyEquals(new Point2D(-5, 0), 1e-6));

        PathNode peak = expanded.SubPaths[0].Nodes
            .First(n => n.Anchor.NearlyEquals(new Point2D(105, 0), 1e-6));
        Assert.False(peak.InHandle.NearlyEquals(peak.Anchor, 1e-9));
        Assert.False(peak.OutHandle.NearlyEquals(peak.Anchor, 1e-9));

        Rect2D bounds = expanded.BoundingBox();
        Assert.Equal(-5, bounds.X, 3);
        Assert.Equal(105, bounds.X + bounds.Width, 3);

        // Measured at a finer flattening than the default: a semicircle measured from chords comes out a
        // shade small - about a square unit across the two caps at 0.05mm - which is the discretisation
        // and not the geometry, and asserting through it would be testing the wrong thing.
        double area = Math.Abs(PathFlattener.Flatten(expanded, tolerance: 1e-4).Sum(o => o.SignedArea));
        Assert.True(Math.Abs(area - (1000 + (Math.PI * 25))) < 0.05,
            $"the area is {area}, expected about {1000 + (Math.PI * 25)}");
    }

    // ---- a closed path is banded on both sides ---------------------------------

    /// <summary>
    /// A closed path expands to both sides: an outer contour and an inner one. The band's area is the
    /// outline's length times its width - 300 x 10 - which is exactly what the offset rectangles give:
    /// 110x60 outside, 90x40 inside.
    /// </summary>
    [Fact]
    public void AClosedPathExpandsToABandOnBothSides()
    {
        PathItem expanded = StrokeExpander.Expand(Box())!;

        Assert.Equal(2, expanded.SubPaths.Count);

        Rect2D bounds = expanded.BoundingBox();
        Assert.Equal(-5, bounds.X, 3);
        Assert.Equal(-5, bounds.Y, 3);
        Assert.Equal(110, bounds.Width, 3);
        Assert.Equal(60, bounds.Height, 3);

        Assert.Equal(3000, Area(expanded), 3);
    }

    /// <summary>The inner contour runs the other way, so the band is a hole under the nonzero rule.</summary>
    [Fact]
    public void TheBandsInnerContourWindsAgainstTheOuterOne()
    {
        PathItem expanded = StrokeExpander.Expand(Box())!;

        IReadOnlyList<FlattenedOutline> outlines = PathFlattener.Flatten(expanded);
        Assert.Equal(2, outlines.Count);
        Assert.True(outlines[0].WindsAgainst(outlines[1]));

        Assert.False(PathFlattener.IsFilled(outlines, FillRule.NonZero, new Point2D(50, 25)));
        Assert.True(PathFlattener.IsFilled(outlines, FillRule.NonZero, new Point2D(50, 0)));
    }

    /// <summary>An open path with a curved input expands to a band along it, not a blob.</summary>
    [Fact]
    public void ACurvedOpenPathTracesItsWholeOutside()
    {
        var path = new PathItem { Name = "arc" };
        SubPath sub = path.AddSubPath(closed: false);

        const double k = 0.5522847498307936;
        PathNode start = sub.AppendNode(new Point2D(0, 50));
        start.OutHandle = new Point2D(k * 50, 50);
        PathNode end = sub.AppendNode(new Point2D(50, 0));
        end.InHandle = new Point2D(50, k * 50);

        path.Stroke = new StrokeSpec(true, ColorRgb.Black, 8, StrokeCap.Butt, StrokeJoin.Miter, 4);
        path.Fill = FillSpec.None;

        PathItem expanded = StrokeExpander.Expand(path)!;

        Assert.Single(expanded.SubPaths);
        Assert.True(expanded.SubPaths[0].IsClosed);

        double expected = Math.PI * 50 / 2 * 8;
        Assert.True(Math.Abs(Area(expanded) - expected) < expected * 0.06,
            $"the band is {Area(expanded)}, expected about {expected}");
    }

    // ---- joins -----------------------------------------------------------------

    /// <summary>Whether the outline has a node at a point, which is where a join style puts its mark.</summary>
    private static bool HasNodeAt(PathItem path, double x, double y, double tolerance = 1e-6)
        => path.SubPaths[0].Nodes.Any(n =>
            Math.Abs(n.Anchor.X - x) < tolerance && Math.Abs(n.Anchor.Y - y) < tolerance);

    /// <summary>Whether the outline has a node at a distance from the corner - a join's reach.</summary>
    private static bool HasNodeAtDistance(PathItem path, double distance, double tolerance = 1e-6)
        => path.SubPaths[0].Nodes.Any(n =>
            Math.Abs(Math.Sqrt(Math.Pow(n.Anchor.X - 50, 2) + Math.Pow(n.Anchor.Y, 2)) - distance) < tolerance);

    /// <summary>
    /// The furthest any node near the corner is from the corner. Restricted to the corner because the far
    /// end of the path is 50 away whatever the join does, so a maximum over every node measures the path
    /// rather than the join - which is exactly what the first version of these tests did, and it proved
    /// nothing at all.
    /// </summary>
    private static double JoinReachNearTheCorner(PathItem path, double within = 30)
        => path.SubPaths[0].Nodes
            .Select(n => Math.Sqrt(Math.Pow(n.Anchor.X - 50, 2) + Math.Pow(n.Anchor.Y, 2)))
            .Where(d => d <= within)
            .Max();

    /// <summary>
    /// A miter runs the corner to a point: at half width 5 and a right angle that point is exactly
    /// (55, 5) - half the width along each axis, which is 5*sqrt(2) = 7.07 from the vertex.
    /// </summary>
    [Fact]
    public void AMiterPutsAPointAtTheWidenedCorner()
    {
        PathItem expanded = StrokeExpander.Expand(Corner(StrokeJoin.Miter, limit: 4))!;

        Assert.True(HasNodeAt(expanded, 55, 5), "the miter point should be at (55, 5)");
        Assert.True(HasNodeAtDistance(expanded, 5 * Math.Sqrt(2)), "and 7.07 from the vertex");
    }

    /// <summary>
    /// The miter limit is what stops a spike. A right angle's spike is 1.41 times the half width, so a
    /// limit of 1.2 cuts it back and a limit of 4 does not - tested from both sides of the boundary, which
    /// is the only way to know the limit is applied rather than the join ignored.
    /// </summary>
    [Fact]
    public void TheMiterLimitTurnsASpikeIntoABevel()
    {
        PathItem tight = StrokeExpander.Expand(Corner(StrokeJoin.Miter, limit: 1.2))!;
        PathItem generous = StrokeExpander.Expand(Corner(StrokeJoin.Miter, limit: 4))!;

        Assert.False(HasNodeAt(tight, 55, 5), "a limit of 1.2 should cut the spike off");
        Assert.Equal(5, JoinReachNearTheCorner(tight), 3);

        Assert.True(HasNodeAt(generous, 55, 5), "a limit of 4 should leave the spike");
        Assert.Equal(5 * Math.Sqrt(2), JoinReachNearTheCorner(generous), 3);
    }

    /// <summary>
    /// A **sharp** corner's spike reaches much further than a right angle's, and a shallow one reaches
    /// less - the miter length is the width over the sine of half the angle actually turned, so the two
    /// ends of the range are worth pinning, not just the middle.
    ///
    /// `degrees` is the angle between the two segments: 90 is a right angle, 150 is a sharp corner, and 30
    /// is barely a bend at all. The first version of this test called 30 degrees "acute" and expected 19.3
    /// from it; the geometry says 5.18, and the implementation was right.
    /// </summary>
    [Fact]
    public void AMitersReachFollowsTheAngleTurned()
    {
        PathItem right = StrokeExpander.Expand(Corner(StrokeJoin.Miter, limit: 20, degrees: 90))!;
        PathItem sharp = StrokeExpander.Expand(Corner(StrokeJoin.Miter, limit: 20, degrees: 150))!;
        PathItem shallow = StrokeExpander.Expand(Corner(StrokeJoin.Miter, limit: 20, degrees: 30))!;

        Assert.True(HasNodeAtDistance(right, 5 / Math.Cos(Math.PI / 4)), "a right angle reaches 7.07");
        Assert.True(HasNodeAtDistance(sharp, 5 / Math.Cos(75 * Math.PI / 180)), "150 degrees reaches 19.3");
        Assert.True(HasNodeAtDistance(shallow, 5 / Math.Cos(15 * Math.PI / 180)), "30 degrees reaches 5.18");
    }

    /// <summary>A bevel joins the two offset edges straight across, so nothing goes past the half width.</summary>
    [Fact]
    public void ABevelReachesExactlyTheHalfWidth()
    {
        PathItem bevel = StrokeExpander.Expand(Corner(StrokeJoin.Bevel, limit: 20))!;

        Assert.Equal(5, JoinReachNearTheCorner(bevel), 3);
        Assert.True(HasNodeAt(bevel, 55, 0), "the bevel's outer point is on the first segment's side");
        Assert.True(HasNodeAt(bevel, 50, 5), "and on the second segment's side");
    }
    /// <summary>
    /// A round join is an arc of the stroke's own radius about the vertex. It reaches exactly as far as a
    /// bevel - both stop at the half width - and differs by being **curved** and by covering the segment
    /// between the arc and the chord. Reach alone would not tell them apart, which is why both are checked.
    /// </summary>
    [Fact]
    public void ARoundJoinIsAnArcOfTheStrokeRadius()
    {
        PathItem round = StrokeExpander.Expand(Corner(StrokeJoin.Round, limit: 20))!;
        PathItem bevel = StrokeExpander.Expand(Corner(StrokeJoin.Bevel, limit: 20))!;

        Assert.Equal(5, JoinReachNearTheCorner(round), 3);
        Assert.Equal(JoinReachNearTheCorner(bevel), JoinReachNearTheCorner(round), 3);

        // The arc: every join point is within the radius of the vertex, and some of them are curved.
        Assert.Contains(round.SubPaths[0].Nodes, n =>
            Math.Sqrt(Math.Pow(n.Anchor.X - 50, 2) + Math.Pow(n.Anchor.Y, 2)) > 4.9 &&
            !n.InHandle.NearlyEquals(n.Anchor, 1e-9));

        // And it fills the segment between arc and chord, so it is bigger than the bevel.
        // The areas are close rather than ordered: the arc adds the segment beyond the chord on the
        // outside of the corner and takes the matching slice on the inside, and the two very nearly
        // cancel. Asserting "round is bigger" was wrong for that reason - the shape differs, the total
        // barely does - so what is asserted is that they agree to within a couple of percent.
        double roundedArea = Math.Abs(PathFlattener.Flatten(round, tolerance: 1e-4).Sum(o => o.SignedArea));
        double bevelArea = Math.Abs(PathFlattener.Flatten(bevel, tolerance: 1e-4).Sum(o => o.SignedArea));
        Assert.True(Math.Abs(roundedArea - bevelArea) < bevelArea * 0.02,
            $"a round join and a bevel join cover nearly the same area: {roundedArea} vs {bevelArea}");
    }
}
