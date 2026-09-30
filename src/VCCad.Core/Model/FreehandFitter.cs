using VCCad.Geometry;

namespace VCCad.Core.Model;

/// <summary>What a freehand stroke fitted to, and how well.</summary>
/// <param name="Nodes">The path's nodes, in order - an open path.</param>
/// <param name="WorstError">The largest distance any drawn point ended up from the fitted curve.</param>
/// <param name="Segments">How many cubic segments the stroke became.</param>
public sealed record FreehandFit(IReadOnlyList<PathNode> Nodes, double WorstError, int Segments);

/// <summary>
/// Fits a freehand stroke - the points a pointer visited - to a series of cubic Bézier segments.
///
/// This is the standard recursive least-squares fit: fit a cubic to a run of points, measure the worst
/// distance from the points to it, and either accept it or split at the worst point and fit both halves.
/// It terminates because every split shortens the run.
///
/// **The tolerance is the whole tuning**, and it is deliberately tight. This is a drawing tool: the curve
/// has to follow what the hand did, so the fit removes the jitter of a pointer and nothing else. A stroke
/// that is a straight line must come back as **one** segment - a fit that turned a ruled line into several
/// would be doing the opposite of what was drawn.
///
/// Sampling is separate from fitting: points closer together than <see cref="SampleStep"/> are dropped as
/// they arrive, because a pointer repeats itself and fitting to its repetitions would put a segment on
/// every one of them.
/// </summary>
public static class FreehandFitter
{
    private const double PointsPerMm = 72.0 / 25.4;

    /// <summary>
    /// How far inside the tolerance a fit is accepted. The error is measured at the fitted parameters,
    /// and a point's **nearest** distance to the curve can be a few percent further out when the
    /// parameterisation is imperfect - so accepting at exactly the tolerance produces curves that miss it.
    /// </summary>
    private const double Acceptance = 0.85;

    /// <summary>How far the fitted curve may sit from the drawn points: **0.2 mm**.</summary>
    public const double ToleranceMm = 0.2;

    /// <summary>The closest two captured points may be before one is dropped: **0.3 mm**.</summary>
    public const double SampleStepMm = 0.3;

    /// <summary>How far a fitted curve may sit from the points, in model units.</summary>
    public static double Tolerance => ToleranceMm * PointsPerMm;

    /// <summary>The distance a pointer must travel before another point is worth keeping.</summary>
    public static double SampleStep => SampleStepMm * PointsPerMm;

    /// <summary>
    /// Whether a point is far enough from the last one to be worth keeping. Used while drawing, so the
    /// capture is thinned as it happens rather than after the fact.
    /// </summary>
    public static bool ShouldKeep(IReadOnlyList<Point2D> points, Point2D candidate)
        => points.Count == 0 || Distance(points[^1], candidate) >= SampleStep;

    /// <summary>Fits the points to cubic segments. Fewer than two points is not a stroke.</summary>
    public static FreehandFit Fit(IReadOnlyList<Point2D> points, double? tolerance = null)
    {
        double error = tolerance ?? Tolerance;

        List<Point2D> thinned = Thin(points);
        if (thinned.Count < 2)
        {
            return new FreehandFit(Array.Empty<PathNode>(), 0, 0);
        }

        if (thinned.Count == 2)
        {
            List<PathNode> single = OneSegment(thinned[0], thinned[1]);
            return new FreehandFit(single, 0, 1);
        }

        var curves = new List<CubicBezier>();

        // Both tangents point **outward along the stroke**: the start one from the first point toward the
        // second, the end one from the last point back toward the second-to-last. Either of them the
        // wrong way round makes that end's handle stick out past its own end, so the fit fights itself
        // and splits a straight line into dozens of segments.
        FitCubic(
            thinned, 0, thinned.Count - 1,
            Normalize(thinned[0], thinned[1]),
            Normalize(thinned[^1], thinned[^2]),
            error, curves, 0);

        List<PathNode> nodes = ToNodes(curves);
        return new FreehandFit(nodes, WorstDistance(thinned, curves), curves.Count);
    }

    /// <summary>Drops points that repeat the one before them, which a pointer does constantly.</summary>
    private static List<Point2D> Thin(IReadOnlyList<Point2D> points)
    {
        var kept = new List<Point2D>();
        foreach (Point2D point in points)
        {
            if (ShouldKeep(kept, point))
            {
                kept.Add(point);
            }
        }

        return kept;
    }

