using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// An outline's ends and corners are the stroke's, not a filled region's defaults.
///
/// A stroke only becomes an outline when it carries a width profile or an effect, and from that moment the
/// renderer's pen is gone: nothing downstream knows the cap or the join, because a filled loop has neither. The
/// canvas, the PDF writer and the SVG writer all fill the loops the plan carries, so unless the plan puts the
/// stroke's own ends and corners into those loops, all three draw square ends and mitred corners where a person
/// drew a round cap and a round join.
///
/// Every assertion is on **geometry** - the extent past the path's end, and the shape of the corner - because a
/// test that only said "the outline exists" would pass against exactly the defect: an outline that exists and is
/// the wrong shape is what this is here to catch. The caps are measured against the same expectations the pen's
/// own tests use (<c>StrokeExpanderTests</c>), so the two cannot drift apart again.
/// </summary>
public class StrokeOutlineCapJoinTests
{
    private const double Width = 10;

    /// <summary>Half the stroke width, which is what a cap projects or rounds by and a join reaches.</summary>
    private const double Half = Width / 2;

    private static PathItem Path(bool closed, params Point2D[] points)
    {
        var path = new PathItem { Name = "scratch", Fill = FillSpec.None };
        SubPath sub = path.AddSubPath(closed);
        foreach (Point2D point in points)
        {
            sub.Nodes.Add(new PathNode(point));
        }

        return path;
    }

    /// <summary>
    /// A stroke that reaches the outline route: a constant width profile is enough to make it geometry, and it
    /// leaves the cap and the join as the only things the test is varying.
    /// </summary>
    private static StrokeSpec Stroke(StrokeCap cap = StrokeCap.Butt, StrokeJoin join = StrokeJoin.Miter)
        => new(true, ColorRgb.Black, Width, cap, join, 4,
            StrokeAlignment.Center, default, WidthProfileSpec.Constant(Width));

    /// <summary>The single loop the plan carries for this path and stroke.</summary>
    private static IReadOnlyList<Point2D> Outline(PathItem path, StrokeCap cap = StrokeCap.Butt, StrokeJoin join = StrokeJoin.Miter)
    {
        StrokeRenderPlan plan = StrokeOutlineBuilder.Plan(path, Stroke(cap, join));
        Assert.True(plan.IsOutline, "a profiled stroke has to become the region it covers");
        return Assert.Single(plan.Outlines);
    }

    private static double Distance(Point2D a, Point2D b)
        => Math.Sqrt(((b.X - a.X) * (b.X - a.X)) + ((b.Y - a.Y) * (b.Y - a.Y)));

    /// <summary>How far a point lies off a segment - the corner cut's own depth.</summary>
    private static double DistanceToSegment(Point2D point, Point2D a, Point2D b)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        double lengthSquared = (dx * dx) + (dy * dy);
        if (lengthSquared < 1e-18)
        {
            return Distance(point, a);
        }

