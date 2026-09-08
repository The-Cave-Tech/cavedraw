using VCCad.Geometry;
using Xunit;

namespace VCCad.Geometry.Tests;

public class CubicBezierTests
{
    private static readonly CubicBezier Unit = CubicBezier.FromHandles(
        new Point2D(0, 0), new Point2D(0, 100), new Point2D(100, 0), new Point2D(100, 100));

    [Fact]
    public void EndpointsAreExact()
    {
        Assert.Equal(Unit.P0, Unit.PointAt(0.0));
        Assert.Equal(Unit.P3, Unit.PointAt(1.0));
    }

    [Fact]
    public void StraightLineMidpointIsExact()
    {
        CubicBezier line = CubicBezier.FromLine(new Point2D(0, 0), new Point2D(10, 0));
        Assert.Equal(new Point2D(5, 0), line.PointAt(0.5));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.25)]
    [InlineData(0.5)]
    [InlineData(0.9)]
    [InlineData(1.0)]
    public void SplitIsLosslessAcrossSubCurves(double t)
    {
        (CubicBezier left, CubicBezier right) = Unit.SplitAt(t);

        // The split point is shared, exactly, by construction of de Casteljau.
        Assert.Equal(left.P3, right.P0);

        // Samples on either side reproduce the original curve.
        AssertPoint(Unit.PointAt(t * 0.5), left.PointAt(0.5));
        AssertPoint(Unit.PointAt(t + (1.0 - t) * 0.5), right.PointAt(0.5));
    }

    [Fact]
    public void FlattenTouchesCurveWithinTolerance()
    {
        CubicBezier curve = CubicBezier.FromHandles(
            new Point2D(0, 0), new Point2D(40, 200), new Point2D(200, -80), new Point2D(240, 60));

        double tolerance = 0.01;
        IReadOnlyList<Point2D> polyline = curve.Flatten(tolerance);

        Assert.True(polyline.Count >= 4, "A curvy cubic must produce several segments.");
        Assert.Equal(curve.P0, polyline[0]);
        Assert.Equal(curve.P3, polyline[^1]);

        // Oracle check: dense parameter sampling, then verify every dense sample is
        // within ~tolerance of some polyline segment. Sample far denser than the
        // flattening tolerance so a failure is meaningful.
        for (int i = 0; i <= 2000; i++)
        {
            Point2D sample = curve.PointAt(i / 2000.0);
            double nearest = DistanceToPolyline(sample, polyline);
            Assert.True(nearest <= tolerance + 1e-6,
                $"Dense sample at t={i / 2000.0} is {nearest} from the flattened polyline.");
        }
    }

    [Fact]
    public void TighterToleranceYieldsMoreSegments()
    {
        CubicBezier curve = CubicBezier.FromHandles(
            new Point2D(0, 0), new Point2D(100, 100), new Point2D(200, -50), new Point2D(300, 20));
        Assert.True(curve.Flatten(0.5).Count <= curve.Flatten(0.001).Count);
    }

    [Fact]
    public void StraightSegmentFlattensToItsTwoEndpoints()
    {
        CubicBezier line = CubicBezier.FromLine(new Point2D(1, 2), new Point2D(9, 8));
        IReadOnlyList<Point2D> polyline = line.Flatten(0.001);
        Assert.Equal(2, polyline.Count);
        Assert.Equal(new Point2D(1, 2), polyline[0]);
        Assert.Equal(new Point2D(9, 8), polyline[^1]);
    }

    [Fact]
    public void LengthOfStraightLineIsChord()
    {
        CubicBezier line = CubicBezier.FromLine(new Point2D(0, 0), new Point2D(3, 4));
        Assert.Equal(5.0, line.EstimateLength(), 6);
    }

    [Fact]
    public void LengthIsMonotoneWithToleranceAndSane()
    {
        CubicBezier curve = CubicBezier.FromHandles(
            new Point2D(0, 0), new Point2D(300, 300), new Point2D(-300, 300), new Point2D(0, 0));

        double fine = curve.EstimateLength(1e-4);
        double coarse = curve.EstimateLength(1.0);

        // Arc length always exceeds the endpoint chord, and refining the tolerance
        // only ever lengthens the estimate.
        Assert.True(fine >= 2.0 * 300.0, "Length should exceed the control extents chord.");
        Assert.True(fine >= coarse);
    }

    [Fact]
    public void TangentIsParallelToChordForStraightLine()
    {
        CubicBezier line = CubicBezier.FromLine(new Point2D(0, 0), new Point2D(10, 0));
        Vector2D t = line.TangentAt(0.5);
        Assert.Equal(0.0, t.Y, 12);
        Assert.True(t.X > 0); // oriented along travel direction
    }

    [Fact]
    public void BoundingBoxIsTight()
    {
        // S-curve whose true extents lie strictly between its endpoints.
        CubicBezier curve = CubicBezier.FromHandles(
            new Point2D(0, 0), new Point2D(100, 400), new Point2D(200, -400), new Point2D(300, 0));

        Rect2D tight = curve.BoundingBox();

        // Sample oracle at very high resolution.
        double minY = double.PositiveInfinity, maxY = double.NegativeInfinity;
        for (int i = 0; i <= 20000; i++)
        {
            Point2D p = curve.PointAt(i / 20000.0);
            minY = Math.Min(minY, p.Y);
            maxY = Math.Max(maxY, p.Y);
        }

        // Tight box must contain every sample…
        Assert.True(tight.Top <= minY + 1e-6 && tight.Bottom >= maxY - 1e-6,
            "Tight box must contain the sampled extents.");

        // …and must not be meaningfully larger than them (analytic root solving
        // locates the true extrema that 20k samples only approximate).
        Assert.True(Math.Abs(tight.Top - minY) < 1.0, "Box top should hug the true minimum.");
        Assert.True(Math.Abs(tight.Bottom - maxY) < 1.0, "Box bottom should hug the true maximum.");
    }

    [Fact]
    public void ReversedTraversesOppositeDirection()
    {
        CubicBezier reversed = Unit.Reversed();
        Assert.Equal(Unit.P3, reversed.P0);
        Assert.Equal(Unit.P0, reversed.P3);
        AssertPoint(Unit.PointAt(0.25), reversed.PointAt(0.75));
    }

    [Fact]
    public void DegeneratePointCurveIsStable()
    {
        CubicBezier point = CubicBezier.FromPoint(new Point2D(5, 5));
        Assert.Equal(new Point2D(5, 5), point.PointAt(0.3));
        Assert.Equal(0.0, point.CurvatureAt(0.5), 12); // no NaN from zero speed
        Assert.Equal(0.0, point.EstimateLength(), 12);
    }

    [Fact]
    public void CurvatureOfCircleLikeArcIsNearInverseRadius()
    {
        // Large circle-arc whose radius we know from the kappa construction.
        double r = 100.0;
        double k = 0.5522847498307936;
        var arc = new CubicBezier(
            new Point2D(r, 0),
            new Point2D(r, r * k),
            new Point2D(r * k, r),
            new Point2D(0, r));

        double curvature = arc.CurvatureAt(0.5);
        Assert.Equal(1.0 / r, curvature, 3); // circle → constant κ = 1/r
    }

    private static void AssertPoint(Point2D expected, Point2D actual, double epsilon = 1e-6)
    {
        Assert.True(expected.NearlyEquals(actual, epsilon),
            $"Expected {expected} but got {actual}.");
    }

    private static double DistanceToPolyline(Point2D p, IReadOnlyList<Point2D> polyline)
    {
        double best = double.PositiveInfinity;
        for (int i = 1; i < polyline.Count; i++)
        {
            best = Math.Min(best, DistanceToSegment(p, polyline[i - 1], polyline[i]));
        }

        return best;
    }

    private static double DistanceToSegment(Point2D p, Point2D a, Point2D b)
    {
        Vector2D ab = b - a;
        double lenSq = ab.LengthSquared;
        if (lenSq <= MathUtils.Epsilon)
        {
            return p.DistanceTo(a);
        }

        double t = MathUtils.Clamp01((p - a).Dot(ab) / lenSq);
        return p.DistanceTo(a + ab * t);
    }
}
