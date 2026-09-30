using VCCad.Core.Model;
using VCCad.Core.Picking;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

public class PathPickingTests
{
    [Fact]
    public void ARectangleIsHitOnItsOutlineAndNotInsideItsFill()
    {
        var rect = PathFactory.CreateRectangle("r", new Rect2D(0, 0, 100, 50));
        rect.Fill = FillSpec.Solid(ColorRgb.Red);
        rect.Stroke = StrokeSpec.None;

        // The middle of the fill is not the path; its edge is.
        Assert.Equal(PickKind.None, PathPicking.HitTest(rect, new Point2D(50, 25), 2.0));
        Assert.Equal(PickKind.Outline, PathPicking.HitTest(rect, new Point2D(50, 0), 2.0));
        Assert.Equal(PickKind.None, PathPicking.HitTest(rect, new Point2D(200, 200), 2.0));

        // A caller that wants "which shape contains this point" can still ask for the fill.
        Assert.Equal(PickKind.Fill,
            PathPicking.HitTest(rect, new Point2D(50, 25), 2.0, pickInsideFill: true));
    }

    [Fact]
    public void OutlineStrokeIsPickableOnOpenPath()
    {
        var line = PathFactory.CreateLine("l", new Point2D(0, 0), new Point2D(100, 0));
        line.Stroke = StrokeSpec.Hairline(ColorRgb.Black);

        // 1.5 pt from the middle of the segment is within tolerance.
        Assert.Equal(PickKind.Outline, PathPicking.HitTest(line, new Point2D(50, 1.5), 2.0));
        Assert.Equal(PickKind.None, PathPicking.HitTest(line, new Point2D(50, 5.0), 2.0));
    }

    [Fact]
    public void FillRespectsEvenOddRuleForHole()
    {
        // Donut: outer square with an inner square hole.
        var donut = new PathItem();
        SubPath outer = donut.AddSubPath(closed: true);
        outer.AppendNode(new Point2D(0, 0));
        outer.AppendNode(new Point2D(100, 0));
        outer.AppendNode(new Point2D(100, 100));
        outer.AppendNode(new Point2D(0, 100));

        SubPath hole = donut.AddSubPath(closed: true);
        hole.AppendNode(new Point2D(40, 40));
        hole.AppendNode(new Point2D(60, 40));
        hole.AppendNode(new Point2D(60, 60));
        hole.AppendNode(new Point2D(40, 60));

        donut.Fill = FillSpec.Solid(ColorRgb.Red, FillRule.EvenOdd);

        Assert.True(PathPicking.FillContains(donut, new Point2D(20, 50)), "Ring material is inside.");
        Assert.False(PathPicking.FillContains(donut, new Point2D(50, 50)), "The hole is outside.");
        Assert.False(PathPicking.FillContains(donut, new Point2D(200, 200)), "Far away is outside.");
    }

    [Fact]
    public void NonZeroRuleLeavesOppositeWoundHoleEmpty()
    {
        // Under NonZero a hole is cut by winding it opposite to the outer contour
        // (the Illustrator/PDF convention), so the ring fills and the hole does not.
        var donut = new PathItem();
        SubPath outer = donut.AddSubPath(closed: true);
        outer.AppendNode(new Point2D(0, 0));
        outer.AppendNode(new Point2D(100, 0));
        outer.AppendNode(new Point2D(100, 100));
        outer.AppendNode(new Point2D(0, 100));

        SubPath hole = donut.AddSubPath(closed: true);
        hole.AppendNode(new Point2D(40, 40));
        hole.AppendNode(new Point2D(40, 60)); // opposite winding to the outer square
        hole.AppendNode(new Point2D(60, 60));
        hole.AppendNode(new Point2D(60, 40));

        donut.Fill = FillSpec.Solid(ColorRgb.Red, FillRule.NonZero);
        Assert.True(PathPicking.FillContains(donut, new Point2D(20, 50)), "Ring material fills.");
        Assert.False(PathPicking.FillContains(donut, new Point2D(50, 50)), "The hole stays empty.");
    }

    [Fact]
    public void PickNodePrefersAnchorAndFindsHandles()
    {
        var wave = new PathItem();
        SubPath sub = wave.AddSubPath(closed: false);
        var start = sub.AppendNode(new Point2D(0, 0));
        var end = sub.AppendNode(new Point2D(100, 0));
        start.OutHandle = new Point2D(30, -60);
        end.InHandle = new Point2D(70, -60);

        NodePick? anchor = PathPicking.PickNode(wave, new Point2D(0.5, 0.5), 2.0);
        Assert.NotNull(anchor);
        Assert.Equal(0, anchor.Value.NodeIndex);
        Assert.False(anchor.Value.IsInHandle || anchor.Value.IsOutHandle);

        NodePick? outHandle = PathPicking.PickNode(wave, new Point2D(30, -60), 2.0);
        Assert.NotNull(outHandle);
        Assert.True(outHandle.Value.IsOutHandle);

        NodePick? inHandle = PathPicking.PickNode(wave, new Point2D(70, -60), 2.0);
        Assert.NotNull(inHandle);
        Assert.True(inHandle.Value.IsInHandle);

        Assert.Null(PathPicking.PickNode(wave, new Point2D(500, 500), 2.0));
    }

    [Fact]
    public void AnchorWinsWhenHandleCoincidesWithEndpoint()
    {
        // A handle pulled out almost onto its own anchor: clicking the endpoint
        // must grab the anchor, not the coincident control point.
        var wave = new PathItem();
        SubPath sub = wave.AddSubPath(closed: false);
        PathNode start = sub.AppendNode(new Point2D(0, 0));
        sub.AppendNode(new Point2D(100, 0));
        start.OutHandle = new Point2D(0.2, 0); // within picking tolerance of (0,0)

        NodePick? pick = PathPicking.PickNode(wave, new Point2D(0, 0), 2.0);
        Assert.NotNull(pick);
        Assert.Equal(0, pick.Value.NodeIndex);
        Assert.False(pick.Value.IsInHandle || pick.Value.IsOutHandle); // an anchor, not a handle
    }

    [Fact]
    public void FillContainsOpenPathNeverReturnsTrue()
    {
        var open = PathFactory.CreatePolyline("o", new[]
        {
            new Point2D(0, 0),
            new Point2D(100, 0),
            new Point2D(100, 100),
            new Point2D(0, 100),
        });
        open.Fill = FillSpec.Solid(ColorRgb.Red);
        Assert.False(PathPicking.FillContains(open, new Point2D(50, 50)));
    }
}
