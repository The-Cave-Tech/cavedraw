using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// Closing an open path, in the two ways it needs closing.
///
/// Drawing by hand leaves the ends a hair apart; a shape whose ends are genuinely apart needs an edge
/// rather than a snap. The old close only handled the case where the ends coincided to within 1e-6
/// points, so a hand-drawn path would not close at all.
///
/// The measurements are in millimetres, because that is the unit this was specified in.
/// </summary>
public class PathCloserTests
{
    private const double Mm = 72.0 / 25.4;

    private static Point2D At(double xMm, double yMm) => new(xMm * Mm, yMm * Mm);

    /// <summary>An open path through the given points in millimetres, every node a corner.</summary>
    private static PathItem Open(params Point2D[] points)
    {
        var path = new PathItem { Name = "open" };
        SubPath sub = path.AddSubPath(closed: false);
        foreach (Point2D point in points)
        {
            sub.AppendNode(point);
        }

        return path;
    }

    // ---- the ends already meet -------------------------------------------------

    /// <summary>
    /// The example that prompted this: (1mm, 5mm) and (1.011mm, 4.9997mm). The non-whole point moves
    /// to the whole one, so the path closes on a number somebody meant.
    /// </summary>
    [Fact]
    public void TheNonWholePointMovesToTheWholeOne()
    {
        PathItem path = Open(At(1, 5), At(40, 5), At(40, 40), At(1.011, 4.9997));
        SubPath sub = path.SubPaths[0];

        PathCloseResult result = PathCloser.Close(path);

        Assert.Equal(PathCloseMode.Snapped, result.Mode);
        Assert.True(sub.IsClosed);

        // The two ends became one point, and it is the whole-millimetre one.
        Assert.Equal(3, sub.Nodes.Count);
        Assert.Equal(1 * Mm, sub.Nodes[0].Anchor.X, 6);
        Assert.Equal(5 * Mm, sub.Nodes[0].Anchor.Y, 6);
        Assert.True(result.Gap > 0 && result.Gap < PathCloser.SnapToleranceMm);
    }

    /// <summary>The cleaner point wins whichever end it is on - the first point here is the messy one.</summary>
    [Fact]
    public void TheWholePointWinsEvenWhenItIsTheLastOne()
    {
        PathItem path = Open(At(1.011, 4.9997), At(40, 5), At(40, 40), At(1, 5));
        SubPath sub = path.SubPaths[0];

        PathCloseResult result = PathCloser.Close(path);

        Assert.Equal(PathCloseMode.Snapped, result.Mode);
        Assert.Equal(3, sub.Nodes.Count);
        Assert.Equal(1 * Mm, sub.Nodes[0].Anchor.X, 6);
        Assert.Equal(5 * Mm, sub.Nodes[0].Anchor.Y, 6);
    }

    /// <summary>When neither is clean the last point moves to the first: the first is what was aimed at.</summary>
    [Fact]
    public void WithNeitherPointCleanTheLastMovesToTheFirst()
    {
        PathItem path = Open(At(1.0111, 5.0111), At(40, 5), At(40, 40), At(1.0113, 5.0112));
        SubPath sub = path.SubPaths[0];

        Assert.Equal(PathCloseMode.Snapped, PathCloser.Close(path).Mode);

        // The surviving point is where the FIRST one was.
        Assert.Equal(1.0111 * Mm, sub.Nodes[0].Anchor.X, 6);
        Assert.Equal(5.0111 * Mm, sub.Nodes[0].Anchor.Y, 6);
    }

