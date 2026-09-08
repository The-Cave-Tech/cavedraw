namespace VCCad.Geometry;

/// <summary>
/// A 2D location — a point in the plane.
///
/// The invariant that separates <see cref="Point2D"/> from <see cref="Vector2D"/>:
/// points are locations (affine space), vectors are displacements (linear space).
/// Adding two points is therefore illegal by construction; subtracting two points
/// yields the displacement vector between them.
/// </summary>
public readonly record struct Point2D(double X, double Y)
{
    /// <summary>The origin of the coordinate space.</summary>
    public static Point2D Zero { get; } = new(0.0, 0.0);

    /// <summary>Adds a displacement vector to a point, yielding a new location.</summary>
    public static Point2D operator +(Point2D p, Vector2D v) => new(p.X + v.X, p.Y + v.Y);

    /// <summary>Adds a displacement vector to a point, yielding a new location.</summary>
    public static Point2D operator +(Vector2D v, Point2D p) => p + v;

    /// <summary>Subtracts a displacement vector from a point.</summary>
    public static Point2D operator -(Point2D p, Vector2D v) => new(p.X - v.X, p.Y - v.Y);

    /// <summary>Subtracts one point from another: the vector FROM <paramref name="b"/> TO this point.</summary>
    public static Vector2D operator -(Point2D a, Point2D b) => new(a.X - b.X, a.Y - b.Y);

    /// <summary>Euclidean distance to another point.</summary>
    public double DistanceTo(Point2D other) => (other - this).Length;

    /// <summary>Squared distance to another point (cheaper than <see cref="DistanceTo"/>).</summary>
    public double DistanceSquaredTo(Point2D other)
    {
        double dx = X - other.X;
        double dy = Y - other.Y;
        return dx * dx + dy * dy;
    }

    /// <summary>
    /// Linearly interpolates between <c>this</c> (t = 0) and <paramref name="other"/>
    /// (t = 1). Values of t outside [0,1] extrapolate beyond the segment.
    /// </summary>
    public Point2D LerpTo(Point2D other, double t)
        => new(MathUtils.Lerp(X, other.X, t), MathUtils.Lerp(Y, other.Y, t));

    /// <summary>
    /// Component-wise minimum — useful when folding a set of points into a bounding box.
    /// </summary>
    public Point2D Min(Point2D other) => new(Math.Min(X, other.X), Math.Min(Y, other.Y));

    /// <summary>Component-wise maximum — see <see cref="Min"/>.</summary>
    public Point2D Max(Point2D other) => new(Math.Max(X, other.X), Math.Max(Y, other.Y));

    /// <summary>Rounds coordinates to the nearest integral point (pixel snapping helper).</summary>
    public Point2D Rounded() => new(Math.Round(X), Math.Round(Y));

    /// <summary>True when both coordinates are within tolerance of the other point.</summary>
    public bool NearlyEquals(Point2D other, double epsilon = MathUtils.Epsilon)
        => MathUtils.NearlyEquals(X, other.X, epsilon)
           && MathUtils.NearlyEquals(Y, other.Y, epsilon);
}
