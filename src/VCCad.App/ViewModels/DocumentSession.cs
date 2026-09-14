using System.ComponentModel;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using VCCad.Core.Commands;
using VCCad.Core.Model;
using VCCad.Core.Serialization;
using VCCad.Geometry;

namespace VCCad.App.ViewModels;

/// <summary>What to do with an artboard's children when it is deleted.</summary>
public enum ArtboardDeletionChoice
{
    KeepObjects,
    DeleteObjects,
    Cancel,
}

/// <summary>Prompt payload: an artboard with children is being deleted.</summary>
public sealed record ArtboardDeletionRequest(Artboard Artboard, int ChildCount);

/// <summary>The active editing tool.</summary>
public enum EditorTool
{
    /// <summary>Selection &amp; move (V). Shift adds to the selection.</summary>
    Select,

    /// <summary>Direct selection (A): drag nodes and handles, click/Shift-click to
    /// select individual segments.</summary>
    Node,

    /// <summary>Pen: click to add anchors, drag for smooth handles, click the start
    /// anchor to close (P).</summary>
    Pen,

    /// <summary>Drag out a closed rectangle between two corner points.</summary>
    Rectangle,

    /// <summary>Drag out a closed ellipse between two bounding-box corners.</summary>
    Ellipse,

    /// <summary>Select/move/resize artboards (and create new ones).</summary>
    Artboard,

    /// <summary>Click to place a text object, then edit it in the Text pane.</summary>
    Text,
}

/// <summary>
/// One open document: its <see cref="CadDocument"/>, its own command/undo stack,
/// and its selection state. This is the per-tab model; <c>EditorViewModel</c> holds
/// one session per open document and forwards to the active one.
/// </summary>
public sealed class DocumentSession : INotifyPropertyChanged
{
    private CadDocument _document = CadDocument.CreateDefault("Untitled");
    private readonly CommandStack _stack = new();
    private readonly List<LayerItem> _selectedObjects = new();
    private readonly Dictionary<PathItem, List<(int Sub, int Seg)>> _selectedSegments = new();
    /// <summary>Where status messages go (set by the owning view-model).</summary>
    public Action<string>? StatusSink;

    /// <summary>True while a text object is being edited on the canvas.</summary>
    public bool IsEditingText { get; set; }

    /// <summary>Run index under the text caret (for run-aware styling).</summary>
    public int TextCaretRunIndex { get; set; }

    private void SetStatus(string message) => StatusSink?.Invoke(message);

    /// <summary>Raised after any change that must trigger a workspace repaint or a
    /// tree refresh (document edits, selection changes, tool switches).</summary>
    public event EventHandler? DocumentChanged;

    /// <summary>Raised when the selection set changes (used to reset selection
    /// chrome such as the oriented bounding box).</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>Raised when deleting an artboard that still has children, so the
    /// UI can ask whether to keep (orphan), delete, or cancel.</summary>
    public event EventHandler<ArtboardDeletionRequest>? ArtboardDeletionRequested;

    /// <summary>Raised while a pointer gesture mutates geometry (move/node/segment/
    /// resize drags) so the numeric Transform panel updates live, without the cost
    /// of a full document/tree refresh on every mouse move.</summary>
    public event EventHandler? TransformChanged;

    internal void RaiseTransformChanged() => TransformChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>Loads a document into this session (used when opening a tab).</summary>
    public void Initialize(CadDocument document)
    {
        Document = document;
        _stack.Clear();
    }

    public CadDocument Document
    {
        get => _document;
        private set
        {
            _document = value;
            _selectedObjects.Clear();
            _selectedSegments.Clear();
            DocumentChanged?.Invoke(this, EventArgs.Empty);
            OnPropertyChanged();
        }
    }

    // ------------------------------------------------------------------
    // Selection model
    // ------------------------------------------------------------------

    private Artboard? _selectedArtboard;

    // Explicit selection rotation (radians). 0 until the user rotates; preserved
    // through move/scale; reset when the selection changes.
    private double _selectionRotationRadians;

    /// <summary>The selected artboard (Artboard tool / tree), or null.</summary>
    public Artboard? SelectedArtboard => _selectedArtboard;

