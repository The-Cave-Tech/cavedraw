using VCCad.Geometry;
using Xunit;

namespace VCCad.Geometry.Tests;

public class Rect2DTests
{
    [Fact]
    public void FromPointsNormalizesCornerOrder()
    {
        Rect2D r = Rect2D.FromPoints(new Point2D(5, 7), new Point2D(1, 2));
        Assert.Equal(1.0, r.X, 12);
        Assert.Equal(2.0, r.Y, 12);
        Assert.Equal(4.0, r.Width, 12);
        Assert.Equal(5.0, r.Height, 12);
    }

    [Fact]
    public void UnionIncludesBothRects()
    {
        var a = new Rect2D(0, 0, 10, 10);
        var b = new Rect2D(20, 30, 5, 5);
        Rect2D u = a.Union(b);
        Assert.Equal(0.0, u.Left, 12);
        Assert.Equal(0.0, u.Top, 12);
        Assert.Equal(25.0, u.Right, 12);
        Assert.Equal(35.0, u.Bottom, 12);
    }

    [Fact]
    public void ContainsPointAndRect()
    {
        var r = new Rect2D(0, 0, 10, 10);
        Assert.True(r.Contains(new Point2D(5, 5)));
        Assert.True(r.Contains(new Point2D(0, 0)));   // edges are inclusive
        Assert.False(r.Contains(new Point2D(10.5, 5)));
        Assert.True(r.Contains(new Rect2D(2, 2, 3, 3)));
        Assert.False(r.Contains(new Rect2D(2, 2, 20, 3)));
    }

    [Fact]
    public void InflateGrowsSymmetrically()
    {
        var r = new Rect2D(0, 0, 10, 10).Inflated(2);
        Assert.Equal(-2.0, r.X, 12);
        Assert.Equal(14.0, r.Width, 12);
    }

    [Fact]
    public void IntersectionEmptyWhenDisjoint()
    {
        var a = new Rect2D(0, 0, 10, 10);
        var b = new Rect2D(100, 100, 5, 5);
        Assert.True(a.Intersect(b).IsEmpty);
        Assert.False(a.Intersects(b));
    }

    [Fact]
    public void CenterIsMidpoint()
    {
        var r = new Rect2D(2, 4, 8, 10);
        Assert.Equal(6.0, r.Center.X, 12);
        Assert.Equal(9.0, r.Center.Y, 12);
    }
}
