using VCCad.Geometry;

namespace VCCad.Core.Model;

/// <summary>
/// A closed outline as a polygon - the flattened form that booleans and hit-testing work on. Named for
/// what it is rather than for what it resembles, because VCCad.Geometry already has a Polygon.
///
/// Curves become polylines here, once, at a documented tolerance, rather than inside every operation
/// that needs an answer. That is the decision recorded in issue #54: exact boolean geometry on Béziers
/// is a research-grade problem, so shapes are flattened finely enough that nobody can see it and the
/// arithmetic afterwards is arithmetic on points.
/// </summary>
public sealed class FlattenedOutline
{
    public FlattenedOutline(IReadOnlyList<Point2D> points)
    {
        Points = points;
        SignedArea = ComputeSignedArea(points);
    }

    /// <summary>The outline, in order. The last point joins back to the first.</summary>
    public IReadOnlyList<Point2D> Points { get; }

    /// <summary>
    /// Twice the signed area, halved. Positive and negative are the two winding directions, which is
    /// how a hole is told from an island: a hole winds against the outline that contains it.
    /// </summary>
    public double SignedArea { get; }

    /// <summary>The area, ignoring direction.</summary>
    public double Area => Math.Abs(SignedArea);

    /// <summary>Whether the points run the positive way round.</summary>
    public bool IsPositive => SignedArea > 0;

    /// <summary>Whether the outline runs the other way from <paramref name="other"/>.</summary>
    public bool WindsAgainst(FlattenedOutline other) => IsPositive != other.IsPositive;

