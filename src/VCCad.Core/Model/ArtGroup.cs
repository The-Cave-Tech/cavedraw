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

            box = box.IsEmpty ? childBox : box.Union(childBox);
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
