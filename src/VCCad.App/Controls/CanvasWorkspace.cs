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
/// The editable pasteboard. Renders the document (artboards + paths) and hosts
/// the interactive tools:
/// <list type="bullet">
/// <item><b>Select (V)</b> — click to pick the top-most path, drag to move it.</item>
/// <item><b>Node (A)</b> — shows anchors/handles of the selected path; drag an
/// anchor (moves the node) or a handle (reshapes the curve).</item>
/// <item><b>Pen (P)</b> — click places a corner anchor on the active pen path,
/// drag pulls an outgoing handle, click the start anchor to close; Enter/Escape
/// or switching tools finalises.</item>
/// </list>
/// Panning happens with the middle or right mouse button; Ctrl+wheel zooms
/// anchored at the cursor. Live drags mutate geometry for instant feedback and
/// are committed on release through <see cref="GeometryReplaceCommand"/> so every
/// gesture is one undo step.
///
/// The pasteboard rule (one full viewport past every object) is enforced by
/// <see cref="VCCad.Core.Viewport.PasteboardLayout"/>.
/// </summary>
public sealed class CanvasWorkspace : Control
{
    private CadDocument? _document;
    private PasteboardLayout _layout = new(Rect2D.Empty);
    private Vector2D _offset;         // artwork top-left position on screen, px
    private bool _isPanning;
    private bool _hasLaidOutOnce;

    private EditorViewModel? _viewModel;
    private bool _leftDown;

    // Cursor position in model space, updated on hover (pen preview, snaps).
    private Point2D? _hoverModel;

    // Move (Select tool) gesture.
    private PathItem? _moveItem;
    private PathItem? _moveBefore;
    private Point2D _gestureStartModel;
    private bool _gestureMoved;

    // Node drag (Node tool) gesture.
    private PathItem? _nodePath;
    private SubPath? _nodeSub;
    private int _nodeIndex;
    private bool _nodeGrabHandle;
    private bool _nodeIsInHandle;
    private PathItem? _nodeBefore;
    private Point2D _nodeOriginalPoint;

    // Pen tool.
    private PathItem? _penPath;
    private PathNode? _penNode;          // anchor currently being placed / dragged
    private PathItem? _penBefore;
    private bool _penClosePending;

    public CanvasWorkspace()
    {
        ClipToBounds = true;
        Focusable = true;
    }

    /// <summary>Binds the workspace to the editor session it renders and edits.</summary>
    public void AttachEditor(EditorViewModel viewModel)
    {
        _viewModel = viewModel;
        _document = viewModel.Document;
        _layout = new PasteboardLayout(ComputeExtent());
        viewModel.DocumentChanged += (_, _) =>
        {
            // Preserve zoom and pan while re-clamping to the (possibly grown)
            // artwork extent so newly drawn objects outside the old pasteboard
            // remain reachable.
            double zoom = _layout.Zoom;
            Vector2D offset = _offset;
            _document = viewModel.Document;
            _layout = new PasteboardLayout(ComputeExtent()) { Zoom = zoom };
            _offset = _layout.ClampTopLeft(offset, ViewportPixels);
            InvalidateVisual();
        };
        InvalidateVisual();
    }

    /// <summary>Current zoom factor (0.02× .. 64×).</summary>
    public double Zoom
    {
        get => _layout.Zoom;
        private set
        {
            _layout.Zoom = value;
            InvalidateVisual();
        }
    }

    /// <summary>The viewport size in pixels, derived from our laid-out bounds.</summary>
    private Size2D ViewportPixels => new(Math.Max(Bounds.Width, 1), Math.Max(Bounds.Height, 1));

    // ------------------------------------------------------------------
    // Zoom / fit API (toolbar + status bar bind through these).
    // ------------------------------------------------------------------

    public void ZoomIn() => ZoomAtCenter(Zoom * 1.25);
    public void ZoomOut() => ZoomAtCenter(Zoom / 1.25);
    public void ZoomToActualSize() => ZoomAtCenter(1.0);

