using VCCad.Geometry;

namespace VCCad.Core.Model;

/// <summary>
/// Joins two open single-subpath paths that share an endpoint into one path,
/// reversing either as needed. When the joined path's own ends also meet, it is
/// closed (and the duplicate endpoint merged) — so two arcs forming a loop
/// produce a closed shape.
/// </summary>
public static class PathJoin
{
    private const double Epsilon = 1e-6;

    /// <summary>True when the two paths share a coincident endpoint and can join.</summary>
    public static bool CanJoin(PathItem a, PathItem b)
    {
        if (a.SubPaths.Count != 1 || b.SubPaths.Count != 1)
        {
            return false;
        }

        SubPath sa = a.SubPaths[0];
        SubPath sb = b.SubPaths[0];
        if (sa.IsClosed || sb.IsClosed || sa.Nodes.Count < 2 || sb.Nodes.Count < 2)
        {
            return false;
        }

        return SharesEndpoint(sa, sb);
    }

    private static bool SharesEndpoint(SubPath a, SubPath b)
    {
        Point2D[] aEnds = { a.StartPoint!.Value, a.EndPoint!.Value };
        Point2D[] bEnds = { b.StartPoint!.Value, b.EndPoint!.Value };
        foreach (Point2D pa in aEnds)
        {
            foreach (Point2D pb in bEnds)
            {
                if (pa.NearlyEquals(pb, Epsilon))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Joins <paramref name="b"/> into <paramref name="a"/> (mutating a).
    /// Returns true on success.</summary>
    public static bool Join(PathItem a, PathItem b)
    {
        if (!CanJoin(a, b))
        {
            return false;
        }

        SubPath sa = a.SubPaths[0];
        SubPath sb = b.SubPaths[0].Clone();

        // Orient so that sb starts where sa ends.
        if (sa.EndPoint!.Value.NearlyEquals(sb.EndPoint!.Value, Epsilon))
        {
            sb.Reverse();
        }
        else if (sa.StartPoint!.Value.NearlyEquals(sb.StartPoint!.Value, Epsilon))
        {
            sa.Reverse();
        }
        else if (sa.StartPoint!.Value.NearlyEquals(sb.EndPoint!.Value, Epsilon))
        {
            sa.Reverse();
            sb.Reverse();
        }

        if (!sa.EndPoint!.Value.NearlyEquals(sb.StartPoint!.Value, Epsilon))
        {
            return false; // not actually joinable in this orientation
        }

        // Merge the shared node: keep sa's outgoing handle, take sb's incoming.
        PathNode tail = sa.Nodes[^1];
        PathNode head = sb.Nodes[0];
        tail.InHandle = head.InHandle;
        tail.OutHandle = tail.OutHandle; // unchanged (outgoing continues to next)

        for (int i = 1; i < sb.Nodes.Count; i++)
        {
            sa.Nodes.Add(sb.Nodes[i].Clone());
        }

        // If the combined path's own ends now meet, close it.
        sa.CloseAndMergeEndpoints();
        return true;
    }
}
