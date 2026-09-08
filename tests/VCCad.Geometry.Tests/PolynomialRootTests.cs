using VCCad.Geometry;
using Xunit;

namespace VCCad.Geometry.Tests;

public class PolynomialRootTests
{
    [Theory]
    [InlineData(1, -3, 2, 1.0, 2.0)]      // (t-1)(t-2)
    [InlineData(1, 0, -4, -2.0, 2.0)]     // t² - 4
    [InlineData(1, -5, 6, 2.0, 3.0)]      // (t-2)(t-3)
    public void QuadraticRoots(double a, double b, double c, double expectedA, double expectedB)
    {
        int count = MathUtils.SolveQuadratic(a, b, c, out double r0, out double r1);
        Assert.Equal(2, count);
        Assert.Equal(expectedA, r0, 9);
        Assert.Equal(expectedB, r1, 9);
    }

    [Fact]
    public void QuadraticDoubleRootIsReportedOnce()
    {
        // 2(t - 1)² = 2t² - 4t + 2 → a single distinct root at t = 1.
        int count = MathUtils.SolveQuadratic(2, -4, 2, out double root, out _);
        Assert.Equal(1, count);
        Assert.Equal(1.0, root, 9);
    }

    [Fact]
    public void QuadraticRootsHandlesLinearFallback()
    {
        // 2t + 4 = 0 → t = -2. Leading coefficient is ~0 so we take the linear path.
        int count = MathUtils.SolveQuadratic(1e-12, 2, 4, out double root, out _);
        Assert.Equal(1, count);
        Assert.Equal(-2.0, root, 9);
    }

    [Fact]
    public void QuadraticWithNegativeDiscriminantHasNoRealRoots()
    {
        int count = MathUtils.SolveQuadratic(1, 0, 5, out _, out _); // t² + 5
        Assert.Equal(0, count);
    }

    [Fact]
    public void CubicThreeDistinctRoots()
    {
        // (t - 1)(t - 2)(t - 3) = t³ - 6t² + 11t - 6
        Span<double> roots = stackalloc double[3];
        int count = PolynomialRoots.SolveCubic(1, -6, 11, -6, roots);
        Assert.Equal(3, count);
        Assert.Equal(1.0, roots[0], 9);
        Assert.Equal(2.0, roots[1], 9);
        Assert.Equal(3.0, roots[2], 9);
    }

    [Fact]
    public void CubicSingleRoot()
    {
        // (t - 2)(t² + 1) = t³ - 2t² + t - 2 → exactly one real root at t = 2.
        Span<double> roots = stackalloc double[3];
        int count = PolynomialRoots.SolveCubic(1, -2, 1, -2, roots);
        Assert.Equal(1, count);
        Assert.Equal(2.0, roots[0], 9);
    }

    [Fact]
    public void CubicRepeatedRoot()
    {
        // (t - 1)²(t - 4) = t³ - 6t² + 9t - 4 → double root at 1, simple at 4.
        Span<double> roots = stackalloc double[3];
        int count = PolynomialRoots.SolveCubic(1, -6, 9, -4, roots);
        Assert.Equal(2, count);
        Assert.Equal(1.0, roots[0], 9);
        Assert.Equal(4.0, roots[1], 9);
    }

    [Fact]
    public void CubicDegenerateFallsThroughToLinear()
    {
        // 3t + 6 = 0 handled when the cubic coefficient vanishes.
        Span<double> roots = stackalloc double[3];
        int count = PolynomialRoots.SolveCubic(0, 0, 3, 6, roots);
        Assert.Equal(1, count);
        Assert.Equal(-2.0, roots[0], 9);
    }
}