    public void ZoomToFit()
    {
        _layout.Zoom = _layout.ZoomToFit(ViewportPixels);
        _offset = _layout.CenterInViewport(ViewportPixels);
        InvalidateVisual();
    }

    /// <summary>Refits after the artwork extent changed drastically (e.g. open).</summary>
    public void RefreshFromDocument()
    {
        _layout = new PasteboardLayout(ComputeExtent());
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
    // Model ↔ screen mapping
    // ------------------------------------------------------------------

    private Point ModelToScreen(Point2D model)
        => new((model.X - _layout.Extent.Left) * _layout.Zoom + _offset.X,
               (model.Y - _layout.Extent.Top) * _layout.Zoom + _offset.Y);

    private Point2D ModelPointAtScreen(Point screen)
        => new((screen.X - _offset.X) / _layout.Zoom + _layout.Extent.Left,
               (screen.Y - _offset.Y) / _layout.Zoom + _layout.Extent.Top);

    /// <summary>Picking tolerance in model units: ~5 px regardless of zoom.</summary>
    private double PickTolerance => Math.Max(0.05, 5.0 / Math.Max(_layout.Zoom, 1e-6));

    /// <summary>The union of artboards and all painted content.</summary>
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

    // ------------------------------------------------------------------
    // Pointer interaction
    // ------------------------------------------------------------------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        Point position = e.GetPosition(this);
        Point2D model = ModelPointAtScreen(position);
        PointerPointProperties props = e.GetCurrentPoint(this).Properties;

        // Middle / right button always pans (space-equivalent for touchpads).
        if (props.IsMiddleButtonPressed || props.IsRightButtonPressed)
        {
            StartPan(e, position);
            return;
        }

        if (!props.IsLeftButtonPressed)
        {
            return;
        }

        _leftDown = true;
        e.Pointer.Capture(this);
        e.Handled = true;

        switch (_viewModel?.Tool ?? EditorTool.Select)
        {
            case EditorTool.Select:
                StartMoveGesture(model);
                break;

            case EditorTool.Node:
                StartNodeGesture(model);
                break;

            case EditorTool.Pen:
                PenPress(model);
                break;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        Point position = e.GetPosition(this);
        Point2D model = ModelPointAtScreen(position);
        _hoverModel = model;

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
            switch (_viewModel?.Tool ?? EditorTool.Select)
            {
                case EditorTool.Select:
                    UpdateMoveGesture(model);
                    break;

                case EditorTool.Node:
                    UpdateNodeGesture(model);
                    break;

                case EditorTool.Pen:
                    PenDrag(model);
                    break;
            }
        }

        // Hover feedback (pen rubber band, node hover) needs a repaint even when
        // not pressing.
        if (_viewModel?.Tool == EditorTool.Pen && _penPath is not null)
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

        switch (_viewModel?.Tool ?? EditorTool.Select)
        {
            case EditorTool.Select:
                CommitMoveGesture();
                break;

            case EditorTool.Node:
                CommitNodeGesture();
                break;

            case EditorTool.Pen:
                PenRelease(model);
                break;
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
            _layout.Zoom = _layout.Zoom * factor;
            _offset = new Vector2D(
                cursor.X - (before.X - _layout.Extent.Left) * _layout.Zoom,
                cursor.Y - (before.Y - _layout.Extent.Top) * _layout.Zoom);
            _offset = _layout.ClampTopLeft(_offset, ViewportPixels);
            InvalidateVisual();
        }
        else
        {
            Vector2D delta = new(0, -e.Delta.Y * 60.0);
            _offset = _layout.ClampTopLeft(_offset + delta, ViewportPixels);
            InvalidateVisual();
        }

        e.Handled = true;
    }

    private Point _lastPan;

