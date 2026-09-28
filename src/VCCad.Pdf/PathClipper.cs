using VCCad.Core.Model;
using VCCad.Geometry;

namespace VCCad.Pdf;

/// <summary>
/// Clips path geometry to a rectangle — the operation a PDF content stream's
/// <c>W</c>/<c>W*</c> operator asks for.
///
/// Why this matters: a pattern paginated across A4 sheets draws artwork that
/// straddles the sheet edges and relies on a clip to show only the part belonging
/// to that page. Without clipping, every page imports as a full-size copy of each
/// straddling piece, so the document looks like the pieces are scattered across the
/// sheet when in fact the geometry is at the right scale and position — it simply
/// was never cut at the page edge.
///
/// The clipping is deliberately conservative:
///
/// - It runs **only** when a subpath actually crosses the clip boundary, so the
///   overwhelming majority of imported geometry keeps its original curves, control
///   handles and byte-for-byte fidelity.
/// - Clipped subpaths are flattened to polylines. A cut curve cannot be represented
///   exactly by the Bézier nodes we store, and the visible result is what matters for
///   the clipped region; the un-clipped parts of the document are untouched.
///
/// A rectangle is convex, so Sutherland–Hodgman is sufficient for closed subpaths.
/// Open subpaths (strokes) are cut segment by segment, which can split one subpath
/// into several visible pieces.
/// </summary>
public static class PathClipper
{
    /// <summary>Curve flattening resolution: 1/8 pt is far below display resolution.</summary>
    private const double FlattenTolerance = 0.125;

    /// <summary>Hard cap so a pathological curve cannot explode the node count.</summary>
    private const int MaxSegmentsPerCurve = 64;

    /// <summary>True when any subpath crosses the clip rectangle.</summary>
    public static bool CrossesBoundary(IEnumerable<SubPath> subPaths, Rect2D clip)
        => subPaths.Any(sp => !clip.Contains(sp.BoundingBox()));

    /// <summary>
    /// Returns the geometry of <paramref name="subPaths"/> restricted to
    /// <paramref name="clip"/>. Subpaths already inside the clip are returned
    /// unchanged (same instances), preserving curve fidelity.
    /// </summary>
    public static List<SubPath> Clip(IReadOnlyList<SubPath> subPaths, Rect2D clip)
    {
        var result = new List<SubPath>(subPaths.Count);
        foreach (SubPath subPath in subPaths)
        {
            if (clip.Contains(subPath.BoundingBox()))
            {
                result.Add(subPath);
                continue;
            }

            if (!clip.Intersects(subPath.BoundingBox()))
            {
                continue; // entirely hidden by the clip
            }

            List<Point2D> polyline = Flatten(subPath);
            if (polyline.Count < 2)
            {
                continue;
            }

            if (subPath.IsClosed)
            {
                List<Point2D>? clipped = ClipClosed(polyline, clip);
                if (clipped is { Count: >= 3 })
                {
                    result.Add(ToSubPath(clipped, closed: true));
                }
            }
            else
            {
                foreach (List<Point2D> piece in ClipOpen(polyline, clip))
                {
                    if (piece.Count >= 2)
                    {
                        result.Add(ToSubPath(piece, closed: false));
                    }
                }
            }
        }

        return result;
    }

    /// <summary>Converts a subpath to a polyline, sampling curves adaptively.</summary>
    public static List<Point2D> Flatten(SubPath subPath)
    {
        var points = new List<Point2D>();
        IReadOnlyList<PathNode> nodes = subPath.Nodes;
        if (nodes.Count == 0)
        {
            return points;
        }

        points.Add(nodes[0].Anchor);
        int segments = subPath.IsClosed ? nodes.Count : nodes.Count - 1;
        for (int i = 0; i < segments; i++)
        {
            CubicBezier curve = subPath.GetSegment(i);
            int steps = Steps(curve);
            for (int s = 1; s <= steps; s++)
            {
                points.Add(curve.PointAt(s / (double)steps));
            }
        }

        return points;
    }

    /// <summary>
    /// Number of straight steps for a curve: enough that the chord deviates from the
    /// curve by less than <see cref="FlattenTolerance"/>.
    /// </summary>
    private static int Steps(CubicBezier curve)
    {
        double length = curve.EstimateLength(0.05);
        if (length <= FlattenTolerance)
        {
            return 1;
        }

        // Deviation of a chord from a circular arc ~ L^2 / (8R); using the classic
        // adaptive rule with a safety factor keeps this cheap and conservative.
        int steps = (int)Math.Ceiling(Math.Sqrt(length / FlattenTolerance));
        return Math.Clamp(steps, 2, MaxSegmentsPerCurve);
    }

    /// <summary>Sutherland–Hodgman polygon clip against a convex rectangle.</summary>
    private static List<Point2D>? ClipClosed(List<Point2D> polygon, Rect2D clip)
    {
        var output = new List<Point2D>(polygon);
        output = ClipEdge(output, p => p.X >= clip.Left, (a, b) => Vertical(a, b, clip.Left));
        output = ClipEdge(output, p => p.X <= clip.Right, (a, b) => Vertical(a, b, clip.Right));
        output = ClipEdge(output, p => p.Y >= clip.Top, (a, b) => Horizontal(a, b, clip.Top));
        output = ClipEdge(output, p => p.Y <= clip.Bottom, (a, b) => Horizontal(a, b, clip.Bottom));

        // Drop a degenerate result (a sliver along an edge).
        return output.Count >= 3 && Math.Abs(SignedArea(output)) > 1e-6 ? output : null;
    }

