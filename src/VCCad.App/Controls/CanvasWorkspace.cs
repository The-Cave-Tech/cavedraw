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
    private Point2D _bendMidpoint;

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

        Point2D handle = new((bounds.Left + bounds.Right) / 2, bounds.Top);
        return model.DistanceTo(handle) <= PickTolerance * 2.0;
    }

    private void SelectPress(Point2D model)
    {
        if (_vm is null)
        {
            return;
        }

        _shiftToggleCandidate = null;
        _selectMoved = false;

        PathItem? hit = HitTestTopPath(model);
        if (_shiftHeld)
        {
            // Shift semantics: clicking an unselected object adds it (so a
            // drag can move the whole selection); clicking an already selected
            // object starts a constrained drag and only removes it if the press
            // turns out to be a plain click (handled on release).
            if (hit is not null && !_vm.IsObjectSelected(hit))
            {
                _vm.ToggleObjectSelection(hit);
            }
            else if (hit is not null)
            {
                _shiftToggleCandidate = hit;
            }

            BeginObjectMoveTargets(model);
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
        if (_dragPaths.Count == 0)
        {
            return;
        }

        // Shift constrains the drag to the dominant axis (Illustrator behaviour):
        // the axis with the largest total displacement wins.
        Vector2D delta = SnapTranslation(model - _dragStartModel);
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

        InvalidateVisual();
    }

    private void SelectRelease(Point2D model)
    {
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

            BeginNodeDrag(pickPath, pick.Value);
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
        if (!_shiftHeld)
        {
            _vm.SelectObject(HitTestTopPath(model));
        }
        else if (segmentPath is not null)
        {
            _vm.ToggleObjectSelection(segmentPath);
        }
    }

    private void BeginNodeDrag(PathItem path, NodePick pick)
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
        _gestureMoved = false;
        InvalidateVisual();
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
        PathNode from = sub.Nodes[a];
        PathNode to = sub.Nodes[b];
        bool straight = from.OutHandle.NearlyEquals(from.Anchor)
                        && to.InHandle.NearlyEquals(to.Anchor);
        _segmentBendMode = straight && a != b;
        _bendMidpoint = new Point2D(
            (_segmentOrigA.A.X + _segmentOrigB.A.X) / 2.0,
            (_segmentOrigA.A.Y + _segmentOrigB.A.Y) / 2.0);

        _dragStartModel = model;
        _gestureMoved = false;
    }

    private void NodeDrag(Point2D model)
    {
        if (_nodePath is not null && _nodeSub is not null)
        {
            // Mutate the grabbed node directly, recomputing from the stored
            // originals — never restoring whole geometry mid-gesture.
            Vector2D delta = model - _nodeAnchorStart;
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
                }
                else
                {
                    node.OutHandle = target;
                }
            }
            else
            {
                node.Anchor = _nodeAnchorStart + delta;
                node.InHandle = _nodeInStart + delta;
                node.OutHandle = _nodeOutStart + delta;
            }

            InvalidateVisual();
            return;
        }

        if (_segmentPath is not null && _segmentNodeA is not null && _segmentNodeB is not null)
        {
            if (_segmentBendMode)
            {
                // Bend: endpoints stay fixed; recompute the two control handles
                // from the original anchors so the curve passes through the
                // pointer. For a symmetric handle offset h, the cubic's midpoint
                // is mid + 3h/4, so h = 4/3 (pointer − mid).
                Vector2D w = _shiftHeld ? SnapTranslation(model - _bendMidpoint) : model - _bendMidpoint;
                if (w.IsZero)
                {
                    return;
                }

                _gestureMoved = true;
                Vector2D h = w * (4.0 / 3.0);
                _segmentNodeA.OutHandle = _segmentOrigA.A + h;
                _segmentNodeB.InHandle = _segmentOrigB.A + h;
                InvalidateVisual();
                return;
            }

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
            SetNodeFromOriginal(_segmentNodeA, _segmentOrigA, delta);
            SetNodeFromOriginal(_segmentNodeB, _segmentOrigB, delta);
            InvalidateVisual();
        }
    }

    private static void SetNodeFromOriginal(PathNode node, (Point2D A, Point2D I, Point2D O) original, Vector2D delta)
    {
        node.Anchor = original.A + delta;
        node.InHandle = original.I + delta;
        node.OutHandle = original.O + delta;
    }

    private void NodeRelease()
    {
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
        foreach (PathItem path in _vm.SelectedPaths())
        {
            bool editNodes = tool == EditorTool.Node;
            bool hasSegments = _vm.SelectedSegments().Any(s => s.Path == path);
            PaintObjectChrome(context, path, showNodes: editNodes, showSegments: editNodes || hasSegments);
        }

        if (_vm.HasSegmentSelection)
        {
            PaintSegmentHighlights(context);
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

    private void PaintObjectChrome(DrawingContext context, PathItem path, bool showNodes, bool showSegments)
    {
        IBrush accent = new SolidColorBrush(Color.FromRgb(0x19, 0x76, 0xD2));
        var pen = new Pen(accent, 1.0);
        double half = Math.Max(2.0 / _layout.Zoom, 0.5);

        Rect2D box = path.BoundingBox();
        if (!box.IsEmpty)
        {
            Point tl = ModelToScreen(new Point2D(box.Left, box.Top));
            var screen = new Rect(tl.X, tl.Y, box.Width * _layout.Zoom, box.Height * _layout.Zoom);
            var selBrush = new SolidColorBrush(Color.FromArgb(45, 0x19, 0x76, 0xD2));
            context.DrawRectangle(selBrush, pen, screen);
        }

        if (!showNodes)
        {
            return;
        }

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

    private void DrawHandleLine(DrawingContext context, Point from, Point to)
    {
        var lineBrush = new SolidColorBrush(Color.FromArgb(210, 0x76, 0x76, 0x76));
        context.DrawLine(new Pen(lineBrush, 1.0), from, to);
    }

    private void PaintSegmentHighlights(DrawingContext context)
    {
        var accent = new SolidColorBrush(Color.FromRgb(0xE6, 0x6A, 0x00));
        var pen = new Pen(accent, Math.Max(2.5, 3.0 * _layout.Zoom));
        var handlePen = new Pen(new SolidColorBrush(Color.FromRgb(0xC6, 0x4A, 0x00)), 1.0);
        double half = Math.Max(2.0 / _layout.Zoom, 0.5);

        foreach ((PathItem path, int sub, int seg) in _vm!.SelectedSegments())
        {
            if (sub >= path.SubPaths.Count || path.SubPaths[sub].SegmentCount <= seg)
            {
                continue;
            }

            SubPath sp = path.SubPaths[sub];
            CubicBezier curve = sp.GetSegment(seg);

            // 1) The segment itself, highlighted.
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

            context.DrawGeometry(null, pen, geometry);

            // 2) For a Bézier segment also reveal its two control handles so the
            //    curve can be reshaped directly (Illustrator behaviour). The start
            //    node's outgoing handle and the end node's incoming handle define
            //    this segment's shape.
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
