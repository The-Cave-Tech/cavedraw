using VCCad.Geometry;

namespace VCCad.Core.Model;

/// <summary>
/// One family of parallel lines in a hatch, and how each line is drawn.
///
/// A family needs four things to be a family: a **direction**, a **starting offset** in x and y, the
/// **distance between repetitions**, and the stroke. The offset says where it starts and the spacing says how
/// far apart the lines are; only the angle says which way they run, so it is part of the definition rather
/// than something implied.
/// </summary>
public sealed record HatchLineSpec(
    double AngleDegrees,
    double OffsetX,
    double OffsetY,
    double Spacing,
    double Width = 1.0,
    DashPattern Dash = default,
    StrokeCap Cap = StrokeCap.Butt)
{
    /// <summary>A family at 45 degrees, a millimetre apart, drawn one point wide.</summary>
    public static HatchLineSpec Standard(double spacing = 4.0)
        => new(45, 0, 0, spacing);
}

/// <summary>
/// A hatch fill: one or more families of parallel lines, drawn in the object's stroke colour and clipped to
/// the object's own region.
/// </summary>
public sealed record HatchSpec(IReadOnlyList<HatchLineSpec> Lines)
{
    /// <summary>One family at a given angle, which is what a person draws first.</summary>
    public static HatchSpec Single(double angleDegrees, double spacing, double width = 1.0, DashPattern dash = default)
        => new(new[] { new HatchLineSpec(angleDegrees, 0, 0, spacing, width, dash) });

    /// <summary>The ordinary cross-hatch: the same spacing at two angles.</summary>
    public static HatchSpec Cross(double spacing = 4.0)
        => new(new[]
        {
            new HatchLineSpec(45, 0, 0, spacing),
            new HatchLineSpec(-45, 0, 0, spacing),
        });

    public bool IsEmpty => Lines.Count == 0 || Lines.All(l => l.Spacing <= 0 || l.Width <= 0);
}

/// <summary>One drawn piece of a hatch line, already clipped to the region it fills.</summary>
public readonly record struct HatchSegment(Point2D A, Point2D B, HatchLineSpec Line);

/// <summary>
/// Turns a hatch into the segments that are actually drawn.
///
/// **The clipping is a walk, not a polygon operation.** A hatch line is straight and the region is a set of
/// flattened outlines, so the answer is found by walking the line: collect every crossing with the outlines,
/// sort them along the line, and walk that list with the object's own fill rule - parity for even-odd, the
/// running winding for nonzero. Concavities and holes come out right because the rule is the one the object
/// already carries, and there is no polygon arithmetic to get subtly wrong.
///
/// The result is segments **already clipped to the object**, which is what makes the feature testable without
/// pixels ("which points does this hatch cover") and what an exported file needs: the clipped segments are
/// what a PDF should contain, so the exporter writes the same geometry the renderer draws rather than a second
/// implementation that has to agree with it.
/// </summary>
public static class HatchGenerator
{
    /// <summary>The drawn pieces of a hatch over a region, or empty when there is nothing to draw.</summary>
    public static IReadOnlyList<HatchSegment> Segments(
        HatchSpec hatch,
        IReadOnlyList<FlattenedOutline> outlines,
        FillRule rule,
        Rect2D area)
    {
        var segments = new List<HatchSegment>();
        if (hatch.IsEmpty || outlines.Count == 0 || area.IsEmpty)
        {
            return segments;
        }

        foreach (HatchLineSpec line in hatch.Lines)
        {
            if (line.Spacing <= 0 || line.Width <= 0)
            {
                continue;
            }

            double angle = line.AngleDegrees * Math.PI / 180.0;
            var direction = new Vector2D(Math.Cos(angle), Math.Sin(angle));
            var normal = new Vector2D(-direction.Y, direction.X);

            // Where the area sits along the normal, and where the family's first line sits on it.
            (double low, double high) = Project(area, normal);
            double first = (line.OffsetX * normal.X) + (line.OffsetY * normal.Y);

            // Start at the first repetition at or past the area's near edge.
            long start = (long)Math.Ceiling((low - first) / line.Spacing);
            for (long k = start; ; k++)
            {
                double offset = first + (k * line.Spacing);
                if (offset > high + 1e-9)
                {
                    break;
                }

                // A point on the line, and how far it runs across the area along the direction.
                var origin = new Point2D(normal.X * offset, normal.Y * offset);
                (double tLow, double tHigh) = ProjectAlong(area, direction, origin);
                if (tHigh - tLow <= 1e-9)
                {
                    continue;
                }

                Walk(outlines, rule, origin, direction, tLow, tHigh, line, segments);

                // A guard against an unbounded loop if the area is degenerate.
                if (k - start > 100_000)
                {
                    break;
                }
            }
        }

        return segments;
    }

