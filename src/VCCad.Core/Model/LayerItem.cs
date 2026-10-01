using VCCad.Geometry;
namespace VCCad.Core.Model;

/// <summary>
/// Something that can directly contain <see cref="LayerItem"/>s: a
/// <see cref="Layer"/> or a nested <see cref="ArtGroup"/>.
///
/// Two structural rules keep the graph coherent:
/// <list type="bullet">
/// <item>An item has exactly one container at a time; adding it elsewhere first
/// removes it from its current container.</item>
/// <item>Cycles are impossible because an item can never contain itself (only
/// <see cref="ArtGroup"/> contains children, and groups cannot parent their own
/// ancestors).</item>
/// </list>
/// </summary>
public interface IItemContainer
{
    /// <summary>Children in z-order: index 0 is the bottom-most object.</summary>
    IReadOnlyList<LayerItem> Children { get; }

    /// <summary>
    /// Adds <paramref name="item"/> on top of this container (or at z-index
    /// <paramref name="zIndex"/> when supplied). Returns the assigned z-index.
    /// </summary>
    int AddItem(LayerItem item, int? zIndex = null);

    /// <summary>Removes the item from this container. Returns false when absent.</summary>
    bool RemoveItem(LayerItem item);

    /// <summary>
    /// Raised whenever the child list changes (add, remove, reorder). The UI and
    /// the change bus subscribe here rather than polling the tree.
    /// </summary>
    event EventHandler? StructureChanged;
}

/// <summary>
/// Anything that can live inside a container: a group or a path. This is the
/// "art object" node of the model (Illustrator's page items). Leaf styling —
/// fill and stroke — lives on <see cref="PathItem"/> only; groups are pure
/// structure plus a transform and an opacity.
/// </summary>
public abstract class LayerItem : CadObject
{
    private bool _isVisible = true;
    private bool _isLocked;

    /// <summary>When false the object is skipped during rendering and hit testing.</summary>
    public bool IsVisible
    {
        get => _isVisible;
        set => SetField(ref _isVisible, value);
    }

    /// <summary>When true the object is immune to edit/selection operations.</summary>
    public bool IsLocked
    {
        get => _isLocked;
        set => SetField(ref _isLocked, value);
    }

    /// <summary>
    /// The clip paths that were in force when this item was painted, outermost first, or
    /// empty when it was not clipped.
    ///
    /// Clipping is part of the artwork, not a rendering detail to be applied later: a file
    /// may draw the same paragraph several times and use a clip to show one copy, and
    /// without the clip every copy paints. Several clips mean their intersection — PDF
    /// accumulates clips as it goes, so an item inside two nested clips is inside both.
    /// </summary>
    public List<ClipSpec> Clips { get; } = new();

    /// <summary>
    /// Whether any clip restricts this item - its own, or one an ancestor carries.
    ///
    /// The documented meaning has always been "is this item restricted by a clip", and that used to be the same
    /// question as "does it carry one", because a clip was only ever written on the object it cut. A clip is
    /// now a **container**: the importer records it on the group holding what it clips, which is what the file
    /// means. Asking only an item's own list then answered "not clipped" for the most clipped object on the
    /// page.
    ///
    /// The same shape as <see cref="IsEffectivelyVisible"/>: a property of an item that an ancestor can decide.
    /// </summary>
    public bool IsClipped
    {
        get
        {
            for (LayerItem? item = this; item is not null; item = item.Container as LayerItem)
            {
                if (item.Clips.Count > 0)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Effective visibility: true only when this item AND every ancestor
    /// (groups, its layer, and the artboard) are visible. Hiding a parent
    /// therefore hides its whole subtree for display/selection/export without
    /// altering the children's own flags; showing the parent leaves hidden
    /// children hidden.
    /// </summary>
    public bool IsEffectivelyVisible()
    {
        if (!IsVisible)
        {
            return false;
        }

        IItemContainer? container = Container;
        while (container is not null)
        {
            switch (container)
            {
                case LayerItem ancestor:
                    if (!ancestor.IsVisible)
                    {
                        return false;
                    }

                    container = ancestor.Container;
                    break;
                case Layer layer:
                    return layer.IsEffectivelyVisible;
                default:
                    return true;
            }
        }

        return true;
    }

    /// <summary>
    /// The top-most layer that (transitively) contains this item. Returns null for
    /// items that are detached from any artboard. Used by selection, hit testing
    /// and the Layers panel.
    /// </summary>
    public Layer? OwningLayer()
    {
        for (IItemContainer? c = Container; c is not null; c = (c as LayerItem)?.Container)
        {
            if (c is Layer layer)
            {
                return layer;
            }
        }

        return null;
    }

    /// <summary>The owning artboard's origin (path/text coordinates are stored
    /// relative to it). Zero for items on the pasteboard.</summary>
    public Vector2D ArtboardOffset()
    {
        Artboard? artboard = OwningLayer()?.Artboard;
        return artboard is null ? default : new Vector2D(artboard.X, artboard.Y);
    }

    /// <summary>Deep copy of the item, detached from any container.</summary>
    public abstract LayerItem Clone();
}