        double t = Math.Clamp((((point.X - a.X) * dx) + ((point.Y - a.Y) * dy)) / lengthSquared, 0.0, 1.0);
        return Distance(point, new Point2D(a.X + (dx * t), a.Y + (dy * t)));
    }

    // ------------------------------------------------------------------ caps

    /// <summary>
    /// **Butt does not extend at all.** The two offset edges already meet the end squarely, so the outline stops
    /// where the path does. Stated as a test rather than left implicit because the two caps below both *do*
    /// project, and a fix that made every cap project would leave butt wrong in the other direction.
    /// </summary>
    [Fact]
    public void AButtCapLeavesTheOutlineFlushWithThePathEnd()
    {
        IReadOnlyList<Point2D> band = Outline(
            Path(false, new Point2D(0, 0), new Point2D(100, 0)), StrokeCap.Butt);

        Assert.Equal(0.0, band.Min(p => p.X), 6);
        Assert.Equal(100.0, band.Max(p => p.X), 6);
        Assert.All(band, p => Assert.InRange(p.X, -1e-9, 100.0 + 1e-9));
    }

    /// <summary>
    /// **A square cap projects by half the width past each end**, so a 10-wide stroke on a line from 0 to 100
    /// reaches -5 and 105. The furthest points are the cap's two corners, which is what tells a square cap from a
    /// round one on extent alone.
    /// </summary>
    [Fact]
    public void ASquareCapProjectsHalfTheWidthPastEachEnd()
    {
        IReadOnlyList<Point2D> band = Outline(
            Path(false, new Point2D(0, 0), new Point2D(100, 0)), StrokeCap.Square);

        Assert.Equal(-Half, band.Min(p => p.X), 6);
        Assert.Equal(100.0 + Half, band.Max(p => p.X), 6);

        List<Point2D> far = band.Where(p => p.X > 100.0 + Half - 1e-9).ToList();
        Assert.Equal(2, far.Count);
        Assert.All(far, p => Assert.Equal(Half, Math.Abs(p.Y), 6));

        List<Point2D> near = band.Where(p => p.X < -Half + 1e-9).ToList();
        Assert.Equal(2, near.Count);
        Assert.All(near, p => Assert.Equal(Half, Math.Abs(p.Y), 6));
    }

    /// <summary>
    /// **A round cap is a half turn past the end.** It reaches the same distance as a square one but along the
    /// path's own axis, so the furthest point has no sideways offset at all - and the end is a curve, which is
    /// asserted by the cap's own points lying on a circle of radius half the width about the path's end.
    /// </summary>
    [Fact]
    public void ARoundCapIsAHalfTurnPastEachEnd()
    {
        IReadOnlyList<Point2D> band = Outline(
            Path(false, new Point2D(0, 0), new Point2D(100, 0)), StrokeCap.Round);

        Assert.Equal(-Half, band.Min(p => p.X), 6);
        Assert.Equal(100.0 + Half, band.Max(p => p.X), 6);

        Point2D furthest = band.OrderByDescending(p => p.X).First();
        Assert.Equal(0.0, furthest.Y, 6);

        // The cap's points sit at the cap's radius about the end of the path, where a square cap puts its corners
        // on the same circle but leaves the arc empty.
        var end = new Point2D(100, 0);
        int onTheArc = band.Count(p => Math.Abs(Distance(p, end) - Half) < 1e-6);
        Assert.True(onTheArc >= 4, $"the end should be an arc about (100,0), but only {onTheArc} points lie on it");
    }

    /// <summary>
    /// **A closed outline has no ends, so its cap cannot change it.** Capping the seam would put a round bulge in
    /// the middle of a loop that was never broken - the same mistake the dash tests guard against at a dash seam.
    /// </summary>
    [Fact]
    public void AClosedOutlineHasNoEndsToCap()
    {
        PathItem square = Path(
            true, new Point2D(0, 0), new Point2D(100, 0), new Point2D(100, 100), new Point2D(0, 100));

        IReadOnlyList<Point2D> butt = Outline(square, StrokeCap.Butt);
        IReadOnlyList<Point2D> round = Outline(square, StrokeCap.Round);

        Assert.Equal(butt.Count, round.Count);
        for (int i = 0; i < butt.Count; i++)
        {
            Assert.Equal(butt[i].X, round[i].X, 6);
            Assert.Equal(butt[i].Y, round[i].Y, 6);
        }
    }

    // ---------------------------------------------------------------- corners

    /// <summary>The right-angle corner every join test below turns: (0,0) to (100,0) to (100,100).</summary>
    private static readonly Point2D Corner = new(100, 0);

    private static IReadOnlyList<Point2D> RightAngle(StrokeJoin join)
        => Outline(
            Path(false, new Point2D(0, 0), Corner, new Point2D(100, 100)),
            StrokeCap.Butt,
            join);

    /// <summary>
    /// The outline's own points at the **outside** of the corner, in contour order.
    ///
    /// The corner's outer side is the one the path turns away from, and on this path it is everything at or past
    /// the vertex's x that is not on the inner edge. Naming the selection keeps the assertions about the join
    /// rather than about where a point happens to sit in the loop.
    /// </summary>
    private static List<Point2D> OuterCorner(IReadOnlyList<Point2D> loop)
        => loop.Where(p => p.X >= Corner.X - 1e-9 && p.Y <= Corner.Y + 1e-9).ToList();

    /// <summary>The direction the two offset edges' bisector points: out along the mitre, at 45 degrees here.</summary>
    private static readonly Vector2D Bisector = new(1.0 / Math.Sqrt(2), -1.0 / Math.Sqrt(2));

    /// <summary>Whether a point is out along the corner's bisector.</summary>
    private static bool IsOnBisector(Point2D point)
    {
        double distance = Distance(point, Corner);
        return distance > 1e-9
            && Math.Abs(((point.X - Corner.X) / distance) - Bisector.X) < 1e-6
            && Math.Abs(((point.Y - Corner.Y) / distance) - Bisector.Y) < 1e-6;
    }

    /// <summary>
    /// **A mitre reaches the intersection of the two offset edges.** They meet at half the width times sqrt(2)
    /// from the corner, along its bisector - 7.07 for a 10-wide stroke, against the 5 a bevel would stop at. This
    /// is the behaviour that was already right, and bevel-everything would quietly take it away.
    /// </summary>
    [Fact]
    public void AMiterJoinMeetsAtTheIntersectionOfTheTwoOffsetEdges()
    {
        List<Point2D> corner = OuterCorner(RightAngle(StrokeJoin.Miter));

        Point2D only = Assert.Single(corner);
        Assert.Equal(Half * Math.Sqrt(2), Distance(only, Corner), 6);
        Assert.True(IsOnBisector(only), $"a mitre runs out along the bisector, not to {only.X},{only.Y}");
    }

    /// <summary>
    /// **A bevel cuts the corner off.** The two offset edges stop at their own ends - half the width from the
    /// corner each - and a straight line joins them, which passes only `half / sqrt(2)` from the corner. That is
    /// the opposite of a mitre in both ways: no point reaches past the half-width, and the corner loses area
    /// instead of gaining a point.
    /// </summary>
    [Fact]
    public void ABevelJoinCutsTheCornerOff()
    {
        List<Point2D> corner = OuterCorner(RightAngle(StrokeJoin.Bevel));

        Assert.Equal(2, corner.Count);
        Assert.All(corner, p => Assert.Equal(Half, Distance(p, Corner), 6));

        double cutDepth = DistanceToSegment(Corner, corner[0], corner[1]);
        Assert.Equal(Half / Math.Sqrt(2), cutDepth, 6);
    }

    /// <summary>
    /// **A round join arcs about the corner.** Every point of the join is the half-width from the vertex, and the
    /// arc bulges into the corner far enough to reach the bisector - where a bevel's straight cut passes only
    /// 3.54 out - and no further, which is what separates it from the mitre's 7.07.
    /// </summary>
    [Fact]
    public void ARoundJoinArcsAboutTheCorner()
    {
        List<Point2D> corner = OuterCorner(RightAngle(StrokeJoin.Round));

        Assert.True(corner.Count >= 4, $"a round join is an arc of points, but only {corner.Count} came out");
        Assert.All(corner, p => Assert.Equal(Half, Distance(p, Corner), 6));
        Assert.Contains(corner, IsOnBisector);
    }
}
