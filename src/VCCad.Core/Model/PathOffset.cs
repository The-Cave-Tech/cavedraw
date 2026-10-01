using VCCad.Geometry;

namespace VCCad.Core.Model;

/// <summary>
/// Builds the outline of a path stroked with a width profile.
///
/// A width profile is not a stroke with a different width: the stroke's edges are **offset curves**, each
/// following its own half-width along the path. So the work is to parameterise the path, read the profile at
/// each position, and push the centreline out on both sides by the amounts it gives - which is what this does.
///
/// **How the outline is closed.** The loop runs along the left edge and back along the right one, so its two
/// ends are the stroke's **caps** and the corners between its points are the stroke's **joins**. A mitre runs
/// the two edges to their intersection - so the offset distance is the same at a corner as along a segment,
/// which is what "a stroke this wide" means - a bevel cuts straight across it and a round join arcs between
/// them. Where the path doubles back tight enough that a mitre would spike, the corner is bevelled instead.
///
/// **Why the cap and the join are resolved here and not in a renderer.** The canvas, the PDF writer and the SVG
/// writer all fill the loops this returns, out of one <see cref="StrokeOutlineBuilder"/> plan. A filled region
/// has no cap and no join of its own, so an answer left to them would be three answers - and the two that
/// disagreed would be the ones a person compares, an export against the window. Resolving them where the loop
/// is built is what makes all three draw the region the pen would have painted.
///
/// Where the offset edges cross themselves on a tight curve the overlap is left in place: the loop is filled
/// with the **nonzero** rule, where an overlap of the same winding simply fills. Removing the self-intersection
/// instead would mean polygon boolean work whose only visible effect is a shape nobody asked for.
/// </summary>
public static class PathOffset
{
    /// <summary>
    /// How much of a half turn one straight piece covers, for a round cap and for a round join.
    ///
    /// Sampled rather than emitted as an arc because everything downstream of here - the effects, the PDF
    /// writer, the hit tests - works on points. One step serves both, so a cap and a corner of the same radius
    /// are faceted the same way.
    /// </summary>
    private const double ArcStep = Math.PI / 12.0;

