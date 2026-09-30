using VCCad.Geometry;

namespace VCCad.Core.Model;

/// <summary>
/// Editing one segment of a shape edits all of them.
///
/// This is what makes a shape a shape rather than a path that started out regular. A five-pointed star
/// is ten segments, and a person who pulls one of them into a concave bow expects **all ten to bow
/// inward** - not one bent segment and nine straight ones. Making them agree by hand, ten times, is the
/// work this removes; and a star whose points disagree is not a star, it is a mistake.
///
/// Two things make it work:
///
/// - **The orbit.** Each segment has equivalents: the motions that map the shape onto itself
///   (rotations about its centre, and reflections when it has an axis) map each segment onto another.
///   Those motions are computed from the geometry rather than assumed from the kind, so a rounded
///   rectangle's four straight sides form one orbit and its four corner arcs another - which is right,
///   because bowing a side and bowing a corner are different edits.
///
/// - **Each segment's own frame.** The bow is applied along every segment's *own* normal, pointing
///   away from the shape's centre. That is what makes "inward" read as inward all the way round: a
///   reflected segment bows toward the centre too, rather than bowing outward because its winding
///   happens to run the other way.
/// </summary>
public static class ShapeSymmetry
{
    /// <summary>How many times the outline repeats around its centre. One means it does not.</summary>
    public static int RotationOrder(PathItem path)
    {
        ShapeParameters? p = path.Shape?.Parameters;
        if (path.Shape is null || p is null)
        {
            return 1;
        }

        return path.Shape.Kind switch
        {
            ShapeKind.Star or ShapeKind.Polygon or ShapeKind.Cloud => Math.Max(3, p.Points),
            ShapeKind.Rectangle or ShapeKind.RoundedRectangle => 2,
            _ => 1,
        };
    }

    /// <summary>Whether the shape has an axis of symmetry through its centre.</summary>
    public static bool Mirrors(PathItem path) => path.Shape?.Kind is
        ShapeKind.Star or ShapeKind.Polygon or ShapeKind.Rectangle or ShapeKind.RoundedRectangle
        or ShapeKind.Cloud or ShapeKind.Heart or ShapeKind.Trapezoid or ShapeKind.Arrow;

    /// <summary>
    /// The segments equivalent to <paramref name="segment"/> under the shape's own symmetry, itself
    /// included. One entry - the segment alone - when the shape has no symmetry, or is not a shape.
    /// </summary>
    public static IReadOnlyList<int> Orbit(PathItem path, int segment)
    {
        List<(Point2D Start, Point2D End)> segments = Endpoints(path);
        if (segment < 0 || segment >= segments.Count)
        {
            return Array.Empty<int>();
        }

        var orbit = new SortedSet<int> { segment };
        if (path.Shape is null)
        {
            return orbit.ToList();
        }

        Point2D centre = Centre(path);
        int order = Math.Max(1, RotationOrder(path));
        var motions = new List<(double Degrees, bool Mirror)>();
        for (int k = 0; k < order; k++)
        {
            motions.Add((360.0 * k / order, false));
            if (Mirrors(path))
            {
                motions.Add((360.0 * k / order, true));
            }
        }

        (Point2D start, Point2D end) = segments[segment];
        foreach ((double degrees, bool mirror) in motions)
        {
            Point2D mappedStart = Apply(start, centre, degrees, mirror);
            Point2D mappedEnd = Apply(end, centre, degrees, mirror);

            for (int j = 0; j < segments.Count; j++)
            {
                // Either orientation: a reflection reverses winding, and the segment it lands on is
                // still the equivalent one.
                bool same = Near(segments[j].Start, mappedStart) && Near(segments[j].End, mappedEnd);
                bool reversed = Near(segments[j].End, mappedStart) && Near(segments[j].Start, mappedEnd);
                if (same || reversed)
                {
                    orbit.Add(j);
                }
            }
        }

        return orbit.ToList();
    }

