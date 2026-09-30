using VCCad.Geometry;

namespace VCCad.Core.Model;

/// <summary>What rounding a corner did, and why not when it could not.</summary>
/// <param name="Rounded">Whether the corner became an arc.</param>
/// <param name="Radius">The radius actually used, which is the requested one unless it had to be clamped.</param>
/// <param name="Requested">The radius that was asked for.</param>
/// <param name="Clamped">Whether the radius was reduced to the largest arc that fits.</param>
/// <param name="Reason">Why nothing happened, when nothing did.</param>
public sealed record CornerRoundResult(
    bool Rounded,
    double Radius = 0,
    double Requested = 0,
    bool Clamped = false,
    string? Reason = null);

/// <summary>
/// Rounds the corner of a path: the point where two segments meet at an angle becomes an arc, tangent to
/// both, of a given radius.
///
/// **The radius and the drag distance are the same thing only at a right angle.** An arc of radius `r`
/// tangent to both segments meets them at `r / tan(theta/2)` from the corner, where `theta` is the angle
/// between them - so a sharp corner needs more room along its edges than the radius suggests and a
/// shallow one needs less. The radius is what is set, and the tangent points follow.
///
/// Where those tangent points would run past the end of a neighbouring segment the radius is **clamped to
/// the largest arc that fits**, and the result says so, rather than quietly producing something else.
///
/// The corner itself does not move: it is the origin the radius is measured from.
/// </summary>
public static class CornerRounder
{
    /// <summary>How much of a neighbouring segment an arc may consume, leaving the rest as an edge.</summary>
    private const double SegmentShare = 0.999;

    /// <summary>The largest radius whose arc fits within the two segments meeting at a corner.</summary>
    public static double MaxRadius(SubPath sub, int nodeIndex)
    {
        if (!TryCorner(sub, nodeIndex, out Point2D corner, out Point2D before, out Point2D after, out double theta))
        {
            return 0;
        }

        double reach = Math.Min(Distance(corner, before), Distance(corner, after));
        return reach * SegmentShare * Math.Tan(theta / 2);
    }

    /// <summary>
    /// Rounds one corner of a path. Returns what it did; the path is unchanged when it reports a reason.
    /// </summary>
    public static CornerRoundResult Round(PathItem path, int subPathIndex, int nodeIndex, double radius)
    {
        if (subPathIndex < 0 || subPathIndex >= path.SubPaths.Count)
        {
            return new CornerRoundResult(false, Requested: radius, Reason: "There is no such subpath.");
        }

        SubPath sub = path.SubPaths[subPathIndex];
        if (sub.Nodes.Count < 3)
        {
            return new CornerRoundResult(false, Requested: radius, Reason: "A path needs three points before it has a corner.");
        }

        if (!TryCorner(sub, nodeIndex, out Point2D corner, out Point2D before, out Point2D after, out double theta))
        {
            return new CornerRoundResult(false, Requested: radius, Reason: DescribeCorner(sub, nodeIndex, out string why) ? why : "That is not a corner.");
        }

        double reach = Math.Min(Distance(corner, before), Distance(corner, after));
        double largest = reach * SegmentShare * Math.Tan(theta / 2);
        double used = Math.Min(radius, largest);

        if (used <= 1e-6)
        {
            return new CornerRoundResult(false, Requested: radius, Reason: "The radius is too small to change anything.");
        }

        // The tangent points, at r / tan(theta/2) along each segment from the corner.
        double offset = used / Math.Tan(theta / 2);
        var toBefore = new Vector2D((before.X - corner.X) / Distance(corner, before), (before.Y - corner.Y) / Distance(corner, before));
        var toAfter = new Vector2D((after.X - corner.X) / Distance(corner, after), (after.Y - corner.Y) / Distance(corner, after));

        var first = new Point2D(corner.X + (toBefore.X * offset), corner.Y + (toBefore.Y * offset));
        var second = new Point2D(corner.X + (toAfter.X * offset), corner.Y + (toAfter.Y * offset));

        // The arc's centre: along the bisector, at r / sin(theta/2) from the corner.
        double bx = toBefore.X + toAfter.X;
        double by = toBefore.Y + toAfter.Y;
        double bisector = Math.Sqrt((bx * bx) + (by * by));
        if (bisector < 1e-12)
        {
            return new CornerRoundResult(false, Requested: radius, Reason: "The two segments double back on each other.");
        }

        double toCentre = used / Math.Sin(theta / 2);
        var centre = new Point2D(
            corner.X + (bx / bisector * toCentre), corner.Y + (by / bisector * toCentre));

        double startAngle = Math.Atan2(first.Y - centre.Y, first.X - centre.X);
        double endAngle = Math.Atan2(second.Y - centre.Y, second.X - centre.X);

        // The arc is the turn, which is the short way between the tangent points: the far way round would
        // swallow the rest of the path.
        double sweep = endAngle - startAngle;
        while (sweep > Math.PI)
        {
            sweep -= 2 * Math.PI;
        }

        while (sweep < -Math.PI)
        {
            sweep += 2 * Math.PI;
        }

        // Replace the corner with the two tangent points and the arc between them. Everything else the
        // path had - the neighbouring segments, their handles - is left where it was.
        var arc = new List<PathNode>();
        ArcBuilder.Append(arc, centre, used, startAngle, sweep);

        // The corner is replaced in place: the arc's first node is the tangent point on the incoming side
        // and its last is on the outgoing side, so they take exactly the corner's position in the sequence.
        sub.Nodes.RemoveAt(nodeIndex);
        sub.Nodes.InsertRange(nodeIndex, arc);

        path.GeometryChanged();
        return new CornerRoundResult(true, used, radius, used < radius - 1e-9);
    }

