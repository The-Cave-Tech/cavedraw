using VCCad.Core.Model;
using VCCad.Geometry;

namespace VCCad.Core.Commands;

/// <summary>
/// Adds a fresh artboard to a document. Undo removes exactly that artboard.
/// </summary>
public sealed class AddArtboardCommand : IUndoableCommand
{
    private readonly CadDocument _document;
    private readonly Size2D _size;
    private readonly string? _name;
    private Artboard? _created;

    /// <summary>Command description shown in the undo menu.</summary>
    public string Description => "Add artboard";

    public AddArtboardCommand(CadDocument document, Size2D size, string? name = null)
    {
        _document = document;
        _size = size;
        _name = name;
    }

    /// <summary>Adds an already-constructed artboard (used when the caller wants to
    /// pre-populate layers before inserting).</summary>
    public AddArtboardCommand(CadDocument document, Artboard artboard)
    {
        _document = document;
        _size = artboard.Bounds.Size;
        _name = artboard.Name;
        _created = artboard;
    }

    public void Do()
    {
        // Re-running after an Undo must restore the *same* artboard instance so
        // that object identity (and any references held by scripts) survives.
        if (_created is null)
        {
            _created = _document.AddArtboard(_size, _name);
        }
        else
        {
            _document.AddArtboard(_created);
        }
    }

    public void Undo()
    {
        if (_created is not null)
        {
            _document.RemoveArtboard(_created);
        }
    }
}

/// <summary>
/// Adds a layer to an artboard. Undo removes exactly that layer.
/// </summary>
public sealed class AddLayerCommand : IUndoableCommand
{
    private readonly Artboard _artboard;
    private readonly string _layerName;
    private Layer? _created;

    public string Description => "Add layer";

    public AddLayerCommand(Artboard artboard, string? layerName = null)
    {
        _artboard = artboard;
        _layerName = layerName ?? "Layer";
    }

    public void Do()
    {
        if (_created is null)
        {
            _created = _artboard.AddLayer(_layerName);
        }
        else
        {
            // Re-run after an Undo: restore the *same* instance so identity holds.
            _artboard.AddLayer(_created);
        }
    }

    public void Undo()
    {
        if (_created is not null)
        {
            _artboard.RemoveLayer(_created);
        }
    }
}

/// <summary>
/// Adds a pre-constructed item (path or group) to a layer at a given z-index.
/// The inserted object is supplied by the caller, which lets the API pass any
/// node built by a <see cref="PathFactory"/> helper. Undo detaches it.
/// </summary>
public sealed class AddItemCommand : IUndoableCommand
{
    private readonly Layer _layer;
    private readonly LayerItem _item;
    private int _index; // captured during the first Do so Undo/Redo keep stacking order
    private bool _executedOnce;

    public string Description => $"Add {_item.Name}";

    public AddItemCommand(Layer layer, LayerItem item)
    {
        _layer = layer;
        _item = item;
    }

    public void Do()
    {
        if (_layer.Children.Contains(_item))
        {
            return; // already present (defensive; should not happen in normal flow)
        }

        _index = _executedOnce ? _index : _layer.Children.Count;
        _executedOnce = true;
        _layer.AddItem(_item, _index);
    }

    public void Undo()
    {
        _layer.RemoveItem(_item);
    }
}

/// <summary>
/// Replaces a path's fill and restores <paramref name="previous"/> on undo.
///
/// The previous value is supplied by the caller rather than read off the path when the command
/// first runs. The panels write the new colour onto the path while the person drags and build
/// the command afterwards, so a command that read the path would record the value it had just
/// set and Undo would put it straight back - a silent no-op.
/// </summary>
public sealed class SetFillCommand : IUndoableCommand
{
    private readonly PathItem _path;
    private readonly FillSpec _newFill;
    private readonly FillSpec _previous;

    public string Description => "Change fill";

    public SetFillCommand(PathItem path, FillSpec newFill, FillSpec previous)
    {
        _path = path;
        _newFill = newFill;
        _previous = previous;
    }

    public void Do() => _path.Fill = _newFill;

    public void Undo() => _path.Fill = _previous;
}

/// <summary>
/// Replaces a path's stroke. Mirrors <see cref="SetFillCommand"/>, including why the previous
/// value is the caller's to supply.
/// </summary>
public sealed class SetStrokeCommand : IUndoableCommand
{
    private readonly PathItem _path;
    private readonly StrokeSpec _newStroke;
    private readonly StrokeSpec _previous;

    public string Description => "Change stroke";

    public SetStrokeCommand(PathItem path, StrokeSpec newStroke, StrokeSpec previous)
    {
        _path = path;
        _newStroke = newStroke;
        _previous = previous;
    }

    public void Do() => _path.Stroke = _newStroke;

    public void Undo() => _path.Stroke = _previous;
}
