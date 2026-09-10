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
    public void ScaleAboutCornerKeepsReferenceStationary()
    {
        PathItem square = PathFactory.CreateRectangle("sq", new Rect2D(0, 0, 100, 100));
        square.ScaleGeometryAbout(new Point2D(0, 0), 0.5, 0.5);

        Assert.True(square.SubPaths[0].Nodes[0].Anchor.NearlyEquals(new Point2D(0, 0), 1e-9));
        Assert.True(square.SubPaths[0].Nodes[1].Anchor.NearlyEquals(new Point2D(50, 0), 1e-9));
        Rect2D box = square.BoundingBox();
        Assert.Equal(50.0, box.Width, 9);
        Assert.Equal(50.0, box.Height, 9);
    }

    [Fact]
    public void InsertNodeOnStraightSegmentSplitsWithoutChangingShape()
    {
        PathItem rect = PathFactory.CreateRectangle("r", new Rect2D(0, 0, 100, 50));
        SubPath sub = rect.SubPaths[0];
        Rect2D before = rect.BoundingBox();

        PathNode inserted = sub.InsertNodeOnSegment(0, 0.5);

        Assert.Equal(5, sub.Nodes.Count);
        Assert.Equal(5, sub.SegmentCount);
        Assert.True(inserted.Anchor.NearlyEquals(new Point2D(50, 0), 1e-9));
        // Splitting a straight line leaves the outline unchanged.
        Assert.True(rect.BoundingBox().NearlyEquals(before, 1e-9));
    }

    [Fact]
    public void InsertNodeOnBezierSplicesExactly()
    {
        var curve = new PathItem();
        SubPath sub = curve.AddSubPath(closed: false);
        var a = sub.AppendNode(new Point2D(0, 0));
        var b = sub.AppendNode(new Point2D(200, 0));
        a.OutHandle = new Point2D(60, 160);
        b.InHandle = new Point2D(140, -160);

        CubicBezier original = sub.GetSegment(0);
        PathNode inserted = sub.InsertNodeOnSegment(0, 0.4);

        // The new node lies exactly on the original curve…
        Point2D expected = original.PointAt(0.4);
        Assert.True(inserted.Anchor.NearlyEquals(expected, 1e-9));

        // …and the two replacement segments reproduce it (sample mid-points).
        CubicBezier left = sub.GetSegment(0);
        CubicBezier right = sub.GetSegment(1);
        Assert.True(left.PointAt(0.5).NearlyEquals(original.PointAt(0.2), 1e-6));
        Assert.True(right.PointAt(0.5).NearlyEquals(original.PointAt(0.7), 1e-6));
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