    /// <summary>
    /// The moved point's own handles travel with it, by the same delta: the curve arrives at the join
    /// along the tangent it had rather than kinking, which is what "the shape doesn't change" means.
    /// </summary>
    [Fact]
    public void TheMovedPointsHandlesTravelWithIt()
    {
        // A block whose last segment arrives with a long handle, so a translation is measurable.
        PathItem path = Open(At(1, 5), At(40, 5), At(40, 40), At(1.011, 4.9997));
        SubPath sub = path.SubPaths[0];

        PathNode last = sub.Nodes[^1];
        Point2D handleBefore = last.InHandle;
        Point2D anchorBefore = last.Anchor;

        PathCloser.Close(path);

        // The delta the anchor moved is the delta the handle moved, so the tangent is unchanged.
        var anchorDelta = new Vector2D(
            sub.Nodes[0].Anchor.X - anchorBefore.X, sub.Nodes[0].Anchor.Y - anchorBefore.Y);

        // After the merge the incoming handle lives on the surviving node.
        var handleDelta = new Vector2D(
            sub.Nodes[0].InHandle.X - handleBefore.X, sub.Nodes[0].InHandle.Y - handleBefore.Y);

        Assert.Equal(anchorDelta.X, handleDelta.X, 6);
        Assert.Equal(anchorDelta.Y, handleDelta.Y, 6);
    }

    /// <summary>Closing on the join keeps the far end of the path exactly where it was.</summary>
    [Fact]
    public void TheRestOfThePathIsUntouched()
    {
        PathItem path = Open(At(1, 5), At(40, 5), At(40, 40), At(1.011, 4.9997));
        SubPath sub = path.SubPaths[0];
        Point2D middleBefore = sub.Nodes[1].Anchor;
        Point2D cornerBefore = sub.Nodes[2].Anchor;

        PathCloser.Close(path);

        Assert.Equal(middleBefore, sub.Nodes[1].Anchor);
        Assert.Equal(cornerBefore, sub.Nodes[2].Anchor);
    }

    // ---- the ends are apart ----------------------------------------------------

    /// <summary>
    /// A curved path closes curved: the new segment's handles continue the neighbouring ones,
    /// colinear, and are scaled by the two segments' lengths - the worked example, 5 cm and 3 cm,
    /// giving six tenths of the neighbour's handle.
    /// </summary>
    [Fact]
    public void TheNewSegmentContinuesTheNeighbouringHandlesInProportion()
    {
        // A 5 cm segment arriving at the last point, with a long handle at 45 degrees off straight.
        const double neighbourMm = 50;
        PathItem path = Open(At(0, 0), At(neighbourMm, 0), At(neighbourMm, 30));
        SubPath sub = path.SubPaths[0];

        // Give the last segment a handle 20 mm long, 45 degrees off the straight line to its
        // neighbour, and the first segment one likewise.
        Point2D last = sub.Nodes[^1].Anchor;
        sub.Nodes[^1].InHandle = new Point2D(
            last.X - (neighbourMm * Mm) + (14.142 * Mm), last.Y - (14.142 * Mm));
        Point2D first = sub.Nodes[0].Anchor;
        sub.Nodes[0].OutHandle = new Point2D(
            first.X + (14.142 * Mm), first.Y + (14.142 * Mm));

        // The gap is 3 cm: the example's other number.
        sub.Nodes[^1].Anchor = At(neighbourMm, 30);
        sub.Nodes[0].Anchor = new Point2D((neighbourMm + 30) * Mm, 30 * Mm);

        double newLength = Math.Sqrt(
            Math.Pow(sub.Nodes[0].Anchor.X - last.X, 2) +
            Math.Pow(sub.Nodes[0].Anchor.Y - last.Y, 2));

        PathCloseResult result = PathCloser.Close(path);

        Assert.Equal(PathCloseMode.SegmentAdded, result.Mode);
        Assert.True(sub.IsClosed);

        // The handle at the last point continues the incoming one and is scaled by 30/50.
        var closing = new Vector2D(
            sub.Nodes[^1].OutHandle.X - sub.Nodes[^1].Anchor.X,
            sub.Nodes[^1].OutHandle.Y - sub.Nodes[^1].Anchor.Y);

        Assert.True(Math.Sqrt((closing.X * closing.X) + (closing.Y * closing.Y)) > 0,
            "a long, angled handle should have produced a handle for the new segment");

        // Colinear with the neighbouring handle: the direction is the same.
        var neighbour = new Vector2D(
            sub.Nodes[^1].InHandle.X - sub.Nodes[^1].Anchor.X,
            sub.Nodes[^1].InHandle.Y - sub.Nodes[^1].Anchor.Y);
        double cross = (closing.X * neighbour.Y) - (closing.Y * neighbour.X);
        Assert.Equal(0.0, cross / (newLength * 1), 3);
    }

