using VCCad.Geometry;

namespace VCCad.Core.Model;

/// <summary>
/// A group — an <see cref="IItemContainer"/> that is itself a <see cref="LayerItem"/>.
/// Groups give the user a single handle for transforming and styling many objects
/// at once (brief requirement 6). Nesting is unbounded.
///
/// A group applies an affine <see cref="Transform"/> to every descendant. Path
/// coordinates are stored artboard-local, so the transform is applied <em>on the
/// way out</em> (during render/export) rather than baked into child nodes. Baking
/// happens only when the user explicitly chooses "expand" — keeping transforms
/// live is what makes group editing reversible and lossless.
/// </summary>
public sealed class ArtGroup : LayerItem, IItemContainer
{
    private readonly List<LayerItem> _children = new();

    private AffineTransform _transform = AffineTransform.Identity;
    private double _opacity = 1.0;

    /// <inheritdoc/>
    public IReadOnlyList<LayerItem> Children => _children;

    /// <summary>
    /// Local→parent affine transform. Children are expressed in this group's local
    /// space; world space is reached by concatenating ancestor transforms.
    /// </summary>
    public AffineTransform Transform
    {
        get => _transform;
        set => SetField(ref _transform, value);
    }

    /// <summary>Group opacity in [0,1], multiplied with any layer and object opacity.</summary>
    public double Opacity
    {
        get => _opacity;
        set => SetField(ref _opacity, MathUtils.Clamp(value, 0.0, 1.0));
    }

    /// <summary>
    /// The id of the element this group is an **instance** of, or null when it is a group in its own right.
    ///
    /// SVG's `use` does not draw a copy of its target: it draws the target, and editing the target changes every
    /// instance of it. That is what the person who wrote the file meant, and flattening it into copies loses the
    /// meaning while looking identical in a single render.
    ///
    /// The children here are the definition's content as it stood when the file was read - so the geometry is
    /// right and the link is recorded - and a renderer that re-resolves the id draws the current definition
    /// instead. Keeping both is what makes the link survive a save and an edit rather than being a promise nothing
    /// can keep.
    /// </summary>
    public string? SourceId
    {
        get => _sourceId;
        set => SetField(ref _sourceId, value);
    }

    private string? _sourceId;

    /// <inheritdoc/>
    public event EventHandler? StructureChanged;

    /// <inheritdoc/>
    public int AddItem(LayerItem item, int? zIndex = null)
    {
        int index = ItemTree.Insert(item, this, _children, zIndex);
        StructureChanged?.Invoke(this, EventArgs.Empty);
        return index;
    }

    /// <inheritdoc/>
    public bool RemoveItem(LayerItem item)
    {
        bool removed = ItemTree.Remove(item, this, _children);
        if (removed)
        {
            StructureChanged?.Invoke(this, EventArgs.Empty);
        }

        return removed;
    }

    /// <summary>
    /// Bounding box of all descendants expressed in this group's <em>local</em>
    /// space (i.e. ignoring the group's own transform). Callers that need world
    /// bounds should transform the result by this group's world matrix.
    /// </summary>
    public Rect2D BoundingBox()
    {
        Rect2D box = Rect2D.Empty;
        foreach (LayerItem child in _children)
        {
            Rect2D childBox = child switch
            {
                PathItem path => path.BoundingBox(),
                ArtGroup inner => inner.Transform.Transform(inner.BoundingBox()),
                _ => Rect2D.Empty,
            };

            box = box.Union(childBox);
        }

        return box;
    }

    /// <inheritdoc/>
    public override LayerItem Clone()
    {
        var copy = new ArtGroup
        {
            Name = Name,
            IsVisible = IsVisible,
            IsLocked = IsLocked,
            Transform = _transform,
            Opacity = _opacity,

            // The link to the definition, or a duplicate of an instance would stop being an instance - the same
            // way a cloned path loses the rest of its stroke stack if the clone forgets to carry it.
            SourceId = _sourceId,
        };
        copy.AddRangeCloned(_children);
        return copy;
    }

    private void AddRangeCloned(IEnumerable<LayerItem> children)
    {
        foreach (LayerItem child in children)
        {
            AddItem(child.Clone());
        }
    }
}