    /// <summary>Selects an artboard (clearing object/point/segment selections).</summary>
    public void SelectArtboard(Artboard? artboard)
    {
        _selectedArtboard = artboard;
        _selectedObjects.Clear();
        _selectedSegments.Clear();
        _point = null;
        NotifySelectionChanged();
        OnPropertyChanged(nameof(SelectedArtboard));
    }

    /// <summary>Creates a new A4-landscape artboard offset to the right of the last
    /// one, with a fresh layer, and selects it (one undo step).</summary>
    public void AddNewArtboard()
    {
        double offsetX = 0;
        foreach (Artboard existing in Document.Artboards)
        {
            offsetX = Math.Max(offsetX, existing.X + existing.Width + 40);
        }

        var artboard = new Artboard(PageSizes.A4Landscape, new Point2D(offsetX, 0))
        {
            Name = $"Artboard {Document.Artboards.Count + 1}",
        };
        artboard.AddLayer("Layer 1");
        Execute(new AddArtboardCommand(Document, artboard));
        SelectArtboard(artboard);
        SetStatus($"Added {artboard.Name}");
    }

    /// <summary>Sets an artboard's rectangle (position/size) as one undo step.</summary>
    public void SetArtboardBounds(Artboard artboard, Rect2D before, Rect2D after)
    {
        if (before.Equals(after))
        {
            return;
        }

        Execute(new SetArtboardBoundsCommand(artboard, before, after));
    }

    /// <summary>
    /// Resizes/moves an artboard and reparents any orphan objects that now fall
    /// inside it (their coordinates are converted to artboard-local). One undo.
    /// </summary>
    public void ApplyArtboardBounds(Artboard artboard, Rect2D before, Rect2D after)
    {
        var commands = new List<IUndoableCommand>();
        if (!before.Equals(after))
        {
            commands.Add(new SetArtboardBoundsCommand(artboard, before, after));
        }

        List<LayerItem> orphans = OrphansIntersecting(after);
        if (orphans.Count > 0)
        {
            commands.Add(new ReparentItemsCommand(Document, artboard, orphans, Document.Orphans));
        }

        if (commands.Count == 0)
        {
            return;
        }

        Execute(commands.Count == 1 ? commands[0] : new CompositeCommand("Edit artboard", commands));
    }

    private List<LayerItem> OrphansIntersecting(Rect2D rect)
    {
        var hits = new List<LayerItem>();
        foreach (LayerItem item in Document.Orphans.Children)
        {
            Rect2D bounds = item switch
            {
                PathItem path => path.WorldBounds(),
                ArtGroup group => group.BoundingBox(),
                _ => Rect2D.Empty,
            };

            if (!bounds.IsEmpty && bounds.Intersects(rect))
            {
                hits.Add(item);
            }
        }

        return hits;
    }

    /// <summary>True when the artboard has any content.</summary>
    public static bool ArtboardHasChildren(Artboard artboard)
        => artboard.Layers.Any(l => l.Children.Count > 0);

    /// <summary>Deletes the selected artboard, applying the user's choice about
    /// its children.</summary>
    public void DeleteArtboard(Artboard artboard, ArtboardDeletionChoice choice)
    {
        if (choice == ArtboardDeletionChoice.Cancel)
        {
            return;
        }

        Execute(new DeleteArtboardCommand(Document, artboard, choice == ArtboardDeletionChoice.KeepObjects));
        SelectArtboard(null);
        SetStatus(choice == ArtboardDeletionChoice.KeepObjects
            ? "Artboard deleted; objects kept as orphans"
            : "Artboard and objects deleted");
    }

    /// <summary>Requests deletion of the current selection (object or artboard).
    /// For an artboard with children this raises a prompt event instead.</summary>
    public void RequestDeleteSelection()
    {
        if (_selectedArtboard is { } artboard)
        {
            if (ArtboardHasChildren(artboard))
            {
                int count = artboard.Layers.Sum(l => l.Children.Count);
                ArtboardDeletionRequested?.Invoke(this, new ArtboardDeletionRequest(artboard, count));
            }
            else
            {
                DeleteArtboard(artboard, ArtboardDeletionChoice.DeleteObjects);
            }

            return;
        }

        DeleteSelection();
    }

    /// <summary>Explicit rotation of the current selection, in radians (0 until
    /// the user rotates something).</summary>
    public double SelectionRotationRadians => _selectionRotationRadians;

