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

    /// <summary>True when this item (and any ancestor group) is visible.</summary>
    public bool IsEffectivelyVisible()
    {
        for (LayerItem? current = this; current is not null; current = (current.Container as LayerItem))
        {
            if (!current.IsVisible)
            {
                return false;
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
