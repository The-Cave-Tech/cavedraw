using VCCad.Geometry;

namespace VCCad.Core.Model;

/// <summary>Which way along the page the arrangement acts.</summary>
public enum ArrangeAxis
{
    Horizontal,
    Vertical,
}

/// <summary>Which part of an object is put on the line, or lines up with its neighbours.</summary>
public enum ArrangeEdge
{
    /// <summary>Left or top.</summary>
    Start,

    /// <summary>Horizontal or vertical centre.</summary>
    Centre,

    /// <summary>Right or bottom.</summary>
    End,
}

/// <summary>Which end of the stack is laid out first.</summary>
public enum ArrangeAnchor
{
    /// <summary>The first object is placed first and the rest follow it.</summary>
    Start,

    /// <summary>The last object is placed first and the rest are laid out back towards the start.</summary>
    End,
}

/// <summary>
/// Aligning and distributing the selection.
///
/// The arrangement is computed as a set of **deltas** rather than applied here: the caller owns the
/// command stack and the different object kinds, so this is the arithmetic and nothing else, and it can
/// be tested without a document.
///
/// Two decisions are worth stating up front, because they are the difference between a feature that works
/// and one that quietly does the wrong thing:
///
/// - **Every measurement is the object's axis-aligned bounding box.** A rotated object is arranged by the
///   box it occupies, which is what a person sees when they select it and what every other editor does.
/// - **The order is the stack order, never the position.** Objects are arranged in z-order (bottom to
///   top), so a person's stack is honoured; when they are already in positional order nothing moves, and
///   when they are not, distributing puts them in order, which is what asking for it means.
/// </summary>
public static class Arrange
{
    /// <summary>
    /// Moves every object so the chosen edge or centre lies on the same line: the selection's own extent.
    ///
    /// The line is the selection's, not the artboard's and not a key object's, because that is what
    /// "align these" means when a person selects a group and presses one button.
    /// </summary>
    public static IReadOnlyList<(LayerItem Item, Vector2D Delta)> Align(
        IReadOnlyList<LayerItem> items, ArrangeAxis axis, ArrangeEdge edge)
    {
        var moves = new List<(LayerItem, Vector2D)>();

        if (items.Count < 2)
        {
            return moves;
        }

        double start = items.Min(item => Start(item, axis));
        double end = items.Max(item => End(item, axis));

        double target = edge switch
        {
            ArrangeEdge.Start => start,
            ArrangeEdge.End => end,
            _ => (start + end) / 2,
        };

        foreach (LayerItem item in items)
        {
            double current = edge switch
            {
                ArrangeEdge.Start => Start(item, axis),
                ArrangeEdge.End => End(item, axis),
                _ => (Start(item, axis) + End(item, axis)) / 2,
            };

            double delta = target - current;
            if (Math.Abs(delta) > 1e-9)
            {
                moves.Add((item, Along(axis, delta)));
            }
        }

        return moves;
    }

    /// <summary>
    /// Spreads the objects out with **equal gaps** between them, in stack order, holding the selection's
    /// extent. <paramref name="anchor"/> decides which end of the stack is laid out first: from the start
    /// the first object is placed first and the rest follow it, from the end the last is placed first and
    /// the rest are laid out back towards the start.
    ///
    /// **Overlapping objects are the case that needs a second rule.** Equal gaps need room for the gaps,
    /// and overlapping objects have taken all of it: the sum of their own widths is greater than the span
    /// they occupy, so the gap that would make the spacing equal is **negative** and there is no sensible
    /// equal-gap answer. Rather than push objects into one another or refuse to act, the fallback spreads
    /// their **centres** evenly over the same extent: sizes and order untouched, the overlap they already
    /// had left alone, and the result as even as the situation allows.
    ///
    /// The two rules meet where the gap reaches zero, which is why they are chosen by computing the gap
    /// rather than by counting overlaps: there is no jump between them.
    /// </summary>
    public static IReadOnlyList<(LayerItem Item, Vector2D Delta)> Distribute(
        IReadOnlyList<LayerItem> items, ArrangeAxis axis, ArrangeAnchor anchor = ArrangeAnchor.Start)
    {
        var moves = new List<(LayerItem, Vector2D)>();

        // Two objects are already evenly spaced; there is nothing between them to even out.
        if (items.Count < 3)
        {
            return moves;
        }

        // The order the objects are laid out in: the stack, or the stack backwards.
        List<LayerItem> ordered = anchor == ArrangeAnchor.Start
            ? items.ToList()
            : Enumerable.Reverse(items).ToList();

        double spanStart = items.Min(item => Start(item, axis));
        double spanEnd = items.Max(item => End(item, axis));
        double span = spanEnd - spanStart;

        double totalWidth = items.Sum(item => Extent(item, axis));
        double gap = (span - totalWidth) / (items.Count - 1);

        double cursor = spanStart;

        if (gap >= 0)
        {
            // There is room: equal gaps, each object keeping its own size.
            foreach (LayerItem item in ordered)
            {
                moves.Add((item, Along(axis, cursor - Start(item, axis))));
                cursor += Extent(item, axis) + gap;
            }

            return moves;
        }

        // No room: equal centres across the same extent, which leaves the overlap alone. The centre range
        // is inset by half the width of the object at each end of the sequence, or the fallback would push
        // those two outside the extent the equal-gap rule holds - and the two rules would then disagree
        // exactly where they meet.
        double centreStart = spanStart + (Extent(ordered[0], axis) / 2);
        double centreEnd = spanEnd - (Extent(ordered[^1], axis) / 2);
        double step = (centreEnd - centreStart) / (items.Count - 1);

        double centre = centreStart;

        foreach (LayerItem item in ordered)
        {
            double current = (Start(item, axis) + End(item, axis)) / 2;
            moves.Add((item, Along(axis, centre - current)));
            centre += step;
        }

        return moves;
    }

    /// <summary>Whether distributing these objects has to fall back to centres, because they overlap.</summary>
    public static bool OverlapsTooMuchForGaps(IReadOnlyList<LayerItem> items, ArrangeAxis axis)
    {
        if (items.Count < 3)
        {
            return false;
        }

        double span = items.Max(item => End(item, axis)) - items.Min(item => Start(item, axis));
        double totalWidth = items.Sum(item => Extent(item, axis));

        return span - totalWidth < 0;
    }

    private static double Start(LayerItem item, ArrangeAxis axis)
        => axis == ArrangeAxis.Horizontal ? ItemBounds.Of(item).X : ItemBounds.Of(item).Y;

    private static double End(LayerItem item, ArrangeAxis axis)
    {
        Rect2D box = ItemBounds.Of(item);
        return axis == ArrangeAxis.Horizontal ? box.X + box.Width : box.Y + box.Height;
    }

    private static double Extent(LayerItem item, ArrangeAxis axis)
    {
        Rect2D box = ItemBounds.Of(item);
        return axis == ArrangeAxis.Horizontal ? box.Width : box.Height;
    }

    private static Vector2D Along(ArrangeAxis axis, double delta)
        => axis == ArrangeAxis.Horizontal ? new Vector2D(delta, 0) : new Vector2D(0, delta);
}
