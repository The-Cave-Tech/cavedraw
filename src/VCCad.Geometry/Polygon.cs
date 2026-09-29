namespace VCCad.Geometry;

/// <summary>
/// A closed polygon, as the flattened form of a path.
///
/// Selection asks questions that a bounding box cannot answer: does a marquee enclose what
/// survives of a rectangle once a yin-yang has cut it? Curves are approximated to line
/// segments to make that tractable, and the approximation is fine because the answer is a yes
/// or a no about a region, not a curve.
/// </summary>
public sealed class Polygon
{
    private readonly List<Point2D> _points;

    private readonly List<List<Point2D>> _rings;

    /// <summary>Creates a polygon from one ring of points, in order.</summary>
    public Polygon(IEnumerable<Point2D> points)
        => _rings = new List<List<Point2D>> { points.ToList() };

    /// <summary>
    /// Creates a polygon from several rings.
    ///
    /// A clip with a hole in it - an even-odd clip, or a yin-yang - is not one ring: the hole
    /// is a second ring, and a point is inside when it is enclosed an odd number of times.
    /// Concatenating the rings into one list does not describe a hole at all; it describes a
    /// self-crossing loop, which is what the first attempt at this test built.
    /// </summary>
    public Polygon(IEnumerable<IEnumerable<Point2D>> rings)
        => _rings = rings.Select(r => r.ToList()).ToList();

    /// <summary>The outer ring, in order. What most callers mean by "the polygon".</summary>
    public IReadOnlyList<Point2D> Points => _rings.Count > 0 ? _rings[0] : Array.Empty<Point2D>();

    /// <summary>Every ring, outer first.</summary>
    public IReadOnlyList<IReadOnlyList<Point2D>> Rings => _rings;

    public int Count => Points.Count;

    public bool IsEmpty => _rings.Count == 0 || _rings.All(r => r.Count < 3);

    /// <summary>The box the polygon occupies.</summary>
    public Rect2D Bounds
    {
        get
        {
            if (Points.Count == 0)
            {
                return Rect2D.Empty;
            }

            double left = Points[0].X, right = Points[0].X;
            double top = Points[0].Y, bottom = Points[0].Y;

            foreach (Point2D p in _rings.SelectMany(r => r))
            {
                left = Math.Min(left, p.X);
                right = Math.Max(right, p.X);
                top = Math.Min(top, p.Y);
                bottom = Math.Max(bottom, p.Y);
            }

            return new Rect2D(left, top, right - left, bottom - top);
        }
    }

    /// <summary>Whether a point is inside, by the even-odd rule over every ring.</summary>
    public bool Contains(Point2D point)
    {
        if (IsEmpty)
        {
            return false;
        }

        bool inside = false;

        foreach (List<Point2D> ring in _rings)
        {
            for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            {
                Point2D a = ring[i];
                Point2D b = ring[j];

                // A ray along +X: count the edges it crosses.
                if ((a.Y > point.Y) != (b.Y > point.Y) &&
                    point.X < ((b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y)) + a.X)
                {
                    inside = !inside;
                }
            }
        }

        return inside;
    }

    /// <summary>
    /// The part of this polygon inside <paramref name="outer"/>, which must be convex.
    ///
    /// Sutherland-Hodgman: walk the subject's edges and keep the pieces on the inside of each
    /// of the clipper's edges in turn. A rectangle is convex, and a marquee is a rectangle, so
    /// this is exactly the operation the selection needs - and it gives back a region rather
    /// than a yes or no, which is what makes the nested cases work.
    /// </summary>
    public Polygon ClipToConvex(Polygon outer)
    {
        if (IsEmpty || outer.IsEmpty)
        {
            return new Polygon(Array.Empty<Point2D>());
        }

        List<Point2D> output = Normalised(Points);
        IReadOnlyList<Point2D> clip = Normalised(outer.Points);

        for (int i = 0; i < clip.Count && output.Count > 0; i++)
        {
            Point2D a = clip[i];
            Point2D b = clip[(i + 1) % clip.Count];
            List<Point2D> input = output;
            output = new List<Point2D>();

            Point2D previous = input[^1];
            bool previousInside = Side(a, b, previous) >= 0;

            foreach (Point2D current in input)
            {
                bool currentInside = Side(a, b, current) >= 0;

                if (currentInside)
                {
                    if (!previousInside)
                    {
                        output.Add(Intersect(previous, current, a, b));
                    }

                    output.Add(current);
                }
                else if (previousInside)
                {
                    output.Add(Intersect(previous, current, a, b));
                }

                previous = current;
                previousInside = currentInside;
            }
        }

        return new Polygon(output);
    }

