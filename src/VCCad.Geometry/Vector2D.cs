namespace VCCad.Geometry;

/// <summary>
/// A 2D free vector — the difference between two <see cref="Point2D"/>s.
/// Vectors carry direction and magnitude only; they have no location.
///
/// Conventions used throughout VCCad:
/// <list type="bullet">
/// <item>Coordinates are expressed in points (1/72 inch), stored as <c>double</c>.</item>
/// <item>The kernel is orientation-agnostic: "screen vs PDF handedness" is the job of
/// the export layer, never of the math.</item>
/// </list>
/// </summary>
public readonly record struct Vector2D(double X, double Y)
{
    /// <summary>The zero vector ⟨0,0⟩.</summary>
    public static Vector2D Zero { get; } = new(0.0, 0.0);

    /// <summary>Unit vector along +X, ⟨1,0⟩.</summary>
    public static Vector2D UnitX { get; } = new(1.0, 0.0);

    /// <summary>Unit vector along +Y, ⟨0,1⟩.</summary>
    public static Vector2D UnitY { get; } = new(0.0, 1.0);

    /// <summary>Squared Euclidean length. Cheaper than <see cref="Length"/>; use it for comparisons.</summary>
    public double LengthSquared => X * X + Y * Y;

    /// <summary>Euclidean length ‖v‖ = √(x² + y²).</summary>
    public double Length => Math.Sqrt(LengthSquared);

    /// <summary>
    /// This vector normalised to unit length. The zero vector cannot be normalised
    /// and is returned unchanged; callers that care should guard with
    /// <see cref="IsZero"/> first.
    /// </summary>
    public Vector2D Normalized
    {
        get
        {
            double len = Length;
            if (len <= MathUtils.Epsilon)
            {
                return this;
            }

            return new Vector2D(X / len, Y / len);
        }
    }

    /// <summary>True when the vector is (within tolerance) the zero vector.</summary>
    public bool IsZero => LengthSquared <= MathUtils.Epsilon * MathUtils.Epsilon;

    /// <summary>Negation — the same direction reversed.</summary>
    public Vector2D Negated => new(-X, -Y);

    /// <summary>
    /// The perpendicular (also "orthogonal") vector ⟨−y, x⟩: v rotated +90° (CCW in a
    /// right-handed frame). Useful for normals, offsets and stroke outlines.
    /// </summary>
    public Vector2D Perpendicular => new(-Y, X);

    /// <summary>Adds two vectors component-wise.</summary>
    public static Vector2D operator +(Vector2D a, Vector2D b) => new(a.X + b.X, a.Y + b.Y);

    /// <summary>Subtracts <paramref name="b"/> from <paramref name="a"/> component-wise.</summary>
    public static Vector2D operator -(Vector2D a, Vector2D b) => new(a.X - b.X, a.Y - b.Y);

    /// <summary>Negates both components.</summary>
    public static Vector2D operator -(Vector2D a) => a.Negated;

    /// <summary>Scales a vector by a scalar.</summary>
    public static Vector2D operator *(Vector2D a, double s) => new(a.X * s, a.Y * s);

    /// <summary>Scales a vector by a scalar (commutative).</summary>
    public static Vector2D operator *(double s, Vector2D a) => a * s;

    /// <summary>Divides a vector by a scalar.</summary>
    public static Vector2D operator /(Vector2D a, double s) => new(a.X / s, a.Y / s);

    /// <summary>
    /// Dot product <c>a·b = |a||b|cos θ</c>. Zero when the vectors are perpendicular.
    /// </summary>
    public double Dot(Vector2D other) => X * other.X + Y * other.Y;

    /// <summary>
    /// 2D cross product (scalar) <c>a×b = a.x·b.y − a.y·b.x</c>. Its sign encodes the
    /// signed angle from <c>this</c> to <paramref name="other"/> and its magnitude is
    /// the area of the parallelogram they span.
    /// </summary>
    public double Cross(Vector2D other) => X * other.Y - Y * other.X;

    /// <summary>Angle of this vector relative to +X, in radians within (−π, π].</summary>
    public double Angle => Math.Atan2(Y, X);

    /// <summary>
    /// Rotates this vector by <paramref name="angleRadians"/> (CCW in a right-handed frame).
    /// Rotation matrix applied to a column vector:
    /// <code>[ cosθ  −sinθ ] [x]</code>
    /// <code>[ sinθ   cosθ ] [y]</code>
    /// </summary>
    public Vector2D Rotated(double angleRadians)
    {
        double cos = Math.Cos(angleRadians);
        double sin = Math.Sin(angleRadians);
        return new Vector2D(X * cos - Y * sin, X * sin + Y * cos);
    }

    /// <summary>Distance between the tips of two vectors — same as ‖a − b‖.</summary>
    public double DistanceTo(Vector2D other) => (other - this).Length;

    /// <summary>Projects this vector onto <paramref name="onto"/>, yielding a parallel vector.</summary>
    public Vector2D ProjectedOnto(Vector2D onto)
    {
        double lenSq = onto.LengthSquared;
        if (lenSq <= MathUtils.Epsilon)
        {
            return Zero;
        }

        double scale = Dot(onto) / lenSq;
        return onto * scale;
    }
}
