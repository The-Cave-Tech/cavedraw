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
    /// The full pick test used by the selection tool: returns <see cref="PickKind.Fill"/>
    /// for points inside the fill region, else <see cref="PickKind.Outline"/> when the
    /// point is within <paramref name="outlineTolerance"/> of the stroked geometry,
    /// else <see cref="PickKind.None"/>.
    /// </summary>
    public static PickKind HitTest(PathItem path, Point2D query, double outlineTolerance)
    {
        bool fillVisible = path.Fill.IsVisible && path.SubPaths.Any(sp => sp.IsClosed);
        if (fillVisible && FillContains(path, query))
        {
            return PickKind.Fill;
        }

        bool strokeVisible = path.Stroke.HasVisibleOutline;
        if (strokeVisible && OutlineDistance(path, query) <= outlineTolerance)
        {
            return PickKind.Outline;
        }

        // Illustrator also lets you grab a path with no paint by clicking its
        // outline — useful for freshly created pen paths that have no style yet.
        if (!fillVisible && OutlineDistance(path, query) <= outlineTolerance)
        {
            return PickKind.Outline;
        }

        return PickKind.None;
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

    /// <summary>Flattens a closed subpath into its boundary polygon (used by the
    /// winding test). A small tolerance keeps the fill test fast and accurate.</summary>
    private static IReadOnlyList<Point2D> FlattenClosedContour(SubPath sub)
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
