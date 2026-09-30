using VCCad.Geometry;

namespace VCCad.Core.Model;

/// <summary>
/// Turns an object's stroke into a filled path - Illustrator's Object, Path, Outline Stroke.
///
/// A stroke is already a shape: a line with round caps is two parallel edges with a curve at each end.
/// This decomposes the object into that shape, so the ink becomes geometry a person can edit, and the
/// result is an ordinary filled path.
///
/// The side each subpath expands to follows from what the subpath is:
///
/// - an **open** subpath traces its whole outside: one contour down one side, round the far cap, back
///   along the other side, and round the start cap;
/// - a **closed** subpath expands to **both** sides of its outline, as an outer contour and an inner
///   one - the band the stroke painted, which straddles the original line.
///
/// For a compound path the two together give the letter B its joinery: the outer contour is banded on
/// its own side, and each hole is banded on the **inside** of that hole, because a hole's outline is its
/// own closed shape.
///
/// The geometry is built from **flattened** outlines - the tolerance recorded in
/// <see cref="PathFlattener"/>, which every vertex here follows - while caps and round joins are emitted
/// as **Bézier arcs**, because a round end is a curve and a polygon standing in for it is visibly faceted
/// at print size.
/// </summary>
public static class StrokeExpander
{
    private const double Kappa = 0.5522847498307936;

    /// <summary>How far a flattened segment may deviate, and so how finely a stroke is built.</summary>
    public static double Tolerance => PathFlattener.Tolerance;

    /// <summary>
    /// The stroke, as a filled path - or null when there is no stroke to expand.
    ///
    /// Nothing is guessed: a stroke with no width, or an invisible one, expands to nothing, because
    /// inventing a width for it would put ink on the page that was not there.
    /// </summary>
    public static PathItem? Expand(PathItem path)
    {
        if (!path.Stroke.HasVisibleOutline || path.Stroke.Width <= 0)
        {
            return null;
        }

        double half = path.Stroke.Width / 2;
        var expanded = new PathItem { Name = path.Name };

        foreach (SubPath sub in path.SubPaths)
        {
            List<Point2D>? points = Flatten(sub);
            if (points is null)
            {
                continue;
            }

            if (sub.IsClosed)
            {
                AddBand(expanded, points, half, path.Stroke);
            }
            else
            {
                AddOutline(expanded, points, half, path.Stroke);
            }
        }

        if (expanded.SubPaths.Count == 0)
        {
            return null;
        }

        // The ink is geometry now: it paints with a fill, and the stroke it came from is spent.
        expanded.Fill = path.Fill.IsVisible ? path.Fill : FillSpec.Solid(path.Stroke.Color);

        // The outline's own stroke is set by the rule in issue #55: below 4pt the original over four,
        // otherwise 1pt. It is a hairline either way - the shape now carries the weight.
        expanded.Stroke = new StrokeSpec(
            true, path.Stroke.Color, OutlineWidth(path.Stroke.Width),
            StrokeCap.Butt, StrokeJoin.Miter, 4);

        expanded.GeometryChanged();
        return expanded;
    }

    /// <summary>
    /// The stroke width for the outline itself: **below 4 pt, the original divided by four; at or above
    /// it, 1 pt**. Exactly 4 pt is covered by neither half of the rule as it was given, and is treated as
    /// the second case - recorded here rather than left to whoever reads it next.
    /// </summary>
    public static double OutlineWidth(double originalWidth)
        => originalWidth < 4.0 ? originalWidth / 4.0 : 1.0;

    // ---- a closed subpath becomes a band ---------------------------------------

    /// <summary>
    /// Both sides of a closed outline, as an outer contour and an inner one. The inner runs the other
    /// way, so under the nonzero rule the band is an outline with a hole rather than a solid blob.
    /// </summary>
    private static void AddBand(PathItem into, List<Point2D> points, double half, StrokeSpec stroke)
    {
        // Which side is "inside" depends on which way the outline runs, so it is read from the geometry
        // rather than assumed: the left of the direction of travel is inside for an outline wound one
        // way round and outside for the other, and getting it backwards insets the shape instead of
        // outlining it.
        double inside = SignedArea(points) > 0 ? half : -half;

        Append(into, Offset(points, -inside, stroke, closed: true));
        Append(into, Offset(points, inside, stroke, closed: true, reverse: true));
    }

    /// <summary>Twice the signed area of a polygon, halved - positive or negative by its winding.</summary>
    private static double SignedArea(List<Point2D> points)
    {
        double total = 0;
        for (int i = 0; i < points.Count; i++)
        {
            Point2D a = points[i];
            Point2D b = points[(i + 1) % points.Count];
            total += (a.X * b.Y) - (b.X * a.Y);
        }

        return total / 2.0;
    }