    /// <summary>
    /// Bows one segment of a shape, and every equivalent segment with it. Positive is away from the
    /// shape's centre, negative is toward it - so a negative amount makes a star's points concave, all
    /// of them, which is what "bow inward" has to mean.
    ///
    /// Returns false when the path is not a shape, which is the caller's answer to "did anything move".
    /// </summary>
    public static bool Bow(PathItem path, int segment, double amount)
    {
        if (path.Shape is null)
        {
            return false;
        }

        IReadOnlyList<int> orbit = Orbit(path, segment);
        if (orbit.Count == 0)
        {
            return false;
        }

        foreach (int index in orbit)
        {
            BowOne(path, index, amount);
        }

        path.GeometryChanged();
        return true;
    }

    /// <summary>
    /// Bows a single segment: a cubic whose control points sit a third of the way along it, offset
    /// along its own normal. A cubic built that way passes through three quarters of the offset at its
    /// midpoint, so the handles are pushed by four thirds of the requested bow - which is what makes
    /// <paramref name="amount"/> mean the distance the segment actually moves.
    /// </summary>
    private static void BowOne(PathItem path, int segment, double amount)
    {
        List<(Point2D Start, Point2D End)> segments = Endpoints(path);
        SubPath sub = path.SubPaths[0];
        int index = segment % sub.Nodes.Count;
        int next = (index + 1) % sub.Nodes.Count;

        (Point2D start, Point2D end) = segments[segment];
        PathNode a = sub.Nodes[index];
        PathNode b = sub.Nodes[next];

        var along = new Vector2D(end.X - start.X, end.Y - start.Y);
        double length = Math.Sqrt((along.X * along.X) + (along.Y * along.Y));
        if (length < 1e-9)
        {
            return;
        }

        var normal = new Vector2D(-along.Y / length, along.X / length);

        // Point it away from the shape's centre, so a negative amount is inward on every segment
        // however that segment is oriented.
        Point2D centre = Centre(path);
        var middle = new Point2D((start.X + end.X) / 2, (start.Y + end.Y) / 2);
        if (((middle.X - centre.X) * normal.X) + ((middle.Y - centre.Y) * normal.Y) < 0)
        {
            normal = new Vector2D(-normal.X, -normal.Y);
        }

        double offset = amount * 4.0 / 3.0;
        var lift = new Vector2D(normal.X * offset, normal.Y * offset);
        var third = new Vector2D(along.X / 3, along.Y / 3);

        a.OutHandle = new Point2D(start.X + third.X + lift.X, start.Y + third.Y + lift.Y);
        b.InHandle = new Point2D(end.X - third.X + lift.X, end.Y - third.Y + lift.Y);
    }

    /// <summary>The shape's centre: what it was made from, or the middle of what it is now.</summary>
    private static Point2D Centre(PathItem path)
    {
        if (path.Shape is { } shape)
        {
            return shape.Parameters.Centre;
        }

        Rect2D box = path.BoundingBox();
        return box.Center;
    }

    private static List<(Point2D Start, Point2D End)> Endpoints(PathItem path)
    {
        var list = new List<(Point2D, Point2D)>();
        foreach (SubPath sub in path.SubPaths)
        {
            int count = sub.Nodes.Count;
            if (count == 0)
            {
                continue;
            }

            int segments = sub.IsClosed ? count : count - 1;
            for (int i = 0; i < segments; i++)
            {
                list.Add((sub.Nodes[i].Anchor, sub.Nodes[(i + 1) % count].Anchor));
            }
        }

        return list;
    }

    private static Point2D Apply(Point2D point, Point2D centre, double degrees, bool mirror)
    {
        double x = point.X - centre.X;
        double y = point.Y - centre.Y;
        if (mirror)
        {
            x = -x;
        }

        double radians = degrees * Math.PI / 180.0;
        double cos = Math.Cos(radians);
        double sin = Math.Sin(radians);
        return new Point2D(centre.X + (x * cos) - (y * sin), centre.Y + (x * sin) + (y * cos));
    }

    private static bool Near(Point2D a, Point2D b) => a.NearlyEquals(b, 1e-6);
}
