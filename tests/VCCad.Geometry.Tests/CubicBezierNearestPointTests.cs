using VCCad.Geometry;
using Xunit;

namespace VCCad.Geometry.Tests;

public class CubicBezierNearestPointTests
{
    private static readonly CubicBezier S = CubicBezier.FromHandles(
        new Point2D(0, 0), new Point2D(0, 100), new Point2D(100, 0), new Point2D(100, 100));

    [Fact]
    public void EndpointsAreExactForCornerQueries()
    {
        S.NearestPoint(S.P0, out double t0, out double d0);
        Assert.Equal(0.0, t0, 4);
        Assert.Equal(0.0, d0, 4);

        S.NearestPoint(S.P3, out double t1, out double d1);
        Assert.Equal(1.0, t1, 4);
        Assert.Equal(0.0, d1, 4);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.3)]
    [InlineData(0.7)]
    [InlineData(1.0)]
    public void PointOnCurveReportsZeroDistance(double t)
    {
        Point2D onCurve = S.PointAt(t);
        S.NearestPoint(onCurve, out double foundT, out double distance);
        Assert.True(distance < 1e-6, $"Expected ~0 distance at t={t}, got {distance}.");
        Assert.Equal(t, foundT, 3);
    }

    [Fact]
    public void OffCurveQueryFindsTheVisibleMinimum()
    {
        // For this S curve the closest point to (−20, 50) is the left bulge.
        S.NearestPoint(new Point2D(-20, 50), out _, out double distance);

        // The curve never gets closer than ~ distance to that point: verify by
        // brute-force dense sampling that nothing is meaningfully closer.
        double brute = double.PositiveInfinity;
        for (int i = 0; i <= 200000; i++)
        {
            brute = Math.Min(brute, S.PointAt(i / 200000.0).DistanceTo(new Point2D(-20, 50)));
        }

        Assert.Equal(brute, distance, 2);
        Assert.True(distance >= 0.0);
    }

    [Fact]
    public void DistanceToStraightLineMatchesSegmentGeometry()
    {
        CubicBezier line = CubicBezier.FromLine(new Point2D(0, 0), new Point2D(10, 0));

        // Query beyond the right end clamps to the endpoint.
        line.NearestPoint(new Point2D(30, 0), out _, out double pastEnd);
        Assert.Equal(20.0, pastEnd, 6);

        // Query above the middle projects onto the segment.
        line.NearestPoint(new Point2D(5, 7), out _, out double above);
        Assert.Equal(7.0, above, 6);
    }
}
