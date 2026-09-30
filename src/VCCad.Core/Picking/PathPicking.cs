using VCCad.Core.Model;
using VCCad.Geometry;

namespace VCCad.Core.Picking;

/// <summary>
/// Which part of a path a pick test landed on.
/// </summary>
public enum PickKind
{
    None,
    /// <summary>Inside a closed, fillable region.</summary>
    Fill,
    /// <summary>On the stroked outline.</summary>
    Outline,
}

/// <summary>A picked editable feature of a path (used by the node tool).</summary>
public readonly record struct NodePick(SubPath SubPath, int NodeIndex, bool IsInHandle, bool IsOutHandle);

/// <summary>A picked segment of a path plus its distance from the query point.</summary>
public readonly record struct SegmentPick(SubPath SubPath, int SegmentIndex, double Distance);

/// <summary>
/// Geometry tests that let the UI decide what a click grabbed. All distances are
/// expressed in model points and are independent of zoom; the view layer converts
/// its pixel tolerance into model units before calling in.
///
/// Points are assumed to be in the same space as the path's own coordinates. For
/// paths nested in transformed groups the view must map through the inverse chain
/// first (see project plan M6); this seed covers identity-transform hierarchies.
/// </summary>
public static class PathPicking
{
    /// <summary>
    /// Distance from <paramref name="query"/> to the nearest point on the path's
    /// outline (the strokable geometry). Open subpaths measure only their own
    /// segments — there is no implicit closing line. Closed subpaths include their
    /// closing segment because <see cref="SubPath.Segments"/> enumerates it.
    /// </summary>
    public static double OutlineDistance(PathItem path, Point2D query)
    {
        double best = double.PositiveInfinity;
        foreach (SubPath sub in path.SubPaths)
        {
            foreach (CubicBezier segment in sub.Segments())
            {
                segment.NearestPoint(query, out _, out double distance);
                best = Math.Min(best, distance);
            }
        }

        return best;
    }

    /// <summary>
    /// Returns true when <paramref name="query"/> lies inside any of the closed
    /// subpaths under the path's fill rule (NonZero winding or EvenOdd parity).
    /// Open subpaths never contribute to the fill region.
    /// </summary>
    public static bool FillContains(PathItem path, Point2D query)
    {
        bool nonzero = path.Fill.Rule == FillRule.NonZero;
        int parity = 0; // toggled per crossing when using even-odd
        int winding = 0; // signed crossings when using non-zero

        foreach (SubPath sub in path.SubPaths)
        {
            if (!sub.IsClosed || sub.Nodes.Count < 3)
            {
                continue;
            }

            // Winding contribution is computed per closed contour.
            (int crossCount, int signedCrossing) = WindingAt(sub, query);
            if (nonzero)
            {
                winding += signedCrossing;
            }
            else
            {
                parity ^= crossCount & 1;
            }
        }

        return nonzero ? winding != 0 : parity != 0;
    }

    /// <summary>
    /// The pick test used by the selection tool.
    ///
    /// A path is picked by its OUTLINE - its own geometry. Picking by the filled region instead
    /// made a large panel swallow every click over the lines and labels drawn on top of it: the
    /// panel contains the point, so it was "at distance zero" and beat whatever the pointer was
    /// actually on. A click aims at the artwork, and the artwork is the paths.
    ///
    /// <paramref name="pickInsideFill"/> puts the filled region back in front, for a caller that
    /// genuinely wants "select the shape containing this point" - a colour sampler, say.
    ///
    /// An unstroked fill is pickable by its outline too: the edge of a shape is visible whatever
    /// its stroke says, and it is now the only thing a click can aim at.
    /// </summary>
    public static PickKind HitTest(
        PathItem path, Point2D query, double outlineTolerance, bool pickInsideFill = false)
    {
        if (pickInsideFill)
        {
            bool fillVisible = path.Fill.IsVisible && path.SubPaths.Any(sp => sp.IsClosed);
            if (fillVisible && FillContains(path, query))
            {
                return PickKind.Fill;
            }
        }

        return OutlineDistance(path, query) <= outlineTolerance ? PickKind.Outline : PickKind.None;
    }

    /// <summary>
    /// The nearest segment of a path whose outline comes within
    /// <paramref name="tolerance"/> of <paramref name="query"/>. This lets the
    /// direct-selection tool pick an individual line/Bézier segment (rather than
    /// the whole object). Returns null when nothing is close enough.
    /// </summary>
    public static SegmentPick? ClosestSegment(PathItem path, Point2D query, double tolerance)
    {
        SegmentPick? best = null;
        for (int s = 0; s < path.SubPaths.Count; s++)
        {
            SubPath sub = path.SubPaths[s];
            for (int i = 0; i < sub.SegmentCount; i++)
            {
                CubicBezier segment = sub.GetSegment(i);
                segment.NearestPoint(query, out _, out double distance);
                if (distance <= tolerance && (best is null || distance < best.Value.Distance))
                {
                    best = new SegmentPick(sub, i, distance);
                }
            }
        }

        return best;
    }