    private static List<Point2D> ClipEdge(
        List<Point2D> input, Func<Point2D, bool> inside, Func<Point2D, Point2D, Point2D> intersect)
    {
        var output = new List<Point2D>();
        for (int i = 0; i < input.Count; i++)
        {
            Point2D currentPoint = input[i];
            Point2D previousPoint = input[(i - 1 + input.Count) % input.Count];
            bool currentInside = inside(currentPoint);
            bool previousInside = inside(previousPoint);

            if (currentInside)
            {
                if (!previousInside)
                {
                    output.Add(intersect(previousPoint, currentPoint));
                }

                output.Add(currentPoint);
            }
            else if (previousInside)
            {
                output.Add(intersect(previousPoint, currentPoint));
            }
        }

        return output;
    }

    /// <summary>Clips an open polyline, splitting it into the pieces that survive.</summary>
    private static List<List<Point2D>> ClipOpen(List<Point2D> polyline, Rect2D clip)
    {
        var pieces = new List<List<Point2D>>();
        List<Point2D>? current = null;

        for (int i = 0; i < polyline.Count - 1; i++)
        {
            (Point2D a, Point2D b) = (polyline[i], polyline[i + 1]);

            if (!SegmentIntersectsRect(a, b, clip))
            {
                current = null;
                continue;
            }

            List<Point2D>? segment = ClipSegment(a, b, clip);
            if (segment is null)
            {
                current = null;
                continue;
            }

            if (current is null)
            {
                current = new List<Point2D>();
                pieces.Add(current);
            }

            Point2D start = segment[0];
            Point2D end = segment[1];

            // A new piece starts whenever the clipped run is not continuous with the
            // previous one.
            if (current.Count > 0 && !current[^1].NearlyEquals(start))
            {
                current = new List<Point2D> { start };
                pieces.Add(current);
            }
            else if (current.Count == 0)
            {
                current.Add(start);
            }

            current.Add(end);

            // Leaving the clip rectangle ends this piece.
            if (!clip.Contains(end))
            {
                current = null;
            }
        }

        return pieces;
    }

    private static bool SegmentIntersectsRect(Point2D a, Point2D b, Rect2D clip)
        => clip.Contains(a) || clip.Contains(b) || ClipSegment(a, b, clip) is not null;

    /// <summary>
    /// Liang–Barsky segment clip. Returns the visible part, or null when the segment
    /// misses the rectangle entirely.
    /// </summary>
    private static List<Point2D>? ClipSegment(Point2D a, Point2D b, Rect2D clip)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        double t0 = 0.0;
        double t1 = 1.0;

        double[] p = { -dx, dx, -dy, dy };
        double[] q = { a.X - clip.Left, clip.Right - a.X, a.Y - clip.Top, clip.Bottom - a.Y };

        for (int i = 0; i < 4; i++)
        {
            if (Math.Abs(p[i]) < 1e-12)
            {
                if (q[i] < 0)
                {
                    return null; // parallel and outside
                }

                continue;
            }

            double r = q[i] / p[i];
            if (p[i] < 0)
            {
                if (r > t1)
                {
                    return null;
                }

                t0 = Math.Max(t0, r);
            }
            else
            {
                if (r < t0)
                {
                    return null;
                }

                t1 = Math.Min(t1, r);
            }
        }

        if (t0 > t1)
        {
            return null;
        }

        Point2D start = new(a.X + (t0 * dx), a.Y + (t0 * dy));
        Point2D end = new(a.X + (t1 * dx), a.Y + (t1 * dy));
        return new List<Point2D> { start, end };
    }

    private static Point2D Vertical(Point2D a, Point2D b, double x)
    {
        double dx = b.X - a.X;
        double t = Math.Abs(dx) < 1e-12 ? 0.0 : (x - a.X) / dx;
        return new Point2D(x, a.Y + (t * (b.Y - a.Y)));
    }

    private static Point2D Horizontal(Point2D a, Point2D b, double y)
    {
        double dy = b.Y - a.Y;
        double t = Math.Abs(dy) < 1e-12 ? 0.0 : (y - a.Y) / dy;
        return new Point2D(a.X + (t * (b.X - a.X)), y);
    }

    private static double SignedArea(List<Point2D> points)
    {
        double area = 0;
        for (int i = 0; i < points.Count; i++)
        {
            Point2D a = points[i];
            Point2D b = points[(i + 1) % points.Count];
            area += (a.X * b.Y) - (b.X * a.Y);
        }

        return area / 2.0;
    }

    private static SubPath ToSubPath(List<Point2D> points, bool closed)
    {
        var subPath = new SubPath { IsClosed = closed };
        foreach (Point2D point in points)
        {
            subPath.AppendNode(point);
        }

        return subPath;
    }
}
