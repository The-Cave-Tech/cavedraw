using VCCad.Geometry;

namespace VCCad.Core.Model;

/// <summary>
/// Builds the outline of a path stroked with a width profile.
///
/// A width profile is not a stroke with a different width: the stroke's edges are **offset curves**, each
/// following its own half-width along the path. So the work is to parameterise the path, read the profile at
/// each position, and push the centreline out on both sides by the amounts it gives - which is what this does.
///
/// **How the outline is closed.** The loop runs along the left edge and back along the right one, which gives
/// straight ends (butt caps). At a corner the two edges are mitred - see <see cref="MiterPoint"/> - so the
/// offset distance is the same at a corner as along a segment, which is what "a stroke this wide" means. Where
/// the path doubles back tight enough that the miter would spike, the corner is bevelled instead.
///
/// Where the offset edges cross themselves on a tight curve the overlap is left in place: the loop is filled
/// with the **nonzero** rule, where an overlap of the same winding simply fills. Removing the self-intersection
/// instead would mean polygon boolean work whose only visible effect is a shape nobody asked for.
/// </summary>
public static class PathOffset
{
    /// <summary>
    /// The filled outline of a stroked path, one closed loop per flattened outline.
    ///
    /// <paramref name="fallbackWidth"/> is used where the profile has nothing to say - an empty profile - so an
    /// empty profile leaves the stroke as it was rather than erasing it.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<Point2D>> Outline(
        IReadOnlyList<FlattenedOutline> outlines,
        WidthProfileSpec? profile,
        double fallbackWidth,
        double miterLimit = 4.0)
    {
        var result = new List<IReadOnlyList<Point2D>>();

        foreach (FlattenedOutline outline in outlines)
        {
            IReadOnlyList<Point2D> points = outline.Points;
            if (points.Count < 2)
            {
                continue;
            }

            (double[] positions, Vector2D[] incoming, Vector2D[] outgoing) =
                Parameterise(points, outline.IsClosed);

            var loop = new List<Point2D>(points.Count * 2);
            for (int i = 0; i < points.Count; i++)
            {
                (double left, double _) = WidthsAt(profile, fallbackWidth, positions[i]);
                loop.Add(MiterPoint(points, i, incoming, outgoing, left, left: true, miterLimit));
            }

            for (int i = points.Count - 1; i >= 0; i--)
            {
                (double _, double right) = WidthsAt(profile, fallbackWidth, positions[i]);
                loop.Add(MiterPoint(points, i, incoming, outgoing, right, left: false, miterLimit));
            }

            result.Add(loop);
        }

        return result;
    }

    /// <summary>
    /// The half-widths at a position along the path, which is the profile's answer when it has one and the
    /// fallback width - halved - when it does not.
    /// </summary>
    public static (double Left, double Right) WidthsAt(
        WidthProfileSpec? profile, double fallbackWidth, double position)
        => profile?.HalvesAt(position) ?? (fallbackWidth / 2.0, fallbackWidth / 2.0);

    /// <summary>
    /// Where a vertex goes when its two edges are offset by <paramref name="halfWidth"/>: the point where the
    /// two offset lines meet.
    ///
    /// **Not the vertex moved along the averaged normal**, which is the tempting shortcut and is wrong wherever
    /// the path turns. At a right angle the averaged normal is a unit vector, so the offset lands four units out
    /// on the diagonal when the edges need to be four units from each segment - the corner comes out about 30%
    /// short, on every corner, in the same direction. The intersection is what "four units from both segments"
    /// actually means: at a right angle it is 4*sqrt(2) from the vertex, which is the familiar miter.
    ///
    /// Past the miter limit the two lines are nearly parallel or doubling back, and the intersection runs off to
    /// infinity; there the vertex is bevelled - moved by the offset along the bisector - which is finite, and is
    /// what every renderer does with a spike.
    /// </summary>
    private static Point2D MiterPoint(
        IReadOnlyList<Point2D> points,
        int index,
        Vector2D[] incoming,
        Vector2D[] outgoing,
        double halfWidth,
        bool left,
        double miterLimit)
    {
        Vector2D n1 = OffsetNormal(incoming[index], left);
        Vector2D n2 = OffsetNormal(outgoing[index], left);

        double dot = (n1.X * n2.X) + (n1.Y * n2.Y);
        double denominator = 1.0 + dot;

        // How far the miter reaches, as a multiple of the half-width. The miter length is halfWidth / sin(theta/2)
        // and cos(theta) = dot, so sin(theta/2) = sqrt((1+dot)/2) and the ratio is sqrt(2/(1+dot)). A straight run
        // has dot = 1 and so a ratio of 1; a right angle gives sqrt(2); a corner whose included angle is ten degrees
        // gives 11.5, which is the needle a viewer would draw sticking out of the artwork.
        double ratio = Math.Sqrt(2.0 / Math.Max(1e-9, denominator));

        if (denominator < 1e-3 || ratio > Math.Max(1.0, miterLimit))
        {
            // Past the limit the two offset lines meet so far out that the join is a spike, so the corner is
            // bevelled: finite, and the same fallback every renderer uses.
            return points[index] + (Normalise(n1 + n2) * halfWidth);
        }

        double scale = halfWidth / denominator;
        return points[index] + new Vector2D((n1.X + n2.X) * scale, (n1.Y + n2.Y) * scale);
    }

    /// <summary>The unit offset direction at a vertex: left of travel, or its opposite for the right side.</summary>
    private static Vector2D OffsetNormal(Vector2D direction, bool left)
    {
        Vector2D normal = LeftNormal(direction);
        return left ? normal : new Vector2D(-normal.X, -normal.Y);
    }

    private static Vector2D Normalise(Vector2D v)
    {
        double length = Length(v);
        return length < 1e-12 ? new Vector2D(0, 0) : new Vector2D(v.X / length, v.Y / length);
    }

    /// <summary>
    /// Each point's position along the path (0..1 by arc length) and the directions of the segments arriving at
    /// and leaving it.
    ///
    /// **Left is to the left of travel**: in this coordinate system Y grows downward, so for a segment heading
    /// right the left-hand normal points up, which is what a person following the path would call its left.
    /// Getting that backwards mirrors every profile, so it is stated here rather than left to be inferred.
    /// </summary>
    private static (double[] Positions, Vector2D[] Incoming, Vector2D[] Outgoing) Parameterise(
        IReadOnlyList<Point2D> points, bool closed)
    {
        int n = points.Count;
        var positions = new double[n];
        var incoming = new Vector2D[n];
        var outgoing = new Vector2D[n];

        // A closed outline's last point joins back to the first, so that segment counts towards the length. An
        // open one stops at its last point - counting the closing segment there would double the length and make
        // a profile reach only half way along the path.
        int segments = closed ? n : n - 1;
        double total = 0.0;
        var lengths = new double[n];
        for (int i = 0; i < segments; i++)
        {
            lengths[i] = Distance(points[i], points[(i + 1) % n]);
            total += lengths[i];
        }

        for (int i = 0; i < n; i++)
        {
            // The end vertices of an open path have one segment, not two, so both directions are that one.
            bool hasIncoming = closed || i > 0;
            bool hasOutgoing = closed || i < n - 1;
            Vector2D forward = hasOutgoing ? Unit(points[(i + 1) % n] - points[i]) : new Vector2D(0, 0);
            Vector2D backward = hasIncoming ? Unit(points[i] - points[(i - 1 + n) % n]) : new Vector2D(0, 0);
            incoming[i] = hasIncoming ? backward : forward;
            outgoing[i] = hasOutgoing ? forward : backward;
        }

        if (total <= 0)
        {
            for (int i = 0; i < n; i++)
            {
                positions[i] = n <= 1 ? 0 : i / (double)(n - 1);
                if (incoming[i].X == 0 && incoming[i].Y == 0)
                {
                    incoming[i] = new Vector2D(1, 0);
                    outgoing[i] = new Vector2D(1, 0);
                }
            }

            return (positions, incoming, outgoing);
        }

        double walked = 0.0;
        for (int i = 0; i < n; i++)
        {
            positions[i] = walked / total;
            walked += lengths[i];
        }

        return (positions, incoming, outgoing);
    }

    /// <summary>The left-hand normal of a direction: to the left of travel, in a Y-down space.</summary>
    private static Vector2D LeftNormal(Vector2D direction) => new(direction.Y, -direction.X);

    private static double Distance(Point2D a, Point2D b)
        => Math.Sqrt(((b.X - a.X) * (b.X - a.X)) + ((b.Y - a.Y) * (b.Y - a.Y)));

    private static Vector2D Unit(Vector2D v)
    {
        double length = Length(v);
        return length < 1e-12 ? new Vector2D(0, 0) : new Vector2D(v.X / length, v.Y / length);
    }

    private static double Length(Vector2D v) => Math.Sqrt((v.X * v.X) + (v.Y * v.Y));
}
