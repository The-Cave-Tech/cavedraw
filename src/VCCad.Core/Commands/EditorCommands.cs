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
/// Replaces a path's fill. Undo restores the previous fill, which is captured the
/// first time Do runs so the command remains correct across Do/Undo/Redo cycles.
/// </summary>
public sealed class SetFillCommand : IUndoableCommand
{
    private readonly PathItem _path;
    private readonly FillSpec _newFill;
    private FillSpec _previous = FillSpec.None;
    private bool _captured;

    public string Description => "Change fill";

    public SetFillCommand(PathItem path, FillSpec newFill)
    {
        _path = path;
        _newFill = newFill;
    }

    public void Do()
    {
        if (!_captured)
        {
            _previous = _path.Fill;
            _captured = true;
        }

        _path.Fill = _newFill;
    }

    public void Undo()
    {
        _path.Fill = _previous;
    }
}

/// <summary>
/// Replaces a path's stroke. Mirrors <see cref="SetFillCommand"/>.
/// </summary>
public sealed class SetStrokeCommand : IUndoableCommand
{
    private readonly PathItem _path;
    private readonly StrokeSpec _newStroke;
    private StrokeSpec _previous = StrokeSpec.None;
    private bool _captured;

    public string Description => "Change stroke";

    public SetStrokeCommand(PathItem path, StrokeSpec newStroke)
    {
        _path = path;
        _newStroke = newStroke;
    }

    public void Do()
    {
        if (!_captured)
        {
            _previous = _path.Stroke;
            _captured = true;
        }

        _path.Stroke = _newStroke;
    }

    public void Undo()
    {
        _path.Stroke = _previous;
    }
}