    /// <summary>
    /// Fits a cubic to the points between <paramref name="first"/> and <paramref name="last"/>, splitting
    /// at the worst point when it does not fit.
    /// </summary>
    private static void FitCubic(
        List<Point2D> points, int first, int last,
        Vector2D startTangent, Vector2D endTangent, double error,
        List<CubicBezier> into, int depth)
    {
        if (depth > 24)
        {
            into.Add(OneCurve(points[first], points[last]));
            return;
        }

        if (last - first == 1)
        {
            // Two points: a segment whose handles are a third of the way along, which is the shape a
            // hand-drawn short stroke has.
            into.Add(OneCurve(points[first], points[last]));
            return;
        }

        double[] u = ChordLengthParameterize(points, first, last);
        CubicBezier curve = GenerateBezier(points, first, last, u, startTangent, endTangent);
        (double maxError, int split) = ComputeMaxError(points, first, last, curve, u);

        if (maxError <= error * Acceptance)
        {
            into.Add(curve);
            return;
        }

        // Close enough to be worth trying to re-space the parameters before splitting: the split is
        // expensive in segments, and a bad parameterisation is a common cause of a fit that just misses.
        if (maxError <= error * Acceptance * 4)
        {
            for (int attempt = 0; attempt < 4; attempt++)
            {
                u = Reparameterize(points, first, last, u, curve);
                curve = GenerateBezier(points, first, last, u, startTangent, endTangent);
                (maxError, split) = ComputeMaxError(points, first, last, curve, u);

                if (maxError <= error * Acceptance)
                {
                    into.Add(curve);
                    return;
                }
            }
        }

        if (split <= first)
        {
            split = first + 1;
        }

        if (split >= last)
        {
            split = last - 1;
        }

        // Backward, from the split point towards the left half. The left half's END tangent has to point
        // away from the stroke just as its start tangent does, and the right half uses its negation -
        // pointing one of them the wrong way makes the two halves fight at the join, and the fit then
        // splits again and again: a 270-degree arc came out as 214 segments from 400 points.
        Vector2D centreTangent = Normalize(points[split], points[split - 1]);

        FitCubic(points, first, split, startTangent, centreTangent, error, into, depth + 1);
        FitCubic(points, split, last, new Vector2D(-centreTangent.X, -centreTangent.Y), endTangent, error, into, depth + 1);
    }

    /// <summary>
    /// The least-squares cubic for the points, given the two end tangents. This is the heart of the fit:
    /// the handles' lengths are the two unknowns, and the two normal equations solve for them at once.
    /// </summary>
    private static CubicBezier GenerateBezier(
        List<Point2D> points, int first, int last, double[] u, Vector2D startTangent, Vector2D endTangent)
    {
        Point2D p0 = points[first];
        Point2D p3 = points[last];

        int count = last - first + 1;
        double c00 = 0, c01 = 0, c11 = 0, x0 = 0, x1 = 0;

        for (int i = 0; i < count; i++)
        {
            double t = u[i];
            double b0 = B0(t), b1 = B1(t), b2 = B2(t), b3 = B3(t);

            var a0 = new Vector2D(startTangent.X * b1, startTangent.Y * b1);
            var a1 = new Vector2D(endTangent.X * b2, endTangent.Y * b2);

            c00 += (a0.X * a0.X) + (a0.Y * a0.Y);
            c01 += (a0.X * a1.X) + (a0.Y * a1.Y);
            c11 += (a1.X * a1.X) + (a1.Y * a1.Y);

            double tmpx = points[first + i].X - ((p0.X * (b0 + b1)) + (p3.X * (b2 + b3)));
            double tmpy = points[first + i].Y - ((p0.Y * (b0 + b1)) + (p3.Y * (b2 + b3)));

            x0 += (a0.X * tmpx) + (a0.Y * tmpy);
            x1 += (a1.X * tmpx) + (a1.Y * tmpy);
        }

        double det = (c00 * c11) - (c01 * c01);
        double alphaStart;
        double alphaEnd;

        if (Math.Abs(det) > 1e-12)
        {
            alphaStart = ((x0 * c11) - (x1 * c01)) / det;
            alphaEnd = ((c00 * x1) - (c01 * x0)) / det;
        }
        else
        {
            double fallback = Distance(p0, p3) / 3.0;
            alphaStart = fallback;
            alphaEnd = fallback;
        }

        // The Wu/Barsky heuristic: a handle longer than the segment it spans, or pointing backwards, is
        // not a fit but an artefact of the arithmetic. Falling back to a third of the chord is what keeps
        // the curve sane on a run of nearly-collinear points.
        double span = Distance(p0, p3);
        double limit = span * 3.0;
        if (alphaStart < 1e-6 || alphaEnd < 1e-6 ||
            (span > 1e-9 && (alphaStart > limit || alphaEnd > limit)))
        {
            double fallback = span / 3.0;
            alphaStart = fallback;
            alphaEnd = fallback;
        }

        return new CubicBezier(
            p0,
            new Point2D(p0.X + (startTangent.X * alphaStart), p0.Y + (startTangent.Y * alphaStart)),
            new Point2D(
                p3.X + (endTangent.X * alphaEnd),
                p3.Y + (endTangent.Y * alphaEnd)),
            p3);
    }

    private static double[] ChordLengthParameterize(List<Point2D> points, int first, int last)
    {
        var u = new double[last - first + 1];
        for (int i = 1; i < u.Length; i++)
        {
            u[i] = u[i - 1] + Distance(points[first + i - 1], points[first + i]);
        }

        double total = u[^1];
        if (total < 1e-12)
        {
            for (int i = 0; i < u.Length; i++)
            {
                u[i] = (double)i / (u.Length - 1);
            }

            return u;
        }

        for (int i = 0; i < u.Length; i++)
        {
            u[i] /= total;
        }

        return u;
    }

