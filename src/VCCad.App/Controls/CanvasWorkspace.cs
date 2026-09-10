using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using VCCad.App.ViewModels;
using VCCad.Core.Commands;
using VCCad.Core.Model;
using VCCad.Core.Picking;
using VCCad.Core.Viewport;
using VCCad.Geometry;

namespace VCCad.App.Controls;

/// <summary>
/// The editable pasteboard.
///
/// <b>Select (V)</b> — click picks the top path; Shift adds/toggles objects;
/// drag moves every selected object together (one composite undo step). A
/// rotation handle floats above the selection and drags rotate it about its
/// centre.
///
/// <b>Node / direct selection (A)</b> — anchors and handles of the selected path
/// are shown and draggable; clicking a line/Bézier segment selects that segment
/// (Shift adds), a selected segment can be dragged to slide its two end nodes.
///
/// <b>Pen (P)</b> — click adds corner anchors on the active path, drag pulls the
/// outgoing handle, clicking the start anchor closes the path (which finalises it
/// so the next click starts a fresh path), Enter/Escape or a tool switch
/// finalises.
///
/// <b>Rectangle / Ellipse</b> — drag between two corners to create the shape
/// (closed, four segments; the ellipse is four joined cubics).
///
/// Live drags mutate geometry for instant feedback and commit through
/// <see cref="GeometryReplaceCommand"/> (composed for multi-object gestures) so
/// every gesture is a single undo step. Middle/right drag pans, Ctrl+wheel zooms.
/// </summary>
public sealed class CanvasWorkspace : Control
{
    private CadDocument? _document;
    private PasteboardLayout _layout = new(Rect2D.Empty);
    private Vector2D _offset;
    private bool _isPanning;
    private bool _hasLaidOutOnce;

    private EditorViewModel? _vm;
    private bool _leftDown;
    private bool _shiftHeld;
    private Point2D? _hoverModel;

    // Select-tool bookkeeping.
    private bool _selectMoved;
    private LayerItem? _shiftToggleCandidate;

    // Marquee (rubber-band) selection.
    private bool _marqueeActive;
    private Point2D _marqueeStart;
    private Point2D _marqueeCurrent;

    // Bounding-box resize handles (drag W/H from the selection rectangle).
    private bool _resizeActive;
    private int _resizeHandle;
    private Point2D _resizePivot;
    private Rect2D _resizeRect0;
    private readonly List<PathItem> _resizePaths = new();
    private readonly Dictionary<PathItem, PathItem> _resizeOriginals = new();

    // Whole-object move / rotate targets (Select tool).
    private readonly List<PathItem> _dragPaths = new();
    private readonly Dictionary<PathItem, PathItem> _dragOriginals = new();
    private Point2D _dragStartModel;

    // Node / handle drag (Node tool).
    private PathItem? _nodePath;
    private SubPath? _nodeSub;
    private int _nodeIndex;
    private bool _nodeGrabHandle;
    private bool _nodeIsIn;
    private PathItem? _nodeBefore;
    private Point2D _nodeAnchorStart;
    private Point2D _nodeInStart;
    private Point2D _nodeOutStart;
    private bool _nodeWasSmooth;

    // Colinear handle snap: while dragging a handle near the line through the
    // anchor and the neighbouring segment's control point, arm a snap (feedback
    // = heavier handle). Releasing inside the epsilon snaps the handle to the
    // mirror position so the shared endpoint becomes smooth.
    private bool _handleSnapArmed;
    private Point2D _handleSnapPos;

    // Segment drag (Node tool).
    private PathItem? _segmentPath;
    private SubPath? _segmentSub;
    private int _segmentIndex;
    private PathItem? _segmentBefore;
    private PathNode? _segmentNodeA;
    private PathNode? _segmentNodeB;
    private (Point2D A, Point2D I, Point2D O) _segmentOrigA;
    private (Point2D A, Point2D I, Point2D O) _segmentOrigB;

    // Segment bend mode: dragging a STRAIGHT segment pulls out its (collapsed)
    // control handles so the line bends into a curve that follows the pointer.
    private bool _segmentBendMode;
    private double _bendGrabU;

    // Whether the current gesture actually displaced anything (commit gating).
    private bool _gestureMoved;

    // Rotation handle drag (Select tool).
    private Point2D _rotateCenter;
    private double _rotateStartAngle;
    private readonly List<PathItem> _rotatePaths = new();
    private readonly Dictionary<PathItem, PathItem> _rotateOriginals = new();

    // Shape tools (Rectangle / Ellipse).
    private Point2D? _shapeStart;
    private Point2D _shapeCurrent;

    // Pen tool.
    private PathItem? _penPath;
    private PathNode? _penNode;
    private PathItem? _penBefore;
    private bool _penClosePending;

    public CanvasWorkspace()
    {
        ClipToBounds = true;
        Focusable = true;
    }

    public void AttachEditor(EditorViewModel viewModel)
    {
        _vm = viewModel;
        _document = viewModel.Document;
        _layout = new PasteboardLayout(ComputeExtent());
        viewModel.DocumentChanged += (_, _) =>
        {
            double zoom = _layout.Zoom;
            Vector2D offset = _offset;
            _document = viewModel.Document;
            _layout = new PasteboardLayout(ComputeExtent()) { Zoom = zoom };
            _offset = _layout.ClampTopLeft(offset, ViewportPixels);
            InvalidateVisual();
        };
        viewModel.PropertyChanged += (_, args) =>
        {
            // Leaving the pen tool (toolbar/menu path) must finalise the active
            // pen path even though the tool was changed outside the canvas.
            if (args.PropertyName == nameof(EditorViewModel.Tool)
                && _penPath is not null && viewModel.Tool != EditorTool.Pen)
            {
                FinalizePen();
            }
        };
        InvalidateVisual();
    }

    public double Zoom
    {
        get => _layout.Zoom;
        private set
        {
            _layout.Zoom = value;
            InvalidateVisual();
        }
    }

    private Size2D ViewportPixels => new(Math.Max(Bounds.Width, 1), Math.Max(Bounds.Height, 1));

    public void ZoomIn() => ZoomAtCenter(Zoom * 1.25);
    public void ZoomOut() => ZoomAtCenter(Zoom / 1.25);
    public void ZoomToActualSize() => ZoomAtCenter(1.0);

    public void ZoomToFit()
    {
        _layout.Zoom = _layout.ZoomToFit(ViewportPixels);
        _offset = _layout.CenterInViewport(ViewportPixels);
        InvalidateVisual();
    }

    private void ZoomAtCenter(double factor)
    {
        _layout.Zoom = factor;
        Size2D vp = ViewportPixels;
        Point2D centre = ModelPointAtScreen(new Point(vp.Width / 2, vp.Height / 2));
        _offset = new Vector2D(vp.Width / 2 - (centre.X - _layout.Extent.Left) * _layout.Zoom,
                               vp.Height / 2 - (centre.Y - _layout.Extent.Top) * _layout.Zoom);
        InvalidateVisual();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (!_hasLaidOutOnce && e.NewSize.Width > 10 && e.NewSize.Height > 10 && _document is not null)
        {
            _hasLaidOutOnce = true;
            ZoomToFit();
        }
    }

    // ------------------------------------------------------------------
    // Mapping / picking helpers
    // ------------------------------------------------------------------

    private Point ModelToScreen(Point2D model)
        => new((model.X - _layout.Extent.Left) * _layout.Zoom + _offset.X,
               (model.Y - _layout.Extent.Top) * _layout.Zoom + _offset.Y);