    /// <summary>Updates the selection rotation (called by the canvas rotate gesture
    /// and the numeric rotation field).</summary>
    internal void SetSelectionRotationRadians(double radians) => _selectionRotationRadians = radians;

    /// <summary>Closes every selected open path (adds the closing segment; merges
    /// coincident endpoints). One undo step.</summary>
    public void CloseSelectedPaths()
    {
        var edits = new List<IUndoableCommand>();
        foreach (PathItem path in SelectedPaths())
        {
            PathItem before = path.GeometrySnapshot();
            bool changed = false;
            foreach (SubPath sub in path.SubPaths)
            {
                if (sub.IsClosed)
                {
                    continue;
                }

                if (!sub.CloseAndMergeEndpoints())
                {
                    sub.IsClosed = true;
                }

                changed = true;
            }

            if (changed)
            {
                edits.Add(new GeometryReplaceCommand(path, before, path.GeometrySnapshot(), "Close path"));
            }
        }

        if (edits.Count == 0)
        {
            SetStatus("No open paths to close");
            return;
        }

        Execute(edits.Count == 1 ? edits[0] : new CompositeCommand("Close path", edits));
        SetStatus("Path closed");
    }

    /// <summary>Joins two selected paths that share an endpoint (closing the result
    /// if its ends meet).</summary>
    public void JoinSelection()
    {
        PathItem[] paths = SelectedPaths().ToArray();
        for (int i = 0; i < paths.Length; i++)
        {
            for (int j = i + 1; j < paths.Length; j++)
            {
                if (PathJoin.CanJoin(paths[i], paths[j]))
                {
                    Execute(new JoinPathsCommand(paths[i], paths[j]));
                    SelectObject(paths[i]);
                    SetStatus(paths[i].IsFullyClosed ? "Paths joined and closed" : "Paths joined");
                    return;
                }
            }
        }

        SetStatus("Select two open paths with a shared endpoint");
    }

    /// <summary>Text items in the selection (including inside selected groups).</summary>
    public IEnumerable<TextItem> SelectedTextItems()
    {
        foreach (LayerItem item in _selectedObjects)
        {
            switch (item)
            {
                case TextItem text:
                    yield return text;
                    break;
                case ArtGroup group:
                    foreach (TextItem nested in DescendantTexts(group))
                    {
                        yield return nested;
                    }

                    break;
            }
        }
    }

    private static IEnumerable<TextItem> DescendantTexts(ArtGroup group)
    {
        foreach (LayerItem child in group.Children)
        {
            switch (child)
            {
                case TextItem text:
                    yield return text;
                    break;
                case ArtGroup nested:
                    foreach (TextItem t in DescendantTexts(nested))
                    {
                        yield return t;
                    }

                    break;
            }
        }
    }

    /// <summary>Creates a text object at a world point in the artboard under it
    /// (or the pasteboard), selects it and returns it.</summary>
    public TextItem CreateTextAt(Point2D world, string family, double fontSize)
    {
        (Layer layer, Vector2D offset) = TargetFor(world);
        var item = new TextItem
        {
            Name = "Text",
            Origin = world - offset,
            Color = ColorRgb.Black,
        };
        item.Runs.Add(new TextRun { Text = "Text", FontFamily = family, FontSize = fontSize });
        Execute(new AddItemCommand(layer, item));
        SelectObject(item);
        SetStatus("Text created — edit it in the Text pane");
        return item;
    }

    /// <summary>Updates the content and uniform style of the selected text
    /// object(s). One undo step.</summary>
    public void UpdateSelectedText(string content, string family, double fontSize, bool bold, bool italic,
        ColorRgb color, int? runIndex = null)
    {
        var edits = new List<IUndoableCommand>();
        foreach (TextItem text in SelectedTextItems())
        {
            TextItem before = (TextItem)text.Clone();

            if (runIndex is { } r && r >= 0 && r < text.Runs.Count)
            {
                // Style just the run under the caret (rich text).
                TextRun run = text.Runs[r];
                run.FontFamily = family;
                run.FontSize = fontSize;
                run.Bold = bold;
                run.Italic = italic;
            }
            else
            {
                text.PlainText = content;
                foreach (TextRun run in text.Runs)
                {
                    run.FontFamily = family;
                    run.FontSize = fontSize;
                    run.Bold = bold;
                    run.Italic = italic;
                }
            }

            text.Color = color;
            edits.Add(new ReplaceTextCommand(text, before, (TextItem)text.Clone(), "Edit text"));
        }

        if (edits.Count == 0)
        {
            SetStatus("Select a text object first");
            return;
        }

        Execute(edits.Count == 1 ? edits[0] : new CompositeCommand("Edit text", edits));
        SetStatus("Text updated");
    }

