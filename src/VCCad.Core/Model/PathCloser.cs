using VCCad.Geometry;

namespace VCCad.Core.Model;

/// <summary>Which of the two things closing a path did.</summary>
public enum PathCloseMode
{
    /// <summary>The path was already closed; nothing changed.</summary>
    AlreadyClosed,

    /// <summary>The ends were within tolerance: one moved onto the other and they became one point.</summary>
    Snapped,

    /// <summary>The ends were apart: a segment was added to close the shape.</summary>
    SegmentAdded,

    /// <summary>Fewer than three points: there is no shape to close.</summary>
    NotEnoughNodes,
}

/// <summary>What closing a path did, so a person and a driver can tell the two cases apart.</summary>
/// <param name="Mode">Which case it was.</param>
/// <param name="MovedTo">Where the path now ends, when the ends were snapped together.</param>
/// <param name="MovedFrom">Where that point was before it moved, when the ends were snapped.</param>
/// <param name="Gap">How far apart the ends were, in millimetres.</param>
public sealed record PathCloseResult(
    PathCloseMode Mode,
    Point2D? MovedTo = null,
    Point2D? MovedFrom = null,
    double Gap = 0)
{
    /// <summary>Whether anything about the path changed.</summary>
    public bool Changed => Mode is PathCloseMode.Snapped or PathCloseMode.SegmentAdded;
}

/// <summary>
/// Closes an open path, in the two ways a path actually needs closing.
///
/// Drawing by hand leaves the ends a hair apart, and a shape whose ends are apart needs an edge rather
/// than a snap. Treating them the same either refuses to close the first (which is what the old
/// exact-match-only close did) or adds a stray edge across the second.
///
/// Measurements are in millimetres because that is the unit a person measures in; the model is in
/// points, and the constants below are millimetres converted once.
/// </summary>
public static class PathCloser
{
    private const double PointsPerMm = 72.0 / 25.4;

    /// <summary>
    /// How close the ends have to be to count as the same point: **0.05 mm**.
    ///
    /// Well under anything visible at working zoom and well over the arithmetic error of drawing by
    /// hand - and it has to exceed the 0.011 mm in the example that prompted this, or a point that is
    /// visibly "on 1mm" would be treated as somewhere else.
    /// </summary>
    public const double SnapToleranceMm = 0.05;

    /// <summary>A handle shorter than this is not worth continuing: **2 mm**.</summary>
    public const double MinHandleMm = 2.0;

    /// <summary>How far off straight a handle must be to be worth continuing: **5 degrees**.</summary>
    public const double MinHandleAngleDegrees = 5.0;

    /// <summary>The snap tolerance in model units.</summary>
    public static double SnapTolerance => SnapToleranceMm * PointsPerMm;

    /// <summary>The shortest handle worth continuing, in model units.</summary>
    public static double MinHandleLength => MinHandleMm * PointsPerMm;

    /// <summary>
    /// Closes the subpath. Returns what it did; the path is unchanged when it reports
    /// <see cref="PathCloseMode.AlreadyClosed"/> or <see cref="PathCloseMode.NotEnoughNodes"/>.
    /// </summary>
    public static PathCloseResult Close(PathItem path, int subPathIndex = 0)
    {
        if (subPathIndex < 0 || subPathIndex >= path.SubPaths.Count)
        {
            return new PathCloseResult(PathCloseMode.NotEnoughNodes);
        }

        SubPath sub = path.SubPaths[subPathIndex];
        if (sub.IsClosed)
        {
            return new PathCloseResult(PathCloseMode.AlreadyClosed);
        }

        // A closed shape has three points: two is a line, and closing it would be inventing a shape.
        if (sub.Nodes.Count < 3)
        {
            return new PathCloseResult(PathCloseMode.NotEnoughNodes);
        }

        Point2D first = sub.Nodes[0].Anchor;
        Point2D last = sub.Nodes[^1].Anchor;
        double gap = Math.Sqrt(Math.Pow(last.X - first.X, 2) + Math.Pow(last.Y - first.Y, 2));

        PathCloseResult result = gap <= SnapTolerance
            ? Snap(sub, gap)
            : AddSegment(sub, gap);

        path.GeometryChanged();
        return result;
    }

