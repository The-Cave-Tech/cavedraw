using VCCad.Core.Model;
using VCCad.Core.Selection;
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

    // The shape definition travels with the geometry. A snapshot is geometry only, so without these
    // an undone shape edit would leave the parameters describing a shape the outline no longer is -
    // and the handles would then move a star that is not the star on screen.
    private readonly ShapeDefinition? _beforeShape;
    private readonly ShapeDefinition? _afterShape;

    public string Description { get; }

    public GeometryReplaceCommand(PathItem path, PathItem before, PathItem after, string? description = null)
    {
        _path = path;
        _before = before.GeometrySnapshot();
        _after = after.GeometrySnapshot();
        _beforeShape = before.Shape;
        _afterShape = after.Shape;
        Description = description ?? "Edit path";
    }

    public void Do()
    {
        _path.RestoreGeometryFrom(_after);
        _path.Shape = _afterShape;
    }

    public void Undo()
    {
        _path.RestoreGeometryFrom(_before);
        _path.Shape = _beforeShape;
    }
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

    /// <summary>The group created by <see cref="Do"/> (available after execution).</summary>
    public ArtGroup? Group => _group;

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

/// <summary>
/// Deletes an artboard. When <c>keepChildren</c> is true its objects are moved to
/// the document's orphan/pasteboard layer at their current world position;
/// otherwise the objects are deleted with it. Undo restores everything.
/// </summary>
public sealed class DeleteArtboardCommand : IUndoableCommand
{
    private readonly CadDocument _document;
    private readonly Artboard _artboard;
    private readonly bool _keepChildren;
    private bool _captured;
    private int _artboardIndex;
    private Vector2D _offset;
    private readonly List<(Layer Layer, int Index, LayerItem Item)> _direct = new();

    public string Description => "Delete artboard";

    public DeleteArtboardCommand(CadDocument document, Artboard artboard, bool keepChildren)
    {
        _document = document;
        _artboard = artboard;
        _keepChildren = keepChildren;
    }

    public void Do()
    {
        if (!_captured)
        {
            _artboardIndex = _document.Artboards.ToList().IndexOf(_artboard);
            _offset = new Vector2D(_artboard.X, _artboard.Y);
            foreach (Layer layer in _artboard.Layers)
            {
                for (int i = 0; i < layer.Children.Count; i++)
                {
                    _direct.Add((layer, i, layer.Children[i]));
                }
            }

            _captured = true;
        }

        foreach ((Layer layer, _, LayerItem item) in _direct)
        {
            layer.RemoveItem(item);
            if (_keepChildren)
            {
                FrameMove.TranslateDescendants(item, _offset);
                _document.Orphans.AddItem(item);
            }
        }

        _document.RemoveArtboard(_artboard);
    }

    public void Undo()
    {
        foreach ((Layer layer, int index, LayerItem item) in _direct)
        {
            if (_keepChildren)
            {
                _document.Orphans.RemoveItem(item);
                FrameMove.TranslateDescendants(item, _offset.Negated);
            }

            layer.AddItem(item, index);
        }

        _document.InsertArtboard(_artboard, _artboardIndex);
    }
}

/// <summary>
/// The frame arithmetic every command that moves art between containers shares.
///
/// `DeleteArtboardCommand` (a page's objects to the pasteboard) and `ReparentItemsCommand` (the
/// pasteboard's objects onto a page) both compensate the frame change by translating the stored geometry,
/// and the displacement they state is a **world** one - the artboard origin, gained or lost. The geometry
/// is stored in the frame the item's own groups establish, so the displacement is carried across by
/// <see cref="SelectionEngine.DeltaInItem"/> rather than added raw. Added raw it moved a grouped object by
/// the group transform applied to the artboard origin, so a page's grouped art jumped when the page was
/// deleted or reparented (#173).
///
/// A displacement and not a point: the linear part of a frame cancels for a translation, which is what
/// makes this the same conversion the canvas gestures and the API translations use rather than a second
/// rule.
/// </summary>
internal static class FrameMove
{
    public static void TranslateDescendants(LayerItem item, Vector2D delta)
    {
        switch (item)
        {
            case PathItem path:
                path.TranslateGeometryBy(SelectionEngine.DeltaInItem(path, delta));
                break;
            case ArtGroup group:
                foreach (LayerItem child in group.Children)
                {
                    TranslateDescendants(child, delta);
                }

                break;
        }
    }
}