    /// <summary>Groups the selected sibling objects (Edit → Group).</summary>
    public void GroupSelection()
    {
        var items = _selectedObjects.Where(i => i.Container is not null).ToList();
        if (items.Count < 2)
        {
            SetStatus("Select two or more objects to group");
            return;
        }

        IItemContainer container = items[0].Container!;
        var command = new GroupItemsCommand(container, items);
        Execute(command);
        if (command.Group is { } group)
        {
            SelectObject(group);
            SetStatus("Grouped");
        }
    }

    /// <summary>Dissolves the selected groups (Edit → Ungroup).</summary>
    public void UngroupSelection()
    {
        var groups = _selectedObjects.OfType<ArtGroup>().ToList();
        if (groups.Count == 0)
        {
            SetStatus("Select a group to ungroup");
            return;
        }

        var freed = new List<LayerItem>();
        foreach (ArtGroup group in groups)
        {
            LayerItem[] children = group.Children.ToArray();
            Execute(new UngroupItemsCommand(group));
            freed.AddRange(children);
        }

        _selectedObjects.Clear();
        _selectedSegments.Clear();
        _point = null;
        foreach (LayerItem item in freed)
        {
            _selectedObjects.Add(item);
        }

        NotifySelectionChanged();
        SetStatus("Ungrouped");
    }

    /// <summary>Creates an artboard covering <paramref name="rect"/> (document space).</summary>
    public void AddArtboardFromRect(Rect2D rect)
    {
        double w = Math.Max(1, rect.Width);
        double h = Math.Max(1, rect.Height);
        var artboard = new Artboard(new Size2D(w, h), new Point2D(rect.X, rect.Y))
        {
            Name = $"Artboard {Document.Artboards.Count + 1}",
        };
        artboard.AddLayer("Layer 1");

        var commands = new List<IUndoableCommand> { new AddArtboardCommand(Document, artboard) };
        List<LayerItem> orphans = OrphansIntersecting(rect);
        if (orphans.Count > 0)
        {
            commands.Add(new ReparentItemsCommand(Document, artboard, orphans, Document.Orphans));
        }

        Execute(commands.Count == 1 ? commands[0] : new CompositeCommand("Add artboard", commands));
        SelectArtboard(artboard);
        SetStatus($"Added {artboard.Name}");
    }

    /// <summary>Selected objects (paths and groups), in picking order.</summary>
    public IReadOnlyList<LayerItem> SelectedObjects => _selectedObjects;

    /// <summary>The last selected object (used for Properties and rotation centre).</summary>
    public LayerItem? PrimarySelection => _selectedObjects.Count > 0 ? _selectedObjects[^1] : null;

    /// <summary>True when more than one object is selected.</summary>
    public bool HasMultiSelection => _selectedObjects.Count > 1;

    public bool IsObjectSelected(LayerItem item) => _selectedObjects.Contains(item);

    /// <summary>All selected path segments as (path, subpath index, segment index).</summary>
    public IEnumerable<(PathItem Path, int Sub, int Seg)> SelectedSegments()
    {
        foreach (KeyValuePair<PathItem, List<(int Sub, int Seg)>> entry in _selectedSegments)
        {
            foreach ((int sub, int seg) in entry.Value)
            {
                yield return (entry.Key, sub, seg);
            }
        }
    }

    public bool HasSegmentSelection => _selectedSegments.Count > 0;

    /// <summary>Whether a given segment of a path is in the segment selection.</summary>
    public bool IsSegmentSelected(PathItem path, int sub, int seg)
        => _selectedSegments.TryGetValue(path, out List<(int Sub, int Seg)>? list) && list.Contains((sub, seg));

    /// <summary>Replaces the whole selection with a single object (or clears it).</summary>
    public void SelectObject(LayerItem? item)
    {
        _selectedArtboard = null;
        _selectedObjects.Clear();
        _selectedSegments.Clear();
        _point = null;
        if (item is not null)
        {
            _selectedObjects.Add(item);
        }

        NotifySelectionChanged();
    }