    /// <summary>
    /// The corner's geometry: the point, its two neighbours, and the angle between the segments.
    /// </summary>
    private static bool TryCorner(
        SubPath sub, int nodeIndex, out Point2D corner, out Point2D before, out Point2D after, out double theta)
    {
        corner = default;
        before = default;
        after = default;
        theta = 0;

        int count = sub.Nodes.Count;
        if (nodeIndex < 0 || nodeIndex >= count)
        {
            return false;
        }

        // The ends of an open path are not corners: there are not two segments meeting there.
        if (!sub.IsClosed && (nodeIndex == 0 || nodeIndex == count - 1))
        {
            return false;
        }

        corner = sub.Nodes[nodeIndex].Anchor;
        before = sub.Nodes[(nodeIndex - 1 + count) % count].Anchor;
        after = sub.Nodes[(nodeIndex + 1) % count].Anchor;

        double lengthBefore = Distance(corner, before);
        double lengthAfter = Distance(corner, after);
        if (lengthBefore < 1e-9 || lengthAfter < 1e-9)
        {
            return false;
        }

        var toBefore = new Vector2D((before.X - corner.X) / lengthBefore, (before.Y - corner.Y) / lengthBefore);
        var toAfter = new Vector2D((after.X - corner.X) / lengthAfter, (after.Y - corner.Y) / lengthAfter);

        double cos = Math.Clamp((toBefore.X * toAfter.X) + (toBefore.Y * toAfter.Y), -1, 1);
        theta = Math.Acos(cos);

        // A straight run is not a corner, and a doubling-back has no arc between the two sides.
        return theta > 1e-4 && theta < Math.PI - 1e-4;
    }

    private static bool DescribeCorner(SubPath sub, int nodeIndex, out string reason)
    {
        reason = "That is not a corner.";
        int count = sub.Nodes.Count;

        if (!sub.IsClosed && (nodeIndex == 0 || nodeIndex == count - 1))
        {
            reason = "The end of an open path is not a corner: there are not two segments meeting there.";
            return true;
        }

        if (!TryCorner(sub, nodeIndex, out _, out _, out _, out double theta))
        {
            // The angle is between the two directions out of the corner, so a straight run is 180 degrees
            // and a doubling back is zero - the opposite way round from what a glance suggests.
            reason = theta > Math.PI - 1e-4
                ? "The segments either side are straight through, so there is no corner to round."
                : "The segments double back on each other, so there is no arc between them.";
            return true;
        }

        return false;
    }

    private static double Distance(Point2D a, Point2D b)
        => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
}
