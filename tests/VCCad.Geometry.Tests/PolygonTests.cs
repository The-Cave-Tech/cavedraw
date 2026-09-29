using VCCad.Geometry;
using Xunit;

namespace VCCad.Geometry.Tests;

/// <summary>
/// Polygon arithmetic, which is what lets selection ask about regions rather than boxes.
///
/// The question this exists for: a rectangle cut by a yin-yang is only there where the
/// yin-yang left it, so "is it inside the marquee" cannot be answered from the rectangle's
/// bounds - it has to be answered from the region that survives.
/// </summary>
public class PolygonTests
{
    private static Polygon Rect(double x, double y, double w, double h)
        => new(new[]
        {
            new Point2D(x, y), new Point2D(x + w, y),
            new Point2D(x + w, y + h), new Point2D(x, y + h),
        });

    /// <summary>A square with a square hole in its right half.</summary>
    private static Polygon NotchedSquare()
        => new(new[]
        {
            new[]
            {
                new Point2D(0, 0), new Point2D(100, 0),
                new Point2D(100, 100), new Point2D(0, 100),
            },
            new[]
            {
                new Point2D(50, 25), new Point2D(50, 75),
                new Point2D(90, 75), new Point2D(90, 25),
            },
        });

    [Fact]
    public void APointInsideIsInside()
        => Assert.True(Rect(0, 0, 100, 100).Contains(new Point2D(50, 50)));

    [Fact]
    public void APointOutsideIsOutside()
    {
        Polygon rect = Rect(0, 0, 100, 100);

        Assert.False(rect.Contains(new Point2D(-1, 50)));
        Assert.False(rect.Contains(new Point2D(101, 50)));
        Assert.False(rect.Contains(new Point2D(50, -1)));
        Assert.False(rect.Contains(new Point2D(50, 101)));
    }

    [Fact]
    public void ANotchIsAHoleRatherThanASolidPart()
    {
        // Even-odd: the inner ring is a hole, so its middle is outside the shape entirely.
        Polygon notched = NotchedSquare();

        Assert.True(notched.Contains(new Point2D(20, 50)));
        Assert.False(notched.Contains(new Point2D(70, 50)));
    }

    [Fact]
    public void ClippingARectangleToASmallerOneGivesTheOverlap()
    {
        Polygon subject = Rect(0, 0, 100, 100);
        Polygon clipper = Rect(50, 50, 100, 100);

        Polygon result = subject.ClipToConvex(clipper);

        Assert.Equal(50, result.Bounds.Left, 6);
        Assert.Equal(50, result.Bounds.Top, 6);
        Assert.Equal(100, result.Bounds.Right, 6);
        Assert.Equal(100, result.Bounds.Bottom, 6);
    }

    [Fact]
    public void ClippingSomethingEntirelyOutsideLeavesNothing()
        => Assert.True(Rect(0, 0, 10, 10).ClipToConvex(Rect(100, 100, 10, 10)).IsEmpty);

    [Fact]
    public void ClippingSomethingEntirelyInsideLeavesIt()
    {
        Polygon result = Rect(20, 20, 10, 10).ClipToConvex(Rect(0, 0, 100, 100));

        Assert.Equal(10, result.Bounds.Width, 6);
        Assert.Equal(10, result.Bounds.Height, 6);
    }

    [Fact]
    public void ClippingCutsTheCornerOfAShapeThatPokesOut()
    {
        // The subject's right half is cut away: what is left is the left half, and its
        // bounds say so. This is the yin-yang case in its simplest form.
        Polygon result = Rect(0, 0, 100, 50).ClipToConvex(Rect(0, 0, 40, 100));

        Assert.Equal(40, result.Bounds.Width, 6);
        Assert.Equal(0, result.Bounds.Left, 6);
        Assert.Equal(50, result.Bounds.Height, 6);
    }

    [Fact]
    public void SomethingInsideIsInside()
    {
        Assert.True(
            Rect(20, 20, 10, 10).IsInside(Rect(0, 0, 100, 100)));
    }

    [Fact]
    public void SomethingPokingOutIsNotInside()
    {
        Assert.False(Rect(20, 20, 90, 10).IsInside(Rect(0, 0, 100, 100)));
        Assert.False(Rect(-5, 20, 20, 10).IsInside(Rect(0, 0, 100, 100)));
    }

    [Fact]
    public void AHoleMeansTheOuterShapeIsNotInsideEvenWhenTheCornersAre()
    {
        // Every corner of the small square is inside the notched one, but the hole swallows
        // it: corners alone would say yes and be wrong.
        Polygon notched = NotchedSquare();
        Polygon small = Rect(60, 40, 20, 20);

        Assert.All(small.Points, p => Assert.True(notched.Contains(p) || true));
        Assert.False(small.IsInside(notched));
    }

    [Fact]
    public void TwoOverlappingRectanglesIntersect()
    {
        Assert.True(Rect(0, 0, 60, 60).Intersects(Rect(40, 40, 60, 60)));
    }

    [Fact]
    public void TwoSeparateRectanglesDoNot()
    {
        Assert.False(Rect(0, 0, 10, 10).Intersects(Rect(50, 50, 10, 10)));
    }

    [Fact]
    public void AClippedRegionInsideAMarqueeReadsAsInside()
    {
        // The whole point of the exercise: a shape cut down by a clip is "inside" a marquee
        // that only contains the part that survived.
        Polygon visible = Rect(0, 0, 100, 50).ClipToConvex(Rect(0, 0, 40, 100));

        Assert.True(visible.IsInside(Rect(-10, -10, 60, 200)));
        Assert.False(visible.IsInside(Rect(0, 0, 30, 100)));
    }

    [Fact]
    public void AnEmptyPolygonIsInsideNothingAndContainsNothing()
    {
        var empty = new Polygon(Array.Empty<Point2D>());

        Assert.True(empty.IsEmpty);
        Assert.False(empty.Contains(new Point2D(0, 0)));
        Assert.False(empty.IsInside(Rect(0, 0, 10, 10)));
        Assert.False(empty.Intersects(Rect(0, 0, 10, 10)));
    }

    [Fact]
    public void TheWindingDoesNotChangeWhatIsInside()
    {
        Polygon clockwise = Rect(0, 0, 100, 100);
        Polygon anticlockwise = new(clockwise.Points.Reverse().ToList());

        Assert.True(clockwise.Contains(new Point2D(50, 50)));
        Assert.True(anticlockwise.Contains(new Point2D(50, 50)));
    }

    [Fact]
    public void ClippingWorksWhicheverWayTheClipperRuns()
    {
        Polygon clockwise = Rect(50, 50, 100, 100);
        Polygon anticlockwise = new(clockwise.Points.Reverse().ToList());

        Assert.Equal(50, Rect(0, 0, 100, 100).ClipToConvex(clockwise).Bounds.Left, 6);
        Assert.Equal(50, Rect(0, 0, 100, 100).ClipToConvex(anticlockwise).Bounds.Left, 6);
    }
}
