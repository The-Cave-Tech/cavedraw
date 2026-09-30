using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// The nine paint shapes: closed, exact, and defined at the edges.
///
/// Exactness is the whole value of a shape library. A star whose points stop short of the radius, or
/// a polygon whose vertices sit slightly inside it, looks right in a thumbnail and is wrong in a
/// drawing - so these measure the geometry rather than looking at it.
/// </summary>
public class ShapeLibraryTests
{
    private static ShapeParameters Sized(double width = 200, double height = 200) => new()
    {
        Centre = new Point2D(50, 60),
        Width = width,
        Height = height,
    };

    private static SubPath First(PathItem path) => path.SubPaths[0];

    [Theory]
    [InlineData(ShapeKind.Rectangle)]
    [InlineData(ShapeKind.RoundedRectangle)]
    [InlineData(ShapeKind.Star)]
    [InlineData(ShapeKind.Polygon)]
    [InlineData(ShapeKind.Trapezoid)]
    [InlineData(ShapeKind.Cloud)]
    [InlineData(ShapeKind.Callout)]
    [InlineData(ShapeKind.Heart)]
    [InlineData(ShapeKind.Arrow)]
    public void EveryShapeIsAClosedPathWithAtLeastThreeSegments(ShapeKind kind)
    {
        PathItem path = ShapeLibrary.Create(kind, Sized());

        SubPath sub = Assert.Single(path.SubPaths);
        Assert.True(sub.IsClosed, $"{ShapeLibrary.Name(kind)} should be closed");
        Assert.True(sub.SegmentCount >= 3,
            $"{ShapeLibrary.Name(kind)} has only {sub.SegmentCount} segments");
    }

    /// <summary>
    /// The counts the shapes are defined by - and, for the star, the ten segments that symmetric
    /// editing works on for a five-pointed one.
    /// </summary>
    [Theory]
    [InlineData(ShapeKind.Rectangle, 4)]
    [InlineData(ShapeKind.RoundedRectangle, 8)]
    [InlineData(ShapeKind.Star, 10)]
    [InlineData(ShapeKind.Polygon, 5)]
    [InlineData(ShapeKind.Trapezoid, 4)]
    [InlineData(ShapeKind.Cloud, 10)]
    [InlineData(ShapeKind.Heart, 4)]
    [InlineData(ShapeKind.Arrow, 7)]
    public void EachShapeHasTheSegmentCountItIsDefinedBy(ShapeKind kind, int expected)
    {
        Assert.Equal(expected, First(ShapeLibrary.Create(kind, Sized())).SegmentCount);
    }

    /// <summary>A star's points reach the radius and its valleys reach the inner one.</summary>
    [Fact]
    public void AStarsPointsAndValleysSitOnTheirRadii()
    {
        var parameters = Sized() with { Points = 5, InnerRatio = 0.5 };
        SubPath sub = First(ShapeLibrary.Create(ShapeKind.Star, parameters));
        Point2D centre = parameters.Centre;

        for (int i = 0; i < sub.Nodes.Count; i++)
        {
            Point2D node = sub.Nodes[i].Anchor;
            bool outer = i % 2 == 0;
            // Vertices start at the top, so the radius is measured against the node's own angle.
            double angle = (Math.PI * i / 5) - (Math.PI / 2);
            double expectedX = centre.X + (100 * (outer ? 1 : 0.5) * Math.Cos(angle));
            double expectedY = centre.Y + (100 * (outer ? 1 : 0.5) * Math.Sin(angle));

            Assert.Equal(expectedX, node.X, 6);
            Assert.Equal(expectedY, node.Y, 6);
        }
    }

    [Fact]
    public void APolygonsVerticesSitOnTheRadius()
    {
        var parameters = Sized() with { Points = 6 };
        SubPath sub = First(ShapeLibrary.Create(ShapeKind.Polygon, parameters));

        foreach (PathNode node in sub.Nodes)
        {
            double dx = node.Anchor.X - parameters.Centre.X;
            double dy = node.Anchor.Y - parameters.Centre.Y;
            Assert.Equal(100.0, Math.Sqrt((dx * dx) + (dy * dy)), 6);
        }
    }

