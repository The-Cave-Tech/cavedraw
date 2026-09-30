using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// Fitting a freehand stroke to Bézier segments.
///
/// The tests measure the fit **against the points that were drawn**, using a dense sampling of the fitted
/// curve rather than the fitter's own error estimate - a fit that checks itself proves nothing. And the
/// tolerance is deliberately tight, because this is a drawing tool: the curve has to follow the hand, so
/// the fit removes the jitter of a pointer and nothing else.
/// </summary>
public class FreehandFitterTests
{
    private static List<Point2D> Line(int count, double length = 200)
    {
        var points = new List<Point2D>();
        for (int i = 0; i < count; i++)
        {
            points.Add(new Point2D(length * i / (count - 1), 0));
        }

        return points;
    }

    private static List<Point2D> Arc(double radius, double degrees, int count)
    {
        var points = new List<Point2D>();
        for (int i = 0; i < count; i++)
        {
            double angle = degrees * Math.PI / 180.0 * i / (count - 1);
            points.Add(new Point2D(radius * Math.Cos(angle), radius * Math.Sin(angle)));
        }

        return points;
    }

    private static List<Point2D> Wave(int count, double amplitude, double cycles)
    {
        var points = new List<Point2D>();
        for (int i = 0; i < count; i++)
        {
            double t = (double)i / (count - 1);
            points.Add(new Point2D(t * 300, Math.Sin(t * cycles * 2 * Math.PI) * amplitude));
        }

        return points;
    }

    /// <summary>The fitted path, as a densely flattened polyline - the independent measure of the fit.</summary>
    private static List<Point2D> Curve(IReadOnlyList<PathNode> nodes)
    {
        var path = new PathItem { Name = "stroke" };
        SubPath sub = path.AddSubPath(closed: false);
        foreach (PathNode node in nodes)
        {
            sub.Nodes.Add(new PathNode(node.Anchor, node.InHandle, node.OutHandle));
        }

        return PathFlattener
            .Flatten(path, tolerance: 0.005)
            .SelectMany(outline => outline.Points)
            .ToList();
    }

    /// <summary>The largest distance any drawn point ended up from the fitted curve.</summary>
    private static double WorstDeviation(IReadOnlyList<Point2D> drawn, IReadOnlyList<PathNode> nodes)
    {
        List<Point2D> curve = Curve(nodes);
        double worst = 0;

        foreach (Point2D point in drawn)
        {
            double nearest = double.MaxValue;
            for (int i = 1; i < curve.Count; i++)
            {
                nearest = Math.Min(nearest, DistanceToSegment(point, curve[i - 1], curve[i]));
            }

            worst = Math.Max(worst, nearest);
        }

        return worst;
    }

    private static double DistanceToSegment(Point2D point, Point2D a, Point2D b)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        double lengthSquared = (dx * dx) + (dy * dy);
        if (lengthSquared < 1e-18)
        {
            return Math.Sqrt(Math.Pow(point.X - a.X, 2) + Math.Pow(point.Y - a.Y, 2));
        }

