using VCCad.Geometry;
using Xunit;

namespace VCCad.Geometry.Tests;

public class VectorPointTests
{
    [Fact]
    public void LengthAndNormalize()
    {
        var v = new Vector2D(3.0, 4.0);
        Assert.Equal(5.0, v.Length, 12);
        Vector2D unit = v.Normalized;
        Assert.Equal(1.0, unit.Length, 12);
        Assert.Equal(v.X / 5.0, unit.X, 12);
    }

    [Fact]
    public void ZeroVectorCannotBeNormalized()
    {
        Vector2D unit = Vector2D.Zero.Normalized;
        Assert.Equal(0.0, unit.Length, 12); // stays zero, does not NaN
    }

    [Theory]
    [InlineData(1, 0, 0, 1, 0.0)]          // perpendicular vectors
    [InlineData(1, 0, 1, 0, 1.0)]          // parallel same direction
    [InlineData(1, 0, -1, 0, -1.0)]        // anti-parallel
    [InlineData(3, 4, -4, 3, 0.0)]         // classic dot-zero pair
    public void DotProduct(double ax, double ay, double bx, double by, double expected)
    {
        var a = new Vector2D(ax, ay);
        var b = new Vector2D(bx, by);
        Assert.Equal(expected, a.Dot(b), 12);
    }

    [Theory]
    [InlineData(1, 0, 0, 1, 1.0)]          // right-angle cross magnitude
    [InlineData(1, 0, 1, 0, 0.0)]          // collinear
    [InlineData(2, 0, 0, 3, 6.0)]          // parallelogram area
    public void CrossProduct(double ax, double ay, double bx, double by, double expected)
    {
        var a = new Vector2D(ax, ay);
        var b = new Vector2D(bx, by);
        Assert.Equal(expected, a.Cross(b), 12);
    }

    [Fact]
    public void PerpendicularIsOrthogonalAndSameLength()
    {
        var v = new Vector2D(2.0, -3.0);
        Vector2D perp = v.Perpendicular;
        Assert.Equal(0.0, v.Dot(perp), 12);
        Assert.Equal(v.Length, perp.Length, 12);
    }

    [Fact]
    public void PointSubtractionYieldsVector()
    {
        var a = new Point2D(5.0, 7.0);
        var b = new Point2D(2.0, 3.0);
        Vector2D delta = a - b;
        Assert.Equal(3.0, delta.X, 12);
        Assert.Equal(4.0, delta.Y, 12);
        Assert.Equal(b + delta, a);
    }

    [Fact]
    public void PointRotationAboutOrigin()
    {
        // 90° CCW rotation of (1, 0) lands on (0, 1) in a right-handed frame.
        Vector2D rotated = new Vector2D(1.0, 0.0).Rotated(Math.PI / 2.0);
        Assert.Equal(0.0, rotated.X, 9);
        Assert.Equal(1.0, rotated.Y, 9);
    }

    [Fact]
    public void RotationPreservesLength()
    {
        var v = new Vector2D(3.0, -4.0);
        Vector2D rotated = v.Rotated(0.734);
        Assert.Equal(v.Length, rotated.Length, 9);
    }
}
