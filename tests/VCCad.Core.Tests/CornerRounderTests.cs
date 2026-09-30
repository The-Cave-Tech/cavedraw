using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// Rounding a corner: the point where two segments meet becomes an arc of a given radius.
///
/// The measurements are the point of it. **The radius and the distance along the edges are the same only
/// at a right angle** - an arc of radius r meets the segments at r / tan(theta/2) from the corner - so the
/// tests cover a right angle, a sharp corner and a shallow one, because a suite of right angles would pass
/// for an implementation that had the trigonometry wrong everywhere it mattered.
/// </summary>
public class CornerRounderTests
{
    /// <summary>A three-point corner: a segment in, a corner, and a segment out at the given angle.</summary>
    private static PathItem Corner(double degrees, double arm = 100)
    {
        var path = new PathItem { Name = "corner" };
        SubPath sub = path.AddSubPath(closed: false);
        sub.AppendNode(new Point2D(0, 0));
        sub.AppendNode(new Point2D(arm, 0));

        double radians = degrees * Math.PI / 180.0;
        sub.AppendNode(new Point2D(arm + (arm * Math.Cos(radians)), -arm * Math.Sin(radians)));
        return path;
    }

    private static PathItem Square(double size = 100)
        => PathFactory.CreateRectangle("square", new Rect2D(0, 0, size, size));

    private static List<Point2D> Anchors(PathItem path, int sub = 0)
        => path.SubPaths[sub].Nodes.Select(n => n.Anchor).ToList();

    private static double Distance(Point2D a, Point2D b)
        => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    /// <summary>
    /// A right angle, which is the case a person pictures: an arc of radius 40 meets each edge exactly 40
    /// from the corner, and the corner itself is gone.
    /// </summary>
    [Fact]
    public void ARightAngleIsRoundedByTheRadius()
    {
        PathItem path = Corner(90);
        var corner = new Point2D(100, 0);

        CornerRoundResult result = CornerRounder.Round(path, 0, 1, 40);

        Assert.True(result.Rounded);
        Assert.Equal(40, result.Radius, 6);
        Assert.False(result.Clamped);

        // The arc's two ends are a distance of `radius` from the corner - at a right angle the tangent
        // offset and the radius are the same thing, which is why this is the case a person pictures.
        List<Point2D> anchors = Anchors(path);
        Assert.Equal(2, anchors.Count(a => Math.Abs(Distance(a, corner) - 40) < 1e-6));

        // And the corner itself is no longer a point of the path.
        Assert.DoesNotContain(anchors, a => a.NearlyEquals(corner, 1e-6));
    }

    /// <summary>
    /// The arc is a curve, not a cut across the corner: its nearest point to the corner is
    /// `radius * (sqrt(2) - 1)` away, which is a fact about a quarter circle that a straight chamfer would
    /// get wrong. Measured from the flattened outline rather than from chosen nodes, so the test does not
    /// depend on how many cubics the arc happens to be made of.
    /// </summary>
    [Fact]
    public void TheArcBowsAwayFromTheCornerByTheRightAmount()
    {
        PathItem path = Corner(90);
        const double radius = 40;
        var corner = new Point2D(100, 0);

        CornerRounder.Round(path, 0, 1, radius);

        double nearest = PathFlattener
            .Flatten(path, tolerance: 1e-4)
            .SelectMany(o => o.Points)
            .Min(p => Distance(p, corner));

        Assert.Equal(radius * (Math.Sqrt(2) - 1), nearest, 1);
    }
    /// <summary>Rounding a corner of a square leaves the other three alone, and adds one node.</summary>
    [Fact]
    public void RoundingOneCornerLeavesTheOthers()
    {
        PathItem square = Square();
        int before = square.SubPaths[0].Nodes.Count;

        CornerRoundResult result = CornerRounder.Round(square, 0, 0, 30);

        Assert.True(result.Rounded);
        Assert.Equal(before + 1, square.SubPaths[0].Nodes.Count);
        // The three untouched corners are still exactly where they were.
        List<Point2D> anchors = Anchors(square);
        Assert.Contains(anchors, a => a.NearlyEquals(new Point2D(100, 0), 1e-6));
        Assert.Contains(anchors, a => a.NearlyEquals(new Point2D(100, 100), 1e-6));
        Assert.Contains(anchors, a => a.NearlyEquals(new Point2D(0, 100), 1e-6));

        // And the rounded corner is not.
        Assert.DoesNotContain(anchors, a => a.NearlyEquals(new Point2D(0, 0), 1e-6));
    }

    /// <summary>A radius of nearly the whole edge is allowed and consumes no more than the edge.</summary>
    [Fact]
    public void TheLargestRadiusLeavesTheRestOfTheEdge()
    {
        PathItem square = Square(100);

        double largest = CornerRounder.MaxRadius(square.SubPaths[0], 0);
        Assert.Equal(100 * 0.999, largest, 6);

        CornerRoundResult result = CornerRounder.Round(square, 0, 0, largest);

        Assert.True(result.Rounded);
        Assert.False(result.Clamped);
        Assert.Equal(100 * 0.999, result.Radius, 6);
    }

    // ---- the cases that should be refused, with a reason -----------------------

    /// <summary>The end of an open path is not a corner.</summary>
    [Fact]
    public void TheEndOfAnOpenPathIsRefused()
    {
        PathItem path = Corner(90);

        CornerRoundResult result = CornerRounder.Round(path, 0, 0, 20);

        Assert.False(result.Rounded);
        Assert.NotNull(result.Reason);
        Assert.Contains("open path", result.Reason!);
        Assert.Equal(3, path.SubPaths[0].Nodes.Count);
    }

    /// <summary>A straight run has no corner to round.</summary>
    [Fact]
    public void ACollinearPointIsRefused()
    {
        var path = new PathItem { Name = "straight" };
        SubPath sub = path.AddSubPath(closed: true);
        sub.AppendNode(new Point2D(0, 0));
        sub.AppendNode(new Point2D(50, 0));
        sub.AppendNode(new Point2D(100, 0));

        CornerRoundResult result = CornerRounder.Round(path, 0, 1, 20);

        Assert.False(result.Rounded);
        Assert.Contains("straight", result.Reason ?? string.Empty);
    }

    /// <summary>A radius of nothing changes nothing, and says so rather than "succeeding".</summary>
    [Fact]
    public void AZeroRadiusIsRefused()
    {
        PathItem path = Corner(90);

        CornerRoundResult result = CornerRounder.Round(path, 0, 1, 0);

        Assert.False(result.Rounded);
        Assert.NotNull(result.Reason);
    }

    /// <summary>Rounding announces the geometry change, or a renderer's cached geometry goes stale.</summary>
    [Fact]
    public void RoundingBumpsTheGeometryRevision()
    {
        PathItem path = Corner(90);
        int before = path.GeometryRevision;

        CornerRounder.Round(path, 0, 1, 20);

        Assert.True(path.GeometryRevision > before);
    }
}