        double t = Math.Clamp((((point.X - a.X) * dx) + ((point.Y - a.Y) * dy)) / lengthSquared, 0, 1);
        return Math.Sqrt(Math.Pow(point.X - (a.X + (dx * t)), 2) + Math.Pow(point.Y - (a.Y + (dy * t)), 2));
    }

    /// <summary>A straight drag is **one** segment: a fit that broke a ruled line into several would be
    /// doing the opposite of what was drawn.</summary>
    [Fact]
    public void AStraightDragIsOneSegment()
    {
        FreehandFit fit = FreehandFitter.Fit(Line(40));

        Assert.Equal(1, fit.Segments);
        Assert.Equal(2, fit.Nodes.Count);
        Assert.True(fit.WorstError < 1e-6, $"a straight line should fit exactly, got {fit.WorstError}");
    }

    /// <summary>A quarter circle fits tightly, in a handful of segments rather than a hundred points.</summary>
    [Fact]
    public void ACurvedDragFitsWithinTheTolerance()
    {
        List<Point2D> drawn = Arc(100, 90, 120);

        FreehandFit fit = FreehandFitter.Fit(drawn);

        Assert.True(fit.Segments is >= 1 and <= 6,
            $"a quarter circle should not need many segments, got {fit.Segments}");

        double deviation = WorstDeviation(drawn, fit.Nodes);
        Assert.True(deviation <= FreehandFitter.Tolerance,
            $"the fit is {deviation:0.####} from the drawn points, tolerance is {FreehandFitter.Tolerance:0.####}");
    }

    /// <summary>A wiggly stroke is followed: every drawn point is within the tolerance of the curve.</summary>
    [Theory]
    [InlineData(20, 1)]
    [InlineData(60, 2)]
    [InlineData(120, 3)]
    public void AWigglyStrokeIsFollowed(double amplitude, double cycles)
    {
        List<Point2D> drawn = Wave(200, amplitude, cycles);

        FreehandFit fit = FreehandFitter.Fit(drawn);

        Assert.True(fit.Segments > 1, "a wave is not one segment");

        double deviation = WorstDeviation(drawn, fit.Nodes);
        Assert.True(deviation <= FreehandFitter.Tolerance,
            $"amplitude {amplitude} cycles {cycles}: the fit is {deviation:0.####} off, tolerance is {FreehandFitter.Tolerance:0.####}");
    }

    /// <summary>The fit is doing something: far fewer segments than points, and far more than one.</summary>
    [Fact]
    public void TheFitIsTightButItIsStillAFit()
    {
        List<Point2D> drawn = Arc(200, 270, 400);

        FreehandFit fit = FreehandFitter.Fit(drawn);

        // The point of this test is only that the fit is doing something - a "fit" that returned one
        // segment per point would be a polyline wearing a curve's clothes. A 270-degree arc at a tight
        // tolerance legitimately needs a fair number of segments.
        Assert.True(fit.Segments < drawn.Count / 4,
            $"{fit.Segments} segments from {drawn.Count} points is not much of a fit");
        Assert.True(WorstDeviation(drawn, fit.Nodes) <= FreehandFitter.Tolerance);
    }

    /// <summary>A tighter tolerance gives at least as many segments, and never a worse fit.</summary>
    [Fact]
    public void TheToleranceIsTheTuning()
    {
        List<Point2D> drawn = Arc(150, 180, 200);

        FreehandFit loose = FreehandFitter.Fit(drawn, tolerance: 2.0);
        FreehandFit tight = FreehandFitter.Fit(drawn, tolerance: 0.05);

        Assert.True(tight.Segments >= loose.Segments,
            $"a tighter tolerance should not need fewer segments: {tight.Segments} vs {loose.Segments}");
        Assert.True(WorstDeviation(drawn, tight.Nodes) <= WorstDeviation(drawn, loose.Nodes) + 1e-6,
            "a tighter tolerance should not fit worse");
    }

    /// <summary>The nodes describe an open path: no handle before the first, none after the last.</summary>
    [Fact]
    public void TheFittedPathIsOpen()
    {
        FreehandFit fit = FreehandFitter.Fit(Arc(100, 90, 40));

        Assert.Equal(fit.Segments + 1, fit.Nodes.Count);
        Assert.True(fit.Nodes[0].InHandle.NearlyEquals(fit.Nodes[0].Anchor, 1e-9));
        Assert.True(fit.Nodes[^1].OutHandle.NearlyEquals(fit.Nodes[^1].Anchor, 1e-9));
    }

    // ---- degenerate input ------------------------------------------------------

    /// <summary>A click with no movement is not a stroke, and draws nothing.</summary>
    [Fact]
    public void AClickDrawsNothing()
    {
        Assert.Empty(FreehandFitter.Fit(new[] { new Point2D(5, 5) }).Nodes);
        Assert.Empty(FreehandFitter.Fit(Array.Empty<Point2D>()).Nodes);
    }

    /// <summary>Two points are one segment, and the handles are a third of the way along.</summary>
    [Fact]
    public void TwoPointsAreOneSegment()
    {
        FreehandFit fit = FreehandFitter.Fit(new[] { new Point2D(0, 0), new Point2D(90, 0) });

        Assert.Equal(1, fit.Segments);
        Assert.Equal(2, fit.Nodes.Count);
        Assert.Equal(30, fit.Nodes[0].OutHandle.X, 6);
        Assert.Equal(60, fit.Nodes[1].InHandle.X, 6);
    }

    /// <summary>
    /// Points closer together than the sampling step are dropped: a pointer repeats itself constantly, and
    /// fitting to those repetitions would put a segment on every one of them.
    /// </summary>
    [Fact]
    public void PointsCloserThanTheSamplingStepAreDropped()
    {
        var tremor = new List<Point2D>();
        for (int i = 0; i < 200; i++)
        {
            tremor.Add(new Point2D(i * 0.001, 0));
        }

        // Every point is within the sampling step of the last, so nothing but the first survives.
        Assert.Empty(FreehandFitter.Fit(tremor).Nodes);
    }

    /// <summary>The sampling rule is available while drawing, not only when fitting.</summary>
    [Fact]
    public void TheSamplingRuleCanBeAskedWhileDrawing()
    {
        var points = new List<Point2D> { new(0, 0) };

        Assert.False(FreehandFitter.ShouldKeep(points, new Point2D(0.01, 0)));
        Assert.True(FreehandFitter.ShouldKeep(points, new Point2D(FreehandFitter.SampleStep * 2, 0)));
        Assert.True(FreehandFitter.ShouldKeep(new List<Point2D>(), new Point2D(1, 1)));
    }
}