    /// <summary>
    /// The box-shaped kinds fill exactly the box they were asked for.
    ///
    /// Stars and polygons are not here because they are **inscribed** in that box rather than filling
    /// it: a five-pointed star's points reach the radius but it is narrower than its box left to
    /// right, as every five-pointed star is. Asserting otherwise would be asserting that the star is
    /// wrong. `AStarsPointsAndValleysSitOnTheirRadii` is where that is pinned.
    /// </summary>
    [Theory]
    [InlineData(ShapeKind.Rectangle)]
    [InlineData(ShapeKind.Trapezoid)]
    [InlineData(ShapeKind.Arrow)]
    public void TheShapeFillsTheSizeItWasGiven(ShapeKind kind)
    {
        var parameters = Sized(200, 120);
        Rect2D bounds = ShapeLibrary.Create(kind, parameters).BoundingBox();

        Assert.Equal(parameters.Centre.X - 100, bounds.Left, 3);
        Assert.Equal(parameters.Centre.Y - 60, bounds.Top, 3);
        Assert.Equal(200, bounds.Width, 3);
        Assert.Equal(120, bounds.Height, 3);
    }

    /// <summary>Rotating by a quarter turn swaps the bounds, which is what rotation means.</summary>
    [Fact]
    public void RotationTurnsTheShapeInPlace()
    {
        var upright = Sized(200, 100);
        var turned = upright with { Rotation = 90 };

        Rect2D before = ShapeLibrary.Create(ShapeKind.Trapezoid, upright).BoundingBox();
        Rect2D after = ShapeLibrary.Create(ShapeKind.Trapezoid, turned).BoundingBox();

        Assert.Equal(before.Height, after.Width, 3);
        Assert.Equal(before.Width, after.Height, 3);
        Assert.Equal(before.Center.X, after.Center.X, 3);
        Assert.Equal(before.Center.Y, after.Center.Y, 3);
    }

    /// <summary>
    /// The same parameters produce the same geometry, so an export is reproducible and a round trip
    /// can compare equal.
    /// </summary>
    [Theory]
    [InlineData(ShapeKind.RoundedRectangle)]
    [InlineData(ShapeKind.Cloud)]
    [InlineData(ShapeKind.Heart)]
    [InlineData(ShapeKind.Callout)]
    public void TheSameParametersProduceIdenticalGeometry(ShapeKind kind)
    {
        var parameters = Sized() with { HasTail = true, Tail = new Point2D(10, 200) };

        PathItem a = ShapeLibrary.Create(kind, parameters);
        PathItem b = ShapeLibrary.Create(kind, parameters);

        Assert.Equal(a.SubPaths[0].Nodes.Count, b.SubPaths[0].Nodes.Count);
        for (int i = 0; i < a.SubPaths[0].Nodes.Count; i++)
        {
            Assert.Equal(a.SubPaths[0].Nodes[i].Anchor, b.SubPaths[0].Nodes[i].Anchor);
            Assert.Equal(a.SubPaths[0].Nodes[i].InHandle, b.SubPaths[0].Nodes[i].InHandle);
            Assert.Equal(a.SubPaths[0].Nodes[i].OutHandle, b.SubPaths[0].Nodes[i].OutHandle);
        }
    }