    /// <summary>
    /// Returns the editable feature (anchor or handle) of a path nearest to
    /// <paramref name="query"/> within <paramref name="tolerance"/>.
    ///
    /// Anchors are preferred over handles <em>globally</em> (two passes): when a
    /// control handle sits on or very near an endpoint — which happens constantly
    /// while sculpting curves — clicking that spot must grab the anchor, not the
    /// coincident handle.
    /// </summary>
    public static NodePick? PickNode(PathItem path, Point2D query, double tolerance)
    {
        // Pass 1 — every anchor across the whole path.
        for (int s = 0; s < path.SubPaths.Count; s++)
        {
            SubPath sub = path.SubPaths[s];
            for (int n = 0; n < sub.Nodes.Count; n++)
            {
                if (sub.Nodes[n].Anchor.DistanceTo(query) <= tolerance)
                {
                    return new NodePick(sub, n, false, false);
                }
            }
        }

        // Pass 2 — handles (only the ones that are actually pulled out; collapsed
        // corner handles are not editable and are skipped).
        for (int s = 0; s < path.SubPaths.Count; s++)
        {
            SubPath sub = path.SubPaths[s];
            for (int n = 0; n < sub.Nodes.Count; n++)
            {
                PathNode node = sub.Nodes[n];
                if (!node.HasStraightIncoming && node.InHandle.DistanceTo(query) <= tolerance)
                {
                    return new NodePick(sub, n, true, false);
                }

                if (!node.HasStraightOutgoing && node.OutHandle.DistanceTo(query) <= tolerance)
                {
                    return new NodePick(sub, n, false, true);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Crosses a horizontal ray from <paramref name="query"/> toward +X against the
    /// closed contour and returns (a) whether it crosses an odd number of edges
    /// and (b) the signed winding-number contribution.
    ///
    /// The sign is derived from the edge's direction at the crossing: an edge
    /// going upward while crossing contributes +1 to the winding (standard
    /// scanline convention). Only the sign relative to zero matters for NonZero.
    /// </summary>
    private static (int Parity, int Winding) WindingAt(SubPath sub, Point2D query)
    {
        IReadOnlyList<Point2D> polyline = FlattenClosedContour(sub);

        int parity = 0;
        int winding = 0;
        for (int i = 0; i < polyline.Count; i++)
        {
            Point2D a = polyline[i];
            Point2D b = polyline[(i + 1) % polyline.Count];

            // Standard "crossing" test: the edge must straddle the query's y.
            bool straddles = (a.Y > query.Y) != (b.Y > query.Y);
            if (!straddles)
            {
                continue;
            }

            double xAtY = a.X + (query.Y - a.Y) * (b.X - a.X) / (b.Y - a.Y);
            if (xAtY <= query.X)
            {
                continue;
            }

            parity ^= 1;
            winding += a.Y < b.Y ? 1 : -1;
        }

        return (parity, winding);
    }

    /// <summary>
    /// Whether a path's geometry actually meets a rectangle — what a rubber-band
    /// selection has to ask.
    ///
    /// Testing bounding rectangles instead is what makes marquee selection useless on a
    /// document like a tiled pattern: a path whose geometry is one thin diagonal line has
    /// a box covering a quarter of the page, so any rectangle overlapping that box
    /// "selects" it however far away the line actually is. This walks the flattened
    /// outline instead, so a rectangle selects what it encloses or crosses and nothing
    /// else.
    /// </summary>
    public static bool IntersectsRect(PathItem path, Rect2D rect)
    {
        if (rect.IsEmpty)
        {
            return false;
        }

        foreach (SubPath sub in path.SubPaths)
        {
            IReadOnlyList<Point2D> polyline = FlattenOpenContour(sub);
            if (polyline.Count == 0)
            {
                continue;
            }

            for (int i = 0; i < polyline.Count; i++)
            {
                if (rect.Contains(polyline[i]))
                {
                    return true;
                }
            }

            // The samples can straddle the rectangle without any one landing inside it,
            // so the edges have to be tested too.
            int last = sub.IsClosed ? polyline.Count : polyline.Count - 1;
            for (int i = 0; i < last; i++)
            {
                Point2D a = polyline[i];
                Point2D b = polyline[(i + 1) % polyline.Count];
                if (SegmentMeetsRect(a, b, rect))
                {
                    return true;
                }
            }
        }

        // A rectangle wholly inside a filled shape selects it, even though no outline
        // sample lands inside the rectangle.
        if (path.Fill.IsVisible && path.SubPaths.Any(sp => sp.IsClosed))
        {
            if (FillContains(path, new Point2D(
                    (rect.Left + rect.Right) / 2, (rect.Top + rect.Bottom) / 2)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool SegmentMeetsRect(Point2D a, Point2D b, Rect2D r)
    {
        // Reject on the axis-aligned box first; most segments fail this.
        if (Math.Max(a.X, b.X) < r.Left || Math.Min(a.X, b.X) > r.Right ||
            Math.Max(a.Y, b.Y) < r.Top || Math.Min(a.Y, b.Y) > r.Bottom)
        {
            return false;
        }

        var topLeft = new Point2D(r.Left, r.Top);
        var topRight = new Point2D(r.Right, r.Top);
        var bottomRight = new Point2D(r.Right, r.Bottom);
        var bottomLeft = new Point2D(r.Left, r.Bottom);

        if (SegmentsCross(a, b, topLeft, topRight)
            || SegmentsCross(a, b, topRight, bottomRight)
            || SegmentsCross(a, b, bottomRight, bottomLeft)
            || SegmentsCross(a, b, bottomLeft, topLeft))
        {
            return true;
        }

        // A straight run is flattened to just its endpoints, so a segment that passes
        // through the rectangle without either end inside it — or that only touches a
        // corner — has to be sampled. Step finer than the rectangle so the samples
        // cannot straddle it.
        double span = Math.Max(0.5, Math.Min(r.Width, r.Height) / 2);
        double length = Math.Sqrt(((b.X - a.X) * (b.X - a.X)) + ((b.Y - a.Y) * (b.Y - a.Y)));
        int steps = (int)Math.Min(4096, Math.Ceiling(length / span));
        for (int i = 1; i < steps; i++)
        {
            double t = (double)i / steps;
            if (r.Contains(new Point2D(a.X + ((b.X - a.X) * t), a.Y + ((b.Y - a.Y) * t))))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Two line segments crossing, touching, or overlapping collinearly.</summary>
    private static bool SegmentsCross(Point2D a, Point2D b, Point2D c, Point2D d)
    {
        double D(Point2D p, Point2D q, Point2D r) =>
            (q.X - p.X) * (r.Y - p.Y) - (q.Y - p.Y) * (r.X - p.X);

        double d1 = D(c, d, a);
        double d2 = D(c, d, b);
        double d3 = D(a, b, c);
        double d4 = D(a, b, d);

        if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) &&
            ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0)))
        {
            return true;
        }

        // Touching and collinear cases: a line entering exactly through a rectangle
        // corner is common in technical artwork, and the strict test above misses it.
        const double Epsilon = 1e-9;
        return (Math.Abs(d1) < Epsilon && OnSegment(c, d, a))
            || (Math.Abs(d2) < Epsilon && OnSegment(c, d, b))
            || (Math.Abs(d3) < Epsilon && OnSegment(a, b, c))
            || (Math.Abs(d4) < Epsilon && OnSegment(a, b, d));
    }

    /// <summary>Whether <paramref name="p"/> lies on the segment <paramref name="a"/>-<paramref name="b"/>.</summary>
    private static bool OnSegment(Point2D a, Point2D b, Point2D p)
        => p.X >= Math.Min(a.X, b.X) - 1e-9 && p.X <= Math.Max(a.X, b.X) + 1e-9
        && p.Y >= Math.Min(a.Y, b.Y) - 1e-9 && p.Y <= Math.Max(a.Y, b.Y) + 1e-9;

    /// <summary>Flattens a closed subpath into its boundary polygon (used by the
    /// winding test). A small tolerance keeps the fill test fast and accurate.</summary>
    private static IReadOnlyList<Point2D> FlattenClosedContour(SubPath sub)
        => FlattenOpenContour(sub);

    /// <summary>Flattens a subpath's segments into a polyline, closed or not.</summary>
    private static IReadOnlyList<Point2D> FlattenOpenContour(SubPath sub)
    {
        var points = new List<Point2D>();
        foreach (CubicBezier segment in sub.Segments())
        {
            // Reuse Flatten's interior samples but skip duplicated endpoints:
            // Segment i+1 starts exactly where segment i ends.
            IReadOnlyList<Point2D> piece = segment.Flatten(0.05);
            if (points.Count == 0)
            {
                points.Add(piece[0]);
            }

            for (int k = 1; k < piece.Count; k++)
            {
                points.Add(piece[k]);
            }
        }

        return points;
    }
}
