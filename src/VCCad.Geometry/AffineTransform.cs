namespace VCCad.Geometry;

/// <summary>
/// A general 2D affine transformation represented by six coefficients:
///
/// <code>
///        ┌          ┐     ┌   ┐     ┌              ┐
///  x'    │ a  c  e  │     │ x │     │ a·x + c·y + e │
///  y'  = │ b  d  f  │  ·  │ y │  =  │ b·x + d·y + f │
///   1    │ 0  0  1  │     │ 1 │     │              │
///        └          ┘     └   ┘     └              ┘
/// </code>
///
/// Column-vector convention: the matrix is applied to points written as columns
/// (<c>p' = M·p</c>). Composition therefore reads right-to-left: the transform
/// written <em>closest</em> to the point is applied <em>first</em>. This mirrors
/// PDF's <c>cm</c> operator and PostScript matrix semantics, which keeps the PDF
/// exporter and the geometry kernel in lock step.
///
/// Affine maps preserve straight lines and parallelism but may change lengths and
/// angles (scale/shear) — exactly what is needed for layer/group transforms,
/// coordinate flips (top-left model space → PDF user space) and artboard mapping.
/// </summary>
public readonly record struct AffineTransform(double A, double B, double C, double D, double E, double F)
{
    /// <summary>The identity transform: leaves every point unchanged.</summary>
    public static AffineTransform Identity { get; } = new(1.0, 0.0, 0.0, 1.0, 0.0, 0.0);

    /// <summary>Determinant |a·d − c·b|; zero ⟺ the transform collapses to a line/point.</summary>
    public double Determinant => A * D - C * B;

    /// <summary>True when the transform is invertible (non-zero determinant).</summary>
    public bool IsInvertible => Math.Abs(Determinant) > MathUtils.Epsilon;

    /// <summary>A pure translation by ⟨tx, ty⟩.</summary>
    public static AffineTransform CreateTranslation(double tx, double ty)
        => new(1.0, 0.0, 0.0, 1.0, tx, ty);

    /// <summary>A pure scale about the origin.</summary>
    public static AffineTransform CreateScale(double sx, double sy)
        => new(sx, 0.0, 0.0, sy, 0.0, 0.0);

    /// <summary>
    /// Uniform scale about an arbitrary pivot point <paramref name="about"/> — the
    /// common "zoom about a screen anchor" operation used by the canvas view.
    /// Implemented as translate→scale→translate around the pivot.
    /// </summary>
    public static AffineTransform CreateScaleAround(Point2D about, double sx, double sy)
        => CreateTranslation(about.X, about.Y)
            .Compose(CreateScale(sx, sy))
            .Compose(CreateTranslation(-about.X, -about.Y));

    /// <summary>Rotation by <paramref name="angleRadians"/> about the origin, CCW-positive.</summary>
    public static AffineTransform CreateRotation(double angleRadians)
    {
        double cos = Math.Cos(angleRadians);
        double sin = Math.Sin(angleRadians);
        return new AffineTransform(cos, sin, -sin, cos, 0.0, 0.0);
    }

    /// <summary>Rotation about an arbitrary pivot (see <see cref="CreateScaleAround"/>).</summary>
    public static AffineTransform CreateRotationAround(Point2D about, double angleRadians)
        => CreateTranslation(about.X, about.Y)
            .Compose(CreateRotation(angleRadians))
            .Compose(CreateTranslation(-about.X, -about.Y));

    /// <summary>Shear along X by tangent <paramref name="skewX"/> and along Y by <paramref name="skewY"/>.</summary>
    public static AffineTransform CreateSkew(double skewX, double skewY)
        => new(1.0, Math.Tan(skewY), Math.Tan(skewX), 1.0, 0.0, 0.0);

    /// <summary>
    /// Builds the affine map that carries source rectangle <paramref name="from"/>
    /// onto destination rectangle <paramref name="to"/>.
    ///
    /// This is the workhorse for translating between coordinate spaces that are
    /// axis-aligned but differently scaled/originated — e.g. mapping model points
    /// (top-left origin) into a PDF page (bottom-left origin) or a screen viewport.
    ///
    /// sₓ = to.W / from.W ; sᵧ = to.H / from.H ; tx = to.X − from.X·sₓ ; ty likewise.
    /// </summary>
    public static AffineTransform MapRect(Rect2D from, Rect2D to)
    {
        if (from.IsEmpty || to.IsEmpty)
        {
            return Identity;
        }

        double sx = to.Width / from.Width;
        double sy = to.Height / from.Height;
        double tx = to.X - from.X * sx;
        double ty = to.Y - from.Y * sy;
        return new AffineTransform(sx, 0.0, 0.0, sy, tx, ty);
    }

    /// <summary>Applies the transform to a point: <c>p' = M·p</c>.</summary>
    public Point2D Transform(Point2D p)
        => new(A * p.X + C * p.Y + E, B * p.X + D * p.Y + F);

    /// <summary>
    /// Applies the transform to a vector (displacement). Translation is ignored —
    /// vectors have no origin, so the e/f column must not leak into the result.
    /// </summary>
    public Vector2D Transform(Vector2D v)
        => new(A * v.X + C * v.Y, B * v.X + D * v.Y);

    /// <summary>
    /// Transforms a rectangle by mapping all four corners and taking the resulting
    /// bounding box. Because a general affine map can shear/rotate, the image of a
    /// rectangle is only guaranteed to fit inside the returned axis-aligned box.
    /// </summary>
    public Rect2D Transform(Rect2D r)
    {
        Point2D tl = Transform(new Point2D(r.Left, r.Top));
        Point2D tr = Transform(new Point2D(r.Right, r.Top));
        Point2D br = Transform(new Point2D(r.Right, r.Bottom));
        Point2D bl = Transform(new Point2D(r.Left, r.Bottom));
        return Rect2D.FromPoints(tl, tr).Union(Rect2D.FromPoints(br, bl));
    }

    /// <summary>
    /// Matrix multiplication under the column-vector convention: <c>this ∘ other</c>
    /// means "apply <paramref name="other"/> first, then apply this".
    ///
    /// Concretely, the returned transform maps x → this.Transform(other.Transform(x)).
    /// </summary>
    public AffineTransform Compose(AffineTransform other)
    {
        // Row (this) × column (other) products of the 3×3 matrices; the bottom row
        // [0 0 1] is constant so only a..f are recomputed.
        return new AffineTransform(
            A * other.A + C * other.B,
            B * other.A + D * other.B,
            A * other.C + C * other.D,
            B * other.C + D * other.D,
            A * other.E + C * other.F + E,
            B * other.E + D * other.F + F);
    }

    /// <summary>
    /// The inverse transform, when it exists. Throws <see cref="InvalidOperationException"/>
    /// for singular (non-invertible) matrices.
    ///
    /// Inverse of ┌ a c e ┐      1    ┌  d  −c   c·f−d·e ┐
    ///            │ b d f │  =  ───  │ −b   a   b·e−a·f │   where Δ = a·d − c·b
    ///            └ 0 0 1 ┘       Δ   └  0   0      Δ    ┘
    /// </summary>
    public AffineTransform Inverted()
    {
        double det = Determinant;
        if (Math.Abs(det) <= MathUtils.Epsilon)
        {
            throw new InvalidOperationException("Cannot invert a singular affine transform.");
        }

        double invDet = 1.0 / det;
        return new AffineTransform(
            D * invDet,
            -B * invDet,
            -C * invDet,
            A * invDet,
            (C * F - D * E) * invDet,
            (B * E - A * F) * invDet);
    }
}