/// <summary>
/// Moves a set of items from one container to another, translating their geometry
/// by <paramref name="deltaAtDo"/> (computed from the target artboard at Do time).
/// Used to reparent orphan objects that fall inside an artboard.
/// </summary>
public sealed class ReparentItemsCommand : IUndoableCommand
{
    private readonly CadDocument _document;
    private readonly Artboard _targetArtboard;
    private readonly IReadOnlyList<LayerItem> _items;
    private readonly IItemContainer _from;
    private Vector2D _delta;
    private IItemContainer? _to;

    public string Description => "Reparent objects";

    public ReparentItemsCommand(CadDocument document, Artboard targetArtboard,
        IReadOnlyList<LayerItem> items, IItemContainer from)
    {
        _document = document;
        _targetArtboard = targetArtboard;
        _items = items;
        _from = from;
    }

    public void Do()
    {
        _to = _targetArtboard.Layers.Count > 0
            ? _targetArtboard.Layers[^1]
            : _targetArtboard.AddLayer("Layer 1");

        // World → artboard-local translation for the items being reparented.
        _delta = new Vector2D(-_targetArtboard.X, -_targetArtboard.Y);

        foreach (LayerItem item in _items)
        {
            _from.RemoveItem(item);
            FrameMove.TranslateDescendants(item, _delta);
            _to.AddItem(item);
        }
    }

    public void Undo()
    {
        if (_to is null)
        {
            return;
        }

        foreach (LayerItem item in _items)
        {
            _to.RemoveItem(item);
            FrameMove.TranslateDescendants(item, _delta.Negated);
            _from.AddItem(item);
        }
    }
}

/// <summary>
/// Joins two open paths that share an endpoint into one (mutating the first and
/// removing the second). If the result's ends meet, it is closed.
/// </summary>
public sealed class JoinPathsCommand : IUndoableCommand
{
    private readonly PathItem _a;
    private readonly PathItem _b;
    private IItemContainer? _container;
    private int _index;
    private PathItem? _before;

    public string Description => "Join paths";

    public JoinPathsCommand(PathItem a, PathItem b)
    {
        _a = a;
        _b = b;
    }

    public void Do()
    {
        _before = _a.GeometrySnapshot();
        _container = _b.Container;
        _index = IndexOf(_container, _b);
        if (PathJoin.Join(_a, _b))
        {
            _container?.RemoveItem(_b);
        }
    }

    public void Undo()
    {
        if (_before is not null)
        {
            _a.RestoreGeometryFrom(_before);
        }

        _container?.AddItem(_b, _index);
    }

