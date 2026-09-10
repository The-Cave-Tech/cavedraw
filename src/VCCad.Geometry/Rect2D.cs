namespace VCCad.Geometry;

/// <summary>
/// An axis-aligned bounding rectangle, stored as top-left origin (X, Y) plus width
/// and height, exactly as it will appear in the model and in the UI. All
/// construction helpers normalise so that Width/Height are never negative.
///
/// "Top" here means minimum Y: the struct is handedness agnostic, matching the
/// rest of the geometry kernel.
/// </summary>
public readonly record struct Rect2D(double X, double Y, double Width, double Height)
{
    /// <summary>An empty rectangle at the origin with zero extent.</summary>
    public static Rect2D Empty { get; } = new(0.0, 0.0, 0.0, 0.0);

    /// <summary>Left edge (minimum X).</summary>
    public double Left => X;

    /// <summary>Top edge (minimum Y).</summary>
    public double Top => Y;

    /// <summary>Right edge (maximum X) = X + Width.</summary>
    public double Right => X + Width;

    /// <summary>Bottom edge (maximum Y) = Y + Height.</summary>
    public double Bottom => Y + Height;

    /// <summary>The centre point of the rectangle.</summary>
    public Point2D Center => new(X + Width * 0.5, Y + Height * 0.5);

    /// <summary>The rectangle as a size.</summary>
    public Size2D Size => new(Width, Height);

    /// <summary>Whether the rectangle has zero or negative extent in either axis.</summary>
    public bool IsEmpty => Width <= 0.0 || Height <= 0.0;

    /// <summary>
    /// Builds a rectangle from two corner points, normalising so that X,Y is the
    /// minimum corner regardless of argument order.
    /// </summary>
    public static Rect2D FromPoints(Point2D a, Point2D b)
    {
        double minX = Math.Min(a.X, b.X);
        double maxX = Math.Max(a.X, b.X);
        double minY = Math.Min(a.Y, b.Y);
        double maxY = Math.Max(a.Y, b.Y);
        return new Rect2D(minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>
    /// Builds the tight bounding rectangle over an arbitrary sequence of points.
    /// Returns <see cref="Empty"/> when the sequence is empty.
    /// </summary>
    public static Rect2D FromPoints(IEnumerable<Point2D> points)
    {
        bool any = false;
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;

        foreach (Point2D p in points)
        {
            any = true;
            minX = Math.Min(minX, p.X);
            minY = Math.Min(minY, p.Y);
            maxX = Math.Max(maxX, p.X);
            maxY = Math.Max(maxY, p.Y);
        }

        return any ? new Rect2D(minX, minY, maxX - minX, maxY - minY) : Empty;
    }

    /// <summary>Smallest rectangle containing both <c>this</c> and <paramref name="other"/>.</summary>
    public Rect2D Union(Rect2D other)
    {
        // Rect2D.Empty is the canonical "nothing here" value and acts as the
        // identity element of the union. Everything else — including degenerate
        // boxes with zero width or height — carries real extent in at least one
        // axis and must participate in the min/max computation. Treating
        // degenerate boxes as "empty" would silently discard, say, a purely
        // horizontal line when computing artwork bounds.
        if (this == Empty)
        {
            return other;
        }

        if (other == Empty)
        {
            return this;
        }

        double minX = Math.Min(Left, other.Left);
        double minY = Math.Min(Top, other.Top);
        double maxX = Math.Max(Right, other.Right);
        double maxY = Math.Max(Bottom, other.Bottom);
        return new Rect2D(minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>Smallest rectangle containing this rect and a single point.</summary>
    public Rect2D Union(Point2D p) => Union(new Rect2D(p.X, p.Y, 0.0, 0.0));

    /// <summary>
    /// Grows the rectangle by <paramref name="amount"/> on every side.
    /// A negative amount shrinks it. Result is not re-normalised (callers should
    /// not shrink past collapse).
    /// </summary>
    public Rect2D Inflated(double amount)
        => new(X - amount, Y - amount, Width + 2.0 * amount, Height + 2.0 * amount);

    /// <summary>True when the point lies on or inside the rectangle.</summary>
    public bool Contains(Point2D p)
        => p.X >= Left && p.X <= Right && p.Y >= Top && p.Y <= Bottom;

    /// <summary>True when <paramref name="other"/> lies entirely inside this rectangle.</summary>
    public bool Contains(Rect2D other)
        => other.Left >= Left && other.Right <= Right
           && other.Top >= Top && other.Bottom <= Bottom;

    /// <summary>True when the two rectangles overlap (interiors or edges touch).</summary>
    public bool Intersects(Rect2D other)
        => Left < other.Right && Right > other.Left
           && Top < other.Bottom && Bottom > other.Top;

    /// <summary>Intersection of two rectangles; empty when they do not overlap.</summary>
    public Rect2D Intersect(Rect2D other)
    {
        double minX = Math.Max(Left, other.Left);
        double minY = Math.Max(Top, other.Top);
        double maxX = Math.Min(Right, other.Right);
        double maxY = Math.Min(Bottom, other.Bottom);

        if (maxX < minX || maxY < minY)
        {
            return Empty;
        }

        return new Rect2D(minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>True when all four components are within tolerance of the other rect.</summary>
    public bool NearlyEquals(Rect2D other, double epsilon = MathUtils.Epsilon)
        => MathUtils.NearlyEquals(X, other.X, epsilon)
           && MathUtils.NearlyEquals(Y, other.Y, epsilon)
           && MathUtils.NearlyEquals(Width, other.Width, epsilon)
           && MathUtils.NearlyEquals(Height, other.Height, epsilon);
}