    /// <summary>
    /// The ends already meet: one moves onto the other and the two become one point.
    ///
    /// Which one moves: the point that is not on a whole millimetre moves to the one that is, so the
    /// path closes on a number somebody meant rather than on 1.011. If neither is cleaner, the last
    /// point moves to the first - the first is the one the person aimed at.
    /// </summary>
    private static PathCloseResult Snap(SubPath sub, double gap)
    {
        Point2D first = sub.Nodes[0].Anchor;
        Point2D last = sub.Nodes[^1].Anchor;

        // Which point is cleaner, not which is whole: (1, 5) and (1.011, 4.9997) are BOTH within the
        // tolerance of a whole millimetre, so "is it whole" would keep whichever came first. The
        // question is which one is closer to whole, and a tie keeps the first - the point the person
        // aimed at.
        bool keepFirst = WholeMillimetreError(first) <= WholeMillimetreError(last);
        Point2D kept = keepFirst ? first : last;
        Point2D moved = keepFirst ? last : first;

        var delta = new Vector2D(kept.X - moved.X, kept.Y - moved.Y);
        PathNode movingNode = keepFirst ? sub.Nodes[^1] : sub.Nodes[0];

        // The moved point's own handles move with it, by the same delta, so the curve arrives at the
        // join along the tangent it had rather than kinking.
        movingNode.Anchor = new Point2D(movingNode.Anchor.X + delta.X, movingNode.Anchor.Y + delta.Y);
        movingNode.InHandle = new Point2D(movingNode.InHandle.X + delta.X, movingNode.InHandle.Y + delta.Y);
        movingNode.OutHandle = new Point2D(movingNode.OutHandle.X + delta.X, movingNode.OutHandle.Y + delta.Y);

        // Now the two anchors coincide, so merge: the surviving point keeps the outgoing handle of the
        // first and the incoming handle of the last, which is what preserves the curve through the join.
        sub.Nodes[0].InHandle = sub.Nodes[^1].InHandle;
        sub.Nodes.RemoveAt(sub.Nodes.Count - 1);
        sub.IsClosed = true;

        return new PathCloseResult(PathCloseMode.Snapped, kept, moved, gap / (72.0 / 25.4));
    }

    /// <summary>
    /// The ends are apart: the shape closes by gaining an edge, with handles derived from the segments
    /// it joins so a curved shape closes curved rather than gaining a chord that flattens it.
    /// </summary>
    private static PathCloseResult AddSegment(SubPath sub, double gap)
    {
        Point2D first = sub.Nodes[0].Anchor;
        Point2D last = sub.Nodes[^1].Anchor;
        double newLength = gap;

        // At each end, the handle of the neighbouring segment, and how long that segment is.
        sub.Nodes[0].InHandle = ClosingHandle(
            first, sub.Nodes[0].OutHandle, sub.Nodes[1].Anchor, newLength);

        sub.Nodes[^1].OutHandle = ClosingHandle(
            last, sub.Nodes[^1].InHandle, sub.Nodes[^2].Anchor, newLength);

        sub.IsClosed = true;
        return new PathCloseResult(PathCloseMode.SegmentAdded, last, first, gap / (72.0 / 25.4));
    }

    /// <summary>
    /// The handle the new segment wants at one end.
    ///
    /// It continues the neighbouring handle - colinear with it, so the curve passes through the join
    /// without a crease - and its length is proportional to the two segments: a third of the length
    /// gets a third of the handle.
    ///
    /// The neighbour is only worth continuing when its handle is longer than 2 mm **and** points more
    /// than 5 degrees away from the straight line between the segment's ends. A short handle, or one
    /// that is nearly colinear already, means the shape is straight there and the new segment should be
    /// straight there too.
    /// </summary>
    private static Point2D ClosingHandle(Point2D node, Point2D neighbourHandle, Point2D neighbourAnchor, double newLength)
    {
        var fromNode = new Vector2D(neighbourHandle.X - node.X, neighbourHandle.Y - node.Y);
        double handleLength = Math.Sqrt((fromNode.X * fromNode.X) + (fromNode.Y * fromNode.Y));

        if (handleLength < MinHandleLength || handleLength < 1e-9)
        {
            return node;
        }

        // The straight-line equivalent: the direction from this node to the other end of its segment.
        var straight = new Vector2D(neighbourAnchor.X - node.X, neighbourAnchor.Y - node.Y);
        double straightLength = Math.Sqrt((straight.X * straight.X) + (straight.Y * straight.Y));
        if (straightLength < 1e-9)
        {
            return node;
        }

        double angle = Math.Acos(Math.Clamp(
            ((fromNode.X * straight.X) + (fromNode.Y * straight.Y)) / (handleLength * straightLength),
            -1, 1)) * 180.0 / Math.PI;

        if (angle <= MinHandleAngleDegrees)
        {
            return node;
        }

        // Colinear with the neighbour's handle, scaled by the two segments' lengths.
        double scaled = handleLength * (newLength / straightLength);
        return new Point2D(
            node.X + (fromNode.X / handleLength * scaled),
            node.Y + (fromNode.Y / handleLength * scaled));
    }

    /// <summary>
    /// How far a point is from whole millimetres, in millimetres - zero when it is exactly on them.
    ///
    /// The smaller error is the point worth keeping: a path closed on (1, 5) is one somebody can
    /// measure, and one closed on (1.011, 4.9997) is not - even though both are within the snap
    /// tolerance of a whole millimetre and so both would pass a yes-or-no test.
    /// </summary>
    private static double WholeMillimetreError(Point2D point)
    {
        double x = point.X / PointsPerMm;
        double y = point.Y / PointsPerMm;
        return Math.Max(Math.Abs(x - Math.Round(x)), Math.Abs(y - Math.Round(y)));
    }
}