    private static int IndexOf(IItemContainer? container, LayerItem item)
    {
        if (container is null)
        {
            return 0;
        }

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

/// <summary>Replaces a text item's state (runs/origin/colour) as one undo step.</summary>
public sealed class ReplaceTextCommand : IUndoableCommand
{
    private readonly TextItem _item;
    private readonly TextItem _before;
    private readonly TextItem _after;

    public string Description { get; }

    public ReplaceTextCommand(TextItem item, TextItem before, TextItem after, string? description = null)
    {
        _item = item;
        _before = before.Clone() as TextItem ?? new TextItem();
        _after = after.Clone() as TextItem ?? new TextItem();
        Description = description ?? "Edit text";
    }

    public void Do() => _item.CopyFrom(_after);

    public void Undo() => _item.CopyFrom(_before);
}

/// <summary>
/// Applies paragraph style and orientation to text objects as one undo step. These
/// change how a block is set — leading, space between paragraphs, the angle it sits at,
/// the width it wraps in — and never what it says.
/// </summary>
public sealed class TextStyleCommand : IUndoableCommand
{
    private readonly List<(TextItem Item, TextItem Before, TextItem After)> _changes = new();

    public string Description { get; }

    public TextStyleCommand(IEnumerable<TextItem> items, double? lineSpacing = null,
        double? paragraphSpacing = null, double? rotationDegrees = null,
        double? frameWidth = null, TextAlignment? alignment = null,
        TextWritingMode? writingMode = null, TextDirection? direction = null)
    {
        Description = "Text style";
        foreach (TextItem item in items)
        {
            var before = (TextItem)item.Clone();
            var after = (TextItem)item.Clone();

            if (lineSpacing is { } ls)
            {
                after.LineSpacing = Math.Clamp(ls, 0.5, 4.0);
            }

            if (paragraphSpacing is { } ps)
            {
                after.ParagraphSpacing = Math.Clamp(ps, 0, 400);
            }

            if (rotationDegrees is { } rd)
            {
                after.RotationRadians = rd * Math.PI / 180.0;
            }

            if (frameWidth is { } fw)
            {
                after.FrameWidth = Math.Max(0, fw);
            }

            if (alignment is { } al)
            {
                after.Alignment = al;
            }

            // The block's own axes, which are part of how it is set rather than what it says: a horizontal block and
            // a vertical one hold the same run and draw it down the page or along it. They belong in this command and
            // not in a text-replacement one for the same reason the leading does - the words are untouched, so the
            // undo step has to say "how it is set" rather than "what it says".
            if (writingMode is { } mode)
            {
                after.WritingMode = mode;
            }

            if (direction is { } dir)
            {
                after.Direction = dir;
            }

            _changes.Add((item, before, after));
        }
    }

    public TextStyleCommand(TextItem item, double? lineSpacing = null,
        double? paragraphSpacing = null, double? rotationDegrees = null,
        double? frameWidth = null, TextAlignment? alignment = null,
        TextWritingMode? writingMode = null, TextDirection? direction = null)
        : this(new[] { item }, lineSpacing, paragraphSpacing, rotationDegrees, frameWidth, alignment,
            writingMode, direction)
    {
    }

    public void Do()
    {
        foreach ((TextItem item, _, TextItem after) in _changes)
        {
            item.CopyFrom(after);
        }
    }

    public void Undo()
    {
        foreach ((TextItem item, TextItem before, _) in _changes)
        {
            item.CopyFrom(before);
        }
    }
}

/// <summary>
/// Moves and resizes an image's placement as one undo step.
///
/// An image carries a placement box rather than geometry, so it needs its own command;
/// the geometry-replace commands used for paths have nothing to snapshot.
/// </summary>
public sealed class ImagePlacementCommand : IUndoableCommand
{
    private readonly ImageItem _image;
    private readonly Rect2D _before;
    private readonly Rect2D _after;

    public string Description { get; }

    public ImagePlacementCommand(ImageItem image, Rect2D before, Rect2D after,
        string? description = null)
    {
        _image = image;
        _before = before;
        _after = after;
        Description = description ?? "Move image";
    }

    public void Do() => _image.Placement = _after;

    public void Undo() => _image.Placement = _before;
}

/// <summary>
/// Mirrors an image without touching its samples: the flip is state the renderer maps through, so
/// the file's own bytes stay the file's own bytes and the flip is undoable on its own.
/// </summary>
public sealed class ImageFlipCommand : IUndoableCommand
{
    private readonly ImageItem _image;
    private readonly bool _beforeX;
    private readonly bool _beforeY;
    private readonly bool _afterX;
    private readonly bool _afterY;

    public string Description => "Flip image";

    public ImageFlipCommand(ImageItem image, bool afterX, bool afterY)
    {
        _image = image;
        _beforeX = image.MirrorX;
        _beforeY = image.MirrorY;
        _afterX = afterX;
        _afterY = afterY;
    }

    public void Do()
    {
        _image.MirrorX = _afterX;
        _image.MirrorY = _afterY;
    }

    public void Undo()
    {
        _image.MirrorX = _beforeX;
        _image.MirrorY = _beforeY;
    }
}

/// <summary>Moves a text item's origin as one undo step.</summary>
public sealed class SetTextOriginCommand : IUndoableCommand
{
    private readonly TextItem _item;
    private readonly Geometry.Point2D _before;
    private readonly Geometry.Point2D _after;

    public string Description => "Move text";

    public SetTextOriginCommand(TextItem item, Geometry.Point2D before, Geometry.Point2D after)
    {
        _item = item;
        _before = before;
        _after = after;
    }

    public void Do() => _item.Origin = _after;

    public void Undo() => _item.Origin = _before;
}

/// <summary>
/// Moves one or more items to a new position — optionally a different container —
/// changing stacking order and/or parenting as one undo step. Original
/// container/index pairs are captured for the inverse.
///
/// **A reparent is a frame change, so the geometry is converted into the destination container's frame**
/// (#174). The numbers an item stores mean nothing until the frame they are written in is named: a path
/// inside `translate(150,80) scale(2)` holds coordinates in that group's space, and dropping it on a layer
/// without converting moved it 150,80 across the page and doubled the error the other way round. The
/// conversion is the composition the repository states once - `CadDocument.ToWorld` and
/// <see cref="SelectionEngine.FromWorld"/> - mapped through
/// `ToWorld(destination) ∘ FromWorld(source)`, so where the art is drawn does not change.
///
/// An item whose own members cannot express that transform - a text block holds an origin and an angle, a
/// placed image holds a rectangle - is **refused** rather than stored in the wrong frame, and so is a
/// destination whose frame collapses the plane, where no honest world position exists. That is the rule
/// behind #140, #143, #144, #150, #151, #152, #155, #158, #160, #162, #164, #165, #169, #172 and #173, and
/// it is applied here rather than a second rule being invented for this path.
/// </summary>
public sealed class MoveItemsCommand : IUndoableCommand
{
    /// <summary>How far two affines may differ and still be the same map, component by component.</summary>
    private const double FrameTolerance = 1e-9;

    private readonly IReadOnlyList<LayerItem> _items;
    private readonly IItemContainer _target;
    private readonly int _index;
    private List<Move>? _before;
    private List<AffineTransform?>? _planned;

    /// <summary>
    /// Everything an undo needs about one item's move: where it was, the geometry it had, and - for a group,
    /// whose conversion lives in its own transform rather than in any node - the transform it had.
    /// </summary>
    private readonly record struct Move(
        IItemContainer Container, int Index, LayerItem Item, PathItem? Geometry, AffineTransform? Transform);

    public string Description => "Reorder objects";

    public MoveItemsCommand(IEnumerable<LayerItem> items, IItemContainer target, int index)
    {
        _items = items.ToList();
        _target = target;
        _index = index;
    }

    public void Do()
    {
        if (_planned is null)
        {
            _planned = Plan();
        }

        if (_before is null)
        {
            _before = new List<Move>();
            foreach (LayerItem item in _items)
            {
                if (item.Container is { } container)
                {
                    _before.Add(new Move(
                        container,
                        IndexOf(container, item),
                        item,
                        item is PathItem path ? path.GeometrySnapshot() : null,
                        item is ArtGroup group ? group.Transform : null));
                }
            }
        }

        foreach (LayerItem item in _items)
        {
            item.Container?.RemoveItem(item);
        }

        int index = Math.Clamp(_index, 0, _target.Children.Count);
        for (int i = 0; i < _items.Count; i++)
        {
            LayerItem item = _items[i];

            // The item's own frame, including the artboard origin it is moving between: the same conversion
            // a gesture makes, for the same reason. A path carries the whole mapping; text and images can
            // only carry a translation, which `Plan` has already refused anything else for.
            if (_planned[i] is { } local)
            {
                switch (item)
                {
                    case PathItem path:
                        path.TransformGeometry(local);
                        break;
                    case TextItem text:
                        text.Origin = local.Transform(text.Origin);
                        break;
                    case ImageItem image:
                        image.Placement = local.Transform(image.Placement);
                        break;
                    case ArtGroup group:
                        group.Transform = local.Compose(group.Transform);
                        break;
                }
            }

            _target.AddItem(item, index++);
        }
    }

    public void Undo()
    {
        if (_before is null)
        {
            return;
        }

        foreach (LayerItem item in _items)
        {
            item.Container?.RemoveItem(item);
        }

        foreach (Move move in _before)
        {
            if (move.Item is PathItem path && move.Geometry is not null)
            {
                path.RestoreGeometryFrom(move.Geometry);
            }

            // A group's conversion is its own transform, so putting it back is part of the inverse rather
            // than something the container can restore.
            if (move.Item is ArtGroup group && move.Transform is { } transform)
            {
                group.Transform = transform;
            }

            move.Container.AddItem(move.Item, Math.Clamp(move.Index, 0, move.Container.Children.Count));
        }
    }

    /// <summary>
    /// Works out, before anything is touched, where each item's geometry has to be written once it has
    /// moved: the affine taking the source container's frame to the destination's, and how that item
    /// expresses it.
    ///
    /// Planned as a whole rather than converted as the moves happen, because the second item of three
    /// failing halfway through would leave a document with some art converted and some not - and the stack
    /// has not recorded the command yet, so an undo could not put it back.
    /// </summary>
    private List<AffineTransform?> Plan()
    {
        var plan = new List<AffineTransform?>();

        foreach (LayerItem item in _items)
        {
            if (item.Container is null)
            {
                plan.Add(null);
                continue;
            }

            CadDocument? document = item.Document ?? (_target as CadObject)?.Document;

            // The frame the geometry is written in now, and the frame it is going to. Both are asked for
            // without moving anything, and both are the frame the item's **own members** are written in -
            // which is the container's frame. For a group that distinction is everything: its own transform
            // is one of the members being converted, so the frame handed to it has to stop at its container
            // or the transform would be conjugated twice.
            AffineTransform source = document is null
                ? AffineTransform.Identity
                : document.ToWorld(item, item.Container);
            AffineTransform destination = document is null
                ? AffineTransform.Identity
                : document.ToWorld(item, _target);

            if (item is ArtGroup)
            {
                // Its own transform is not part of the frame its own members are written in.
                source = document?.Ancestors(item.Container) ?? AffineTransform.Identity;
                destination = document?.Ancestors(_target) ?? AffineTransform.Identity;
            }

            if (!source.IsInvertible)
            {
                throw new FrameConversionException(
                    $"Cannot move '{item.Name}' (a {item.GetType().Name}): the frame it is stored in " +
                    "collapses the plane, so there is no world position to carry across.");
            }

            if (!destination.IsInvertible)
            {
                throw new FrameConversionException(
                    $"Cannot move '{item.Name}' (a {item.GetType().Name}) into " +
                    $"'{(_target as CadObject)?.Name ?? "the target"}': that frame is not invertible, so " +
                    "there is no honest position for the geometry and it will not be stored in the wrong frame.");
            }

            // From the source frame, through world, into the destination frame.
            AffineTransform relative = destination.Inverted().Compose(source);

            // A path carries the mapping in its nodes and a group carries it in its own transform - the
            // transform that establishes its children's frame and so may be any affine at all.
            if (item is PathItem || item is ArtGroup)
            {
                plan.Add(relative);
                continue;
            }

            // Text holds an origin and an angle and an image holds a rectangle: neither can hold a shear or
            // an anisotropic scale, so the only mapping they can carry exactly is a translation. A uniform
            // scale is representable for text (it is the font size) but is deliberately not attempted here -
            // the operation that changes a text block's size is the one that also changes its runs.
            if (IsTranslation(relative))
            {
                plan.Add(relative);
                continue;
            }

            throw new FrameConversionException(
                $"Cannot move '{item.Name}' (a {item.GetType().Name}) into " +
                $"'{(_target as CadObject)?.Name ?? "the target"}': that frame scales or turns what is " +
                "stored in it, and a text block or a placed image cannot express that mapping - so the " +
                "move is refused rather than storing numbers in a frame they were never written in.");
        }

        return plan;
    }

    /// <summary>Whether an affine is the identity in its linear part, so it displaces without distorting.</summary>
    private static bool IsTranslation(AffineTransform transform)
        => Math.Abs(transform.A - 1) <= FrameTolerance &&
           Math.Abs(transform.B) <= FrameTolerance &&
           Math.Abs(transform.C) <= FrameTolerance &&
           Math.Abs(transform.D - 1) <= FrameTolerance;

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