    /// <summary>Walks one line across the outlines and records the inside runs.</summary>
    private static void Walk(
        IReadOnlyList<FlattenedOutline> outlines,
        FillRule rule,
        Point2D origin,
        Vector2D direction,
        double tLow,
        double tHigh,
        HatchLineSpec line,
        List<HatchSegment> segments)
    {
        var crossings = new List<(double T, int Winding)>();

        foreach (FlattenedOutline outline in outlines)
        {
            IReadOnlyList<Point2D> points = outline.Points;
            for (int i = 0; i < points.Count; i++)
            {
                Point2D a = points[i];
                Point2D b = points[(i + 1) % points.Count];

                double da = ((a.X - origin.X) * direction.Y) - ((a.Y - origin.Y) * direction.X);
                double db = ((b.X - origin.X) * direction.Y) - ((b.Y - origin.Y) * direction.X);

                // Half-open: an edge counts when its ends straddle the line, and a vertex exactly on it is
                // attributed to one side consistently, so a shared vertex is not counted twice.
                if ((da > 0) == (db > 0))
                {
                    continue;
                }

                double s = da / (da - db);
                double t = (((a.X - origin.X) + ((b.X - a.X) * s)) * direction.X)
                           + (((a.Y - origin.Y) + ((b.Y - a.Y) * s)) * direction.Y);
                if (t < tLow - 1e-9 || t > tHigh + 1e-9)
                {
                    continue;
                }

                double cross = ((b.X - a.X) * direction.Y) - ((b.Y - a.Y) * direction.X);
                crossings.Add((t, cross > 0 ? 1 : -1));
            }
        }

        if (crossings.Count < 2)
        {
            return;
        }

        crossings.Sort((x, y) => x.T.CompareTo(y.T));

        int winding = 0;
        for (int i = 0; i < crossings.Count - 1; i++)
        {
            winding += crossings[i].Winding;
            bool inside = rule == FillRule.EvenOdd ? ((i + 1) % 2) == 1 : winding != 0;
            if (!inside)
            {
                continue;
            }

            double from = crossings[i].T;
            double to = crossings[i + 1].T;
            if (to - from <= 1e-9)
            {
                continue;
            }

            segments.Add(new HatchSegment(
                origin + (direction * from),
                origin + (direction * to),
                line));
        }
    }

    /// <summary>The range the area's corners cover along an axis.</summary>
    private static (double Low, double High) Project(Rect2D area, Vector2D axis)
    {
        double a = (area.Left * axis.X) + (area.Top * axis.Y);
        double b = (area.Right * axis.X) + (area.Top * axis.Y);
        double c = (area.Left * axis.X) + (area.Bottom * axis.Y);
        double d = (area.Right * axis.X) + (area.Bottom * axis.Y);
        return (Math.Min(Math.Min(a, b), Math.Min(c, d)), Math.Max(Math.Max(a, b), Math.Max(c, d)));
    }

    /// <summary>How far the area extends along a direction from a point on the line.</summary>
    private static (double Low, double High) ProjectAlong(Rect2D area, Vector2D direction, Point2D origin)
    {
        double a = ((area.Left - origin.X) * direction.X) + ((area.Top - origin.Y) * direction.Y);
        double b = ((area.Right - origin.X) * direction.X) + ((area.Top - origin.Y) * direction.Y);
        double c = ((area.Left - origin.X) * direction.X) + ((area.Bottom - origin.Y) * direction.Y);
        double d = ((area.Right - origin.X) * direction.X) + ((area.Bottom - origin.Y) * direction.Y);
        return (Math.Min(Math.Min(a, b), Math.Min(c, d)), Math.Max(Math.Max(a, b), Math.Max(c, d)));
    }
}