    /// <summary>
    /// Whether this polygon lies entirely inside <paramref name="outer"/>.
    ///
    /// Every corner inside and no edge crossing means inside - enough for a convex outer, and
    /// a marquee that is a rectangle is convex. A concave marquee is a lasso, and there the
    /// corners alone are not enough, which is why the edges are tested too.
    /// </summary>
    public bool IsInside(Polygon outer)
    {
        if (IsEmpty || outer.IsEmpty)
        {
            return false;
        }

        foreach (Point2D point in _rings.SelectMany(r => r))
        {
            if (!outer.Contains(point))
            {
                return false;
            }
        }

        foreach (Point2D point in outer.Points)
        {
            if (Contains(point))
            {
                // The outer shape pokes into this one, so this one is not inside it.
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether the two overlap at all.</summary>
    public bool Intersects(Polygon other)
    {
        if (IsEmpty || other.IsEmpty)
        {
            return false;
        }

        if (!Bounds.Intersects(other.Bounds))
        {
            return false;
        }

        foreach (Point2D point in _rings.SelectMany(r => r))
        {
            if (other.Contains(point))
            {
                return true;
            }
        }

        foreach (Point2D point in other.Points)
        {
            if (Contains(point))
            {
                return true;
            }
        }

        // Neither inside the other: they may still cross, which the bounds check plus the two
        // corner tests above covers for the rectangular case that selection actually asks
        // about. Reported honestly rather than guessed at: a crossing pair of thin slivers is
        // the one shape this misses.
        return false;
    }

    /// <summary>Twice the signed area; positive when the winding is clockwise in model space.</summary>
    private static double SignedArea(IReadOnlyList<Point2D> points)
    {
        double sum = 0;

        for (int i = 0; i < points.Count; i++)
        {
            Point2D a = points[i];
            Point2D b = points[(i + 1) % points.Count];
            sum += (b.X - a.X) * (b.Y + a.Y);
        }

        return sum;
    }

    /// <summary>
    /// A polygon wound the same way as every other one this clips against.
    ///
    /// Sutherland-Hodgman asks "which side of this edge is inside" for each of the clipper's
    /// edges, and the answer is only right when both run the same way round. A subject wound
    /// the other way is not clipped differently - it is clipped to nothing, because every edge
    /// reads as outside. That is what happened the first time: a rectangle and its own
    /// reversed copy gave an empty result.
    ///
    /// The reference winding is the one in which <see cref="Side"/> is positive inside, which
    /// for these edges is a negative signed area. The first attempt normalised to positive and
    /// inverted every test instead of fixing anything.
    /// </summary>
    private static List<Point2D> Normalised(IReadOnlyList<Point2D> points)
        => SignedArea(points) <= 0 ? points.ToList() : points.Reverse().ToList();

    /// <summary>Which side of a directed line a point falls on.</summary>
    private static double Side(Point2D a, Point2D b, Point2D p)
        => ((b.X - a.X) * (p.Y - a.Y)) - ((b.Y - a.Y) * (p.X - a.X));

    /// <summary>Where two segments cross.</summary>
    private static Point2D Intersect(Point2D p1, Point2D p2, Point2D p3, Point2D p4)
    {
        double d = ((p1.X - p2.X) * (p3.Y - p4.Y)) - ((p1.Y - p2.Y) * (p3.X - p4.X));
        if (Math.Abs(d) < 1e-12)
        {
            return p2;
        }

        double a = (p1.X * p2.Y) - (p1.Y * p2.X);
        double b = (p3.X * p4.Y) - (p3.Y * p4.X);

        return new Point2D(
            ((a * (p3.X - p4.X)) - ((p1.X - p2.X) * b)) / d,
            ((a * (p3.Y - p4.Y)) - ((p1.Y - p2.Y) * b)) / d);
    }
}