    private Point2D ModelPointAtScreen(Point screen)
        => new((screen.X - _offset.X) / _layout.Zoom + _layout.Extent.Left,
               (screen.Y - _offset.Y) / _layout.Zoom + _layout.Extent.Top);

    private double PickTolerance => Math.Max(0.05, 5.0 / Math.Max(_layout.Zoom, 1e-6));

    private Rect2D ComputeExtent()
    {
        if (_document is null)
        {
            return Rect2D.Empty;
        }

        Rect2D extent = Rect2D.Empty;
        foreach (Artboard artboard in _document.Artboards)
        {
            extent = extent.Union(artboard.Bounds);
            extent = extent.Union(artboard.ArtworkBounds());
        }

        return extent;
    }

    private PathItem? HitTestTopPath(Point2D model)
    {
        if (_document is null)
        {
            return null;
        }

        PathItem? topmost = null;
        double tolerance = PickTolerance;
        foreach (Artboard artboard in _document.Artboards)
        {
            foreach (Layer layer in artboard.Layers)
            {
                if (!layer.IsVisible)
                {
                    continue;
                }

                foreach (LayerItem item in layer.Children)
                {
                    PathItem? hit = HitTestItem(item, model, tolerance);
                    if (hit is not null)
                    {
                        topmost = hit;
                    }
                }
            }
        }

        return topmost;
    }

    private static PathItem? HitTestItem(LayerItem item, Point2D model, double tolerance)
    {
        if (!item.IsVisible)
        {
            return null;
        }

        return item switch
        {
            PathItem path when PathPicking.HitTest(path, model, tolerance) != PickKind.None => path,
            ArtGroup group => HitTestGroup(group, model, tolerance),
            _ => null,
        };
    }

    private static PathItem? HitTestGroup(ArtGroup group, Point2D model, double tolerance)
    {
        PathItem? topmost = null;
        foreach (LayerItem child in group.Children)
        {
            PathItem? hit = HitTestItem(child, model, tolerance);
            if (hit is not null)
            {
                topmost = hit;
            }
        }

        return topmost;
    }