    /// <summary>
    /// A handle shorter than 2 mm means the shape is straight there, so the new segment is straight
    /// there too - a short handle carries no direction worth continuing.
    /// </summary>
    [Fact]
    public void AShortHandleLeavesTheNewSegmentStraight()
    {
        PathItem path = Open(At(0, 0), At(50, 0), At(50, 30));
        SubPath sub = path.SubPaths[0];

        // 1 mm, angled: short, so it must not be continued.
        Point2D last = sub.Nodes[^1].Anchor;
        sub.Nodes[^1].InHandle = new Point2D(last.X - (0.7 * Mm), last.Y - (0.7 * Mm));

        PathCloser.Close(path);

        Assert.Equal(sub.Nodes[^1].Anchor, sub.Nodes[^1].OutHandle);
    }

    /// <summary>
    /// A handle that is already nearly colinear means the shape is straight there: continuing it would
    /// add a curve where the drawing has none.
    /// </summary>
    [Fact]
    public void ANearlyColinearHandleLeavesTheNewSegmentStraight()
    {
        PathItem path = Open(At(0, 0), At(50, 0), At(50, 30));
        SubPath sub = path.SubPaths[0];

        // A long handle - 20 mm - but 2 degrees off the straight line back to its neighbour, which
        // from (50mm, 30mm) to (50mm, 0mm) is straight up the screen: -90 degrees.
        Point2D last = sub.Nodes[^1].Anchor;
        double radians = (-90 + 2) * Math.PI / 180.0;
        sub.Nodes[^1].InHandle = new Point2D(
            last.X + (20 * Mm * Math.Cos(radians)), last.Y + (20 * Mm * Math.Sin(radians)));

        PathCloser.Close(path);

        Assert.Equal(sub.Nodes[^1].Anchor, sub.Nodes[^1].OutHandle);
    }

    /// <summary>One close adds one segment, and no node: the shape gains an edge, not a point.</summary>
    [Fact]
    public void AddingASegmentKeepsTheNodesItHad()
    {
        PathItem path = Open(At(0, 0), At(50, 0), At(50, 30));
        SubPath sub = path.SubPaths[0];
        int nodes = sub.Nodes.Count;
        int segments = sub.SegmentCount;
        Assert.False(sub.IsClosed);

        PathCloseResult result = PathCloser.Close(path);

        Assert.Equal(PathCloseMode.SegmentAdded, result.Mode);
        Assert.Equal(nodes, sub.Nodes.Count);
        Assert.Equal(segments + 1, sub.SegmentCount);
        Assert.True(result.Changed);
    }

    // ---- the cases where nothing should happen --------------------------------

    [Fact]
    public void AnAlreadyClosedPathIsLeftAlone()
    {
        PathItem path = Open(At(0, 0), At(50, 0), At(50, 30));
        PathCloser.Close(path);

        PathCloseResult second = PathCloser.Close(path);

        Assert.Equal(PathCloseMode.AlreadyClosed, second.Mode);
        Assert.False(second.Changed);
    }

    [Fact]
    public void ATwoPointPathIsNotAShapeToClose()
    {
        PathItem path = Open(At(0, 0), At(50, 0));

        PathCloseResult result = PathCloser.Close(path);

        Assert.Equal(PathCloseMode.NotEnoughNodes, result.Mode);
        Assert.False(path.SubPaths[0].IsClosed);
    }

    /// <summary>Closing announces the geometry change, or a renderer's cached geometry goes stale.</summary>
    [Fact]
    public void ClosingBumpsTheGeometryRevision()
    {
        PathItem path = Open(At(0, 0), At(50, 0), At(50, 30));
        int before = path.GeometryRevision;

        PathCloser.Close(path);

        Assert.True(path.GeometryRevision > before);
    }
}
