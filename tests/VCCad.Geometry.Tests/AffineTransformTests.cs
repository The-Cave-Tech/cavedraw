using VCCad.Geometry;
using Xunit;

namespace VCCad.Geometry.Tests;

public class AffineTransformTests
{
    [Fact]
    public void IdentityLeavesPointsUnchanged()
    {
        var p = new Point2D(3.2, -9.1);
        Assert.Equal(p, AffineTransform.Identity.Transform(p));
    }

    [Fact]
    public void TranslationMovesByVector()
    {
        AffineTransform t = AffineTransform.CreateTranslation(10, -5);
        Assert.Equal(new Point2D(13, 2), t.Transform(new Point2D(3, 7)));
    }

    [Fact]
    public void ScaleAboutOrigin()
    {
        AffineTransform s = AffineTransform.CreateScale(2, 3);
        Assert.Equal(new Point2D(4, 9), s.Transform(new Point2D(2, 3)));
    }

    [Fact]
    public void VectorTransformsIgnoreTranslation()
    {
        AffineTransform t = AffineTransform.CreateTranslation(100, 100);
        var v = new Vector2D(3, 4);
        Assert.Equal(v, t.Transform(v)); // translations must not leak into vectors
    }

    [Fact]
    public void RotationIsOrthonormal()
    {
        AffineTransform r = AffineTransform.CreateRotation(Math.PI / 3);
        var p = new Point2D(1, 2);
        Point2D q = r.Transform(p);
        Assert.Equal(p.X * p.X + p.Y * p.Y, q.X * q.X + q.Y * q.Y, 9); // length preserved
    }

    [Fact]
    public void ComposeAppliesRightMostFirst()
    {
        // Compose(x) must behave as "apply x, then apply this".
        AffineTransform scale2 = AffineTransform.CreateScale(2, 2);
        AffineTransform translate10 = AffineTransform.CreateTranslation(10, 0);

        AffineTransform combined = translate10.Compose(scale2);
        Point2D result = combined.Transform(new Point2D(1, 0));
        Assert.Equal(new Point2D(12, 0), result); // scale(1→2), then translate → 12
    }

    [Fact]
    public void TransformRectIsBoundingBoxOfCorners()
    {
        AffineTransform r = AffineTransform.CreateRotationAround(new Point2D(0, 0), Math.PI / 4);
        var rect = new Rect2D(0, 0, 2, 2); // square of side 2
        Rect2D box = r.Transform(rect);

        // Rotating the square 45° gives a diamond spanning ±√2 in X and 0..2√2 in Y.
        Assert.Equal(2 * Math.Sqrt(2.0), box.Width, 9);
        Assert.Equal(2 * Math.Sqrt(2.0), box.Height, 9);
    }

    [Fact]
    public void InverseRoundTrips()
    {
        AffineTransform t = AffineTransform.CreateTranslation(5, -7)
            .Compose(AffineTransform.CreateRotation(0.42))
            .Compose(AffineTransform.CreateScale(2.5, 1.5));

        var p = new Point2D(-13.7, 0.001);
        Point2D forward = t.Transform(p);
        Point2D back = t.Inverted().Transform(forward);
        Assert.Equal(p.X, back.X, 9);
        Assert.Equal(p.Y, back.Y, 9);
    }

    [Fact]
    public void SingularMatrixThrowsOnInvert()
    {
        AffineTransform degenerate = new(1, 2, 2, 4, 0, 0); // det = 0
        Assert.False(degenerate.IsInvertible);
        Assert.Throws<InvalidOperationException>(() => degenerate.Inverted());
    }

    [Fact]
    public void MapRectCarriesCornersOntoDestination()
    {
        var from = new Rect2D(0, 0, 100, 50);
        var to = new Rect2D(10, 20, 200, 100);
        AffineTransform map = AffineTransform.MapRect(from, to);

        Assert.Equal(new Point2D(10, 20), map.Transform(new Point2D(0, 0)));
        Assert.Equal(new Point2D(210, 120), map.Transform(new Point2D(100, 50)));
    }

    [Fact]
    public void TopLeftModelSpaceFlipsIntoPdfUserSpace()
    {
        // The exporter maps a model point (top-left origin, y-down) into PDF user
        // space (bottom-left origin, y-up) for a page of height H using the affine
        // [1 0 0 −1 −X (Y+H)]. Verify against the equivalent translate∘scale form.
        double x = 25.0, y = 40.0, h = 300.0;
        AffineTransform flip = new(1.0, 0.0, 0.0, -1.0, -x, y + h);
        AffineTransform expected = AffineTransform.CreateTranslation(0, y + h)
            .Compose(AffineTransform.CreateScale(1, -1))
            .Compose(AffineTransform.CreateTranslation(-x, 0));

        var p = new Point2D(x + 12, y + 33);
        Assert.Equal(expected.Transform(p).X, flip.Transform(p).X, 12);
        Assert.Equal(expected.Transform(p).Y, flip.Transform(p).Y, 12);

        // The artboard's top-left corner must land on the PDF page's top-left
        // corner: model (x, y) → (0, h).
        Assert.Equal(0.0, flip.Transform(new Point2D(x, y)).X, 12);
        Assert.Equal(h, flip.Transform(new Point2D(x, y)).Y, 12);
    }
}
