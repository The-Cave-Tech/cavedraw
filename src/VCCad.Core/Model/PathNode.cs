using VCCad.Geometry;

namespace VCCad.Core.Model;

/// <summary>
/// A node (Illustrator: "anchor point") on a path.
///
/// VCCad stores handles as <em>absolute points</em> (ADR-01). A node therefore
/// carries:
/// <list type="bullet">
/// <item><see cref="Anchor"/> — the point that lies exactly on the path.</item>
/// <item><see cref="InHandle"/> — the Bézier control point on the incoming segment
/// (the segment whose far end is this node).</item>
/// <item><see cref="OutHandle"/> — the control point on the outgoing segment.</item>
/// </list>
///
/// Segment semantics are defined in <see cref="SubPath"/>; in short, for the
/// segment running from node A to node B the cubic is
/// (A.Anchor, A.OutHandle, B.InHandle, B.Anchor). A straight segment is simply the
/// degenerate cubic in which both handles collapse onto their respective anchors,
/// so the editor never special-cases lines in its geometry — they are cubics with
/// coincident control points. This is the same trick PDF's path model encourages.
///
/// Node type (smooth/corner) is <em>derived</em>: a node whose In and Out handles
/// are mirror images across the anchor is "smooth". Editing code toggles between
/// the two by aligning/breaking the handles, never by flipping a boolean that
/// could disagree with the stored geometry.
/// </summary>
public sealed class PathNode
{
    /// <summary>The on-path anchor point, in artboard local coordinates.</summary>
    public Point2D Anchor { get; set; }

    /// <summary>Absolute control point governing the curve arriving at this node.</summary>
    public Point2D InHandle { get; set; }

    /// <summary>Absolute control point governing the curve leaving this node.</summary>
    public Point2D OutHandle { get; set; }

    /// <summary>Creates a corner-style node: both handles collapse onto the anchor,
    /// so the adjacent segments begin straight. Handles become meaningful the
    /// moment the pen tool or a conversion command pulls them out.</summary>
    public PathNode(Point2D anchor)
    {
        Anchor = anchor;
        InHandle = anchor;
        OutHandle = anchor;
    }

    /// <summary>Creates a node with explicit incoming and outgoing handles.</summary>
    public PathNode(Point2D anchor, Point2D inHandle, Point2D outHandle)
    {
        Anchor = anchor;
        InHandle = inHandle;
        OutHandle = outHandle;
    }

    /// <summary>Deep copy for clone/undo support (mutable reference semantics).</summary>
    public PathNode Clone() => new(Anchor, InHandle, OutHandle);

    /// <summary>True when this node produces a straight incoming segment.</summary>
    public bool HasStraightIncoming => InHandle.NearlyEquals(Anchor);

    /// <summary>True when this node produces a straight outgoing segment.</summary>
    public bool HasStraightOutgoing => OutHandle.NearlyEquals(Anchor);

    /// <summary>
    /// True when both handles lie on the same line through the anchor, on opposite
    /// sides — the geometric definition of a smooth (tangent-continuous) node.
    /// </summary>
    public bool IsSmooth()
    {
        if (HasStraightIncoming && HasStraightOutgoing)
        {
            return true; // Corner-ish, but continuity is trivially satisfied.
        }

        // Compare the unit vectors anchor→in and anchor→out: smooth ⟺ they are
        // anti-parallel (angle π).
        Vector2D vIn = (InHandle - Anchor).Normalized;
        Vector2D vOut = (OutHandle - Anchor).Normalized;
        double dot = vIn.Dot(vOut);
        return dot <= -1.0 + MathUtils.Epsilon;
    }
}
