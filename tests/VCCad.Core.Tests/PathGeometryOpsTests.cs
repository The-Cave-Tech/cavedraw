using VCCad.Core.Model;
using VCCad.Core.Picking;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

public class PathGeometryOpsTests
{
    [Fact]
    public void SegmentEndNodesHandleClosedWrap()
    {
        SubPath sub = PathFactory.CreateRectangle("r", new Rect2D(0, 0, 10, 10)).SubPaths[0];
        // Segment 0 runs node0→node1; the final (index 3) segment wraps to node 0.
        Assert.Equal((0, 1), sub.SegmentEndNodes(0));
        Assert.Equal((2, 3), sub.SegmentEndNodes(2));
        Assert.Equal((3, 0), sub.SegmentEndNodes(3));
        Assert.Throws<ArgumentOutOfRangeException>(() => sub.SegmentEndNodes(4));
    }

    [Fact]
    public void TranslateSegmentMovesOnlyItsTwoEndNodes()
    {
        // Open polyline of three nodes A—B—C. Moving segment B—C must touch B and
        // C but leave A alone.
        PathItem poly = PathFactory.CreatePolyline("p", new[]
        {
            new Point2D(0, 0),
            new Point2D(100, 0),
            new Point2D(200, 50),
        });
        SubPath sub = poly.SubPaths[0];
        poly.TranslateSegmentBy(sub, 1, new Vector2D(0, 20));

        Assert.Equal(new Point2D(0, 0), sub.Nodes[0].Anchor);
        Assert.Equal(new Point2D(100, 20), sub.Nodes[1].Anchor);
        Assert.Equal(new Point2D(200, 70), sub.Nodes[2].Anchor);
    }

    [Fact]
    public void RotateGeometryAboutCenterQuarterTurnsSquareOntoItself()
    {
        PathItem square = PathFactory.CreateRectangle("sq", new Rect2D(0, 0, 100, 100));
        var center = new Point2D(50, 50);

        square.RotateGeometryAbout(center, Math.PI / 2);

        // Node0 (0,0) rotated 90° about the centre lands on (100,0) (screen CCW).
        Assert.True(square.SubPaths[0].Nodes[0].Anchor.NearlyEquals(new Point2D(100, 0), 1e-6));
        // The square is invariant under the quarter turn, so bounds are unchanged.
        Assert.Equal(100.0, square.BoundingBox().Width, 9);
        Assert.Equal(100.0, square.BoundingBox().Height, 9);
    }

    [Fact]
    public void ClosestSegmentFindsThePickedSegment()
    {
        // Path of two collinear runs: horizontal from (0,0) and a vertical tail.
        PathItem zig = PathFactory.CreatePolyline("z", new[]
        {
            new Point2D(0, 0),
            new Point2D(100, 0),
            new Point2D(100, 100),
        });

        // Query near the vertical tail should pick segment 1…
        SegmentPick? pick = PathPicking.ClosestSegment(zig, new Point2D(100.8, 50), 2.0);
        Assert.NotNull(pick);
        Assert.Equal(1, pick.Value.SegmentIndex);

        // …and a query beside the horizontal run picks segment 0.
        SegmentPick? horizontal = PathPicking.ClosestSegment(zig, new Point2D(50, -1.2), 2.0);
        Assert.NotNull(horizontal);
        Assert.Equal(0, horizontal.Value.SegmentIndex);

        Assert.Null(PathPicking.ClosestSegment(zig, new Point2D(500, 500), 2.0));
    }
}