    // ---- an open subpath becomes one contour -----------------------------------

    /// <summary>
    /// The whole outside of an open path: down one side, round the far cap, back along the other, and
    /// round the start cap. The order matters and is the thing that is easy to get wrong - a cap appended
    /// to the wrong side leaves a contour that crosses itself and paints a bow tie.
    /// </summary>
    private static void AddOutline(PathItem into, List<Point2D> points, double half, StrokeSpec stroke)
    {
        List<PathNode> left = Offset(points, half, stroke, closed: false);
        List<PathNode> right = Offset(points, -half, stroke, closed: false);

        var nodes = new List<PathNode>();
        nodes.AddRange(left);

        Vector2D forward = Direction(points[^2], points[^1]);
        AppendCap(nodes, left[^1].Anchor, right[^1].Anchor, forward, half, stroke.Cap);

        for (int i = right.Count - 1; i >= 0; i--)
        {
            nodes.Add(new PathNode(right[i].Anchor));
        }

        Vector2D backward = Direction(points[1], points[0]);
        AppendCap(nodes, right[0].Anchor, left[0].Anchor, backward, half, stroke.Cap);

        Append(into, nodes);
    }

    /// <summary>
    /// One end of an open stroke, appended in the order the contour travels: the side it is leaving,
    /// round or across, to the side it joins.
    ///
    /// - **butt** adds nothing: the two sides already meet the end squarely.
    /// - **square** projects both sides by half the width, so the end is a rectangle past the line.
    /// - **round** is a half circle of two cubics about the midpoint of the two sides.
    /// </summary>
    private static void AppendCap(
        List<PathNode> nodes, Point2D from, Point2D to, Vector2D forward, double half, StrokeCap cap)
    {
        switch (cap)
        {
            case StrokeCap.Square:
                nodes.Add(new PathNode(new Point2D(
                    from.X + (forward.X * half), from.Y + (forward.Y * half))));
                nodes.Add(new PathNode(new Point2D(
                    to.X + (forward.X * half), to.Y + (forward.Y * half))));
                break;

            case StrokeCap.Round:
                AppendHalfCircle(nodes, from, to, forward, half);
                break;

            default:
                break;
        }
    }

    /// <summary>
    /// Half a circle from <paramref name="from"/> to <paramref name="to"/>, bulging along
    /// <paramref name="forward"/>, as two quarter-circle cubics. The two points are the ends of a
    /// diameter, so the centre is their midpoint and the radius is half their separation.
    /// </summary>
    private static void AppendHalfCircle(
        List<PathNode> nodes, Point2D from, Point2D to, Vector2D forward, double half)
    {
        var centre = new Point2D((from.X + to.X) / 2, (from.Y + to.Y) / 2);
        var middle = new Point2D(centre.X + (forward.X * half), centre.Y + (forward.Y * half));
        double k = half * Kappa;

        PathNode first = nodes.Count > 0 && nodes[^1].Anchor.NearlyEquals(from, 1e-9)
            ? nodes[^1]
            : new PathNode(from);
        if (!nodes.Contains(first))
        {
            nodes.Add(first);
        }

        double sideX = -forward.Y;
        double sideY = forward.X;

        first.OutHandle = new Point2D(from.X + (forward.X * k), from.Y + (forward.Y * k));

        // At the peak the curve is heading back down toward the far side, so the incoming handle is the
        // one on the near side and the outgoing handle on the far one. Swapping them flattens the cap
        // into a shallow lens - which still looks round in a thumbnail and has two thirds of the area.
        var peak = new PathNode(middle)
        {
            InHandle = new Point2D(middle.X + (sideX * k), middle.Y + (sideY * k)),
            OutHandle = new Point2D(middle.X - (sideX * k), middle.Y - (sideY * k)),
        };
        nodes.Add(peak);

        var last = new PathNode(to)
        {
            InHandle = new Point2D(to.X + (forward.X * k), to.Y + (forward.Y * k)),
        };
        nodes.Add(last);
    }

    // ---- offsetting ------------------------------------------------------------

