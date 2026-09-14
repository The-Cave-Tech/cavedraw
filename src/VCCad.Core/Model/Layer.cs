using VCCad.Geometry;

namespace VCCad.Core.Model;

/// <summary>
/// A layer — the direct subdivision of an artboard, beneath groups and paths in
/// the document hierarchy (brief requirement 5). Each artboard owns its own layer
/// stack; a document with three artboards therefore has three independent stacks.
///
/// Layering semantics follow Illustrator: <em>within</em> a container, children at
/// a higher list index are painted above lower ones. So index 0 is the bottom of
/// that layer.
/// </summary>
public sealed class Layer : CadObject, IItemContainer
{
    private readonly List<LayerItem> _items = new();

    private bool _isVisible = true;
    private bool _isLocked;

    /// <inheritdoc/>
    public IReadOnlyList<LayerItem> Children => _items;

    /// <summary>Artboard-level visibility switch (overrides every child).</summary>
    public bool IsVisible
    {
        get => _isVisible;
        set => SetField(ref _isVisible, value);
    }

    /// <summary>Artboard-level lock (guards every child from editing).</summary>
    public bool IsLocked
    {
        get => _isLocked;
        set => SetField(ref _isLocked, value);
    }

    /// <summary>Group opacity multiplier applied to all children during render.</summary>
    public double Opacity { get; set; } = 1.0;

    /// <summary>Layer visibility combined with its artboard's visibility.</summary>
    public bool IsEffectivelyVisible => IsVisible && (Artboard?.IsVisible ?? true);

    /// <summary>The artboard that owns this layer. Object coordinates are stored
    /// relative to the artboard's top-left; the artboard origin is added when
    /// mapping to document/world space. Maintained by <see cref="Artboard"/>.</summary>
    public Artboard? Artboard { get; internal set; }

    /// <inheritdoc/>
    public event EventHandler? StructureChanged;

    /// <inheritdoc/>
    public int AddItem(LayerItem item, int? zIndex = null)
    {
        int index = ItemTree.Insert(item, this, _items, zIndex);
        StructureChanged?.Invoke(this, EventArgs.Empty);
        return index;
    }

    /// <inheritdoc/>
    public bool RemoveItem(LayerItem item)
    {
        bool removed = ItemTree.Remove(item, this, _items);
        if (removed)
        {
            StructureChanged?.Invoke(this, EventArgs.Empty);
        }

        return removed;
    }

    /// <summary>Convenience wrapper: adds an empty, visible group on top.</summary>
    public ArtGroup AddGroup(string name)
    {
        var group = new ArtGroup { Name = name };
        AddItem(group);
        return group;
    }
}