    /// <summary>Shift-click object selection: toggle membership, keep everything else.</summary>
    public void ToggleObjectSelection(LayerItem item)
    {
        if (_selectedObjects.Remove(item))
        {
            _selectedSegments.Remove(item as PathItem);
        }
        else
        {
            _selectedObjects.Add(item);
        }

        NotifySelectionChanged();
    }

    /// <summary>
    /// Adds a set of objects to the selection (after an object drag on a shared
    /// element) without disturbing segment selections.
    /// </summary>
    public void AddObjects(IEnumerable<LayerItem> items)
    {
        bool changed = false;
        foreach (LayerItem item in items)
        {
            if (!_selectedObjects.Contains(item))
            {
                _selectedObjects.Add(item);
                changed = true;
            }
        }

        if (changed)
        {
            NotifySelectionChanged();
        }
    }

    /// <summary>
    /// Selects a segment of a path for direct selection. Without
    /// <paramref name="additive"/> the whole selection is replaced by (this path,
    /// this segment); additive (Shift) toggles the segment and keeps other
    /// objects/segments selected.
    /// </summary>
    public void SelectSegment(PathItem path, int sub, int seg, bool additive)
    {
        if (!additive)
        {
            _selectedArtboard = null;
            _selectedObjects.Clear();
            _selectedSegments.Clear();
            _point = null;
            _selectedObjects.Add(path);
            _selectedSegments[path] = new List<(int, int)> { (sub, seg) };
        }
        else
        {
            if (!_selectedObjects.Contains(path))
            {
                _selectedObjects.Add(path);
            }

            if (!_selectedSegments.TryGetValue(path, out List<(int Sub, int Seg)>? list))
            {
                _selectedSegments[path] = list = new List<(int, int)>();
            }

            (int, int) key = (sub, seg);
            if (list.Contains(key))
            {
                list.Remove(key);
                if (list.Count == 0)
                {
                    _selectedSegments.Remove(path);
                }
            }
            else
            {
                list.Add(key);
            }
        }

        NotifySelectionChanged();
    }

    /// <summary>Clears object and segment selections.</summary>
    public void ClearSelection()
    {
        if (_selectedObjects.Count == 0 && _selectedSegments.Count == 0 && _point is null)
        {
            return;
        }

        _selectedArtboard = null;
        _selectedObjects.Clear();
        _selectedSegments.Clear();
        _point = null;
        NotifySelectionChanged();
    }

    /// <summary>Clears only the segment/point sub-selection, keeping objects selected.</summary>
    public void ClearSegmentSelection()
    {
        if (_selectedSegments.Count == 0 && _point is null)
        {
            return;
        }

        _selectedSegments.Clear();
        _point = null;
        NotifySelectionChanged();
    }

    /// <summary>
    /// Inserts a new node on a segment at the location nearest
    /// <paramref name="near"/> (one undo step). Bézier segments are spliced with
    /// de Casteljau so the curve's shape is preserved exactly.
    /// </summary>
    public void InsertPointOnSegment(PathItem path, int subIndex, int segmentIndex, Point2D near)
    {
        if (subIndex < 0 || subIndex >= path.SubPaths.Count)
        {
            return;
        }

        SubPath sub = path.SubPaths[subIndex];
        if (segmentIndex < 0 || segmentIndex >= sub.SegmentCount)
        {
            return;
        }

        sub.GetSegment(segmentIndex).NearestPoint(near, out double t, out _);

        PathItem before = path.GeometrySnapshot();
        sub.InsertNodeOnSegment(segmentIndex, t);
        PathItem after = path.GeometrySnapshot();
        Execute(new GeometryReplaceCommand(path, before, after, "Add point"));
        ClearSegmentSelection();
        SetStatus("Point added");
    }

    /// <summary>Sets the object selection to a set (marquee): replaces it unless
    /// <paramref name="additive"/>, in which case items are added to the current
    /// selection (Shift-marquee).</summary>
    public void SelectRange(IEnumerable<LayerItem> items, bool additive)
    {
        var materialised = items.Where(i => i is PathItem or ArtGroup).ToArray();
        if (!additive)
        {
            _selectedArtboard = null;
            _selectedObjects.Clear();
            _selectedSegments.Clear();
            _point = null;
        }

        foreach (LayerItem item in materialised)
        {
            if (!_selectedObjects.Contains(item))
            {
                _selectedObjects.Add(item);
            }
        }

        NotifySelectionChanged();
    }