    // ------------------------------------------------------------------
    // Pointer plumbing
    // ------------------------------------------------------------------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        Point position = e.GetPosition(this);
        Point2D model = ModelPointAtScreen(position);
        PointerPointProperties props = e.GetCurrentPoint(this).Properties;
        _shiftHeld = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        if (props.IsMiddleButtonPressed || props.IsRightButtonPressed)
        {
            _lastPan = position;
            _isPanning = true;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        if (!props.IsLeftButtonPressed)
        {
            return;
        }

        _leftDown = true;
        e.Pointer.Capture(this);
        e.Handled = true;

        switch (_vm?.Tool ?? EditorTool.Select)
        {
            case EditorTool.Select: SelectPress(model); break;
            case EditorTool.Node: NodePress(model); break;
            case EditorTool.Pen: PenPress(model); break;
            case EditorTool.Rectangle:
            case EditorTool.Ellipse:
                _shapeStart = model;
                _shapeCurrent = model;
                _vm!.ClearSelection();
                _vm.ClearPointSelection();
                break;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        Point position = e.GetPosition(this);
        Point2D model = ModelPointAtScreen(position);
        _hoverModel = model;
        _shiftHeld = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        if (_isPanning)
        {
            Vector2D delta = new(position.X - _lastPan.X, position.Y - _lastPan.Y);
            _lastPan = position;
            _offset = _layout.ClampTopLeft(_offset + delta, ViewportPixels);
            InvalidateVisual();
            return;
        }

        if (_leftDown)
        {
            switch (_vm?.Tool ?? EditorTool.Select)
            {
                case EditorTool.Select:
                    if (_rotatePaths.Count > 0)
                    {
                        RotateDrag(model);
                    }
                    else
                    {
                        SelectDrag(model);
                    }

                    break;
                case EditorTool.Node: NodeDrag(model); break;
                case EditorTool.Pen: PenDrag(model); break;
                case EditorTool.Rectangle:
                case EditorTool.Ellipse:
                    if (_shapeStart is not null)
                    {
                        _shapeCurrent = model;
                        InvalidateVisual();
                    }

                    break;
            }
        }
        else if (_vm?.Tool == EditorTool.Pen && _penPath is not null)
        {
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        Point2D model = ModelPointAtScreen(e.GetPosition(this));

        if (_isPanning)
        {
            _isPanning = false;
            e.Pointer.Capture(null);
            return;
        }

        if (!_leftDown)
        {
            return;
        }

        _leftDown = false;
        e.Pointer.Capture(null);

        switch (_vm?.Tool ?? EditorTool.Select)
        {
            case EditorTool.Select:
                if (_rotatePaths.Count > 0)
                {
                    EndRotate();
                }
                else
                {
                    SelectRelease(model);
                }

                break;
            case EditorTool.Node: NodeRelease(); break;
            case EditorTool.Pen: PenRelease(model); break;
            case EditorTool.Rectangle: CreateShape(rect: true); break;
            case EditorTool.Ellipse: CreateShape(rect: false); break;
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            Point cursor = e.GetPosition(this);
            Point2D before = ModelPointAtScreen(cursor);
            double factor = e.Delta.Y > 0 ? 1.1 : 1 / 1.1;
            _layout.Zoom *= factor;
            _offset = new Vector2D(
                cursor.X - (before.X - _layout.Extent.Left) * _layout.Zoom,
                cursor.Y - (before.Y - _layout.Extent.Top) * _layout.Zoom);
            _offset = _layout.ClampTopLeft(_offset, ViewportPixels);
        }
        else
        {
            Vector2D delta = new(0, -e.Delta.Y * 60.0);
            _offset = _layout.ClampTopLeft(_offset + delta, ViewportPixels);
        }

        InvalidateVisual();
        e.Handled = true;
    }

    private Point _lastPan;

    // ------------------------------------------------------------------
    // Select tool: multi-object move + rotate handle
    // ------------------------------------------------------------------

    /// <summary>The rotation handle's model position: above the top-centre of the
    /// selection. Both the hit test and the renderer use this one definition.</summary>
    private Point2D RotationHandlePoint(Rect2D bounds)
    {
        Point2D topCentre = CellPoint(bounds, 1);
        double lift = Math.Max(14.0 / _layout.Zoom, 3.0);
        return new Point2D(topCentre.X, topCentre.Y - lift);
    }

    private bool HitRotationHandle(Point2D model)
    {
        if (_vm is null || !_vm.SelectedPaths().Any() || _shiftHeld)
        {
            return false;
        }

        Rect2D bounds = _vm.SelectionBounds();
        if (bounds.IsEmpty)
        {
            return false;
        }

        return model.DistanceTo(RotationHandlePoint(bounds)) <= PickTolerance * 2.5;
    }

    private void SelectPress(Point2D model)
    {
        if (_vm is null)
        {
            return;
        }

        _shiftToggleCandidate = null;
        _selectMoved = false;
        _vm.ClearPointSelection();

        // Rotation handle sits above the selection box.
        if (HitRotationHandle(model))
        {
            BeginRotate(model);
            return;
        }

        // Resize handles on the dashed selection rectangle.
        if (!_shiftHeld && HitResizeHandle(model))
        {
            BeginResize(model);
            return;
        }

        PathItem? hit = HitTestTopPath(model);
        if (_shiftHeld)
        {
            if (hit is null)
            {
                // Shift + drag on empty space: additive marquee.
                BeginMarquee(model);
                return;
            }

            // Shift semantics: clicking an unselected object adds it (so a drag
            // can move the whole selection); clicking an already selected object
            // starts a constrained drag and only removes it if the press turns
            // out to be a plain click (handled on release).
            if (!_vm.IsObjectSelected(hit))
            {
                _vm.ToggleObjectSelection(hit);
            }
            else
            {
                _shiftToggleCandidate = hit;
            }

            BeginObjectMoveTargets(model);
            return;
        }

        if (hit is null)
        {
            // Rubber-band marquee on empty space; the old selection survives
            // until the marquee is released.
            BeginMarquee(model);
            return;
        }

        _vm.SelectObject(hit);
        BeginObjectMoveTargets(model);
        InvalidateVisual();
    }

    private void BeginObjectMoveTargets(Point2D model)
    {
        _dragPaths.Clear();
        _dragOriginals.Clear();
        foreach (PathItem path in _vm!.SelectedPaths())
        {
            _dragPaths.Add(path);
            _dragOriginals[path] = path.GeometrySnapshot();
        }

        _dragStartModel = model;
    }

    private void SelectDrag(Point2D model)
    {
        if (_marqueeActive)
        {
            _marqueeCurrent = model;
            InvalidateVisual();
            return;
        }

        if (_resizeActive)
        {
            ResizeUpdate(model);
            return;
        }

        if (_dragPaths.Count == 0)
        {
            return;
        }

        // Shift constrains the drag to the dominant axis (Illustrator behaviour):
        // the axis with the largest total displacement wins.
        Vector2D raw = model - _dragStartModel;
        Vector2D delta = _shiftHeld ? SnapTranslation(raw) : raw;
        if (delta.IsZero)
        {
            return;
        }

        _selectMoved = true;
        foreach (PathItem path in _dragPaths)
        {
            path.RestoreGeometryFrom(_dragOriginals[path]);
            path.TranslateGeometryBy(delta);
        }

        _vm!.RaiseTransformChanged();
        InvalidateVisual();
    }

    private void SelectRelease(Point2D model)
    {
        if (_marqueeActive)
        {
            FinishMarquee();
            return;
        }

        if (_resizeActive)
        {
            CommitResize();
            return;
        }

        if (_selectMoved)
        {
            CommitMultiPathEdit("Move objects");
        }
        else if (_shiftToggleCandidate is not null && _shiftHeld)
        {
            // A Shift click (no drag) on an already-selected object removes it.
            _vm!.ToggleObjectSelection(_shiftToggleCandidate);
        }

        _dragPaths.Clear();
        _dragOriginals.Clear();
        _shiftToggleCandidate = null;
        _selectMoved = false;
    }

    // ---- marquee ---------------------------------------------------------

    private void BeginMarquee(Point2D model)
    {
        _marqueeActive = true;
        _marqueeStart = model;
        _marqueeCurrent = model;
        InvalidateVisual();
    }

    private void FinishMarquee()
    {
        _marqueeActive = false;
        Rect2D rect = Rect2D.FromPoints(_marqueeStart, _marqueeCurrent);
        if (!rect.IsEmpty)
        {
            List<LayerItem> hits = ItemsIntersectingRect(rect);
            _vm!.SelectRange(hits, additive: _shiftHeld);
        }

        InvalidateVisual();
    }

    private List<LayerItem> ItemsIntersectingRect(Rect2D rect)
    {
        var result = new List<LayerItem>();
        if (_document is null)
        {
            return result;
        }

        foreach (Artboard artboard in _document.Artboards)
        {
            foreach (Layer layer in artboard.Layers)
            {
                if (!layer.IsVisible)
                {
                    continue;
                }

                foreach (LayerItem item in layer.Children)
                {
                    Rect2D bounds = ItemBounds(item);
                    if (!bounds.IsEmpty && bounds.Intersects(rect))
                    {
                        result.Add(item);
                    }
                }
            }
        }

        return result;
    }

    private static Rect2D ItemBounds(LayerItem item)
    {
        switch (item)
        {
            case PathItem path:
                return path.BoundingBox();
            case ArtGroup group:
                return group.Transform.Transform(group.BoundingBox());
            default:
                return Rect2D.Empty;
        }
    }

    // ---- bounding-box resize handles ------------------------------------

    /// <summary>Reference point for a 3×3 cell (row-major 0..8).</summary>
    private Point2D CellPoint(Rect2D bounds, int index)
    {
        double x = (index % 3) switch { 0 => bounds.Left, 1 => bounds.Center.X, _ => bounds.Right };
        double y = (index / 3) switch { 0 => bounds.Top, 1 => bounds.Center.Y, _ => bounds.Bottom };
        return new Point2D(x, y);
    }

    private bool HitResizeHandle(Point2D model)
    {
        if (_vm is null || !_vm.HasTransformableSelection)
        {
            return false;
        }

        Rect2D bounds = _vm.SelectionBounds();
        if (bounds.IsEmpty)
        {
            return false;
        }

        double tol = PickTolerance * 2.0;
        for (int i = 0; i < 9; i++)
        {
            if (i == 4)
            {
                continue; // centre is not a handle
            }

            if (model.DistanceTo(CellPoint(bounds, i)) <= tol)
            {
                _resizeHandle = i;
                return true;
            }
        }

        return false;
    }

    private void BeginResize(Point2D model)
    {
        if (_vm is null)
        {
            return;
        }

        _resizeActive = true;
        _resizeRect0 = _vm.SelectionBounds();
        _resizePivot = CellPoint(_resizeRect0, 8 - _resizeHandle); // opposite cell stays fixed

        _resizePaths.Clear();
        _resizeOriginals.Clear();
        foreach (PathItem path in _vm.SelectedPaths())
        {
            _resizePaths.Add(path);
            _resizeOriginals[path] = path.GeometrySnapshot();
        }

        _dragStartModel = model;
        InvalidateVisual();
    }

    private void ResizeUpdate(Point2D model)
    {
        if (_resizePaths.Count == 0 || _resizeRect0.IsEmpty)
        {
            return;
        }

        // Which side does this handle pull? 1 = right/bottom, −1 = left/top, 0 = fixed.
        int signX = _resizeHandle % 3 == 2 ? 1 : _resizeHandle % 3 == 0 ? -1 : 0;
        int signY = _resizeHandle / 3 == 2 ? 1 : _resizeHandle / 3 == 0 ? -1 : 0;

        double left = _resizeRect0.Left;
        double top = _resizeRect0.Top;
        double right = _resizeRect0.Right;
        double bottom = _resizeRect0.Bottom;

        const double minSize = 0.5;
        if (signX < 0)
        {
            left = Math.Min(model.X, _resizePivot.X - minSize);
        }
        else if (signX > 0)
        {
            right = Math.Max(model.X, _resizePivot.X + minSize);
        }

        if (signY < 0)
        {
            top = Math.Min(model.Y, _resizePivot.Y - minSize);
        }
        else if (signY > 0)
        {
            bottom = Math.Max(model.Y, _resizePivot.Y + minSize);
        }

        double sx = Math.Max(minSize / Math.Max(1e-6, _resizeRect0.Width), (right - left) / _resizeRect0.Width);
        double sy = Math.Max(minSize / Math.Max(1e-6, _resizeRect0.Height), (bottom - top) / _resizeRect0.Height);
        if (_shiftHeld && (signX != 0) && (signY != 0))
        {
            sy = sx; // Shift + corner drag keeps the aspect ratio
        }

        foreach (PathItem path in _resizePaths)
        {
            path.RestoreGeometryFrom(_resizeOriginals[path]);
            path.ScaleGeometryAbout(_resizePivot, sx, sy);
        }

        _vm!.RaiseTransformChanged();
        InvalidateVisual();
    }

    private void CommitResize()
    {
        if (_resizePaths.Count > 0)
        {
            CommitPaths("Resize objects", _resizePaths, _resizeOriginals);
        }

        _resizeActive = false;
        _resizePaths.Clear();
        _resizeOriginals.Clear();
    }

    /// <summary>Locks a translation to the horizontal or vertical axis, whichever
    /// has the greater magnitude — the orthogonal-drag constraint.</summary>
    private static Vector2D SnapTranslation(Vector2D delta)
        => Math.Abs(delta.X) >= Math.Abs(delta.Y)
            ? new Vector2D(delta.X, 0.0)
            : new Vector2D(0.0, delta.Y);

    private void CommitMultiPathEdit(string label)
        => CommitPaths(label, _dragPaths, _dragOriginals);

    private void CommitRotateEdit()
        => CommitPaths("Rotate objects", _rotatePaths, _rotateOriginals);

    private void CommitPaths(string label, IReadOnlyList<PathItem> paths,
        Dictionary<PathItem, PathItem> originals)
    {
        if (_vm is null)
        {
            return;
        }

        var edits = new List<IUndoableCommand>();
        foreach (PathItem path in paths)
        {
            if (originals.TryGetValue(path, out PathItem? before))
            {
                edits.Add(new GeometryReplaceCommand(path, before, path.GeometrySnapshot()));
            }
        }

        if (edits.Count > 0)
        {
            _vm.Execute(edits.Count == 1 ? edits[0] : new CompositeCommand(label, edits));
        }
    }

    private void EndRotate()
    {
        if (_rotatePaths.Count > 0)
        {
            CommitRotateEdit();
        }

        _rotatePaths.Clear();
        _rotateOriginals.Clear();
    }

    private void BeginRotate(Point2D model)
    {
        if (_vm is null)
        {
            return;
        }

        Rect2D bounds = _vm.SelectionBounds();
        _rotateCenter = bounds.Center;
        Vector2D fromCenter = model - _rotateCenter;
        _rotateStartAngle = Math.Atan2(fromCenter.Y, fromCenter.X);

        _rotatePaths.Clear();
        _rotateOriginals.Clear();
        foreach (PathItem path in _vm.SelectedPaths())
        {
            _rotatePaths.Add(path);
            _rotateOriginals[path] = path.GeometrySnapshot();
        }
    }

    private void RotateDrag(Point2D model)
    {
        if (_rotatePaths.Count == 0)
        {
            return;
        }

        Vector2D fromCenter = model - _rotateCenter;
        double angle = Math.Atan2(fromCenter.Y, fromCenter.X) - _rotateStartAngle;
        foreach (PathItem path in _rotatePaths)
        {
            path.RestoreGeometryFrom(_rotateOriginals[path]);
            path.RotateGeometryAbout(_rotateCenter, angle);
        }

        _vm!.RaiseTransformChanged();
        InvalidateVisual();
    }

    // Node tool: nodes, handles and segments
    // ------------------------------------------------------------------

    private void NodePress(Point2D model)
    {
        if (_vm is null)
        {
            return;
        }

        // 1) Grabbing a node or handle takes priority over segment selection.
        PathItem? pickPath = null;
        NodePick? pick = null;
        foreach (PathItem candidate in _vm.SelectedPaths())
        {
            NodePick? candidatePick = PathPicking.PickNode(candidate, model, PickTolerance * 1.6);
            if (candidatePick is not null)
            {
                pick = candidatePick;
                pickPath = candidate;
                break;
            }
        }

        if (pick is null)
        {
            pickPath = HitTestTopPath(model);
            if (pickPath is not null)
            {
                pick = PathPicking.PickNode(pickPath, model, PickTolerance * 1.6);
            }
        }

        if (pick is not null && pickPath is not null)
        {
            if (!_shiftHeld && !_vm.IsObjectSelected(pickPath))
            {
                _vm.SelectObject(pickPath); // select the path whose node we grab
            }

            BeginNodeDrag(pickPath, pick.Value, model);

            // Anchor grabs become "point" selections (position-only editing);
            // handle grabs edit the curve, so they drop any point selection.
            if (pick.Value.IsInHandle || pick.Value.IsOutHandle)
            {
                _vm.ClearPointSelection();
            }
            else
            {
                _vm.SelectPoint(pickPath, pickPath.SubPaths.IndexOf(pick.Value.SubPath), pick.Value.NodeIndex);
            }

            return;
        }

        // 2) Clicking a line/Bézier segment selects it (Shift adds toggles);
        //    the segment can then be dragged to slide its end nodes.
        PathItem? segmentPath = pickPath;
        SegmentPick? segment = segmentPath is null
            ? null
            : PathPicking.ClosestSegment(segmentPath, model, PickTolerance * 1.6);
        if (segment is null)
        {
            segmentPath = HitTestTopPath(model);
            segment = segmentPath is null
                ? null
                : PathPicking.ClosestSegment(segmentPath, model, PickTolerance * 1.6);
        }

        if (segment is not null && segmentPath is not null)
        {
            _vm.ClearPointSelection(); // a segment isn't a point
            int subIndex = segmentPath.SubPaths.IndexOf(segment.Value.SubPath);
            _vm.SelectSegment(segmentPath, subIndex, segment.Value.SegmentIndex, additive: _shiftHeld);
            InvalidateVisual();

            // Drag the segment only when it is still selected after the toggle.
            if (_vm.IsSegmentSelected(segmentPath, subIndex, segment.Value.SegmentIndex))
            {
                BeginSegmentDrag(segmentPath, subIndex, segment.Value.SegmentIndex, model);
            }

            return;
        }

        // 3) Otherwise: focus a whole path or clear the selection.
        _vm.ClearPointSelection();
        if (!_shiftHeld)
        {
            _vm.SelectObject(HitTestTopPath(model));
        }
        else if (segmentPath is not null)
        {
            _vm.ToggleObjectSelection(segmentPath);
        }
    }

    private void BeginNodeDrag(PathItem path, NodePick pick, Point2D pressModel)
    {
        _nodePath = path;
        _nodeSub = pick.SubPath;
        _nodeIndex = pick.NodeIndex;
        _nodeGrabHandle = pick.IsInHandle || pick.IsOutHandle;
        _nodeIsIn = pick.IsInHandle;
        PathNode node = pick.SubPath.Nodes[pick.NodeIndex];
        _nodeAnchorStart = node.Anchor;
        _nodeInStart = node.InHandle;
        _nodeOutStart = node.OutHandle;
        _nodeBefore = path.GeometrySnapshot();
        _nodeWasSmooth = HandlesAntiParallel(_nodeAnchorStart, _nodeInStart, _nodeOutStart);
        _handleSnapArmed = false;
        _dragStartModel = pressModel; // grab point — handle deltas are measured from here
        _gestureMoved = false;
        InvalidateVisual();
    }

    /// <summary>True when a node's two handles are collinear with (and opposite to)
    /// the anchor — the smooth-continuity condition. Dragging such a handle should
    /// keep the opposite handle collinear (symmetric reflection).</summary>
    private static bool HandlesAntiParallel(Point2D anchor, Point2D inHandle, Point2D outHandle)
    {
        Vector2D vi = inHandle - anchor;
        Vector2D vo = outHandle - anchor;
        if (vi.LengthSquared <= 1e-12 || vo.LengthSquared <= 1e-12)
        {
            return false; // a collapsed (corner/straight) handle isn't a curve pair
        }

        double scale = Math.Max(1.0, vi.Length * vo.Length);
        return vi.Dot(vo) < 0 && Math.Abs(vi.Cross(vo)) <= 1e-6 * scale;
    }

    private void BeginSegmentDrag(PathItem path, int subIndex, int segmentIndex, Point2D model)
    {
        SubPath sub = path.SubPaths[subIndex];
        (int a, int b) = sub.SegmentEndNodes(segmentIndex);
        _segmentPath = path;
        _segmentSub = sub;
        _segmentIndex = segmentIndex;
        _segmentBefore = path.GeometrySnapshot();
        _segmentNodeA = sub.Nodes[a];
        _segmentNodeB = sub.Nodes[b];
        _segmentOrigA = (sub.Nodes[a].Anchor, sub.Nodes[a].InHandle, sub.Nodes[a].OutHandle);
        _segmentOrigB = (sub.Nodes[b].Anchor, sub.Nodes[b].InHandle, sub.Nodes[b].OutHandle);

        // A "line" is just a cubic whose two control points sit on the endpoints.
        // Dragging such a straight segment should bend it: pull the collapsed
        // handles out toward the pointer instead of sliding the whole segment.
        // The grab parameter u locates where on the segment the pointer grabbed
        // (projected onto the A-B chord). During the drag we bend the curve so it
        // passes through the pointer at that same u — the endpoints never move.
        Point2D a0 = _segmentOrigA.A;
        Point2D b0 = _segmentOrigB.A;
        Vector2D chord = b0 - a0;
        double u = 0.5;
        if (chord.LengthSquared > 1e-9)
        {
            u = MathUtils.Clamp01((model - a0).Dot(chord) / chord.LengthSquared);
        }

        _segmentBendMode = a != b;
        _bendGrabU = MathUtils.Clamp(u, 0.1, 0.9);
        _dragStartModel = model;
        _gestureMoved = false;
    }

    private void NodeDrag(Point2D model)
    {
        if (_nodePath is not null && _nodeSub is not null)
        {
            // Mutate the grabbed node directly, recomputing from the stored
            // originals — never restoring whole geometry mid-gesture. The delta
            // is measured from the grab point (press position), so a handle
            // follows the cursor 1:1 instead of inheriting a fixed offset.
            Vector2D delta = model - _dragStartModel;
            if (_shiftHeld)
            {
                delta = SnapTranslation(delta);
            }

            if (delta.IsZero)
            {
                return;
            }

            _gestureMoved = true;
            PathNode node = _nodeSub.Nodes[_nodeIndex];
            if (_nodeGrabHandle)
            {
                Point2D target = (_nodeIsIn ? _nodeInStart : _nodeOutStart) + delta;
                if (_nodeIsIn)
                {
                    node.InHandle = target;
                    // Keep the shared endpoint smooth: mirror the opposite handle.
                    if (_nodeWasSmooth)
                    {
                        node.OutHandle = _nodeAnchorStart + (_nodeAnchorStart - target);
                    }
                }
                else
                {
                    node.OutHandle = target;
                    if (_nodeWasSmooth)
                    {
                        node.InHandle = _nodeAnchorStart + (_nodeAnchorStart - target);
                    }
                }

                UpdateHandleSnap(target);
            }
            else
            {
                node.Anchor = _nodeAnchorStart + delta;
                node.InHandle = _nodeInStart + delta;
                node.OutHandle = _nodeOutStart + delta;
            }

            _vm!.RaiseTransformChanged();
            InvalidateVisual();
            return;
        }

        if (_segmentPath is not null && _segmentNodeA is not null && _segmentNodeB is not null)
        {
            if (!_segmentBendMode)
            {
                return;
            }

            // A symmetric handle offset h makes the cubic pass through the point
            // A(1−u) + B·u + h·3u(1−u) at parameter u, so solve for h to make the
            // curve follow the pointer while A and B stay put.
            Point2D a0 = _segmentOrigA.A;
            Point2D b0 = _segmentOrigB.A;
            double u = _bendGrabU;
            double spread = 3.0 * u * (1.0 - u);
            if (spread < 1e-6)
            {
                return;
            }

            Point2D target = _shiftHeld
                ? _dragStartModel + SnapTranslation(model - _dragStartModel)
                : model;
            Point2D baseline = a0 + (b0 - a0) * u;
            Vector2D h = (target - baseline) / spread;
            if (h.LengthSquared < 1e-12)
            {
                return;
            }

            _gestureMoved = true;
            _segmentNodeA.OutHandle = a0 + h;
            _segmentNodeB.InHandle = b0 + h;
            _vm!.RaiseTransformChanged();
            InvalidateVisual();
        }
    }

    private static void SetNodeFromOriginal(PathNode node, (Point2D A, Point2D I, Point2D O) original, Vector2D delta)
    {
        node.Anchor = original.A + delta;
        node.InHandle = original.I + delta;
        node.OutHandle = original.O + delta;
    }

    /// <summary>
    /// Arms a colinear snap when the dragged handle comes within an epsilon of the
    /// line through the anchor and the opposite (neighbouring) control point.
    /// Smooth nodes already mirror their handles, so no snap is needed there; a
    /// collapsed opposite handle offers no line to snap to.
    /// </summary>
    private void UpdateHandleSnap(Point2D draggedTarget)
    {
        _handleSnapArmed = false;
        if (_nodePath is null || _nodeSub is null || _nodeWasSmooth)
        {
            return;
        }

        // The neighbouring segment's control point on the other side of this node.
        Point2D opposite = _nodeIsIn ? _nodeOutStart : _nodeInStart;
        if (opposite.NearlyEquals(_nodeAnchorStart, 1e-6))
        {
            return; // no real neighbouring handle → nothing to be colinear with
        }

        Vector2D line = opposite - _nodeAnchorStart;
        double distance = Math.Abs(line.Cross(draggedTarget - _nodeAnchorStart)) / line.Length;
        if (distance <= PickTolerance)
        {
            // Mirror the opposite handle about the anchor: equal length, exactly
            // colinear — the smooth-node geometry we snap to on release.
            _handleSnapArmed = true;
            _handleSnapPos = _nodeAnchorStart + (_nodeAnchorStart - opposite);
        }
    }

    private void NodeRelease()
    {
        if (_handleSnapArmed && _nodePath is not null && _nodeSub is not null && _nodeGrabHandle)
        {
            // Release inside the snap epsilon: land the handle exactly on the
            // mirrored (colinear, smooth) position before committing.
            PathNode snappedNode = _nodeSub.Nodes[_nodeIndex];
            if (_nodeIsIn)
            {
                snappedNode.InHandle = _handleSnapPos;
            }
            else
            {
                snappedNode.OutHandle = _handleSnapPos;
            }

            InvalidateVisual();
        }

        if (_nodePath is not null && _nodeBefore is not null && _gestureMoved)
        {
            _vm!.Execute(new GeometryReplaceCommand(
                _nodePath, _nodeBefore, _nodePath.GeometrySnapshot(),
                _nodeGrabHandle ? "Edit handle" : "Move node"));
        }

        if (_segmentPath is not null && _segmentBefore is not null && _gestureMoved)
        {
            _vm!.Execute(new GeometryReplaceCommand(
                _segmentPath, _segmentBefore, _segmentPath.GeometrySnapshot(),
                _segmentBendMode ? "Bend segment" : "Move segment"));
        }

        _nodePath = null;
        _nodeSub = null;
        _nodeBefore = null;
        _segmentPath = null;
        _segmentSub = null;
        _segmentBefore = null;
        _segmentNodeA = null;
        _segmentNodeB = null;
        _segmentBendMode = false;
        _handleSnapArmed = false;
        _gestureMoved = false;
    }

    // ------------------------------------------------------------------
    // Pen tool
    // ------------------------------------------------------------------

    private void PenPress(Point2D model)
    {
        if (_vm is null)
        {
            return;
        }

        _vm.ClearPointSelection();

        // Clicking the start anchor of the active path closes (and finalises) it.
        if (_penPath is not null && _penPath.SubPaths[0].Nodes.Count >= 2 &&
            _penPath.SubPaths[0].Nodes[0].Anchor.DistanceTo(model) <= PickTolerance * 2.5)
        {
            _penClosePending = true;
            _penBefore = _penPath.GeometrySnapshot();
            return;
        }

        if (_penPath is null)
        {
            _penPath = new PathItem { Name = "Path", Stroke = StrokeSpec.Hairline(ColorRgb.Black) };
            _penPath.AddSubPath(closed: false);
            _vm.Execute(new AddItemCommand(_vm.TargetLayer(), _penPath));
            _penBefore = _penPath.GeometrySnapshot();
        }
        else
        {
            _penBefore = _penPath.GeometrySnapshot();
        }

        SubPath sub = _penPath.SubPaths[0];
        _penNode = sub.AppendNode(model);
        _dragStartModel = model;
        InvalidateVisual();
    }

    private void PenDrag(Point2D model)
    {
        if (_penNode is null)
        {
            return;
        }

        if ((model - _dragStartModel).IsZero)
        {
            return;
        }

        _penNode.OutHandle = model;
        InvalidateVisual();
    }

    private void PenRelease(Point2D model)
    {
        if (_penClosePending && _penPath is not null)
        {
            _penPath.SubPaths[0].IsClosed = true;
            CommitPenGeometry("Close path");
            FinalizePen(select: true);
            return;
        }

        if (_penPath is not null && _penNode is not null)
        {
            CommitPenGeometry(_penNode.OutHandle != _penNode.Anchor ? "Add curve" : "Add point");
        }

        _penNode = null;
    }

    private void CommitPenGeometry(string description)
    {
        if (_penPath is null || _penBefore is null)
        {
            return;
        }

        PathItem after = _penPath.GeometrySnapshot();
        _vm!.Execute(new GeometryReplaceCommand(_penPath, _penBefore, after, description));
        _penNode = null;
        _penBefore = null;
        _penClosePending = false;
        InvalidateVisual();
    }

    /// <summary>Ends the active pen path. Stray single-anchor paths are removed;
    /// finished paths become the selection so the tree highlights them.</summary>
    private void FinalizePen(bool select = true)
    {
        if (_penPath is null)
        {
            return;
        }

        if (_penPath.SubPaths[0].Nodes.Count < 2)
        {
            _vm!.Execute(new RemoveItemCommand(_penPath));
        }
        else if (select)
        {
            _vm!.SelectObject(_penPath);
        }

        _penPath = null;
        _penNode = null;
        _penBefore = null;
        _penClosePending = false;
        InvalidateVisual();
    }

    // ------------------------------------------------------------------
    // Shape tools
    // ------------------------------------------------------------------

    private void CreateShape(bool rect)
    {
        if (_vm is null || _shapeStart is null)
        {
            _shapeStart = null;
            return;
        }

        Point2D a = _shapeStart.Value;
        Point2D b = _shapeCurrent;
        _shapeStart = null;
        InvalidateVisual();

        if (a.DistanceTo(b) < PickTolerance)
        {
            return; // a click, not a drag — no shape
        }

        Rect2D box = Rect2D.FromPoints(a, b);
        PathItem shape = rect
            ? PathFactory.CreateRectangle("Rectangle", box)
            : PathFactory.CreateEllipse("Ellipse",
                new Point2D((a.X + b.X) / 2, (a.Y + b.Y) / 2),
                Math.Abs(box.Width) / 2,
                Math.Abs(box.Height) / 2);
        shape.Stroke = StrokeSpec.Hairline(ColorRgb.Black);
        shape.Fill = FillSpec.None;

        _vm.Execute(new AddItemCommand(_vm.TargetLayer(), shape));
        _vm.SelectObject(shape);
        _vm.Status = rect ? "Rectangle created" : "Ellipse created";
    }

    // ------------------------------------------------------------------
    // Rendering
    // ------------------------------------------------------------------

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (_document is null)
        {
            return;
        }

        context.FillRectangle(new SolidColorBrush(Colors.White), new Rect(Bounds.Size));

        Rect2D extent = _layout.Extent;
        Point topLeft = ModelToScreen(new Point2D(extent.Left, extent.Top));
        context.FillRectangle(
            new SolidColorBrush(Color.FromArgb(40, 180, 180, 180)),
            new Rect(topLeft.X, topLeft.Y, extent.Width * _layout.Zoom, extent.Height * _layout.Zoom));

        foreach (Artboard artboard in _document.Artboards)
        {
            PaintArtboard(context, artboard);
        }

        PaintOverlays(context);
    }

    private void PaintArtboard(DrawingContext context, Artboard artboard)
    {
        Point p = ModelToScreen(new Point2D(artboard.X, artboard.Y));
        var rect = new Rect(p.X, p.Y, artboard.Width * _layout.Zoom, artboard.Height * _layout.Zoom);
        context.FillRectangle(new SolidColorBrush(Colors.White), rect);
        context.DrawRectangle(new Pen(new SolidColorBrush(Colors.Gray), 1.0), rect);

        foreach (Layer layer in artboard.Layers)
        {
            if (!layer.IsVisible)
            {
                continue;
            }

            foreach (LayerItem item in layer.Children)
            {
                PaintItem(context, item, layer.Opacity);
            }
        }
    }

    private void PaintItem(DrawingContext context, LayerItem item, double opacity)
    {
        switch (item)
        {
            case PathItem path when path.IsVisible:
                PaintPath(context, path, opacity * path.Opacity);
                break;

            case ArtGroup group when group.IsVisible:
                foreach (LayerItem child in group.Children)
                {
                    PaintItem(context, child, opacity * group.Opacity);
                }

                break;
        }
    }

    private void PaintPath(DrawingContext context, PathItem path, double opacity)
    {
        bool anyClosed = path.SubPaths.Any(sp => sp.IsClosed);
        bool fillVisible = path.Fill.IsVisible && anyClosed;
        bool strokeVisible = path.Stroke.HasVisibleOutline;
        if (!fillVisible && !strokeVisible)
        {
            return;
        }

        var geometry = new StreamGeometry();
        using (StreamGeometryContext g = geometry.Open())
        {
            foreach (SubPath sub in path.SubPaths)
            {
                if (sub.Nodes.Count < 2)
                {
                    continue;
                }

                g.BeginFigure(ModelToScreen(sub.Nodes[0].Anchor), sub.IsClosed);
                int segmentCount = sub.SegmentCount;
                for (int i = 0; i < segmentCount; i++)
                {
                    PathNode from = sub.Nodes[i];
                    PathNode to = sub.Nodes[(i + 1) % sub.Nodes.Count];
                    Point fromAnchor = ModelToScreen(from.Anchor);
                    Point p1 = ModelToScreen(from.OutHandle);
                    Point p2 = ModelToScreen(to.InHandle);
                    Point p3 = ModelToScreen(to.Anchor);

                    if (Near(p1, fromAnchor) && Near(p2, p3))
                    {
                        g.LineTo(p3);
                    }
                    else
                    {
                        g.CubicBezierTo(p1, p2, p3);
                    }
                }

                g.EndFigure(sub.IsClosed);
            }
        }

        var fillBrush = ToBrush(path.Fill.Color, opacity);
        var strokePen = new Pen(
            ToBrush(path.Stroke.Color, opacity),
            thickness: Math.Max(0.1, path.Stroke.Width * _layout.Zoom),
            lineCap: ToLineCap(path.Stroke.Cap),
            lineJoin: ToLineJoin(path.Stroke.Join),
            miterLimit: path.Stroke.MiterLimit);

        context.DrawGeometry(fillVisible ? fillBrush : null, strokeVisible ? strokePen : null, geometry);
    }

    /// <summary>Everything drawn above the artwork: selection chrome, node/segment
    /// overlays, shape previews and the pen rubber band.</summary>
    private void PaintOverlays(DrawingContext context)
    {
        if (_vm is null)
        {
            return;
        }

        EditorTool tool = _vm.Tool;

        if (tool == EditorTool.Select && _vm.SelectedPaths().Any())
        {
            Rect2D selection = _vm.SelectionBounds();
            if (!selection.IsEmpty)
            {
                PaintSelectChrome(context, selection);
            }
        }
        else if (tool == EditorTool.Node)
        {
            foreach (PathItem path in _vm.SelectedPaths())
            {
                PaintNodeChrome(context, path);
            }

            if (_handleSnapArmed)
            {
                PaintSnapIndicator(context);
            }
        }

        if (_vm.HasSegmentSelection)
        {
            PaintSegmentHighlights(context);
        }

        if (_marqueeActive)
        {
            PaintMarquee(context);
        }

        if ((tool is EditorTool.Rectangle or EditorTool.Ellipse) && _shapeStart is { } start)
        {
            PaintShapePreview(context, start, _shapeCurrent, rect: tool == EditorTool.Rectangle);
        }

        if (tool == EditorTool.Pen && _penPath is not null && _penPath.SubPaths[0].Nodes.Count > 0)
        {
            PaintPenOverlay(context);
        }
    }

    /// <summary>Selection chrome in the Select tool: a DASHED outline (no fill) with
    /// draggable resize handles and a rotation handle above the top edge.</summary>
    private void PaintSelectChrome(DrawingContext context, Rect2D bounds)
    {
        IBrush accent = new SolidColorBrush(Color.FromRgb(0x19, 0x76, 0xD2));
        var pen = new Pen(accent, 1.0) { DashStyle = new DashStyle(new[] { 4.0, 3.0 }, 0) };
        double half = Math.Max(3.0 / _layout.Zoom, 0.75);

        Point tl = ModelToScreen(new Point2D(bounds.Left, bounds.Top));
        var screen = new Rect(tl.X, tl.Y, bounds.Width * _layout.Zoom, bounds.Height * _layout.Zoom);
        context.DrawRectangle(null, pen, screen);

        // Eight resize handles (all 3×3 cells except the centre).
        var handlePen = new Pen(accent, 1.0);
        for (int i = 0; i < 9; i++)
        {
            if (i == 4)
            {
                continue;
            }

            Point center = ModelToScreen(CellPoint(bounds, i));
            context.DrawRectangle(Brushes.White, handlePen,
                new Rect(center.X - half, center.Y - half, half * 2, half * 2));
        }

        // Rotation handle above the top-centre.
        Point2D top = CellPoint(bounds, 1);
        Point rot = ModelToScreen(RotationHandlePoint(bounds));
        double r = Math.Max(4.0 / _layout.Zoom, 1.5);
        context.DrawLine(handlePen, ModelToScreen(top), rot);
        context.DrawEllipse(Brushes.White, new Pen(accent, 1.2), rot, r, r);
    }

    /// <summary>Node tool chrome: anchors and handles only — no bounding rectangle.</summary>
    private void PaintNodeChrome(DrawingContext context, PathItem path)
    {
        IBrush accent = new SolidColorBrush(Color.FromRgb(0x19, 0x76, 0xD2));
        var pen = new Pen(accent, 1.0);
        double half = Math.Max(2.0 / _layout.Zoom, 0.5);

        foreach (SubPath sub in path.SubPaths)
        {
            foreach (PathNode node in sub.Nodes)
            {
                Point anchor = ModelToScreen(node.Anchor);
                if (!node.HasStraightIncoming)
                {
                    DrawHandleLine(context, anchor, ModelToScreen(node.InHandle));
                }

                if (!node.HasStraightOutgoing)
                {
                    DrawHandleLine(context, anchor, ModelToScreen(node.OutHandle));
                }
            }

            foreach (PathNode node in sub.Nodes)
            {
                Point anchor = ModelToScreen(node.Anchor);
                if (!node.HasStraightIncoming)
                {
                    context.DrawEllipse(Brushes.White, pen, ModelToScreen(node.InHandle), half, half);
                }

                if (!node.HasStraightOutgoing)
                {
                    context.DrawEllipse(Brushes.White, pen, ModelToScreen(node.OutHandle), half, half);
                }

                context.DrawRectangle(Brushes.White, pen,
                    new Rect(anchor.X - half, anchor.Y - half, half * 2, half * 2));
            }
        }
    }

    /// <summary>The heavier-weight feedback on a handle that will snap on release.</summary>
    private void PaintSnapIndicator(DrawingContext context)
    {
        if (_nodePath is null || _nodeSub is null)
        {
            return;
        }

        PathNode node = _nodeSub.Nodes[_nodeIndex];
        Point2D dragged = _nodeIsIn ? node.InHandle : node.OutHandle;
        Point anchor = ModelToScreen(_nodeAnchorStart);
        Point handle = ModelToScreen(dragged);

        IBrush accent = new SolidColorBrush(Color.FromRgb(0x19, 0x76, 0xD2));

        // Faint line from the anchor to the (future) mirrored position.
        context.DrawLine(new Pen(accent, 1.0) { DashStyle = new DashStyle(new[] { 3.0, 3.0 }, 0) },
            anchor, ModelToScreen(_handleSnapPos));

        // Heavier control handle to signal "release to snap".
        double big = Math.Max(3.6 / _layout.Zoom, 1.4);
        var ring = new Pen(new SolidColorBrush(Color.FromRgb(0x0E, 0x5A, 0xA8)), Math.Max(1.8, 2.0 * _layout.Zoom));
        context.DrawEllipse(Brushes.White, ring, handle, big, big);
    }

    private void PaintMarquee(DrawingContext context)
    {
        Rect2D rect = Rect2D.FromPoints(_marqueeStart, _marqueeCurrent);
        Point tl = ModelToScreen(new Point2D(rect.Left, rect.Top));
        var screen = new Rect(tl.X, tl.Y, rect.Width * _layout.Zoom, rect.Height * _layout.Zoom);

        var accent = new SolidColorBrush(Color.FromRgb(0x19, 0x76, 0xD2));
        var pen = new Pen(accent, 1.0) { DashStyle = new DashStyle(new[] { 4.0, 3.0 }, 0) };
        var fill = new SolidColorBrush(Color.FromArgb(18, 0x19, 0x76, 0xD2));
        context.DrawRectangle(fill, pen, screen);
    }

    private void DrawHandleLine(DrawingContext context, Point from, Point to)
    {
        var lineBrush = new SolidColorBrush(Color.FromArgb(210, 0x76, 0x76, 0x76));
        context.DrawLine(new Pen(lineBrush, 1.0), from, to);
    }

    private void PaintSegmentHighlights(DrawingContext context)
    {
        IBrush accent = new SolidColorBrush(Color.FromRgb(0x19, 0x76, 0xD2));
        var handlePen = new Pen(accent, 1.0);
        double half = Math.Max(2.0 / _layout.Zoom, 0.5);

        foreach ((PathItem path, int sub, int seg) in _vm!.SelectedSegments())
        {
            if (sub >= path.SubPaths.Count || path.SubPaths[sub].SegmentCount <= seg)
            {
                continue;
            }

            SubPath sp = path.SubPaths[sub];
            CubicBezier curve = sp.GetSegment(seg);

            // Restroke the segment with ITS OWN colour, just heavier — the pen
            // colour must not change for selected segments.
            IBrush segmentBrush = path.Stroke.HasVisibleOutline
                ? ToBrush(path.Stroke.Color, 1.0)
                : new SolidColorBrush(Color.FromRgb(0x50, 0x50, 0x50));
            double baseWidth = path.Stroke.HasVisibleOutline
                ? Math.Max(0.5, path.Stroke.Width * _layout.Zoom)
                : 1.0;
            var segmentPen = new Pen(segmentBrush, baseWidth + Math.Max(2.5, 2.0 * _layout.Zoom));

            var geometry = new StreamGeometry();
            using (StreamGeometryContext g = geometry.Open())
            {
                Point p0 = ModelToScreen(curve.P0);
                g.BeginFigure(p0, false);
                if (Near(ModelToScreen(curve.P1), p0) && Near(ModelToScreen(curve.P2), ModelToScreen(curve.P3)))
                {
                    g.LineTo(ModelToScreen(curve.P3));
                }
                else
                {
                    g.CubicBezierTo(ModelToScreen(curve.P1), ModelToScreen(curve.P2), ModelToScreen(curve.P3));
                }

                g.EndFigure(false);
            }

            context.DrawGeometry(null, segmentPen, geometry);

            // Control handles of a selected Bézier segment remain visible/editable.
            (int startNode, int endNode) = sp.SegmentEndNodes(seg);
            PathNode start = sp.Nodes[startNode];
            PathNode end = sp.Nodes[endNode];

            Point anchorStart = ModelToScreen(start.Anchor);
            Point anchorEnd = ModelToScreen(end.Anchor);

            if (!start.HasStraightOutgoing)
            {
                Point handle = ModelToScreen(start.OutHandle);
                context.DrawLine(handlePen, anchorStart, handle);
                context.DrawEllipse(Brushes.White, handlePen, handle, half, half);
            }

            if (!end.HasStraightIncoming)
            {
                Point handle = ModelToScreen(end.InHandle);
                context.DrawLine(handlePen, anchorEnd, handle);
                context.DrawEllipse(Brushes.White, handlePen, handle, half, half);
            }
        }
    }

    private void PaintShapePreview(DrawingContext context, Point2D a, Point2D b, bool rect)
    {
        var previewPen = new Pen(new SolidColorBrush(Color.FromRgb(0x19, 0x76, 0xD2)), 1.0);
        previewPen.DashStyle = new DashStyle(new[] { 4.0, 3.0 }, 0);
        Rect2D box = Rect2D.FromPoints(a, b);
        Point tl = ModelToScreen(new Point2D(box.Left, box.Top));
        var screen = new Rect(tl.X, tl.Y, box.Width * _layout.Zoom, box.Height * _layout.Zoom);

        if (rect)
        {
            context.DrawRectangle(null, previewPen, screen);
        }
        else
        {
            context.DrawEllipse(null, previewPen,
                ModelToScreen(new Point2D((a.X + b.X) / 2, (a.Y + b.Y) / 2)),
                Math.Max(0, box.Width / 2 * _layout.Zoom),
                Math.Max(0, box.Height / 2 * _layout.Zoom));
        }
    }

    private void PaintPenOverlay(DrawingContext context)
    {
        SubPath sub = _penPath!.SubPaths[0];
        var previewPen = new Pen(new SolidColorBrush(Color.FromRgb(0x19, 0x76, 0xD2)), 1.0);
        previewPen.DashStyle = new DashStyle(new[] { 4.0, 3.0 }, 0);

        Point last = ModelToScreen(sub.Nodes[^1].Anchor);
        if (_hoverModel is { } hover)
        {
            context.DrawLine(previewPen, last, ModelToScreen(hover));
        }

        if (sub.Nodes.Count >= 2)
        {
            Point start = ModelToScreen(sub.Nodes[0].Anchor);
            double r = Math.Max(4.0 / _layout.Zoom, 1.0);
            context.DrawEllipse(Brushes.White, new Pen(new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28)), 1.0),
                start, r, r);
        }
    }

    // ------------------------------------------------------------------
    // Keyboard
    // ------------------------------------------------------------------

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_vm is null)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.V:
                _vm.Tool = EditorTool.Select;
                FinalizePen(select: false);
                e.Handled = true;
                break;

            case Key.A:
                _vm.Tool = EditorTool.Node;
                FinalizePen(select: false);
                e.Handled = true;
                break;

            case Key.P:
                _vm.Tool = EditorTool.Pen;
                e.Handled = true;
                break;

            case Key.M:
                _vm.Tool = EditorTool.Rectangle;
                FinalizePen();
                e.Handled = true;
                break;

            case Key.L:
                _vm.Tool = EditorTool.Ellipse;
                FinalizePen();
                e.Handled = true;
                break;

            case Key.Delete:
                if (_vm.HasSegmentSelection)
                {
                    _vm.ClearSelection();
                }
                else
                {
                    _vm.DeleteSelection();
                }

                e.Handled = true;
                break;

            case Key.Escape or Key.Enter:
                FinalizePen(select: false);
                e.Handled = true;
                break;
        }
    }

    private static bool Near(Point a, Point b)
        => Math.Abs(a.X - b.X) < 1e-6 && Math.Abs(a.Y - b.Y) < 1e-6;

    private static IBrush ToBrush(ColorRgb color, double opacity)
    {
        byte alpha = (byte)Math.Round(MathUtils.Clamp(opacity, 0.0, 1.0) * 255.0);
        byte r = (byte)Math.Round(MathUtils.Clamp(color.R, 0.0, 1.0) * 255.0);
        byte g = (byte)Math.Round(MathUtils.Clamp(color.G, 0.0, 1.0) * 255.0);
        byte b = (byte)Math.Round(MathUtils.Clamp(color.B, 0.0, 1.0) * 255.0);
        return new SolidColorBrush(new Color(alpha, r, g, b));
    }

    private static PenLineCap ToLineCap(StrokeCap cap) => cap switch
    {
        StrokeCap.Round => PenLineCap.Round,
        StrokeCap.Square => PenLineCap.Square,
        _ => PenLineCap.Flat,
    };

    private static PenLineJoin ToLineJoin(StrokeJoin join) => join switch
    {
        StrokeJoin.Round => PenLineJoin.Round,
        StrokeJoin.Bevel => PenLineJoin.Bevel,
        _ => PenLineJoin.Miter,
    };
}