    /// <summary>One Newton step per point, pulling each parameter toward the curve's nearest point.</summary>
    private static double[] Reparameterize(
        List<Point2D> points, int first, int last, double[] u, CubicBezier curve)
    {
        var updated = new double[u.Length];
        for (int i = 0; i < u.Length; i++)
        {
            updated[i] = NewtonRaphson(curve, points[first + i], u[i]);
        }

        return updated;
    }

    private static double NewtonRaphson(CubicBezier curve, Point2D point, double u)
    {
        Point2D on = curve.PointAt(u);
        Vector2D first = curve.DerivativeAt(u);
        Vector2D second = curve.SecondDerivativeAt(u);

        var diff = new Vector2D(on.X - point.X, on.Y - point.Y);
        double numerator = (diff.X * first.X) + (diff.Y * first.Y);
        double denominator = (first.X * first.X) + (first.Y * first.Y)
                             + (diff.X * second.X) + (diff.Y * second.Y);

        if (Math.Abs(denominator) < 1e-12)
        {
            return u;
        }

        return Math.Clamp(u - (numerator / denominator), 0, 1);
    }

    private static (double Error, int Split) ComputeMaxError(
        List<Point2D> points, int first, int last, CubicBezier curve, double[] u)
    {
        double maxError = 0;
        int split = (last - first + 1) / 2;

        for (int i = 1; i < last - first; i++)
        {
            Point2D on = curve.PointAt(u[i]);
            double distance = Distance(on, points[first + i]);

            if (distance >= maxError)
            {
                maxError = distance;
                split = first + i;
            }
        }

        return (maxError, split);
    }

    /// <summary>
    /// The largest distance any drawn point ended up from the fitted curve, measured against the curve
    /// itself rather than the fit's own estimate - which is what makes it a check on the fit.
    /// </summary>
    /// <summary>
    /// The largest distance any drawn point ended up from the fitted curve.
    ///
    /// Measured as the distance to the **polyline** standing in for the curve, not to its sample points:
    /// the distance to a sample is bounded below by the sampling interval, so a coarse sample reports a
    /// fit as worse than it is - which is exactly what the first version of this did.
    /// </summary>
    private static double WorstDistance(List<Point2D> points, List<CubicBezier> curves)
    {
        double worst = 0;

        foreach (Point2D point in points)
        {
            double nearest = double.MaxValue;
            foreach (CubicBezier curve in curves)
            {
                const int steps = 32;
                Point2D previous = curve.PointAt(0);

                for (int i = 1; i <= steps; i++)
                {
                    Point2D on = curve.PointAt((double)i / steps);
                    nearest = Math.Min(nearest, DistanceToSegment(point, previous, on));
                    previous = on;
                }
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
            return Distance(point, a);
        }

        double t = Math.Clamp((((point.X - a.X) * dx) + ((point.Y - a.Y) * dy)) / lengthSquared, 0, 1);
        return Distance(point, new Point2D(a.X + (dx * t), a.Y + (dy * t)));
    }
    private static List<PathNode> ToNodes(List<CubicBezier> curves)
    {
        var nodes = new List<PathNode>();

        for (int i = 0; i < curves.Count; i++)
        {
            CubicBezier curve = curves[i];

            if (i == 0)
            {
                nodes.Add(new PathNode(curve.P0) { OutHandle = curve.P1 });
            }
            else
            {
                nodes[^1].OutHandle = curve.P1;
            }

            nodes.Add(new PathNode(curve.P3) { InHandle = curve.P2 });
        }

        return nodes;
    }

    private static List<PathNode> OneSegment(Point2D from, Point2D to)
    {
        CubicBezier curve = OneCurve(from, to);
        return new List<PathNode>
        {
            new(from) { OutHandle = curve.P1 },
            new(to) { InHandle = curve.P2 },
        };
    }

    private static CubicBezier OneCurve(Point2D from, Point2D to)
    {
        var third = new Vector2D((to.X - from.X) / 3.0, (to.Y - from.Y) / 3.0);
        return new CubicBezier(
            from,
            new Point2D(from.X + third.X, from.Y + third.Y),
            new Point2D(to.X - third.X, to.Y - third.Y),
            to);
    }

    private static Vector2D Normalize(Point2D from, Point2D to)
    {
        double dx = to.X - from.X;
        double dy = to.Y - from.Y;
        double length = Math.Sqrt((dx * dx) + (dy * dy));
        return length < 1e-12 ? new Vector2D(1, 0) : new Vector2D(dx / length, dy / length);
    }

    private static double Distance(Point2D a, Point2D b)
        => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    private static double B0(double t) => (1 - t) * (1 - t) * (1 - t);

    private static double B1(double t) => 3 * t * (1 - t) * (1 - t);

    private static double B2(double t) => 3 * t * t * (1 - t);

    private static double B3(double t) => t * t * t;
}
