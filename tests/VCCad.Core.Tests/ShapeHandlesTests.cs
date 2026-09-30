using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// A shape's control points: one per parameter the shape actually has, positioned where that parameter
/// lives.
///
/// Two rules are the whole of it. A shape offers **only the handles it has** - drawing a full set on every
/// shape puts handles on a heart that change nothing, which teaches a person that handles are unreliable.
/// And every handle is **clamped at the degenerate end**, because a zero width or an inner ring outside its
/// outer one is not a shape, and a drag that produced one would leave an object no handle could drag back.
/// </summary>
public class ShapeHandlesTests
{
    private static ShapeDefinition Shape(ShapeKind kind, double width = 200, double height = 100, double rotation = 0)
        => new(kind, new ShapeParameters
        {
            Centre = new Point2D(100, 100),
            Width = width,
            Height = height,
            Rotation = rotation,
        });

    private static ShapeParameters Moved(ShapeDefinition shape, ShapeHandle handle, double x, double y)
        => ShapeHandles.Move(shape, handle, new Point2D(x, y));

    private static IReadOnlyList<ShapeHandle> Handles(ShapeDefinition shape)
        => ShapeHandles.For(shape).Select(h => h.Handle).ToList();

    // ---- which handles a shape has ----------------------------------------------

    /// <summary>A shape offers the handles for its own parameters and no others.</summary>
    [Fact]
    public void AShapeOffersOnlyTheHandlesItHas()
    {
        // A heart is a box: where it is and how big it is, and nothing else.
        Assert.Equal(
            new[] { ShapeHandle.Centre, ShapeHandle.Width, ShapeHandle.Height, ShapeHandle.Rotation },
            Handles(Shape(ShapeKind.Heart)));

        // A star also has its inner ring.
        Assert.Contains(ShapeHandle.InnerRatio, Handles(Shape(ShapeKind.Star)));
        Assert.Contains(ShapeHandle.InnerRatio, Handles(Shape(ShapeKind.Polygon)));

        // A rounded rectangle has its corner radius, a trapezoid its top, an arrow three of its own.
        Assert.Contains(ShapeHandle.CornerRadius, Handles(Shape(ShapeKind.RoundedRectangle)));
        Assert.Contains(ShapeHandle.TopRatio, Handles(Shape(ShapeKind.Trapezoid)));
        Assert.Contains(ShapeHandle.HeadLength, Handles(Shape(ShapeKind.Arrow)));
        Assert.Contains(ShapeHandle.HeadWidth, Handles(Shape(ShapeKind.Arrow)));
        Assert.Contains(ShapeHandle.ShaftWidth, Handles(Shape(ShapeKind.Arrow)));

        // None of those belongs to a heart.
        Assert.DoesNotContain(ShapeHandle.InnerRatio, Handles(Shape(ShapeKind.Heart)));
        Assert.DoesNotContain(ShapeHandle.CornerRadius, Handles(Shape(ShapeKind.Heart)));
        Assert.DoesNotContain(ShapeHandle.TopRatio, Handles(Shape(ShapeKind.Heart)));
    }

    /// <summary>A callout offers its tail only when it has one.</summary>
    [Fact]
    public void ACalloutOffersItsTailOnlyWhenItHasOne()
    {
        var without = new ShapeDefinition(ShapeKind.Callout, new ShapeParameters
        {
            Centre = new Point2D(100, 100), Width = 200, Height = 100, HasTail = false,
        });

        var with = new ShapeDefinition(ShapeKind.Callout, new ShapeParameters
        {
            Centre = new Point2D(100, 100), Width = 200, Height = 100, HasTail = true, Tail = new Point2D(150, 160),
        });

        Assert.DoesNotContain(ShapeHandle.Tail, Handles(without));
        Assert.Contains(ShapeHandle.Tail, Handles(with));
    }

    /// <summary>Every handle sits on the shape, not floating somewhere unrelated.</summary>
    [Fact]
    public void TheHandlesSitWhereTheirParameterIs()
    {
        ShapeDefinition shape = Shape(ShapeKind.Star);

        Assert.Contains(ShapeHandles.For(shape), h =>
            h.Handle == ShapeHandle.Centre && h.Position.NearlyEquals(new Point2D(100, 100), 1e-9));
        Assert.Contains(ShapeHandles.For(shape), h =>
            h.Handle == ShapeHandle.Width && h.Position.NearlyEquals(new Point2D(200, 100), 1e-9));
        Assert.Contains(ShapeHandles.For(shape), h =>
            h.Handle == ShapeHandle.Height && h.Position.NearlyEquals(new Point2D(100, 150), 1e-9));
    }

