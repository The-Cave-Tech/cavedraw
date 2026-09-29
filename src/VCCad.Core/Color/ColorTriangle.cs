using VCCad.Core.Model;
using VCCad.Geometry;

namespace VCCad.Core.Color;

/// <summary>
/// The three vertices of the picker triangle, in order.
///
/// The ordering is the whole point: <see cref="First"/> is the vertex that sits
/// on the ring at the selected angle, and <see cref="Second"/> and
/// <see cref="Third"/> follow it **clockwise around the screen**.
/// </summary>
public readonly record struct TriangleCorners(Point2D First, Point2D Second, Point2D Third)
{
    /// <summary>Vertex 0 (first/selected), 1 (second), 2 (third). Out of range throws.</summary>
    public Point2D this[int index] => index switch
    {
        0 => First,
        1 => Second,
        2 => Third,
        _ => throw new ArgumentOutOfRangeException(nameof(index), index, "A triangle has three corners."),
    };

    /// <summary>The centroid — the point equidistant from all three colours.</summary>
    public Point2D Centroid => new(
        (First.X + Second.X + Third.X) / 3.0,
        (First.Y + Second.Y + Third.Y) / 3.0);

    /// <summary>Side length of the (equilateral) triangle.</summary>
    public double SideLength => First.DistanceTo(Second);

    /// <summary>True when all three vertices are within tolerance of a circle of the given radius.</summary>
    public bool AllTouchRing(Point2D center, double radius, double tolerance = 1e-9)
        => Math.Abs(First.DistanceTo(center) - radius) <= tolerance
           && Math.Abs(Second.DistanceTo(center) - radius) <= tolerance
           && Math.Abs(Third.DistanceTo(center) - radius) <= tolerance;
}

/// <summary>
/// The three colours of the picker triangle, aligned with
/// <see cref="TriangleCorners"/>: <see cref="First"/> is the ring colour at the
/// selected angle, <see cref="Second"/> is white and <see cref="Third"/> is black.
/// </summary>
public readonly record struct CornerColors(ColorRgb First, ColorRgb Second, ColorRgb Third)
{
    /// <summary>Colour 0 (hue), 1 (white), 2 (black). Out of range throws.</summary>
    public ColorRgb this[int index] => index switch
    {
        0 => First,
        1 => Second,
        2 => Third,
        _ => throw new ArgumentOutOfRangeException(nameof(index), index, "A triangle has three colours."),
    };
}

/// <summary>
/// Weights of a point over the three triangle corners. The weights sum to 1 for
/// every point in the triangle's plane, are all non-negative inside it, and one
/// weight is 1 at each corner.
/// </summary>
public readonly record struct Barycentric(double First, double Second, double Third)
{
    /// <summary>Sum of the weights. 1 for any affine combination.</summary>
    public double Sum => First + Second + Third;

    /// <summary>True when the point lies inside (or on) the triangle.</summary>
    public bool IsInside(double epsilon = 1e-9)
        => First >= -epsilon && Second >= -epsilon && Third >= -epsilon;

    /// <summary>
    /// The nearest point inside the triangle, as weights: negative weights are
    /// discarded and the rest renormalised. Used when a click lands just outside
    /// the edge and the picker should still select the closest edge colour.
    /// </summary>
    public Barycentric Clamped()
    {
        double a = Math.Max(First, 0.0);
        double b = Math.Max(Second, 0.0);
        double c = Math.Max(Third, 0.0);
        double sum = a + b + c;
        if (sum <= 0.0)
        {
            // Degenerate triangle (zero radius); fall back to the first corner.
            return new Barycentric(1.0, 0.0, 0.0);
        }

        return new Barycentric(a / sum, b / sum, c / sum);
    }
}

/// <summary>One stop of the triangle's three-way gradient: a corner and the colour there.</summary>
public readonly record struct TriangleGradientStop(Point2D Position, ColorRgb Color);

/// <summary>
/// The triangle fill, as the three stops a renderer needs: position and colour
/// at each corner. Interpolating the three stops by barycentric weight
/// reproduces <see cref="ColorTriangle.ColorAt(Barycentric, CornerColors)"/>
/// exactly.
/// </summary>
public sealed record TriangleGradient(
    TriangleGradientStop First,
    TriangleGradientStop Second,
    TriangleGradientStop Third)
{
    /// <summary>The three stops, in triangle order.</summary>
    public IReadOnlyList<TriangleGradientStop> Stops => new[] { First, Second, Third };
}

/// <summary>
/// The inscribed picker triangle and the barycentric colour model inside it.
///
/// For a selected ring angle the triangle is equilateral, circumscribed by the
/// ring (all three vertices touch it). Its <see cref="TriangleCorners.First"/>
/// vertex sits on the ring at that angle and adopts the ring colour there;
/// going **clockwise** the second vertex is white and the third is black. The
/// colour at a point is the barycentric blend of those three corner colours, and
/// <see cref="WeightsForColor"/> inverts that blend exactly, which is what places
/// the small white selection circle.
/// </summary>
public static class ColorTriangle
{
    /// <summary>Angle between consecutive corners: 360° / 3.</summary>
    public const double CornerStepDegrees = 120.0;

