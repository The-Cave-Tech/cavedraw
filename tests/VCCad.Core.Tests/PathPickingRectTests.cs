using VCCad.Core.Model;
using VCCad.Core.Picking;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// Rubber-band selection must ask about geometry, not bounding boxes: on a tiled pattern
/// a diagonal line's box covers a quarter of the page, so a box test selects it for any
/// rectangle that overlaps the box at all.
/// </summary>
public class PathPickingRectTests
{
    /// <summary>A single straight diagonal stroke from (0,0) to (100,100).</summary>
    private static PathItem Diagonal()
    {
        var item = new PathItem { Name = "diagonal" };
        var sub = new SubPath();
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(100, 100)));
        item.SubPaths.Add(sub);
        item.Fill = FillSpec.None;
        item.Stroke = new StrokeSpec(true, ColorRgb.Black, 1, StrokeCap.Butt, StrokeJoin.Miter,
            4, StrokeAlignment.Center);
        return item;
    }

    [Fact]
    public void ARectangleOnTheLineMeetsIt()
    {
        PathItem path = Diagonal();
        Assert.True(PathPicking.IntersectsRect(path, new Rect2D(40, 40, 20, 20)));
    }

    [Fact]
    public void ARectangleTouchingOnlyTheBoundingBoxDoesNot()
    {
        // (80, 0)-(95, 15) is inside the line's bounding box but nowhere near the line.
        PathItem path = Diagonal();
        Assert.False(PathPicking.IntersectsRect(path, new Rect2D(80, 0, 15, 15)));
    }

    [Fact]
    public void ARectangleCrossingTheLineMeetsIt()
    {
        // Straddles the diagonal without any corner being on it.
        PathItem path = Diagonal();
        Assert.True(PathPicking.IntersectsRect(path, new Rect2D(10, 40, 80, 5)));
    }

    [Fact]
    public void ARectangleInsideAFilledShapeMeetsIt()
    {
        var item = new PathItem { Name = "square" };
        var sub = new SubPath { IsClosed = true };
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(100, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(100, 100)));
        sub.Nodes.Add(new PathNode(new Point2D(0, 100)));
        item.SubPaths.Add(sub);
        item.Fill = FillSpec.Solid(ColorRgb.Black);
        item.Stroke = StrokeSpec.None;

        Assert.True(PathPicking.IntersectsRect(item, new Rect2D(40, 40, 10, 10)));
        Assert.False(PathPicking.IntersectsRect(item, new Rect2D(200, 200, 10, 10)));
    }
}