    // ---- moving them -----------------------------------------------------------

    /// <summary>Dragging the width handle sets the width to twice the distance from the centre.</summary>
    [Fact]
    public void TheWidthHandleSetsTheWidth()
    {
        ShapeDefinition shape = Shape(ShapeKind.Rectangle);

        Assert.Equal(300, Moved(shape, ShapeHandle.Width, 250, 100).Width, 6);
        Assert.Equal(60, Moved(shape, ShapeHandle.Width, 130, 100).Width, 6);
    }

    /// <summary>A width dragged through the centre clamps rather than inverting into nothing.</summary>
    [Fact]
    public void TheWidthCannotBeDraggedAway()
    {
        ShapeDefinition shape = Shape(ShapeKind.Rectangle);

        ShapeParameters through = Moved(shape, ShapeHandle.Width, 100, 100);
        Assert.True(through.Width >= ShapeHandles.MinExtent,
            $"a width dragged onto the centre should clamp, not become {through.Width}");

        ShapeParameters tiny = Moved(shape, ShapeHandle.Width, 100.1, 100);
        Assert.True(tiny.Width >= ShapeHandles.MinExtent);
    }

    [Fact]
    public void TheHeightHandleSetsTheHeight()
    {
        ShapeDefinition shape = Shape(ShapeKind.Rectangle);

        Assert.Equal(200, Moved(shape, ShapeHandle.Height, 100, 200).Height, 6);
        Assert.True(Moved(shape, ShapeHandle.Height, 100, 100).Height >= ShapeHandles.MinExtent);
    }

    /// <summary>The rotation handle sets which way the shape faces.</summary>
    [Fact]
    public void TheRotationHandleTurnsTheShape()
    {
        ShapeDefinition shape = Shape(ShapeKind.Star);

        // The handle starts above the centre; dragging it to the right is a quarter turn.
        Assert.Equal(Math.PI / 2, Moved(shape, ShapeHandle.Rotation, 300, 100).Rotation, 6);
        Assert.Equal(0, Moved(shape, ShapeHandle.Rotation, 100, -100).Rotation, 6);
    }

    /// <summary>An inner ring is dragged between the centre and the edge, and clamped inside them.</summary>
    [Fact]
    public void TheInnerRatioHandleIsClamped()
    {
        ShapeDefinition shape = Shape(ShapeKind.Star);

        // Halfway out along the short axis: 50 of 50.
        Assert.Equal(0.5, Moved(shape, ShapeHandle.InnerRatio, 100, 75).InnerRatio, 3);

        // Outside the shape, and on the centre: both clamped.
        Assert.True(Moved(shape, ShapeHandle.InnerRatio, 100, 400).InnerRatio <= 0.98);
        Assert.True(Moved(shape, ShapeHandle.InnerRatio, 100, 100).InnerRatio >= 0.02);
    }

    /// <summary>A corner radius cannot exceed the shorter side, or the arcs would overlap.</summary>
    [Fact]
    public void TheCornerRadiusHandleIsClamped()
    {
        ShapeDefinition shape = Shape(ShapeKind.RoundedRectangle, width: 200, height: 100);

        // Half the short side is the most a corner can take, and the corner itself is at (0, 50) - the box
        // is 200x100 centred on (100, 100), so its top-left is 100 left and 50 up from the centre.
        Assert.Equal(50, Moved(shape, ShapeHandle.CornerRadius, 100, 500).CornerRadius, 6);
        Assert.Equal(0, Moved(shape, ShapeHandle.CornerRadius, 0, 50).CornerRadius, 6);
        Assert.Equal(25, Moved(shape, ShapeHandle.CornerRadius, 25, 50).CornerRadius, 6);
    }

