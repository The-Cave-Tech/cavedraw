using VCCad.Geometry;

namespace VCCad.Core.Model;

/// <summary>
/// A clip path: the outline a page restricts painting to, and the rule that decides which
/// side of it is inside.
///
/// Clipping is stored rather than resolved away because it is part of the artwork. A file
/// may draw a paragraph several times and use a clip to show one copy, which is how a
/// flattened-transparency demonstration is built; without the clip every copy paints and
/// the page reads as doubled text. It is also how a logo is rounded or a photograph
/// trimmed, and neither is reproducible from the geometry underneath.
///
/// The outline is kept in the model's own coordinates, like every other path, so the
/// renderer and the exporter clip to the same shape without either re-deriving it.
/// </summary>
public sealed class ClipSpec
{
    /// <summary>The outline's subpaths, in the frame of the item being clipped.</summary>
    public List<SubPath> SubPaths { get; } = new();

    /// <summary>Which side of the outline is inside.</summary>
    public FillRule Rule { get; set; } = FillRule.NonZero;

    /// <summary>Whether the clip holds any geometry at all.</summary>
    public bool IsEmpty => SubPaths.Count == 0;

    /// <summary>
    /// Whether <paramref name="point"/> is inside the clip, by the outline's rule.
    ///
    /// Uses the same even-odd and non-zero tests as filling, because PDF defines a clip as
    /// the region a fill would cover.
    /// </summary>
    public bool Contains(Point2D point)
    {
        // An outline with a non-finite coordinate has no inside. Left to the winding
        // tests below it answers "inside" for every query point — NaN fails every
        // comparison, so the edges never straddle and the winding stays 0 for the
        // wrong reason — and a clipped item then paints as though it were not clipped
        // at all. Refusing the outline up front makes the answer "nothing is inside".
        if (!HasFiniteOutline())
        {
            return false;
        }

        if (Rule == FillRule.EvenOdd)
        {
            bool inside = false;
            foreach (SubPath sub in SubPaths)
            {
                if (Crossings(sub, point) % 2 != 0)
                {
                    inside = !inside;
                }
            }

            return inside;
        }

        int winding = SubPaths.Sum(sub => Winding(sub, point));
        return winding != 0;
    }

    /// <summary>
    /// Whether every node of every subpath has finite anchor and handles. A single
    /// non-finite coordinate makes the whole outline unusable.
    /// </summary>
    private bool HasFiniteOutline()
    {
        foreach (SubPath sub in SubPaths)
        {
            foreach (PathNode node in sub.Nodes)
            {
                if (!IsFinite(node.Anchor) || !IsFinite(node.InHandle) || !IsFinite(node.OutHandle))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool IsFinite(Point2D p) => double.IsFinite(p.X) && double.IsFinite(p.Y);

    /// <summary>A copy, so a clip is never shared between two items that might diverge.</summary>
    public ClipSpec Clone()
    {
        var copy = new ClipSpec { Rule = Rule };
        foreach (SubPath sub in SubPaths)
        {
            copy.SubPaths.Add(sub.Clone());
        }

        return copy;
    }

    /// <summary>
    /// How many times a ray to the right of the point crosses the subpath. Segments are
    /// flattened first, because a curve can cross a line twice.
    /// </summary>
    private static int Crossings(SubPath sub, Point2D point)
    {
        int count = 0;
        foreach ((Point2D a, Point2D b) in Segments(sub))
        {
            if ((a.Y > point.Y) == (b.Y > point.Y))
            {
                continue;
            }

            double x = a.X + ((point.Y - a.Y) / (b.Y - a.Y) * (b.X - a.X));
            if (x > point.X)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Which way the subpath winds around the point.</summary>
    private static int Winding(SubPath sub, Point2D point)
    {
        int winding = 0;
        foreach ((Point2D a, Point2D b) in Segments(sub))
        {
            if (a.Y <= point.Y)
            {
                if (b.Y > point.Y && Side(a, b, point) > 0)
                {
                    winding++;
                }
            }
            else if (b.Y <= point.Y && Side(a, b, point) < 0)
            {
                winding--;
            }
        }

        return winding;
    }

    private static double Side(Point2D a, Point2D b, Point2D p)
        => ((b.X - a.X) * (p.Y - a.Y)) - ((p.X - a.X) * (b.Y - a.Y));

    /// <summary>
    /// The subpath's segments as straight pieces, curves flattened finely enough that a
    /// crossing is not missed.
    /// </summary>
    private static IEnumerable<(Point2D A, Point2D B)> Segments(SubPath sub)
    {
        int n = sub.Nodes.Count;
        if (n < 2)
        {
            yield break;
        }

        int last = sub.IsClosed ? n : n - 1;
        for (int i = 0; i < last; i++)
        {
            PathNode from = sub.Nodes[i];
            PathNode to = sub.Nodes[(i + 1) % n];

            const int steps = 16;
            Point2D previous = from.Anchor;
            for (int s = 1; s <= steps; s++)
            {
                double t = (double)s / steps;
                Point2D current = CubicAt(from.Anchor, from.OutHandle, to.InHandle, to.Anchor, t);
                yield return (previous, current);
                previous = current;
            }
        }
    }

    private static Point2D CubicAt(Point2D p0, Point2D p1, Point2D p2, Point2D p3, double t)
    {
        double u = 1 - t;
        double a = u * u * u;
        double b = 3 * u * u * t;
        double c = 3 * u * t * t;
        double d = t * t * t;
        return new Point2D(
            (a * p0.X) + (b * p1.X) + (c * p2.X) + (d * p3.X),
            (a * p0.Y) + (b * p1.Y) + (c * p2.Y) + (d * p3.Y));
    }
}
