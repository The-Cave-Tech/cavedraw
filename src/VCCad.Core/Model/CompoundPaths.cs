using VCCad.Geometry;

namespace VCCad.Core.Model;

/// <summary>
/// A compound path: several closed outlines filled as one object, where the inner ones are holes.
///
/// The model already holds several subpaths in one <see cref="PathItem"/> with a <see cref="FillRule"/>,
/// so a compound path needs no new geometry type. What it needs is the **semantics**: which subpaths are
/// holes, which way they wind, and how to take one apart again.
///
/// Both fill rules are real and the difference matters. Under **even-odd** a region alternates filled
/// and hollow by nesting depth, so winding is irrelevant. Under **nonzero** a hole must wind *against*
/// the outline containing it, or it fills solid. Illustrator writes compound paths either way depending
/// on how they were made and reads both, so both are expressible here, and
/// <see cref="Normalise"/> puts the windings into the form the nonzero rule needs.
/// </summary>
public static class CompoundPaths
{
    /// <summary>
    /// Whether the path is several closed outlines filled as one object. One outline is just a path,
    /// and an open one cannot be a hole.
    /// </summary>
    public static bool IsCompound(PathItem path)
        => path.SubPaths.Count > 1 && path.SubPaths.All(sub => sub.IsClosed && sub.Nodes.Count >= 3);

    /// <summary>
    /// Which subpaths are holes, by **nesting**: an outline inside an odd number of others is a hole.
    ///
    /// Nesting rather than winding, because an outline's winding is exactly what is in doubt when this
    /// is asked - a hole that has been drawn the same way round as its container is still a hole, and
    /// saying otherwise would make the answer depend on the problem.
    /// </summary>
    public static IReadOnlyList<bool> Holes(PathItem path)
    {
        FlattenedOutline?[] outlines = path.SubPaths.Select(OutlineOf).ToArray();
        var holes = new bool[path.SubPaths.Count];

        for (int i = 0; i < outlines.Length; i++)
        {
            FlattenedOutline? probe = outlines[i];
            if (probe is null)
            {
                continue;
            }

            // A point just inside the outline: the middle of one of its edges, pushed along the inward
            // normal. A point ON the boundary would be ambiguous - it is inside both the outline and
            // whatever contains it.
            Point2D inside = Inset(probe);
            int depth = 0;

            for (int j = 0; j < outlines.Length; j++)
            {
                if (j != i && outlines[j] is { } other && other.Contains(inside))
                {
                    depth++;
                }
            }

            holes[i] = depth % 2 == 1;
        }

        return holes;
    }

    /// <summary>
    /// Puts the windings into the form the **nonzero** rule needs for the holes to be holes: the
    /// outermost outline one way, every outline inside it the other, alternating with depth.
    ///
    /// Returns whether anything had to change. Under even-odd this does nothing useful and nothing
    /// harmful - that rule ignores direction entirely - so it is safe to call without asking which rule
    /// the object uses.
    /// </summary>
    public static bool Normalise(PathItem path)
    {
        IReadOnlyList<bool> holes = Holes(path);
        bool changed = false;

        for (int i = 0; i < path.SubPaths.Count; i++)
        {
            FlattenedOutline? outline = OutlineOf(path.SubPaths[i]);
            if (outline is null)
            {
                continue;
            }

            // A hole should be negative, an island positive, in the drawing's own coordinates.
            bool wantPositive = !holes[i];
            if (outline.IsPositive != wantPositive)
            {
                path.SubPaths[i].Reverse();
                changed = true;
            }
        }

        if (changed)
        {
            path.GeometryChanged();
        }

        return changed;
    }

    /// <summary>
    /// Takes a compound path apart: one path per outline, each keeping the original's appearance, so a
    /// letter B becomes three objects that can be moved independently.
    /// </summary>
    public static IReadOnlyList<PathItem> Release(PathItem path)
    {
        var released = new List<PathItem>();

        foreach (SubPath sub in path.SubPaths)
        {
            PathItem piece = new()
            {
                Name = path.Name,
                Fill = path.Fill,
                Stroke = path.Stroke,
                Opacity = path.Opacity,
                Shape = path.Shape,
            };

            SubPath copy = piece.AddSubPath(sub.IsClosed);
            foreach (PathNode node in sub.Nodes)
            {
                copy.Nodes.Add(new PathNode(node.Anchor, node.InHandle, node.OutHandle));
            }

            piece.GeometryChanged();
            released.Add(piece);
        }

        return released;
    }

    /// <summary>
    /// Turns one subpath inside out: a hole becomes an island and an island becomes a hole. Under the
    /// nonzero rule that is the only thing that changes - the geometry is identical - which is why it is
    /// a one-keystroke operation in every editor.
    /// </summary>
    public static bool Reverse(PathItem path, int index)
    {
        if (index < 0 || index >= path.SubPaths.Count)
        {
            return false;
        }

        path.SubPaths[index].Reverse();
        path.GeometryChanged();
        return true;
    }

    /// <summary>The subpath as a flattened outline, or null when it has no area.</summary>
    private static FlattenedOutline? OutlineOf(SubPath sub)
    {
        if (sub.Nodes.Count < 3)
        {
            return null;
        }

        var single = new PathItem();
        single.SubPaths.Add(sub);
        IReadOnlyList<FlattenedOutline> outlines = PathFlattener.Flatten(single);
        return outlines.Count == 1 ? outlines[0] : null;
    }

    /// <summary>A point a little way inside an outline, found from one of its edges.</summary>
    private static Point2D Inset(FlattenedOutline outline)
    {
        Point2D a = outline.Points[0];
        Point2D b = outline.Points[1 % outline.Points.Count];

        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        double length = Math.Sqrt((dx * dx) + (dy * dy));
        if (length < 1e-12)
        {
            return new Point2D((a.X + b.X) / 2, (a.Y + b.Y) / 2);
        }

        // The inward normal depends on which way the outline runs, and the outline's own sign is what
        // says so - so the probe lands inside whether this outline is wound one way or the other.
        double sign = outline.IsPositive ? 1.0 : -1.0;
        double step = Math.Min(length, 1.0) * 0.01;
        double nx = -dy / length * sign;
        double ny = dx / length * sign;

        return new Point2D(((a.X + b.X) / 2) + (nx * step), ((a.Y + b.Y) / 2) + (ny * step));
    }
}