    /// <summary>A trapezoid's top is a fraction of its bottom, and cannot go outside it.</summary>
    [Fact]
    public void TheTopRatioHandleIsClamped()
    {
        ShapeDefinition shape = Shape(ShapeKind.Trapezoid, width: 200);

        // The box is centred on (100, 100) and 200 wide, so it spans 0 to 200: the middle is 100, not 200.
        Assert.Equal(1.0, Moved(shape, ShapeHandle.TopRatio, 200, 0).TopRatio, 6);
        Assert.Equal(0.0, Moved(shape, ShapeHandle.TopRatio, 0, 0).TopRatio, 6);
        Assert.Equal(0.5, Moved(shape, ShapeHandle.TopRatio, 100, 0).TopRatio, 6);
    }

    /// <summary>An arrow's three handles stay inside the range where an arrow is still an arrow.</summary>
    [Fact]
    public void TheArrowsHandlesAreClamped()
    {
        ShapeDefinition shape = Shape(ShapeKind.Arrow, width: 200, height: 100);

        Assert.True(Moved(shape, ShapeHandle.HeadLength, 300, 0).HeadLength is >= 0.05 and <= 0.95);
        Assert.True(Moved(shape, ShapeHandle.HeadWidth, 0, 500).HeadWidth <= 2.0);
        Assert.True(Moved(shape, ShapeHandle.ShaftWidth, 0, 0).ShaftWidth >= 0.02);
    }

    /// <summary>
    /// Moving the centre moves the tail with it. The tail is a point on the page, not an offset, so a shape
    /// dragged by its centre that left its tail behind would tear itself apart.
    /// </summary>
    [Fact]
    public void MovingTheCentreCarriesTheTail()
    {
        var shape = new ShapeDefinition(ShapeKind.Callout, new ShapeParameters
        {
            Centre = new Point2D(100, 100),
            Width = 200,
            Height = 100,
            HasTail = true,
            Tail = new Point2D(160, 170),
        });

        ShapeParameters moved = Moved(shape, ShapeHandle.Centre, 150, 130);

        Assert.Equal(150, moved.Centre.X, 6);
        Assert.Equal(130, moved.Centre.Y, 6);
        Assert.Equal(210, moved.Tail.X, 6);
        Assert.Equal(200, moved.Tail.Y, 6);
    }

    /// <summary>Dragging a tail simply puts it where the pointer is.</summary>
    [Fact]
    public void TheTailHandleFollowsThePointer()
    {
        var shape = new ShapeDefinition(ShapeKind.Callout, new ShapeParameters
        {
            Centre = new Point2D(100, 100), Width = 200, Height = 100, HasTail = true,
        });

        ShapeParameters moved = Moved(shape, ShapeHandle.Tail, 240, 250);

        Assert.True(moved.HasTail);
        Assert.Equal(240, moved.Tail.X, 6);
        Assert.Equal(250, moved.Tail.Y, 6);
    }

    // ---- rotation --------------------------------------------------------------

    /// <summary>
    /// A rotated shape's handles are rotated with it, and a pointer dragged onto one is understood in the
    /// shape's own frame. Without that, resizing a turned shape moves it in the wrong direction.
    /// </summary>
    [Fact]
    public void HandlesFollowTheShapeWhenItIsTurned()
    {
        ShapeDefinition turned = Shape(ShapeKind.Rectangle, rotation: Math.PI / 2);

        // Turned a quarter turn, the width handle that was to the right is now below.
        ShapeHandlePoint width = ShapeHandles.For(turned).First(h => h.Handle == ShapeHandle.Width);
        Assert.Equal(100, width.Position.X, 6);
        Assert.Equal(200, width.Position.Y, 6);

        // And dragging that handle downwards still changes the width, not the height.
        ShapeParameters moved = Moved(turned, ShapeHandle.Width, 100, 250);
        Assert.Equal(300, moved.Width, 6);
        Assert.Equal(100, moved.Height, 6);
    }

    // ---- finding one -----------------------------------------------------------

    /// <summary>The nearest handle within reach is found, and nothing further away is.</summary>
    [Fact]
    public void TheNearestHandleIsFound()
    {
        ShapeDefinition shape = Shape(ShapeKind.Rectangle);

        Assert.Equal(ShapeHandle.Width, ShapeHandles.Nearest(shape, new Point2D(202, 101), 6)?.Handle);
        Assert.Equal(ShapeHandle.Height, ShapeHandles.Nearest(shape, new Point2D(99, 152), 6)?.Handle);
        Assert.Null(ShapeHandles.Nearest(shape, new Point2D(500, 500), 6));
    }
}
