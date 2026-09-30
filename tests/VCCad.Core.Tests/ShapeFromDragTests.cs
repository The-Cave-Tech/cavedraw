using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// Creating a shape from the parameters a drag produces: a centre and a box, and nothing else.
///
/// The shape library's own tests build shapes with parameters they choose; this is the path the canvas
/// takes - a drag gives a box, so every kind has to be creatable from just a centre and a width and height,
/// at a size that is not square. A non-square box is the case that catches a builder that assumes its two
/// extents are equal.
/// </summary>
public class ShapeFromDragTests
{
    /// <summary>Every kind builds from a centre and a non-square box, with a sensible outline.</summary>
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
    public void EveryKindBuildsFromADragBox(ShapeKind kind)
    {
        var parameters = new ShapeParameters
        {
            Centre = new Point2D(300, 260),
            Width = 200,
            Height = 120,
        };

        PathItem shape = ShapeLibrary.Create(kind, parameters);

        Assert.Equal(kind, shape.Shape!.Kind);
        Assert.NotEmpty(shape.SubPaths);

        Rect2D box = shape.BoundingBox();
        Assert.True(box.Width > 0 && box.Height > 0,
            $"{kind} came out with an empty outline: {box.Width}x{box.Height}");
        Assert.True(box.Width <= 260 && box.Height <= 180,
            $"{kind} came out far larger than the box it was given: {box.Width}x{box.Height}");
    }

    /// <summary>The shape knows which box it was made for, which is what its handles are positioned from.</summary>
    [Fact]
    public void TheShapeKeepsTheBoxItWasGiven()
    {
        PathItem shape = ShapeLibrary.Create(ShapeKind.Star, new ShapeParameters
        {
            Centre = new Point2D(300, 260),
            Width = 200,
            Height = 120,
        });

        Assert.Equal(200, shape.Shape!.Parameters.Width, 6);
        Assert.Equal(120, shape.Shape.Parameters.Height, 6);
        Assert.Equal(300, shape.Shape.Parameters.Centre.X, 6);
        Assert.Equal(260, shape.Shape.Parameters.Centre.Y, 6);
    }
}