    /// <summary>Removes segment selections whose path object is no longer selected.</summary>
    private void PruneSegmentSelection()
    {
        foreach (PathItem path in _selectedSegments.Keys.Where(p => !_selectedObjects.Contains(p)).ToArray())
        {
            _selectedSegments.Remove(path);
        }
    }

    /// <summary>Selected paths whose geometry can be edited. Selecting a group
    /// expands to every path inside it (groups have no coordinates of their own).</summary>
    public IEnumerable<PathItem> SelectedPaths()
    {
        foreach (LayerItem item in _selectedObjects)
        {
            switch (item)
            {
                case PathItem path:
                    yield return path;
                    break;
                case ArtGroup group:
                    foreach (PathItem nested in DescendantPaths(group))
                    {
                        yield return nested;
                    }

                    break;
            }
        }
    }

    private static IEnumerable<PathItem> DescendantPaths(ArtGroup group)
    {
        foreach (LayerItem child in group.Children)
        {
            switch (child)
            {
                case PathItem path:
                    yield return path;
                    break;
                case ArtGroup nested:
                    foreach (PathItem p in DescendantPaths(nested))
                    {
                        yield return p;
                    }

                    break;
            }
        }
    }

    /// <summary>The combined world-space bounds of the selected objects.</summary>
    public Rect2D SelectionBounds()
    {
        Rect2D box = Rect2D.Empty;
        foreach (PathItem path in SelectedPaths())
        {
            box = box.Union(path.WorldBounds());
        }

        foreach (TextItem text in SelectedTextItems())
        {
            box = box.Union(text.WorldBounds());
        }

        return box;
    }

    // ------------------------------------------------------------------
    // Point (node) selection & numeric editing
    // ------------------------------------------------------------------

    private (PathItem Path, int Sub, int Node)? _point;

    /// <summary>True when a single node has been picked (point editing mode: only
    /// position applies — a point has no width, height or rotation).</summary>
    public bool HasPointSelection => _point is not null && ResolvePointNode() is not null;

    /// <summary>The anchor of the currently selected point, or null.</summary>
    public Point2D? PointPosition => ResolvePointNode()?.Anchor;

    /// <summary>Remembers the picked node (also selects its path so the tree and
    /// overlays show it).</summary>
    public void SelectPoint(PathItem path, int sub, int node)
    {
        _selectedArtboard = null;
        // Ensure the path is part of the object selection WITHOUT clearing the
        // point we are about to set (SelectObject would wipe it).
        if (!_selectedObjects.Contains(path))
        {
            _selectedObjects.Add(path);
        }

        _point = (path, sub, node);
        NotifySelectionChanged();
    }

    public void ClearPointSelection()
    {
        if (_point is null)
        {
            return;
        }

        _point = null;
        NotifySelectionChanged();
    }

    private PathNode? ResolvePointNode()
    {
        if (_point is not { } p)
        {
            return null;
        }

        if (p.Sub < 0 || p.Sub >= p.Path.SubPaths.Count)
        {
            return null;
        }

        SubPath sub = p.Path.SubPaths[p.Sub];
        if (p.Node < 0 || p.Node >= sub.Nodes.Count)
        {
            return null;
        }

        return sub.Nodes[p.Node];
    }

    /// <summary>Moves the selected point (its anchor and both handles) to an
    /// absolute model coordinate — one undo step.</summary>
    public void MovePointTo(Point2D target)
    {
        if (_point is not { } p)
        {
            return;
        }

        PathNode node = ResolvePointNode();
        if (node is null)
        {
            return;
        }

        PathItem before = p.Path.GeometrySnapshot();
        Vector2D delta = target - node.Anchor;
        p.Path.TranslateNode(p.Path.SubPaths[p.Sub], p.Node, delta);
        PathItem after = p.Path.GeometrySnapshot();
        Execute(new VCCad.Core.Commands.GeometryReplaceCommand(p.Path, before, after, "Move point"));
    }

