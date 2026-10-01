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
    private readonly List<WidthProfileSpec> _widthProfiles = new();
    private readonly List<FilterSpec> _filters = new();

    /// <summary>
    /// Root-level elements the model has no meaning for, kept verbatim as XML.
    ///
    /// `sodipodi:namedview` holds the grid, the zoom and the page settings; `&lt;metadata&gt;` holds the RDF.
    /// Neither is artwork and neither is ours to interpret - but a file that loses its named view comes back to
    /// Inkscape with the document's own settings reset, which is a silent rewrite of somebody's file.
    ///
    /// Stored as text for the same reason an item's foreign attributes are: the model carries them, and does not
    /// pretend to understand them.
    /// </summary>
    public IReadOnlyList<string> SvgExtras => _svgExtras;

    private readonly List<string> _svgExtras = new();

    /// <summary>Replaces the preserved root-level elements.</summary>
    public void SetSvgExtras(IEnumerable<string> extras)
    {
        _svgExtras.Clear();
        _svgExtras.AddRange(extras);
    }

    /// <summary>
    /// The namespace prefixes the file declared, prefix to URI, excluding the default namespace.
    ///
    /// Carried so that what is written back uses the **same prefixes** the file used. The namespace is what matters
    /// semantically - `p1:label` and `inkscape:label` are the same attribute to a parser - but a file that comes
    /// back with machine-generated prefixes is not the file that went in, and a person reading it would reasonably
    /// call that a rewrite.
    /// </summary>
    public IReadOnlyDictionary<string, string> SvgNamespaces => _svgNamespaces;

    private readonly Dictionary<string, string> _svgNamespaces = new(StringComparer.Ordinal);

    /// <summary>Replaces the declared namespace prefixes.</summary>
    public void SetSvgNamespaces(IEnumerable<KeyValuePair<string, string>> namespaces)
    {
        _svgNamespaces.Clear();
        foreach (KeyValuePair<string, string> entry in namespaces)
        {
            if (entry.Key.Length > 0)
            {
                _svgNamespaces[entry.Key] = entry.Value;
            }
        }
    }

    /// <summary>
    /// Foreign-namespace **definitions** the file carried that nothing in the document points at, kept verbatim as
    /// XML.
    ///
    /// This is the second half of the SVG baggage problem, and the one issue #155 names. A foreign element that
    /// something refers to - an Inkscape live path effect a path names by id - travels on the item that refers to
    /// it, because that item is what can find it and what an export reaches it from. An element *nothing* refers to
    /// has no such item: it says nothing about how anything is drawn, so the reader hangs it on no path, and before
    /// this member it was dropped in silence. An Inkscape file that keeps a library of named path effects is the
    /// case that hurts: an effect applied to nothing today is one somebody applies tomorrow, and losing it is a
    /// silent rewrite of their file.
    ///
    /// It lives here rather than on a path because only the **document** can hold something nothing points at, and
    /// the writer puts it back into <c>defs</c> beside the referenced definitions, which is where the file had it
    /// and where the next reader looks for it. Nothing is ever invented onto an item to give one of these a home:
    /// a path that did not carry an effect must not gain one.
    ///
    /// Stored as text for the same reason an item's foreign elements are: the model carries it and does not pretend
    /// to understand it.
    /// </summary>
    public IReadOnlyList<string> ForeignPathEffects => _foreignPathEffects;

    private readonly List<string> _foreignPathEffects = new();

    /// <summary>Replaces the unreferenced foreign definitions the document keeps.</summary>
    public void SetForeignPathEffects(IEnumerable<string> effects)
    {
        _foreignPathEffects.Clear();
        _foreignPathEffects.AddRange(effects);
    }

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

    /// <summary>
    /// The document's reusable width profiles, in the order they were created.
    ///
    /// A profile is an asset like a colour, and a stroke **refers to it by name**: editing the asset is meant to
    /// change every stroke that names it, which is the difference between a reusable profile and a copied one.
    /// The link is the name rather than an id because a name is what a person sees and picks, and it is what the
    /// operations take.
    ///
    /// The consequence is that a profile is never edited on its own: renaming one has to travel with the strokes
    /// that used the old name, and deleting one has to clear them. That is why every edit here goes through
    /// <see cref="Commands.EditWidthProfilesCommand"/>, which captures both halves and undoes both together.
    /// </summary>
    public IReadOnlyList<WidthProfileSpec> WidthProfiles => _widthProfiles;

    /// <summary>The profile with this name, or null. Names are matched exactly and case-sensitively.</summary>
    public WidthProfileSpec? FindProfile(string name)
        => _widthProfiles.FirstOrDefault(p => p.Name == name);

    /// <summary>
    /// The document's filters, in the order they were created.
    ///
    /// Filters are **document state**, like a width profile: an element refers to one by name, and a filter that did
    /// not travel with the file would leave every shape that used it unpainted on the machine that opened it.
    /// </summary>
    public IReadOnlyList<FilterSpec> Filters => _filters;

    /// <summary>The filter with this name, or null.</summary>
    public FilterSpec? FindFilter(string name)
        => _filters.FirstOrDefault(f => f.Name == name);

    /// <summary>Adds a filter, or replaces the one with that name.</summary>
    public FilterSpec AddFilter(FilterSpec filter)
    {
        int existing = _filters.FindIndex(f => f.Name == filter.Name);
        if (existing >= 0)
        {
            _filters[existing] = filter;
        }
        else
        {
            _filters.Add(filter);
        }

        return filter;
    }

    /// <summary>Removes a filter, reporting whether it was there.</summary>
    public bool RemoveFilter(string name) => _filters.RemoveAll(f => f.Name == name) > 0;

    /// <summary>Replaces the filter library wholesale; deserialization and the edit command use this.</summary>
    internal void SetFilters(IEnumerable<FilterSpec> filters)
    {
        _filters.Clear();
        _filters.AddRange(filters);
    }

    /// <summary>
    /// The items that refer to a filter the document does not have.
    ///
    /// A reported condition rather than a silent default, for the same reason a missing width profile is: an element
    /// asking for a filter that is not there is a document that has been merged or edited in a way that lost an
    /// asset, and drawing it unfiltered quietly would make that look like a design decision.
    /// </summary>
    public IEnumerable<(LayerItem Item, string Name)> MissingFilters()
    {
        foreach (LayerItem item in AllItems())
        {
            if (item.FilterId is { Length: > 0 } name && FindFilter(name) is null)
            {
                yield return (item, name);
            }
        }
    }

    /// <summary>Adds a profile to the library, or replaces the one with that name. Returns the profile added.</summary>
    public WidthProfileSpec AddWidthProfile(WidthProfileSpec profile)
    {
        int existing = _widthProfiles.FindIndex(p => p.Name == profile.Name);
        if (existing >= 0)
        {
            _widthProfiles[existing] = profile;
        }
        else
        {
            _widthProfiles.Add(profile);
        }

        return profile;
    }

    /// <summary>Removes a profile from the library, reporting whether it was there.</summary>
    public bool RemoveWidthProfile(string name)
        => _widthProfiles.RemoveAll(p => p.Name == name) > 0;

    /// <summary>
    /// The strokes that name a profile the document does not have.
    ///
    /// A stroke holds its profile as a value, so it can still be drawn - but a name that resolves to nothing means
    /// a document has been edited or merged in a way that lost the asset, and the issue is explicit that this is
    /// a **reported** condition rather than a silent default. Silently drawing the stroke at its own width would
    /// make a lost asset look like a design decision.
    /// </summary>
    public IEnumerable<(PathItem Path, string Name)> MissingWidthProfiles()
    {
        foreach (PathItem path in AllPaths())
        {
            foreach (StrokeSpec stroke in path.Strokes)
            {
                if (stroke.WidthProfile is { } profile && FindProfile(profile.Name) is null)
                {
                    yield return (path, profile.Name);
                }
            }
        }
    }

    /// <summary>
    /// Every item in the document, artboards and pasteboard alike, in tree order.
    ///
    /// Needed by anything that has to act on the whole document rather than on a selection - editing a profile
    /// asset has to reach the strokes that refer to it wherever they are, including inside groups.
    /// </summary>
    public IEnumerable<LayerItem> AllItems()
    {
        foreach (Artboard artboard in _artboards)
        {
            foreach (Layer layer in artboard.Layers)
            {
                foreach (LayerItem item in Walk(layer.Children))
                {
                    yield return item;
                }
            }
        }

        foreach (LayerItem item in Walk(Orphans.Children))
        {
            yield return item;
        }
    }

    /// <summary>Every path in the document, in tree order.</summary>
    public IEnumerable<PathItem> AllPaths() => AllItems().OfType<PathItem>();

    /// <summary>
    /// Every group in the document, in tree order.
    ///
    /// Used to find the **instances** - a group carrying a source id - which is how SVG's `use` maps: the link is
    /// on the group rather than in a table, so finding them means walking the tree.
    /// </summary>
    public IEnumerable<ArtGroup> AllGroups() => AllItems().OfType<ArtGroup>();

    private static IEnumerable<LayerItem> Walk(IEnumerable<LayerItem> items)
    {
        foreach (LayerItem item in items)
        {
            yield return item;

            if (item is ArtGroup group)
            {
                foreach (LayerItem child in Walk(group.Children))
                {
                    yield return child;
                }
            }
        }
    }

    /// <summary>Replaces the library wholesale; deserialization and the edit command use this.</summary>
    internal void SetWidthProfiles(IEnumerable<WidthProfileSpec> profiles)
    {
        _widthProfiles.Clear();
        _widthProfiles.AddRange(profiles);
    }

    /// <summary>
    /// The decoded Adobe Illustrator private-data payload this document carries,
    /// or <c>null</c> when the document has none.
    ///
    /// This is the "stay in sync" channel for foreign <c>.ai</c>/PDF files: the
    /// importer captures the payload, the lossless serializer persists it (as
    /// decoded text plus its source format — never the compressed bytes), and the
    /// exporter re-emits it into every page's <c>/PieceInfo</c>. A document with a
    /// null (or empty) payload exports exactly as before.
    /// </summary>
    public AiPrivateData? AiPrivateData { get; set; }

    /// <summary>
    /// What the file this document was read from was protected with, or null when it was not
    /// protected at all.
    ///
    /// Deliberately **not** in the lossless sidecar. It describes the *file that was opened*, not the
    /// document: a document saved again is a new file, and carrying the old file's permissions into
    /// it would be a claim a person could act on and that nothing supports.
    /// </summary>
    public DocumentSecurity? Security { get; set; }

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
