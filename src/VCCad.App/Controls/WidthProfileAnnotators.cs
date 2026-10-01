using VCCad.Core.Model;
using VCCad.Geometry;

namespace VCCad.App.Controls;

/// <summary>Which edge of the stroke a width-profile grip sits on, read from the direction of travel.</summary>
public enum WidthProfileSide
{
    /// <summary>To the left of travel, which on a path drawn left to right is above it.</summary>
    Left,

    /// <summary>To the right of travel.</summary>
    Right,
}

/// <summary>
/// One grip: a width point's width, as the edge of the stroke it holds out. The point is where that edge
/// is on the canvas, and <see cref="Width"/> is the full width the stroke has on that side there - twice
/// the distance the grip sits out from the centreline, because a point's widths are full widths and its
/// edges are half of them apart (<see cref="WidthPoint.Halves"/>).
/// </summary>
public readonly record struct WidthProfileGrip(
    int Index,
    WidthProfileSide Side,
    Point2D Point,
    double Width);

/// <summary>
/// The handle one width point offers: where the point falls on the path, the direction across the stroke
/// there, and the two edge grips a person can drag.
///
/// **One handle per width point**, because a handle is what a point *is* on the canvas; the two grips are
/// its two ends. A single grip would have to represent both widths at once, and the case this exists for -
/// a profile that swells on one side only - cannot be expressed by one distance from the centreline.
/// </summary>
public readonly record struct WidthProfileHandle(
    int Index,
    double Position,
    Point2D Centre,
    Vector2D LeftNormal,
    WidthProfileGrip Left,
    WidthProfileGrip Right);

/// <summary>
/// A width profile's on-canvas annotators: where its handles are, which grip a pointer is on, and what
/// dragging it does to the point.
///
/// The profile is measured along the path <see cref="PathOffset"/> offsets - parameterised 0..1 of each
/// subpath's own arc length over the flattened polyline - so the sampling here is that same
/// parameterisation, and a handle sits exactly where the outline it draws was measured from. Two
/// implementations of "where is t on this path" would put the handle somewhere the stroke is not.
///
/// Everything is in **world (model) coordinates**, the space the canvas paints in and the space the
/// pointer is reported in, so the drawing, the hit test and the drag all agree about one set of points
/// without a window being open.
/// </summary>
public static class WidthProfileAnnotators
{
    /// <summary>How near a grip counts as grabbing it, in world units at the current zoom.</summary>
    public const double DefaultTolerance = 6.0;

    /// <summary>The handles for a path and its profile, one per width point, in profile order.</summary>
    public static IReadOnlyList<WidthProfileHandle> Handles(PathItem path, WidthProfileSpec? profile)
    {
        if (path is null || profile is null || profile.IsEmpty)
        {
            return Array.Empty<WidthProfileHandle>();
        }

        // The longest subpath carries the handles. PathOffset applies a profile to **every** subpath in
        // full, so a compound path has the same position 0..1 on each of them; a handle per subpath per
        // point would be more than one handle per point, and the longest is the stroke a person means
        // when they point at one.
        FlattenedOutline? outline = PathFlattener.FlattenForStroke(path)
            .OrderByDescending(Length)
            .FirstOrDefault();
        if (outline is null)
        {
            return Array.Empty<WidthProfileHandle>();
        }

        Vector2D offset = path.ArtboardOffset();
        var handles = new List<WidthProfileHandle>(profile.Points.Count);

        for (int i = 0; i < profile.Points.Count; i++)
        {
            WidthPoint point = profile.Points[i];
            if (Sample(outline, point.Position) is not { } sampled)
            {
                continue;
            }

            Point2D centre = sampled.Point + offset;
            Vector2D left = LeftNormal(sampled.Forward);
            var leftGrip = new WidthProfileGrip(i, WidthProfileSide.Left,
                centre + (left * (point.LeftWidth / 2.0)), point.LeftWidth);
            var rightGrip = new WidthProfileGrip(i, WidthProfileSide.Right,
                centre - (left * (point.RightWidth / 2.0)), point.RightWidth);

            handles.Add(new WidthProfileHandle(i, point.Position, centre, left, leftGrip, rightGrip));
        }

        return handles;
    }

    /// <summary>Every grip of every handle, which is what the pointer actually lands on.</summary>
    public static IReadOnlyList<WidthProfileGrip> Grips(PathItem path, WidthProfileSpec? profile)
    {
        var grips = new List<WidthProfileGrip>();
        foreach (WidthProfileHandle handle in Handles(path, profile))
        {
            grips.Add(handle.Left);
            grips.Add(handle.Right);
        }

        return grips;
    }

    /// <summary>The grip within <paramref name="tolerance"/> of a world point, or null.</summary>
    public static WidthProfileGrip? HitTest(
        PathItem path, WidthProfileSpec? profile, Point2D world, double tolerance = DefaultTolerance)
    {
        WidthProfileGrip? best = null;
        double nearest = tolerance;

        foreach (WidthProfileGrip grip in Grips(path, profile))
        {
            double distance = (world - grip.Point).Length;
            if (distance <= nearest)
            {
                nearest = distance;
                best = grip;
            }
        }

        return best;
    }

