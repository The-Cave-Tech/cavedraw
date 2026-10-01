using System.ComponentModel;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using VCCad.Core.Commands;
using VCCad.Core.Model;
using VCCad.Core.Selection;
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

    /// <summary>
    /// Round a corner: press on a corner of a path and drag, and the distance dragged becomes the
    /// radius of the arc that replaces it. The corner stays put - it is where the radius is measured
    /// from - so pulling out from the corner a person is looking at grows the round predictably.
    /// </summary>
    Corner,
    /// <summary>
    /// Pencil: draw freehand. Press, move, release, and what was drawn becomes an open path of cubic
    /// segments. The stroke is shown exactly as it was captured while drawing and fitted **once** on
    /// release, because re-fitting under the pointer makes the line wander.
    /// </summary>
    Pencil,
    /// <summary>Drag out a closed rectangle between two corner points.</summary>
    Rectangle,

    /// <summary>
    /// Draw one of the nine paint shapes - rectangle, rounded rectangle, star, polygon, trapezoid, cloud,
    /// callout, heart, arrow. Which one is the view's <c>CurrentShape</c>, and the toolbar button shows
    /// it, so the armed shape is never a thing a person has to remember.
    /// </summary>
    Shape,
    /// <summary>Drag out a closed ellipse between two bounding-box corners.</summary>
    Ellipse,

    /// <summary>Select/move/resize artboards (and create new ones).</summary>
    Artboard,

    /// <summary>Click to place a text object, then edit it in the Text pane.</summary>
    Text,

    /// <summary>
    /// Freehand selection (Q): drag any shape and what it encloses is selected.
    ///
    /// The shape closes itself with a straight segment from where the pointer is back to where
    /// it went down, so a scribble that never returns to its start still encloses something.
    /// It is the same selection code as the rectangular marquee - a marquee is a rectangular
    /// path and this is any path - so the two cannot drift apart.
    /// </summary>
    Lasso,
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
    /// <summary>
    /// The text block currently being edited, if any.
    ///
    /// While a block is being typed into it is the editing target whether or not it is in
    /// the selection — the person may have clicked the canvas, chosen a font, or had the
    /// selection cleared by selecting a document. Operations that style text need to reach
    /// it regardless.
    /// </summary>
    /// <summary>
    /// Whether the document has been changed since it was last saved or opened.
    ///
    /// Closing or exiting with unsaved changes is the one place an editor must stop and
    /// ask, and it can only ask if it knows. Any undoable edit sets this; saving, and
    /// loading a document, clear it.
    /// </summary>
    public bool IsModified { get; private set; }

    /// <summary>Records that the current state is on disk, so nothing needs saving.</summary>
    public void MarkSaved() => IsModified = false;

    /// <summary>
    /// The face new text is created with, set by the font picker when nothing is
    /// selected. Kept per session so switching documents does not carry a choice across.
    /// </summary>
    public string DefaultFontFamily { get; set; } = TextItem.DefaultFontFamily;

    /// <summary>
    /// The size new text is created with, and the size the text controls show while a block is edited.
    ///
    /// A block can hold several sizes, so this is adopted from the run at the caret when a block is opened -
    /// the same reasoning as the face - and it is what the next shape of text is drawn in when nothing is
    /// being edited.
    /// </summary>
    public double DefaultFontSize { get; set; } = 12.0;

    public TextItem? EditingText { get; set; }

    public bool IsEditingText { get; set; }

    /// <summary>Run index under the text caret (for run-aware styling).</summary>
    public int TextCaretRunIndex { get; set; }

    /// <summary>
    /// Where the text caret is, as a **character offset** into the block being edited.
    ///
    /// The same caret the run index above is read from, published in the coordinate the text helpers take
    /// (`TextEditing.RunAt`, `FontAt`). It exists because two integers that mean different things cannot be told
    /// apart at a call site: the type toolbar was handed the run index where an offset was wanted and described
    /// the first run for every caret past it (issue #157).
    /// </summary>
    public int TextCaretOffset { get; set; }

    /// <summary>Current text selection range (global char indices) while editing.</summary>
    public int TextSelectionStart { get; set; }

    public int TextSelectionEnd { get; set; }

    /// <summary>Fill used for newly drawn objects (adopted from the selection).</summary>
    public FillSpec CurrentFill { get; set; } = FillSpec.None;

    /// <summary>Stroke used for newly drawn objects (adopted from the selection).</summary>
    public StrokeSpec CurrentStroke { get; set; } = StrokeSpec.Hairline(ColorRgb.Black);

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

    /// <summary>
    /// Closes every selected open path: the ends snap together when they nearly meet, and a closing
    /// segment is added when they do not. One undo step.
    ///
    /// Returns what each close did, so a caller can say which case it was rather than reporting "path
    /// closed" whether the path gained an edge or lost a stray end.
    /// </summary>
    public IReadOnlyList<PathCloseResult> CloseSelectedPaths()
    {
        var edits = new List<IUndoableCommand>();
        var results = new List<PathCloseResult>();

        foreach (PathItem path in SelectedPaths())
        {
            PathItem before = path.GeometrySnapshot();
            bool changed = false;

            for (int index = 0; index < path.SubPaths.Count; index++)
            {
                if (path.SubPaths[index].IsClosed)
                {
                    continue;
                }

                PathCloseResult result = PathCloser.Close(path, index);
                results.Add(result);
                changed |= result.Changed;
            }

            if (changed)
            {
                edits.Add(new GeometryReplaceCommand(path, before, path.GeometrySnapshot(), "Close path"));
            }
        }

        if (edits.Count == 0)
        {
            SetStatus("No open paths to close");
            return results;
        }

        Execute(edits.Count == 1 ? edits[0] : new CompositeCommand("Close path", edits));

        SetStatus(results.Any(r => r.Mode == PathCloseMode.SegmentAdded)
            ? "Path closed with a new segment"
            : results.Any(r => r.Mode == PathCloseMode.Snapped)
                ? "Path closed: the ends were snapped together"
                : "Path closed");

        return results;
    }

    /// <summary>
    /// The selected paths in z-order, bottom first, with the layer each is on. The order is load-bearing
    /// for subtract, which keeps the back-most path - Illustrator's Minus Front - so both the operation
    /// and the panel must ask for the selection the same way.
    /// </summary>
    public List<(Layer Layer, PathItem Path)> SelectedPathsInZOrder()
    {
        var selected = SelectedPaths().ToHashSet();
        var ordered = new List<(Layer, PathItem)>();

        foreach (Artboard artboard in Document.Artboards)
        {
            foreach (Layer layer in artboard.Layers)
            {
                foreach (LayerItem item in layer.Children)
                {
                    if (item is PathItem path && selected.Contains(path))
                    {
                        ordered.Add((layer, path));
                    }
                }
            }
        }

        return ordered;
    }

    /// <summary>Applies a boolean to the selection. One undo step, however many objects it consumes.</summary>
    public PathBooleanResult BooleanSelection(BooleanOp op)
    {
        List<(Layer Layer, PathItem Path)> targets = SelectedPathsInZOrder();
        if (targets.Count < 2)
        {
            throw new InvalidOperationException(
                $"{op} needs at least two paths selected; {targets.Count} selected.");
        }

        PathItem? result = PathBoolean.Combine(targets.Select(t => t.Path).ToList(), op);
        return ReplaceSelection(
            targets,
            result is null ? Array.Empty<PathItem>() : new[] { result },
            op.ToString());
    }

    /// <summary>Cuts the selection into its separate regions: one object each, not one merged object.</summary>
    public PathBooleanResult DivideSelection()
    {
        List<(Layer Layer, PathItem Path)> targets = SelectedPathsInZOrder();
        if (targets.Count < 2)
        {
            throw new InvalidOperationException(
                $"Divide needs at least two paths selected; {targets.Count} selected.");
        }

        IReadOnlyList<PathItem> pieces = PathBoolean.Divide(targets.Select(t => t.Path).ToList());
        return ReplaceSelection(targets, pieces, "Divide");
    }

    /// <summary>Fills the selection as one object with holes where they overlap, without cutting.</summary>
    public PathBooleanResult MakeCompoundSelection()
    {
        List<(Layer Layer, PathItem Path)> targets = SelectedPathsInZOrder();
        if (targets.Count < 2)
        {
            throw new InvalidOperationException(
                $"Making a compound path needs at least two paths selected; {targets.Count} selected.");
        }

        PathItem source = targets[0].Path;
        var compound = new PathItem
        {
            Name = source.Name,
            Fill = source.Fill,
            Stroke = source.Stroke,
            Opacity = source.Opacity,
        };

        foreach ((Layer _, PathItem path) in targets)
        {
            foreach (SubPath sub in path.SubPaths)
            {
                SubPath target = compound.AddSubPath(sub.IsClosed);
                foreach (PathNode node in sub.Nodes)
                {
                    target.Nodes.Add(new PathNode(node.Anchor, node.InHandle, node.OutHandle));
                }
            }
        }

        // The windings are put into the form the nonzero rule needs, so the inner outlines are holes.
        CompoundPaths.Normalise(compound);
        compound.GeometryChanged();

        return ReplaceSelection(targets, new[] { compound }, "Make compound path");
    }

    /// <summary>Takes the selected compound paths apart: one object per outline.</summary>
    public PathBooleanResult ReleaseCompoundSelection()
    {
        List<(Layer Layer, PathItem Path)> targets = SelectedPathsInZOrder();
        if (targets.Count == 0)
        {
            throw new InvalidOperationException("Select a path to release.");
        }

        var pieces = new List<PathItem>();
        foreach ((Layer _, PathItem path) in targets)
        {
            pieces.AddRange(CompoundPaths.Release(path));
        }

        return ReplaceSelection(targets, pieces, "Release compound path");
    }

    /// <summary>Turns one outline of the selection inside out: a hole becomes an island.</summary>
    public bool ReverseSubpathOfSelection(int index)
    {
        PathItem? path = SelectedPaths().FirstOrDefault();
        if (path is null)
        {
            throw new InvalidOperationException("Select a path, or pass itemId.");
        }

        PathItem before = path.GeometrySnapshot();
        if (!CompoundPaths.Reverse(path, index))
        {
            throw new InvalidOperationException(
                $"'{path.Name}' has {path.SubPaths.Count} outlines, so there is no outline {index}.");
        }

        Execute(new GeometryReplaceCommand(path, before, path.GeometrySnapshot(), "Reverse outline"));
        SetStatus("Outline reversed");
        return true;
    }

    /// <summary>
    /// Swaps the selected paths for their result, in one undo step, and reports what went in and what
    /// came out so a driver can tell an empty result from a failed one.
    /// </summary>
    private PathBooleanResult ReplaceSelection(
        List<(Layer Layer, PathItem Path)> targets,
        IReadOnlyList<PathItem> results,
        string description)
    {
        Layer layer = targets[0].Layer;
        var edits = new List<IUndoableCommand>();

        foreach ((Layer _, PathItem path) in targets)
        {
            edits.Add(new RemoveItemCommand(path));
        }

        foreach (PathItem result in results)
        {
            edits.Add(new AddItemCommand(layer, result));
        }

        if (edits.Count > 0)
        {
            // One action, however many objects were consumed and produced: a divide of six shapes is one
            // thing a person did, and undoing it takes one keystroke.
            Execute(new CompositeCommand(description, edits));
        }

        SelectRange(results.Cast<LayerItem>(), additive: false);

        SetStatus(results.Count == 0
            ? $"{description}: nothing was left"
            : $"{description}: {targets.Count} in, {results.Count} out, " +
              $"{results.Sum(r => r.SubPaths.Count)} outline(s)");

        return new PathBooleanResult(
            description, targets.Count, results.Count, results.Sum(r => r.SubPaths.Count));
    }
    /// <summary>
    /// Expands the selected objects' strokes into filled outlines: the ink becomes geometry. One undo
    /// step, and the result is selected - a person who has just turned a stroke into a shape wants the
    /// shape, not the shape plus the stroke that made it.
    ///
    /// The geometry is <see cref="StrokeExpander"/>'s, which takes it from <see cref="StrokeOutlineBuilder"/> -
    /// the one plan the canvas, the PDF writer and the SVG writer draw from. A command that works the expansion
    /// out for itself draws a different picture from the window it was run in, which is how a dashed stroke came
    /// out solid here while the canvas drew it dashed.
    ///
    /// A path with no stroke is left alone rather than silently removed: expanding it would produce
    /// nothing, and deleting something because it had nothing to expand would be a surprise.
    /// </summary>
    public int ExpandSelectedStrokes()
    {
        var edits = new List<IUndoableCommand>();
        var expanded = new List<LayerItem>();

        foreach (PathItem path in SelectedPaths().ToList())
        {
            PathItem? outline = StrokeExpander.Expand(path);
            if (outline is null)
            {
                expanded.Add(path);
                continue;
            }

            Layer? layer = LayerOf(path);
            if (layer is null)
            {
                continue;
            }

            edits.Add(new RemoveItemCommand(path));
            edits.Add(new AddItemCommand(layer, outline));
            expanded.Add(outline);
        }

        if (edits.Count == 0)
        {
            SetStatus("Nothing to expand: the selection has no stroke");
            return 0;
        }

        Execute(new CompositeCommand("Expand stroke", edits));
        SelectRange(expanded, additive: false);
        SetStatus($"Expanded {edits.Count / 2} stroke(s) into outlines");
        return edits.Count / 2;
    }

    /// <summary>The layer an item sits on, or null when it is not in the document.</summary>
    private Layer? LayerOf(LayerItem item)
    {
        foreach (Artboard artboard in Document.Artboards)
        {
            foreach (Layer layer in artboard.Layers)
            {
                if (layer.Children.Contains(item))
                {
                    return layer;
                }
            }
        }

        return null;
    }
    /// <summary>
    /// Aligns the selection on the chosen axis. One undo step, and it reports how many objects moved so a
    /// no-op is distinguishable from a failure.
    /// </summary>
    public int AlignSelection(ArrangeAxis axis, ArrangeEdge edge)
        => MoveByDeltas(Arrange.Align(SelectedObjects.ToList(), axis, edge), $"Align {edge}");

    /// <summary>Distributes the selection evenly, in stack order. One undo step.</summary>
    public int DistributeSelection(ArrangeAxis axis, ArrangeAnchor anchor)
        => MoveByDeltas(
            Arrange.Distribute(SelectedObjects.ToList(), axis, anchor),
            anchor == ArrangeAnchor.Start ? "Distribute" : "Distribute from the end");

    /// <summary>
    /// Applies a set of arranged deltas as one undo step.
    ///
    /// Paths have their geometry translated and text has its origin moved, which is how each kind says
    /// where it is. Other kinds - a group, a placed image - are **counted and reported** rather than
    /// silently left behind: arranging half a selection and saying nothing is worse than saying so.
    /// </summary>
    private int MoveByDeltas(IReadOnlyList<(LayerItem Item, Vector2D Delta)> moves, string description)
    {
        var edits = new List<IUndoableCommand>();
        var moved = new HashSet<LayerItem>(ReferenceEqualityComparer.Instance);
        int skipped = 0;

        foreach ((LayerItem item, Vector2D delta) in moves)
        {
            // **A container has no geometry of its own** - it is where its contents are, so arranging one
            // means arranging everything inside it. A group used to fall to the `default` arm and be counted
            // as skipped: selecting a group and aligning it moved nothing and said so, which is the
            // "the contents stay behind" half of the report. It moved nothing at all, which is worse.
            foreach (LayerItem inside in Flatten(item))
            {
                // A selection can hold a group and one of its own children. Moving that child twice would
                // double its travel, so each object is translated once however it was reached.
                if (!moved.Add(inside))
                {
                    continue;
                }

                switch (inside)
                {
                    case PathItem path:
                        PathItem before = path.GeometrySnapshot();
                        path.TranslateGeometryBy(delta);
                        edits.Add(new GeometryReplaceCommand(path, before, path.GeometrySnapshot(), description));
                        break;

                    case TextItem text:
                        Point2D origin = text.Origin;
                        edits.Add(new SetTextOriginCommand(text, origin, origin + delta));
                        break;

                    default:
                        skipped++;
                        break;
                }
            }
        }

        if (edits.Count > 0)
        {
            Execute(new CompositeCommand(description, edits));
        }

        SetStatus(skipped > 0
            ? $"{description}: {edits.Count} moved, {skipped} left alone (not movable yet)"
            : $"{description}: {edits.Count} moved");

        return edits.Count;
    }
    /// <summary>
    /// An object and everything inside it, outermost first. A leaf is itself.
    ///
    /// Arranging works on what has geometry, and a group does not: it is the sum of its contents' geometry, so
    /// moving the group means moving all of them.
    /// </summary>
    private static IEnumerable<LayerItem> Flatten(LayerItem item)
    {
        yield return item;

        if (item is not ArtGroup group)
        {
            yield break;
        }

        foreach (LayerItem child in group.Children)
        {
            foreach (LayerItem nested in Flatten(child))
            {
                yield return nested;
            }
        }
    }

    /// <summary>
    /// Rounds one corner of a path - one undo step. The radius is used unless it is larger than the two
    /// segments meeting at the corner allow, in which case the largest that fits is used and the result
    /// says it was clamped.
    /// </summary>
    public CornerRoundResult RoundCorner(PathItem path, int subPath, int node, double radius)
    {
        PathItem before = path.GeometrySnapshot();
        CornerRoundResult result = CornerRounder.Round(path, subPath, node, radius);

        if (!result.Rounded)
        {
            SetStatus(result.Reason ?? "That corner cannot be rounded");
            return result;
        }

        Execute(new GeometryReplaceCommand(path, before, path.GeometrySnapshot(), "Round corner"));
        SetStatus($"Corner rounded: radius {result.Radius:0.###}" + (result.Clamped ? " (clamped to fit)" : string.Empty));
        return result;
    }

    /// <summary>The corner of the selected path nearest a point, for a drag that starts anywhere near it.</summary>
    public (PathItem Path, int SubPath, int Node, double Distance)? NearestCorner(Point2D point, double within)
    {
        (PathItem Path, int SubPath, int Node, double Distance)? best = null;

        foreach (PathItem path in SelectedPaths())
        {
            for (int s = 0; s < path.SubPaths.Count; s++)
            {
                SubPath sub = path.SubPaths[s];
                int count = sub.Nodes.Count;

                for (int n = 0; n < count; n++)
                {
                    // The ends of an open path are not corners.
                    if (!sub.IsClosed && (n == 0 || n == count - 1))
                    {
                        continue;
                    }

                    // Paths store artboard-local coordinates and the pointer is in document space, so the
                    // anchor is moved into the same space before the distance means anything. Skipping
                    // that does not fail loudly: it either misses the corner or finds the wrong one, and
                    // the radius - measured from a corner in the wrong place - comes out wrong.
                    Point2D anchor = sub.Nodes[n].Anchor + path.ArtboardOffset();
                    double distance = Math.Sqrt(
                        Math.Pow(anchor.X - point.X, 2) + Math.Pow(anchor.Y - point.Y, 2));

                    if (distance <= within && (best is null || distance < best.Value.Distance))
                    {
                        best = (path, s, n, distance);
                    }
                }
            }
        }

        return best;
    }
    /// <summary>
    /// Records a geometry change that has already been applied - a drag previews by mutating and
    /// restoring, so at the end the path is already where it should be and only the undo record is
    /// missing. Without this a drag would have to either push a command per mouse move or leave the
    /// change unrecorded, and an unrecorded change never marks the document modified.
    /// </summary>
    public void CommitGeometry(PathItem path, PathItem before, string description)
    {
        Execute(new GeometryReplaceCommand(path, before, path.GeometrySnapshot(), description));
        SetStatus(description);
    }
    /// <summary>
    /// Draws a freehand stroke: the points the pointer visited, fitted once to cubic segments.
    ///
    /// The points arrive in **document** space, because that is where a pointer lives, and the path's
    /// geometry is artboard-local - so they are moved into the artboard's space by subtracting its offset
    /// before anything is fitted. Fitting in the wrong space gives a stroke of the right shape in the
    /// wrong place, which looks like a drawing bug and is an arithmetic one.
    ///
    /// One undo step. Nothing is drawn for a stroke too short to be one - a click is not a line.
    /// </summary>
    public PathItem? DrawFreehand(IReadOnlyList<Point2D> points, double? tolerance = null)
    {
        FreehandFit fit = FreehandFitter.Fit(points, tolerance);
        if (fit.Nodes.Count < 2)
        {
            SetStatus("Nothing drawn: a stroke needs movement");
            return null;
        }

        (Layer layer, Vector2D offset) = TargetFor(fit.Nodes[0].Anchor);

        var path = new PathItem { Name = "stroke" };
        SubPath sub = path.AddSubPath(closed: false);
        foreach (PathNode node in fit.Nodes)
        {
            sub.Nodes.Add(new PathNode(
                new Point2D(node.Anchor.X - offset.X, node.Anchor.Y - offset.Y),
                new Point2D(node.InHandle.X - offset.X, node.InHandle.Y - offset.Y),
                new Point2D(node.OutHandle.X - offset.X, node.OutHandle.Y - offset.Y)));
        }

        path.Fill = FillSpec.None;
        path.Stroke = new StrokeSpec(true, ColorRgb.Black, 1, StrokeCap.Round, StrokeJoin.Round, 4);
        path.GeometryChanged();

        Execute(new AddItemCommand(layer, path));
        SelectObject(path);
        SetStatus($"Drew a stroke: {fit.Segments} segment(s), within {fit.WorstError:0.###} of the drawn line");
        return path;
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

    /// <summary>
    /// Text items in the selection (including inside selected groups).
    ///
    /// While a block is being edited it is yielded even when the selection no longer holds
    /// it — the person may have clicked away, picked a font, or switched document, and the
    /// text they are typing into is still the text they mean. Without this, styling the
    /// block being edited silently did nothing.
    /// </summary>
    public IEnumerable<TextItem> SelectedTextItems()
    {
        bool any = false;

        foreach (LayerItem item in _selectedObjects)
        {
            switch (item)
            {
                case TextItem text:
                    any = true;
                    yield return text;
                    break;
                case ArtGroup group:
                    foreach (TextItem nested in DescendantTexts(group))
                    {
                        any = true;
                        yield return nested;
                    }

                    break;
            }
        }

        if (!any && EditingText is { } editing)
        {
            yield return editing;
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
        // Start empty. Placing the word "Text" in a new object means the text tool litters
        // the page with placeholder words instead of asking for typing, and every one has
        // to be cleared by hand before it says anything.
        item.Runs.Add(new TextRun { Text = string.Empty, FontFamily = family, FontSize = fontSize });
        Execute(new AddItemCommand(layer, item));
        SelectObject(item);
        SetStatus("Type to enter text");
        return item;
    }

    /// <summary>
    /// Applies paragraph style and orientation to the selected text objects in one undo
    /// step. These are how the block is set — leading, space between paragraphs, and the
    /// angle it sits at — and they never change what the text says.
    /// </summary>
    public void ApplyTextStyle(double? lineSpacing, double? paragraphSpacing,
        double? rotationDegrees, double? frameWidth, TextAlignment? alignment)
    {
        List<TextItem> targets = SelectedTextItems().ToList();
        if (targets.Count == 0)
        {
            SetStatus("Select a text object first");
            return;
        }

        Execute(new TextStyleCommand(targets, lineSpacing, paragraphSpacing,
            rotationDegrees, frameWidth, alignment));
    }

    /// <summary>
    /// Records that the person chose a run's face, rather than the document having asked
    /// for it.
    ///
    /// A run imported from a PDF keeps <see cref="TextRun.SourceFont"/> — the name the
    /// file used — so a font it did not embed can be supplied from the standard chain.
    /// Once someone picks a face, that name no longer describes what should be drawn, and
    /// leaving it in place would quietly override their choice.
    /// </summary>
    private static void ChoseFace(TextRun run, string family)
    {
        run.SourceFont = null;

        // The embedded programme belonged to the face we just replaced.
        run.EmbeddedFont = null;
        run.RawCodes = null;
        run.GlyphIds = null;
        _ = family;
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

            if (IsEditingText && TextSelectionEnd > TextSelectionStart)
            {
                // Style exactly the selected range (rich text).
                TextEditing.ApplyStyle(text, TextSelectionStart, TextSelectionEnd, run =>
                {
                    run.FontFamily = family;
                    ChoseFace(run, family);
                    run.FontSize = fontSize;
                    run.Bold = bold;
                    run.Italic = italic;
                });
            }
            else if (runIndex is { } r && r >= 0 && r < text.Runs.Count)
            {
                // Style just the run under the caret.
                TextRun run = text.Runs[r];
                run.FontFamily = family;
                ChoseFace(run, family);
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
                    ChoseFace(run, family);
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

    /// <summary>
    /// Applies text fields **member by member** to the selected text blocks, where a member that is null is left
    /// exactly as the block has it, and reports how many blocks changed.
    ///
    /// This is the shape a panel editing a mixed selection needs, and the reason <see cref="UpdateSelectedText"/>
    /// could not serve it: that method takes one content string, one colour and one face for the whole selection, so
    /// where two selected blocks hold different words the caller has no value it could pass without writing one
    /// block's text over the other's from a field nobody touched. Naming the members to change is the same judgement
    /// <see cref="ApplyStrokeFieldsAt"/> makes about the fields within a stroke.
    ///
    /// <paramref name="runIndex"/> names the run the face members land on. A block whose run list is shorter is
    /// **skipped** for those members rather than having them clamped onto a run nobody named - the same gap
    /// `StrokeSummary` reports, and the reason it is a gap rather than a disagreement. Null styles every run, which
    /// is the uniform style <see cref="UpdateSelectedText"/> applies. Content and colour belong to the block: the
    /// model holds one string and one colour per block and no per-run colour at all.
    ///
    /// One <see cref="ReplaceTextCommand"/> per block and a composite across the selection, so a gesture is one undo
    /// step; a request that changes nothing adds no command, because an undo step that undoes to exactly where it
    /// started reads as "undo did nothing".
    /// </summary>
    public int ApplyTextFieldsAt(int? runIndex, string? content, string? family, double? fontSize,
        bool? bold, bool? italic, ColorRgb? color)
    {
        var edits = new List<IUndoableCommand>();
        foreach (TextItem text in SelectedTextItems())
        {
            TextItem before = (TextItem)text.Clone();
            bool changed = false;

            if (content is not null && !string.Equals(text.PlainText, content, StringComparison.Ordinal))
            {
                text.PlainText = content;
                changed = true;
            }

            if (color is { } wanted && !SameColor(text.Color, wanted))
            {
                text.Color = wanted;
                changed = true;
            }

            foreach (int r in RunsToStyle(text, runIndex))
            {
                TextRun run = text.Runs[r];
                bool faceChanged = false;

                if (family is { } face && !string.Equals(run.FontFamily, face, StringComparison.Ordinal))
                {
                    run.FontFamily = face;
                    faceChanged = true;
                }

                if (bold is { } weight && run.Bold != weight)
                {
                    run.Bold = weight;
                    faceChanged = true;
                }

                if (italic is { } slant && run.Italic != slant)
                {
                    run.Italic = slant;
                    faceChanged = true;
                }

                // A face the person chose no longer means what the file said: the name the document asked for and
                // the programme it carried both belonged to the face just replaced. A size alone does not replace a
                // face - the programme is scale-independent - so it is not treated as a choice of face.
                if (faceChanged)
                {
                    ChoseFace(run, run.FontFamily);
                }

                if (fontSize is { } size)
                {
                    double clamped = Math.Max(1, size);
                    if (Math.Abs(run.FontSize - clamped) > 1e-9)
                    {
                        run.FontSize = clamped;
                        changed = true;
                    }
                }

                changed |= faceChanged;
            }

            if (changed)
            {
                edits.Add(new ReplaceTextCommand(text, before, (TextItem)text.Clone(), "Edit text"));
            }
        }

        ExecuteIfAny(edits, "Edit text");
        return edits.Count;
    }

    /// <summary>The runs a member-by-member edit names: the one at the index, or every run when none is named.</summary>
    private static IEnumerable<int> RunsToStyle(TextItem text, int? runIndex)
    {
        if (runIndex is not { } index)
        {
            for (int i = 0; i < text.Runs.Count; i++)
            {
                yield return i;
            }

            yield break;
        }

        if (index >= 0 && index < text.Runs.Count)
        {
            yield return index;
        }
    }

    /// <summary>
    /// Distinct colours currently used in the document (fills, strokes and text),
    /// always including black and white — the source for the swatch strip.
    /// </summary>
    public IReadOnlyList<ColorRgb> UsedColors()
    {
        var list = new List<ColorRgb>();

        void Add(ColorRgb c)
        {
            if (!list.Any(x => SameColor(x, c)))
            {
                list.Add(c);
            }
        }

        Add(ColorRgb.White);
        Add(ColorRgb.Black);

        foreach (Artboard artboard in Document.Artboards)
        {
            foreach (Layer layer in artboard.Layers)
            {
                foreach (LayerItem item in layer.Children)
                {
                    CollectColors(item, Add);
                }
            }
        }

        foreach (LayerItem item in Document.Orphans.Children)
        {
            CollectColors(item, Add);
        }

        return list;
    }

    private static void CollectColors(LayerItem item, Action<ColorRgb> add)
    {
        switch (item)
        {
            case PathItem path:
                if (path.Fill.IsVisible)
                {
                    add(path.Fill.Color);
                }

                if (path.Stroke.IsVisible)
                {
                    add(path.Stroke.Color);
                }

                break;
            case TextItem text:
                add(text.Color);
                break;
            case ArtGroup group:
                foreach (LayerItem child in group.Children)
                {
                    CollectColors(child, add);
                }

                break;
        }
    }

    private static bool SameColor(ColorRgb a, ColorRgb b)
        => (byte)Math.Round(a.R * 255) == (byte)Math.Round(b.R * 255)
           && (byte)Math.Round(a.G * 255) == (byte)Math.Round(b.G * 255)
           && (byte)Math.Round(a.B * 255) == (byte)Math.Round(b.B * 255)
           && (byte)Math.Round(a.A * 255) == (byte)Math.Round(b.A * 255);

    /// <summary>Sets the horizontal alignment of the selected text object(s).</summary>
    public void SetTextAlignment(TextAlignment alignment)
    {
        var edits = new List<IUndoableCommand>();
        foreach (TextItem text in SelectedTextItems())
        {
            TextItem before = (TextItem)text.Clone();
            text.Alignment = alignment;
            edits.Add(new ReplaceTextCommand(text, before, (TextItem)text.Clone(), "Align text"));
        }

        if (edits.Count > 0)
        {
            Execute(edits.Count == 1 ? edits[0] : new CompositeCommand("Align text", edits));
        }
    }

    /// <summary>Moves items to a new container/index (drag-and-drop in the object
    /// list) as one undo step.</summary>
    public void MoveItems(IReadOnlyList<LayerItem> items, IItemContainer target, int index)
    {
        if (items.Count == 0)
        {
            return;
        }

        Execute(new MoveItemsCommand(items, target, index));
        SetStatus("Reordered");
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
        // Every kind of layer item is selectable. This used to accept only paths and
        // groups, which silently dropped text from marquee selections — and from the
        // automation API, where "select this text and edit it" then failed with
        // "no text is selected".
        LayerItem[] materialised = items.ToArray();
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
    /// <summary>
    /// The paths a transform should act on.
    ///
    /// By default a selected group brings its contents with it, which is what a group is for.
    /// <paramref name="ownOnly"/> - the platform modifier - says the group itself and nothing
    /// inside it, so its contents are not expanded here.
    /// </summary>
    public IEnumerable<PathItem> SelectedPaths(bool ownOnly = false)
    {
        foreach (LayerItem item in _selectedObjects)
        {
            switch (item)
            {
                case PathItem path:
                    yield return path;
                    break;
                case ArtGroup group when !ownOnly:
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

        // Images count: without them a picture could be selected but its bounds would
        // read as empty, so the transform pivot and the numeric readout would be wrong.
        foreach (ImageItem image in _selectedObjects.OfType<ImageItem>())
        {
            box = box.Union(image.WorldBounds());
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

        // The target is a **document** point and the node is stored in the path's own frame, so the point
        // is carried into that frame rather than merely having the artboard origin taken off it. The
        // difference is the whole of #172 for this path: inside `translate(50,50) scale(2)` the requested
        // (60,45) landed at (127.5,105) - the group transform applied to the point.
        if (SelectionEngine.FromWorld(p.Path) is not { } fromWorld)
        {
            return;
        }

        Point2D local = fromWorld.Transform(target);
        Vector2D delta = local - node.Anchor;
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
        CurrentFill = FillSpec.Solid(color, rule);
        var edits = SelectedPaths()
            .Select(p => (IUndoableCommand)new SetFillCommand(p, FillSpec.Solid(color, rule), p.Fill))
            .ToList();
        ExecuteIfAny(edits, "Fill");
    }

    /// <summary>Clears the fill of every selected path (one undo step).</summary>
    public void ClearFill()
    {
        var edits = SelectedPaths()
            .Select(p => (IUndoableCommand)new SetFillCommand(p, FillSpec.None, p.Fill))
            .ToList();
        ExecuteIfAny(edits, "Clear fill");
    }

    /// <summary>Clears the stroke of every selected path (one undo step).</summary>
    public void ClearStroke()
    {
        var edits = SelectedPaths()
            .Select(p => (IUndoableCommand)new SetStrokeCommand(p, StrokeSpec.None, p.Stroke))
            .ToList();
        ExecuteIfAny(edits, "Clear stroke");
    }

    /// <summary>
    /// Adds a stroke to every selected path, on top of the ones it has, and reports how many paths changed.
    ///
    /// Here rather than inside the operation, so the appearance panel and a driver run the **same** code. A
    /// capability that exists only inside a control's event handler is a defect in this repository, and the way to
    /// avoid one is for the panel to call this rather than to reimplement it.
    ///
    /// A stroke that is not given is a copy of the path's current top stroke - what pressing add gives a person: a
    /// copy they then edit, which is why the new one is the one selected afterwards.
    /// </summary>
    public int AddStroke(StrokeSpec? stroke = null)
    {
        int changed = 0;
        foreach (PathItem path in SelectedPaths().ToList())
        {
            StrokeSpec top = path.Strokes.Count > 0
                ? path.Strokes[^1]
                : StrokeSpec.Hairline(ColorRgb.Black);

            var stack = path.Strokes.ToList();
            stack.Add(stroke ?? top);
            Execute(new SetStrokesCommand(path, stack, "Add stroke"));
            changed++;
        }

        return changed;
    }

    /// <summary>
    /// Removes a stroke from every selected path. The index counts from the bottom and defaults to the top one.
    ///
    /// A path left with none gets a single invisible stroke, because a path with no strokes is not a state the
    /// model has - and the panel wants a row to fall back to rather than an empty list.
    /// </summary>
    public int RemoveStroke(int? index = null)
    {
        int changed = 0;
        foreach (PathItem path in SelectedPaths().ToList())
        {
            if (path.Strokes.Count == 0)
            {
                continue;
            }

            int at = index ?? path.Strokes.Count - 1;
            if (at < 0 || at >= path.Strokes.Count)
            {
                continue;
            }

            var stack = path.Strokes.ToList();
            stack.RemoveAt(at);
            Execute(new SetStrokesCommand(path, stack, "Remove stroke"));
            changed++;
        }

        return changed;
    }

    /// <summary>
    /// Shows or hides one stroke of every selected path, which is what a row's toggle does.
    ///
    /// A hidden stroke keeps everything else about it - width, caps, joins, miter limit, dash - because it is a
    /// member somebody is about to switch back on rather than a stroke to throw away. Removing and re-adding it
    /// would lose exactly the settings they were working on.
    /// </summary>
    public int SetStrokeVisible(int? index, bool visible)
    {
        int changed = 0;
        foreach (PathItem path in SelectedPaths().ToList())
        {
            int at = index ?? path.Strokes.Count - 1;
            if (at < 0 || at >= path.Strokes.Count)
            {
                continue;
            }

            var stack = path.Strokes.ToList();
            if (stack[at].IsVisible == visible)
            {
                continue;
            }

            stack[at] = stack[at] with { IsVisible = visible };
            Execute(new SetStrokesCommand(path, stack, visible ? "Show stroke" : "Hide stroke"));
            changed++;
        }

        return changed;
    }

    /// <summary>
    /// Adds an outline effect to the selected paths' strokes, and reports how many paths changed.
    ///
    /// Here rather than inside the operation, so the stroke pane and a driver run the **same** code - the shape
    /// `AddStroke` already has. A capability that exists only inside a control's event handler is a defect in this
    /// repository, and the way to avoid one is for the panel to call this rather than to reimplement it.
    ///
    /// `strokeIndex` names one member of the stack, counted from the bottom, and is what a panel showing "stroke 2
    /// of 3" passes so that the effect lands on the stroke it is describing. Naming none keeps the old walk over
    /// the whole stack, so a caller that was working is unaffected. A path whose stack is shorter than the index is
    /// **skipped**, the same gap `StrokeSummary` reports, rather than clamped onto a stroke nobody named.
    /// </summary>
    public int AddOutlineEffect(OutlineEffectSpec effect, int? strokeIndex = null)
    {
        int changed = 0;
        foreach (PathItem path in SelectedPaths().ToList())
        {
            if (strokeIndex is not null && (strokeIndex < 0 || strokeIndex >= path.Strokes.Count))
            {
                continue;
            }

            var stack = path.Strokes.ToList();
            for (int i = 0; i < stack.Count; i++)
            {
                if (strokeIndex is not null && i != strokeIndex)
                {
                    continue;
                }

                stack[i] = stack[i] with
                {
                    Effects = new EffectStack(stack[i].AllEffects.Concat(new[] { effect })),
                };
            }

            Execute(new SetStrokesCommand(path, stack, "Add stroke effect"));
            changed++;
        }

        return changed;
    }

    /// <summary>The same for a raster effect, which is a separate list on the same stroke.</summary>
    public int AddRasterEffect(RasterEffectSpec effect, int? strokeIndex = null)
    {
        int changed = 0;
        foreach (PathItem path in SelectedPaths().ToList())
        {
            if (strokeIndex is not null && (strokeIndex < 0 || strokeIndex >= path.Strokes.Count))
            {
                continue;
            }

            var stack = path.Strokes.ToList();
            for (int i = 0; i < stack.Count; i++)
            {
                if (strokeIndex is not null && i != strokeIndex)
                {
                    continue;
                }

                stack[i] = stack[i] with
                {
                    RasterEffects = new RasterEffectStack(stack[i].AllRasterEffects.Concat(new[] { effect })),
                };
            }

            Execute(new SetStrokesCommand(path, stack, "Add raster effect"));
            changed++;
        }

        return changed;
    }

    /// <summary>
    /// The current value of one effect parameter, by the name the registry declares it under, or null when the
    /// effect does not take it.
    ///
    /// The counterpart of <see cref="SetEffectParameter"/>, and deliberately in the same file: between them they are
    /// the whole of what a panel needs to show a control per declared parameter and write it back, and the names
    /// they answer to are the registry's rather than the panel's. Colours are not here, for the same reason they
    /// are not in the setter.
    ///
    /// `strokeIndex` names the stack member to read, so a panel describing "stroke 2 of 3" does not show stroke 1's
    /// value and then write it to stroke 2 - the same disagreement the setter's index exists to remove. Naming none
    /// keeps the old first-match walk.
    /// </summary>
    public double? EffectParameterValue(bool raster, int index, string name, int? strokeIndex = null)
    {
        foreach (PathItem path in SelectedPaths())
        {
            for (int i = 0; i < path.Strokes.Count; i++)
            {
                if (strokeIndex is not null && i != strokeIndex)
                {
                    continue;
                }

                StrokeSpec stroke = path.Strokes[i];
                if (raster)
                {
                    if (stroke.AllRasterEffects is not { } effects || index < 0 || index >= effects.Count)
                    {
                        continue;
                    }

                    RasterEffectSpec effect = effects[index];
                    return name switch
                    {
                        "radius" => effect.Radius,
                        "offsetX" => effect.OffsetX,
                        "offsetY" => effect.OffsetY,
                        "opacity" => effect.Opacity,
                        _ => null,
                    };
                }
                else
                {
                    var effects = stroke.AllEffects.ToList();
                    if (index < 0 || index >= effects.Count)
                    {
                        continue;
                    }

                    OutlineEffectSpec effect = effects[index];
                    return name switch
                    {
                        "size" => effect.Size,
                        "detail" => effect.Detail,
                        "seed" => effect.Seed,
                        "ridges" => effect.Ridges,
                        "smooth" => effect.Smooth ? 1.0 : 0.0,
                        "join" => (double)(int)effect.Join,
                        "density" => effect.Density,
                        "overlap" => effect.Overlap,
                        "width" => effect.Width,
                        "curviness" => effect.Curviness,
                        "scatter" => effect.Scatter,
                        _ => null,
                    };
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Sets one parameter of one effect, by the name the **registry** declares it under.
    ///
    /// The name being the registry's is what lets a panel build its editors from the declaration rather than from a
    /// switch: the panel asks what an effect takes, shows a control per parameter, and calls this with the name it
    /// was given. A name the effect does not take is refused rather than guessed at - the alternative is a typo
    /// silently setting something else.
    ///
    /// Colours are not settable here: they are not a number, and a signature that took a string would be guessing
    /// at what kind of value it was handed. `tint` is on the list of things this does not yet cover.
    ///
    /// `strokeIndex` names one member of the stack, counted from the bottom, and is what a panel describing "stroke
    /// 2 of 3" passes so the edit lands where it says. Naming none keeps the old walk, which sets the first stroke
    /// that has the effect. A path whose stack is shorter than the index is skipped, as a gap.
    /// </summary>
    public int SetEffectParameter(bool raster, int index, string name, double value, int? strokeIndex = null)
    {
        int changed = 0;
        foreach (PathItem path in SelectedPaths().ToList())
        {
            if (strokeIndex is not null && (strokeIndex < 0 || strokeIndex >= path.Strokes.Count))
            {
                continue;
            }

            var stack = path.Strokes.ToList();
            for (int i = 0; i < stack.Count; i++)
            {
                if (strokeIndex is not null && i != strokeIndex)
                {
                    continue;
                }

                if (raster)
                {
                    if (stack[i].AllRasterEffects is not { } effects || index < 0 || index >= effects.Count)
                    {
                        continue;
                    }

                    RasterEffectSpec effect = effects[index];
                    RasterEffectSpec? updated = name switch
                    {
                        "radius" => effect with { Radius = value },
                        "offsetX" => effect with { OffsetX = value },
                        "offsetY" => effect with { OffsetY = value },
                        "opacity" => effect with { Opacity = Math.Clamp(value, 0.0, 1.0) },
                        _ => null,
                    };

                    if (updated is null)
                    {
                        continue;
                    }

                    var rebuilt = new List<RasterEffectSpec>(effects);
                    rebuilt[index] = updated;
                    stack[i] = stack[i] with { RasterEffects = new RasterEffectStack(rebuilt) };
                }
                else
                {
                    var effects = stack[i].AllEffects.ToList();
                    if (index < 0 || index >= effects.Count)
                    {
                        continue;
                    }

                    OutlineEffectSpec effect = effects[index];
                    OutlineEffectSpec? updated = name switch
                    {
                        "size" => effect with { Size = value },
                        "detail" => effect with { Detail = Math.Max(1.0, value) },
                        "seed" => effect with { Seed = (int)value },
                        "ridges" => effect with { Ridges = Math.Max(1, (int)Math.Round(value)) },

                        // `smooth` and `join` are choices rather than measurements, so they arrive as the whole
                        // number a panel has to offer for them today: zero or one, and the three joins in order.
                        "smooth" => effect with { Smooth = value >= 0.5 },
                        "join" => effect with { Join = (OutlineJoin)Math.Clamp((int)Math.Round(value), 0, 2) },

                        "density" => effect with { Density = Math.Max(1.0, Math.Round(value)) },
                        "overlap" => effect with { Overlap = Math.Max(0.0, value) },
                        "width" => effect with { Width = Math.Max(0.0, value) },
                        "curviness" => effect with { Curviness = Math.Max(0.0, value) },
                        "scatter" => effect with { Scatter = Math.Max(0.0, value) },
                        _ => null,
                    };

                    if (updated is null)
                    {
                        continue;
                    }

                    effects[index] = updated;
                    stack[i] = stack[i] with { Effects = new EffectStack(effects) };
                }

                Execute(new SetStrokesCommand(path, stack, "Set effect parameter"));
                changed++;
                break;
            }
        }

        return changed;
    }

    /// <summary>
    /// Removes one **outline** effect from every selected path's strokes, counted from the start of the effect list.
    ///
    /// The two families are separate lists, so the caller says which one rather than this searching both: an index
    /// into the raster effects and an index into the outline effects name different effects, and guessing which was
    /// meant would remove the wrong one. Mirrors <see cref="MoveStrokeEffect"/>, which has the same shape.
    ///
    /// `strokeIndex` names one member of the stack, and is what a panel describing "stroke 2 of 3" passes so it
    /// removes from that stroke rather than from the first one that happens to carry the effect. Naming none keeps
    /// the old first-match search.
    /// </summary>
    public int RemoveStrokeEffect(int index, int? strokeIndex = null)
    {
        int changed = 0;
        foreach (PathItem path in SelectedPaths().ToList())
        {
            if (strokeIndex is not null && (strokeIndex < 0 || strokeIndex >= path.Strokes.Count))
            {
                continue;
            }

            var stack = path.Strokes.ToList();
            for (int i = 0; i < stack.Count; i++)
            {
                if (strokeIndex is not null && i != strokeIndex)
                {
                    continue;
                }

                var effects = stack[i].AllEffects.ToList();
                if (index < 0 || index >= effects.Count)
                {
                    continue;
                }

                effects.RemoveAt(index);
                stack[i] = stack[i] with { Effects = new EffectStack(effects) };
                Execute(new SetStrokesCommand(path, stack, "Remove effect"));
                changed++;
                break;
            }
        }

        return changed;
    }

    /// <summary>The same for a raster effect, which is the other list.</summary>
    public int RemoveStrokeRasterEffect(int index, int? strokeIndex = null)
    {
        int changed = 0;
        foreach (PathItem path in SelectedPaths().ToList())
        {
            if (strokeIndex is not null && (strokeIndex < 0 || strokeIndex >= path.Strokes.Count))
            {
                continue;
            }

            var stack = path.Strokes.ToList();
            for (int i = 0; i < stack.Count; i++)
            {
                if (strokeIndex is not null && i != strokeIndex)
                {
                    continue;
                }

                if (stack[i].AllRasterEffects is not { } raster || index < 0 || index >= raster.Count)
                {
                    continue;
                }

                var effects = new List<RasterEffectSpec>();
                for (int k = 0; k < raster.Count; k++)
                {
                    effects.Add(raster[k]);
                }

                effects.RemoveAt(index);
                stack[i] = stack[i] with { RasterEffects = new RasterEffectStack(effects) };
                Execute(new SetStrokesCommand(path, stack, "Remove raster effect"));
                changed++;
                break;
            }
        }

        return changed;
    }

    /// <summary>
    /// Moves an outline effect within a stroke's list, which is how a person changes the order they apply in.
    ///
    /// **The order is the picture.** Effects compose in order, so roughen inside an offset does not look like an
    /// offset inside a roughen - which is why this is a list rather than a set, and why moving one is a real edit
    /// rather than a tidy-up. Mirrors <see cref="MoveStroke"/>, because the same thing is true one level down.
    ///
    /// `strokeIndex` names the stack member whose list moves, so a panel describing "stroke 2 of 3" reorders that
    /// stroke rather than the first one with two effects. Naming none keeps the old first-match search.
    /// </summary>
    public int MoveStrokeEffect(int from, int to, int? strokeIndex = null)
    {
        int changed = 0;
        foreach (PathItem path in SelectedPaths().ToList())
        {
            if (strokeIndex is not null && (strokeIndex < 0 || strokeIndex >= path.Strokes.Count))
            {
                continue;
            }

            var stack = path.Strokes.ToList();
            for (int i = 0; i < stack.Count; i++)
            {
                if (strokeIndex is not null && i != strokeIndex)
                {
                    continue;
                }

                var effects = stack[i].AllEffects.ToList();
                if (from < 0 || from >= effects.Count)
                {
                    continue;
                }

                OutlineEffectSpec moved = effects[from];
                effects.RemoveAt(from);
                effects.Insert(Math.Clamp(to, 0, effects.Count), moved);

                stack[i] = stack[i] with { Effects = new EffectStack(effects) };
                Execute(new SetStrokesCommand(path, stack, "Reorder effect"));
                changed++;
                break;
            }
        }

        return changed;
    }

    /// <summary>The same for a stroke's raster effects, which are a separate list and ordered for the same reason.</summary>
    public int MoveStrokeRasterEffect(int from, int to, int? strokeIndex = null)
    {
        int changed = 0;
        foreach (PathItem path in SelectedPaths().ToList())
        {
            if (strokeIndex is not null && (strokeIndex < 0 || strokeIndex >= path.Strokes.Count))
            {
                continue;
            }

            var stack = path.Strokes.ToList();
            for (int i = 0; i < stack.Count; i++)
            {
                if (strokeIndex is not null && i != strokeIndex)
                {
                    continue;
                }

                if (stack[i].RasterEffects is not { } raster || from < 0 || from >= raster.Count)
                {
                    continue;
                }

                var effects = new List<RasterEffectSpec>();
                for (int k = 0; k < raster.Count; k++)
                {
                    effects.Add(raster[k]);
                }

                RasterEffectSpec moved = effects[from];
                effects.RemoveAt(from);
                effects.Insert(Math.Clamp(to, 0, effects.Count), moved);

                stack[i] = stack[i] with { RasterEffects = new RasterEffectStack(effects) };
                Execute(new SetStrokesCommand(path, stack, "Reorder raster effect"));
                changed++;
                break;
            }
        }

        return changed;
    }

    /// <summary>Moves a stroke within the stack, which is how a person changes which one is on top.</summary>
    public int MoveStroke(int from, int to)
    {
        int changed = 0;
        foreach (PathItem path in SelectedPaths().ToList())
        {
            if (from < 0 || from >= path.Strokes.Count)
            {
                continue;
            }

            var stack = path.Strokes.ToList();
            StrokeSpec moved = stack[from];
            stack.RemoveAt(from);
            stack.Insert(Math.Clamp(to, 0, stack.Count), moved);
            Execute(new SetStrokesCommand(path, stack, "Reorder stroke"));
            changed++;
        }

        return changed;
    }

    /// <summary>Applies a stroke colour to every selected path, keeping each path's
    /// existing width/caps/joins.</summary>
    public void ApplyStrokeColor(ColorRgb color)
    {
        CurrentStroke = new StrokeSpec(true, color,
            CurrentStroke.Width > 0 ? CurrentStroke.Width : 1.0,
            CurrentStroke.Cap, CurrentStroke.Join, CurrentStroke.MiterLimit, CurrentStroke.Alignment,
            CurrentStroke.Dash);
        var edits = SelectedPaths().Select(p =>
        {
            double width = p.Stroke.Width > 0 ? p.Stroke.Width : 1.0;
            return (IUndoableCommand)new SetStrokeCommand(p,
                new StrokeSpec(true, color, width, p.Stroke.Cap, p.Stroke.Join, p.Stroke.MiterLimit, p.Stroke.Alignment, p.Stroke.Dash),
                p.Stroke);
        }).ToList();
        ExecuteIfAny(edits, "Stroke colour");
    }

    /// <summary>Applies stroke geometry (width/cap/join/miter) to selected paths,
    /// keeping each path's existing colour.</summary>
    public void ApplyStroke(double width, StrokeCap cap, StrokeJoin join, double miterLimit,
        StrokeAlignment alignment, DashPattern? dash = null, ColorRgb? color = null)
    {
        // The new spec becomes the "current style" for objects drawn next, even
        // when nothing is selected (so setting a width before drawing works).
        ColorRgb baseColor = color ?? (CurrentStroke.IsVisible ? CurrentStroke.Color : ColorRgb.Black);
        DashPattern currentDash = dash ?? CurrentStroke.Dash;
        CurrentStroke = new StrokeSpec(true, baseColor, Math.Max(0, width), cap, join,
            Math.Max(1, miterLimit), alignment, currentDash);

        var edits = SelectedPaths().Select(p =>
        {
            // The colour asked for, or the path's own when none was: this keeps each path's colour rather
            // than forcing black over it.
            ColorRgb existing = color ?? (p.Stroke.IsVisible ? p.Stroke.Color : ColorRgb.Black);
            DashPattern d = dash ?? p.Stroke.Dash;
            return (IUndoableCommand)new SetStrokeCommand(p,
                new StrokeSpec(true, existing, Math.Max(0, width), cap, join, Math.Max(1, miterLimit), alignment, d),
                p.Stroke);
        }).ToList();
        ExecuteIfAny(edits, "Stroke");
    }

    /// <summary>
    /// Applies stroke geometry to **one** stroke of every selected path - the one at <paramref name="index"/>, counted
    /// from the bottom of the stack - and reports how many paths changed.
    ///
    /// The member-wise counterpart of <see cref="ApplyStroke"/>. With the appearance stack there is no such thing as
    /// "the stroke" on a selection, so a panel editing a stroke has to name which one it is describing rather than
    /// overwriting whichever happens to be on top. Here rather than inside the panel, so a person's field and a
    /// driver's operation run the **same** code - the same reason <see cref="AddStroke"/> is here.
    ///
    /// The colour and the width profile are left as the stroke's own unless the caller gives them: this edit is the
    /// geometry a person typed, and
    /// rebuilding the whole spec would silently discard what the stroke otherwise is. `CurrentStroke`, the style
    /// objects drawn next get, is deliberately untouched too - it is "the stroke" rather than a member of somebody's
    /// stack, and there is no honest way to choose which member's geometry should become it.
    ///
    /// One <see cref="SetStrokesCommand"/> per path and a composite across the selection, so a gesture is one undo
    /// step.
    /// </summary>
    public int ApplyStrokeAt(int index, double width, StrokeCap cap, StrokeJoin join, double miterLimit,
        StrokeAlignment alignment, DashPattern? dash = null, ColorRgb? color = null)
        => ApplyStrokeFieldsAt(index, width, cap, join, miterLimit, alignment, dash, color);

    /// <summary>
    /// Applies stroke geometry **member by member** to one stroke of every selected path, where a member that is
    /// null is left exactly as the stroke has it, and reports how many paths changed.
    ///
    /// This is the shape a panel editing a mixed selection needs, and the reason <see cref="ApplyStrokeAt"/> could
    /// not serve it: with widths of 4 and 8 the width field reads **mixed**, and a method that must be given a
    /// `double` can only be handed *some* number - the first path's, or a default - which writes one member's value
    /// over the others' from a field the person never touched. Naming the members to change is the same judgement
    /// the appearance stack already makes about strokes, applied to the fields within one.
    ///
    /// <see cref="ApplyStrokeAt"/> delegates here, so a person's field and a driver's `style.setStroke` run the
    /// **same** code rather than two that agree until somebody changes one of them. A caller that names a stroke
    /// index reaches this method with only the members they gave, so an omitted member is left as the stroke has it
    /// rather than taking the default that means something to the un-indexed call.
    ///
    /// One <see cref="SetStrokesCommand"/> per path and a composite across the selection, so a gesture is one undo
    /// step; a request that changes nothing on a path adds no command at all, because an undo step that undoes to
    /// exactly where it started reads as "undo did nothing".
    /// </summary>
    public int ApplyStrokeFieldsAt(int index, double? width, StrokeCap? cap, StrokeJoin? join, double? miterLimit,
        StrokeAlignment? alignment, DashPattern? dash = null, ColorRgb? color = null)
    {
        if (index < 0)
        {
            return 0;
        }

        var edits = new List<IUndoableCommand>();
        foreach (PathItem path in SelectedPaths().ToList())
        {
            // A path whose stack is shorter simply has no stroke at this index - the same reading `StrokeSummary`
            // takes, where that is a gap in the selection rather than a disagreement about a value.
            if (index >= path.Strokes.Count)
            {
                continue;
            }

            var stack = path.Strokes.ToList();
            StrokeSpec before = stack[index];
            stack[index] = before with
            {
                Width = width is { } w ? Math.Max(0, w) : before.Width,
                Cap = cap ?? before.Cap,
                Join = join ?? before.Join,
                MiterLimit = miterLimit is { } m ? Math.Max(1, m) : before.MiterLimit,
                Alignment = alignment ?? before.Alignment,
                Dash = dash ?? before.Dash,
                Color = color ?? before.Color,
            };

            if (stack[index] == before)
            {
                continue;
            }

            edits.Add(new SetStrokesCommand(path, stack, "Stroke"));
        }

        ExecuteIfAny(edits, "Stroke");
        return edits.Count;
    }

    /// <summary>
    /// Gives **one stroke** of every selected path a width profile, or clears it, and reports how many paths
    /// changed.
    ///
    /// The stroke is named by index for the reason <see cref="ApplyStrokeAt"/> names it: with a stack there is no
    /// such thing as "the stroke" on a selection, and a panel describing stroke 2 of 3 must not give the profile to
    /// strokes 1 and 3. `style.setWidthProfile` reaches this method when it is given a `strokeIndex`, and writes
    /// every stroke itself when it is not - the two behaviours a caller chooses between by naming an index at all.
    ///
    /// Null clears, because that is the state the model uses for "no profile" - an empty profile is the same thing
    /// spelled differently, and the type's own comment says so. The stroke keeps its own width either way: a
    /// profile modulates an ordinary stroke rather than replacing it.
    /// </summary>
    public int SetWidthProfileAt(int index, WidthProfileSpec? profile)
    {
        if (index < 0)
        {
            return 0;
        }

        var edits = new List<IUndoableCommand>();
        foreach (PathItem path in SelectedPaths().ToList())
        {
            if (index >= path.Strokes.Count)
            {
                continue;
            }

            var stack = path.Strokes.ToList();
            StrokeSpec before = stack[index];
            if (before.WidthProfile == profile)
            {
                continue;
            }

            stack[index] = before with { WidthProfile = profile };
            edits.Add(new SetStrokesCommand(path, stack, profile is null ? "Clear width profile" : "Width profile"));
        }

        ExecuteIfAny(edits, "Width profile");
        return edits.Count;
    }

    /// <summary>
    /// Sets one target of the tablet response on **one stroke** of every selected path, leaving every other target
    /// as it was, and reports how many paths changed.
    ///
    /// The other targets are carried over rather than rebuilt, because turning width dynamics on must not switch
    /// opacity dynamics off - the same judgement `style.setDynamics` makes when it walks the target list, which is
    /// also why an untouched target is left alone rather than compared.
    ///
    /// A path already carrying exactly this response is skipped, so a panel refresh cannot put an empty undo step on
    /// the stack. `style.setDynamics` reaches this method when it is given a `strokeIndex`, and writes every stroke
    /// itself when it is not.
    /// </summary>
    public int SetDynamicsAt(int index, DynamicsTarget target, bool enabled, DynamicsCurve curve)
    {
        if (index < 0)
        {
            return 0;
        }

        var edits = new List<IUndoableCommand>();
        foreach (PathItem path in SelectedPaths().ToList())
        {
            if (index >= path.Strokes.Count)
            {
                continue;
            }

            var stack = path.Strokes.ToList();
            StrokeSpec before = stack[index];

            var wanted = new DynamicsTargetSpec(enabled, curve);
            if ((before.Dynamics?.For(target) ?? DynamicsTargetSpec.Off) == wanted)
            {
                continue;
            }

            var spec = new DynamicsSpec(Enum.GetValues<DynamicsTarget>().Select(existing =>
                existing == target ? wanted : before.Dynamics?.For(existing) ?? DynamicsTargetSpec.Off));

            stack[index] = before with { Dynamics = spec };
            edits.Add(new SetStrokesCommand(path, stack, "Tablet dynamics"));
        }

        ExecuteIfAny(edits, "Tablet dynamics");
        return edits.Count;
    }

    /// <summary>
    /// Removes the tablet response from **one stroke** of every selected path, and reports how many paths changed.
    ///
    /// Null rather than a spec with every target off: the model's own comment says the two are different - null
    /// stores nothing, an all-off spec stores a decision - and clearing a response removes it rather than recording
    /// that it was switched off. The indexed half of `style.clearDynamics`, for the reason
    /// <see cref="SetDynamicsAt"/> is the indexed half of `style.setDynamics`: the stroke a panel describes is one
    /// member of the stack, and clearing every member instead is the opposite of naming one.
    /// </summary>
    public int ClearDynamicsAt(int index)
    {
        if (index < 0)
        {
            return 0;
        }

        var edits = new List<IUndoableCommand>();
        foreach (PathItem path in SelectedPaths().ToList())
        {
            if (index >= path.Strokes.Count)
            {
                continue;
            }

            var stack = path.Strokes.ToList();
            StrokeSpec before = stack[index];
            if (before.Dynamics is null)
            {
                continue;
            }

            stack[index] = before with { Dynamics = null };
            edits.Add(new SetStrokesCommand(path, stack, "Clear tablet dynamics"));
        }

        ExecuteIfAny(edits, "Clear tablet dynamics");
        return edits.Count;
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
    /// selected, no point mode). Text blocks count: they have bounds, move, and
    /// scale with the same handles as paths.</summary>
    public bool HasTransformableSelection
        => !HasPointSelection &&
           (SelectedPaths().Any() || SelectedTextItems().Any() ||
            _selectedObjects.OfType<ImageItem>().Any());

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
    /// <summary>What travels with an object when it is scaled. See <see cref="ScaleWithObject"/>.</summary>
    public ScaleWithObject ScaleOptions { get; } = new();

    public void ApplyTransform(
        Point2D pivot, Vector2D translation, double scaleX, double scaleY, double rotationDegrees,
        bool ownOnly = false)
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
        foreach (PathItem path in SelectedPaths(ownOnly))
        {
            PathItem before = path.GeometrySnapshot();

            // Paths store artboard-local coordinates; convert the world pivot.
            Point2D localPivot = pivot - path.ArtboardOffset();

            if (anyTranslation)
            {
                // The translation arrives in **world** coordinates and the geometry it moves is stored in
                // this path's own placement frame, so the delta is carried into that frame first - the
                // same composition the drag gesture uses (#165), stated once in SelectionEngine. Adding
                // the world delta to the stored coordinates moved the artwork by the group transform
                // applied to the delta: inside `scale(2)` a scripted 30 pt move became 45 (#172).
                path.TranslateGeometryBy(SelectionEngine.DeltaInItem(path, translation));
            }

            double? strokeBefore = null;
            if (anyScale)
            {
                path.ScaleGeometryAbout(localPivot, scaleX, scaleY);

                // A stroke width is a distance in the document, so leaving it fixed makes a scaled drawing
                // disagree with its own geometry: a piece scaled up keeps a hairline, one scaled down turns
                // into a smear. The undo is the same step as the geometry, because a person who scales
                // something twice and undoes once means both halves of it.
                double measurement = ScaleWithObject.MeasurementFactor(scaleX, scaleY);
                if (ScaleOptions.LineWeights && path.Stroke.HasVisibleOutline &&
                    Math.Abs(measurement - 1.0) > 1e-9 && path.Stroke.Width > 0)
                {
                    strokeBefore = path.Stroke.Width;
                    edits.Add(new SetStrokeWidthCommand(path, strokeBefore.Value, strokeBefore.Value * measurement));
                }
            }

            if (anyRotation)
            {
                path.RotateGeometryAbout(localPivot, rotationDegrees * Math.PI / 180.0);
            }

            edits.Add(new GeometryReplaceCommand(path, before, path.GeometrySnapshot()));
        }

        // **Text has no outline of its own**, so it was not transformed here at all: its geometry is where
        // its origin is and how big its type is. A scale moves the origin, and - when the option is on -
        // takes the type with it, which is the difference between enlarging a label and reflowing a frame.
        foreach (TextItem text in _selectedObjects.OfType<TextItem>())
        {
            Point2D localPivot = pivot - text.ArtboardOffset();
            Point2D origin = text.Origin;

            // The translation is in world coordinates and the origin is stored in the block's own frame,
            // so it is carried across in the same step as the scale (#172) - the drag path does exactly
            // this (#165), and an operation and a gesture must not disagree about a frame.
            Vector2D carried = SelectionEngine.DeltaInItem(text, translation);
            var moved = new Point2D(
                localPivot.X + ((origin.X - localPivot.X) * scaleX) + carried.X,
                localPivot.Y + ((origin.Y - localPivot.Y) * scaleY) + carried.Y);

            if (anyTranslation || anyScale)
            {
                edits.Add(new SetTextOriginCommand(text, origin, moved));
            }

            double measurement = ScaleWithObject.MeasurementFactor(scaleX, scaleY);
            if (anyScale && ScaleOptions.TextFrameContents && text.Runs.Count > 0 &&
                Math.Abs(measurement - 1.0) > 1e-9)
            {
                edits.Add(new ScaleTextFontCommand(text, measurement));
            }
        }

        if (anyRotation)
        {
            _selectionRotationRadians += rotationDegrees * Math.PI / 180.0;
        }

        // An image's geometry is its placement box, so it scales by moving and resizing
        // that box. Rotation is not supported for images yet: the model has no angle for
        // one, and silently ignoring the request would be worse than saying so.
        foreach (ImageItem image in _selectedObjects.OfType<ImageItem>())
        {
            Rect2D before = image.Placement;
            Point2D localPivot = pivot - image.ArtboardOffset();

            // The placement box is stored in the image's own frame, so the world translation is carried
            // into it, exactly as the drag does (#165, #172).
            Vector2D carried = SelectionEngine.DeltaInItem(image, translation);

            double x = localPivot.X + ((before.X - localPivot.X) * scaleX);
            double y = localPivot.Y + ((before.Y - localPivot.Y) * scaleY);
            double w = before.Width * Math.Abs(scaleX);
            double h = before.Height * Math.Abs(scaleY);

            if (scaleX < 0)
            {
                x -= w;
            }

            if (scaleY < 0)
            {
                y -= h;
            }

            image.Placement = new Rect2D(
                x + carried.X, y + carried.Y, Math.Max(0.5, w), Math.Max(0.5, h));

            edits.Add(new ImagePlacementCommand(image, before, image.Placement));
        }

        Execute(edits.Count == 1
            ? edits[0]
            : new CompositeCommand("Transform objects", edits));
    }

    /// <summary>
    /// Mirrors the selection across its own centre: horizontal swaps left and right, vertical swaps
    /// top and bottom. Flipping twice is the identity, in either order.
    ///
    /// Paths are mirrored as geometry, which is what they are. Text and images cannot be: a run has
    /// to keep its string and its glyph ids, and an image has to keep the file's own samples
    /// (AGENTS.md §9), so both record the mirror as state and the renderer maps through it. That is
    /// also what keeps a flipped block editable - the text editor works in the block's own space,
    /// where nothing has moved.
    /// </summary>
    public void FlipSelection(bool horizontal, bool vertical)
    {
        if ((!horizontal && !vertical) || !HasTransformableSelection)
        {
            return;
        }

        Rect2D box = SelectionBounds();
        if (box.IsEmpty)
        {
            return;
        }

        Point2D centre = new(box.X + (box.Width / 2), box.Y + (box.Height / 2));
        double sx = horizontal ? -1 : 1;
        double sy = vertical ? -1 : 1;

        var edits = new List<IUndoableCommand>();

        foreach (PathItem path in SelectedPaths())
        {
            PathItem before = path.GeometrySnapshot();
            path.ScaleGeometryAbout(centre - path.ArtboardOffset(), sx, sy);
            edits.Add(new GeometryReplaceCommand(path, before, path.GeometrySnapshot()));
        }

        foreach (ImageItem image in _selectedObjects.OfType<ImageItem>())
        {
            edits.Add(new ImageFlipCommand(
                image,
                horizontal ? !image.MirrorX : image.MirrorX,
                vertical ? !image.MirrorY : image.MirrorY));
        }

        foreach (TextItem text in SelectedTextItems())
        {
            TextItem before = (TextItem)text.Clone();
            Point2D local = centre - text.ArtboardOffset();

            // Mirroring about the same centre would leave the origin where it was, so the block
            // would not move: the origin goes to the other side of the centre, and the mirror
            // state says which way the block then runs.
            if (horizontal)
            {
                text.MirrorX = !text.MirrorX;
                text.Origin = new Point2D((2 * local.X) - text.Origin.X, text.Origin.Y);
            }

            if (vertical)
            {
                text.MirrorY = !text.MirrorY;
                text.Origin = new Point2D(text.Origin.X, (2 * local.Y) - text.Origin.Y);
            }

            edits.Add(new ReplaceTextCommand(text, before, (TextItem)text.Clone(), "Flip text"));
        }

        if (edits.Count == 0)
        {
            return;
        }

        Execute(edits.Count == 1
            ? edits[0]
            : new CompositeCommand(horizontal && vertical ? "Flip both" : horizontal ? "Flip horizontal" : "Flip vertical", edits));
    }


    // ------------------------------------------------------------------
    // Commands / undo / actions
    // ------------------------------------------------------------------

    /// <summary>Whether there is an edit left to undo.</summary>
    public bool CanUndo => _stack.CanUndo;

    /// <summary>Whether an undo left something that can be redone.</summary>
    public bool CanRedo => _stack.CanRedo;

    /// <summary>
    /// How many edits are on the stack.
    ///
    /// The stack keeps its own position rather than exposing itself, and "did that gesture take one step or two"
    /// is a question that has to be answerable from outside - the alternative is counting by undoing, which
    /// answers it by changing the answer.
    /// </summary>
    public int UndoDepth => _stack.Depth;

    public void Execute(IUndoableCommand command)
    {
        _stack.Execute(command);

        // Every undoable edit is an unsaved change until something writes it out.
        IsModified = true;
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
        // New objects inherit the selected object's style (or the last used one).
        if (PrimarySelection is PathItem primary)
        {
            CurrentFill = primary.Fill;
            CurrentStroke = primary.Stroke;
        }

        _selectionRotationRadians = 0;
        PruneSegmentSelection();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        OnPropertyChanged(nameof(SelectedObjects));
        OnPropertyChanged(nameof(PrimarySelection));
        OnPropertyChanged(nameof(HasMultiSelection));
        // Selection is a lightweight change: it raises SelectionChanged only, so
        // the object tree is not rebuilt when the user merely picks something.
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