    /// <summary>
    /// The three corners for a selected ring angle. All lie on the ring; the
    /// first is at <paramref name="angleDegrees"/>, the second 120° clockwise of
    /// it, the third a further 120° clockwise.
    /// </summary>
    public static TriangleCorners Corners(Point2D center, double radius, double angleDegrees)
        => new(
            SpectrumRing.PointAtAngle(center, radius, angleDegrees),
            SpectrumRing.PointAtAngle(center, radius, angleDegrees + CornerStepDegrees),
            SpectrumRing.PointAtAngle(center, radius, angleDegrees + 2.0 * CornerStepDegrees));

    /// <summary>
    /// The three corner colours for a selected ring angle: the ring colour at
    /// the angle, then white, then black, clockwise.
    /// </summary>
    public static CornerColors CornerColors(double angleDegrees)
        => new(SpectrumRing.ColorAtAngle(angleDegrees), ColorRgb.White, ColorRgb.Black);

    /// <summary>
    /// The barycentric weights of a point with respect to a triangle. The result
    /// sums to 1 everywhere; weights go negative outside the triangle (the
    /// mapping is affine, not clamped — use <see cref="Barycentric.Clamped"/> to
    /// pull a point back inside).
    /// </summary>
    public static Barycentric Barycentric(Point2D point, TriangleCorners corners)
    {
        Point2D a = corners.First;
        Point2D b = corners.Second;
        Point2D c = corners.Third;

        double v0x = b.X - a.X, v0y = b.Y - a.Y;
        double v1x = c.X - a.X, v1y = c.Y - a.Y;
        double v2x = point.X - a.X, v2y = point.Y - a.Y;

        double d00 = v0x * v0x + v0y * v0y;
        double d01 = v0x * v1x + v0y * v1y;
        double d11 = v1x * v1x + v1y * v1y;
        double d20 = v2x * v0x + v2y * v0y;
        double d21 = v2x * v1x + v2y * v1y;

        double denominator = d00 * d11 - d01 * d01;
        if (Math.Abs(denominator) <= 1e-12)
        {
            // Degenerate (zero-radius) triangle: everything is the first corner.
            return new Barycentric(1.0, 0.0, 0.0);
        }

        double second = (d11 * d20 - d01 * d21) / denominator;
        double third = (d00 * d21 - d01 * d20) / denominator;
        return new Barycentric(1.0 - second - third, second, third);
    }

    /// <summary>The point at a set of barycentric weights (the inverse of <see cref="Barycentric"/>).</summary>
    public static Point2D PointFromWeights(Barycentric weights, TriangleCorners corners)
        => new(
            weights.First * corners.First.X + weights.Second * corners.Second.X + weights.Third * corners.Third.X,
            weights.First * corners.First.Y + weights.Second * corners.Second.Y + weights.Third * corners.Third.Y);

    /// <summary>The colour at a set of barycentric weights: the blend of the three corner colours.</summary>
    public static ColorRgb ColorAt(Barycentric weights, CornerColors colors)
        => new(
            weights.First * colors.First.R + weights.Second * colors.Second.R + weights.Third * colors.Third.R,
            weights.First * colors.First.G + weights.Second * colors.Second.G + weights.Third * colors.Third.G,
            weights.First * colors.First.B + weights.Second * colors.Second.B + weights.Third * colors.Third.B,
            weights.First * colors.First.A + weights.Second * colors.Second.A + weights.Third * colors.Third.A);

    /// <summary>The colour at a point inside (or extrapolated from) the triangle.</summary>
    public static ColorRgb ColorAt(Point2D point, TriangleCorners corners, CornerColors colors)
        => ColorAt(Barycentric(point, corners), colors);

    /// <summary>
    /// The weights that reproduce a colour from the three corner colours.
    ///
    /// Writing the corner colours as pure hue (max = 1, min = 0), white and
    /// black, the HSV value is <c>max</c> and the HSV saturation times the value
    /// is <c>max - min</c>, so the weights are <c>(V - min, min, 1 - V)</c>. This
    /// is the exact inverse of <see cref="ColorAt(Barycentric, CornerColors)"/>
    /// and needs no triangle: the weights depend only on the colour.
    /// </summary>
    public static Barycentric WeightsForColor(ColorRgb color)
    {
        double r = Math.Clamp(color.R, 0.0, 1.0);
        double g = Math.Clamp(color.G, 0.0, 1.0);
        double b = Math.Clamp(color.B, 0.0, 1.0);

        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        return new Barycentric(max - min, min, 1.0 - max);
    }

    /// <summary>
    /// Where the selection circle for a colour sits in the triangle: the inverse
    /// of reading the colour at a point. A point inside the triangle maps to a
    /// colour whose weights recover that same point, so clicking and reading back
    /// is stable.
    /// </summary>
    public static Point2D PointForColor(ColorRgb color, TriangleCorners corners)
        => PointFromWeights(WeightsForColor(color), corners);

    /// <summary>The nearest point inside the triangle to <paramref name="point"/>.</summary>
    public static Point2D ClampToTriangle(Point2D point, TriangleCorners corners)
        => PointFromWeights(Barycentric(point, corners).Clamped(), corners);

    /// <summary>The three gradient stops of the triangle fill: a corner and its colour.</summary>
    public static TriangleGradient Gradient(TriangleCorners corners, CornerColors colors)
        => new(
            new TriangleGradientStop(corners.First, colors.First),
            new TriangleGradientStop(corners.Second, colors.Second),
            new TriangleGradientStop(corners.Third, colors.Third));
}
