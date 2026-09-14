using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using VCCad.App.ViewModels;
using VCCad.Core.Commands;
using VCCad.Core.Model;
using ModelFillRule = VCCad.Core.Model.FillRule;
using MediaFillRule = Avalonia.Media.FillRule;
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
    private double _resizeAngle0;
    private Point2D _resizeCenter0;
    private readonly List<PathItem> _resizePaths = new();
    private readonly Dictionary<PathItem, PathItem> _resizeOriginals = new();
    private readonly List<TextItem> _resizeTexts = new();
    private readonly Dictionary<TextItem, TextItem> _resizeTextBefore = new();

    // Oriented selection chrome: a base (unrotated) rectangle plus an angle, so
    // the selection box rotates with the object instead of turning into a
    // growing axis-aligned bounding box.
    private Rect2D? _chromeRect;
    private double _chromeAngle;
    private Rect2D _chromeRect0; // chrome box captured at the start of a move gesture

    // Whole-object move / rotate targets (Select tool).
    private readonly List<PathItem> _dragPaths = new();
    private readonly Dictionary<PathItem, PathItem> _dragOriginals = new();
    private readonly List<TextItem> _dragTexts = new();
    private readonly Dictionary<TextItem, Point2D> _dragTextOrigins = new();
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
    private Vector2D _segmentOffset;
    private PathNode? _segmentNodeA;
    private PathNode? _segmentNodeB;
    private (Point2D A, Point2D I, Point2D O) _segmentOrigA;
    private (Point2D A, Point2D I, Point2D O) _segmentOrigB;

    // Segment bend mode: dragging a STRAIGHT segment pulls out its (collapsed)
    // control handles so the line bends into a curve that follows the pointer.
    private bool _segmentBendMode;
    private double _bendGrabU;

    // Click (no drag) on an ALREADY-selected segment inserts a new node there.
    private bool _pendingInsertArmed;
    private PathItem? _pendingInsertPath;
    private int _pendingInsertSub;
    private int _pendingInsertSeg;
    private Point2D _pendingInsertPoint;

    // On-canvas text editing.
    private TextItem? _editingText;
    private TextItem? _editBefore;
    private int _caret;

    // Whether the current gesture actually displaced anything (commit gating).
    private bool _gestureMoved;

    // Rotation handle drag (Select tool).
    private Point2D _rotateCenter;
    private double _rotateStartAngle;
    private double _rotateAngle0;
    private readonly List<PathItem> _rotatePaths = new();
    private readonly Dictionary<PathItem, PathItem> _rotateOriginals = new();
    private readonly List<TextItem> _rotateTexts = new();
    private readonly Dictionary<TextItem, TextItem> _rotateTextBefore = new();

    // Shape tools (Rectangle / Ellipse).
    private Point2D? _shapeStart;
    private Point2D _shapeCurrent;

    // Artboard tool gesture state.
    private enum ArtboardGesture { None, Move, Resize, Create }
    private ArtboardGesture _artboardGesture;
    private Artboard? _artboard;
    private Rect2D _artboardBefore;
    private int _artboardHandle;
    private Point2D _artboardCreateStart;
    private Point2D _artboardCreateCurrent;
    private readonly List<(PathItem Path, PathItem Before)> _artboardChildren = new();

    // Pen tool.
    private PathItem? _penPath;
    private Vector2D _penOffset;
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

            // Undo/redo/open/style changes should rebuild the chrome; live
            // gestures manage it themselves and must not lose the orientation.
            if (!AnyGestureActive())
            {
                _chromeRect = null;
                _chromeAngle = 0;
            }

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
        viewModel.SelectionChanged += (_, _) =>
        {
            // A new selection starts with an axis-aligned chrome box.
            _chromeRect = null;
            _chromeAngle = 0;
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

    private IEnumerable<PathItem> AllPaths()
    {
        if (_document is null)
        {
            yield break;
        }

        foreach (Artboard artboard in _document.Artboards)
        {
            foreach (Layer layer in artboard.Layers)
            {
                foreach (LayerItem item in layer.Children)
                {
                    foreach (PathItem p in Flatten(item))
                    {
                        yield return p;
                    }
                }
            }
        }

        foreach (LayerItem item in _document.Orphans.Children)
        {
            foreach (PathItem p in Flatten(item))
            {
                yield return p;
            }
        }
    }

    private static IEnumerable<PathItem> Flatten(LayerItem item)
    {
        switch (item)
        {
            case PathItem path:
                yield return path;
                break;
            case ArtGroup group:
                foreach (LayerItem child in group.Children)
                {
                    foreach (PathItem p in Flatten(child))
                    {
                        yield return p;
                    }
                }

                break;
        }
    }

    /// <summary>Nearest anchor across the document within snap tolerance, in world
    /// space (or null). <paramref name="exclude"/> skips the path being edited.</summary>
    private Point2D? FindSnapAnchor(Point2D world, PathItem? exclude)
    {
        Point2D? best = null;
        double bestDistance = PickTolerance * 1.5;

        foreach (PathItem path in AllPaths())
        {
            if (ReferenceEquals(path, exclude))
            {
                continue;
            }

            Vector2D offset = path.ArtboardOffset();
            foreach (SubPath sub in path.SubPaths)
            {
                foreach (PathNode node in sub.Nodes)
                {
                    Point2D candidate = node.Anchor + offset;
                    double distance = candidate.DistanceTo(world);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = candidate;
                    }
                }
            }
        }

        return best;
    }

    /// <summary>Converts a world point into a path's artboard-local frame.</summary>
    private static Point2D LocalFor(PathItem path, Point2D world) => world - path.ArtboardOffset();

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

    private PathItem? HitTestTopPath(Point2D model) => HitTestTopItem(model) switch
    {
        PathItem path => path,
        ArtGroup group => FirstPath(group),
        _ => null,
    };

    private static PathItem? FirstPath(ArtGroup group)
    {
        foreach (LayerItem child in group.Children)
        {
            if (child is PathItem p)
            {
                return p;
            }

            if (child is ArtGroup nested && FirstPath(nested) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>Top-most container-level item (group or path) under the point.</summary>
    private LayerItem? HitTestTopItem(Point2D model)
    {
        if (_document is null)
        {
            return null;
        }

        LayerItem? topmost = null;
        double tolerance = PickTolerance;
        foreach (Artboard artboard in _document.Artboards)
        {
            if (!artboard.IsVisible)
            {
                continue;
            }

            Point2D local = model - new Vector2D(artboard.X, artboard.Y);
            foreach (Layer layer in artboard.Layers)
            {
                if (!layer.IsEffectivelyVisible)
                {
                    continue;
                }

                foreach (LayerItem item in layer.Children)
                {
                    if (HitTestItem(item, local, tolerance) is not null)
                    {
                        topmost = item;
                    }
                }
            }
        }

        // Pasteboard/orphan objects live in world coordinates.
        foreach (LayerItem item in _document.Orphans.Children)
        {
            if (HitTestItem(item, model, tolerance) is not null)
            {
                topmost = item;
            }
        }

        return topmost;
    }

    /// <summary>Top-most descendant of a group under the point (to enter a group).</summary>
    private LayerItem? HitTestChildOf(ArtGroup group, Point2D model)
    {
        Vector2D offset = group.OwningLayer()?.Artboard is { } ab
            ? new Vector2D(ab.X, ab.Y)
            : default;
        Point2D local = model - offset;

        LayerItem? topmost = null;
        foreach (LayerItem child in group.Children)
        {
            if (HitTestItem(child, local, PickTolerance) is not null)
            {
                topmost = child;
            }
        }

        return topmost;
    }

    private static LayerItem? HitTestItem(LayerItem item, Point2D model, double tolerance)
    {
        if (!item.IsEffectivelyVisible())
        {
            return null;
        }

        return item switch
        {
            PathItem path when PathPicking.HitTest(path, model, tolerance) != PickKind.None => path,
            TextItem text when text.BoundingBox().Contains(model) => text,
            ArtGroup group => HitTestGroup(group, model, tolerance),
            _ => null,
        };
    }

    private static LayerItem? HitTestGroup(ArtGroup group, Point2D model, double tolerance)
    {
        LayerItem? topmost = null;
        foreach (LayerItem child in group.Children)
        {
            LayerItem? hit = HitTestItem(child, model, tolerance);
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

        // Text editing: a click outside the edited text leaves edit mode; a
        // double-click on a text object enters it.
        if (_editingText is { } editing && !editing.BoundingBox().Contains(model - editing.ArtboardOffset()))
        {
            ExitTextEdit();
        }

        if (e.ClickCount >= 2 && HitTestTopItem(model) is TextItem dblText)
        {
            EnterTextEdit(dblText);
            e.Handled = true;
            return;
        }

        // Clicking an artboard's title enters artboard editing mode.
        if (HitTestArtboardLabel(model) is { } labelled)
        {
            _vm!.SelectArtboard(labelled);
            _vm.Tool = EditorTool.Artboard;
            _leftDown = true;
            e.Pointer.Capture(this);
            e.Handled = true;
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

            case EditorTool.Artboard:
                ArtboardPress(model);
                break;

            case EditorTool.Text:
                EnterTextEdit(_vm!.CreateTextAt(model, "DejaVu Sans", 12));
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

                case EditorTool.Artboard:
                    ArtboardDrag(model);
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
            case EditorTool.Artboard: ArtboardRelease(model); break;
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

    private bool AnyGestureActive()
        => _marqueeActive || _resizeActive || _rotatePaths.Count > 0 || _dragPaths.Count > 0
           || _nodePath is not null || _segmentPath is not null || _penPath is not null
           || _shapeStart is not null;

    /// <summary>The oriented selection box (base rectangle + angle). Initialised
    /// from the geometry's principal axis so it matches an already-rotated object;
    /// then preserved through move/scale/rotate gestures.</summary>
    private Rect2D ChromeRect()
    {
        if (_chromeRect is { } cached)
        {
            return cached;
        }

        if (_vm is null)
        {
            return Rect2D.Empty;
        }

        // Rotation is explicit state (0 until the user rotates). Bounds are the
        // EXACT extents of the curve geometry along that frame — anchors alone
        // would let Bézier bulges escape the box.
        double angle = _vm.SelectionRotationRadians;
        var u = new Vector2D(Math.Cos(angle), Math.Sin(angle));
        var v = new Vector2D(-Math.Sin(angle), Math.Cos(angle));

        double minU = double.PositiveInfinity, minV = double.PositiveInfinity;
        double maxU = double.NegativeInfinity, maxV = double.NegativeInfinity;
        bool any = false;

        void Include(double cu, double cv)
        {
            minU = Math.Min(minU, cu);
            maxU = Math.Max(maxU, cu);
            minV = Math.Min(minV, cv);
            maxV = Math.Max(maxV, cv);
            any = true;
        }

        foreach (TextItem text in _vm.SelectedTextItems())
        {
            Rect2D b = text.WorldBounds();
            if (!b.IsEmpty)
            {
                Include(b.Left, b.Top);
                Include(b.Right, b.Bottom);
            }
        }

        foreach (PathItem path in _vm.SelectedPaths())
        {
            Vector2D offset = path.ArtboardOffset();
            foreach (SubPath sub in path.SubPaths)
            {
                foreach (CubicBezier segment in sub.Segments())
                {
                    // Project the curve (shifted into world space) onto the frame.
                    CubicBezier world = new(
                        segment.P0 + offset, segment.P1 + offset,
                        segment.P2 + offset, segment.P3 + offset);
                    (double sMinU, double sMaxU, double sMinV, double sMaxV) = world.ExtentsAlong(u, v);
                    Include(sMinU, sMinV);
                    Include(sMaxU, sMaxV);
                }

                if (sub.Nodes.Count == 1)
                {
                    Point2D p = sub.Nodes[0].Anchor + offset;
                    Include(p.X * u.X + p.Y * u.Y, p.X * v.X + p.Y * v.Y);
                }
            }
        }

        if (!any)
        {
            return Rect2D.Empty;
        }

        double width = maxU - minU;
        double height = maxV - minV;
        double centerU = (minU + maxU) / 2.0;
        double centerV = (minV + maxV) / 2.0;

        // Reconstruct the world centre from the orthonormal frame.
        var center = new Point2D(u.X * centerU + v.X * centerV, u.Y * centerU + v.Y * centerV);
        var rect = new Rect2D(center.X - width / 2, center.Y - height / 2, width, height);
        _chromeAngle = angle;
        _chromeRect = rect;
        return rect;
    }

    private static Point2D RotatePoint(Point2D p, Point2D center, double radians)
    {
        double cos = Math.Cos(radians);
        double sin = Math.Sin(radians);
        double dx = p.X - center.X;
        double dy = p.Y - center.Y;
        return new Point2D(center.X + dx * cos - dy * sin, center.Y + dx * sin + dy * cos);
    }

    private static Vector2D RotateVector(Vector2D v, double radians)
    {
        double cos = Math.Cos(radians);
        double sin = Math.Sin(radians);
        return new Vector2D(v.X * cos - v.Y * sin, v.X * sin + v.Y * cos);
    }

    /// <summary>A 3×3 cell of the oriented selection box, in model space.</summary>
    private Point2D OrientedCell(Rect2D baseRect, int index)
        => RotatePoint(CellPoint(baseRect, index), baseRect.Center, _chromeAngle);

    /// <summary>The rotation knob: above the oriented top edge, along its normal.</summary>
    private Point2D RotationHandlePoint(Rect2D baseRect)
    {
        Point2D topCentre = OrientedCell(baseRect, 1);
        Vector2D normal = RotateVector(new Vector2D(0, -1), _chromeAngle);
        double lift = Math.Max(22.0 / _layout.Zoom, 4.0);
        return topCentre + normal * lift;
    }

    private bool HitRotationHandle(Point2D model)
    {
        if (_vm is null || !_vm.SelectedPaths().Any() || _shiftHeld)
        {
            return false;
        }

        Rect2D bounds = ChromeRect();
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

        LayerItem? hit = HitTestTopItem(model);
        if (hit is ArtGroup selectedGroup && _vm.IsObjectSelected(selectedGroup)
            && HitTestChildOf(selectedGroup, model) is { } child)
        {
            hit = child; // second click enters the already-selected group
        }

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
        _dragTexts.Clear();
        _dragTextOrigins.Clear();
        foreach (PathItem path in _vm!.SelectedPaths())
        {
            _dragPaths.Add(path);
            _dragOriginals[path] = path.GeometrySnapshot();
        }

        foreach (TextItem text in _vm.SelectedTextItems())
        {
            _dragTexts.Add(text);
            _dragTextOrigins[text] = text.Origin;
        }

        // Snapshot the chrome box BEFORE any translation so each move can place
        // it absolutely (incrementing it would make it drift ahead of the object).
        _chromeRect0 = ChromeRect();
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

        foreach (TextItem text in _dragTexts)
        {
            text.Origin = _dragTextOrigins[text] + delta;
        }

        if (!_chromeRect0.IsEmpty)
        {
            _chromeRect = new Rect2D(
                _chromeRect0.X + delta.X,
                _chromeRect0.Y + delta.Y,
                _chromeRect0.Width,
                _chromeRect0.Height);
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
            CommitMoveEdits();
        }
        else if (_shiftToggleCandidate is not null && _shiftHeld)
        {
            // A Shift click (no drag) on an already-selected object removes it.
            _vm!.ToggleObjectSelection(_shiftToggleCandidate);
        }

        _dragPaths.Clear();
        _dragOriginals.Clear();
        _dragTexts.Clear();
        _dragTextOrigins.Clear();
        _shiftToggleCandidate = null;
        _selectMoved = false;
    }

    /// <summary>Commits a move gesture (paths + text) as one undo step.</summary>
    private void CommitMoveEdits()
    {
        if (_vm is null)
        {
            return;
        }

        var edits = new List<IUndoableCommand>();
        foreach (PathItem path in _dragPaths)
        {
            if (_dragOriginals.TryGetValue(path, out PathItem? before))
            {
                edits.Add(new GeometryReplaceCommand(path, before, path.GeometrySnapshot()));
            }
        }

        foreach (TextItem text in _dragTexts)
        {
            if (_dragTextOrigins.TryGetValue(text, out Point2D before))
            {
                edits.Add(new SetTextOriginCommand(text, before, text.Origin));
            }
        }

        if (edits.Count > 0)
        {
            _vm.Execute(edits.Count == 1 ? edits[0] : new CompositeCommand("Move objects", edits));
        }
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

        // Even a click (zero-area rectangle) resolves the selection: without
        // Shift it clears it, with Shift it leaves the existing selection alone.
        List<LayerItem> hits = rect.IsEmpty ? new List<LayerItem>() : ItemsIntersectingRect(rect);
        _vm!.SelectRange(hits, additive: _shiftHeld);

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
            if (!artboard.IsVisible)
            {
                continue;
            }

            foreach (Layer layer in artboard.Layers)
            {
                if (!layer.IsEffectivelyVisible)
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

        foreach (LayerItem item in _document.Orphans.Children)
        {
            Rect2D bounds = ItemBounds(item);
            if (!bounds.IsEmpty && bounds.Intersects(rect))
            {
                result.Add(item);
            }
        }

        return result;
    }

    private static Rect2D ItemBounds(LayerItem item)
    {
        Vector2D offset = item.OwningLayer()?.Artboard is { } artboard
            ? new Vector2D(artboard.X, artboard.Y)
            : default;

        switch (item)
        {
            case PathItem path:
                Rect2D b = path.BoundingBox();
                return b.IsEmpty ? b : new Rect2D(b.X + offset.X, b.Y + offset.Y, b.Width, b.Height);
            case ArtGroup group:
                Rect2D g = group.Transform.Transform(group.BoundingBox());
                return g.IsEmpty ? g : new Rect2D(g.X + offset.X, g.Y + offset.Y, g.Width, g.Height);
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

        Rect2D bounds = ChromeRect();
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

            if (model.DistanceTo(OrientedCell(bounds, i)) <= tol)
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
        _resizeRect0 = ChromeRect();
        _resizeAngle0 = _chromeAngle;
        _resizeCenter0 = _resizeRect0.Center;
        _resizePivot = CellPoint(_resizeRect0, 8 - _resizeHandle); // opposite cell stays fixed (local frame)

        _resizePaths.Clear();
        _resizeOriginals.Clear();
        _resizeTexts.Clear();
        _resizeTextBefore.Clear();
        foreach (PathItem path in _vm.SelectedPaths())
        {
            _resizePaths.Add(path);
            _resizeOriginals[path] = path.GeometrySnapshot();
        }

        foreach (TextItem text in _vm.SelectedTextItems())
        {
            _resizeTexts.Add(text);
            _resizeTextBefore[text] = (TextItem)text.Clone();
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

        // Work in the box's LOCAL frame: rotate the pointer back by the box angle.
        Point2D local = RotatePoint(model, _resizeCenter0, -_resizeAngle0);

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
            left = Math.Min(local.X, _resizePivot.X - minSize);
        }
        else if (signX > 0)
        {
            right = Math.Max(local.X, _resizePivot.X + minSize);
        }

        if (signY < 0)
        {
            top = Math.Min(local.Y, _resizePivot.Y - minSize);
        }
        else if (signY > 0)
        {
            bottom = Math.Max(local.Y, _resizePivot.Y + minSize);
        }

        double sx = Math.Max(minSize / Math.Max(1e-6, _resizeRect0.Width), (right - left) / _resizeRect0.Width);
        double sy = Math.Max(minSize / Math.Max(1e-6, _resizeRect0.Height), (bottom - top) / _resizeRect0.Height);
        if (_shiftHeld && (signX != 0) && (signY != 0))
        {
            sy = sx; // Shift + corner drag keeps the aspect ratio
        }

        foreach (TextItem text in _resizeTexts)
        {
            TextItem before = _resizeTextBefore[text];
            text.CopyFrom(before);
            Vector2D offset = text.ArtboardOffset();
            Point2D pivotLocal = _resizePivot - offset;
            text.Origin = pivotLocal + new Vector2D(
                (text.Origin.X - pivotLocal.X) * sx,
                (text.Origin.Y - pivotLocal.Y) * sy);
            double fontScale = Math.Sqrt(Math.Abs(sx * sy));
            foreach (TextRun run in text.Runs)
            {
                run.FontSize *= fontScale;
            }
        }

        // Shift + a group selection scales each object in place (about its own
        // centre) instead of translating it to match the group scale.
        bool groupInPlace = _shiftHeld && _vm!.SelectedObjects.Any(o => o is ArtGroup);
        foreach (PathItem path in _resizePaths)
        {
            Vector2D offset = path.ArtboardOffset();
            path.RestoreGeometryFrom(_resizeOriginals[path]);
            if (groupInPlace)
            {
                Point2D center = path.BoundingBox().Center;
                path.ScaleGeometryAbout(center, sx, sx);
            }
            else
            {
                ApplyRotatedScale(path, _resizeCenter0 - offset, _resizeAngle0, _resizePivot - offset, sx, sy);
            }
        }

        // Keep the chrome box glued to the new (still oriented) rectangle.
        var localRect = new Rect2D(left, top, right - left, bottom - top);
        Point2D worldCenter = RotatePoint(localRect.Center, _resizeCenter0, _resizeAngle0);
        _chromeRect = new Rect2D(worldCenter.X - localRect.Width / 2,
            worldCenter.Y - localRect.Height / 2, localRect.Width, localRect.Height);
        _chromeAngle = _resizeAngle0;

        _vm!.RaiseTransformChanged();
        InvalidateVisual();
    }

    private void CommitResize()
    {
        CommitTransformEdits("Resize objects", _resizePaths, _resizeOriginals, _resizeTexts, _resizeTextBefore);
        _resizeActive = false;
        _resizePaths.Clear();
        _resizeOriginals.Clear();
        _resizeTexts.Clear();
        _resizeTextBefore.Clear();
    }

    /// <summary>Commits a transform gesture over paths and text as one undo step.</summary>
    private void CommitTransformEdits(string label, IReadOnlyList<PathItem> paths,
        Dictionary<PathItem, PathItem> pathOriginals, IReadOnlyList<TextItem> texts,
        Dictionary<TextItem, TextItem> textOriginals)
    {
        if (_vm is null)
        {
            return;
        }

        var edits = new List<IUndoableCommand>();
        foreach (PathItem path in paths)
        {
            if (pathOriginals.TryGetValue(path, out PathItem? before))
            {
                edits.Add(new GeometryReplaceCommand(path, before, path.GeometrySnapshot()));
            }
        }

        foreach (TextItem text in texts)
        {
            if (textOriginals.TryGetValue(text, out TextItem? before))
            {
                edits.Add(new ReplaceTextCommand(text, before, (TextItem)text.Clone()));
            }
        }

        if (edits.Count > 0)
        {
            _vm.Execute(edits.Count == 1 ? edits[0] : new CompositeCommand(label, edits));
        }
    }

    /// <summary>Scales a path about <paramref name="pivotLocal"/> along axes rotated
    /// by <paramref name="angle"/> around <paramref name="center"/> (used when the
    /// selection box is oriented).</summary>
    private static void ApplyRotatedScale(PathItem path, Point2D center, double angle,
        Point2D pivotLocal, double sx, double sy)
    {
        Point2D Map(Point2D p)
        {
            Point2D local = RotatePoint(p, center, -angle);
            var scaled = new Point2D(
                pivotLocal.X + (local.X - pivotLocal.X) * sx,
                pivotLocal.Y + (local.Y - pivotLocal.Y) * sy);
            return RotatePoint(scaled, center, angle);
        }

        foreach (SubPath sub in path.SubPaths)
        {
            foreach (PathNode node in sub.Nodes)
            {
                node.Anchor = Map(node.Anchor);
                node.InHandle = Map(node.InHandle);
                node.OutHandle = Map(node.OutHandle);
            }
        }
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
        CommitTransformEdits("Rotate objects", _rotatePaths, _rotateOriginals, _rotateTexts, _rotateTextBefore);
        _rotatePaths.Clear();
        _rotateOriginals.Clear();
        _rotateTexts.Clear();
        _rotateTextBefore.Clear();
    }

    private void BeginRotate(Point2D model)
    {
        if (_vm is null)
        {
            return;
        }

        Rect2D bounds = ChromeRect();
        _rotateCenter = bounds.Center;
        _rotateAngle0 = _vm.SelectionRotationRadians;
        Vector2D fromCenter = model - _rotateCenter;
        _rotateStartAngle = Math.Atan2(fromCenter.Y, fromCenter.X);

        _rotatePaths.Clear();
        _rotateOriginals.Clear();
        _rotateTexts.Clear();
        _rotateTextBefore.Clear();
        foreach (PathItem path in _vm.SelectedPaths())
        {
            _rotatePaths.Add(path);
            _rotateOriginals[path] = path.GeometrySnapshot();
        }

        foreach (TextItem text in _vm.SelectedTextItems())
        {
            _rotateTexts.Add(text);
            _rotateTextBefore[text] = (TextItem)text.Clone();
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
            path.RotateGeometryAbout(_rotateCenter - path.ArtboardOffset(), angle);
        }

        foreach (TextItem text in _rotateTexts)
        {
            TextItem before = _rotateTextBefore[text];
            text.CopyFrom(before);
            Vector2D offset = text.ArtboardOffset();
            Point2D localCenter = _rotateCenter - offset;
            double cos = Math.Cos(angle);
            double sin = Math.Sin(angle);
            double dx = text.Origin.X - localCenter.X;
            double dy = text.Origin.Y - localCenter.Y;
            text.Origin = new Point2D(localCenter.X + dx * cos - dy * sin, localCenter.Y + dx * sin + dy * cos);
            text.RotationRadians = before.RotationRadians + angle;
        }

        _chromeAngle = _rotateAngle0 + angle; // selection box rotates with the objects
        _vm!.SetSelectionRotationRadians(_chromeAngle);

        _vm.RaiseTransformChanged();
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

        _pendingInsertArmed = false;

        // 1) Grabbing a node or handle takes priority over segment selection.
        PathItem? pickPath = null;
        NodePick? pick = null;
        foreach (PathItem candidate in _vm.SelectedPaths())
        {
            NodePick? candidatePick = PathPicking.PickNode(candidate, LocalFor(candidate, model), PickTolerance * 1.6);
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
                pick = PathPicking.PickNode(pickPath, LocalFor(pickPath, model), PickTolerance * 1.6);
            }
        }

        if (pick is not null && pickPath is not null)
        {
            if (!_shiftHeld && !_vm.IsObjectSelected(pickPath))
            {
                _vm.SelectObject(pickPath); // select the path whose node we grab
            }

            BeginNodeDrag(pickPath, pick.Value, LocalFor(pickPath, model));

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
            : PathPicking.ClosestSegment(segmentPath, LocalFor(segmentPath, model), PickTolerance * 1.6);
        if (segment is null)
        {
            segmentPath = HitTestTopPath(model);
            segment = segmentPath is null
                ? null
                : PathPicking.ClosestSegment(segmentPath, LocalFor(segmentPath, model), PickTolerance * 1.6);
        }

        if (segment is not null && segmentPath is not null)
        {
            _vm.ClearPointSelection(); // a segment isn't a point
            int subIndex = segmentPath.SubPaths.IndexOf(segment.Value.SubPath);
            bool wasSelected = _vm.IsSegmentSelected(segmentPath, subIndex, segment.Value.SegmentIndex);

            _vm.SelectSegment(segmentPath, subIndex, segment.Value.SegmentIndex, additive: _shiftHeld);
            InvalidateVisual();

            if (!_vm.IsSegmentSelected(segmentPath, subIndex, segment.Value.SegmentIndex))
            {
                return; // Shift toggled it off
            }

            // A plain click on a segment that was ALREADY selected inserts a point
            // there; a drag instead bends/moves the segment. Arm the insertion and
            // cancel it if the pointer actually moves.
            if (wasSelected && !_shiftHeld)
            {
                _pendingInsertArmed = true;
                _pendingInsertPath = segmentPath;
                _pendingInsertSub = subIndex;
                _pendingInsertSeg = segment.Value.SegmentIndex;
                _pendingInsertPoint = LocalFor(segmentPath, model);
            }

            BeginSegmentDrag(segmentPath, subIndex, segment.Value.SegmentIndex, LocalFor(segmentPath, model));
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
        _chromeRect = null;
        _chromeAngle = 0;
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
        _segmentOffset = path.ArtboardOffset();
        _chromeRect = null;
        _chromeAngle = 0;
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
                    // Keep the shared endpoint colinear while preserving the
                    // opposite handle's LENGTH (only its direction changes).
                    if (_nodeWasSmooth)
                    {
                        Vector2D dir = (target - _nodeAnchorStart).Normalized;
                        double outLen = (_nodeOutStart - _nodeAnchorStart).Length;
                        if (!dir.IsZero)
                        {
                            node.OutHandle = _nodeAnchorStart - dir * outLen;
                        }
                    }
                }
                else
                {
                    node.OutHandle = target;
                    if (_nodeWasSmooth)
                    {
                        Vector2D dir = (target - _nodeAnchorStart).Normalized;
                        double inLen = (_nodeInStart - _nodeAnchorStart).Length;
                        if (!dir.IsZero)
                        {
                            node.InHandle = _nodeAnchorStart - dir * inLen;
                        }
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

            Point2D targetWorld = _shiftHeld
                ? _dragStartModel + SnapTranslation(model - _dragStartModel)
                : model;
            Point2D target = targetWorld - _segmentOffset;
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
            // Point the dragged handle along the line (opposite side of the
            // anchor) while KEEPING ITS CURRENT LENGTH.
            double draggedLength = (draggedTarget - _nodeAnchorStart).Length;
            Vector2D direction = (_nodeAnchorStart - opposite).Normalized;
            _handleSnapArmed = true;
            _handleSnapPos = _nodeAnchorStart + direction * draggedLength;
        }
    }

    /// <summary>
    /// On release, snaps the moved point to a neighbouring point's horizontal or
    /// vertical line when it is within an epsilon of being orthogonal with it.
    /// Anchors align to adjacent anchors; a dragged handle aligns to its anchor.
    /// Toggleable from the UI (Transform tab).
    /// </summary>
    private void ApplyOrthogonalSnap()
    {
        if (_vm is null || !_vm.OrthogonalSnapEnabled || !_gestureMoved ||
            _nodePath is null || _nodeSub is null)
        {
            return;
        }

        double eps = PickTolerance * 1.2;
        PathNode node = _nodeSub.Nodes[_nodeIndex];

        if (_nodeGrabHandle)
        {
            Point2D anchor = node.Anchor;
            Point2D handle = _nodeIsIn ? node.InHandle : node.OutHandle;
            double hx = handle.X;
            double hy = handle.Y;
            bool changed = false;
            if (Math.Abs(handle.X - anchor.X) <= eps)
            {
                hx = anchor.X;
                changed = true;
            }

            if (Math.Abs(handle.Y - anchor.Y) <= eps)
            {
                hy = anchor.Y;
                changed = true;
            }

            if (!changed)
            {
                return;
            }

            Point2D snapped = new(hx, hy);
            if (_nodeIsIn)
            {
                node.InHandle = snapped;
            }
            else
            {
                node.OutHandle = snapped;
            }

            if (_nodeWasSmooth)
            {
                // Keep the shared endpoint colinear (length-preserving).
                Vector2D dir = (snapped - anchor).Normalized;
                double otherLen = _nodeIsIn
                    ? (_nodeOutStart - anchor).Length
                    : (_nodeInStart - anchor).Length;
                if (!dir.IsZero)
                {
                    Point2D other = anchor - dir * otherLen;
                    if (_nodeIsIn)
                    {
                        node.OutHandle = other;
                    }
                    else
                    {
                        node.InHandle = other;
                    }
                }
            }

            return;
        }

        // Anchor drag: align to the adjacent anchors (previous/next in the subpath).
        int count = _nodeSub.Nodes.Count;
        if (count < 2)
        {
            return;
        }

        var neighbours = new List<Point2D>();
        bool closed = _nodeSub.IsClosed;
        if (_nodeIndex > 0 || closed)
        {
            neighbours.Add(_nodeSub.Nodes[(_nodeIndex - 1 + count) % count].Anchor);
        }

        if (_nodeIndex < count - 1 || closed)
        {
            neighbours.Add(_nodeSub.Nodes[(_nodeIndex + 1) % count].Anchor);
        }

        double x = node.Anchor.X;
        double y = node.Anchor.Y;
        bool moved = false;
        foreach (Point2D neighbour in neighbours)
        {
            if (Math.Abs(x - neighbour.X) <= eps)
            {
                x = neighbour.X;
                moved = true;
            }

            if (Math.Abs(y - neighbour.Y) <= eps)
            {
                y = neighbour.Y;
                moved = true;
            }
        }

        if (!moved)
        {
            return;
        }

        Vector2D delta = new Point2D(x, y) - node.Anchor;
        node.Anchor += delta;
        node.InHandle += delta;
        node.OutHandle += delta;
    }

    /// <summary>
    /// Snap the moved anchor: first to the opposite endpoint of its own open
    /// subpath (which closes the path by merging), otherwise to any nearby anchor.
    /// Gated on the snap toggle.
    /// </summary>
    private void ApplyNodeSnap()
    {
        if (_vm is null || !_vm.OrthogonalSnapEnabled || !_gestureMoved ||
            _nodePath is null || _nodeSub is null || _nodeGrabHandle)
        {
            return;
        }

        Vector2D offset = _nodePath.ArtboardOffset();
        Point2D world = _nodeSub.Nodes[_nodeIndex].Anchor + offset;

        // 1) Dragging one end onto the other closes the path.
        if (!_nodeSub.IsClosed && (_nodeIndex == 0 || _nodeIndex == _nodeSub.Nodes.Count - 1))
        {
            int otherIndex = _nodeIndex == 0 ? _nodeSub.Nodes.Count - 1 : 0;
            Point2D otherWorld = _nodeSub.Nodes[otherIndex].Anchor + offset;
            if (world.DistanceTo(otherWorld) <= PickTolerance * 1.5)
            {
                PathNode node = _nodeSub.Nodes[_nodeIndex];
                Vector2D delta = otherWorld - world;
                node.Anchor += delta;
                node.InHandle += delta;
                node.OutHandle += delta;
                _nodeSub.CloseAndMergeEndpoints();
                InvalidateVisual();
                return;
            }
        }

        // 2) Otherwise snap to any other anchor.
        if (FindSnapAnchor(world, _nodePath) is { } snap)
        {
            PathNode node = _nodeSub.Nodes[_nodeIndex];
            Vector2D delta = snap - world;
            node.Anchor += delta;
            node.InHandle += delta;
            node.OutHandle += delta;
            InvalidateVisual();
        }
    }

    private void NodeRelease()
    {
        ApplyNodeSnap();
        ApplyOrthogonalSnap();

        if (_pendingInsertArmed && !_gestureMoved && _pendingInsertPath is not null)
        {
            _vm!.InsertPointOnSegment(_pendingInsertPath, _pendingInsertSub, _pendingInsertSeg, _pendingInsertPoint);
            _pendingInsertArmed = false;
            _segmentPath = null;
            _segmentSub = null;
            _segmentBefore = null;
            _segmentNodeA = null;
            _segmentNodeB = null;
            _segmentBendMode = false;
            _gestureMoved = false;
            return;
        }

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

        // Node/segment edits change the true extents; rebuild the box next paint.
        _chromeRect = null;
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
        Point2D penLocal = model - _penOffset;
        if (_penPath is not null && _penPath.SubPaths[0].Nodes.Count >= 2 &&
            _penPath.SubPaths[0].Nodes[0].Anchor.DistanceTo(penLocal) <= PickTolerance * 2.5)
        {
            _penClosePending = true;
            _penBefore = _penPath.GeometrySnapshot();
            return;
        }

        (Layer penLayer, Vector2D penOffset) = _vm.TargetFor(model);
        _penOffset = penOffset;
        _chromeRect = null;
        _chromeAngle = 0;
        if (_penPath is null)
        {
            _penPath = new PathItem { Name = "Path", Stroke = StrokeSpec.Hairline(ColorRgb.Black) };
            _penPath.AddSubPath(closed: false);
            _vm.Execute(new AddItemCommand(penLayer, _penPath));
            _penBefore = _penPath.GeometrySnapshot();
        }
        else
        {
            _penBefore = _penPath.GeometrySnapshot();
        }

        SubPath sub = _penPath.SubPaths[0];

        // Shift constrains the new anchor to be orthogonal to the previous one.
        Point2D placed = penLocal;
        if (_vm.OrthogonalSnapEnabled && FindSnapAnchor(model, null) is { } snapAnchor)
        {
            placed = snapAnchor - _penOffset;
        }
        else if (_shiftHeld && sub.Nodes.Count > 0)
        {
            Point2D previous = sub.Nodes[^1].Anchor;
            placed = previous + SnapTranslation(penLocal - previous);
        }

        _penNode = sub.AppendNode(placed);
        _dragStartModel = placed;
        InvalidateVisual();
    }

    private void PenDrag(Point2D model)
    {
        if (_penNode is null)
        {
            return;
        }

        Point2D local = model - _penOffset;
        Point2D anchor = _penNode.Anchor;
        Point2D target = _shiftHeld ? anchor + SnapTranslation(local - anchor) : local;
        if ((target - anchor).IsZero)
        {
            return;
        }

        _penNode.OutHandle = target;
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
    // Artboard tool
    // ------------------------------------------------------------------

    private Artboard? HitTestArtboard(Point2D model)
    {
        if (_document is null)
        {
            return null;
        }

        for (int i = _document.Artboards.Count - 1; i >= 0; i--)
        {
            if (_document.Artboards[i].Bounds.Contains(model))
            {
                return _document.Artboards[i];
            }
        }

        return null;
    }

    private bool TryHitArtboardHandle(Artboard artboard, Point2D model, out int handle)
    {
        handle = -1;
        Rect2D r = artboard.Bounds;
        Point2D[] corners =
        {
            new(r.Left, r.Top), new(r.Right, r.Top),
            new(r.Right, r.Bottom), new(r.Left, r.Bottom),
        };
        for (int i = 0; i < 4; i++)
        {
            if (model.DistanceTo(corners[i]) <= PickTolerance * 2)
            {
                handle = i;
                return true;
            }
        }

        return false;
    }

    private void ArtboardPress(Point2D model)
    {
        _artboardChildren.Clear();
        _dragStartModel = model;

        // 1) A handle of the selected artboard takes priority — the handle sits on
        //    (and slightly outside) the corner, so containment alone would miss it.
        if (_vm!.SelectedArtboard is { } selected && TryHitArtboardHandle(selected, model, out int selectedHandle))
        {
            _artboard = selected;
            _artboardBefore = selected.Bounds;
            _artboardGesture = ArtboardGesture.Resize;
            _artboardHandle = selectedHandle;
            InvalidateVisual();
            return;
        }

        // 2) Otherwise select the artboard under the point (handle or move).
        Artboard? artboard = HitTestArtboard(model);
        if (artboard is not null)
        {
            _vm.SelectArtboard(artboard);
            _artboard = artboard;
            _artboardBefore = artboard.Bounds;
            if (TryHitArtboardHandle(artboard, model, out int handle))
            {
                _artboardGesture = ArtboardGesture.Resize;
                _artboardHandle = handle;
            }
            else
            {
                _artboardGesture = ArtboardGesture.Move;
                foreach (Layer layer in artboard.Layers)
                {
                    foreach (LayerItem item in layer.Children)
                    {
                        CollectPaths(item, _artboardChildren);
                    }
                }
            }
        }
        else
        {
            _vm.SelectArtboard(null);
            _artboardGesture = ArtboardGesture.Create;
            _artboardCreateStart = model;
            _artboardCreateCurrent = model;
        }

        InvalidateVisual();
    }

    private void CollectPaths(LayerItem item, List<(PathItem Path, PathItem Before)> sink)
    {
        switch (item)
        {
            case PathItem path:
                sink.Add((path, path.GeometrySnapshot()));
                break;
            case ArtGroup group:
                foreach (LayerItem child in group.Children)
                {
                    CollectPaths(child, sink);
                }

                break;
        }
    }

    private void ArtboardDrag(Point2D model)
    {
        switch (_artboardGesture)
        {
            case ArtboardGesture.Create:
                _artboardCreateCurrent = model;
                break;

            case ArtboardGesture.Move when _artboard is not null:
                Vector2D delta = model - _dragStartModel;
                _artboard.X = _artboardBefore.X + delta.X;
                _artboard.Y = _artboardBefore.Y + delta.Y;
                foreach ((PathItem path, PathItem before) in _artboardChildren)
                {
                    path.RestoreGeometryFrom(before);
                    path.TranslateGeometryBy(delta);
                }

                _vm!.RaiseTransformChanged();
                break;

            case ArtboardGesture.Resize when _artboard is not null:
                Rect2D r = _artboardBefore;
                Point2D fixedCorner = _artboardHandle switch
                {
                    0 => new Point2D(r.Right, r.Bottom),
                    1 => new Point2D(r.Left, r.Bottom),
                    2 => new Point2D(r.Left, r.Top),
                    _ => new Point2D(r.Right, r.Top),
                };
                Rect2D resized = Rect2D.FromPoints(fixedCorner, model);
                _artboard.X = resized.X;
                _artboard.Y = resized.Y;
                _artboard.Width = Math.Max(1, resized.Width);
                _artboard.Height = Math.Max(1, resized.Height);
                _vm!.RaiseTransformChanged();
                break;
        }

        InvalidateVisual();
    }

    private void ArtboardRelease(Point2D model)
    {
        if (_vm is null)
        {
            return;
        }

        switch (_artboardGesture)
        {
            case ArtboardGesture.Move when _artboard is not null:
                var edits = new List<IUndoableCommand>
                {
                    new SetArtboardBoundsCommand(_artboard, _artboardBefore, _artboard.Bounds, "Move artboard"),
                };
                foreach ((PathItem path, PathItem before) in _artboardChildren)
                {
                    edits.Add(new GeometryReplaceCommand(path, before, path.GeometrySnapshot()));
                }

                _vm.Execute(new CompositeCommand("Move artboard", edits));
                break;

            case ArtboardGesture.Resize when _artboard is not null:
                _vm.ApplyArtboardBounds(_artboard, _artboardBefore, _artboard.Bounds);
                break;

            case ArtboardGesture.Create:
                Rect2D rect = Rect2D.FromPoints(_artboardCreateStart, _artboardCreateCurrent);
                if (rect.Width < 4 || rect.Height < 4)
                {
                    rect = new Rect2D(_artboardCreateStart.X, _artboardCreateStart.Y,
                        PageSizes.A4Landscape.Width, PageSizes.A4Landscape.Height);
                }

                _vm.AddArtboardFromRect(rect);
                break;
        }

        _artboardGesture = ArtboardGesture.None;
        _artboard = null;
        _artboardChildren.Clear();
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

        (Layer shapeLayer, Vector2D offset) = _vm!.TargetFor(a);
        Point2D la = a - offset;
        Point2D lb = b - offset;
        Rect2D box = Rect2D.FromPoints(la, lb);
        PathItem shape = rect
            ? PathFactory.CreateRectangle("Rectangle", box)
            : PathFactory.CreateEllipse("Ellipse",
                new Point2D((la.X + lb.X) / 2, (la.Y + lb.Y) / 2),
                Math.Abs(box.Width) / 2,
                Math.Abs(box.Height) / 2);
        shape.Stroke = StrokeSpec.Hairline(ColorRgb.Black);
        shape.Fill = FillSpec.None;

        _vm.Execute(new AddItemCommand(shapeLayer, shape));
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

        context.FillRectangle(new SolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x1F)), new Rect(Bounds.Size));

        Rect2D extent = _layout.Extent;
        Point topLeft = ModelToScreen(new Point2D(extent.Left, extent.Top));
        context.FillRectangle(
            new SolidColorBrush(Color.FromRgb(0x23, 0x23, 0x27)),
            new Rect(topLeft.X, topLeft.Y, extent.Width * _layout.Zoom, extent.Height * _layout.Zoom));

        foreach (Artboard artboard in _document.Artboards)
        {
            if (artboard.IsVisible)
            {
                PaintArtboard(context, artboard);
            }
        }

        // Orphaned (pasteboard) objects, drawn in world coordinates.
        if (_document.Orphans.IsVisible)
        {
            foreach (LayerItem item in _document.Orphans.Children)
            {
                PaintItem(context, item, _document.Orphans.Opacity);
            }
        }

        PaintArtboardLabels(context);
        PaintOverlays(context);
    }

    private void PaintArtboard(DrawingContext context, Artboard artboard)
    {
        Point p = ModelToScreen(new Point2D(artboard.X, artboard.Y));
        var rect = new Rect(p.X, p.Y, artboard.Width * _layout.Zoom, artboard.Height * _layout.Zoom);
        // Soft drop shadow so the page lifts off the dark pasteboard.
        context.FillRectangle(new SolidColorBrush(Color.FromArgb(110, 0, 0, 0)),
            new Rect(rect.X + 3, rect.Y + 3, rect.Width, rect.Height));
        context.FillRectangle(new SolidColorBrush(Colors.White), rect);
        context.DrawRectangle(new Pen(new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x42)), 1.0), rect);

        foreach (Layer layer in artboard.Layers)
        {
            if (!layer.IsEffectivelyVisible)
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
        if (!item.IsEffectivelyVisible())
        {
            return;
        }

        switch (item)
        {
            case PathItem path when path.IsVisible:
                PaintPath(context, path, opacity * path.Opacity);
                break;

            case TextItem text when text.IsVisible:
                PaintText(context, text, opacity);
                break;

            case ArtGroup group when group.IsVisible:
                foreach (LayerItem child in group.Children)
                {
                    PaintItem(context, child, opacity * group.Opacity);
                }

                break;
        }
    }

    private void PaintText(DrawingContext context, TextItem text, double opacity)
    {
        Vector2D offset = text.ArtboardOffset();
        IBrush brush = ToBrush(text.Color, opacity);
        double x = 0;
        double y = 0;

        Avalonia.Matrix? transform = null;
        if (Math.Abs(text.RotationRadians) > 1e-9)
        {
            Point s0 = ModelToScreen(text.Origin + offset);
            transform = Avalonia.Matrix.CreateTranslation(-s0.X, -s0.Y)
                * Avalonia.Matrix.CreateRotation(text.RotationRadians)
                * Avalonia.Matrix.CreateTranslation(s0.X, s0.Y);
        }

        IDisposable? pushed = transform is { } m ? context.PushTransform(m) : null;

        foreach (TextRun run in text.Runs)
        {
            var typeface = new Typeface(
                new FontFamily(run.FontFamily),
                run.Italic ? FontStyle.Italic : FontStyle.Normal,
                run.Bold ? FontWeight.Bold : FontWeight.Normal);
            string[] lines = run.Text.Split('\n');

            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Length > 0)
                {
                    var formatted = new FormattedText(lines[i], CultureInfo.CurrentCulture,
                        FlowDirection.LeftToRight, typeface, run.FontSize, brush);
                    Point screen = ModelToScreen(new Point2D(text.Origin.X + x, text.Origin.Y + y) + offset);
                    context.DrawText(formatted, screen);
                    x += formatted.Width / Math.Max(_layout.Zoom, 1e-6);
                }

                if (i < lines.Length - 1)
                {
                    x = 0;
                    y += run.FontSize * 1.2;
                }
            }
        }

        pushed?.Dispose();
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

        StreamGeometry geometry = BuildGeometry(path, outsideClip: false);
        double width = Math.Max(0.1, path.Stroke.Width * _layout.Zoom);
        Pen StrokePen(double thickness) => new(
            ToBrush(path.Stroke.Color, opacity),
            thickness: Math.Max(0.1, thickness),
            lineCap: ToLineCap(path.Stroke.Cap),
            lineJoin: ToLineJoin(path.Stroke.Join),
            miterLimit: path.Stroke.MiterLimit);

        if (fillVisible)
        {
            context.DrawGeometry(ToBrush(path.Fill.Color, opacity), null, geometry);
        }

        if (!strokeVisible)
        {
            return;
        }

        bool aligned = path.Stroke.Alignment != StrokeAlignment.Center && anyClosed;
        if (!aligned)
        {
            context.DrawGeometry(null, StrokePen(width), geometry);
            return;
        }

        // Inside/Outside: clip to the region, then stroke at double width.
        Avalonia.Media.Geometry clip = path.Stroke.Alignment == StrokeAlignment.Inside
            ? geometry
            : BuildGeometry(path, outsideClip: true);
        using (context.PushGeometryClip(clip))
        {
            context.DrawGeometry(null, StrokePen(width * 2), geometry);
        }
    }

    /// <summary>Builds the on-screen geometry for a path. When
    /// <paramref name="outsideClip"/> is true a huge surrounding rectangle is
    /// prepended with the even-odd rule, giving the complement region (used to
    /// clip an "outside" stroke).</summary>
    private StreamGeometry BuildGeometry(PathItem path, bool outsideClip)
    {
        var geometry = new StreamGeometry();
        MediaFillRule rule = outsideClip || path.Fill.Rule == ModelFillRule.EvenOdd
            ? MediaFillRule.EvenOdd
            : MediaFillRule.NonZero;

        // Path coordinates are artboard-local; shift into world space.
        Vector2D offset = path.ArtboardOffset();
        Point Map(Point2D local) => ModelToScreen(local + offset);

        using (StreamGeometryContext g = geometry.Open())
        {
            g.SetFillRule(rule);

            if (outsideClip)
            {
                Rect2D huge = _layout.Extent.Inflated(10000);
                Point tl = ModelToScreen(new Point2D(huge.Left, huge.Top));
                Point tr = ModelToScreen(new Point2D(huge.Right, huge.Top));
                Point br = ModelToScreen(new Point2D(huge.Right, huge.Bottom));
                Point bl = ModelToScreen(new Point2D(huge.Left, huge.Bottom));
                g.BeginFigure(tl, true);
                g.LineTo(tr);
                g.LineTo(br);
                g.LineTo(bl);
                g.EndFigure(true);
            }

            foreach (SubPath sub in path.SubPaths)
            {
                if (sub.Nodes.Count < 2)
                {
                    continue;
                }

                g.BeginFigure(Map(sub.Nodes[0].Anchor), sub.IsClosed);
                int segmentCount = sub.SegmentCount;
                for (int i = 0; i < segmentCount; i++)
                {
                    PathNode from = sub.Nodes[i];
                    PathNode to = sub.Nodes[(i + 1) % sub.Nodes.Count];
                    Point fromAnchor = Map(from.Anchor);
                    Point p1 = Map(from.OutHandle);
                    Point p2 = Map(to.InHandle);
                    Point p3 = Map(to.Anchor);

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

        return geometry;
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
            Rect2D selection = ChromeRect();
            if (!selection.IsEmpty)
            {
                PaintSelectChrome(context);
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

        if (_editingText is not null)
        {
            PaintTextCaret(context);
        }

        if (tool == EditorTool.Artboard)
        {
            PaintArtboardChrome(context);
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

    /// <summary>Selection chrome in the Select tool: an ORIENTED dashed outline (no
    /// fill) that rotates with the objects, with draggable resize handles and a
    /// rotation knob above its top edge.</summary>
    private void PaintSelectChrome(DrawingContext context)
    {
        Rect2D bounds = ChromeRect();
        if (bounds.IsEmpty)
        {
            return;
        }

        IBrush accent = new SolidColorBrush(Color.FromRgb(0x4C, 0x9A, 0xFF));
        var pen = new Pen(accent, 1.2) { DashStyle = new DashStyle(new[] { 4.0, 3.0 }, 0) };
        double half = Math.Max(4.0 / _layout.Zoom, 1.0);

        // Oriented outline through the four corners (TL → TR → BR → BL).
        var outline = new StreamGeometry();
        using (StreamGeometryContext g = outline.Open())
        {
            g.BeginFigure(ModelToScreen(OrientedCell(bounds, 0)), true);
            g.LineTo(ModelToScreen(OrientedCell(bounds, 2)));
            g.LineTo(ModelToScreen(OrientedCell(bounds, 8)));
            g.LineTo(ModelToScreen(OrientedCell(bounds, 6)));
            g.EndFigure(true);
        }

        context.DrawGeometry(null, pen, outline);

        // Eight resize handles (all 3×3 cells except the centre).
        var handlePen = new Pen(accent, 1.2);
        for (int i = 0; i < 9; i++)
        {
            if (i == 4)
            {
                continue;
            }

            Point center = ModelToScreen(OrientedCell(bounds, i));
            context.DrawRectangle(Brushes.White, handlePen,
                new Rect(center.X - half, center.Y - half, half * 2, half * 2));
        }

        // Rotation knob above the oriented top-centre.
        Point2D top = OrientedCell(bounds, 1);
        Point rot = ModelToScreen(RotationHandlePoint(bounds));
        double r = Math.Max(4.5 / _layout.Zoom, 1.6);
        context.DrawLine(handlePen, ModelToScreen(top), rot);
        context.DrawEllipse(Brushes.White, new Pen(accent, 1.4), rot, r, r);
    }

    /// <summary>Node tool chrome: anchors and handles only — no bounding rectangle.</summary>
    private void PaintNodeChrome(DrawingContext context, PathItem path)
    {
        IBrush accent = new SolidColorBrush(Color.FromRgb(0x4C, 0x9A, 0xFF));
        var pen = new Pen(accent, 1.4);
        double half = Math.Max(4.0 / _layout.Zoom, 1.0);

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

        IBrush accent = new SolidColorBrush(Color.FromRgb(0x4C, 0x9A, 0xFF));

        // Faint line from the anchor to the (future) mirrored position.
        context.DrawLine(new Pen(accent, 1.0) { DashStyle = new DashStyle(new[] { 3.0, 3.0 }, 0) },
            anchor, ModelToScreen(_handleSnapPos));

        // Heavier control handle to signal "release to snap".
        double big = Math.Max(3.6 / _layout.Zoom, 1.4);
        var ring = new Pen(new SolidColorBrush(Color.FromRgb(0x2B, 0x6C, 0xB0)), Math.Max(1.8, 2.0 * _layout.Zoom));
        context.DrawEllipse(Brushes.White, ring, handle, big, big);
    }

    private void PaintMarquee(DrawingContext context)
    {
        Rect2D rect = Rect2D.FromPoints(_marqueeStart, _marqueeCurrent);
        Point tl = ModelToScreen(new Point2D(rect.Left, rect.Top));
        var screen = new Rect(tl.X, tl.Y, rect.Width * _layout.Zoom, rect.Height * _layout.Zoom);

        var accent = new SolidColorBrush(Color.FromRgb(0x4C, 0x9A, 0xFF));
        var pen = new Pen(accent, 1.0) { DashStyle = new DashStyle(new[] { 4.0, 3.0 }, 0) };
        var fill = new SolidColorBrush(Color.FromArgb(26, 0x4C, 0x9A, 0xFF));
        context.DrawRectangle(fill, pen, screen);
    }

    /// <summary>The neutral grey used for control-handle lines (and, dashed, for
    /// selected-segment overlays).</summary>
    private static IBrush HandleLineBrush { get; } =
        new SolidColorBrush(Color.FromArgb(225, 0xB0, 0xB0, 0xB5));

    private void DrawHandleLine(DrawingContext context, Point from, Point to)
    {
        context.DrawLine(new Pen(HandleLineBrush, 1.0), from, to);
    }

    private void PaintSegmentHighlights(DrawingContext context)
    {
        IBrush accent = new SolidColorBrush(Color.FromRgb(0x4C, 0x9A, 0xFF));
        var handlePen = new Pen(accent, 1.4);
        double half = Math.Max(4.0 / _layout.Zoom, 1.0);

        foreach ((PathItem path, int sub, int seg) in _vm!.SelectedSegments())
        {
            if (sub >= path.SubPaths.Count || path.SubPaths[sub].SegmentCount <= seg)
            {
                continue;
            }

            SubPath sp = path.SubPaths[sub];
            CubicBezier curve = sp.GetSegment(seg);

            // Overlay a dashed line in the same colour used for control handle
            // lines, at 90% of the segment's own rendered width (so it reads as a
            // selection highlight without changing the underlying pen).
            double overlayWidth = path.Stroke.HasVisibleOutline
                ? Math.Max(1.0, path.Stroke.Width * _layout.Zoom * 0.9)
                : 1.5;
            var segmentPen = new Pen(HandleLineBrush, overlayWidth)
            {
                DashStyle = new DashStyle(new[] { 5.0, 3.5 }, 0),
            };

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

    // ------------------------------------------------------------------
    // On-canvas text editing (rich text)
    // ------------------------------------------------------------------

    /// <summary>Enters text-edit mode for an object (caret at the end).</summary>
    private void EnterTextEdit(TextItem text)
    {
        _editingText = text;
        _editBefore = (TextItem)text.Clone();
        _caret = text.PlainText.Length;
        _vm!.SelectObject(text);
        _vm.IsEditingText = true;
        _vm.TextCaretRunIndex = RunIndexForCaret();
        Focus();
        InvalidateVisual();
    }

    /// <summary>Leaves text-edit mode, committing the session as one undo step.</summary>
    private void ExitTextEdit()
    {
        if (_editingText is null)
        {
            return;
        }

        if (_editBefore is not null && !TextEquals(_editBefore, _editingText))
        {
            _vm!.Execute(new ReplaceTextCommand(_editingText, _editBefore, (TextItem)_editingText.Clone(), "Edit text"));
        }

        _editingText = null;
        _editBefore = null;
        if (_vm is not null)
        {
            _vm.IsEditingText = false;
        }

        InvalidateVisual();
    }

    private static bool TextEquals(TextItem a, TextItem b)
    {
        if (a.Runs.Count != b.Runs.Count || a.Origin != b.Origin || a.Color != b.Color ||
            Math.Abs(a.RotationRadians - b.RotationRadians) > 1e-9)
        {
            return false;
        }

        for (int i = 0; i < a.Runs.Count; i++)
        {
            TextRun ra = a.Runs[i];
            TextRun rb = b.Runs[i];
            if (ra.Text != rb.Text || ra.FontFamily != rb.FontFamily ||
                Math.Abs(ra.FontSize - rb.FontSize) > 1e-9 || ra.Bold != rb.Bold || ra.Italic != rb.Italic)
            {
                return false;
            }
        }

        return true;
    }

    private (int Run, int Char) LocateCaret()
    {
        if (_editingText is null)
        {
            return (0, 0);
        }

        int remaining = _caret;
        for (int r = 0; r < _editingText.Runs.Count; r++)
        {
            int len = _editingText.Runs[r].Text.Length;
            if (remaining <= len)
            {
                return (r, remaining);
            }

            remaining -= len;
        }

        int last = Math.Max(0, _editingText.Runs.Count - 1);
        return (last, _editingText.Runs[last].Text.Length);
    }

    private int RunIndexForCaret()
    {
        (int run, _) = LocateCaret();
        return run;
    }

    private void InsertText(string value)
    {
        if (_editingText is null)
        {
            return;
        }

        if (_editingText.Runs.Count == 0)
        {
            _editingText.Runs.Add(new TextRun { FontSize = 12 });
        }

        (int run, int ch) = LocateCaret();
        _editingText.Runs[run].Text = _editingText.Runs[run].Text.Insert(ch, value);
        _caret += value.Length;
        AfterTextEdit();
    }

    private void BackspaceText()
    {
        if (_editingText is null || _caret == 0)
        {
            return;
        }

        (int run, int ch) = LocateCaret();
        if (ch > 0)
        {
            _editingText.Runs[run].Text = _editingText.Runs[run].Text.Remove(ch - 1, 1);
        }
        else if (run > 0)
        {
            TextRun prev = _editingText.Runs[run - 1];
            if (prev.Text.Length > 0)
            {
                prev.Text = prev.Text.Remove(prev.Text.Length - 1);
            }

            if (prev.Text.Length == 0)
            {
                _editingText.Runs.RemoveAt(run - 1);
            }
        }

        _caret--;
        AfterTextEdit();
    }

    private void DeleteText()
    {
        if (_editingText is null || _caret >= _editingText.PlainText.Length)
        {
            return;
        }

        (int run, int ch) = LocateCaret();
        string text = _editingText.Runs[run].Text;
        if (ch < text.Length)
        {
            _editingText.Runs[run].Text = text.Remove(ch, 1);
        }
        else if (run + 1 < _editingText.Runs.Count)
        {
            _editingText.Runs.RemoveAt(run + 1);
        }

        AfterTextEdit();
    }

    private void AfterTextEdit()
    {
        if (_vm is not null)
        {
            _vm.TextCaretRunIndex = RunIndexForCaret();
            _vm.RaiseTransformChanged();
        }

        InvalidateVisual();
    }

    private void HandleTextEditKey(KeyEventArgs e)
    {
        bool handled = true;
        switch (e.Key)
        {
            case Key.Escape:
                ExitTextEdit();
                break;
            case Key.Left:
                _caret = Math.Max(0, _caret - 1);
                AfterTextEdit();
                break;
            case Key.Right:
                _caret = Math.Min(_editingText!.PlainText.Length, _caret + 1);
                AfterTextEdit();
                break;
            case Key.Home:
                _caret = 0;
                AfterTextEdit();
                break;
            case Key.End:
                _caret = _editingText!.PlainText.Length;
                AfterTextEdit();
                break;
            case Key.Back:
                BackspaceText();
                break;
            case Key.Delete:
                DeleteText();
                break;
            case Key.Enter:
                InsertText("\n");
                break;
            default:
                // Printable keys are left unhandled so OnTextInput receives them;
                // returning here still prevents tool shortcuts firing.
                handled = false;
                break;
        }

        e.Handled = handled;
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (_editingText is not null && !string.IsNullOrEmpty(e.Text))
        {
            InsertText(e.Text);
            e.Handled = true;
        }
    }

    private Point2D CaretLocal()
    {
        if (_editingText is null)
        {
            return default;
        }

        double x = 0;
        double y = 0;
        int remaining = _caret;
        foreach (TextRun run in _editingText.Runs)
        {
            foreach (char ch in run.Text)
            {
                if (remaining == 0)
                {
                    return new Point2D(x, y);
                }

                if (ch == '\n')
                {
                    x = 0;
                    y += run.FontSize * 1.2;
                }
                else
                {
                    x += run.FontSize * 0.6;
                }

                remaining--;
            }
        }

        return new Point2D(x, y);
    }

    private void PaintTextCaret(DrawingContext context)
    {
        if (_editingText is null)
        {
            return;
        }

        Vector2D offset = _editingText.ArtboardOffset();
        Point2D local = CaretLocal();
        Point2D world = _editingText.Origin + new Vector2D(local.X, local.Y);
        if (Math.Abs(_editingText.RotationRadians) > 1e-9)
        {
            double cos = Math.Cos(_editingText.RotationRadians);
            double sin = Math.Sin(_editingText.RotationRadians);
            double dx = world.X - _editingText.Origin.X;
            double dy = world.Y - _editingText.Origin.Y;
            world = new Point2D(_editingText.Origin.X + dx * cos - dy * sin,
                _editingText.Origin.Y + dx * sin + dy * cos);
        }

        Point screen = ModelToScreen(world + offset);
        double h = _editingText.MaxFontSize * 1.2 * _layout.Zoom;
        context.DrawLine(new Pen(Brushes.White, 1.4), screen, new Point(screen.X, screen.Y + h));
    }

    private static FormattedText BuildArtboardLabel(string name, IBrush brush)
        => new(name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default), EditorTheme.FontSize, brush);

    private Rect ArtboardLabelRect(Artboard artboard)
    {
        Point topLeft = ModelToScreen(new Point2D(artboard.X, artboard.Y));
        FormattedText text = BuildArtboardLabel(artboard.Name, Brushes.White);
        double w = text.Width + 8;
        double h = text.Height + 2;
        return new Rect(topLeft.X, topLeft.Y - h - 4, w, h);
    }

    /// <summary>Artboard whose name label contains the (screen) point, or null.</summary>
    private Artboard? HitTestArtboardLabel(Point2D model)
    {
        if (_document is null)
        {
            return null;
        }

        Point screen = ModelToScreen(model);
        for (int i = _document.Artboards.Count - 1; i >= 0; i--)
        {
            if (ArtboardLabelRect(_document.Artboards[i]).Contains(screen))
            {
                return _document.Artboards[i];
            }
        }

        return null;
    }

    private void PaintArtboardLabels(DrawingContext context)
    {
        if (_document is null)
        {
            return;
        }

        IBrush background = new SolidColorBrush(Color.FromArgb(200, 0x2A, 0x2A, 0x2F));
        IBrush textBrush = new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE9));

        foreach (Artboard artboard in _document.Artboards)
        {
            Rect rect = ArtboardLabelRect(artboard);
            context.FillRectangle(background, rect, 3);
            FormattedText text = BuildArtboardLabel(artboard.Name, textBrush);
            context.DrawText(text, new Point(rect.X + 4, rect.Y + 1));
        }
    }

    private void PaintArtboardChrome(DrawingContext context)
    {
        IBrush accent = new SolidColorBrush(Color.FromRgb(0x4C, 0x9A, 0xFF));
        var pen = new Pen(accent, 1.4) { DashStyle = new DashStyle(new[] { 5.0, 3.0 }, 0) };

        if (_artboardGesture == ArtboardGesture.Create)
        {
            Rect2D preview = Rect2D.FromPoints(_artboardCreateStart, _artboardCreateCurrent);
            Point ptl = ModelToScreen(new Point2D(preview.Left, preview.Top));
            context.DrawRectangle(null, pen,
                new Rect(ptl.X, ptl.Y, preview.Width * _layout.Zoom, preview.Height * _layout.Zoom));
            return;
        }

        if (_vm?.SelectedArtboard is not { } artboard)
        {
            return;
        }

        Rect2D r = artboard.Bounds;
        Point tl = ModelToScreen(new Point2D(r.Left, r.Top));
        context.DrawRectangle(null, pen, new Rect(tl.X, tl.Y, r.Width * _layout.Zoom, r.Height * _layout.Zoom));

        double half = Math.Max(4.0 / _layout.Zoom, 1.0);
        var handlePen = new Pen(accent, 1.4);
        Point2D[] corners =
        {
            new(r.Left, r.Top), new(r.Right, r.Top), new(r.Right, r.Bottom), new(r.Left, r.Bottom),
        };
        foreach (Point2D corner in corners)
        {
            Point p = ModelToScreen(corner);
            context.DrawRectangle(Brushes.White, handlePen,
                new Rect(p.X - half, p.Y - half, half * 2, half * 2));
        }
    }

    private void PaintShapePreview(DrawingContext context, Point2D a, Point2D b, bool rect)
    {
        var previewPen = new Pen(new SolidColorBrush(Color.FromRgb(0x4C, 0x9A, 0xFF)), 1.0);
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
        var previewPen = new Pen(new SolidColorBrush(Color.FromRgb(0x4C, 0x9A, 0xFF)), 1.0);
        previewPen.DashStyle = new DashStyle(new[] { 4.0, 3.0 }, 0);

        Point2D lastLocal = sub.Nodes[^1].Anchor;
        Point2D lastWorld = lastLocal + _penOffset;
        Point last = ModelToScreen(lastWorld);
        if (_hoverModel is { } hover)
        {
            Point2D hoverLocal = hover - _penOffset;
            Point2D previewLocal = _shiftHeld ? lastLocal + SnapTranslation(hoverLocal - lastLocal) : hoverLocal;
            context.DrawLine(previewPen, last, ModelToScreen(previewLocal + _penOffset));
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

        if (_editingText is not null)
        {
            HandleTextEditKey(e);
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.Z)
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                _vm.Redo();
            }
            else
            {
                _vm.Undo();
            }

            e.Handled = true;
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.Y)
        {
            _vm.Redo();
            e.Handled = true;
            return;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.G)
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                _vm.UngroupSelection();
            }
            else
            {
                _vm.GroupSelection();
            }

            e.Handled = true;
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

            case Key.T:
                _vm.Tool = EditorTool.Text;
                FinalizePen();
                e.Handled = true;
                break;

            case Key.O:
                _vm.Tool = EditorTool.Artboard;
                FinalizePen();
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
                    _vm.RequestDeleteSelection();
                }

                e.Handled = true;
                break;

            case Key.Escape or Key.Enter:
                if (_vm.Tool == EditorTool.Artboard)
                {
                    _vm.Tool = EditorTool.Select;
                }
                else
                {
                    FinalizePen(select: false);
                }

                e.Handled = true;
                break;
        }
    }

    private static bool Near(Point a, Point b)
        => Math.Abs(a.X - b.X) < 1e-6 && Math.Abs(a.Y - b.Y) < 1e-6;

    private static IBrush ToBrush(ColorRgb color, double opacity)
    {
        byte alpha = (byte)Math.Round(MathUtils.Clamp(opacity * color.A, 0.0, 1.0) * 255.0);
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
