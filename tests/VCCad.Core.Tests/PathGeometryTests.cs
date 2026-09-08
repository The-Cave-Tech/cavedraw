using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

public class PathGeometryTests
{
    [Fact]
    public void RectangleHasFourStraightSegments()
    {
        PathItem rect = PathFactory.CreateRectangle("r", new Rect2D(0, 0, 100, 50));
        SubPath sub = rect.SubPaths[0];

        Assert.True(sub.IsClosed);
        Assert.Equal(4, sub.Nodes.Count);
        Assert.Equal(4, sub.SegmentCount);

        foreach (CubicBezier seg in sub.Segments())
        {
            // A straight segment is a cubic with collapsed handles: assert the
            // control points coincide with the endpoints.
            Assert.True(seg.P1.NearlyEquals(seg.P0));
            Assert.True(seg.P2.NearlyEquals(seg.P3));
        }

        Assert.Equal(new Rect2D(0, 0, 100, 50), rect.BoundingBox());
    }

    [Fact]
    public void OpenPolylineSegmentCountIsNodesMinusOne()
    {
        PathItem poly = PathFactory.CreatePolyline("p", new[]
        {
            new Point2D(0, 0),
            new Point2D(10, 0),
            new Point2D(20, 10),
        });
        SubPath sub = poly.SubPaths[0];
        Assert.False(sub.IsClosed);
        Assert.Equal(3, sub.Nodes.Count);
        Assert.Equal(2, sub.SegmentCount);
        Assert.False(poly.IsFullyClosed);
    }

    [Fact]
    public void ClosedRectangleLengthIsPerimeter()
    {
        PathItem rect = PathFactory.CreateRectangle("r", new Rect2D(0, 0, 100, 50));
        Assert.Equal(300.0, rect.SubPaths[0].EstimateLength(), 4);
    }

    [Fact]
    public void EllipseIsFourSegmentsWithExpectedExtent()
    {
        PathItem ellipse = PathFactory.CreateEllipse("e", new Point2D(0, 0), 100, 50);
        SubPath sub = ellipse.SubPaths[0];
        Assert.Equal(4, sub.SegmentCount);
        Assert.True(sub.IsClosed);

        Rect2D box = ellipse.BoundingBox();
        Assert.Equal(200.0, box.Width, 3);
        Assert.Equal(100.0, box.Height, 3);

        // Every anchor must sit exactly on the ellipse boundary.
        foreach (PathNode node in sub.Nodes)
        {
            double xr = node.Anchor.X / 100.0;
            double yr = node.Anchor.Y / 50.0;
            Assert.Equal(1.0, xr * xr + yr * yr, 9);
        }
    }

    [Fact]
    public void SubPathGetSegmentIndexesFromStart()
    {
        SubPath sub = PathFactory.CreateRectangle("r", new Rect2D(0, 0, 10, 10)).SubPaths[0];
        // Segment 0 runs from the first node to the second: top-left → top-right.
        Assert.Equal(new Point2D(0, 0), sub.GetSegment(0).P0);
        Assert.Equal(new Point2D(10, 0), sub.GetSegment(0).P3);
        // Closing segment wraps from the last node back to the first.
        Assert.Equal(new Point2D(0, 10), sub.GetSegment(3).P0);
        Assert.Equal(new Point2D(0, 0), sub.GetSegment(3).P3);
        Assert.Throws<ArgumentOutOfRangeException>(() => sub.GetSegment(4));
    }

    [Fact]
    public void CurvedSegmentHasDistinctHandles()
    {
        var sub = new SubPath { IsClosed = false };
        var start = new PathNode(new Point2D(0, 0));
        var end = new PathNode(new Point2D(10, 0));
        start.OutHandle = new Point2D(5, -20);  // control pulled up-out
        end.InHandle = new Point2D(8, 10);
        sub.Nodes.Add(start);
        sub.Nodes.Add(end);

        CubicBezier seg = sub.GetSegment(0);
        Assert.Equal(new Point2D(5, -20), seg.P1);
        Assert.Equal(new Point2D(8, 10), seg.P2);
        Assert.Equal(new Point2D(0, 0), seg.P0);
        Assert.Equal(new Point2D(10, 0), seg.P3);
        // The bend above the baseline must be reflected in the tight bounds.
        Assert.True(sub.BoundingBox().Top < -1.0);
    }
}