    /// <summary>
    /// The filled outline of a stroked path, one closed loop per flattened outline.
    ///
    /// <paramref name="fallbackWidth"/> is used where the profile has nothing to say - an empty profile - so an
    /// empty profile leaves the stroke as it was rather than erasing it.
    ///
    /// <paramref name="cap"/> and <paramref name="join"/> are the stroke's own, and a closed outline has
    /// neither: a loop with no free end is not capped, and its join applies at every vertex.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<Point2D>> Outline(
        IReadOnlyList<FlattenedOutline> outlines,
        WidthProfileSpec? profile,
        double fallbackWidth,
        double miterLimit = 4.0,
        StrokeCap cap = StrokeCap.Butt,
        StrokeJoin join = StrokeJoin.Miter)
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

            var left = new List<Point2D>(points.Count);
            var right = new List<Point2D>(points.Count);
            for (int i = 0; i < points.Count; i++)
            {
                (double leftWidth, double rightWidth) = WidthsAt(profile, fallbackWidth, positions[i]);
                AddCorner(left, points, i, incoming, outgoing, leftWidth, left: true,
                    miterLimit, join, outline.IsClosed);
                AddCorner(right, points, i, incoming, outgoing, rightWidth, left: false,
                    miterLimit, join, outline.IsClosed);
            }

            // The right-hand edge is walked backwards, so its corners are built forwards - where the join's own
            // order is the one the join means - and the whole edge is reversed to run back along the path.
            right.Reverse();

            var loop = new List<Point2D>(left.Count + right.Count + 4);
            loop.AddRange(left);
            if (!outline.IsClosed)
            {
                (double endLeft, double endRight) = WidthsAt(profile, fallbackWidth, positions[^1]);
                AppendCap(
                    loop,
                    left[^1],
                    right[0],
                    Unit(points[^1] - points[^2]),
                    (endLeft + endRight) / 2.0,
                    cap);
            }

            loop.AddRange(right);
            if (!outline.IsClosed)
            {
                (double startLeft, double startRight) = WidthsAt(profile, fallbackWidth, positions[0]);
                AppendCap(
                    loop,
                    right[^1],
                    left[0],
                    Unit(points[0] - points[1]),
                    (startLeft + startRight) / 2.0,
                    cap);
            }

            result.Add(loop);
        }

        return result;
    }

    /// <summary>
    /// Adds where a vertex's two offset edges are brought together, which is the stroke's join.
    ///
    /// The order is the order the contour travels: the offset of the **incoming** segment first, then the join,
    /// then the offset of the outgoing one. Getting that backwards on the right-hand edge - which is walked the
    /// other way - puts the bevel or the arc on the wrong side of the corner and crosses the contour there.
    /// </summary>
    private static void AddCorner(
        List<Point2D> into,
        IReadOnlyList<Point2D> points,
        int index,
        Vector2D[] incoming,
        Vector2D[] outgoing,
        double halfWidth,
        bool left,
        double miterLimit,
        StrokeJoin join,
        bool closed)
    {
        Vector2D n1 = OffsetNormal(incoming[index], left);
        Vector2D n2 = OffsetNormal(outgoing[index], left);

        // An open path's two end vertices have one segment, not two, so there is no corner to join; and a
        // straight run's two edges are the same line, so there the corner is the offset point itself.
        if ((!closed && (index == 0 || index == points.Count - 1)) || IsStraight(n1, n2))
        {
            into.Add(points[index] + (n1 * halfWidth));
            return;
        }

        switch (join)
        {
            case StrokeJoin.Bevel:
                into.Add(points[index] + (n1 * halfWidth));
                into.Add(points[index] + (n2 * halfWidth));
                return;

            case StrokeJoin.Round:
                into.Add(points[index] + (n1 * halfWidth));
                AddRoundJoin(into, points[index], n1, n2, halfWidth);
                return;

            default:
                into.Add(MiterPoint(
                    points, index, incoming, outgoing, halfWidth, left, miterLimit,
                    IsInner(incoming[index], outgoing[index], left)));
                return;
        }
    }

    /// <summary>
    /// Whether this edge is on the side the path turns **towards** - the concave side of the corner.
    ///
    /// The turn's direction is the cross product of the two directions, which is positive when the path turns
    /// towards its own right-hand side. So the left edge is the inner one exactly when that product is negative,
    /// and the right edge exactly when it is positive. This is what tells a corner where a join style applies from
    /// one where only the two offset edges matter - see <see cref="MiterPoint"/>.
    /// </summary>
    private static bool IsInner(Vector2D incoming, Vector2D outgoing, bool left)
    {
        double turn = (incoming.X * outgoing.Y) - (incoming.Y * outgoing.X);
        return left ? turn < 0.0 : turn > 0.0;
    }

    /// <summary>Whether two offset directions describe the same line, so there is no corner between them.</summary>
    private static bool IsStraight(Vector2D n1, Vector2D n2)
        => ((n1.X * n2.X) + (n1.Y * n2.Y)) > 1.0 - 1e-12;

    /// <summary>
    /// A round join: an arc about the vertex from the incoming offset direction to the outgoing one, the short
    /// way round, ending on the outgoing point so the contour leaves the corner along the next edge.
    /// </summary>
    private static void AddRoundJoin(
        List<Point2D> into, Point2D vertex, Vector2D from, Vector2D to, double halfWidth)
    {
        double first = Math.Atan2(from.Y, from.X);
        double sweep = Math.Atan2(to.Y, to.X) - first;
        while (sweep > Math.PI)
        {
            sweep -= Math.PI * 2.0;
        }

        while (sweep < -Math.PI)
        {
            sweep += Math.PI * 2.0;
        }

        int steps = Math.Max(2, (int)Math.Ceiling(Math.Abs(sweep) / ArcStep));
        for (int k = 1; k <= steps; k++)
        {
            double angle = first + (sweep * k / steps);
            into.Add(vertex + new Vector2D(Math.Cos(angle) * halfWidth, Math.Sin(angle) * halfWidth));
        }
    }

    /// <summary>
    /// The points a cap adds between the two sides of an open end, in the order the contour travels.
    ///
    /// Butt adds nothing, because the two sides already meet the end squarely. Square projects them by half the
    /// width, so the end is a rectangle past the line. Round is the half turn about the end's midpoint which
    /// bulges along the direction of travel, sampled into points - the other half turn would carve a bite out of
    /// the stroke instead, which is the mistake the sweep test below exists to prevent.
    ///
    /// A cap is symmetric, so where a profile is wider on one side of the path than the other the two halves are
    /// averaged: there is no cap that is half a round end and half a square one, and an average keeps the end
    /// where the person drew it rather than pushing it to one side of the line.
    /// </summary>
    private static void AppendCap(
        List<Point2D> into, Point2D from, Point2D to, Vector2D forward, double half, StrokeCap cap)
    {
        if (cap == StrokeCap.Butt || half <= 0.0)
        {
            return;
        }

        if (cap == StrokeCap.Square)
        {
            into.Add(from + (forward * half));
            into.Add(to + (forward * half));
            return;
        }

        var centre = new Point2D((from.X + to.X) / 2.0, (from.Y + to.Y) / 2.0);
        double radius = Distance(from, to) / 2.0;
        if (radius <= 0.0)
        {
            return;
        }

        double first = Math.Atan2(from.Y - centre.Y, from.X - centre.X);
        double sweep = Math.Atan2(to.Y - centre.Y, to.X - centre.X) - first;
        while (sweep > Math.PI)
        {
            sweep -= Math.PI * 2.0;
        }

        while (sweep <= -Math.PI)
        {
            sweep += Math.PI * 2.0;
        }

        double middle = first + (sweep / 2.0);
        if ((Math.Cos(middle) * forward.X) + (Math.Sin(middle) * forward.Y) < 0.0)
        {
            sweep += sweep > 0.0 ? -Math.PI * 2.0 : Math.PI * 2.0;
        }

        int steps = Math.Max(2, (int)Math.Ceiling(Math.Abs(sweep) / ArcStep));
        for (int k = 1; k < steps; k++)
        {
            double angle = first + (sweep * k / steps);
            into.Add(new Point2D(
                centre.X + (Math.Cos(angle) * radius), centre.Y + (Math.Sin(angle) * radius)));
        }
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
    ///
    /// **The limit is the outer corner's rule, and does not apply to the inner one.** The inner corner is where
    /// the two offset edges genuinely meet, however far that is, and there is no spike to suppress because the
    /// region folds around it rather than running out to a point. Applying the limit there replaces a corner that
    /// is legitimately a hundred half-widths away with one a single half-width away, and the two edges then have
    /// to travel past it and back - so the loop crosses itself and a filled outline becomes a bow-tie. That is
    /// what Inkscape's powerstroke join test draws.
    /// </summary>
    private static Point2D MiterPoint(
        IReadOnlyList<Point2D> points,
        int index,
        Vector2D[] incoming,
        Vector2D[] outgoing,
        double halfWidth,
        bool left,
        double miterLimit,
        bool inner = false)
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

        if (denominator < 1e-3 || (!inner && ratio > Math.Max(1.0, miterLimit)))
        {
            // Past the limit the two offset lines meet so far out that the join is a spike, so the corner is
            // bevelled: finite, and the same fallback every renderer uses. A doubled-back path has no finite
            // intersection on either side, so the inner corner falls back as well.
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