    /// <summary>
    /// Degenerate input is defined rather than fatal: a shape nobody could have meant is clamped to
    /// the nearest one they could.
    /// </summary>
    [Fact]
    public void DegenerateParametersAreClampedRatherThanCrashing()
    {
        // A zero-sized shape is still a shape.
        PathItem empty = ShapeLibrary.Create(ShapeKind.Star, new ShapeParameters { Width = 0, Height = 0 });
        Assert.True(First(empty).IsClosed);
        Assert.True(First(empty).SegmentCount >= 3);

        // Fewer than three points is not a star; it becomes the smallest one that is.
        Assert.Equal(6, First(ShapeLibrary.Create(ShapeKind.Star, Sized() with { Points = 1 })).SegmentCount);

        // An inner radius past the outer one would turn the star inside out.
        var inside = Sized() with { InnerRatio = 5 };
        Rect2D clamped = ShapeLibrary.Create(ShapeKind.Star, inside).BoundingBox();
        Assert.True(clamped.Width <= 200.01, $"the star grew to {clamped.Width}");

        // A corner radius larger than the side it turns is clamped to half the shorter side.
        var fat = Sized(100, 60) with { CornerRadius = 500 };
        Rect2D rounded = ShapeLibrary.Create(ShapeKind.RoundedRectangle, fat).BoundingBox();
        Assert.Equal(100, rounded.Width, 3);
        Assert.Equal(60, rounded.Height, 3);
    }

    /// <summary>A callout without a tail is its box; with one, the tail is part of the same outline.</summary>
    [Fact]
    public void ACalloutGrowsATailWhenOneIsPlaced()
    {
        PathItem box = ShapeLibrary.Create(ShapeKind.Callout, Sized());
        var withTail = Sized() with { HasTail = true, Tail = new Point2D(50, 260) };
        PathItem tailed = ShapeLibrary.Create(ShapeKind.Callout, withTail);

        Assert.True(First(tailed).Nodes.Count > First(box).Nodes.Count);
        Assert.True(First(tailed).IsClosed);
        Assert.Equal(260, tailed.BoundingBox().Bottom, 3);
    }

    /// <summary>
    /// A rounded rectangle's corners are quarter circles about the corner centres.
    ///
    /// This is the test that would have caught a corner built from the wrong tangents: the shape still
    /// had eight segments and the right bounding box, so it passed everything else while drawing a
    /// spiky quadrilateral. A point on a quarter circle is a fact, and measuring it is the difference
    /// between a rounded rectangle and something that merely has eight nodes.
    /// </summary>
    [Fact]
    public void ARoundedRectanglesCornersAreQuarterCircles()
    {
        double radius = 24;
        var parameters = new ShapeParameters
        {
            Centre = new Point2D(0, 0),
            Width = 300,
            Height = 200,
            CornerRadius = radius,
        };

        SubPath sub = First(ShapeLibrary.Create(ShapeKind.RoundedRectangle, parameters));
        Assert.Equal(8, sub.Nodes.Count);

        // The arc's CENTRE is the inner point, one radius in from each edge of the corner - not the
        // corner of the box. Every point of it is `radius` from there, which is the fact being checked.
        (Point2D Centre, int Segment)[] corners =
        {
            (new Point2D(-150 + radius, -100 + radius), 0),
            (new Point2D(150 - radius, -100 + radius), 2),
            (new Point2D(150 - radius, 100 - radius), 4),
            (new Point2D(-150 + radius, 100 - radius), 6),
        };

        foreach ((Point2D corner, int segment) in corners)
        {
            int count = sub.Nodes.Count;
            PathNode a = sub.Nodes[segment];
            PathNode b = sub.Nodes[(segment + 1) % count];

            var middle = new Point2D(
                (a.Anchor.X + (3 * a.OutHandle.X) + (3 * b.InHandle.X) + b.Anchor.X) / 8,
                (a.Anchor.Y + (3 * a.OutHandle.Y) + (3 * b.InHandle.Y) + b.Anchor.Y) / 8);

            double distance = Math.Sqrt(
                Math.Pow(middle.X - corner.X, 2) + Math.Pow(middle.Y - corner.Y, 2));

            Assert.Equal(radius, distance, 1);
        }
    }
    /// <summary>Every kind has a name for a person and an operation to use.</summary>
    [Fact]
    public void EveryKindIsNamed()
    {
        foreach (ShapeKind kind in ShapeLibrary.All)
        {
            string name = ShapeLibrary.Name(kind);
            Assert.False(string.IsNullOrWhiteSpace(name));
            Assert.NotEqual("shape", name);
        }

        Assert.Equal(9, ShapeLibrary.All.Count);
    }
}
