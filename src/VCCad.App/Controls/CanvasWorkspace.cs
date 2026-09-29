using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using VCCad.App.Fonts;
using VCCad.App.ViewModels;
using VCCad.Core.Text;
using VCCad.Core.Commands;
using VCCad.Core.Model;
using VCCad.Core.Selection;
using ModelFillRule = VCCad.Core.Model.FillRule;
using ModelTextAlignment = VCCad.Core.Model.TextAlignment;
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

    // Images carry a placement rather than geometry, so they travel in their own lists.
    private readonly List<ImageItem> _resizeImages = new();
    private readonly Dictionary<ImageItem, Rect2D> _resizeImageBefore = new();
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
    private readonly List<ImageItem> _dragImages = new();
    private readonly Dictionary<ImageItem, Rect2D> _dragImageOrigins = new();
    private readonly Dictionary<TextItem, Point2D> _dragTextOrigins = new();
    private Point2D _dragStartModel;

    /// <summary>Caret blink state; the caret is drawn only when this is true.</summary>
    private bool _caretOn = true;

    private Avalonia.Threading.DispatcherTimer? _caretTimer;

    /// <summary>Starts the caret blinking while a block is edited, and stops it after.</summary>
    private void UpdateCaretBlink()
    {
        if (_editingText is null)
        {
            _caretTimer?.Stop();
            _caretTimer = null;
            _caretOn = true;
            return;
        }

        if (_caretTimer is not null)
        {
            return;
        }

        _caretTimer = new Avalonia.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(530),
        };
        _caretTimer.Tick += (_, _) =>
        {
            _caretOn = !_caretOn;
            InvalidateVisual();
        };
        _caretTimer.Start();
    }

    /// <summary>Where a text frame drag began, while the text tool is drawing a box.</summary>
    private Point2D? _textFrameStart;

    // Edit-box handle drag: which handle, and where the drag began.
    private int _frameResizeHandle = -1;
    private double _frameResizeStartWidth;
    private double _frameResizeStartLocalX;

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
    private int _editAnchor;
    private bool _textSelecting;
    private string? _textClipboard;

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
        RegisterEmbeddedFonts(_document);
        _layout = new PasteboardLayout(ComputeExtent());
        viewModel.DocumentChanged += (_, _) =>
        {
            double zoom = _layout.Zoom;
            Vector2D offset = _offset;
            _document = viewModel.Document;
            RegisterEmbeddedFonts(_document);
            _layout = new PasteboardLayout(ComputeExtent()) { Zoom = zoom };
            _offset = _layout.ClampTopLeft(offset, ViewportPixels);

            // Undo/redo/open/style changes should rebuild the chrome; live
            // gestures manage it themselves and must not lose the orientation.
            if (!AnyGestureActive())
            {
                _chromeRect = null;
                _chromeAngle = 0;
            }

            ClearGeometryCache();
            _brushCache.Clear();
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
            InvalidateVisual();
        };

        // Lightweight repaints (layer/artboard visibility, stroke colour) raised
        // by panes that mutate the model without going through a command.
        viewModel.TransformChanged += (_, _) => InvalidateVisual();
        InvalidateVisual();
    }

    public double Zoom
    {
        get => _layout.Zoom;
        private set => SetZoom(value);
    }

    /// <summary>
    /// Raised when the view changes (zoom, fit, clipping) — including changes made
    /// through the automation API, so the status bar never shows a stale figure.
    /// </summary>
    public event EventHandler? ViewChanged;

    /// <summary>
    /// Whether each artboard clips its own content to the page box, as a PDF viewer
    /// does. On by default: an imported tiled document draws full-size artwork on
    /// every sheet and relies on the page edge to cut it, so without this the pieces
    /// and labels sprawl across the pasteboard. Turn it off to inspect that overflow.
    /// </summary>
    public bool ClipToArtboard
    {
        get => _clipToArtboard;
        set
        {
            if (_clipToArtboard == value)
            {
                return;
            }

            _clipToArtboard = value;
            _layout = new PasteboardLayout(ComputeExtent()) { Zoom = _layout.Zoom };
            ZoomToFit();
            InvalidateVisual();
            ViewChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool _clipToArtboard = true;

    private Size2D ViewportPixels => new(Math.Max(Bounds.Width, 1), Math.Max(Bounds.Height, 1));

    /// <summary>
    /// Space at the bottom of the viewport that must stay clear when fitting — the
    /// diagnostics overlay. Without it a fitted artboard would slide underneath the
    /// panel and the person could not see the artwork they are working on.
    /// </summary>
    public double ViewportInsetBottom { get; set; }

    /// <summary>True until the person changes the zoom themselves.</summary>
    private bool _userAdjustedZoom;

    public void ZoomIn()
    {
        _userAdjustedZoom = true;
        ZoomAtCenter(Zoom * 1.25);
    }

    public void ZoomOut()
    {
        _userAdjustedZoom = true;
        ZoomAtCenter(Zoom / 1.25);
    }

    public void ZoomToActualSize()
    {
        _userAdjustedZoom = true;
        ZoomAtCenter(1.0);
    }

    /// <summary>True while the view is still auto-fitting (the person has not zoomed).</summary>
    public bool IsAutoFit => !_userAdjustedZoom;

    /// <summary>Sets an absolute zoom factor, centred on the current view.</summary>
    public void ZoomTo(double factor)
    {
        _userAdjustedZoom = true;
        ZoomAtCenter(factor);
    }

    /// <summary>
    /// Scrolls the view so <paramref name="model"/> sits in the middle of the visible
    /// area. A person pans with the scrollbars; this is the same thing as an operation.
    /// </summary>
    public void CenterOn(Point2D model)
    {
        Size2D vp = UsableViewport;
        _offset = new Vector2D(vp.Width / 2 - (model.X - _layout.Extent.Left) * _layout.Zoom,
                               vp.Height / 2 - (model.Y - _layout.Extent.Top) * _layout.Zoom);
        InvalidateVisual();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The model point at the middle of the visible area.</summary>
    public Point2D ViewCenter
    {
        get
        {
            Size2D vp = UsableViewport;
            return ModelPointAtScreen(new Point(vp.Width / 2, vp.Height / 2));
        }
    }

    /// <summary>Fits the document into the visible viewport (above the diagnostics overlay)
    /// and hands control of the zoom back to the automatic behaviour.
    /// </summary>
    public void ZoomToFit()
    {
        _userAdjustedZoom = false;
        Size2D vp = UsableViewport;
        SetZoom(_layout.ZoomToFit(vp));
        _offset = _layout.CenterInViewport(vp);
        InvalidateVisual();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Applies a zoom level and notifies observers. Every zoom change must go through
    /// here: writing <c>_layout.Zoom</c> directly used to leave the status bar showing
    /// the previous figure.
    /// </summary>
    /// <summary>Zoom for an off-screen page render (see <see cref="Views.PageRenderer"/>).</summary>
    public void SetZoomForExport(double factor) => SetZoom(factor);

    /// <summary>
    /// A model point in *window* coordinates, which is what a pointer event and
    /// <c>input.pointer</c> use. <see cref="ModelToScreen"/> is relative to this control,
    /// so the control's own offset in the window has to be added or every aim lands a
    /// toolbar's height out.
    /// </summary>
    public Point ModelToWindow(Point2D model)
    {
        Point local = ModelToScreen(model);
        return this.TranslatePoint(local, TopLevel.GetTopLevel(this) ?? (Visual)this) ?? local;
    }

    /// <summary>A window point in model coordinates.</summary>
    public Point2D WindowToModel(Point window)
    {
        Point local = TopLevel.GetTopLevel(this)?.TranslatePoint(window, this) ?? window;
        return ModelPointAtScreen(local);
    }

    private void SetZoom(double value)
    {
        _layout.Zoom = value;
        InvalidateVisual();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The viewport minus the reserved bottom inset.</summary>
    private Size2D UsableViewport
    {
        get
        {
            Size2D vp = ViewportPixels;
            return new Size2D(vp.Width, Math.Max(1, vp.Height - Math.Max(0, ViewportInsetBottom)));
        }
    }

    private void ZoomAtCenter(double factor)
    {
        SetZoom(factor);
        Size2D vp = ViewportPixels;
        Point2D centre = ModelPointAtScreen(new Point(vp.Width / 2, vp.Height / 2));
        _offset = new Vector2D(vp.Width / 2 - (centre.X - _layout.Extent.Left) * _layout.Zoom,
                               vp.Height / 2 - (centre.Y - _layout.Extent.Top) * _layout.Zoom);
        InvalidateVisual();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);

        // Refit on every size change until the person takes control of the zoom.
        // Fitting only once is not enough: the window is resized after it opens (it
        // is docked to a screen half), and the one-shot fit would leave the artboard
        // small in the larger viewport.
        if (e.NewSize.Width > 10 && e.NewSize.Height > 10 && _document is not null && !_userAdjustedZoom)
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

            // Only count artwork that is actually visible. When pages clip their own
            // content, a tiled document's overflow must not stretch the extent — it
            // would push "fit" out to a zoom where the pages are unreadable.
            if (!ClipToArtboard)
            {
                extent = extent.Union(artboard.ArtworkBounds());
            }
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

        // Only the artboard that CONTAINS the point. Testing every artboard's local
        // coordinates in turn made a click on page 1 select whatever sat under the same offset
        // on page 6 - a point inside one page is also a point inside another page shifted by
        // five of them, and the later one won.
        Artboard? board = SelectionEngine.ArtboardAt(_document.Artboards, model);

        if (board is not null)
        {
            // Nearest, not merely near enough: two objects can both fall within the pick
            // tolerance of one click, and the one the person meant is the one they are
            // closest to. The engine owns that rule so the canvas and the tests agree.
            topmost = SelectionEngine.Nearest(board, model, tolerance) ?? topmost;
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
            // An image has no outline to pick, so its box is its geometry and a click
            // anywhere inside it selects it.
            ImageItem image when image.Placement.Contains(model) => image,
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

        // Text editing: clicks inside position the caret / select; outside exits.
        if (_editingText is { } editing)
        {
            // A handle takes priority over placing the caret.
            int handle = HandleAt(editing, model);
            if (handle >= 0)
            {
                _frameResizeHandle = handle;
                _frameResizeStartWidth = editing.FrameWidth > 0
                    ? editing.FrameWidth
                    : Math.Max(MeasureText(editing).MaxWidth, 24);
                _frameResizeStartLocalX = ToTextLocal(editing, model).X;
                _gestureMoved = false;

                // Capture, or the moves that carry the resize may never come back here.
                e.Pointer.Capture(this);
                e.Handled = true;
                return;
            }

            Point2D localPoint = ToTextLocal(editing, model);
            if (TextContains(editing, model))
            {
                int index = IndexAtLocal(editing, localPoint);
                if (e.ClickCount >= 2)
                {
                    (int wordStart, int wordEnd) = WordBounds(editing, index);
                    _editAnchor = wordStart;
                    _caret = wordEnd;
                }
                else
                {
                    _caret = index;
                    if (!e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                    {
                        _editAnchor = index;
                    }
                }

                _textSelecting = true;
                e.Pointer.Capture(this);
                AfterTextEdit();
                e.Handled = true;
                return;
            }

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
            case EditorTool.Select:
            case EditorTool.Lasso:
                SelectPress(model);
                break;

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
                // A handle on the block already being edited takes the press; without this
                // the text tool starts a brand-new frame instead and the handles drawn on
                // the box can never be grabbed.
                if (_editingText is { } current && HandleAt(current, model) is int grabbed && grabbed >= 0)
                {
                    _frameResizeHandle = grabbed;
                    _frameResizeStartWidth = current.FrameWidth > 0
                        ? current.FrameWidth
                        : Math.Max(MeasureText(current).MaxWidth, 24);
                    _frameResizeStartLocalX = ToTextLocal(current, model).X;
                    _gestureMoved = false;
                    break;
                }

                // New text uses the face the person last chose, which is what makes
                // picking a font with nothing selected mean something.
                TextItem created = _vm!.CreateTextAt(
                    model, _vm.DefaultFontFamily ?? TextItem.DefaultFontFamily, 12);
                created.Color = _vm.CurrentFill.IsVisible ? _vm.CurrentFill.Color : ColorRgb.Black;
                // Drag out a box to give it a frame width; a plain click leaves the block
                // auto-width, so both "type a line" and "draw a text box" are available.
                _textFrameStart = model;
                EnterTextEdit(created);
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

        // An I-beam over text is the only thing that tells a person the text can be
        // typed into; without it a text block looks like any other object.
        Cursor = _editingText is not null || HitTestTopItem(model) is TextItem
            ? new Cursor(StandardCursorType.Ibeam)
            : Cursor.Default;

        if (_frameResizeHandle >= 0 && _editingText is not null)
        {
            _gestureMoved = true;
            DragEditBoxHandle(model);
            return;
        }

        if (_textSelecting && _editingText is { } selText)
        {
            _caret = IndexAtLocal(selText, ToTextLocal(selText, model));
            AfterTextEdit();
            return;
        }

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

                // The lasso's path grows here. Without this case the tool recorded where the
                // drag began and nothing after it, so every lasso enclosed a zero-area region
                // and selected nothing - the tool looked broken while the selection rules
                // underneath were fine all along.
                case EditorTool.Lasso:
                    SelectDrag(model);
                    break;

                case EditorTool.Pen: PenDrag(model); break;
                case EditorTool.Rectangle:
                case EditorTool.Ellipse:
                    if (_shapeStart is not null)
                    {
                        _shapeCurrent = _shiftHeld ? ConstrainSquare(_shapeStart.Value, model) : model;
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

        // Releasing an edit-box handle ends the resize; the width has already been applied.
        if (_frameResizeHandle >= 0)
        {
            _frameResizeHandle = -1;
            e.Pointer.Capture(null);
            InvalidateVisual();
            return;
        }

        if (_textSelecting)
        {
            _textSelecting = false;
            e.Pointer.Capture(null);
            return;
        }

        // Finishing a text-frame drag: the box the person drew becomes the wrap width.
        if (_textFrameStart is { } frameStart)
        {
            _textFrameStart = null;
            if (_editingText is { } framed)
            {
                double width = Math.Abs(model.X - frameStart.X);
                if (width >= 24)
                {
                    framed.FrameWidth = width;
                    AfterTextEdit();
                    InvalidateVisual();

                    if (_leftDown)
                    {
                        _leftDown = false;
                        e.Pointer.Capture(null);
                    }

                    return;
                }
            }
        }

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

            // The press set the marquee going and the moves grew its path; without this the
            // release never resolved it, so the whole gesture was thrown away at the end.
            case EditorTool.Lasso: SelectRelease(model); break;

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
            _userAdjustedZoom = true;
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
        if (_vm is null || !_vm.HasTransformableSelection || _shiftHeld)
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

        // The lasso is a selection gesture wherever it starts, so a press begins its path
        // even over an object - being diverted into moving that object would make the tool
        // useless in a drawing, which is exactly where it is wanted.
        if (_vm?.Tool == EditorTool.Lasso)
        {
            if (_document is not null)
            {
                _focusedArtboard = SelectionEngine.Click(_document, model, _focusedArtboard).Focused;
            }

            // The button state is set here rather than after, because the move handler only
            // follows a drag while the button is down - returning before this made the lasso
            // record where it started and nothing else, which is why it selected nothing
            // however carefully it was aimed.
            _leftDown = true;

            BeginMarquee(model);
            return;
        }

        LayerItem? hit = HitTestTopItem(model);

        // Whatever the click found, it also decides which artboard is focused - and that is
        // what the next marquee is measured against. The engine owns the rule so the canvas
        // and the tests cannot disagree about it.
        if (_document is not null)
        {
            _focusedArtboard = SelectionEngine.Click(_document, model, _focusedArtboard).Focused;
        }

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
        _dragImages.Clear();
        _dragImageOrigins.Clear();
        foreach (ImageItem image in _vm!.SelectedObjects.OfType<ImageItem>())
        {
            _dragImages.Add(image);
            _dragImageOrigins[image] = image.Placement;
        }

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

            if (_lassoPath.Count > 0)
            {
                _lassoPath.Add(model);
            }

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

        // An image's geometry is its placement, so dragging shifts the box and the
        // picture travels with it.
        foreach (ImageItem image in _dragImages)
        {
            Rect2D start = _dragImageOrigins[image];
            image.Placement = new Rect2D(
                start.X + delta.X, start.Y + delta.Y, start.Width, start.Height);
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
            RehomeAfterDrag(model);
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

    /// <summary>
    /// Moves a dragged selection onto the page it was dropped on, or off the page altogether.
    ///
    /// The pointer decides, because during a drag the pointer is the honest answer - unlike a
    /// translation that arrives through the API, which has nothing to hover with and uses the
    /// end of its own vector instead. Dragging something off one page and onto another is how
    /// a person moves it between pages, so where it is dropped is where it now belongs.
    /// </summary>
    private void RehomeAfterDrag(Point2D pointer)
    {
        if (_document is null || _vm is null)
        {
            return;
        }

        LayerItem[] moving = _vm.SelectedObjects.ToArray();
        if (moving.Length == 0)
        {
            return;
        }

        if (SelectionEngine.RehomeTarget(_document, moving, pointer) is not { } target)
        {
            return;
        }

        _vm.MoveItems(
            moving.Where(i => !ReferenceEquals(i.Container, target)).ToArray(),
            target,
            target.Children.Count);
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

    /// <summary>
    /// The artboard the last gesture focused.
    ///
    /// Kept between gestures because it decides what a marquee means: one started on a page is
    /// about that page, and one started on the pasteboard is about whole pages. Losing it
    /// between a press and a move would change the answer mid-gesture.
    /// </summary>
    private Artboard? _focusedArtboard;

    /// <summary>
    /// The pointer's own path while the lasso is dragging.
    ///
    /// Kept as points rather than as a shape so the freehand gesture and the rectangular
    /// marquee go through the same selection call: a marquee is a rectangular path, and this
    /// is any path.
    /// </summary>
    private readonly List<Point2D> _lassoPath = new();

    private void BeginMarquee(Point2D model)
    {
        _marqueeActive = true;
        _marqueeStart = model;
        _marqueeCurrent = model;
        _lassoPath.Clear();

        if (_vm?.Tool == EditorTool.Lasso)
        {
            _lassoPath.Add(model);
        }

        InvalidateVisual();
    }

    private void FinishMarquee()
    {
        _marqueeActive = false;

        if (_vm is null || _document is null)
        {
            InvalidateVisual();
            return;
        }

        // The rules live in SelectionEngine and are replayed from a document and a list of
        // events, so the canvas asks the same code the tests do rather than keeping a second
        // copy of them that can drift. A marquee that began on a page stays about that page
        // however far it is dragged, and one that encloses a page whole selects the page.
        //
        // A lasso is the same call with the pointer's own path instead of four corners: the
        // path is a path either way, which is why there is only one of these.
        SelectionResult result = _lassoPath.Count >= 2
            ? SelectionEngine.ByLasso(_document, _marqueeStart, _lassoPath, _focusedArtboard)
            : SelectionEngine.Marquee(_document, _marqueeStart, _marqueeCurrent, _focusedArtboard);

        _lassoPath.Clear();

        _focusedArtboard = result.Focused;

        if (result.Artboards.Count > 0)
        {
            _vm.SelectArtboard(result.Artboards[0]);
        }
        else if (_shiftHeld)
        {
            foreach (LayerItem item in result.Items)
            {
                _vm.SelectRange(new[] { item }, additive: true);
            }
        }
        else
        {
            _vm.SelectRange(result.Items, additive: false);
        }

        InvalidateVisual();
    }

    /// <summary>
    /// Whether an object meets a rubber-band rectangle.
    ///
    /// A path is asked about its geometry, not its box: on a tiled pattern a diagonal
    /// line has a page-sized box, so a box test selects it for any rectangle that
    /// overlaps the box at all, however far away the line actually is. Every other kind
    /// of object keeps the rectangle test, which is honest for it — a text block's box
    /// *is* its geometry.
    /// </summary>
    private bool ItemMeetsRect(LayerItem item, Rect2D rect)
    {
        if (item is PathItem path)
        {
            // The rectangle is in document space; a path's geometry is artboard-local.
            Rect2D local = rect;
            Vector2D offset = item.ArtboardOffset();
            if (offset.X != 0 || offset.Y != 0)
            {
                local = new Rect2D(rect.X - offset.X, rect.Y - offset.Y, rect.Width, rect.Height);
            }

            return PathPicking.IntersectsRect(path, local);
        }

        Rect2D bounds = ItemBounds(item);
        return !bounds.IsEmpty && bounds.Intersects(rect);
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
                    if (ItemMeetsRect(item, rect))
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
            case TextItem text:
                // Marquee selection must see text too, otherwise a drag never
                // catches a text block even though clicking one selects it.
                Rect2D t = text.BoundingBox();
                return t.IsEmpty ? t : new Rect2D(t.X + offset.X, t.Y + offset.Y, t.Width, t.Height);
            case ArtGroup group:
                Rect2D g = group.Transform.Transform(group.BoundingBox());
                return g.IsEmpty ? g : new Rect2D(g.X + offset.X, g.Y + offset.Y, g.Width, g.Height);
            case ImageItem image:
                // The placement is the image's box, so selection chrome and the resize
                // handles land on the picture itself.
                Rect2D i = image.Placement;
                return i.IsEmpty ? i : new Rect2D(i.X + offset.X, i.Y + offset.Y, i.Width, i.Height);
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

        _resizeImages.Clear();
        _resizeImageBefore.Clear();
        foreach (ImageItem image in _vm!.SelectedObjects.OfType<ImageItem>())
        {
            _resizeImages.Add(image);
            _resizeImageBefore[image] = image.Placement;
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

        // An image scales by moving and resizing its placement box. Scaling is not
        // uniform unless the handle (or Shift) made it so: the pixels stretch to fill the
        // box, which is what a person sees happen to a picture they drag a corner of.
        foreach (ImageItem image in _resizeImages)
        {
            Rect2D start = _resizeImageBefore[image];
            Vector2D offset = image.ArtboardOffset();
            Point2D pivotLocal = _resizePivot - offset;

            double x = pivotLocal.X + ((start.X - pivotLocal.X) * sx);
            double y = pivotLocal.Y + ((start.Y - pivotLocal.Y) * sy);
            double w = start.Width * Math.Abs(sx);
            double h = start.Height * Math.Abs(sy);

            // A negative scale mirrors the image; the placement box stays positive and the
            // origin moves to the far corner.
            if (sx < 0)
            {
                x -= w;
            }

            if (sy < 0)
            {
                y -= h;
            }

            image.Placement = new Rect2D(x, y, Math.Max(0.5, w), Math.Max(0.5, h));
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

            InvalidateGeometry(_nodePath);
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
            InvalidateGeometry(_segmentPath);
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
            _penPath = new PathItem { Name = "Path", Stroke = _vm.CurrentStroke.HasVisibleOutline ? _vm.CurrentStroke : StrokeSpec.Hairline(ColorRgb.Black) };
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
        if (_penPath is not null)
        {
            InvalidateGeometry(_penPath);
        }

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
                // Moving the page carries its contents, because their geometry is stored
                // relative to the artboard origin. Nothing to collect and translate.
                _artboardGesture = ArtboardGesture.Move;
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

                // The artwork is stored relative to the artboard origin, so moving the page
                // carries it: translating the children's own geometry here as well moved
                // them twice as far as the page and walked them off the sheet.
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
                // One command: moving the bounds is the whole edit, because the children
                // travel with the origin.
                _vm.Execute(new SetArtboardBoundsCommand(
                    _artboard, _artboardBefore, _artboard.Bounds, "Move artboard"));
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
        InvalidateVisual();
    }

    // ------------------------------------------------------------------
    // Shape tools
    // ------------------------------------------------------------------

    /// <summary>Constrains a drag to a square/circle (equal extents) when Shift.</summary>
    private static Point2D ConstrainSquare(Point2D start, Point2D current)
    {
        double dx = current.X - start.X;
        double dy = current.Y - start.Y;
        double size = Math.Max(Math.Abs(dx), Math.Abs(dy));
        return new Point2D(start.X + Math.Sign(dx == 0 ? 1 : dx) * size,
                           start.Y + Math.Sign(dy == 0 ? 1 : dy) * size);
    }

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
        shape.Fill = _vm!.CurrentFill;
        shape.Stroke = _vm.CurrentStroke;
        if (!shape.Fill.IsVisible && !shape.Stroke.HasVisibleOutline)
        {
            shape.Stroke = StrokeSpec.Hairline(ColorRgb.Black);
        }

        _vm.Execute(new AddItemCommand(shapeLayer, shape));
        _vm.SelectObject(shape);
        _vm.Status = rect ? "Rectangle created" : "Ellipse created";
    }

    // ------------------------------------------------------------------
    // Rendering
    // ------------------------------------------------------------------

    // Cached resources (rebuilt only when geometry/colour changes).
    private readonly Dictionary<PathItem, (int Revision, StreamGeometry Geometry)> _geometryCache = new();
    private readonly Dictionary<uint, IBrush> _brushCache = new();
    private static readonly IBrush BackgroundBrush = new SolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x1F));
    private static readonly IBrush PasteboardBrush = new SolidColorBrush(Color.FromRgb(0x23, 0x23, 0x27));
    private static readonly IBrush PageBrush = new SolidColorBrush(Colors.White);
    private static readonly IBrush PageBorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x42));
    private static readonly IBrush ShadowBrush = new SolidColorBrush(Color.FromArgb(110, 0, 0, 0));
    private Rect2D _worldViewport = Rect2D.Empty;

    /// <summary>Drops cached geometry (called on structural/geometry changes).</summary>
    private void ClearGeometryCache() => _geometryCache.Clear();

    private void InvalidateGeometry(PathItem path) => _geometryCache.Remove(path);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (_document is null)
        {
            return;
        }

        context.FillRectangle(BackgroundBrush, new Rect(Bounds.Size));

        double z = Math.Max(_layout.Zoom, 1e-6);
        double tx = _offset.X - _layout.Extent.Left * z;
        double ty = _offset.Y - _layout.Extent.Top * z;
        Avalonia.Matrix world = Avalonia.Matrix.CreateScale(z, z) * Avalonia.Matrix.CreateTranslation(tx, ty);

        _worldViewport = Rect2D.FromPoints(
            ModelPointAtScreen(new Point(0, 0)),
            ModelPointAtScreen(new Point(Bounds.Width, Bounds.Height)));

        Rect2D extent = _layout.Extent;
        using (context.PushTransform(world))
        {
            context.FillRectangle(PasteboardBrush, new Rect(extent.Left, extent.Top, extent.Width, extent.Height));

            foreach (Artboard artboard in _document.Artboards)
            {
                if (artboard.IsVisible)
                {
                    PaintArtboardWorld(context, artboard);
                }
            }

            if (_document.Orphans.IsVisible)
            {
                foreach (LayerItem item in _document.Orphans.Children)
                {
                    PaintItem(context, item, _document.Orphans.Opacity);
                }
            }
        }

        PaintArtboardLabels(context);
        PaintOverlays(context);
    }

    private void PaintArtboardWorld(DrawingContext context, Artboard artboard)
    {
        Rect2D r = artboard.Bounds;
        double z = Math.Max(_layout.Zoom, 1e-6);
        var rect = new Rect(r.Left, r.Top, r.Width, r.Height);

        context.FillRectangle(ShadowBrush, new Rect(rect.X + 3 / z, rect.Y + 3 / z, rect.Width, rect.Height));
        context.FillRectangle(PageBrush, rect);
        context.DrawRectangle(null, new Pen(PageBorderBrush, 1 / z), rect);

        // A page clips its own content, exactly as a PDF viewer does: anything
        // outside the MediaBox is never visible. An imported tiled document depends
        // on this — it draws each piece at full size on every sheet it touches and
        // lets the page edge do the cutting — so without it the artwork spills across
        // the pasteboard. The document keeps the overflow; only the view clips it.
        if (ClipToArtboard)
        {
            using (context.PushClip(rect))
            {
                PaintLayers(context, artboard);
            }
        }
        else
        {
            PaintLayers(context, artboard);
        }
    }

    private void PaintLayers(DrawingContext context, Artboard artboard)
    {
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

        // An item keeps the clips the file put on it, and the canvas has to honour them or
        // the person sees artwork the document says is hidden. They were imported, dumped,
        // round-tripped and exported, and drawn nowhere: the view clipped each page to its
        // box and left it at that.
        //
        // One clip per scope, pushed in turn, because several clips intersect — a file that
        // sets two means "inside both". Putting them in one geometry would union them and
        // show more than the file allows.
        var scopes = new List<DrawingContext.PushedState>();
        try
        {
            foreach (StreamGeometry clip in ClipGeometries(item))
            {
                scopes.Add(context.PushGeometryClip(clip));
            }

            PaintItemCore(context, item, opacity);
        }
        finally
        {
            for (int i = scopes.Count - 1; i >= 0; i--)
            {
                scopes[i].Dispose();
            }
        }
    }

    /// <summary>One geometry per clip on the item, in the order the file set them.</summary>
    private static IEnumerable<StreamGeometry> ClipGeometries(LayerItem item)
    {
        if (!item.IsClipped)
        {
            yield break;
        }

        Vector2D offset = item.ArtboardOffset();

        foreach (ClipSpec clip in item.Clips)
        {
            var geometry = new StreamGeometry();

            using (StreamGeometryContext g = geometry.Open())
            {
                g.SetFillRule(clip.Rule == ModelFillRule.EvenOdd
                    ? MediaFillRule.EvenOdd
                    : MediaFillRule.NonZero);

                foreach (SubPath sub in clip.SubPaths)
                {
                    if (sub.Nodes.Count == 0)
                    {
                        continue;
                    }

                    g.BeginFigure(
                        new Point(sub.Nodes[0].Anchor.X + offset.X, sub.Nodes[0].Anchor.Y + offset.Y),
                        sub.IsClosed);

                    int last = sub.IsClosed ? sub.Nodes.Count : sub.Nodes.Count - 1;
                    for (int i = 0; i < last; i++)
                    {
                        PathNode from = sub.Nodes[i];
                        PathNode to = sub.Nodes[(i + 1) % sub.Nodes.Count];
                        g.CubicBezierTo(
                            new Point(from.OutHandle.X + offset.X, from.OutHandle.Y + offset.Y),
                            new Point(to.InHandle.X + offset.X, to.InHandle.Y + offset.Y),
                            new Point(to.Anchor.X + offset.X, to.Anchor.Y + offset.Y));
                    }

                    g.EndFigure(sub.IsClosed);
                }
            }

            yield return geometry;
        }
    }

    private void PaintItemCore(DrawingContext context, LayerItem item, double opacity)
    {

        switch (item)
        {
            case PathItem path when path.IsVisible:
                Rect2D bounds = path.WorldBounds();
                if (!bounds.IsEmpty &&
                    !bounds.Inflated(path.Stroke.Width + 1).Intersects(_worldViewport))
                {
                    return; // culled (off-screen)
                }

                PaintPath(context, path, opacity * path.Opacity);
                break;

            case TextItem text when text.IsVisible:
                PaintText(context, text, opacity);
                break;

            case ImageItem image when image.IsVisible:
                PaintImage(context, image, opacity);
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
        // PDF fills implicitly close open subpaths, so honour Fill.IsVisible
        // regardless of closure (imported content relies on this).
        bool fillVisible = path.Fill.IsVisible;
        bool strokeVisible = path.Stroke.HasVisibleOutline;
        if (!fillVisible && !strokeVisible)
        {
            return;
        }

        StreamGeometry geometry = GetGeometry(path);
        double width = Math.Max(0.01, path.Stroke.Width);

        // Keep hairlines visible. A 0.3pt stroke is 0.08 device pixels at 27% zoom, so
        // it faded to nothing and an imported pattern looked washed out next to a
        // reference render. Viewers hold thin strokes at roughly a pixel.
        const double MinDevicePixels = 0.75;
        width = Math.Max(width, MinDevicePixels / Math.Max(_layout.Zoom, 1e-6));

        Pen StrokePen(double thickness)
        {
            var pen = new Pen(
                ToBrush(path.Stroke.Color, opacity),
                thickness: Math.Max(0.01, thickness),
                lineCap: ToLineCap(path.Stroke.Cap),
                lineJoin: ToLineJoin(path.Stroke.Join),
                miterLimit: path.Stroke.MiterLimit);

            if (!path.Stroke.Dash.IsEmpty)
            {
                // Dash lengths are in the same user-space units as the stroke width,
                // so scale them by the same factor used for the (possibly group-
                // transformed) width.
                double factor = path.Stroke.Width > 0 ? thickness / path.Stroke.Width : 1.0;
                pen.DashStyle = new DashStyle(
                    path.Stroke.Dash.Segments.Select(d => Math.Max(0.01, d * factor)).ToArray(),
                    path.Stroke.Dash.Offset * factor);
            }

            return pen;
        }

        if (fillVisible)
        {
            PaintFill(context, path, geometry, opacity);
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

        Avalonia.Media.Geometry clip = path.Stroke.Alignment == StrokeAlignment.Inside
            ? geometry
            : BuildGeometry(path, outsideClip: true);
        using (context.PushGeometryClip(clip))
        {
            context.DrawGeometry(null, StrokePen(width * 2), geometry);
        }
    }

    /// <summary>
    /// Fills a path, preferring its gradient when it has one.
    ///
    /// The gradient geometry is normalised to the object's bounding box, so it is mapped onto the
    /// world-space bounds of the very geometry being filled. A gradient that cannot be expressed
    /// as a shader - freeform today - falls back to <see cref="FillSpec.Color"/>, which the model
    /// keeps meaningful for exactly this reason, so a gradient fill is never a hole.
    /// </summary>
    private void PaintFill(DrawingContext context, PathItem path, StreamGeometry geometry, double opacity)
    {
        FillSpec fill = path.Fill;
        if (fill.Gradient is { } gradient)
        {
            IBrush? brush = GradientPaint.CreateBrush(gradient, geometry.Bounds, opacity);
            if (brush is not null)
            {
                context.DrawGeometry(brush, null, geometry);
                return;
            }
        }

        context.DrawGeometry(ToBrush(fill.Color, opacity), null, geometry);
    }

    /// <summary>Cached world-space geometry for a path (rebuilt when its revision changes).</summary>
    private StreamGeometry GetGeometry(PathItem path)
    {
        if (_geometryCache.TryGetValue(path, out (int Revision, StreamGeometry Geometry) entry) &&
            entry.Revision == path.GeometryRevision)
        {
            return entry.Geometry;
        }

        StreamGeometry geometry = BuildGeometry(path, outsideClip: false);
        _geometryCache[path] = (path.GeometryRevision, geometry);
        return geometry;
    }

    /// <summary>
    /// Draws an embedded raster image. The samples live in the model in the colour space
    /// the file used, so the conversion to pixels happens here rather than on import.
    ///
    /// The destination is in **model** coordinates, not screen ones: everything painted
    /// under <see cref="Render"/> is already inside the world transform, so converting to
    /// screen space here would apply the zoom twice and draw the image tiny and in the
    /// wrong place. Paths and text are drawn in model space for the same reason.
    /// </summary>
    private void PaintImage(DrawingContext context, ImageItem image, double opacity)
    {
        Rect2D bounds = image.WorldBounds();
        if (bounds.IsEmpty || !bounds.Intersects(_worldViewport))
        {
            return;
        }

        var destination = new Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height);

        using (context.PushOpacity(Math.Clamp(opacity, 0, 1)))
        {
            context.DrawImage(ImageRenderer.BitmapFor(image), destination);
        }
    }

    private void PaintText(DrawingContext context, TextItem text, double opacity)
    {
        Vector2D offset = text.ArtboardOffset();
        IBrush brush = ToBrush(text.Color, opacity);
        TextMetrics metrics = MeasureText(text);

        Avalonia.Matrix? rotation = null;
        if (Math.Abs(text.RotationRadians) > 1e-9)
        {
            Point2D o = text.Origin + offset;
            rotation = Avalonia.Matrix.CreateTranslation(-o.X, -o.Y)
                * Avalonia.Matrix.CreateRotation(text.RotationRadians)
                * Avalonia.Matrix.CreateTranslation(o.X, o.Y);
        }

        using IDisposable? pushed = rotation is { } m ? context.PushTransform(m) : null;

        if (ReferenceEquals(text, _editingText))
        {
            int a = Math.Min(_caret, _editAnchor);
            int b = Math.Max(_caret, _editAnchor);
            var selBrush = new SolidColorBrush(Color.FromArgb(110, 0x4C, 0x9A, 0xFF));
            for (int i = a; i < b && i + 1 < metrics.X.Length; i++)
            {
                // The highlight covers the glyphs: it starts at the caret's x and spans
                // the line's full height, so it sits behind the characters rather than
                // floating above or below them.
                Point2D p0 = text.Origin + offset + new Vector2D(
                    metrics.X[i],
                    metrics.Y[i] - VCCad.Core.Text.TextMeasurement.EstimatedAscent(metrics.Size[i]));
                double w = metrics.Y[i + 1] == metrics.Y[i]
                    ? metrics.X[i + 1] - metrics.X[i]
                    : metrics.Size[i] * 0.3;
                double h = metrics.Size[i] * 1.05;

                if (Math.Abs(text.RotationRadians) > 1e-9)
                {
                    Point2D a0 = RotateAbout(text, p0);
                    Point2D a1 = RotateAbout(text, p0 + new Vector2D(Math.Max(0.5, w), 0));
                    Point2D a2 = RotateAbout(text, p0 + new Vector2D(Math.Max(0.5, w), h));
                    Point2D a3 = RotateAbout(text, p0 + new Vector2D(0, h));
                    var quad = new Avalonia.Media.StreamGeometry();
                    using (var g = quad.Open())
                    {
                        g.BeginFigure(ModelToScreen(a0), true);
                        g.LineTo(ModelToScreen(a1));
                        g.LineTo(ModelToScreen(a2));
                        g.LineTo(ModelToScreen(a3));
                        g.EndFigure(true);
                    }

                    context.DrawGeometry(selBrush, null, quad);
                }
                else
                {
                    context.FillRectangle(selBrush, new Rect(p0.X, p0.Y, Math.Max(0.5, w), h));
                }
            }
        }

        // Measure each run's natural width first: the wrapping constraint must
        // never be narrower than the text, or every line wraps.
        var laidOut = new List<(TextRun Run, FormattedText Formatted, double Natural, double LineHeight, int Lines)>();
        double blockWidth = 0;
        foreach (TextRun run in text.Runs)
        {
            FormattedText formatted = CreateFormattedText(run, brush);
            if (text.FrameWidth > 0)
            {
                // A drawn frame wraps: the text flows to the box width instead of running
                // off the page in one endless line.
                formatted.MaxTextWidth = text.FrameWidth;
            }

            double natural = formatted.Width;
            double target = run.AdvanceWidth is > 0 ? run.AdvanceWidth.Value : natural;
            blockWidth = Math.Max(blockWidth, target);
            laidOut.Add((run, formatted, natural, run.FontSize * text.LineSpacing, run.Text.Count(ch => ch == '\n') + 1));
        }

        // Runs are pieces of a line, not lines. A PDF lays a heading out as separate pieces -
        // "Jalie", "3464", "-", "LILLIE" - and the importer keeps them as runs of one text
        // object. Advancing a line per run stacked them at the same x one line below each
        // other, which printed every heading on the page on top of itself. Placement lives in
        // the model so it can be tested without a canvas.
        var measured = new List<VCCad.Core.Text.RunMetrics>(laidOut.Count);
        foreach ((TextRun run, FormattedText formatted, double natural, double lineHeight, int lines) in laidOut)
        {
            double width = run.AdvanceWidth is > 0 ? run.AdvanceWidth.Value : natural;
            measured.Add(new VCCad.Core.Text.RunMetrics(run.Text, width, lines * lineHeight));
        }

        IReadOnlyList<VCCad.Core.Text.RunPlacement> placed = VCCad.Core.Text.RunLayout.Place(measured);

        // Alignment is a property of a line, so each line's width is needed before any of its
        // runs can be shifted by it.
        var lineWidth = new Dictionary<double, double>();
        foreach (VCCad.Core.Text.RunPlacement p in placed)
        {
            double end = p.X + p.Width;
            lineWidth[p.Y] = Math.Max(lineWidth.TryGetValue(p.Y, out double w) ? w : 0, end);
        }

        for (int i = 0; i < laidOut.Count; i++)
        {
            (TextRun run, FormattedText formatted, double natural, _, _) = laidOut[i];

            double target = measured[i].Width;
            double scaleX = run.AdvanceWidth is > 0 && natural > 0 ? target / natural : 1.0;
            double alignX = text.Alignment switch
            {
                ModelTextAlignment.Center => (blockWidth - lineWidth[placed[i].Y]) / 2,
                ModelTextAlignment.Right => blockWidth - lineWidth[placed[i].Y],
                _ => 0,
            };

            Point2D origin = text.Origin + offset
                + new Vector2D(alignX + placed[i].X, placed[i].Y);

            // The glyph-by-id path needs the run's place in the line as much as the drawn-text
            // path does, so it is resolved before either is taken.
            if (run.EmbeddedFont is { } embedded && run.GlyphIds is { Length: > 0 } glyphIds &&
                TryDrawEmbeddedGlyphs(context, brush, text, run, embedded, glyphIds, origin))
            {
                continue;
            }

            if (Math.Abs(scaleX - 1.0) > 1e-9)
            {
                // Squeeze/stretch to the original PDF advance width so a wider
                // fallback font does not reflow or overprint the layout.
                Avalonia.Matrix scale = Avalonia.Matrix.CreateTranslation(-origin.X, -origin.Y)
                    * Avalonia.Matrix.CreateScale(scaleX, 1.0)
                    * Avalonia.Matrix.CreateTranslation(origin.X, origin.Y);
                using (context.PushTransform(scale))
                {
                    context.DrawText(formatted, new Point(origin.X, origin.Y));
                }
            }
            else
            {
                context.DrawText(formatted, new Point(origin.X, origin.Y));
            }
        }
    }

    /// <summary>Draws a run with its imported embedded programme by glyph id.
    /// Returns false when the font is not registered, so the caller substitutes.</summary>
    private static bool TryDrawEmbeddedGlyphs(DrawingContext context, IBrush brush, TextItem text,
        TextRun run, EmbeddedFont embedded, ushort[] glyphIds, Point2D origin)
    {
        // Resolve through the embedded collection itself, never the global font
        // manager: the latter answers with a fallback face (and reports success)
        // when the family is unknown, which would index these glyph ids into an
        // unrelated font and paint garbage. See EmbeddedFontManager.
        if (!EmbeddedFontManager.TryGetEmbeddedGlyphTypeface(embedded.FamilyName, out IGlyphTypeface glyphTypeface))
        {
            return false;
        }


        // A glyph id outside the programme cannot belong to it, so draw the decoded
        // text instead of trusting a mismatched typeface.
        foreach (ushort glyphId in glyphIds)
        {
            if (glyphId >= glyphTypeface.GlyphCount)
            {
                return false;
            }
        }

        double ascent = embedded.Ascent > 0
            ? embedded.Ascent / 1000.0
            : VCCad.Core.Text.TextMeasurement.TypicalAscentEm;

        // The run's own place in the line, handed in. Drawing every run at the text object's
        // origin put each piece of a heading on top of the others - the pieces are separate
        // runs, so a title of seven pieces printed as seven titles over each other.
        var baseline = new Point(origin.X, origin.Y + (ascent * run.FontSize));
        var glyphRun = new GlyphRun(glyphTypeface, run.FontSize, run.Text.AsMemory(), glyphIds, baseline, 0);

        context.DrawGlyphRun(brush, glyphRun);
        return true;
    }

    private static void RegisterEmbeddedFonts(CadDocument document)
    {
        var fonts = new List<EmbeddedFont>();
        foreach (Artboard artboard in document.Artboards)
        {
            foreach (Layer layer in artboard.Layers)
            {
                CollectEmbeddedFonts(layer.Children, fonts);
            }
        }

        CollectEmbeddedFonts(document.Orphans.Children, fonts);
        if (fonts.Count > 0)
        {
            EmbeddedFontManager.Register(fonts.Distinct());
        }
    }

    private static void CollectEmbeddedFonts(IReadOnlyList<LayerItem> items, List<EmbeddedFont> fonts)
    {
        foreach (LayerItem item in items)
        {
            switch (item)
            {
                case TextItem text:
                    foreach (TextRun run in text.Runs)
                    {
                        if (run.EmbeddedFont is { } font)
                        {
                            fonts.Add(font);
                        }
                    }

                    break;
                case ArtGroup group:
                    CollectEmbeddedFonts(group.Children, fonts);
                    break;
            }
        }
    }

    private static FormattedText CreateFormattedText(TextRun run, IBrush brush)
    {
        var typeface = new Typeface(
            ResolveFontFamily(run),
            run.Italic ? FontStyle.Italic : FontStyle.Normal,
            run.Bold ? FontWeight.Bold : FontWeight.Normal);

        return new FormattedText(run.Text, CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, typeface, run.FontSize, brush);
    }

    /// <summary>
    /// The family to draw a run with.
    ///
    /// A run drawn from an imported programme uses that programme. Everything else is
    /// supplied by the standard-font chain: the URW Core 35 faces from this machine,
    /// else a metric-compatible platform clone (Arial / Times New Roman / Courier New).
    /// A PDF is entitled to name Helvetica and embed nothing at all, and those are the
    /// faces every viewer supplies for it — never an arbitrary bundled font, whose
    /// letterforms and widths would not match the document.</summary>
    private static FontFamily ResolveFontFamily(TextRun run)
        => new(StandardFontResolver.FamilyFor(run));

    /// <summary>Measured layout of a text block: per-character boundary positions
    /// (global indices) with alignment applied, plus the block size.</summary>
    private sealed class TextMetrics
    {
        public double[] X = Array.Empty<double>();
        public double[] Y = Array.Empty<double>();
        public double[] Size = Array.Empty<double>();
        public double MaxWidth;
        public double TotalHeight;
    }

    private static TextMetrics MeasureText(TextItem text)
    {
        // Flatten to characters first: wrapping has to look ahead to the previous break,
        // which a straight run-by-run walk cannot do.
        var chars = new List<(int Index, char Ch, double Size)>();
        int total = 0;
        foreach (TextRun run in text.Runs)
        {
            foreach (char ch in run.Text)
            {
                chars.Add((total, ch, run.FontSize));
                total++;
            }
        }

        double frame = text.FrameWidth;
        var lines = new List<(int Start, int Count, double Width, double Height)>();

        double WidthOf(int from, int count)
        {
            double sum = 0;
            for (int i = from; i < from + count && i < chars.Count; i++)
            {
                sum += CharWidth(text, chars[i].Index);
            }

            return sum;
        }

        void AddLine(int from, int count)
        {
            double maxSize = 0;
            for (int i = from; i < from + count && i < chars.Count; i++)
            {
                maxSize = Math.Max(maxSize, chars[i].Size);
            }

            lines.Add((chars.Count == 0 ? 0 : chars[Math.Min(from, chars.Count - 1)].Index,
                count, WidthOf(from, count), Math.Max(maxSize, text.MaxFontSize) * text.LineSpacing));
        }

        int lineStartChar = 0;
        int lastBreak = -1;
        for (int i = 0; i < chars.Count; i++)
        {
            char ch = chars[i].Ch;

            if (ch == '\n')
            {
                AddLine(lineStartChar, i - lineStartChar);
                lines.Add((-1, -1, 0, text.ParagraphSpacing));
                lineStartChar = i + 1;
                lastBreak = -1;
                continue;
            }

            if (ch == ' ')
            {
                lastBreak = i;
            }

            if (frame <= 0 || i <= lineStartChar)
            {
                continue;
            }

            // Break at the last space; if a single word is wider than the frame, break it
            // rather than let it run out of the box. A space at the end of a line is
            // collapsed when the line is drawn, so it must not count towards the wrap
            // width — counting it wraps a line early and the box ends up a line too tall.
            int measured = i;
            while (measured > lineStartChar && chars[measured].Ch == ' ')
            {
                measured--;
            }

            if (WidthOf(lineStartChar, measured - lineStartChar + 1) > frame)
            {
                int breakAt = lastBreak > lineStartChar ? lastBreak : i;
                AddLine(lineStartChar, breakAt - lineStartChar);
                lineStartChar = breakAt == lastBreak ? breakAt + 1 : breakAt;
                lastBreak = -1;
            }
        }

        if (lineStartChar <= chars.Count)
        {
            AddLine(lineStartChar, chars.Count - lineStartChar);
        }

        double blockWidth = frame > 0 ? frame : lines.Count == 0 ? 0 : lines.Max(l => l.Width);

        var m = new TextMetrics
        {
            X = new double[total + 1],
            Y = new double[total + 1],
            Size = new double[total + 1],
            MaxWidth = blockWidth,
        };

        double y = 0;
        foreach ((int lineStartIndex, int count, double width, double height) in lines)
        {
            double offset = text.Alignment switch
            {
                ModelTextAlignment.Center => (blockWidth - width) / 2,
                ModelTextAlignment.Right => blockWidth - width,
                _ => 0,
            };

            double x = offset;
            for (int k = 0; k <= count; k++)
            {
                int index = lineStartIndex + k;
                if (index <= total)
                {
                    m.X[index] = x;
                    m.Y[index] = y;
                    m.Size[index] = height / 1.2;
                }

                if (k < count)
                {
                    x += CharWidth(text, index);
                }
            }

            y += height;
        }

        m.TotalHeight = y;
        return m;
    }

    /// <summary>
    /// Per-character advance widths for a run, from the one measurement source the whole
    /// program shares (<see cref="TextMeasurement.Current"/>).
    ///
    /// The canvas used to measure here itself, which meant the caret and the edit box were
    /// laid out by a different code path than the model's <c>BoundingBox</c> — and the two
    /// could disagree about where anything was. They now cannot: both ask the shaper.
    /// </summary>
    private static IReadOnlyList<double> Advances(TextRun run) => TextMeasurement.Advances(run);

    private static double CharWidth(TextItem text, int globalIndex)
    {
        int remaining = globalIndex;
        foreach (TextRun run in text.Runs)
        {
            if (remaining < run.Text.Length)
            {
                return TextMeasurement.AdvanceOf(run, remaining);
            }

            remaining -= run.Text.Length;
        }

        // Only reached when the index is past every run, which callers guard against.
        return TextMeasurement.AdvanceAtEnd(text.Runs.Count > 0 ? text.Runs[^1] : new TextRun());
    }

    /// <summary>Builds world-space geometry for a path. When <paramref name="outsideClip"/>
    /// is true a huge surrounding rectangle is prepended with the even-odd rule to
    /// give the complement region (for clipping an "outside" stroke).</summary>
    private StreamGeometry BuildGeometry(PathItem path, bool outsideClip)
    {
        var geometry = new StreamGeometry();
        MediaFillRule rule = outsideClip || path.Fill.Rule == ModelFillRule.EvenOdd
            ? MediaFillRule.EvenOdd
            : MediaFillRule.NonZero;

        Vector2D offset = path.ArtboardOffset();
        Point Map(Point2D local) => new(local.X + offset.X, local.Y + offset.Y);

        using (StreamGeometryContext g = geometry.Open())
        {
            g.SetFillRule(rule);

            if (outsideClip)
            {
                Rect2D huge = _layout.Extent.Inflated(10000);
                g.BeginFigure(new Point(huge.Left, huge.Top), true);
                g.LineTo(new Point(huge.Right, huge.Top));
                g.LineTo(new Point(huge.Right, huge.Bottom));
                g.LineTo(new Point(huge.Left, huge.Bottom));
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

    /// <summary>
    /// A path's outline in SCREEN coordinates, for drawing the selection trace.
    ///
    /// BuildGeometry returns world coordinates and is drawn inside the world transform.
    /// The trace is not: it is painted alongside the chrome, which converts through
    /// ModelToScreen because it is already outside that transform. Drawing world geometry
    /// there left the trace offset by exactly the page origin - a selected object was traced
    /// wherever its page sits from the canvas origin, which is what the person saw and
    /// reported as "stroked at an offset that does not make sense".
    /// </summary>
    private StreamGeometry BuildScreenGeometry(PathItem path)
    {
        var geometry = new StreamGeometry();
        MediaFillRule rule = path.Fill.Rule == ModelFillRule.EvenOdd
            ? MediaFillRule.EvenOdd
            : MediaFillRule.NonZero;

        Vector2D offset = path.ArtboardOffset();

        Point Map(Point2D local)
            => ModelToScreen(new Point2D(local.X + offset.X, local.Y + offset.Y));

        using (StreamGeometryContext g = geometry.Open())
        {
            g.SetFillRule(rule);

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

    private void PaintOverlays(DrawingContext context)
    {
        if (_vm is null)
        {
            return;
        }

        EditorTool tool = _vm.Tool;

        // Text blocks are selectable objects, so the dashed box and its handles
        // must appear for a text-only selection too — without this, clicking text
        // selected it in the model but drew no feedback at all.
        bool hasObjectSelection = _vm.SelectedPaths().Any() || _vm.SelectedTextItems().Any();

        if (tool == EditorTool.Select && hasObjectSelection)
        {
            Rect2D selection = ChromeRect();
            if (!selection.IsEmpty)
            {
                // The outlines first, so the box and its handles sit on top of them.
                PaintSelectionOutlines(context);
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
            PaintTextEditBox(context, _editingText);
            if (_caretOn)
            {
                PaintTextCaret(context);
            }
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
    /// <summary>
    /// Traces each selected object in the selection colour, along its own shape.
    ///
    /// Stroked, never filled. A selected object that was filled in the selection colour would
    /// hide the very thing the person is looking at to check it is the right one - and with
    /// several objects selected the fill would merge them into one blob. Stroking says "this
    /// one, exactly this outline" without covering anything up, which is why every drawing
    /// program does it this way.
    ///
    /// Text is traced round its block rather than round each glyph: the block is what is
    /// selected and what moves, and stroking every glyph of a paragraph would be a mess.
    /// </summary>
    private void PaintSelectionOutlines(DrawingContext context)
    {
        IBrush accent = new SolidColorBrush(Color.FromRgb(0x4C, 0x9A, 0xFF));

        // Screen-thick. The trace is drawn in screen space now, so the width is already in
        // pixels and must not be divided by the zoom as well.
        var pen = new Pen(accent, 1.4);

        foreach (LayerItem item in _vm!.SelectedObjects)
        {
            switch (item)
            {
                case PathItem path:
                    context.DrawGeometry(null, pen, BuildScreenGeometry(path));
                    break;

                case TextItem text:
                    PaintOutlineOf(context, pen, text.BoundingBox(),
                        text.OwningLayer()?.Artboard);
                    break;

                case ImageItem image:
                    PaintOutlineOf(context, pen, image.Placement, null);
                    break;

                case ArtGroup group:
                    PaintOutlineOf(context, pen, group.BoundingBox(),
                        group.OwningLayer()?.Artboard);
                    break;
            }
        }
    }

    /// <summary>Strokes a box, offset into document coordinates. No fill, deliberately.</summary>
    private void PaintOutlineOf(DrawingContext context, Pen pen, Rect2D box, Artboard? artboard)
    {
        if (box.IsEmpty)
        {
            return;
        }

        var offset = new Vector2D(artboard?.X ?? 0, artboard?.Y ?? 0);
        Point tl = ModelToScreen(new Point2D(box.Left + offset.X, box.Top + offset.Y));
        Point br = ModelToScreen(new Point2D(box.Right + offset.X, box.Bottom + offset.Y));

        context.DrawRectangle(null, pen, new Rect(
            Math.Min(tl.X, br.X), Math.Min(tl.Y, br.Y),
            Math.Abs(br.X - tl.X), Math.Abs(br.Y - tl.Y)));
    }

    /// <summary>Paints the select tool's dashed box, its eight handles and the
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
        double half = 4.0;

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
        double r = 4.5;
        context.DrawLine(handlePen, ModelToScreen(top), rot);
        context.DrawEllipse(Brushes.White, new Pen(accent, 1.4), rot, r, r);
    }

    /// <summary>Node tool chrome: anchors and handles only — no bounding rectangle.</summary>
    private void PaintNodeChrome(DrawingContext context, PathItem path)
    {
        IBrush accent = new SolidColorBrush(Color.FromRgb(0x4C, 0x9A, 0xFF));
        var pen = new Pen(accent, 1.4);
        double half = 4.0;

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
        double big = 3.6;
        var ring = new Pen(new SolidColorBrush(Color.FromRgb(0x2B, 0x6C, 0xB0)), 1.8);
        context.DrawEllipse(Brushes.White, ring, handle, big, big);
    }

    private void PaintMarquee(DrawingContext context)
    {
        var accent = new SolidColorBrush(Color.FromRgb(0x4C, 0x9A, 0xFF));
        var pen = new Pen(accent, 1.0) { DashStyle = new DashStyle(new[] { 4.0, 3.0 }, 0) };
        var fill = new SolidColorBrush(Color.FromArgb(26, 0x4C, 0x9A, 0xFF));

        // A lasso shows the path the pointer has actually taken, closed with a straight
        // segment back to where it began - the same shape the release will select by, so what
        // is drawn and what is chosen cannot disagree.
        if (_lassoPath.Count >= 2)
        {
            var geometry = new StreamGeometry();

            using (StreamGeometryContext g = geometry.Open())
            {
                g.BeginFigure(ModelToScreen(_lassoPath[0]), true);

                for (int i = 1; i < _lassoPath.Count; i++)
                {
                    g.LineTo(ModelToScreen(_lassoPath[i]));
                }

                g.LineTo(ModelToScreen(_lassoPath[0]));
                g.EndFigure(true);
            }

            context.DrawGeometry(fill, pen, geometry);
            return;
        }

        Rect2D rect = Rect2D.FromPoints(_marqueeStart, _marqueeCurrent);
        Point tl = ModelToScreen(new Point2D(rect.Left, rect.Top));
        var screen = new Rect(tl.X, tl.Y, rect.Width * _layout.Zoom, rect.Height * _layout.Zoom);

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
        double half = 4.0;

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
    // On-canvas rich-text editing
    // ------------------------------------------------------------------

    private void EnterTextEdit(TextItem text)
    {
        _editingText = text;
        _editBefore = (TextItem)text.Clone();
        _caret = TextEditing.Length(text);
        _editAnchor = _caret;
        _vm!.SelectObject(text);
        _vm.IsEditingText = true;

        // Publish the target so operations that style text can reach the block even after
        // the selection moves on (picking a font, clicking away, switching document).
        _vm.EditingText = text;
        _caretOn = true;
        UpdateCaretBlink();
        UpdateCaretInfo();
        Focus();
        InvalidateVisual();
    }

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
        _textSelecting = false;
        if (_vm is not null)
        {
            _vm.IsEditingText = false;
            _vm.EditingText = null;
            UpdateCaretBlink();
        }

        InvalidateVisual();
    }

    private void UpdateCaretInfo()
    {
        if (_vm is null || _editingText is null)
        {
            return;
        }

        (int run, _) = TextEditing.Locate(_editingText, _caret);
        _vm.TextCaretRunIndex = run;
        _vm.TextSelectionStart = Math.Min(_caret, _editAnchor);
        _vm.TextSelectionEnd = Math.Max(_caret, _editAnchor);
    }

    /// <summary>
    /// Opens a text block for editing, as double-clicking into it does.
    ///
    /// Public because a person can do it and the assistant must be able to as well: without
    /// it a driver cannot reach rich-text styling at all, since styling part of a selection
    /// needs there to be a selection, and a selection needs the block open.
    /// </summary>
    public bool BeginTextEdit(TextItem text)
    {
        if (_vm is null)
        {
            return false;
        }

        EnterTextEdit(text);
        return true;
    }

    /// <summary>
    /// Places the caret and selection, as dragging across the text does.
    ///
    /// The range is in flattened characters, the same coordinates
    /// <see cref="EditorViewModel.TextSelectionStart"/> reports, so a caller can select
    /// exactly what it measured.
    /// </summary>
    public bool SetTextSelection(int start, int end)
    {
        if (_editingText is null)
        {
            return false;
        }

        int length = TextEditing.Length(_editingText);
        _editAnchor = Math.Clamp(start, 0, length);
        _caret = Math.Clamp(end, 0, length);
        _caretOn = true;

        UpdateCaretInfo();
        UpdateCaretBlink();
        InvalidateVisual();
        return true;
    }

    private static bool TextEquals(TextItem a, TextItem b)
    {
        if (a.Runs.Count != b.Runs.Count || a.Origin != b.Origin || a.Color != b.Color ||
            a.Alignment != b.Alignment || Math.Abs(a.RotationRadians - b.RotationRadians) > 1e-9)
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

    private void AfterTextEdit()
    {
        if (_editingText is null)
        {
            return;
        }

        _caret = Math.Clamp(_caret, 0, TextEditing.Length(_editingText));
        _editAnchor = Math.Clamp(_editAnchor, 0, TextEditing.Length(_editingText));
        UpdateCaretInfo();
        _vm?.RaiseTransformChanged();
        InvalidateVisual();
    }

    private (int Start, int End) Selection()
        => (Math.Min(_caret, _editAnchor), Math.Max(_caret, _editAnchor));

    private void InsertText(string value)
    {
        if (_editingText is null)
        {
            return;
        }

        (int start, int end) = Selection();
        if (start != end)
        {
            TextEditing.DeleteRange(_editingText, start, end);
            _caret = start;
        }

        TextEditing.Insert(_editingText, _caret, value);
        _caret += value.Length;
        _editAnchor = _caret;
        AfterTextEdit();
    }

    private void BackspaceText()
    {
        if (_editingText is null)
        {
            return;
        }

        (int start, int end) = Selection();
        if (start != end)
        {
            TextEditing.DeleteRange(_editingText, start, end);
            _caret = start;
        }
        else if (_caret > 0)
        {
            TextEditing.DeleteRange(_editingText, _caret - 1, _caret);
            _caret--;
        }

        _editAnchor = _caret;
        AfterTextEdit();
    }

    private void DeleteText()
    {
        if (_editingText is null)
        {
            return;
        }

        (int start, int end) = Selection();
        if (start != end)
        {
            TextEditing.DeleteRange(_editingText, start, end);
            _caret = start;
        }
        else if (_caret < TextEditing.Length(_editingText))
        {
            TextEditing.DeleteRange(_editingText, _caret, _caret + 1);
        }

        _editAnchor = _caret;
        AfterTextEdit();
    }

    private void MoveCaret(int index, bool extend)
    {
        _caret = Math.Clamp(index, 0, _editingText is null ? 0 : TextEditing.Length(_editingText));
        if (!extend)
        {
            _editAnchor = _caret;
        }

        AfterTextEdit();
    }

    private void ApplyStyleToSelection(Action<TextRun> style)
    {
        if (_editingText is null)
        {
            return;
        }

        (int start, int end) = Selection();
        TextEditing.ApplyStyle(_editingText, start, end, style);
        AfterTextEdit();
    }

    private void HandleTextEditKey(KeyEventArgs e)
    {
        if (_editingText is null)
        {
            e.Handled = true;
            return;
        }

        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        bool handled = true;
        int length = TextEditing.Length(_editingText);

        if (ctrl && e.Key == Key.A)
        {
            _editAnchor = 0;
            MoveCaret(length, extend: true);
        }
        else if (ctrl && e.Key == Key.B)
        {
            ToggleStyle(bold: true);
        }
        else if (ctrl && e.Key == Key.I)
        {
            ToggleStyle(bold: false);
        }
        else if (ctrl && e.Key == Key.C)
        {
            (int s, int en) = Selection();
            _textClipboard = TextEditing.GetRange(_editingText, s, en);
        }
        else if (ctrl && e.Key == Key.X)
        {
            (int s, int en) = Selection();
            _textClipboard = TextEditing.GetRange(_editingText, s, en);
            if (s != en)
            {
                TextEditing.DeleteRange(_editingText, s, en);
                _caret = s;
                _editAnchor = s;
                AfterTextEdit();
            }
        }
        else if (ctrl && e.Key == Key.V)
        {
            if (!string.IsNullOrEmpty(_textClipboard))
            {
                InsertText(_textClipboard!);
            }
        }
        else
        {
            switch (e.Key)
            {
                case Key.Escape:
                    ExitTextEdit();
                    break;
                case Key.Left:
                    MoveCaret(_caret - 1, shift);
                    break;
                case Key.Right:
                    MoveCaret(_caret + 1, shift);
                    break;
                case Key.Home:
                    MoveCaret(LineStart(_caret), shift);
                    break;
                case Key.End:
                    MoveCaret(LineEnd(_caret), shift);
                    break;
                case Key.Up:
                    MoveCaret(VerticalMove(_caret, -1), shift);
                    break;
                case Key.Down:
                    MoveCaret(VerticalMove(_caret, 1), shift);
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
                    handled = false; // let OnTextInput produce the character
                    break;
            }
        }

        e.Handled = handled;
    }

    private void ToggleStyle(bool bold)
    {
        if (_editingText is null)
        {
            return;
        }

        (int start, int end) = Selection();
        (int run, _) = TextEditing.Locate(_editingText, start);
        bool makeBold = bold
            ? !_editingText.Runs[run].Bold
            : false;
        ApplyStyleToSelection(r =>
        {
            if (bold)
            {
                r.Bold = makeBold;
            }
            else
            {
                r.Italic = !r.Italic;
            }
        });
    }

    private int LineStart(int index)
    {
        if (_editingText is null)
        {
            return 0;
        }

        string flat = TextEditing.GetText(_editingText);
        int i = Math.Clamp(index, 0, flat.Length);
        while (i > 0 && flat[i - 1] != '\n')
        {
            i--;
        }

        return i;
    }

    private int LineEnd(int index)
    {
        if (_editingText is null)
        {
            return 0;
        }

        string flat = TextEditing.GetText(_editingText);
        int i = Math.Clamp(index, 0, flat.Length);
        while (i < flat.Length && flat[i] != '\n')
        {
            i++;
        }

        return i;
    }

    private int VerticalMove(int index, int direction)
    {
        if (_editingText is null)
        {
            return index;
        }

        TextMetrics metrics = MeasureText(_editingText);
        int line = LineStart(index);
        int column = index - line;
        if (direction < 0)
        {
            if (line == 0)
            {
                return index;
            }

            int prevEnd = line - 1;          // the newline char
            int prevStart = LineStart(prevEnd);
            return Math.Min(prevStart + column, prevEnd);
        }

        int end = LineEnd(index);
        if (end >= TextEditing.Length(_editingText))
        {
            return index;
        }

        int nextStart = end + 1;
        int nextEnd = LineEnd(nextStart);
        return Math.Min(nextStart + column, nextEnd);
    }

    private static (int Start, int End) WordBounds(TextItem text, int index)
    {
        string flat = TextEditing.GetText(text);
        index = Math.Clamp(index, 0, flat.Length);
        int start = index;
        int end = index;
        while (start > 0 && char.IsLetterOrDigit(flat[start - 1]))
        {
            start--;
        }

        while (end < flat.Length && char.IsLetterOrDigit(flat[end]))
        {
            end++;
        }

        return (start, end);
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

    /// <summary>
    /// The box around the block being edited: its frame width — the width the text wraps
    /// in, which does not follow the text — and the height its lines actually need, so it
    /// grows downwards as lines are added and never widens with the text.
    /// </summary>
    private void PaintTextEditBox(DrawingContext context, TextItem text)
    {
        TextMetrics metrics = MeasureText(text);

        double width = text.FrameWidth > 0 ? text.FrameWidth : Math.Max(metrics.MaxWidth, 4);
        double height = Math.Max(metrics.TotalHeight, text.MaxFontSize * text.LineSpacing);

        Point2D topLeft = TextLocalToWorld(text, new Point2D(0, 0));
        Point2D topRight = TextLocalToWorld(text, new Point2D(width, 0));
        Point2D bottomRight = TextLocalToWorld(text, new Point2D(width, height));
        Point2D bottomLeft = TextLocalToWorld(text, new Point2D(0, height));

        Point a = ModelToScreen(topLeft);
        Point b = ModelToScreen(topRight);
        Point c = ModelToScreen(bottomRight);
        Point d = ModelToScreen(bottomLeft);

        var pen = new Pen(new SolidColorBrush(Color.FromArgb(190, 0x4C, 0x9A, 0xFF)), 1);
        context.DrawLine(pen, a, b);
        context.DrawLine(pen, b, c);
        context.DrawLine(pen, c, d);
        context.DrawLine(pen, d, a);

        // Grab handles, so the box can be resized while the text is being set.
        var handleFill = new SolidColorBrush(Color.FromRgb(0x4C, 0x9A, 0xFF));
        foreach (Point2D handle in EditBoxHandles(text, metrics))
        {
            Point screen = ModelToScreen(TextLocalToWorld(text, handle));
            context.FillRectangle(handleFill, new Rect(screen.X - 3, screen.Y - 3, 6, 6));
        }
    }

    /// <summary>
    /// The grab handles on the edit box, in the block's own coordinates.
    ///
    /// Only the width is settable: the height is whatever the lines need, so a vertical
    /// handle would be a lie. The handles resize the frame the text wraps in — the box
    /// grows downwards on its own as lines are added and never widens with the text.
    /// </summary>
    private static Point2D[] EditBoxHandles(TextItem text, TextMetrics metrics)
    {
        double w = text.FrameWidth > 0 ? text.FrameWidth : Math.Max(metrics.MaxWidth, 4);
        double h = Math.Max(metrics.TotalHeight, text.MaxFontSize * text.LineSpacing);
        double mid = h / 2;

        // Right edge (three), then the left edge's middle.
        return new[]
        {
            new Point2D(w, 0), new Point2D(w, mid), new Point2D(w, h), new Point2D(0, mid),
        };
    }

    /// <summary>Which edit-box handle, if any, is under a point; -1 when none is.</summary>
    private int HandleAt(TextItem text, Point2D world)
    {
        TextMetrics metrics = MeasureText(text);
        Point2D local = ToTextLocal(text, world);
        Point2D[] handles = EditBoxHandles(text, metrics);
        double tolerance = Math.Max(PickTolerance, 4);

        for (int i = 0; i < handles.Length; i++)
        {
            if (local.DistanceTo(handles[i]) <= tolerance)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Applies a handle drag: only the frame width changes.</summary>
    private void DragEditBoxHandle(Point2D world)
    {
        if (_editingText is not { } text)
        {
            return;
        }

        Point2D local = ToTextLocal(text, world);
        double start = _frameResizeStartWidth;

        // Handle 3 is the left edge, so dragging it left widens the block.
        double delta = _frameResizeHandle == 3
            ? _frameResizeStartLocalX - local.X
            : local.X - _frameResizeStartLocalX;

        text.FrameWidth = Math.Max(24, start + delta);
        AfterTextEdit();
        InvalidateVisual();
    }

    private void PaintTextCaret(DrawingContext context)
    {
        if (_editingText is null)
        {
            return;
        }

        Vector2D offset = _editingText.ArtboardOffset();
        TextMetrics metrics = MeasureText(_editingText);
        int index = Math.Clamp(_caret, 0, metrics.X.Length - 1);

        Point2D local = new(metrics.X[index], metrics.Y[index]);
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

        // The caret spans the line's full height (ymax to ymin), not a fixed multiple of
        // the font size, so it brackets the glyphs rather than floating inside them.
        double size = metrics.Size[index];
        double ascent = VCCad.Core.Text.TextMeasurement.EstimatedAscent(size);
        double descent = VCCad.Core.Text.TextMeasurement.EstimatedDescent(size);
        double h = Math.Max((ascent + descent) * _layout.Zoom, 4);

        // Dark, because the page is white: a white caret on white paper is no caret.
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x10)), 1.5);
        Point top = new(screen.X, screen.Y - ascent * _layout.Zoom);
        context.DrawLine(pen, top, new Point(top.X, top.Y + h));
    }

    /// <summary>Nearest character index for a local text point (click/drag).</summary>
    /// <summary>
    /// A world point in a text block's own coordinates — the space its metrics are laid
    /// out in. A rotated block has to be turned back before it can be hit-tested, or a
    /// click lands somewhere else entirely and the caret jumps.
    /// </summary>
    /// <summary>A world point turned about a text block's origin by its rotation.</summary>
    private static Point2D RotateAbout(TextItem text, Point2D point)
    {
        if (Math.Abs(text.RotationRadians) < 1e-9)
        {
            return point;
        }

        double cos = Math.Cos(text.RotationRadians);
        double sin = Math.Sin(text.RotationRadians);
        double dx = point.X - text.Origin.X;
        double dy = point.Y - text.Origin.Y;
        return new Point2D(text.Origin.X + dx * cos - dy * sin,
            text.Origin.Y + dx * sin + dy * cos);
    }

    private static Point2D ToTextLocal(TextItem text, Point2D world)
    {
        Point2D point = world - text.ArtboardOffset();
        double dx = point.X - text.Origin.X;
        double dy = point.Y - text.Origin.Y;

        if (Math.Abs(text.RotationRadians) > 1e-9)
        {
            double cos = Math.Cos(-text.RotationRadians);
            double sin = Math.Sin(-text.RotationRadians);
            return new Point2D(dx * cos - dy * sin, dx * sin + dy * cos);
        }

        return new Point2D(dx, dy);
    }

    /// <summary>Whether a world point falls inside a text block, rotation included.</summary>
    private static bool TextContains(TextItem text, Point2D world)
    {
        Point2D local = ToTextLocal(text, world);
        Rect2D box = text.BoundingBox();
        return local.X >= -1 && local.X <= box.Width + 1 &&
               local.Y >= -1 && local.Y <= box.Height + 1;
    }

    /// <summary>A world point to a screen point, through a text block's own space.</summary>
    private Point2D TextLocalToWorld(TextItem text, Point2D local)
    {
        double x = text.Origin.X + local.X;
        double y = text.Origin.Y + local.Y;

        if (Math.Abs(text.RotationRadians) > 1e-9)
        {
            double cos = Math.Cos(text.RotationRadians);
            double sin = Math.Sin(text.RotationRadians);
            double dx = x - text.Origin.X;
            double dy = y - text.Origin.Y;
            x = text.Origin.X + dx * cos - dy * sin;
            y = text.Origin.Y + dx * sin + dy * cos;
        }

        return new Point2D(x, y) + text.ArtboardOffset();
    }

    private int IndexAtLocal(TextItem text, Point2D local)
    {
        TextMetrics metrics = MeasureText(text);
        int best = 0;
        double bestDistance = double.PositiveInfinity;
        for (int i = 0; i < metrics.X.Length; i++)
        {
            double dx = metrics.X[i] - local.X;
            double dy = metrics.Y[i] - local.Y;
            double d = dx * dx + dy * dy * 4; // weight vertical distance more
            if (d < bestDistance)
            {
                bestDistance = d;
                best = i;
            }
        }

        return best;
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

        double half = 4.0;
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
            double r = 4.0;
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

    private IBrush ToBrush(ColorRgb color, double opacity)
    {
        byte alpha = (byte)Math.Round(MathUtils.Clamp(opacity * color.A, 0.0, 1.0) * 255.0);
        byte r = (byte)Math.Round(MathUtils.Clamp(color.R, 0.0, 1.0) * 255.0);
        byte g = (byte)Math.Round(MathUtils.Clamp(color.G, 0.0, 1.0) * 255.0);
        byte b = (byte)Math.Round(MathUtils.Clamp(color.B, 0.0, 1.0) * 255.0);
        uint key = ((uint)alpha << 24) | ((uint)r << 16) | ((uint)g << 8) | b;
        if (_brushCache.TryGetValue(key, out IBrush? cached))
        {
            return cached;
        }

        var brush = new SolidColorBrush(new Color(alpha, r, g, b));
        _brushCache[key] = brush;
        return brush;
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