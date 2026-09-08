using VCCad.Geometry;

namespace VCCad.Core.Model;

/// <summary>
/// A connected run of path geometry. A subpath is a single pen-stroke: one open
/// chain of nodes, or a closed loop. The <em>path object</em> (see
/// <see cref="PathItem"/>) holds one or more subpaths so a single fill can describe
/// compound shapes such as a ring (an outer closed loop plus an inner hole).
///
/// Segments are never stored explicitly. Between consecutive nodes
/// <c>nodes[i]</c> and <c>nodes[i+1]</c> lies exactly one cubic Bézier:
///
/// <code>
/// P0 = nodes[i].Anchor
/// P1 = nodes[i].OutHandle
/// P2 = nodes[i+1].InHandle
/// P3 = nodes[i+1].Anchor
/// </code>
///
/// Straight segments are cubics with P1 ≡ P0 and P2 ≡ P3 (see
/// <see cref="PathNode"/>). Because handles are absolute, this representation is
/// invariant under document translation — a property the lossless serializer and
/// the undo commands rely on.
/// </summary>
public sealed class SubPath
{
    /// <summary>The ordered nodes of this subpath, from start to end.</summary>
    public List<PathNode> Nodes { get; } = new();

    /// <summary>
    /// Whether the last node connects back to the first with a closing segment.
    /// A closed subpath is eligible for filling and exposes no stroke caps.
    /// </summary>
    public bool IsClosed { get; set; }

    /// <summary>The number of segments: <c>N</c> for an open subpath of N nodes,
    /// <c>N</c> for a closed subpath (the extra closing segment is implied by the
    /// last → first link), and 0 for an empty subpath.</summary>
    public int SegmentCount
    {
        get
        {
            int n = Nodes.Count;
            return n == 0 ? 0 : IsClosed ? n : n - 1;
        }
    }

    /// <summary>Whether the subpath currently holds no geometry.</summary>
    public bool IsEmpty => Nodes.Count == 0;

    /// <summary>Appends a fresh corner node at <paramref name="point"/> (pen-tool default).</summary>
    public PathNode AppendNode(Point2D point)
    {
        var node = new PathNode(point);
        Nodes.Add(node);
        return node;
    }

    /// <summary>
    /// Returns the i-th segment as its canonical cubic. Segments are indexed from
    /// the subpath start; for closed subpaths the final segment runs from the last
    /// node back to the first. Out-of-range indices throw.
    /// </summary>
    public CubicBezier GetSegment(int index)
    {
        int n = Nodes.Count;
        int segmentCount = SegmentCount;
        if (index < 0 || index >= segmentCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index), "Segment index out of range.");
        }

        PathNode start = Nodes[index];
        PathNode end = index + 1 < n ? Nodes[index + 1] : Nodes[0]; // wrap for closed loops
        return new CubicBezier(start.Anchor, start.OutHandle, end.InHandle, end.Anchor);
    }

    /// <summary>Enumerates every segment in order as cubics.</summary>
    public IEnumerable<CubicBezier> Segments()
    {
        for (int i = 0; i < SegmentCount; i++)
        {
            yield return GetSegment(i);
        }
    }

    /// <summary>
    /// The tight bounding box of this subpath: the union of every segment's own
    /// tight box. Empty subpaths yield <see cref="Rect2D.Empty"/>.
    /// </summary>
    public Rect2D BoundingBox()
    {
        Rect2D box = Rect2D.Empty;
        foreach (CubicBezier segment in Segments())
        {
            box = box.IsEmpty ? segment.BoundingBox() : box.Union(segment.BoundingBox());
        }

        return box;
    }

    /// <summary>Deep copy for clone/undo support.</summary>
    public SubPath Clone()
    {
        var copy = new SubPath { IsClosed = IsClosed };
        copy.Nodes.AddRange(Nodes.Select(n => n.Clone()));
        return copy;
    }

    /// <summary>
    /// The total parametric length of the subpath, estimated by flattening each
    /// segment. Useful for unit tests and dash/measurement code.
    /// </summary>
    public double EstimateLength(double tolerance = 1e-2)
    {
        double total = 0.0;
        foreach (CubicBezier segment in Segments())
        {
            total += segment.EstimateLength(tolerance);
        }

        return total;
    }
}