    /// <summary>
    /// A polyline offset to one side by <paramref name="distance"/>, with each vertex's join inserted.
    ///
    /// The join is the interesting part: the two offset edges do not meet at the vertex, and how they are
    /// brought together is what the join style means. A miter runs them to their intersection, unless
    /// that spike is longer than the miter limit allows, in which case it is cut back to a bevel, which is
    /// the whole purpose of the limit. A bevel joins them straight across; a round join is an arc.
    ///
    /// On the inside of a turn the offset edges cross one another and the small loop that leaves is left
    /// alone: it is degenerate area the fill rule ignores, and removing it robustly is a great deal of
    /// work for no visible difference.
    /// </summary>
    private static List<PathNode> Offset(
        List<Point2D> points, double distance, StrokeSpec stroke, bool closed, bool reverse = false)
    {
        var nodes = new List<PathNode>();
        int count = points.Count;

        for (int i = 0; i < count; i++)
        {
            int previous = (i - 1 + count) % count;
            int next = (i + 1) % count;

            // At the two ends of an open path there is one edge, not two: the cap closes the outline.
            bool openStart = !closed && i == 0;
            bool openEnd = !closed && i == count - 1;

            if (openStart || openEnd)
            {
                Vector2D only = openStart
                    ? Direction(points[0], points[1])
                    : Direction(points[^2], points[^1]);
                nodes.Add(new PathNode(OffsetPoint(points[i], only, distance)));
                continue;
            }

            Vector2D incoming = Direction(points[previous], points[i]);
            Vector2D outgoing = Direction(points[i], points[next]);

            Point2D before = OffsetPoint(points[i], incoming, distance);
            Point2D after = OffsetPoint(points[i], outgoing, distance);

            double turn = (incoming.X * outgoing.Y) - (incoming.Y * outgoing.X);
            if (Math.Abs(turn) < 1e-12)
            {
                nodes.Add(new PathNode(before));
                continue;
            }

            switch (stroke.Join)
            {
                case StrokeJoin.Miter:
                    nodes.Add(new PathNode(MiterPoint(points[i], before, after, distance, stroke.MiterLimit)));
                    break;

                case StrokeJoin.Round:
                    nodes.Add(new PathNode(before));
                    // The arc's radius is the offset's magnitude: passing the signed offset draws
                    // the join on the wrong side of the vertex, where it crosses the outline instead of
                    // rounding it.
                    AppendArc(nodes, points[i], before, after, Math.Abs(distance));
                    break;

                default:
                    nodes.Add(new PathNode(before));
                    nodes.Add(new PathNode(after));
                    break;
            }
        }

        if (reverse)
        {
            nodes.Reverse();
        }

        return nodes;
    }

    private static Point2D OffsetPoint(Point2D from, Vector2D direction, double distance)
        => new(from.X + (Left(direction).X * distance), from.Y + (Left(direction).Y * distance));

    /// <summary>
    /// Where the two offset edges meet, or the first offset point when that is further away than the
    /// miter limit allows - a bevel in place of a spike.
    ///
    /// The spike runs along the bisector of the two offset directions, at a distance of
    /// `offset / cos(half the angle between them)`, and the limit is a ratio against the stroke width,
    /// which is the convention PDF and every drawing program use.
    /// </summary>
    private static Point2D MiterPoint(
        Point2D vertex, Point2D before, Point2D after, double distance, double miterLimit)
    {
        var toBefore = new Vector2D(before.X - vertex.X, before.Y - vertex.Y);
        var toAfter = new Vector2D(after.X - vertex.X, after.Y - vertex.Y);

        double bx = toBefore.X + toAfter.X;
        double by = toBefore.Y + toAfter.Y;
        double length = Math.Sqrt((bx * bx) + (by * by));
        if (length < 1e-12)
        {
            return before;
        }

        // The offset's MAGNITUDE, not its signed form: the two sides of a path are offset by +half and
        // -half, and dividing by the negative one flips the cosine's sign, which silently turns every
        // corner on that side into a bevel - a wrong answer that still looks like a corner.
        double offset = Math.Abs(distance);
        double cosHalf = ((toBefore.X * bx) + (toBefore.Y * by)) / (offset * length);
        if (cosHalf <= 1e-9)
        {
            return before;
        }

        double reach = offset / cosHalf;

        // The limit compares the spike with the full stroke width. A limit below 1 makes no sense - the
        // spike is never shorter than half the width - so it is floored there rather than obeyed literally.
        double limit = Math.Max(miterLimit, 1.0);
        if (reach / offset > limit)
        {
            return before;
        }

        return new Point2D(vertex.X + (bx / length * reach), vertex.Y + (by / length * reach));
    }