    // ------------------------------------------------------------------
    // Style (colour / stroke) application for the Color & Stroke tabs
    // ------------------------------------------------------------------

    /// <summary>Applies a fill (colour + rule) to every selected path, one undo step.</summary>
    public void ApplyFill(ColorRgb color, FillRule rule)
    {
        var edits = SelectedPaths()
            .Select(p => (IUndoableCommand)new SetFillCommand(p, FillSpec.Solid(color, rule)))
            .ToList();
        ExecuteIfAny(edits, "Fill");
    }

    /// <summary>Clears the fill of every selected path (one undo step).</summary>
    public void ClearFill()
    {
        var edits = SelectedPaths()
            .Select(p => (IUndoableCommand)new SetFillCommand(p, FillSpec.None))
            .ToList();
        ExecuteIfAny(edits, "Clear fill");
    }

    /// <summary>Clears the stroke of every selected path (one undo step).</summary>
    public void ClearStroke()
    {
        var edits = SelectedPaths()
            .Select(p => (IUndoableCommand)new SetStrokeCommand(p, StrokeSpec.None))
            .ToList();
        ExecuteIfAny(edits, "Clear stroke");
    }

    /// <summary>Applies a stroke colour to every selected path, keeping each path's
    /// existing width/caps/joins.</summary>
    public void ApplyStrokeColor(ColorRgb color)
    {
        var edits = SelectedPaths().Select(p =>
        {
            double width = p.Stroke.Width > 0 ? p.Stroke.Width : 1.0;
            return (IUndoableCommand)new SetStrokeCommand(p,
                new StrokeSpec(true, color, width, p.Stroke.Cap, p.Stroke.Join, p.Stroke.MiterLimit, p.Stroke.Alignment));
        }).ToList();
        ExecuteIfAny(edits, "Stroke colour");
    }

    /// <summary>Applies stroke geometry (width/cap/join/miter) to selected paths,
    /// keeping each path's existing colour.</summary>
    public void ApplyStroke(double width, StrokeCap cap, StrokeJoin join, double miterLimit,
        StrokeAlignment alignment)
    {
        var edits = SelectedPaths().Select(p =>
        {
            ColorRgb color = p.Stroke.IsVisible ? p.Stroke.Color : ColorRgb.Black;
            return (IUndoableCommand)new SetStrokeCommand(p,
                new StrokeSpec(true, color, Math.Max(0, width), cap, join, Math.Max(1, miterLimit), alignment));
        }).ToList();
        ExecuteIfAny(edits, "Stroke");
    }

    private void ExecuteIfAny(List<IUndoableCommand> edits, string label)
    {
        if (edits.Count == 0)
        {
            return;
        }

        Execute(edits.Count == 1 ? edits[0] : new CompositeCommand(label, edits));
    }

    // ------------------------------------------------------------------
    // Object numeric transform (X / Y / W / H / rotation + 9-point pivot)
    // ------------------------------------------------------------------

    /// <summary>True when whole-object transform fields are meaningful (objects
    /// selected, no point mode).</summary>
    public bool HasTransformableSelection => !HasPointSelection && SelectedPaths().Any();

    /// <summary>Current bounds + principal-axis angle (degrees, 0..180) of the
    /// selected objects — the basis the numeric fields display.</summary>
    public (Rect2D Bounds, double AngleDeg) TransformReadout()
    {
        Rect2D box = Rect2D.Empty;
        foreach (PathItem path in SelectedPaths())
        {
            box = box.Union(path.WorldBounds());
        }

        return (box, _selectionRotationRadians * 180.0 / Math.PI);
    }

