using VCCad.Core.Model;
using VCCad.Geometry;

namespace VCCad.App.Views.Panes;

/// <summary>What a row in the Layers panel is.</summary>
public enum LayerRowKind
{
    Artboard,
    Layer,
    Path,
    Text,
    Image,
    Group,
    Pasteboard,
}

/// <summary>
/// One row of the Layers panel, flattened.
///
/// Flattened rather than nested because a driver needs to read the panel, and reading it
/// should not mean walking a tree of live controls that a dump cannot see. The nesting is
/// carried in <see cref="Depth"/>, which is what the panel draws its vertical rules from.
/// </summary>
/// <param name="Depth">How many levels deep the row is; 0 is a page.</param>
/// <param name="Kind">What the row holds.</param>
/// <param name="Label">Exactly what the panel prints for it.</param>
/// <param name="ItemId">The object's id, or null for a page, layer or the pasteboard.</param>
/// <param name="IsUserNamed">Whether a person gave it the name shown.</param>
/// <param name="Visible">Whether it draws.</param>
/// <param name="Expandable">Whether the row can be opened.</param>
/// <param name="ChildCount">How many rows are directly under it.</param>
public sealed record LayerRow(
    int Depth,
    LayerRowKind Kind,
    string Label,
    Guid? ItemId,
    bool IsUserNamed,
    bool Visible,
    bool Expandable,
    int ChildCount);

/// <summary>
/// The panel's contents, built once and used by both the panel and the API.
///
/// The two must not drift: an operation that reports what the panel "would" show is worth
/// nothing if the panel shows something else, and the only way to be sure is for there to be
/// one traversal. The panel keeps the objects it needs for selection; this describes them.
/// </summary>
public static class LayerTree
{
    /// <summary>
    /// Whether an item belongs to an artboard, in document space.
    ///
    /// Item coordinates are stored relative to their artboard, so an item's own bounds are
    /// near the origin while an artboard's are its place in the sheet's grid. Comparing the
    /// two directly asks "is this item near the top-left of the whole document", which is
    /// only ever true of the first page.
    /// </summary>
    public static bool IntersectsArtboard(Rect2D bounds, Vector2D offset, Artboard artboard)
    {
        if (bounds.IsEmpty)
        {
            return true;
        }

        var page = new Rect2D(
            bounds.Left + offset.X, bounds.Top + offset.Y, bounds.Width, bounds.Height);

        return page.Intersects(artboard.Bounds.Inflated(0.25));
    }

    /// <summary>
    /// Which items belong to which layer, and which belong to no artboard at all.
    /// </summary>
    public static (Dictionary<Layer, List<LayerItem>> ByLayer, List<LayerItem> Pasteboard)
        Classify(CadDocument document)
    {
        var byLayer = new Dictionary<Layer, List<LayerItem>>();
        var pasteboard = new List<LayerItem>();

        foreach (Artboard artboard in document.Artboards)
        {
            foreach (Layer layer in artboard.Layers)
            {
                var mine = new List<LayerItem>();
                byLayer[layer] = mine;

                foreach (LayerItem child in layer.Children)
                {
                    if (IntersectsArtboard(BoundsOf(child), child.ArtboardOffset(), artboard))
                    {
                        mine.Add(child);
                    }
                    else
                    {
                        pasteboard.Add(child);
                    }
                }
            }
        }

        pasteboard.AddRange(document.Orphans.Children);
        return (byLayer, pasteboard);
    }

    /// <summary>
    /// The bounds an item occupies, before its artboard offset is applied. Delegated so the panel, the
    /// canvas and the arranging code cannot disagree about where a group is.
    /// </summary>
    public static Rect2D BoundsOf(LayerItem item) => ItemBounds.Of(item);

    /// <summary>
    /// Every row the panel shows, in the order it shows them.
    ///
    /// <paramref name="expanded"/> is what the person has opened. It only affects which rows
    /// are reported, never their labels, because collapsing a row hides its contents rather
    /// than changing what they are.
    /// </summary>
    public static IReadOnlyList<LayerRow> Rows(
        CadDocument document, Func<LayerItem, bool>? isExpanded = null)
    {
        var rows = new List<LayerRow>();
        (Dictionary<Layer, List<LayerItem>> byLayer, List<LayerItem> loose) = Classify(document);

        foreach (Artboard artboard in document.Artboards)
        {
            rows.Add(new LayerRow(
                Depth: 0,
                Kind: LayerRowKind.Artboard,
                Label: artboard.Name,
                ItemId: null,
                IsUserNamed: false,
                Visible: artboard.IsVisible,
                Expandable: artboard.Layers.Count > 0,
                ChildCount: artboard.Layers.Count));

            foreach (Layer layer in artboard.Layers)
            {
                List<LayerItem> mine = byLayer.TryGetValue(layer, out List<LayerItem>? held)
                    ? held
                    : new List<LayerItem>();

                rows.Add(new LayerRow(
                    Depth: 1,
                    Kind: LayerRowKind.Layer,
                    Label: layer.Name,
                    ItemId: null,
                    IsUserNamed: false,
                    Visible: layer.IsVisible,
                    Expandable: mine.Count > 0,
                    ChildCount: mine.Count));

                foreach (LayerItem item in mine)
                {
                    AddRows(rows, item, depth: 2, isExpanded);
                }
            }
        }

        if (loose.Count > 0)
        {
            rows.Add(new LayerRow(
                Depth: 0,
                Kind: LayerRowKind.Pasteboard,
                Label: "Pasteboard",
                ItemId: null,
                IsUserNamed: false,
                Visible: true,
                Expandable: true,
                ChildCount: loose.Count));

            foreach (LayerItem item in loose)
            {
                AddRows(rows, item, depth: 1, isExpanded);
            }
        }

        return rows;
    }

    private static void AddRows(
        List<LayerRow> rows, LayerItem item, int depth, Func<LayerItem, bool>? isExpanded)
    {
        int children = item is ArtGroup group ? group.Children.Count : 0;
        bool expandable = children > 0;
        bool open = expandable && (isExpanded?.Invoke(item) ?? true);

        rows.Add(new LayerRow(
            Depth: depth,
            Kind: KindOf(item),
            Label: ObjectNaming.DisplayName(item),
            ItemId: item.Id,
            IsUserNamed: item.NameIsUserSet && !string.IsNullOrWhiteSpace(item.Name),
            Visible: item.IsVisible,
            Expandable: expandable,
            ChildCount: children));

        if (open && item is ArtGroup openGroup)
        {
            foreach (LayerItem child in openGroup.Children)
            {
                AddRows(rows, child, depth + 1, isExpanded);
            }
        }
    }

    private static LayerRowKind KindOf(LayerItem item) => item switch
    {
        ImageItem => LayerRowKind.Image,
        TextItem => LayerRowKind.Text,
        ArtGroup => LayerRowKind.Group,
        _ => LayerRowKind.Path,
    };
}
