using VCCad.Core.Model;
using VCCad.Geometry;

namespace VCCad.Core.Svg;

/// <summary>Where a marker goes: the vertex and the heading a path gives the marker in one of its three slots.</summary>
public readonly record struct MarkerAnchor(Point2D Vertex, double HeadingRadians);

/// <summary>
/// The anchors a path's marker slot decorates (issue #202): SVG 1.1 §11.6.4's vertex selection and its tangent.
///
/// A marker is a property of the path, so *where* it goes is the path's answer, not the marker's: a `start` marker
/// sits at the first vertex, an `end` marker at the last, and a `mid` marker at every vertex between them.
///
/// **The heading is the bisector where the path turns.** A `mid` marker at a corner is turned along the sum of the
/// incoming and outgoing tangents - which is what makes an arrowhead at the apex of a chevron point the way the
/// path is going rather than along either leg. At a start vertex on an open path only the outgoing tangent exists,
/// and at an end vertex only the incoming one. That is the rule the reader materialises arrowheads with and the one
/// the renderers must place them by; a second rule here would put an arrowhead on the wrong point of a curve, which
/// looks like a drawing mistake rather than a bug.
/// </summary>
public static class MarkerAnchors
{
    /// <summary>
    /// The anchors a slot decorates in the path's **own** coordinates.
    ///
    /// A closed subpath's last vertex is its first, so an `end` marker on a closed path draws a second marker on the
    /// initial point - the specification says so, and a closed path's start vertex has both tangents, so its heading
    /// is a bisector like any other interior point.
    /// </summary>
    public static IReadOnlyList<MarkerAnchor> For(PathItem path, MarkerSlot slot)
    {
        var anchors = new List<MarkerAnchor>();

        foreach (SubPath sub in path.SubPaths)
        {
            if (sub.Nodes.Count == 0)
            {
                continue;
            }

            for (int i = 0; i < sub.Nodes.Count; i++)
            {
                bool first = i == 0;
                bool last = i == sub.Nodes.Count - 1;

                if (!Include(slot, first, last))
                {
                    continue;
                }

                if (Heading(sub, i, slot) is { } heading)
                {
                    anchors.Add(new MarkerAnchor(sub.Nodes[i].Anchor, heading));
                }
            }
        }

        return anchors;
    }

    private static bool Include(MarkerSlot slot, bool first, bool last) => slot switch
    {
        MarkerSlot.Start => first,
        MarkerSlot.End => last,

        // A `mid` marker decorates the vertices *between* the ends: on a two-node path there are none, which is why
        // a line with `marker-mid` draws nothing at all.
        _ => !first && !last,
    };

    /// <summary>
    /// The direction a marker at this vertex is turned along, or null when the vertex has no tangent at all.
    ///
    /// At a start vertex of an open path only the outgoing tangent exists and at an end vertex only the incoming
    /// one; everywhere else the two are summed and normalised, which is the bisector where the path turns.
    /// </summary>
    private static double? Heading(SubPath sub, int vertex, MarkerSlot slot)
    {
        (double X, double Y)? incoming = slot == MarkerSlot.Start && !sub.IsClosed
            ? null
            : Tangent(sub, vertex, outbound: false);

        (double X, double Y)? outgoing = slot == MarkerSlot.End && !sub.IsClosed
            ? null
            : Tangent(sub, vertex, outbound: true);

        if (incoming is { } a && outgoing is { } b)
        {
            (double ax, double ay) = Normalize(a);
            (double bx, double by) = Normalize(b);
            (double dx, double dy) = Normalize((ax + bx, ay + by));
            return dx == 0.0 && dy == 0.0 ? 0.0 : Math.Atan2(dy, dx);
        }

        if ((incoming ?? outgoing) is not { } only)
        {
            return null;
        }

        (double x, double y) = Normalize(only);
        return x == 0.0 && y == 0.0 ? null : Math.Atan2(y, x);
    }

    /// <summary>
    /// The tangent at a vertex, outbound or inbound.
    ///
    /// The control handle is what decides it for a curve - a straight segment's handles sit on its own anchors, so
    /// the fallback to the neighbouring anchor is what a line uses and is the same answer for a curve written
    /// without handles.
    /// </summary>
    private static (double X, double Y)? Tangent(SubPath sub, int vertex, bool outbound)
    {
        PathNode node = sub.Nodes[vertex];

        int other = outbound
            ? (vertex + 1 < sub.Nodes.Count ? vertex + 1 : sub.IsClosed ? 0 : -1)
            : (vertex - 1 >= 0 ? vertex - 1 : sub.IsClosed ? sub.Nodes.Count - 1 : -1);

        if (other < 0)
        {
            return null;
        }

        Point2D handle = outbound ? node.OutHandle : node.InHandle;
        double dx = outbound ? handle.X - node.Anchor.X : node.Anchor.X - handle.X;
        double dy = outbound ? handle.Y - node.Anchor.Y : node.Anchor.Y - handle.Y;

        if (dx == 0.0 && dy == 0.0)
        {
            Point2D to = sub.Nodes[other].Anchor;
            dx = outbound ? to.X - node.Anchor.X : node.Anchor.X - to.X;
            dy = outbound ? to.Y - node.Anchor.Y : node.Anchor.Y - to.Y;
        }

        return (dx, dy);
    }

    /// <summary>A vector scaled to unit length, or the zero vector left alone.</summary>
    private static (double X, double Y) Normalize((double X, double Y) vector)
    {
        double length = Math.Sqrt((vector.X * vector.X) + (vector.Y * vector.Y));
        return length == 0.0 ? (0.0, 0.0) : (vector.X / length, vector.Y / length);
    }
}

/// <summary>One of SVG's three marker properties.</summary>
public enum MarkerSlot
{
    Start,
    Mid,
    End,
}