    /// <summary>
    /// The profile point as it becomes when a grip is dragged to <paramref name="world"/>.
    ///
    /// The width is the distance the pointer is **across** the stroke, doubled - the grip sits on the
    /// edge, so half the width is how far out it is - and only the dragged grip's own side moves. The
    /// other side keeps what it had, which is what lets a lopsided profile be drawn by dragging one edge.
    ///
    /// **Clamped at zero, never negative.** A negative width is not a thinner stroke: the offset edge
    /// would cross to the other side of the path and the outline would turn inside out, so the geometry
    /// the canvas draws would no longer be the profile the model holds. Past the centreline the width
    /// therefore stops at nothing, which is the narrowest a side can honestly be.
    /// </summary>
    public static WidthPoint Drag(
        PathItem path, WidthProfileSpec profile, WidthProfileGrip grip, Point2D world)
    {
        WidthPoint point = profile.Points[Math.Clamp(grip.Index, 0, profile.Points.Count - 1)];
        if (HandleFor(path, profile, grip.Index) is not { } handle)
        {
            return point;
        }

        // Positive is to the left of travel, so the sign says which way the pointer went across the
        // stroke and the magnitude says how far - the same projection PathOffset offsets along.
        Vector2D offset = world - handle.Centre;
        double across = (offset.X * handle.LeftNormal.X) + (offset.Y * handle.LeftNormal.Y);
        double width = Math.Max(0.0, grip.Side == WidthProfileSide.Left ? across : -across) * 2.0;

        return grip.Side == WidthProfileSide.Left
            ? point with { LeftWidth = width }
            : point with { RightWidth = width };
    }

    /// <summary>The profile with one point's width set from a drag, leaving every other point alone.</summary>
    public static WidthProfileSpec Dragged(
        PathItem path, WidthProfileSpec profile, WidthProfileGrip grip, Point2D world)
    {
        WidthPoint changed = Drag(path, profile, grip, world);
        var points = profile.Points.ToList();
        points[Math.Clamp(grip.Index, 0, points.Count - 1)] = changed;
        return profile with { Points = points };
    }

    private static WidthProfileHandle? HandleFor(
        PathItem path, WidthProfileSpec profile, int index)
    {
        foreach (WidthProfileHandle handle in Handles(path, profile))
        {
            if (handle.Index == index)
            {
                return handle;
            }
        }

        return null;
    }

    /// <summary>A point and the direction the path is travelling there, sampled by arc length.</summary>
    private static (Point2D Point, Vector2D Forward)? Sample(FlattenedOutline outline, double position)
    {
        IReadOnlyList<Point2D> points = outline.Points;
        int count = points.Count;
        if (count < 2)
        {
            return count == 1 ? (points[0], new Vector2D(1, 0)) : null;
        }

        // A closed outline's last point joins back to the first, so that segment is part of its length
        // and of where t falls - the same choice PathOffset makes, and the reason an open path's taper
        // does not stop half way along.
        int segments = outline.IsClosed ? count : count - 1;
        double total = 0.0;
        for (int i = 0; i < segments; i++)
        {
            total += Distance(points[i], points[(i + 1) % count]);
        }

        if (total <= 0.0)
        {
            return (points[0], Unit(points[count - 1] - points[0]));
        }

        double target = Math.Clamp(position, 0.0, 1.0) * total;
        double walked = 0.0;

        for (int i = 0; i < segments; i++)
        {
            Point2D from = points[i];
            Point2D to = points[(i + 1) % count];
            double length = Distance(from, to);
            if (length <= 0.0)
            {
                continue;
            }

            if (walked + length >= target || i == segments - 1)
            {
                double f = Math.Clamp((target - walked) / length, 0.0, 1.0);
                return (new Point2D(
                    from.X + ((to.X - from.X) * f),
                    from.Y + ((to.Y - from.Y) * f)), Unit(to - from));
            }

            walked += length;
        }

        return (points[^1], Unit(points[^1] - points[^2]));
    }

    /// <summary>The left-hand normal of a direction: to the left of travel, in a Y-down space.</summary>
    private static Vector2D LeftNormal(Vector2D direction) => new(direction.Y, -direction.X);

    private static double Length(FlattenedOutline outline)
    {
        IReadOnlyList<Point2D> points = outline.Points;
        int segments = outline.IsClosed ? points.Count : points.Count - 1;
        double total = 0.0;
        for (int i = 0; i < segments; i++)
        {
            total += Distance(points[i], points[(i + 1) % points.Count]);
        }

        return total;
    }

    private static double Distance(Point2D a, Point2D b)
        => Math.Sqrt(((b.X - a.X) * (b.X - a.X)) + ((b.Y - a.Y) * (b.Y - a.Y)));

    private static Vector2D Unit(Vector2D v)
    {
        double length = Math.Sqrt((v.X * v.X) + (v.Y * v.Y));
        return length < 1e-12 ? new Vector2D(1, 0) : new Vector2D(v.X / length, v.Y / length);
    }
}