    /// <summary>
    /// A round join: an arc about the vertex from one offset point to the other, the short way, as cubics
    /// of at most a quarter turn each.
    /// </summary>
    private static void AppendArc(
        List<PathNode> nodes, Point2D vertex, Point2D from, Point2D to, double radius)
    {
        double a0 = Math.Atan2(from.Y - vertex.Y, from.X - vertex.X);
        double a1 = Math.Atan2(to.Y - vertex.Y, to.X - vertex.X);

        double sweep = a1 - a0;
        while (sweep > Math.PI)
        {
            sweep -= 2 * Math.PI;
        }

        while (sweep < -Math.PI)
        {
            sweep += 2 * Math.PI;
        }

        int steps = Math.Max(1, (int)Math.Ceiling(Math.Abs(sweep) / (Math.PI / 2)));
        double step = sweep / steps;

        for (int i = 0; i < steps; i++)
        {
            double start = a0 + (step * i);
            double end = start + step;
            double k = (4.0 / 3.0) * Math.Tan(step / 4.0) * radius;

            var startPoint = new Point2D(
                vertex.X + (radius * Math.Cos(start)), vertex.Y + (radius * Math.Sin(start)));
            var endPoint = new Point2D(
                vertex.X + (radius * Math.Cos(end)), vertex.Y + (radius * Math.Sin(end)));

            // The tangent at each end, in the direction of travel. The sign matters: the circle's tangent
            // runs counterclockwise, so a join swept the other way needs it reversed - without that the
            // arc's handles point backwards, the cubic loops through itself, and the join loses area
            // instead of gaining it, which comes out as a round join smaller than a bevel.
            double sense = sweep >= 0 ? 1 : -1;
            var startTangent = new Vector2D(-Math.Sin(start) * sense, Math.Cos(start) * sense);
            var endTangent = new Vector2D(-Math.Sin(end) * sense, Math.Cos(end) * sense);

            PathNode first = nodes.Count > 0 && nodes[^1].Anchor.NearlyEquals(startPoint, 1e-9)
                ? nodes[^1]
                : new PathNode(startPoint);
            if (!nodes.Contains(first))
            {
                nodes.Add(first);
            }

            first.OutHandle = new Point2D(
                startPoint.X + (startTangent.X * k), startPoint.Y + (startTangent.Y * k));

            nodes.Add(new PathNode(endPoint)
            {
                InHandle = new Point2D(
                    endPoint.X - (endTangent.X * k), endPoint.Y - (endTangent.Y * k)),
            });
        }
    }

    // ---- the input outline -----------------------------------------------------

    /// <summary>The subpath's outline as points, flattened at the same tolerance everything else uses.</summary>
    private static List<Point2D>? Flatten(SubPath sub)
    {
        if (sub.Nodes.Count < 2)
        {
            return null;
        }

        var points = new List<Point2D>();
        int segments = sub.SegmentCount;
        for (int i = 0; i < segments; i++)
        {
            Sample(sub.GetSegment(i), points);
        }

        if (points.Count > 1 && points[0].NearlyEquals(points[^1], 1e-9))
        {
            points.RemoveAt(points.Count - 1);
        }

        return points.Count >= 2 ? points : null;
    }

    private static void Sample(CubicBezier curve, List<Point2D> points)
    {
        if (points.Count == 0)
        {
            points.Add(curve.P0);
        }

        double bow = Math.Max(
            DistanceToLine(curve.P1, curve.P0, curve.P3),
            DistanceToLine(curve.P2, curve.P0, curve.P3));

        int steps = Math.Max(1, Math.Min(64, (int)Math.Ceiling(Math.Sqrt(bow / Math.Max(Tolerance, 1e-9)))));
        for (int i = 1; i <= steps; i++)
        {
            points.Add(curve.PointAt((double)i / steps));
        }
    }

    private static double DistanceToLine(Point2D point, Point2D a, Point2D b)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        double length = Math.Sqrt((dx * dx) + (dy * dy));
        if (length < 1e-12)
        {
            return Math.Sqrt(Math.Pow(point.X - a.X, 2) + Math.Pow(point.Y - a.Y, 2));
        }

        return Math.Abs(((point.X - a.X) * dy) - ((point.Y - a.Y) * dx)) / length;
    }

    // ---- plumbing --------------------------------------------------------------

    private static void Append(PathItem into, List<PathNode> nodes)
    {
        if (nodes.Count < 3)
        {
            return;
        }

        SubPath sub = into.AddSubPath(closed: true);
        foreach (PathNode node in nodes)
        {
            sub.Nodes.Add(new PathNode(node.Anchor, node.InHandle, node.OutHandle));
        }
    }

    private static Vector2D Direction(Point2D from, Point2D to)
    {
        double dx = to.X - from.X;
        double dy = to.Y - from.Y;
        double length = Math.Sqrt((dx * dx) + (dy * dy));
        return length < 1e-12 ? new Vector2D(1, 0) : new Vector2D(dx / length, dy / length);
    }

    /// <summary>The unit normal to the left of a direction, in model coordinates.</summary>
    private static Vector2D Left(Vector2D direction) => new(-direction.Y, direction.X);
}
