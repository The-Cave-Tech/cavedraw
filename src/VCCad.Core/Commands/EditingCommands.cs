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