    /// <summary>
    /// Whether the point is inside, by the ray-crossing rule. Independent of winding direction, which
    /// is what the even-odd fill rule is.
    /// </summary>
    public bool Contains(Point2D point)
    {
        bool inside = false;
        int count = Points.Count;

        for (int i = 0, j = count - 1; i < count; j = i++)
        {
            Point2D a = Points[i];
            Point2D b = Points[j];

            // Does the horizontal ray from the point cross this edge?
            if ((a.Y > point.Y) != (b.Y > point.Y) &&
                point.X < ((b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y)) + a.X)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    /// <summary>
    /// The winding number of the outline about a point: how many times it goes round. Zero is outside;
    /// non-zero is inside under the nonzero rule, and the **sign** says whether this outline is an
    /// island or a hole around that point.
    /// </summary>
    public int Winding(Point2D point)
    {
        int winding = 0;
        int count = Points.Count;

        for (int i = 0; i < count; i++)
        {
            Point2D a = Points[i];
            Point2D b = Points[(i + 1) % count];

            if (a.Y <= point.Y)
            {
                if (b.Y > point.Y && Cross(a, b, point) > 0)
                {
                    winding++;
                }
            }
            else if (b.Y <= point.Y && Cross(a, b, point) < 0)
            {
                winding--;
            }
        }

        return winding;
    }

    private static double Cross(Point2D a, Point2D b, Point2D p)
        => ((b.X - a.X) * (p.Y - a.Y)) - ((p.X - a.X) * (b.Y - a.Y));

    private static double ComputeSignedArea(IReadOnlyList<Point2D> points)
    {
        double total = 0;
        for (int i = 0; i < points.Count; i++)
        {
            Point2D a = points[i];
            Point2D b = points[(i + 1) % points.Count];
            total += (a.X * b.Y) - (b.X * a.Y);
        }

        return total / 2.0;
    }
}

/// <summary>
/// Flattens a path's curves into polygons, and answers "is this point filled" for a set of outlines
/// under a fill rule.
///
/// The second half is what makes a compound path work: the same subpaths mean an annulus under one rule
/// and a solid disc under the other, and the difference is entirely in the winding.
/// </summary>
public static class PathFlattener
{
    private const double PointsPerMm = 72.0 / 25.4;

    /// <summary>
    /// How far a curve may deviate from the polyline standing in for it: **0.05 mm**.
    ///
    /// Chosen to be invisible at print resolution - well under the finest dot a press lays down - and
    /// recorded in issue #54 as the decision it is, because every boolean result inherits it.
    /// </summary>
    public const double ToleranceMm = 0.05;

    /// <summary>The flattening tolerance in model units.</summary>
    public static double Tolerance => ToleranceMm * PointsPerMm;

    /// <summary>Whether the point is filled by these outlines, under the rule.</summary>
    public static bool IsFilled(IReadOnlyList<FlattenedOutline> outlines, FillRule rule, Point2D point)
    {
        if (rule == FillRule.EvenOdd)
        {
            // Nesting depth decides: a point inside an odd number of outlines is filled, so the second
            // ring of a target is filled again.
            int crossings = 0;
            foreach (FlattenedOutline outline in outlines)
            {
                if (outline.Contains(point))
                {
                    crossings++;
                }
            }

            return crossings % 2 == 1;
        }

        // Nonzero: the windings add up, and a hole winds against its container so they cancel.
        int winding = 0;
        foreach (FlattenedOutline outline in outlines)
        {
            winding += outline.Winding(point);
        }

        return winding != 0;
    }

    /// <summary>The path's closed subpaths as polygons, flattened to <see cref="Tolerance"/>.</summary>
    public static IReadOnlyList<FlattenedOutline> Flatten(PathItem path, double? tolerance = null)
    {
        double limit = tolerance ?? Tolerance;
        var outlines = new List<FlattenedOutline>();

        foreach (SubPath sub in path.SubPaths)
        {
            if (sub.Nodes.Count < 2)
            {
                continue;
            }

            var points = new List<Point2D>();
            int segments = sub.SegmentCount;
            for (int i = 0; i < segments; i++)
            {
                CubicBezier curve = sub.GetSegment(i);
                FlattenCurve(curve, limit, points);
            }

            if (points.Count >= 3)
            {
                // A closed subpath's last segment ends where the first began, so the closing point is
                // the first one again. Dropping it here means no consumer has to remember to skip it.
                if (points.Count > 3 && points[0].NearlyEquals(points[^1], 1e-9))
                {
                    points.RemoveAt(points.Count - 1);
                }

                outlines.Add(new FlattenedOutline(points));
            }
        }

        return outlines;
    }

    /// <summary>
    /// Adds a curve's points to the outline, subdividing until each piece is flat enough. The
    /// subdivision is on the control points' distance from the chord, which is the standard measure of
    /// how far a cubic bows away from the straight line between its ends.
    /// </summary>
    private static void FlattenCurve(CubicBezier curve, double tolerance, List<Point2D> points)
    {
        // The start point, once; each subdivision appends its far end.
        if (points.Count == 0)
        {
            points.Add(curve.P0);
        }
        else if (!points[^1].NearlyEquals(curve.P0, 1e-9))
        {
            points.Add(curve.P0);
        }

        Subdivide(curve, tolerance, points, 0);
    }

    private static void Subdivide(CubicBezier curve, double tolerance, List<Point2D> points, int depth)
    {
        if (depth >= 16 || IsFlat(curve, tolerance))
        {
            points.Add(curve.P3);
            return;
        }

        (CubicBezier left, CubicBezier right) = Split(curve, 0.5);
        Subdivide(left, tolerance, points, depth + 1);
        Subdivide(right, tolerance, points, depth + 1);
    }

    /// <summary>Whether the curve is within the tolerance of the straight line between its ends.</summary>
    private static bool IsFlat(CubicBezier curve, double tolerance)
        => DistanceToLine(curve.P1, curve.P0, curve.P3) <= tolerance
           && DistanceToLine(curve.P2, curve.P0, curve.P3) <= tolerance;

    private static double DistanceToLine(Point2D point, Point2D a, Point2D b)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        double length = Math.Sqrt((dx * dx) + (dy * dy));

        if (length < 1e-12)
        {
            return Math.Sqrt(Math.Pow(point.X - a.X, 2) + Math.Pow(point.Y - a.Y, 2));
        }

        return Math.Abs(((point.X - a.X) * dy) - ((point.Y - a.Y) * dx)) / length;
    }

    /// <summary>De Casteljau's split at t - the exact halves of a cubic.</summary>
    private static (CubicBezier Left, CubicBezier Right) Split(CubicBezier curve, double t)
    {
        Point2D ab = Lerp(curve.P0, curve.P1, t);
        Point2D bc = Lerp(curve.P1, curve.P2, t);
        Point2D cd = Lerp(curve.P2, curve.P3, t);

        Point2D abc = Lerp(ab, bc, t);
        Point2D bcd = Lerp(bc, cd, t);
        Point2D middle = Lerp(abc, bcd, t);

        return (
            new CubicBezier(curve.P0, ab, abc, middle),
            new CubicBezier(middle, bcd, cd, curve.P3));
    }

    private static Point2D Lerp(Point2D a, Point2D b, double t)
        => new(a.X + ((b.X - a.X) * t), a.Y + ((b.Y - a.Y) * t));
}