    private void StartPan(PointerPressedEventArgs e, Point position)
    {
        _lastPan = position;
        _isPanning = true;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    // ------------------------------------------------------------------
    // Select tool: pick + move whole paths
    // ------------------------------------------------------------------

    private void StartMoveGesture(Point2D model)
    {
        PathItem? hit = HitTestTopPath(model);
        _viewModel!.Selection = hit;

        _moveItem = hit;
        _moveBefore = hit?.GeometrySnapshot();
        _gestureStartModel = model;
        _gestureMoved = false;
        InvalidateVisual();
    }

    private void UpdateMoveGesture(Point2D model)
    {
        if (_moveItem is null)
        {
            return;
        }

        Vector2D delta = model - _gestureStartModel;
        if (Math.Abs(delta.X) < 1e-9 && Math.Abs(delta.Y) < 1e-9)
        {
            return;
        }

        // Live feedback: restore the press-time geometry, then re-apply the whole
        // displacement. Re-applying from the snapshot avoids cumulative drift.
        _moveItem.RestoreGeometryFrom(_moveBefore!);
        _moveItem.TranslateGeometryBy(delta);
        _gestureMoved = true;
        InvalidateVisual();
    }

    private void CommitMoveGesture()
    {
        if (_moveItem is not null && _gestureMoved)
        {
            PathItem after = _moveItem.GeometrySnapshot();
            _viewModel!.Execute(new GeometryReplaceCommand(_moveItem, _moveBefore!, after, "Move"));
        }

        _moveItem = null;
        _moveBefore = null;
    }

    // ------------------------------------------------------------------
    // Node tool: drag anchors and handles
    // ------------------------------------------------------------------

    private void StartNodeGesture(Point2D model)
    {
        // Ensure the selection is a path (clicking an unselected path with the
        // node tool selects it first).
        if (_viewModel!.Selection is not PathItem path)
        {
            path = HitTestTopPath(model);
            _viewModel.Selection = path;
        }

        _nodePath = path;
        _nodeSub = null;
        _nodeGrabHandle = false;

        if (path is null)
        {
            return;
        }

        NodePick? pick = PathPicking.PickNode(path, model, PickTolerance * 1.5);
        if (pick is null)
        {
            return;
        }

        _nodeSub = pick.Value.SubPath;
        _nodeIndex = pick.Value.NodeIndex;
        _nodeGrabHandle = pick.Value.IsInHandle || pick.Value.IsOutHandle;
        _nodeIsInHandle = pick.Value.IsInHandle;
        _nodeBefore = path.GeometrySnapshot();
        PathNode node = pick.Value.SubPath.Nodes[pick.Value.NodeIndex];
        _nodeOriginalPoint = _nodeGrabHandle ? (_nodeIsInHandle ? node.InHandle : node.OutHandle) : node.Anchor;
        _gestureStartModel = model;
        _gestureMoved = false;
        InvalidateVisual();
    }

    private void UpdateNodeGesture(Point2D model)
    {
        if (_nodePath is null || _nodeSub is null)
        {
            return;
        }

        Vector2D delta = model - _gestureStartModel;
        if (Math.Abs(delta.X) < 1e-9 && Math.Abs(delta.Y) < 1e-9)
        {
            return;
        }

        _nodePath.RestoreGeometryFrom(_nodeBefore!);
        PathNode node = _nodeSub.Nodes[_nodeIndex];

        if (_nodeGrabHandle)
        {
            Point2D target = _nodeOriginalPoint + delta;
            if (_nodeIsInHandle)
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
            // Dragging an anchor carries both handles so adjacent curves keep
            // their shape.
            node.Anchor += delta;
            node.InHandle += delta;
            node.OutHandle += delta;
        }

        _gestureMoved = true;
        InvalidateVisual();
    }

    private void CommitNodeGesture()
    {
        if (_nodePath is not null && _gestureMoved && _nodeBefore is not null)
        {
            PathItem after = _nodePath.GeometrySnapshot();
            _viewModel!.Execute(new GeometryReplaceCommand(_nodePath, _nodeBefore, after, "Edit nodes"));
        }

        _nodePath = null;
        _nodeBefore = null;
        _nodeSub = null;
    }

    // ------------------------------------------------------------------
    // Pen tool
    // ------------------------------------------------------------------

    private void PenPress(Point2D model)
    {
        if (_viewModel is null)
        {
            return;
        }

        // Clicking the start anchor of the active path closes it.
        if (_penPath is not null && _penPath.SubPaths[0].Nodes.Count >= 2 &&
            _penPath.SubPaths[0].Nodes[0].Anchor.DistanceTo(model) <= PickTolerance * 2)
        {
            _penClosePending = true;
            _penBefore = _penPath.GeometrySnapshot();
            return;
        }

        // Otherwise begin a new anchor at the press point. Pen paths are committed
        // to the document on the first anchor so they render as they grow.
        if (_penPath is null)
        {
            _penPath = new PathItem { Name = "Pen path", Stroke = StrokeSpec.Hairline(ColorRgb.Black) };
            _penPath.AddSubPath(closed: false);
            _viewModel.Execute(new AddItemCommand(_viewModel.TargetLayer(), _penPath));
            _penBefore = _penPath.GeometrySnapshot();
        }
        else
        {
            _penBefore = _penPath.GeometrySnapshot();
        }

        SubPath sub = _penPath.SubPaths[0];
        _penNode = sub.AppendNode(model);
        _gestureStartModel = model;
        _gestureMoved = false;
        InvalidateVisual();
    }

    private void PenDrag(Point2D model)
    {
        if (_penNode is null || _penBefore is null || _penPath is null)
        {
            return;
        }

        Vector2D delta = model - _gestureStartModel;
        if (Math.Abs(delta.X) < 1e-9 && Math.Abs(delta.Y) < 1e-9)
        {
            return;
        }

        _gestureMoved = true;

        // Live: pull the outgoing handle of the anchor being placed towards the
        // cursor. Restoring from the pre-press snapshot keeps this a pure function
        // of the total drag.
        _penPath.RestoreGeometryFrom(_penBefore);
        _penNode = _penPath.SubPaths[0].Nodes[^1];
        _penNode.OutHandle = model;
        InvalidateVisual();
    }

    private void PenRelease(Point2D model)
    {
        if (_viewModel is null)
        {
            return;
        }

        if (_penClosePending && _penPath is not null)
        {
            // Final geometry replace: mark the subpath closed.
            _penPath.SubPaths[0].IsClosed = true;
            CommitPenGeometry("Close path");
            return;
        }

        if (_penPath is not null && _penNode is not null)
        {
            CommitPenGeometry(_gestureMoved ? "Add curve" : "Add point");
            return;
        }

        _penNode = null;
    }

    private void CommitPenGeometry(string description)
    {
        if (_penPath is null || _penBefore is null)
        {
            _penNode = null;
            return;
        }

        PathItem after = _penPath.GeometrySnapshot();
        _viewModel!.Execute(new GeometryReplaceCommand(_penPath, _penBefore, after, description));
        _penNode = null;
        _penBefore = null;
        _penClosePending = false;
        InvalidateVisual();
    }

    /// <summary>Ends an active pen path: removes a stray single-anchor path.</summary>
    private void FinalizePen()
    {
        if (_penPath is null)
        {
            return;
        }

        if (_penPath.SubPaths[0].Nodes.Count < 2)
        {
            _viewModel!.Execute(new RemoveItemCommand(_penPath));
        }

        _penPath = null;
        _penNode = null;
        _penBefore = null;
        InvalidateVisual();
    }

    // ------------------------------------------------------------------
    // Hit testing
    // ------------------------------------------------------------------

    /// <summary>
    /// The top-most (last-painted) path item under <paramref name="model"/>, or
    /// null. Searches every artboard and honours z-order across layers, groups
    /// and sibling order (identity-transform hierarchies for this seed).
    /// </summary>
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
                        topmost = hit; // later iterations paint above earlier ones
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

    /// <summary>Selection chrome, node handles and the pen preview overlay.</summary>
    private void PaintOverlays(DrawingContext context)
    {
        if (_viewModel is null)
        {
            return;
        }

        EditorTool tool = _viewModel.Tool;
        if (_viewModel.Selection is PathItem selected)
        {
            // Node tool exposes full anchors/handles; the select tool draws a
            // light bounding box around the picked path.
            PaintNodeOverlay(context, selected, fullNodeUi: tool == EditorTool.Node);
        }

        if (tool == EditorTool.Pen && _penPath is not null && _penPath.SubPaths[0].Nodes.Count > 0)
        {
            PaintPenOverlay(context);
        }
    }

    /// <summary>
    /// Draws anchors/handles of the selected path. In node tool every node is
    /// drawn; the select tool draws a light bounding box only.
    /// </summary>
    private void PaintNodeOverlay(DrawingContext context, PathItem path, bool fullNodeUi)
    {
        IBrush accent = new SolidColorBrush(Color.FromRgb(0x19, 0x76, 0xD2));
        var anchorPen = new Pen(accent, 1.0);
        double half = Math.Max(2.0 / _layout.Zoom, 0.5);

        if (!fullNodeUi)
        {
            Rect2D box = path.BoundingBox();
            if (box.IsEmpty)
            {
                return;
            }

            Point tl = ModelToScreen(new Point2D(box.Left, box.Top));
            var screen = new Rect(tl.X, tl.Y, box.Width * _layout.Zoom, box.Height * _layout.Zoom);
            var selBrush = new SolidColorBrush(Color.FromArgb(60, 0x19, 0x76, 0xD2));
            context.DrawRectangle(selBrush, anchorPen, screen);
            return;
        }

        // Node tool: handles then anchors so anchors stay clickable on top.
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
                    context.DrawEllipse(Brushes.White, anchorPen, ModelToScreen(node.InHandle), half, half);
                }

                if (!node.HasStraightOutgoing)
                {
                    context.DrawEllipse(Brushes.White, anchorPen, ModelToScreen(node.OutHandle), half, half);
                }

                context.DrawRectangle(Brushes.White, anchorPen,
                    new Rect(anchor.X - half, anchor.Y - half, half * 2, half * 2));
            }
        }
    }

    private void DrawHandleLine(DrawingContext context, Point from, Point to)
    {
        var lineBrush = new SolidColorBrush(Color.FromArgb(220, 0x76, 0x76, 0x76));
        context.DrawLine(new Pen(lineBrush, 1.0), from, to);
    }

    private void PaintPenOverlay(DrawingContext context)
    {
        SubPath sub = _penPath!.SubPaths[0];
        var previewPen = new Pen(new SolidColorBrush(Color.FromRgb(0x19, 0x76, 0xD2)), 1.0);
        previewPen.DashStyle = new DashStyle(new[] { 4.0, 3.0 }, 0);

        Point last = ModelToScreen(sub.Nodes[^1].Anchor);

        // Rubber band from the last anchor to the hovered position.
        if (_hoverModel is { } hover && sub.Nodes.Count >= 1)
        {
            context.DrawLine(previewPen, last, ModelToScreen(hover));
        }

        // First anchor indicator for "click here to close".
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
        if (_viewModel is null)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.V:
                _viewModel.Tool = EditorTool.Select;
                FinalizePen();
                e.Handled = true;
                break;

            case Key.A:
                _viewModel.Tool = EditorTool.Node;
                FinalizePen();
                e.Handled = true;
                break;

            case Key.P:
                _viewModel.Tool = EditorTool.Pen;
                e.Handled = true;
                break;

            case Key.Delete:
                _viewModel.DeleteSelection();
                e.Handled = true;
                break;

            case Key.Escape or Key.Enter:
                FinalizePen();
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
