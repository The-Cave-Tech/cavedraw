namespace VCCad.Geometry;

/// <summary>
/// Shared numeric utilities for the geometry kernel.
///
/// All floating point comparisons in VCCad go through the helpers here so that a
/// single, consistent notion of "close enough" is used throughout the code base.
/// This matters enormously for editing code (snapping, hit testing) and for the
/// unit tests, which compare geometry against analytically derived values.
/// </summary>
public static class MathUtils
{
    /// <summary>
    /// Relative machine epsilon scaled constant. We deliberately avoid going all
    /// the way down to <c>double.Epsilon</c> (≈ 5e-324) because nearly all real
    /// values in the editor are the result of arithmetic and carry error of the
    /// order <c>1e-12 .. 1e-9</c> relative to their magnitude.
    /// </summary>
    public const double Epsilon = 1e-9;

    /// <summary>
    /// A looser tolerance used when working in drawing units (points). A quarter
    /// of a pixel at 4x retina zoom would be ~0.01 pt, so 1e-6 pt is comfortably
    /// inside "this is visually a single point".
    /// </summary>
    public const double UnitTolerance = 1e-6;

    /// <summary>Returns <c>true</c> when <paramref name="a"/> and <paramref name="b"/>
    /// differ by less than <see cref="Epsilon"/> (relative comparison).</summary>
    public static bool NearlyEquals(double a, double b)
        => NearlyEquals(a, b, Epsilon);

    /// <summary>Absolute + relative hybrid comparison with a caller supplied epsilon.</summary>
    public static bool NearlyEquals(double a, double b, double epsilon)
    {
        double diff = Math.Abs(a - b);
        if (diff < epsilon)
        {
            return true;
        }

        // Relative comparison guards against huge coordinates where the absolute
        // epsilon above is meaningless (e.g. artboards far from the origin).
        double scale = Math.Max(Math.Abs(a), Math.Abs(b));
        return diff <= epsilon * scale;
    }

    /// <summary>Clamps <paramref name="value"/> into the inclusive range [min, max].</summary>
    public static double Clamp(double value, double min, double max)
        => value < min ? min : (value > max ? max : value);

    /// <summary>
    /// Clamps a value into [0, 1] — the parameter domain shared by every Bézier
    /// function in this library. Keeping the domain explicit avoids the classic
    /// bug of silently evaluating curves outside their span.
    /// </summary>
    public static double Clamp01(double value) => Clamp(value, 0.0, 1.0);

    /// <summary>Linear interpolation <c>(1 - t)·a + t·b</c>. <c>t</c> is NOT clamped.</summary>
    public static double Lerp(double a, double b, double t) => a + (b - a) * t;

    /// <summary>
    /// Solves the quadratic <c>a·t² + b·t + c = 0</c>.
    /// Returns the number of distinct real roots written into <paramref name="rootA"/>
    /// and <paramref name="rootB"/> (0, 1 or 2), in ascending order.
    ///
    /// Numerical notes:
    /// <list type="bullet">
    /// <item>A near-zero leading coefficient is handled as the linear case.</item>
    /// <item>The "Citardauq" formulation <c>2c / (-b ∓ √(b² - 4ac))</c> is used for one
    /// root to avoid catastrophic cancellation when <c>b</c> is large compared to
    /// the discriminant.</item>
    /// </list>
    /// </summary>
    public static int SolveQuadratic(double a, double b, double c, out double rootA, out double rootB)
    {
        rootA = double.NaN;
        rootB = double.NaN;

        // Degenerate: not actually quadratic. Fall through to a linear solve.
        if (Math.Abs(a) <= Epsilon)
        {
            if (Math.Abs(b) <= Epsilon)
            {
                return 0;                       // 0 = c : constant, no roots
            }

            rootA = -c / b;
            return 1;
        }

        // Work with the monic form to reduce magnitude of intermediate terms.
        // p = b/a, q = c/a  ⇒  t² + p·t + q = 0.
        double p = b / a;
        double q = c / a;

        double discriminant = p * p - 4.0 * q;

        // Discriminant at or below zero ⇒ one (double) real root at the vertex.
        if (discriminant <= Epsilon)
        {
            if (discriminant > -Epsilon)
            {
                rootA = rootB = -p / 2.0;
                return 1;
            }

            return 0;                           // strictly complex conjugate pair
        }

        double rootDisc = Math.Sqrt(discriminant);

        // Citardauq form avoids cancellation for one of the two roots.
        // Standard form:  t = (-p ± √(p² - 4q)) / 2
        //  q is typically negative here so  t₁ = 2q / (-p - √(...) )
        // is the numerically stable choice; t₂ = (-p - √(...)) / 2  complements it.
        double t1 = (-p - rootDisc) / 2.0;
        double t2 = (2.0 * q) / (-p - rootDisc);

        // Keep ascending order regardless of which formula produced which root.
        if (t1 < t2)
        {
            rootA = t1;
            rootB = t2;
        }
        else
        {
            rootA = t2;
            rootB = t1;
        }

        return 2;
    }

    /// <summary>
    /// Signs with exact-zero handled: returns -1, 0 or 1. Useful in winding and
    /// monotonicity code where a pure <c>Math.Sign</c> on noisy data would flip.
    /// </summary>
    public static int SignZero(double value)
        => Math.Abs(value) <= Epsilon ? 0 : Math.Sign(value);
}
