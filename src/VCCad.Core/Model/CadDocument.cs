using VCCad.Geometry;

namespace VCCad.Core.Model;

/// <summary>
/// The root of a VCCad drawing: an ordered list of artboards (brief requirement 4).
///
/// <code>
/// CadDocument
///  └── Artboard[]            e.g. one A4 landscape by default
///       └── Layer[]          bottom-to-top
///            ├── ArtGroup    (affine transform; may nest)
///            └── PathItem    (subpaths + fill + stroke)
/// </code>
///
/// A freshly created document using <see cref="CreateDefault"/> matches the brief:
/// a single A4-landscape artboard with one unlocked, visible layer, ready for the
/// first pen stroke.
/// </summary>
public sealed class CadDocument
{
    private readonly List<Artboard> _artboards = new();

    /// <summary>Document-level container for objects that belong to no artboard
    /// (the pasteboard / orphans). These are "parentless" in the sense that no
    /// artboard owns them; their coordinates are document/world coordinates.</summary>
    public Layer Orphans { get; } = new() { Name = "Pasteboard" };

    private string _name = "Untitled";

    /// <summary>Document identity — the key used by the automation API and the
    /// artifact (PDF) naming. Persisted by the lossless serializer.</summary>
    public Guid Id { get; private set; } = Guid.NewGuid();

    /// <summary>Restores a persisted identity (deserialization only).</summary>
    internal void RestoreIdentity(Guid id) => Id = id;

    /// <summary>Document title (used as the default PDF/sidecar file base name).</summary>
    public string Name
    {
        get => _name;
        set => _name = value;
    }

    /// <summary>Artboards in document order; index 0 is the primary/first artboard.</summary>
    public IReadOnlyList<Artboard> Artboards => _artboards;

    /// <summary>Raised whenever the artboard list changes.</summary>
    public event EventHandler? StructureChanged;

    /// <summary>
    /// The factory used by "New document": A4 landscape artboard plus one default
    /// layer. A4 landscape is 297 × 210 mm, i.e. 841.89 × 595.28 pt.
    /// </summary>
    public static CadDocument CreateDefault(string? name = null)
    {
        var document = new CadDocument { Name = name ?? "Untitled" };
        Artboard artboard = document.AddArtboard(PageSizes.A4Landscape, "Artboard 1");
        artboard.AddLayer("Layer 1");
        return document;
    }

    /// <summary>Adds an artboard of the given size and returns it.</summary>
    public Artboard AddArtboard(Size2D size, string? name = null, Point2D origin = default)
    {
        var artboard = new Artboard(size, origin) { Name = name ?? $"Artboard {_artboards.Count + 1}" };
        AddArtboard(artboard);
        return artboard;
    }

    /// <summary>
    /// Re-inserts an existing artboard (used by undo/redo to restore object
    /// identity rather than cloning). No-op when the artboard is already present.
    /// </summary>
    public void AddArtboard(Artboard artboard)
    {
        if (_artboards.Contains(artboard))
        {
            return;
        }

        _artboards.Add(artboard);
        StructureChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The document's content origin, recomputed on the fly: the topmost/leftmost
    /// extent across artboards and every object. Parentless content (artboards and
    /// off-artboard objects) therefore defines the origin dynamically — as the
    /// extremes change, this property reflects it without mutating coordinates.
    /// </summary>
    public Point2D ContentOrigin()
    {
        double minX = double.PositiveInfinity;
        double minY = double.PositiveInfinity;
        bool any = false;

        void Include(double x, double y)
        {
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            any = true;
        }

        foreach (Artboard artboard in _artboards)
        {
            Include(artboard.X, artboard.Y);
            foreach (Layer layer in artboard.Layers)
            {
                foreach (LayerItem item in layer.Children)
                {
                    IncludeItem(item, Include);
                }
            }
        }

        return any ? new Point2D(minX, minY) : default;
    }

    private static void IncludeItem(LayerItem item, Action<double, double> include)
    {
        switch (item)
        {
            case PathItem path:
                Rect2D b = path.WorldBounds();
                if (!b.IsEmpty)
                {
                    include(b.Left, b.Top);
                }

                break;
            case ArtGroup group:
                foreach (LayerItem child in group.Children)
                {
                    IncludeItem(child, include);
                }

                break;
        }
    }

    /// <summary>Re-inserts an artboard at a specific index (undo/redo).</summary>
    public void InsertArtboard(Artboard artboard, int index)
    {
        if (_artboards.Contains(artboard))
        {
            return;
        }

        _artboards.Insert(Math.Clamp(index, 0, _artboards.Count), artboard);
        StructureChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Removes an artboard and all of its content.</summary>
    public bool RemoveArtboard(Artboard artboard)
    {
        bool removed = _artboards.Remove(artboard);
        if (removed)
        {
            StructureChanged?.Invoke(this, EventArgs.Empty);
        }

        return removed;
    }
}
