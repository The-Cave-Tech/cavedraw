using VCCad.Geometry;

namespace VCCad.Core.Model;

/// <summary>
/// Factories that produce ready-made path geometry for the shape tools, the API
/// convenience methods and the test suite. Everything here reduces to nodes and
/// handles (the only geometry the model stores); none of these helpers are magic
/// primitives, so the resulting paths remain fully editable node-by-node.
/// </summary>
public static class PathFactory
{
    /// <summary>
    /// The classic control-point factor for approximating a quarter circle with a
    /// single cubic Bézier:   k = 4/3 · tan(π/8) ≈ 0.552284749831.
    /// This yields a curve whose midpoint lies within ~2.7e-4 of the true radius —
    /// far below the editor's display resolution — and whose endpoints and
    /// tangents are exact.
    /// </summary>
    public const double Kappa = 0.5522847498307936;

    /// <summary>
    /// An open polyline through <paramref name="points"/> — every node is a corner
    /// (collapsed handles) so the result is N−1 straight segments.
    /// </summary>
    public static PathItem CreatePolyline(string name, IReadOnlyList<Point2D> points)
    {
        var path = new PathItem { Name = name };
        var sub = path.AddSubPath(closed: false);
        foreach (Point2D p in points)
        {
            sub.AppendNode(p);
        }

        return path;
    }

    /// <summary>A closed polygon through <paramref name="points"/>.</summary>
    public static PathItem CreatePolygon(string name, IReadOnlyList<Point2D> points)
    {
        var path = new PathItem { Name = name };
        var sub = path.AddSubPath(closed: true);
        foreach (Point2D p in points)
        {
            sub.AppendNode(p);
        }

        return path;
    }

    /// <summary>A closed rectangle (as four corner nodes → four straight segments).</summary>
    public static PathItem CreateRectangle(string name, Rect2D rect)
    {
        var path = new PathItem { Name = name };
        var sub = path.AddSubPath(closed: true);
        sub.AppendNode(new Point2D(rect.Left, rect.Top));
        sub.AppendNode(new Point2D(rect.Right, rect.Top));
        sub.AppendNode(new Point2D(rect.Right, rect.Bottom));
        sub.AppendNode(new Point2D(rect.Left, rect.Bottom));
        return path;
    }

    /// <summary>
    /// A straight open segment between two points. Because a "line" in VCCad is a
    /// cubic with collapsed handles, this is expressed with exactly two corner nodes.
    /// </summary>
    public static PathItem CreateLine(string name, Point2D from, Point2D to)
        => CreatePolyline(name, new[] { from, to });

    /// <summary>
    /// A closed ellipse approximated by four cubic Béziers (one per quadrant).
    ///
    /// Each quadrant cubic runs from angle θ to θ+π/2. With P0 on the ellipse at θ
    /// and P3 at θ+π/2, the two interior control points sit at radius·k beyond the
    /// tangents of those anchor points, which is the standard construction
    /// described by the <see cref="Kappa"/> constant.
    /// </summary>
    public static PathItem CreateEllipse(string name, Point2D center, double radiusX, double radiusY)
    {
        double rx = Math.Abs(radiusX);
        double ry = Math.Abs(radiusY);

        var path = new PathItem { Name = name };
        var sub = path.AddSubPath(closed: true);

        // Four anchor points, one per quadrant, rotated around the centre.
        Point2D right = center + new Vector2D(rx, 0.0);
        Point2D bottom = center + new Vector2D(0.0, ry);
        Point2D left = center + new Vector2D(-rx, 0.0);
        Point2D top = center + new Vector2D(0.0, -ry);

        // Handles hug the tangents. E.g. at the rightmost anchor the tangent is
        // vertical (±Y); the outgoing control sits k·ry above it.
        sub.Nodes.Add(new PathNode(right, right + new Vector2D(0.0, -ry * Kappa), right + new Vector2D(0.0, ry * Kappa)));
        sub.Nodes.Add(new PathNode(bottom, bottom + new Vector2D(rx * Kappa, 0.0), bottom + new Vector2D(-rx * Kappa, 0.0)));
        sub.Nodes.Add(new PathNode(left, left + new Vector2D(0.0, ry * Kappa), left + new Vector2D(0.0, -ry * Kappa)));
        sub.Nodes.Add(new PathNode(top, top + new Vector2D(-rx * Kappa, 0.0), top + new Vector2D(rx * Kappa, 0.0)));
        return path;
    }
}
