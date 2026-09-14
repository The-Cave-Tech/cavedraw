using VCCad.Geometry;

namespace VCCad.Core.Model;

/// <summary>
/// An artboard — a named, rectangular working area. Artboards are the top-level
/// organisational unit of a document (brief requirement 4): every object lives on
/// exactly one artboard, beneath one of that artboard's layers.
///
/// The artboard rectangle is stored in points, in the document coordinate space.
/// The default artboard is A4 landscape (841.89 × 595.28 pt) as required.
/// </summary>
public sealed class Artboard : CadObject
{
    private readonly List<Layer> _layers = new();

    private double _x;
    private double _y;
    private double _width;
    private double _height;

    /// <summary>
    /// Creates an artboard sized <paramref name="size"/> points, positioned so its
    /// top-left corner sits at (0,0) unless a non-zero <paramref name="origin"/>
    /// is supplied.
    /// </summary>
    public Artboard(Size2D size, Point2D origin = default)
    {
        _x = origin.X;
        _y = origin.Y;
        _width = size.Width;
        _height = size.Height;
    }

    /// <summary>Top-left X of the artboard in document points.</summary>
    public double X
    {
        get => _x;
        set => SetField(ref _x, value);
    }

    /// <summary>Top-left Y of the artboard in document points (Y grows downward).</summary>
    public double Y
    {
        get => _y;
        set => SetField(ref _y, value);
    }

    /// <summary>Width in points.</summary>
    public double Width
    {
        get => _width;
        set => SetField(ref _width, value);
    }

    /// <summary>Height in points.</summary>
    public double Height
    {
        get => _height;
        set => SetField(ref _height, value);
    }

    /// <summary>The artboard rectangle in document space.</summary>
    public Rect2D Bounds => new(X, Y, Width, Height);

    private bool _isVisible = true;

    /// <summary>Whether the artboard (and everything on it) is displayed.</summary>
    public bool IsVisible
    {
        get => _isVisible;
        set => SetField(ref _isVisible, value);
    }

    /// <summary>Layers bottom-to-top: index 0 paints below index 1.</summary>
    public IReadOnlyList<Layer> Layers => _layers;

    /// <summary>Raised when the layer list changes.</summary>
    public event EventHandler? StructureChanged;

    /// <summary>Creates a layer and adds it above the existing ones.</summary>
    public Layer AddLayer(string? name = null)
    {
        var layer = new Layer { Name = name ?? $"Layer {_layers.Count + 1}" };
        AddLayer(layer);
        return layer;
    }

    /// <summary>Re-inserts an existing layer instance (undo/redo and deserialization
    /// restore identity through this overload).</summary>
    public void AddLayer(Layer layer)
    {
        if (_layers.Contains(layer))
        {
            return;
        }

        layer.Artboard = this;
        _layers.Add(layer);
        StructureChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Removes a layer (and everything beneath it). Returns false if absent.</summary>
    public bool RemoveLayer(Layer layer)
    {
        bool removed = _layers.Remove(layer);
        if (removed)
        {
            layer.Artboard = null;
            StructureChanged?.Invoke(this, EventArgs.Empty);
        }

        return removed;
    }

    /// <summary>
    /// The union of every layer's object bounds — i.e. the artwork extent on this
    /// artboard. Empty artwork yields an empty box; the pasteboard controller adds
    /// its own one-viewport margin around this (brief requirement 1).
    /// </summary>
    public Rect2D ArtworkBounds()
    {
        Rect2D box = Rect2D.Empty;
        foreach (Layer layer in _layers)
        {
            foreach (LayerItem item in layer.Children)
            {
                Rect2D childBox = item switch
                {
                    PathItem path => path.BoundingBox(),
                    ArtGroup group => group.Transform.Transform(group.BoundingBox()),
                    _ => Rect2D.Empty,
                };

                box = box.Union(childBox);
            }
        }

        return box;
    }
}
