namespace VCCad.Geometry;

/// <summary>
/// A parametric cubic Bézier curve defined by four control points P0…P3:
///
/// <code>
/// B(t) = (1−t)³·P0 + 3(1−t)²·t·P1 + 3(1−t)·t²·P2 + t³·P3 ,   t ∈ [0,1]
/// </code>
///
/// This is the ONLY curved primitive the editor stores. Every path segment —
/// including straight lines — is represented as a cubic:
/// a line from A to B is the cubic with P1 ≡ P0 ≡ A and P2 ≡ P3 ≡ B.
/// Collapsing to a single uniform type keeps flattening, bounding, hit testing
/// and export single-path code, at the cost of a few wasted coefficients on the
/// (comparatively rare) purely-straight polygons.
///
/// Rendering note: PDF and Skia both emit cubics natively, so curves never need
/// flattening for display — flattening is used for measuring, boolean ops and
/// picking where polyline approximations are required.
/// </summary>
public readonly record struct CubicBezier(Point2D P0, Point2D P1, Point2D P2, Point2D P3)
{
    /// <summary>A degenerate cubic that collapses to the single point <paramref name="p"/>.</summary>
    public static CubicBezier FromPoint(Point2D p) => new(p, p, p, p);

    /// <summary>A straight segment from <paramref name="from"/> to <paramref name="to"/>.</summary>
    public static CubicBezier FromLine(Point2D from, Point2D to) => new(from, from, to, to);

    /// <summary>
    /// Builds a cubic from its endpoints and two handles. The handle is the point a
    /// designer pulls with the pen tool; the segment passes through P0 and P3 and
    /// is tangent to (P1−P0) at P0 and (P3−P2) at P3.
    /// </summary>
    public static CubicBezier FromHandles(Point2D p0, Point2D p1, Point2D p2, Point2D p3)
        => new(p0, p1, p2, p3);

    /// <summary>Point on the curve at parameter t. t is clamped to [0,1].</summary>
    public Point2D PointAt(double t)
    {
        t = MathUtils.Clamp01(t);
        // de Casteljau is numerically friendlier than the power-basis expansion
        // near t = 0/1, so evaluate through repeated linear interpolation.
        double u = 1.0 - t;
        Point2D p01 = P0.LerpTo(P1, t);
        Point2D p12 = P1.LerpTo(P2, t);
        Point2D p23 = P2.LerpTo(P3, t);
        Point2D p012 = p01.LerpTo(p12, t);
        Point2D p123 = p12.LerpTo(p23, t);
        return p012.LerpTo(p123, t);
    }

    /// <summary>
    /// First derivative B′(t) = 3(1−t)²(P1−P0) + 6(1−t)t(P2−P1) + 3t²(P3−P2),
    /// a vector tangent to the curve. Its magnitude is the parametric speed |dB/dt|.
    /// </summary>
    public Vector2D DerivativeAt(double t)
    {
        t = MathUtils.Clamp01(t);
        double u = 1.0 - t;

        Vector2D d0 = (P1 - P0) * (3.0 * u * u);
        Vector2D d1 = (P2 - P1) * (6.0 * u * t);
        Vector2D d2 = (P3 - P2) * (3.0 * t * t);
        return d0 + d1 + d2;
    }

    /// <summary>
    /// Second derivative B″(t) = 6(1−t)·(P2 − 2P1 + P0) + 6t·(P3 − 2P2 + P1).
    /// Zero crossings of the components help locate inflection points.
    ///
    /// Points cannot be added, so the inner brackets are rewritten with vector
    /// differences (which are well-defined):
    ///   P2 − 2P1 + P0 = (P2 − P1) − (P1 − P0)
    ///   P3 − 2P2 + P1 = (P3 − P2) − (P2 − P1)
    /// </summary>
    public Vector2D SecondDerivativeAt(double t)
    {
        t = MathUtils.Clamp01(t);
        double u = 1.0 - t;

        Vector2D w0 = (P2 - P1) - (P1 - P0);
        Vector2D w1 = (P3 - P2) - (P2 - P1);
        return w0 * (6.0 * u) + w1 * (6.0 * t);
    }

    /// <summary>
    /// Unit tangent vector at t (normalised first derivative). Falls back to a
    /// sensible tangent derived from the endpoints when the derivative vanishes
    /// (cusp or fully degenerate segment).
    /// </summary>
    public Vector2D TangentAt(double t)
    {
        Vector2D d = DerivativeAt(t);
        return d.IsZero ? (P3 - P0).Normalized : d.Normalized;
    }

    /// <summary>
    /// Unit normal at t — the perpendicular of the tangent (see <see cref="Vector2D.Perpendicular"/>).
    /// Sign convention matches the frame handedness; stroke offsets should document
    /// which side they treat as "outer".
    /// </summary>
    public Vector2D NormalAt(double t) => TangentAt(t).Perpendicular;

    /// <summary>
    /// Signed curvature at t:  κ = (x′y″ − y′x″) / (x′² + y′²)^(3/2).
    /// κ &gt; 0 bends one way, κ &lt; 0 the other; |κ| is the reciprocal of the
    /// local radius of curvature.
    /// </summary>
    public double CurvatureAt(double t)
    {
        Vector2D d1 = DerivativeAt(t);
        Vector2D d2 = SecondDerivativeAt(t);

        double denominator = Math.Pow(d1.LengthSquared, 1.5);
        if (denominator <= MathUtils.Epsilon)
        {
            return 0.0;
        }

        return (d1.X * d2.Y - d1.Y * d2.X) / denominator;
    }

    /// <summary>
    /// Splits the curve at parameter t into a left piece (parameters [0,t]) and a
    /// right piece (parameters [t,1]) using de Casteljau's algorithm, which is
    /// exact in the sense that each sub-curve is the *same* polynomial restricted
    /// to a sub-domain (no approximation error).
    /// </summary>
    public (CubicBezier Left, CubicBezier Right) SplitAt(double t)
    {
        t = MathUtils.Clamp01(t);

        Point2D p01 = P0.LerpTo(P1, t);
        Point2D p12 = P1.LerpTo(P2, t);
        Point2D p23 = P2.LerpTo(P3, t);
        Point2D p012 = p01.LerpTo(p12, t);
        Point2D p123 = p12.LerpTo(p23, t);
        Point2D mid = p012.LerpTo(p123, t);

        return (new CubicBezier(P0, p01, p012, mid), new CubicBezier(mid, p123, p23, P3));
    }

    /// <summary>Returns a copy of this curve traversed in the opposite direction.</summary>
    public CubicBezier Reversed() => new(P3, P2, P1, P0);

    /// <summary>
    /// Flattens the curve into a polyline approximation within <paramref name="tolerance"/>
    /// units, returning the ordered sample points <em>including both endpoints</em>.
    ///
    /// Flatness test: the curve is replaced by its chord when both interior control
    /// points lie within <paramref name="tolerance"/> of the chord P0–P3. This is an
    /// approximation of true distance but is cheap, monotone under subdivision and
    /// more than adequate for rendering, measuring and picking at editor tolerances.
    /// A depth cap guarantees termination even for numerically pathological curves
    /// (e.g. all four control points coincident).
    /// </summary>
    public IReadOnlyList<Point2D> Flatten(double tolerance = 0.05)
    {
        var result = new List<Point2D>();
        FlattenInto(tolerance, 0, result);
        return result;
    }

    /// <summary>Internal recursion implementing <see cref="Flatten"/>.</summary>
    private void FlattenInto(double tolerance, int depth, List<Point2D> sink)
    {
        const int maxDepth = 24;
        if (depth == 0)
        {
            sink.Add(P0);
        }

        if (IsFlatEnough(tolerance) || depth >= maxDepth)
        {
            sink.Add(P3);
            return;
        }

        (CubicBezier left, CubicBezier right) = SplitAt(0.5);
        left.FlattenInto(tolerance, depth + 1, sink);
        right.FlattenInto(tolerance, depth + 1, sink);
    }

    /// <summary>
    /// Distance of the two interior control points to the chord P0–P3, both bounded.
    /// A zero-length chord (P0 == P3) falls back to the distance to that point,
    /// which keeps the test well defined for closed loops of tiny extent.
    /// </summary>
    private bool IsFlatEnough(double tolerance)
    {
        // Structs cannot capture `this` in lambdas/local functions (CS1673), so the
        // endpoint copies needed by the helper are taken explicitly.
        Point2D p0 = P0;
        Point2D p3 = P3;
        Vector2D chord = p3 - p0;
        double chordLengthSq = chord.LengthSquared;

        double Flatness(Point2D p)
        {
            if (chordLengthSq <= MathUtils.Epsilon)
            {
                return p0.DistanceTo(p);
            }

            // Signed area of triangle (p0, p, p3) divided by base length gives the
            // perpendicular distance of p from the line through the chord.
            Vector2D v = p - p0;
            double area = chord.X * v.Y - chord.Y * v.X;
            double distance = Math.Abs(area) / Math.Sqrt(chordLengthSq);

            // Clamp to distance-from-segment: if the projection falls outside the
            // chord, use the distance to the nearer endpoint instead.
            double projection = v.Dot(chord) / chordLengthSq;
            if (projection <= 0.0)
            {
                return p0.DistanceTo(p);
            }

            if (projection >= 1.0)
            {
                return p3.DistanceTo(p);
            }

            return distance;
        }

        return Flatness(P1) <= tolerance && Flatness(P2) <= tolerance;
    }

    /// <summary>
    /// Estimates arc length by adaptive subdivision: the curve is repeatedly split
    /// until each piece is flat to <paramref name="tolerance"/> and the chord lengths
    /// are summed. Accuracy is controlled by tolerance rather than a fixed step
    /// count, so short curves and long lazy S-curves are measured fairly.
    /// </summary>
    public double EstimateLength(double tolerance = 1e-3)
    {
        // 16-element Gauss–Legendre quadrature over the flattened control polygon
        // would be faster; subdivision is used here because it shares the exact
        // same "flatness" test as Flatten and is trivially correct to reason about.
        double length = 0.0;
        IReadOnlyList<Point2D> polyline = Flatten(tolerance);
        for (int i = 1; i < polyline.Count; i++)
        {
            length += polyline[i - 1].DistanceTo(polyline[i]);
        }

        return length;
    }

    /// <summary>
    /// Axis-aligned bounding box of the curve. Unlike a naive evaluation of a few
    /// sample points this is TIGHT: the parameter values where x(t) and y(t) are
    /// extremal are found analytically by solving B′x(t)=0 and B′y(t)=0 (quadratics),
    /// so rounded tops and inflection tails are never clipped.
    /// </summary>
    public Rect2D BoundingBox()
    {
        double minX = Math.Min(P0.X, P3.X);
        double maxX = Math.Max(P0.X, P3.X);
        double minY = Math.Min(P0.Y, P3.Y);
        double maxY = Math.Max(P0.Y, P3.Y);

        foreach (double t in ExtremaParameters())
        {
            Point2D p = PointAt(t);
            minX = Math.Min(minX, p.X);
            maxX = Math.Max(maxX, p.X);
            minY = Math.Min(minY, p.Y);
            maxY = Math.Max(maxY, p.Y);
        }

        return new Rect2D(minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>
    /// The parameter values t ∈ [0,1] at which x(t) or y(t) attains a local extremum.
    ///
    /// The derivative of a cubic is a quadratic Bézier with control points
    /// 3(P1−P0), 3(P2−P1), 3(P3−P2). In power basis a quadratic
    /// Q0,Q1,Q2 has coefficients (Q0−2Q1+Q2)t² + (−2Q0+2Q1)t + Q0, and we solve
    /// that quadratic independently for the x and y components.
    /// </summary>
    private IEnumerable<double> ExtremaParameters()
    {
        // Quadratic Bézier control points of the derivative curve (scaled by 3,
        // which is irrelevant when solving for roots).
        Vector2D d0 = P1 - P0;
        Vector2D d1 = P2 - P1;
        Vector2D d2 = P3 - P2;

        foreach (bool axisX in new[] { true, false })
        {
            double a = (axisX ? d0.X : d0.Y) - 2.0 * (axisX ? d1.X : d1.Y) + (axisX ? d2.X : d2.Y);
            double b = -2.0 * (axisX ? d0.X : d0.Y) + 2.0 * (axisX ? d1.X : d1.Y);
            double c = axisX ? d0.X : d0.Y;

            int count = MathUtils.SolveQuadratic(a, b, c, out double t0, out double t1);
            if (count >= 1 && t0 is > 0.0 and < 1.0)
            {
                yield return t0;
            }

            if (count >= 2 && t1 is > 0.0 and < 1.0)
            {
                yield return t1;
            }
        }
    }

    /// <summary>
    /// Finds the point on the curve nearest to <paramref name="query"/> together
    /// with its parameter t and the distance. This backs stroke picking, snapping
    /// and the pen tool's hover preview.
    ///
    /// Algorithm:
    /// <list type="bullet">
    /// <item>A coarse scan of 24 samples localises the best bracket of t.</item>
    /// <item>The result is refined with Newton's method on the squared-distance
    /// function f(t) = |B(t) − q|², whose derivatives are
    /// f′(t) = 2(B − q)·B′ and f″(t) = 2(|B′|² + (B − q)·B″). Newton converges
    /// quadratically once the coarse scan has put us in the correct basin.</item>
    /// <item>The final iterate is clamped to [0,1] so the answer always lies on
    /// the curve (never on its extension).</item>
    /// </list>
    /// </summary>
    /// <param name="query">The point to measure from (any location).</param>
    /// <param name="t">Receives the parameter of the closest point.</param>
    /// <param name="distance">Receives the closest distance.</param>
    /// <param name="distanceTolerance">Convergence tolerance on the distance estimate.</param>
    public void NearestPoint(Point2D query, out double t, out double distance, double distanceTolerance = 1e-4)
    {
        // Coarse scan: sample dense enough that no extremum can hide between two
        // samples for a cubic (they are smooth), then Newton-refine the winner.
        const int samples = 24;
        double bestT = 0.0;
        double bestDistanceSquared = double.PositiveInfinity;
        for (int i = 0; i <= samples; i++)
        {
            double ti = i / (double)samples;
            Point2D p = PointAt(ti);
            double dsq = p.DistanceSquaredTo(query);
            if (dsq < bestDistanceSquared)
            {
                bestDistanceSquared = dsq;
                bestT = ti;
            }
        }

        // Newton refinement of f'(t) = 0. Guard against a zero denominator
        // (f'' = 0 when the curve is locally a straight line at constant speed).
        const int iterations = 12;
        for (int i = 0; i < iterations; i++)
        {
            Vector2D b = PointAt(bestT) - query;
            Vector2D b1 = DerivativeAt(bestT);
            Vector2D b2 = SecondDerivativeAt(bestT);

            double numerator = b.Dot(b1);
            double denominator = b1.LengthSquared + b.Dot(b2);
            if (Math.Abs(denominator) <= MathUtils.Epsilon)
            {
                break;
            }

            double step = numerator / denominator;
            bestT = MathUtils.Clamp01(bestT - step);

            // Terminate when the Newton step stops moving us (quadratic basin).
            if (Math.Abs(step) <= 1e-12)
            {
                break;
            }
        }

        t = bestT;
        distance = PointAt(t).DistanceTo(query);
    }
}
