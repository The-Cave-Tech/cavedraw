using VCCad.Core.Model;
using VCCad.Geometry;

namespace VCCad.Core.Commands;

/// <summary>
/// Replaces a path's geometry with an explicit "after" snapshot; Undo restores an
/// explicit "before" snapshot. This is the workhorse for pointer gestures that
/// mutate geometry live while dragging (the gesture keeps a before-snapshot taken
/// at press time and commits with this command on release).
///
/// Both snapshots are deep-copied on construction so later live mutations of the
/// path cannot invalidate the undo record.
/// </summary>
public sealed class GeometryReplaceCommand : IUndoableCommand
{
    private readonly PathItem _path;
    private readonly PathItem _before;
    private readonly PathItem _after;

    public string Description { get; }

    public GeometryReplaceCommand(PathItem path, PathItem before, PathItem after, string? description = null)
    {
        _path = path;
        _before = before.GeometrySnapshot();
        _after = after.GeometrySnapshot();
        Description = description ?? "Edit path";
    }

    public void Do() => _path.RestoreGeometryFrom(_after);

    public void Undo() => _path.RestoreGeometryFrom(_before);
}

/// <summary>
/// Changes an artboard's rectangle (position and/or size) as one undo step.
/// Used by the Artboard tool's move and resize gestures.
/// </summary>
public sealed class SetArtboardBoundsCommand : IUndoableCommand
{
    private readonly Artboard _artboard;
    private readonly Geometry.Rect2D _before;
    private readonly Geometry.Rect2D _after;

    public string Description { get; }

    public SetArtboardBoundsCommand(Artboard artboard, Geometry.Rect2D before, Geometry.Rect2D after,
        string? description = null)
    {
        _artboard = artboard;
        _before = before;
        _after = after;
        Description = description ?? "Edit artboard";
    }

    public void Do() => Apply(_after);

    public void Undo() => Apply(_before);

    private void Apply(Geometry.Rect2D rect)
    {
        _artboard.X = rect.X;
        _artboard.Y = rect.Y;
        _artboard.Width = rect.Width;
        _artboard.Height = rect.Height;
    }
}

/// <summary>
/// Groups a set of sibling items into a new <see cref="ArtGroup"/> (one undo
/// step). Undo dissolves the group and restores the original stacking order.
/// </summary>
public sealed class GroupItemsCommand : IUndoableCommand
{
    private readonly IItemContainer _container;
    private readonly List<LayerItem> _items;
    private readonly List<int> _originalIndices;
    private ArtGroup? _group;

    public string Description => "Group";

    public GroupItemsCommand(IItemContainer container, IEnumerable<LayerItem> items)
    {
        _container = container;
        _items = items.ToList();
        _originalIndices = _items.Select(IndexIn).ToList();
    }

    private int IndexIn(LayerItem item)
    {
        for (int i = 0; i < _container.Children.Count; i++)
        {
            if (ReferenceEquals(_container.Children[i], item))
            {
                return i;
            }
        }

        return _container.Children.Count;
    }

    public void Do()
    {
        _group ??= new ArtGroup { Name = "Group" };

        // Order items by their original stacking so the group preserves z-order.
        var ordered = _items
            .Select((item, i) => (item, index: _originalIndices[i]))
            .OrderBy(t => t.index)
            .Select(t => t.item)
            .ToList();

        int insertAt = ordered.Count > 0 ? _originalIndices[_items.IndexOf(ordered[0])] : 0;
        foreach (LayerItem item in ordered)
        {
            _container.RemoveItem(item);
        }

        foreach (LayerItem item in ordered)
        {
            _group.AddItem(item);
        }

        _container.AddItem(_group, Math.Clamp(insertAt, 0, _container.Children.Count));
    }

    public void Undo()
    {
        if (_group is null)
        {
            return;
        }

        foreach (LayerItem item in _group.Children.ToArray())
        {
            _group.RemoveItem(item);
        }

        _container.RemoveItem(_group);

        for (int i = 0; i < _items.Count; i++)
        {
            _container.AddItem(_items[i], Math.Clamp(_originalIndices[i], 0, _container.Children.Count));
        }
    }
}

/// <summary>
/// Dissolves a group, moving its children back into the parent container at the
/// group's position. Undo regroups them.
/// </summary>
public sealed class UngroupItemsCommand : IUndoableCommand
{
    private readonly ArtGroup _group;
    private IItemContainer? _container;
    private int _groupIndex;
    private LayerItem[] _children = Array.Empty<LayerItem>();

    public string Description => "Ungroup";

    public UngroupItemsCommand(ArtGroup group)
    {
        _group = group;
    }

    public void Do()
    {
        _container = _group.Container;
        if (_container is null)
        {
            return;
        }

        _groupIndex = IndexOf(_container, _group);
        _children = _group.Children.ToArray();
        _container.RemoveItem(_group);

        int at = Math.Clamp(_groupIndex, 0, _container.Children.Count);
        foreach (LayerItem child in _children)
        {
            _container.AddItem(child, at++);
        }
    }

    public void Undo()
    {
        if (_container is null)
        {
            return;
        }

        foreach (LayerItem child in _children)
        {
            _container.RemoveItem(child);
            _group.AddItem(child);
        }

        _container.AddItem(_group, Math.Clamp(_groupIndex, 0, _container.Children.Count));
    }

    private static int IndexOf(IItemContainer container, LayerItem item)
    {
        for (int i = 0; i < container.Children.Count; i++)
        {
            if (ReferenceEquals(container.Children[i], item))
            {
                return i;
            }
        }

        return 0;
    }
}

/// <summary>
/// Removes an item (path or group) from whatever container holds it. Undo
/// re-inserts the item at its original z-index.
/// </summary>
public sealed class RemoveItemCommand : IUndoableCommand
{
    private readonly LayerItem _item;
    private IItemContainer? _container;
    private int _index;

    public string Description => $"Delete {_item.Name}";

    public RemoveItemCommand(LayerItem item)
    {
        _item = item;
    }

    public void Do()
    {
        _container = _item.Container;
        if (_container is null)
        {
            return; // already detached
        }

        int index = -1;
        for (int i = 0; i < _container.Children.Count; i++)
        {
            if (ReferenceEquals(_container.Children[i], _item))
            {
                index = i;
                break;
            }
        }

        _index = Math.Max(0, index);
        _container.RemoveItem(_item);
    }

    public void Undo()
    {
        if (_container is not null)
        {
            _container.AddItem(_item, _index);
        }
    }
}
