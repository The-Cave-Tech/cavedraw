namespace VCCad.Geometry;

/// <summary>
/// Real-root solvers for low-degree polynomials. Degree ≤ 3 is all the editor
/// needs: path segments are cubics, so bounding boxes, extrema and (via Bézier
/// clipping) intersections reduce to solving quadratics and cubics.
///
/// Every solver returns roots sorted ascending and silently ignores complex
/// solutions. Callers filter to the interval they care about (usually t ∈ [0,1]).
/// </summary>
public static class PolynomialRoots
{
    /// <summary>
    /// Solves <c>a·t³ + b·t² + c·t + d = 0</c> for its real roots.
    ///
    /// Algorithm (cardano with a trigonometric branch for the three-real-root
    /// case, which is numerically fragile in the plain cardano form):
    ///
    /// 1. Normalise to the monic depressed cubic  t³ + p·t + q = 0 via the
    ///    substitution t = u − b/(3a)  where  p = (3ac − b²)/(3a²),
    ///    q = (2b³ − 9abc + 27a²d)/(27a³).
    /// 2. Discriminant  Δ = (q/2)² + (p/3)³:
    ///    - Δ &gt; 0   → one real root u₁ = cubert(−q/2 + √Δ) + cubert(−q/2 − √Δ).
    ///    - Δ = 0   → double or triple roots.
    ///    - Δ &lt; 0   → three real roots; solve via cos(⅓·acos(…)) which is
    ///      numerically stable where cardano's formula would cancel catastrophically.
    /// 3. Shift back by −b/(3a).
    ///
    /// Degenerate lower-degree inputs (a≈0) fall through to the quadratic/linear
    /// solvers in <see cref="MathUtils"/>.
    /// </summary>
    /// <param name="roots">Receives the real roots, ascending. Length ≤ 3.</param>
    /// <returns>The number of distinct real roots found.</returns>
    public static int SolveCubic(double a, double b, double c, double d, Span<double> roots)
    {
        if (Math.Abs(a) <= MathUtils.Epsilon)
        {
            // Not a cubic: delegate downward, preserving the "ascending" contract.
            int count = MathUtils.SolveQuadratic(b, c, d, out double r0, out double r1);
            roots[0] = r0;
            if (count == 2)
            {
                roots[1] = r1;
            }

            return count;
        }

        // Normalise so the leading coefficient is 1 (improves conditioning).
        b /= a;
        c /= a;
        d /= a;

        // Depressed cubic substitution t = u − b/3.
        double p = c - b * b / 3.0;
        double q = d + (2.0 * b * b * b - 9.0 * b * c) / 27.0;

        // p is exactly zero ⇒ t³ + q = 0, pure cube root, single real root.
        if (Math.Abs(p) <= MathUtils.Epsilon)
        {
            double u = Math.Cbrt(-q);
            roots[0] = u - b / 3.0;
            return 1;
        }

        // Work in the scaled variable to keep intermediate magnitudes sane.
        double p3 = p / 3.0;
        double disc = q * q / 4.0 + p3 * p3 * p3;
        double halfQ = q / 2.0;

        if (disc > MathUtils.Epsilon)
        {
            // One real root.
            double sq = Math.Sqrt(disc);
            double u = Math.Cbrt(-halfQ + sq) + Math.Cbrt(-halfQ - sq);
            roots[0] = u - b / 3.0;
            return 1;
        }

        double offset = -b / 3.0;

        if (Math.Abs(disc) <= MathUtils.Epsilon)
        {
            // Double/triple real root (discriminant zero).
            double u = Math.Cbrt(-halfQ);            // 2·cubert(−q/2) repeated
            roots[0] = 2.0 * u - b / 3.0;
            roots[1] = -u - b / 3.0;
            return 2;
        }

        // Three distinct real roots — trigonometric branch (stable form):
        //   uₖ = 2·√(−p/3)·cos( (1/3)·acos( (3q/(2p))·√(−3/p) ) − 2πk/3 ), k=0,1,2
        double r = Math.Sqrt(-p3);                       // = √(−p/3)
        double phi = Math.Acos((3.0 * q) / (2.0 * p) * Math.Sqrt(-3.0 / p)) / 3.0;
        double twoR = 2.0 * r;

        double u0 = twoR * Math.Cos(phi);
        double u1 = twoR * Math.Cos(phi - 2.0 * Math.PI / 3.0);
        double u2 = twoR * Math.Cos(phi - 4.0 * Math.PI / 3.0);

        roots[0] = u0 + offset;
        roots[1] = u1 + offset;
        roots[2] = u2 + offset;
        roots[..3].Sort();
        return 3;
    }
}
