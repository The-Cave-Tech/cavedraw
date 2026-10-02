using VCCad.Core.Selection;
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
/// Three decisions are worth stating up front, because they are the difference between a feature that works
/// and one that quietly does the wrong thing:
///
/// - **Every measurement is the object's axis-aligned bounding box.** A rotated object is arranged by the
///   box it occupies, which is what a person sees when they select it and what every other editor does.
/// - **Every box is measured in one frame: the artboard's.** A delta is a displacement, and a displacement
///   means nothing until the frame it is in is named. An object inside a group has its numbers written in
///   the group's frame; measuring it there and comparing it with an object written in the artboard's frame
///   compares two different spaces. The frame is stated by
///   <see cref="SelectionEngine.InArtboard"/>, and the caller carries each delta from it into the item's
///   own frame with <see cref="SelectionEngine.DeltaInItem"/> before touching geometry (#174).
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

        // Measured once per object, before anything moves: every box has to come from the same instant or
        // the target line would be computed from a selection that is already half arranged.
        Dictionary<LayerItem, Rect2D> boxes = Measure(items);

        double start = items.Min(item => Start(boxes[item], axis));
        double end = items.Max(item => End(boxes[item], axis));

        double target = edge switch
        {
            ArrangeEdge.Start => start,
            ArrangeEdge.End => end,
            _ => (start + end) / 2,
        };

        foreach (LayerItem item in items)
        {
            Rect2D box = boxes[item];
            double current = edge switch
            {
                ArrangeEdge.Start => Start(box, axis),
                ArrangeEdge.End => End(box, axis),
                _ => (Start(box, axis) + End(box, axis)) / 2,
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

        // Measured once per object, before anything moves: the span and the gaps below have to describe
        // the same reading of the selection, or the layout is computed from a half-arranged one.
        Dictionary<LayerItem, Rect2D> boxes = Measure(items);

        Rect2D BoxOf(LayerItem item) => boxes.TryGetValue(item, out Rect2D box) ? box : Rect2D.Empty;

        double spanStart = items.Min(item => Start(BoxOf(item), axis));
        double spanEnd = items.Max(item => End(BoxOf(item), axis));
        double span = spanEnd - spanStart;

        double totalWidth = items.Sum(item => Extent(BoxOf(item), axis));
        double gap = (span - totalWidth) / (items.Count - 1);

        double cursor = spanStart;

        if (gap >= 0)
        {
            // There is room: equal gaps, each object keeping its own size.
            foreach (LayerItem item in ordered)
            {
                moves.Add((item, Along(axis, cursor - Start(BoxOf(item), axis))));
                cursor += Extent(BoxOf(item), axis) + gap;
            }

            return moves;
        }

        // No room: equal centres across the same extent, which leaves the overlap alone. The centre range
        // is inset by half the width of the object at each end of the sequence, or the fallback would push
        // those two outside the extent the equal-gap rule holds - and the two rules would then disagree
        // exactly where they meet.
        double centreStart = spanStart + (Extent(BoxOf(ordered[0]), axis) / 2);
        double centreEnd = spanEnd - (Extent(BoxOf(ordered[^1]), axis) / 2);
        double step = (centreEnd - centreStart) / (items.Count - 1);

        double centre = centreStart;

        foreach (LayerItem item in ordered)
        {
            Rect2D box = BoxOf(item);
            double current = (Start(box, axis) + End(box, axis)) / 2;
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

        Dictionary<LayerItem, Rect2D> boxes = Measure(items);
        double span = items.Max(item => End(boxes[item], axis)) - items.Min(item => Start(boxes[item], axis));
        double totalWidth = items.Sum(item => Extent(boxes[item], axis));

        return span - totalWidth < 0;
    }

    /// <summary>
    /// Every object's box in the one frame the arrangement measures in, keyed by the object, taken once so
    /// the span, the order and each object's own delta all come from the same reading.
    /// </summary>
    private static Dictionary<LayerItem, Rect2D> Measure(IReadOnlyList<LayerItem> items)
    {
        var measured = new Dictionary<LayerItem, Rect2D>();
        foreach (LayerItem item in items)
        {
            measured[item] = SelectionEngine.InArtboard(item);
        }

        return measured;
    }

    private static double Start(Rect2D box, ArrangeAxis axis)
        => axis == ArrangeAxis.Horizontal ? box.X : box.Y;

    private static double End(Rect2D box, ArrangeAxis axis)
        => axis == ArrangeAxis.Horizontal ? box.X + box.Width : box.Y + box.Height;

    private static double Extent(Rect2D box, ArrangeAxis axis)
        => axis == ArrangeAxis.Horizontal ? box.Width : box.Height;

    private static Vector2D Along(ArrangeAxis axis, double delta)
        => axis == ArrangeAxis.Horizontal ? new Vector2D(delta, 0) : new Vector2D(0, delta);
}