    /// <summary>
    /// Applies numeric object transforms to every selected path in one undo step:
    /// translate by <paramref name="translation"/>, scale by sx/sy about the pivot,
    /// then rotate by <paramref name="rotationDegrees"/> about the pivot.
    /// </summary>
    public void ApplyTransform(Point2D pivot, Vector2D translation, double scaleX, double scaleY, double rotationDegrees)
    {
        if (!HasTransformableSelection)
        {
            return;
        }

        bool anyTranslation = !translation.IsZero;
        bool anyScale = Math.Abs(scaleX - 1.0) > 1e-9 || Math.Abs(scaleY - 1.0) > 1e-9;
        bool anyRotation = Math.Abs(rotationDegrees) > 1e-6;
        if (!anyTranslation && !anyScale && !anyRotation)
        {
            return;
        }

        var edits = new List<IUndoableCommand>();
        foreach (PathItem path in SelectedPaths())
        {
            PathItem before = path.GeometrySnapshot();

            // Paths store artboard-local coordinates; convert the world pivot.
            Point2D localPivot = pivot - path.ArtboardOffset();

            if (anyTranslation)
            {
                path.TranslateGeometryBy(translation);
            }

            if (anyScale)
            {
                path.ScaleGeometryAbout(localPivot, scaleX, scaleY);
            }

            if (anyRotation)
            {
                path.RotateGeometryAbout(localPivot, rotationDegrees * Math.PI / 180.0);
            }

            edits.Add(new GeometryReplaceCommand(path, before, path.GeometrySnapshot()));
        }

        if (anyRotation)
        {
            _selectionRotationRadians += rotationDegrees * Math.PI / 180.0;
        }

        Execute(edits.Count == 1
            ? edits[0]
            : new CompositeCommand("Transform objects", edits));
    }


    // ------------------------------------------------------------------
    // Commands / undo / actions
    // ------------------------------------------------------------------

    public void Execute(IUndoableCommand command)
    {
        _stack.Execute(command);
        NotifyCanvas();
    }

    public void Undo()
    {
        if (_stack.Undo())
        {
            SetStatus($"Undid: {_stack.UndoDescription ?? "action"}");
            NotifyCanvas();
        }
        else
        {
            SetStatus("Nothing to undo");
        }
    }

    public void Redo()
    {
        if (_stack.Redo())
        {
            SetStatus("Redone");
            NotifyCanvas();
        }
        else
        {
            SetStatus("Nothing to redo");
        }
    }

    /// <summary>Deletes the selected objects (single composite undo step).</summary>
    public void DeleteSelection()
    {
        if (_selectedObjects.Count == 0)
        {
            return;
        }

        var remove = new List<IUndoableCommand>();
        foreach (LayerItem item in _selectedObjects.Where(i => i.Container is not null))
        {
            remove.Add(new RemoveItemCommand(item));
        }

        if (remove.Count == 0)
        {
            return;
        }

        string label = remove.Count == 1 ? "Delete object" : $"Delete {remove.Count} objects";
        Execute(new CompositeCommand(label, remove));
        _selectedArtboard = null;
        _selectedObjects.Clear();
        _selectedSegments.Clear();
        _point = null;
        NotifySelectionChanged();
        SetStatus(label);
    }

    /// <summary>
    /// The container new geometry should go into for a world point: the artboard
    /// under the point (its top layer) with that artboard's origin as offset, or
    /// the document's orphan/pasteboard layer when the point is off all artboards.
    /// </summary>
    public (Layer Layer, Vector2D Offset) TargetFor(Point2D world)
    {
        foreach (Artboard artboard in Document.Artboards)
        {
            if (artboard.Bounds.Contains(world))
            {
                Layer layer = artboard.Layers.Count > 0
                    ? artboard.Layers[^1]
                    : artboard.AddLayer("Layer 1");
                return (layer, new Vector2D(artboard.X, artboard.Y));
            }
        }

        return (Document.Orphans, default);
    }

    /// <summary>The default target layer for tool-created items.</summary>
    public Layer TargetLayer()
    {
        if (Document.Artboards.Count == 0)
        {
            Document.AddArtboard(PageSizes.A4Landscape, "Artboard 1");
        }

        Artboard artboard = Document.Artboards[0];
        if (artboard.Layers.Count == 0)
        {
            artboard.AddLayer("Layer 1");
        }

        return artboard.Layers[^1];
    }

    // ------------------------------------------------------------------
    // Notifications
    // ------------------------------------------------------------------

    private void NotifySelectionChanged()
    {
        _selectionRotationRadians = 0;
        PruneSegmentSelection();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        OnPropertyChanged(nameof(SelectedObjects));
        OnPropertyChanged(nameof(PrimarySelection));
        OnPropertyChanged(nameof(HasMultiSelection));
        DocumentChanged?.Invoke(this, EventArgs.Empty);
    }

    private void NotifyCanvas()
    {
        DocumentChanged?.Invoke(this, EventArgs.Empty);
        OnPropertyChanged(nameof(Document));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
