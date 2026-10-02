using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using VCCad.App.Fonts;
using VCCad.App.Automation;
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

    /// <summary>The canvas transform in force during a paint pass, used by a filtered object to render itself
    /// offscreen in the same space. Set at the start of every pass, so it always describes the pass in progress.</summary>
    private Avalonia.Matrix? _paintWorld;
    private PasteboardLayout _layout = new(Rect2D.Empty);

    /// <summary>
    /// How deep the art a stroke's brush maps is currently nested.
    ///
    /// An art brush names a document item, and that item may be a path whose own stroke carries an art brush -
    /// so drawing the art can reach back into this painter. A depth cap ends a cycle (or a chain long enough to
    /// be one) as art that stops nesting, rather than as a stack overflow while a person is drawing.
    /// </summary>
    private int _artDepth;

    private const int MaxArtDepth = 8;

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

    /// <summary>
    /// The object a press landed on when it was already part of the selection. If the
    /// gesture turns out to be a click rather than a drag, the selection reduces to it.
    /// </summary>
    private LayerItem? _plainClickCandidate;

    // Gradient annotator drag (Select tool): the path whose gradient is being placed, the box its
    // normalised geometry is measured against, the handle grabbed, and the fill as it was before
    // the drag so the whole gesture is one undo step.
    private PathItem? _gradientPath;
    private Rect _gradientBox;
    private GradientHandle _gradientHandle;
    private FillSpec _gradientBefore;
    private GradientSpec _gradientSpec0 = GradientSpec.Default;
    private bool _gradientMoved;

    // Width-profile mode: the path whose profile is open for editing on the canvas, the grip a press
    // grabbed, the stroke it belongs to, the profile as it was at the press, and the profile the drag
    // is previewing. The preview is composed for painting only - the model is written once, on release,
    // by the operation - so an abandoned or cancelled gesture has nothing to put back.
    private WidthProfileGrip? _profileGrip;
    private int _profileDragStroke = -1;
    private WidthProfileSpec? _profileDragStart;
    private WidthProfileSpec? _profileDragProfile;

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

    // The shape control point being dragged.
    private PathItem? _shapeHandlePath;
    private PathItem? _shapeHandleBefore;
    private ShapeDefinition? _shapeHandleDefinition;
    private ShapeHandle _shapeHandle;

    // The pencil's stroke, in document space, exactly as captured.
    private readonly List<Point2D> _pencilPoints = new();

    // What the pen reported while that stroke was being drawn, one sample per kept point: the positions above, plus
    // the pressure and tilt only a pen sends and the time that makes speed derivable. The clock starts at the press,
    // so speed is a fact about this stroke rather than about when the application happened to boot.
    private readonly List<InputSample> _pencilSamples = new();
    private readonly System.Diagnostics.Stopwatch _pencilClock = new();

    // The corner tool's drag state.
    private PathItem? _roundPath;
    private PathItem? _roundBefore;
    private Point2D _roundCorner;
    private int _roundSubPath;
    private int _roundNode;
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

    /// <summary>Whether a shape drag is armed. Exposed for tests: it is the one piece of state that
    /// decides whether a release creates anything, and it cannot be seen from outside otherwise.</summary>
    internal bool ShapeDragArmed => _shapeStart is not null;

    /// <summary>Where the shape drag has been pulled to, and whether the button is still down.</summary>
    internal Point2D ShapeDragCurrent => _shapeCurrent;

    internal bool LeftDownForTests => _leftDown;
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
        ZoomAtCenter(Zoom * ButtonZoomStep);
    }

    public void ZoomOut()
    {
        _userAdjustedZoom = true;
        ZoomAtCenter(Zoom / ButtonZoomStep);
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
    /// Points the view at a page for an off-screen render that is exactly <paramref name="pixelSize"/>.
    ///
    /// Unlike <see cref="CenterOn"/>, this does not ask the control how big it is. A render is sized by
    /// the page, so taking the size from <c>Bounds</c> means a layout pass that has not run yet - or
    /// one a parent has re-run after we arranged - silently changes where the page lands: the page is
    /// centred in whatever the control happens to be, and the difference shows up as the artwork
    /// displaced and a strip of canvas background down one edge. That is what made
    /// <c>document.renderPage</c> draw a 3350-point sheet 168 px high with 168 px of background below
    /// it - half the difference between the page and the bounds the control reported at that moment.
    /// </summary>
    public void PointAtForExport(Point2D pageCentre, Size2D pixelSize)
    {
        _offset = new Vector2D(
            (pixelSize.Width / 2) - ((pageCentre.X - _layout.Extent.Left) * _layout.Zoom),
            (pixelSize.Height / 2) - ((pageCentre.Y - _layout.Extent.Top) * _layout.Zoom));
        InvalidateVisual();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

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

    /// <summary>Nearest anchor across the document within snap tolerance, in **world**
    /// coordinates (or null). <paramref name="exclude"/> skips the path being edited.
    ///
    /// World, not the artboard frame: an anchor inside `translate(50,50) scale(2)` is drawn at
    /// `ToWorld ∘ anchor`, so a snap answer measured without that composition is a point the artwork is
    /// nowhere near - and it is compared against a pointer that is in world coordinates (#165).
    /// <see cref="SelectionEngine.ToWorld"/> is the one place the composition is stated.</summary>
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

            AffineTransform toWorld = SelectionEngine.ToWorld(path);
            foreach (SubPath sub in path.SubPaths)
            {
                foreach (PathNode node in sub.Nodes)
                {
                    Point2D candidate = toWorld.Transform(node.Anchor);
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

    /// <summary>
    /// Converts a world point into the frame a path's own geometry is stored in - every enclosing group's
    /// transform composed in, not merely the artboard origin - or null when that frame collapses the plane.
    ///
    /// The node tool picks nodes by comparing stored coordinates, so the pointer has to arrive in the same
    /// frame the nodes are written in. Subtracting the artboard origin alone is that frame only when no
    /// group above the path has a transform: inside `translate(50,50) scale(2)` the nodes are drawn at
    /// twice their stored coordinates, so the tool looked for them where they are not and a drag could not
    /// be aimed at all (#173). <see cref="SelectionEngine.FromWorld"/> is the one place that composition is
    /// inverted.
    /// </summary>
    private static Point2D? InPathFrame(PathItem path, Point2D world)
        => SelectionEngine.FromWorld(path)?.Transform(world);

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
            // **What is under the pointer, before what is near it.** A click inside a filled shape means
            // that shape, and the deepest object under the pointer is the one the person meant.
            //
            // Distance-to-outline alone was the whole reason "I can't select anything on the page except
            // the red text" was true: a path was measured to its outline, so the middle of a filled pattern
            // piece - the most natural place to click - was empty space, while text, which is measured by
            // its box, answered. Nearest is still the fallback, because a thin line is a thing a person
            // aims at and nothing contains it.
            topmost = SelectionEngine.Within(board, model)
                      ?? SelectionEngine.Nearest(board, model, tolerance)
                      ?? topmost;
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
        // `model` is in world coordinates and the group's children are in the group's own space: the
        // artboard origin comes off, then every enclosing transform, then the group's own - the frame
        // the canvas draws them in, taken from the one place that composition is stated.
        AffineTransform above = SelectionEngine.ToArtboard(group);

        if (!group.Transform.IsInvertible || !above.IsInvertible)
        {
            return null;
        }

        Vector2D offset = group.ArtboardOffset();
        Point2D inArtboard = model - offset;
        Point2D local = group.Transform.Inverted().Transform(above.Inverted().Transform(inArtboard));

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
        // The children of a group live in the space its transform establishes.
        if (!group.Transform.IsInvertible)
        {
            return null;
        }

        Point2D local = group.Transform.Inverted().Transform(model);
        LayerItem? topmost = null;
        foreach (LayerItem child in group.Children)
        {
            LayerItem? hit = HitTestItem(child, local, tolerance);
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

        // The width-profile mode owns the canvas while it is on. A press on a grip starts the drag; a
        // press anywhere else is swallowed rather than clearing the selection, because the issue this
        // mode exists for says the profile being edited must survive a click that misses it.
        if (IsEditingWidthProfile)
        {
            e.Handled = true;
            if (TryBeginProfileGrip(model))
            {
                _leftDown = true;
                e.Pointer.Capture(this);
            }

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

        // A double-click means "let me work on this one": text opens with the caret where the click
        // landed, a path hands its geometry to the node tool. The rule lives in EditAt so a driver
        // can do the same thing rather than having to reconstruct it from two calls.
        if (e.ClickCount >= 2 && EditAt(model) != EditTarget.None)
        {
            e.Handled = true;
            return;
        }

        // The name label is the page's handle: holding it moves the artboard, in whatever
        // tool is active. It used to switch to the Artboard tool instead, which armed a
        // body drag - so the page moved from any point on it and the label did nothing.
        if (HitTestArtboardLabel(model) is { } labelled)
        {
            _vm!.SelectArtboard(labelled);
            _leftDown = true;
            e.Pointer.Capture(this);
            e.Handled = true;
            BeginArtboardMove(labelled, model);
            InvalidateVisual();
            return;
        }

        _leftDown = true;
        e.Pointer.Capture(this);
        e.Handled = true;

        switch (_vm?.Tool ?? EditorTool.Select)
        {
            case EditorTool.Select:
            case EditorTool.Lasso:
                SelectPress(model, e.ClickCount);
                break;

            case EditorTool.Node: NodePress(model); break;
                case EditorTool.Corner: CornerPress(model); break;
                case EditorTool.Pencil: PencilPress(model, PenSample(e, model)); break;
            case EditorTool.Pen: PenPress(model); break;
            case EditorTool.Rectangle:
            case EditorTool.Ellipse:
            case EditorTool.Shape:
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

        // An I-beam over text is the only thing that tells a person the text can be typed into; and a
        // handle over a handle is the only thing that tells them the box can be dragged - including
        // while the block is open for editing, which is when the fit is being recalculated underneath
        // them and a box that needs widening is most obvious.
        Cursor = CursorFor(model);

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
            // The width-profile drag outranks everything else: the press that armed it was taken by
            // the mode, so the only gesture that can be in flight here is that grip.
            if (_profileGrip is { } gripped)
            {
                ProfileDrag(gripped, model);
                return;
            }

            // An artboard gesture outranks the tool: the label is a handle in every tool,
            // so a page drag must not depend on which tool happens to be active.
            if (_artboardGesture != ArtboardGesture.None)
            {
                ArtboardDrag(model);
                return;
            }

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
                case EditorTool.Corner: CornerDrag(model); break;
                case EditorTool.Pencil: PencilDrag(model, PenSample(e, model)); break;

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

                // And the shape tool, which was missing here when it was added: without this case it armed
                // the drag on the press and never recorded a later point, so every release looked like a
                // click and no shape was ever created - the identical mistake the lasso made, two cases up.
                case EditorTool.Shape:
                    if (_shapeStart is not null)
                    {
                        _shapeCurrent = _shiftHeld ? ConstrainSquare(_shapeStart.Value, model) : model;
                        InvalidateVisual();
                    }

                    break;

                case EditorTool.Artboard:
                    // No artboard gesture is active (those returned above), so the press on
                    // the page body fell through to object selection.
                    SelectDrag(model);
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

        // A width-profile grip ends its gesture here, and the profile changes once, through the
        // operation. Nothing was written to the model while the pointer was down, so a drag that came
        // back to where it started commits nothing at all.
        if (_profileGrip is { } releasedGrip)
        {
            _profileGrip = null;
            _leftDown = false;
            e.Pointer.Capture(null);
            CommitProfileDrag(releasedGrip);
            InvalidateVisual();
            return;
        }

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

        // An artboard gesture outranks the tool, for the same reason it does on the move:
        // the label is a handle whatever tool is active.
        if (_artboardGesture != ArtboardGesture.None)
        {
            ArtboardRelease(model);
            return;
        }

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
                case EditorTool.Corner: CornerRelease(); break;
                case EditorTool.Pencil: PencilRelease(); break;

            // The press set the marquee going and the moves grew its path; without this the
            // release never resolved it, so the whole gesture was thrown away at the end.
            case EditorTool.Lasso: SelectRelease(model); break;

            case EditorTool.Pen: PenRelease(model); break;
            case EditorTool.Rectangle: CreateShape(rect: true); break;
            case EditorTool.Ellipse: CreateShape(rect: false); break;

            // Which shape this is comes from the view model, so `rect` is not consulted on this branch.
            case EditorTool.Shape: CreateShape(rect: true); break;
            case EditorTool.Artboard: SelectRelease(model); break;
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        // A whole-notch delta is a click on a notched wheel; anything smaller is a trackpad, which
        // reports one small delta per frame and is meant to accumulate smoothly.
        bool notched = Math.Abs(e.Delta.Y) >= 0.999;

        // A notched wheel's click arrives as a BURST of whole-notch events, so only the first is a
        // step - see WheelNotches. Without this one click zoomed by 1.1^(events in the burst), which
        // is what made the view fly and precision impossible.
        if (notched && !_wheelNotches.BeginsClick(Environment.TickCount64))
        {
            e.Handled = true;
            return;
        }

        // One click is one step; a trackpad's delta is taken as the fraction it is.
        double steps = notched ? Math.Sign(e.Delta.Y) : e.Delta.Y;

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            Point cursor = e.GetPosition(this);
            Point2D before = ModelPointAtScreen(cursor);

            // Anchored on the pointer, so the thing being examined stays put while the zoom changes -
            // the other half of "the page flies all over the screen".
            _userAdjustedZoom = true;
            _layout.Zoom *= Math.Pow(ZoomStepPerNotch, steps);
            _offset = new Vector2D(
                cursor.X - (before.X - _layout.Extent.Left) * _layout.Zoom,
                cursor.Y - (before.Y - _layout.Extent.Top) * _layout.Zoom);
            _offset = _layout.ClampTopLeft(_offset, ViewportPixels);
        }
        else
        {
            Vector2D delta = new(0, -steps * PanPerNotchPixels);
            _offset = _layout.ClampTopLeft(_offset + delta, ViewportPixels);
        }

        InvalidateVisual();
        e.Handled = true;
    }

    // One wheel click is one step: a notched wheel's click arrives as a burst of events, so they
    // are collapsed into one, and a trackpad's fractional deltas are left to accumulate.
    private readonly WheelNotches _wheelNotches = new();

    /// <summary>The wheel-click detector, exposed so a test can fix its burst window.</summary>
    internal WheelNotches WheelNotches => _wheelNotches;

    /// <summary>How much one notch of the wheel zooms. 10% is the conventional step and, applied once
    /// per click rather than per event, it is small enough to land on a seam.</summary>
    internal const double ZoomStepPerNotch = 1.1;

    /// <summary>
    /// How far one notch of the wheel pans, in screen pixels.
    ///
    /// Raised from 60 when the burst collapsing landed: collapsing a click's six events into one step was
    /// right, and it left one click scrolling a sixth as far as it used to. 60 was then too little to move
    /// around a twelve-page pattern with, so the step is larger while the collapsing stays. Kept separate
    /// from the zoom step on purpose - tuning the scroll must not move Ctrl+wheel.
    /// </summary>
    internal const double PanPerNotchPixels = 90.0;

    /// <summary>
    /// How much one press of the toolbar's + / - zooms.
    ///
    /// Deliberately larger than a wheel notch: a button is a deliberate step further, and a notch is a fine
    /// adjustment. Two named constants rather than one, so tuning either cannot move the other by surprise.
    /// </summary>
    internal const double ButtonZoomStep = 1.25;

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
            // The block's own box, carried into world coordinates by every group around it - the same frame
            // the artwork is drawn in, which is the only frame the box can be drawn in and be on the artwork.
            AffineTransform toWorld = SelectionEngine.ToWorld(text);
            Rect2D box = text.BoundingBox();
            if (!box.IsEmpty)
            {
                foreach (Point2D corner in Corners(box))
                {
                    Point2D p = toWorld.Transform(corner);
                    Include(p.X * u.X + p.Y * u.Y, p.X * v.X + p.Y * v.Y);
                }
            }
        }

        foreach (PathItem path in _vm.SelectedPaths())
        {
            // **World**, through the one composition, and not "the stored geometry plus the artboard origin".
            // The chrome is where the handles are, so building it in the untransformed frame put the
            // selection box - and every handle on it - somewhere the artwork is not, which is what made a
            // rotate or a resize inside a transformed group impossible to aim at all (#165).
            AffineTransform toWorld = SelectionEngine.ToWorld(path);

            foreach (SubPath sub in path.SubPaths)
            {
                foreach (CubicBezier segment in sub.Segments())
                {
                    // Project the curve (carried into world space) onto the frame.
                    CubicBezier world = new(
                        toWorld.Transform(segment.P0), toWorld.Transform(segment.P1),
                        toWorld.Transform(segment.P2), toWorld.Transform(segment.P3));
                    (double sMinU, double sMaxU, double sMinV, double sMaxV) = world.ExtentsAlong(u, v);
                    Include(sMinU, sMinV);
                    Include(sMaxU, sMaxV);
                }

                if (sub.Nodes.Count == 1)
                {
                    Point2D p = toWorld.Transform(sub.Nodes[0].Anchor);
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

    private void SelectPress(Point2D model, int clickCount = 1)
    {
        if (_vm is null)
        {
            return;
        }

        _shiftToggleCandidate = null;
        _selectMoved = false;
        _vm.ClearPointSelection();

        // The gradient annotators are drawn over the object, so they grab first: a press on one
        // of their handles is about the gradient, not about whatever is underneath it.
        if (TryBeginGradientDrag(model))
        {
            return;
        }

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

        // **What a click selects.** The outermost object under the pointer, and one level further down for
        // each double-click after that. Reaching the group first is what makes a container selectable with
        // the pointer - otherwise it can only be reached by its name in the layers panel - and drilling is
        // how a person then gets at what is inside it.
        IReadOnlyList<LayerItem> chain = _document is not null &&
            SelectionEngine.ArtboardAt(_document.Artboards, model) is { } under
            ? SelectionEngine.Chain(under, model)
            : Array.Empty<LayerItem>();

        IReadOnlyList<LayerItem> alreadySelected = _vm?.SelectedObjects.ToList() ?? new List<LayerItem>();
        LayerItem? hit = SelectionEngine.Drill(chain, alreadySelected, clickCount) ?? HitTestTopItem(model);

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

        // A press on a member of the current selection keeps the whole selection, so the
        // drag moves all of it. Replacing the selection here is what made a drag on one of
        // several objects drop the others on the floor. A press that never moves is a click,
        // and reduces the selection to this object on release.
        if (_vm.IsObjectSelected(hit))
        {
            _plainClickCandidate = hit;
            BeginObjectMoveTargets(model);
            InvalidateVisual();
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

    /// <summary>
    /// The path whose gradient annotators are on screen: the primary selection, when it carries a
    /// gradient. One object at a time, because the annotators are that object's geometry.
    /// </summary>
    private PathItem? GradientTargetPath()
        => _vm?.PrimarySelection is PathItem { Fill.HasGradient: true } path ? path : null;

    /// <summary>Grabs a gradient handle if the press is on one. False means "not about the gradient".</summary>
    private bool TryBeginGradientDrag(Point2D model)
    {
        if (GradientTargetPath() is not { } path)
        {
            return false;
        }

        Rect box = GetGeometry(path).Bounds;
        if (box.Width <= 0 || box.Height <= 0)
        {
            return false;
        }

        GradientSpec spec = path.Fill.Gradient!;
        GradientHandle handle = GradientAnnotators.HitTest(spec, box, model, PickTolerance * 2.5);
        if (handle == GradientHandle.None)
        {
            return false;
        }

        _gradientPath = path;
        _gradientBox = box;
        _gradientHandle = handle;
        _gradientBefore = path.Fill;
        _gradientSpec0 = spec;
        _gradientMoved = false;
        return true;
    }

    /// <summary>Places the gradient's annotators while the gesture lasts.</summary>
    private void GradientDrag(Point2D model)
    {
        if (_gradientPath is null || _gradientHandle == GradientHandle.None)
        {
            return;
        }

        // Always from the spec as it was when the press happened, so the drag is absolute and
        // does not accumulate whatever rounding each move introduces.
        GradientSpec dragged = GradientAnnotators.Drag(
            _gradientSpec0, _gradientBox, _gradientHandle, model, _shiftHeld);

        _gradientPath.Fill = _gradientPath.Fill with { Gradient = dragged };
        _gradientMoved = true;
        _vm!.RaiseTransformChanged();
        InvalidateVisual();
    }

    /// <summary>Commits a gradient drag as one undo step.</summary>
    private void GradientRelease(Point2D model)
    {
        if (_gradientPath is null || _gradientHandle == GradientHandle.None)
        {
            return;
        }

        if (_gradientMoved && _vm is not null)
        {
            _vm.Execute(new SetFillCommand(_gradientPath, _gradientPath.Fill, _gradientBefore));
        }

        _gradientPath = null;
        _gradientHandle = GradientHandle.None;
        _gradientMoved = false;
        InvalidateVisual();
    }

    /// <summary>
    /// Draws a gradient's annotators: the linear ramp's line with its two ends, or the radial
    /// centre with its radius handles. Drawn in the selection colour, because they are that
    /// selection's chrome.
    ///
    /// Freeform and conical gradients have none: neither has a renderer yet, so handles would be
    /// placed on a picture that is only the fill's flat colour.
    /// </summary>
    private void PaintGradientAnnotators(DrawingContext context, PathItem path)
    {
        Rect box = GetGeometry(path).Bounds;
        if (box.Width <= 0 || box.Height <= 0)
        {
            return;
        }

        GradientSpec spec = path.Fill.Gradient!;
        IReadOnlyList<(GradientHandle Handle, Point2D Point)> handles = GradientAnnotators.Handles(spec, box);
        if (handles.Count == 0)
        {
            return;
        }

        var pen = new Pen(new SolidColorBrush(Color.FromRgb(0x4C, 0x9A, 0xFF)), 1.6);
        if (spec.Kind == GradientKind.Linear)
        {
            context.DrawLine(pen, ModelToScreen(handles[0].Point), ModelToScreen(handles[1].Point));
        }

        foreach ((GradientHandle handle, Point2D point) in handles)
        {
            double radius = handle == GradientHandle.RadialCentre ? 5.0 : 4.0;
            context.DrawEllipse(Brushes.White, pen, ModelToScreen(point), radius, radius);
        }
    }

    // ------------------------------------------------------------------
    // Width-profile mode
    // ------------------------------------------------------------------

    /// <summary>
    /// The path whose width profile the canvas is editing, or null when the mode is off.
    ///
    /// The mode is a state of the **canvas**, not of the selection, and it is entered and left through
    /// <c>profile.editMode</c> - the operation a driver calls and the one the W key calls. While it is
    /// on the handles take the pointer and a click anywhere else is swallowed rather than starting a
    /// marquee or reducing the selection: losing the profile being edited to a stray click is worse
    /// than a click that does nothing at all.
    /// </summary>
    public PathItem? WidthProfileTarget { get; private set; }

    /// <summary>Whether the width-profile handles are being shown and dragged on the canvas.</summary>
    public bool IsEditingWidthProfile => WidthProfileTarget is not null;

    /// <summary>
    /// Opens the width-profile editor on a path, or closes it when given null.
    ///
    /// Both halves of a gesture land here - the operation a driver calls and the W key a person presses
    /// - because two ways into a mode are two modes the moment one of them changes something the other
    /// does not.
    /// </summary>
    public void EditWidthProfile(PathItem? path)
    {
        WidthProfileTarget = path;
        _profileGrip = null;
        _profileDragStart = null;
        _profileDragProfile = null;
        InvalidateVisual();
    }

    /// <summary>
    /// The handles the mode is showing right now, in world coordinates: one per width point of the
    /// profile being edited, or none when the target has no profile.
    ///
    /// During a drag these are the previewed ones - what is on screen - rather than the model's, so a
    /// driver reading them sees the same thing a person does.
    /// </summary>
    public IReadOnlyList<WidthProfileHandle> WidthProfileHandles()
        => ProfileEdit() is { } edit
            ? WidthProfileAnnotators.Handles(edit.Path, _profileDragProfile ?? edit.Profile)
            : Array.Empty<WidthProfileHandle>();

    /// <summary>
    /// The profile the mode is editing, with the path and the stroke it came from - or null when the
    /// target has no profile, or names one the document does not have.
    ///
    /// The profile edited is the **document's own asset**, which is the one every stroke that uses it
    /// draws from. Editing a stroke's private copy would change one stroke while the asset it names
    /// stayed as it was, so re-applying the asset would undo the gesture - which is what an asset means.
    /// A name that resolves to nothing is not a profile this can edit, and the handles are simply not
    /// offered; <c>profile.missing</c> is where that is reported.
    /// </summary>
    private (PathItem Path, int StrokeIndex, WidthProfileSpec Profile)? ProfileEdit()
    {
        if (WidthProfileTarget is not { } path || _document is null || path.OwningLayer() is null)
        {
            return null;
        }

        for (int i = path.Strokes.Count - 1; i >= 0; i--)
        {
            if (path.Strokes[i].WidthProfile is not { IsEmpty: false } named)
            {
                continue;
            }

            // Topmost first: the stroke a person is looking at is the one drawn last.
            return _document.FindProfile(named.Name) is { } stored
                ? (path, i, stored)
                : null;
        }

        return null;
    }

    /// <summary>Grabs a width-profile grip if the press is on one. False means "not on a grip".</summary>
    private bool TryBeginProfileGrip(Point2D model)
    {
        if (ProfileEdit() is not { } edit ||
            WidthProfileAnnotators.HitTest(edit.Path, edit.Profile, model, PickTolerance * 1.6)
                is not { } grip)
        {
            return false;
        }

        _profileGrip = grip;
        _profileDragStroke = edit.StrokeIndex;
        _profileDragStart = edit.Profile;
        _profileDragProfile = null;
        return true;
    }

    /// <summary>Places the profile the pointer is dragging.</summary>
    private void ProfileDrag(WidthProfileGrip grip, Point2D model)
    {
        if (ProfileEdit() is not { } edit || _profileDragStart is not { } start)
        {
            return;
        }

        // Always measured from the profile as it was when the press happened, so the drag is absolute:
        // accumulating each move's arithmetic makes a slow drag and a fast one to the same place stop
        // at different widths.
        _profileDragProfile = WidthProfileAnnotators.Dragged(edit.Path, start, grip, model);
        InvalidateVisual();
    }

    /// <summary>
    /// Commits a width-profile drag as one undo step, through the operation a driver would call.
    ///
    /// The canvas never writes the profile itself. It composes a preview while the pointer is down, and
    /// the model changes once, here, by invoking <c>profile.setPoint</c> - the same entry point the HTTP
    /// endpoint and the assistant use. A dragged handle and a called operation are therefore one edit
    /// rather than two that agree until somebody changes one of them.
    /// </summary>
    private void CommitProfileDrag(WidthProfileGrip grip)
    {
        WidthProfileSpec? dragged = _profileDragProfile;
        WidthProfileSpec? start = _profileDragStart;
        _profileDragProfile = null;
        _profileDragStart = null;

        if (dragged is null || start is null || _vm is null ||
            grip.Index < 0 || grip.Index >= start.Points.Count || grip.Index >= dragged.Points.Count)
        {
            return;
        }

        WidthPoint before = start.Points[grip.Index];
        WidthPoint after = dragged.Points[grip.Index];
        if (after == before)
        {
            // A press that never moved is not an edit. Committing it would put an undo step on the
            // stack that undoes to exactly where it started, which reads as "undo did nothing".
            return;
        }

        try
        {
            EditorOperations.Invoke(
                new AutomationContext { ViewModel = _vm },
                "profile.setPoint",
                System.Text.Json.JsonSerializer.SerializeToElement(new
                {
                    name = start.Name,
                    index = grip.Index,
                    left = after.LeftWidth,
                    right = after.RightWidth,
                }));
        }
        catch (EditorOperationException)
        {
            // The profile went out from under the gesture - deleted or renamed while the pointer was
            // down. The preview is dropped rather than written, so the canvas falls back to the model.
        }
    }

    /// <summary>
    /// The width-profile handles: for every width point, the two edges of the stroke there joined
    /// across it, over a dashed trace of the path they are measured along.
    ///
    /// Drawn in the selection colour, because they are that selection's chrome. The trace is what makes
    /// a profile on a curve readable: without it the handles are a row of discs floating in space.
    /// </summary>
    private void PaintWidthProfileAnnotators(DrawingContext context)
    {
        if (ProfileEdit() is not { } edit)
        {
            return;
        }

        IReadOnlyList<WidthProfileHandle> handles =
            WidthProfileAnnotators.Handles(edit.Path, _profileDragProfile ?? edit.Profile);
        if (handles.Count == 0)
        {
            return;
        }

        var accent = new SolidColorBrush(Color.FromRgb(0x4C, 0x9A, 0xFF));
        var pen = new Pen(accent, 1.6);
        var trace = new Pen(new SolidColorBrush(Color.FromArgb(0x80, 0x4C, 0x9A, 0xFF)), 1.0,
            new DashStyle(new double[] { 3.0, 3.0 }, 0.0));

        context.DrawGeometry(null, trace, BuildScreenGeometry(edit.Path));

        foreach (WidthProfileHandle handle in handles)
        {
            Point left = ModelToScreen(handle.Left.Point);
            Point right = ModelToScreen(handle.Right.Point);
            context.DrawLine(pen, left, right);
            context.DrawEllipse(Brushes.White, pen, left, 4, 4);
            context.DrawEllipse(Brushes.White, pen, right, 4, 4);
        }
    }

    /// <summary>Arms the width-profile mode on the primary selection, or disarms it when already armed.</summary>
    private void ToggleWidthProfileEdit()
        => EditWidthProfile(IsEditingWidthProfile ? null : _vm?.PrimarySelection as PathItem);

    /// <summary>
    /// Whether a key selects a tool. Any of them leaves the width-profile mode: a mode that kept the
    /// pointer while the toolbar said "pen" would swallow the pen's clicks, and the toolbar is what a
    /// person trusts about what the next click will do.
    /// </summary>
    private static bool IsToolKey(Key key)
        => key is Key.V or Key.A or Key.P or Key.T or Key.O or Key.M or Key.L or Key.Q or Key.C
            or Key.N or Key.S;

    private void SelectDrag(Point2D model)
    {
        if (_gradientHandle != GradientHandle.None)
        {
            GradientDrag(model);
            return;
        }

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

        // `delta` is what the pointer travelled in **world** coordinates, and the geometry it moves is
        // stored in each item's own placement frame. Carrying the delta into that frame per item is what
        // makes the artwork follow the pointer inside a transformed group; applying the world delta
        // straight to the stored geometry moved it by G∘delta instead (#165).
        foreach (PathItem path in _dragPaths)
        {
            path.RestoreGeometryFrom(_dragOriginals[path]);
            path.TranslateGeometryBy(SelectionEngine.DeltaInItem(path, delta));
        }

        foreach (TextItem text in _dragTexts)
        {
            text.Origin = _dragTextOrigins[text] + SelectionEngine.DeltaInItem(text, delta);
        }

        // An image's geometry is its placement, so dragging shifts the box and the
        // picture travels with it.
        foreach (ImageItem image in _dragImages)
        {
            Rect2D start = _dragImageOrigins[image];
            Vector2D moved = SelectionEngine.DeltaInItem(image, delta);
            image.Placement = new Rect2D(
                start.X + moved.X, start.Y + moved.Y, start.Width, start.Height);
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
        if (_gradientHandle != GradientHandle.None)
        {
            GradientRelease(model);
            return;
        }

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
        else if (_plainClickCandidate is not null)
        {
            // The press kept a multi-selection so it could be dragged; it never moved, so it
            // was a click, and a click reduces the selection to what was clicked.
            _vm!.SelectObject(_plainClickCandidate);
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
        _plainClickCandidate = null;
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
        // `SelectionEngine.ToWorld` is the one place the composition of ancestor group transforms is
        // stated, so the chrome lands where the artwork is drawn rather than where the model's raw
        // numbers are. A group's box is in its own local space, which its own transform carries into
        // the frame it is placed in.
        AffineTransform toWorld = item is ArtGroup own
            ? SelectionEngine.ToWorld(item).Compose(own.Transform)
            : SelectionEngine.ToWorld(item);

        Rect2D box = item switch
        {
            PathItem path => path.BoundingBox(),
            TextItem text => text.BoundingBox(),
            ArtGroup group => group.BoundingBox(),
            ImageItem image => image.Placement,
            _ => Rect2D.Empty,
        };

        return box.IsEmpty ? box : toWorld.Transform(box);
    }

    // ---- a world gesture, expressed in the frame the geometry is stored in -----
    //
    // Every editing gesture happens in **world** coordinates, because that is where the pointer is and
    // where the selection chrome is drawn. The geometry it changes is stored in the item's own
    // **placement frame**: a path inside `translate(50,50) scale(2)` keeps the coordinates the file gave
    // it, and the group's transform carries them out. So the gesture's own transform has to be conjugated
    // into that frame before it is applied, or a drag moves the artwork by G∘delta instead of delta -
    // twice as far as the pointer, and in a rotated group in a different direction (#165).
    //
    // `SelectionEngine.ToWorld` is the one place that composition is stated (#159), so the conversion is
    // its inverse and nothing else.

    /// <summary>
    /// A world-space affine as it acts on an item whose geometry is stored in the item's own placement
    /// frame, or the identity when that frame collapses the plane - for callers that have a fixed shape to
    /// apply rather than a point list to walk.
    ///
    /// The conversion itself is <see cref="SelectionEngine.InItemFrame"/>, stated once in Core so that the
    /// canvas, the pane and the commands cannot come to three answers about the frame (#173).
    /// </summary>
    private static AffineTransform InItemFrameOrIdentity(LayerItem item, AffineTransform world)
        => SelectionEngine.InItemFrame(item, world) ?? AffineTransform.Identity;

    /// <summary>The four corners of a rectangle, which is how a box is carried through a transform that may
    /// turn or shear it.</summary>
    private static IEnumerable<Point2D> Corners(Rect2D box) => new[]
    {
        new Point2D(box.Left, box.Top),
        new Point2D(box.Right, box.Top),
        new Point2D(box.Right, box.Bottom),
        new Point2D(box.Left, box.Bottom),
    };

    /// <summary>
    /// Applies an affine map, already expressed in a text block's own frame, to that block.
    ///
    /// A block is an origin, one rotation angle and a font size, so it stores a **similarity** and no
    /// other map. The origin takes the map exactly; the angle is the map's linear part read as a
    /// rotation; the size is its area's square root, which is the uniform scale of a similarity. All
    /// three are exact when the frame around the block is a similarity - a group that scales, turns or
    /// flips - and are the nearest thing the model can store when it is sheared or scaled unevenly. That
    /// limit is stated on #165 rather than hidden here.
    /// </summary>
    private static void TransformTextIn(TextItem text, TextItem before, AffineTransform local)
    {
        text.Origin = local.Transform(before.Origin);
        text.RotationRadians = before.RotationRadians + Math.Atan2(local.B, local.A);

        double scale = Math.Sqrt(Math.Abs(local.Determinant));
        foreach (TextRun run in text.Runs)
        {
            run.FontSize *= scale;
        }
    }

    /// <summary>
    /// Applies an affine map, already expressed in an image's own frame, to its placement box by mapping
    /// the box's four corners and taking their bounds.
    ///
    /// Exact whenever the map is axis-aligned - a translation, or a scale - which is every frame but a
    /// rotated or sheared one. A rotated group would need a placement the model does not have (the box is
    /// an axis-aligned rectangle), so the corners' bounds are the honest answer there; stated on #165.
    /// </summary>
    private static Rect2D TransformPlacement(Rect2D placement, AffineTransform local)
    {
        Point2D a = local.Transform(new Point2D(placement.Left, placement.Top));
        Point2D b = local.Transform(new Point2D(placement.Right, placement.Top));
        Point2D c = local.Transform(new Point2D(placement.Right, placement.Bottom));
        Point2D d = local.Transform(new Point2D(placement.Left, placement.Bottom));

        double left = Math.Min(Math.Min(a.X, b.X), Math.Min(c.X, d.X));
        double top = Math.Min(Math.Min(a.Y, b.Y), Math.Min(c.Y, d.Y));
        double right = Math.Max(Math.Max(a.X, b.X), Math.Max(c.X, d.X));
        double bottom = Math.Max(Math.Max(a.Y, b.Y), Math.Max(c.Y, d.Y));
        return new Rect2D(left, top, right - left, bottom - top);
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

        // The resize as one world-space affine: into the box's own axes, an axis scale about the fixed
        // pivot, and back out. Stating it once lets each item's own frame be conjugated into it below,
        // which is what makes a resize inside a transformed group land where the handles are (#165).
        AffineTransform resizeWorld =
            AffineTransform.CreateRotationAround(_resizeCenter0, _resizeAngle0)
                .Compose(AffineTransform.CreateScaleAround(_resizePivot, sx, sy))
                .Compose(AffineTransform.CreateRotationAround(_resizeCenter0, -_resizeAngle0));

        foreach (TextItem text in _resizeTexts)
        {
            TextItem before = _resizeTextBefore[text];
            text.CopyFrom(before);
            TransformTextIn(text, before, InItemFrameOrIdentity(text, resizeWorld));
        }

        // An image scales by moving and resizing its placement box. Scaling is not
        // uniform unless the handle (or Shift) made it so: the pixels stretch to fill the
        // box, which is what a person sees happen to a picture they drag a corner of.
        foreach (ImageItem image in _resizeImages)
        {
            Rect2D start = _resizeImageBefore[image];
            Rect2D scaled = TransformPlacement(start, InItemFrameOrIdentity(image, resizeWorld));
            image.Placement = new Rect2D(
                scaled.X, scaled.Y, Math.Max(0.5, scaled.Width), Math.Max(0.5, scaled.Height));
        }

        // Shift + a group selection scales each object in place (about its own
        // centre) instead of translating it to match the group scale.
        bool groupInPlace = _shiftHeld && _vm!.SelectedObjects.Any(o => o is ArtGroup);
        foreach (PathItem path in _resizePaths)
        {
            path.RestoreGeometryFrom(_resizeOriginals[path]);

            // In place means about the object's own centre, which is a world point as surely as the
            // selection's pivot is: it is mapped into the path's frame by the same conjugation.
            AffineTransform world = groupInPlace
                ? AffineTransform.CreateScaleAround(
                    SelectionEngine.ToWorld(path).Transform(path.BoundingBox().Center), sx, sx)
                : resizeWorld;

            if (SelectionEngine.InItemFrame(path, world) is { } inFrameLocal)
            {
                path.TransformGeometry(inFrameLocal);
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

        // The turn is a world-space one, about the world centre of the selection; each item's geometry is
        // stored in its own frame, so the turn is conjugated into that frame before it is applied. The old
        // code subtracted only the artboard origin, which is the right frame only when no group has a
        // transform - inside a rotated group it turned the artwork about a point that was not the one the
        // handle was drawn at (#165).
        AffineTransform turn = AffineTransform.CreateRotationAround(_rotateCenter, angle);

        foreach (PathItem path in _rotatePaths)
        {
            path.RestoreGeometryFrom(_rotateOriginals[path]);
            if (SelectionEngine.InItemFrame(path, turn) is { } inFrameTurn)
            {
                path.TransformGeometry(inFrameTurn);
            }
        }

        foreach (TextItem text in _rotateTexts)
        {
            TextItem before = _rotateTextBefore[text];
            text.CopyFrom(before);
            TransformTextIn(text, before, InItemFrameOrIdentity(text, turn));
        }

        _chromeAngle = _rotateAngle0 + angle; // selection box rotates with the objects
        _vm!.SetSelectionRotationRadians(_chromeAngle);

        _vm.RaiseTransformChanged();
        InvalidateVisual();
    }

    // Node tool: nodes, handles and segments
    // ------------------------------------------------------------------

    // ---- the corner tool -----------------------------------------------
    // Press on a corner, drag, release. The radius is the distance from the pointer to the original
    // corner, and the corner itself does not move while dragging - it is what the radius is measured
    // from. The geometry is previewed live by restoring the pre-drag snapshot and rounding again, so the
    // whole drag is one undo step rather than one per mouse move.

    // ---- the pencil ----------------------------------------------------
    // The captured points, joined, drawn as they are - no fitting, no smoothing, no correction. That is
    // the request and it is also the right behaviour: a curve re-fitted under the pointer wanders as the
    // fit wobbles, so the line would not follow the hand but argue with it.

    private void PencilPress(Point2D model, InputSample sample)
    {
        _pencilPoints.Clear();
        _pencilSamples.Clear();
        _pencilClock.Restart();
        _pencilPoints.Add(model);
        _pencilSamples.Add(sample);
        _gestureMoved = false;
        InvalidateVisual();
    }

    private void PencilDrag(Point2D model, InputSample sample)
    {
        // Points a pointer repeats are dropped as they arrive, because fitting to them would put a
        // segment on every one.
        if (!FreehandFitter.ShouldKeep(_pencilPoints, model))
        {
            return;
        }

        _pencilPoints.Add(model);
        _pencilSamples.Add(sample);
        _gestureMoved = true;
        InvalidateVisual();
    }

    /// <summary>Fits the stroke **once**, on release, and adds it as a path.</summary>
    private void PencilRelease()
    {
        if (_pencilSamples.Count >= 2)
        {
            // The samples, not the bare points: this is the one place a pen's pressure and tilt enter the model,
            // and they enter it as the width profile and nib angle the drawing keeps.
            _vm.DrawFreehand(_pencilSamples.ToList());
        }

        _pencilPoints.Clear();
        _pencilSamples.Clear();
        _pencilClock.Reset();
        _gestureMoved = false;
        InvalidateVisual();
    }

    /// <summary>
    /// What the pointer reported, as a sample: where it is in document space, how long the stroke has been running,
    /// and - on a pen - how hard it is pressed and which way it is laid over.
    ///
    /// **A mouse is at full pressure and upright, rather than at whatever number it reports.** Platforms disagree
    /// about what a mouse's pressure member holds: some send zero and some send a half. Obeying it would draw every
    /// mouse line at nothing or at half width the moment anybody switched pressure dynamics on, which is the
    /// opposite of what a device with no pressure sensor means. Tilt is a pen's answer for the same reason.
    /// </summary>
    private InputSample PenSample(PointerEventArgs e, Point2D model)
    {
        bool pen = e.Pointer.Type == PointerType.Pen;
        PointerPointProperties properties = e.GetCurrentPoint(this).Properties;

        return new InputSample(
            model,
            _pencilClock.Elapsed.TotalMilliseconds,
            pen ? properties.Pressure : 1.0,
            pen ? properties.XTilt : 0.0,
            pen ? properties.YTilt : 0.0);
    }

    /// <summary>Draws the stroke being drawn: the captured points, joined, unfitted.</summary>
    private void PaintPencilPreview(DrawingContext context)
    {
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22)), 1.5);

        for (int i = 1; i < _pencilPoints.Count; i++)
        {
            context.DrawLine(pen, ModelToScreen(_pencilPoints[i - 1]), ModelToScreen(_pencilPoints[i]));
        }
    }
    /// <summary>Begins rounding a corner, if the press landed on one.</summary>
    private void CornerPress(Point2D model)
    {
        (PathItem Path, int SubPath, int Node, double Distance)? hit =
            _vm.NearestCorner(model, Math.Max(PickTolerance, 6));

        if (hit is null)
        {
            return;
        }

        _roundPath = hit.Value.Path;
        _roundSubPath = hit.Value.SubPath;
        _roundNode = hit.Value.Node;
        // In document space, because that is where the pointer is: the anchor itself is artboard-local.
        _roundCorner = _roundPath.SubPaths[_roundSubPath].Nodes[_roundNode].Anchor + _roundPath.ArtboardOffset();

        // The snapshot the drag restores from, so every preview starts from the same shape.
        _roundBefore = _roundPath.GeometrySnapshot();
    }

    /// <summary>Previews the rounding: the radius is how far the pointer is from the corner.</summary>
    private void CornerDrag(Point2D model)
    {
        if (_roundPath is null || _roundBefore is null)
        {
            return;
        }

        double radius = model.DistanceTo(_roundCorner);
        if (radius <= 0)
        {
            return;
        }

        _roundPath.RestoreGeometryFrom(_roundBefore);
        _vm.RoundCornerByDrag(_roundPath, _roundSubPath, _roundNode, radius);
        _gestureMoved = true;
        InvalidateVisual();
    }

    /// <summary>Ends the drag: one undo step for the whole gesture.</summary>
    private void CornerRelease()
    {
        if (_roundPath is not null && _roundBefore is not null && _gestureMoved)
        {
            _vm.CommitGeometry(_roundPath, _roundBefore, "Round corner");
        }

        _roundPath = null;
        _roundBefore = null;
        _gestureMoved = false;
    }
    // ---- a shape's control points --------------------------------------
    // Drawn only for the shape they belong to, and only for the parameters that shape actually has, so a
    // handle on screen is always a handle that does something. They take priority over the node tool's
    // segments because they sit on the shape's own box, which is exactly where a segment drag would
    // otherwise reach.

    /// <summary>Starts dragging a control point, if the press landed on one. True when it did.</summary>
    private bool ShapeHandlePress(Point2D model)
    {
        foreach (PathItem path in _vm.SelectedPaths())
        {
            if (path.Shape is not { } shape)
            {
                continue;
            }

            ShapeHandlePoint? hit = ShapeHandles.Nearest(shape, model, Math.Max(PickTolerance, 7));
            if (hit is null)
            {
                continue;
            }

            _shapeHandlePath = path;
            _shapeHandleDefinition = shape;
            _shapeHandle = hit.Handle;
            _shapeHandleBefore = path.GeometrySnapshot();
            _gestureMoved = false;
            return true;
        }

        return false;
    }

    /// <summary>Previews the parameter change: the geometry is rebuilt from the shape, live.</summary>
    private void ShapeHandleDrag(Point2D model)
    {
        if (_shapeHandlePath is null || _shapeHandleBefore is null || _shapeHandleDefinition is null)
        {
            return;
        }

        ShapeParameters moved = ShapeHandles.Move(_shapeHandleDefinition, _shapeHandle, model);

        // From the pre-drag snapshot every time, so the drag is always built from the shape as it was
        // rather than compounding its own results.
        _shapeHandlePath.RestoreGeometryFrom(_shapeHandleBefore);
        new ShapeDefinition(_shapeHandleDefinition.Kind, moved).ApplyTo(_shapeHandlePath);

        // The definition travels with the path, or the next drag would start from stale parameters.
        _shapeHandleDefinition = _shapeHandlePath.Shape;

        _gestureMoved = true;
        InvalidateVisual();
    }

    /// <summary>Ends the drag: one undo step for the whole gesture.</summary>
    private void ShapeHandleRelease()
    {
        if (_shapeHandlePath is not null && _shapeHandleBefore is not null && _gestureMoved)
        {
            _vm.CommitGeometry(_shapeHandlePath, _shapeHandleBefore, $"Drag shape {_shapeHandle}");
        }

        _shapeHandlePath = null;
        _shapeHandleBefore = null;
        _shapeHandleDefinition = null;
        _gestureMoved = false;
    }

    /// <summary>Draws the control points of every selected shape.</summary>
    private void PaintShapeHandles(DrawingContext context)
    {
        var fill = new SolidColorBrush(Color.FromRgb(0x4C, 0x9A, 0xFF));
        var rotate = new SolidColorBrush(Color.FromRgb(0xFF, 0xC4, 0x4C));

        foreach (PathItem path in _vm.SelectedPaths())
        {
            if (path.Shape is not { } shape)
            {
                continue;
            }

            foreach (ShapeHandlePoint handle in ShapeHandles.For(shape))
            {
                // The shape's parameters are artboard-local; the handles are drawn in model space.
                Point screen = ModelToScreen(handle.Position + path.ArtboardOffset());
                context.FillRectangle(
                    handle.Handle == ShapeHandle.Rotation ? rotate : fill,
                    new Rect(screen.X - 3.5, screen.Y - 3.5, 7, 7));
            }
        }
    }
    private void NodePress(Point2D model)
    {
        // A control point takes priority: it sits on the shape's own box, exactly where a segment drag would
        // otherwise reach.
        if (ShapeHandlePress(model))
        {
            return;
        }

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
            NodePick? candidatePick = InPathFrame(candidate, model) is { } local
                ? PathPicking.PickNode(candidate, local, PickTolerance * 1.6)
                : null;
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
            if (pickPath is not null && InPathFrame(pickPath, model) is { } local)
            {
                pick = PathPicking.PickNode(pickPath, local, PickTolerance * 1.6);
            }
        }

        if (pick is not null && pickPath is not null)
        {
            if (!_shiftHeld && !_vm.IsObjectSelected(pickPath))
            {
                _vm.SelectObject(pickPath); // select the path whose node we grab
            }

            // The gesture is stated in **world** coordinates - where the pointer is - and carried into the
            // path's own frame once, by the delta conversion in NodeDrag (#173).
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
        SegmentPick? segment = segmentPath is null || InPathFrame(segmentPath, model) is not { } segmentLocal
            ? null
            : PathPicking.ClosestSegment(segmentPath, segmentLocal, PickTolerance * 1.6);
        if (segment is null)
        {
            segmentPath = HitTestTopPath(model);
            segment = segmentPath is null || InPathFrame(segmentPath, model) is not { } again
                ? null
                : PathPicking.ClosestSegment(segmentPath, again, PickTolerance * 1.6);
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
                _pendingInsertPoint = InPathFrame(segmentPath, model) ?? default;
            }

            BeginSegmentDrag(segmentPath, subIndex, segment.Value.SegmentIndex, model);
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

    private void BeginSegmentDrag(PathItem path, int subIndex, int segmentIndex, Point2D world)
    {
        SubPath sub = path.SubPaths[subIndex];
        (int a, int b) = sub.SegmentEndNodes(segmentIndex);
        _segmentPath = path;
        _segmentSub = sub;
        _segmentIndex = segmentIndex;
        _segmentBefore = path.GeometrySnapshot();
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
        //
        // The chord is written in the path's own frame, so the world pointer is carried into it first:
        // the group above the path is not a plain offset, and a projection taken in the world frame puts
        // the bend somewhere the pointer is not (#173).
        Point2D local = InPathFrame(path, world) ?? world;
        Point2D a0 = _segmentOrigA.A;
        Point2D b0 = _segmentOrigB.A;
        Vector2D chord = b0 - a0;
        double u = 0.5;
        if (chord.LengthSquared > 1e-9)
        {
            u = MathUtils.Clamp01((local - a0).Dot(chord) / chord.LengthSquared);
        }

        _segmentBendMode = a != b;
        _bendGrabU = MathUtils.Clamp(u, 0.1, 0.9);
        _dragStartModel = world;
        _gestureMoved = false;
    }

    private void NodeDrag(Point2D model)
    {
        if (_shapeHandlePath is not null)
        {
            ShapeHandleDrag(model);
            return;
        }

        if (_nodePath is not null && _nodeSub is not null)
        {
            // Mutate the grabbed node directly, recomputing from the stored
            // originals — never restoring whole geometry mid-gesture. The delta
            // is measured from the grab point (press position), so a handle
            // follows the cursor 1:1 instead of inheriting a fixed offset.
            //
            // The pointer travels in **world** coordinates and the node's coordinates are stored in the
            // path's own placement frame, so the world delta is carried across once, by the composition
            // #165 stated - the whole of the drag below is stated in the stored frame. Applying the world
            // delta to the stored anchor moved a node inside `scale(2)` half as far again as the pointer
            // (#173); the release snap in ApplyNodeSnap was converted in #165 and the drag itself was not.
            Vector2D delta = model - _dragStartModel;
            if (_shiftHeld)
            {
                delta = SnapTranslation(delta);
            }

            delta = SelectionEngine.DeltaInItem(_nodePath, delta);

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

            // The bend is solved in the path's own frame, so the world pointer is carried into it there -
            // the artboard origin alone is that frame only when no group has a transform (#173).
            Point2D target = InPathFrame(_segmentPath, targetWorld) ?? targetWorld;
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

        // The node's own geometry is in the path's frame; the anchor is compared, and the snap offset
        // applied, in world coordinates. Both halves therefore go through the one composition - the old
        // code added the artboard origin alone, which is a world point only when no group transforms the
        // path, so a node inside a group snapped to a place the artwork was not (#165).
        AffineTransform toWorld = SelectionEngine.ToWorld(_nodePath);
        Point2D world = toWorld.Transform(_nodeSub.Nodes[_nodeIndex].Anchor);

        // 1) Dragging one end onto the other closes the path.
        if (!_nodeSub.IsClosed && (_nodeIndex == 0 || _nodeIndex == _nodeSub.Nodes.Count - 1))
        {
            int otherIndex = _nodeIndex == 0 ? _nodeSub.Nodes.Count - 1 : 0;
            Point2D otherWorld = toWorld.Transform(_nodeSub.Nodes[otherIndex].Anchor);
            if (world.DistanceTo(otherWorld) <= PickTolerance * 1.5)
            {
                PathNode node = _nodeSub.Nodes[_nodeIndex];
                Vector2D delta = SelectionEngine.DeltaInItem(_nodePath, otherWorld - world);
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
            Vector2D delta = SelectionEngine.DeltaInItem(_nodePath, snap - world);
            node.Anchor += delta;
            node.InHandle += delta;
            node.OutHandle += delta;
            InvalidateVisual();
        }
    }

    private void NodeRelease()
    {
        if (_shapeHandlePath is not null)
        {
            ShapeHandleRelease();
            return;
        }

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

        // The pen keeps its state across a tool switch, so that stepping to the node tool and
        // coming back carries on from the last point. What ends a path is closing it or Escape.
        // A path that was deleted or undone away while another tool was active has nothing left
        // to continue from, so the state goes with it rather than pointing at a detached item.
        if (_penPath is not null && _penPath.Container is null)
        {
            _penPath = null;
            _penNode = null;
            _penBefore = null;
            _penClosePending = false;
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

        // 2) Otherwise the page body is the artwork's, not the page's: an artboard is moved
        //    by its name label (handled before the tool switch), so a press here behaves
        //    like the Select tool - picking objects and starting a marquee.
        Artboard? artboard = HitTestArtboard(model);
        if (artboard is not null)
        {
            _vm.SelectArtboard(artboard);
            _artboard = null;
            if (TryHitArtboardHandle(artboard, model, out int handle))
            {
                _artboard = artboard;
                _artboardBefore = artboard.Bounds;
                _artboardGesture = ArtboardGesture.Resize;
                _artboardHandle = handle;
            }
            else
            {
                SelectPress(model);
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

    /// <summary>Starts moving a page from its name label.</summary>
    private void BeginArtboardMove(Artboard artboard, Point2D model)
    {
        _artboard = artboard;
        _artboardBefore = artboard.Bounds;
        _artboardGesture = ArtboardGesture.Move;
        _dragStartModel = model;
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
                // travel with the origin. A press on the label that never moved is a click,
                // and a click must not leave a no-op on the undo stack.
                if (_artboard.Bounds != _artboardBefore)
                {
                    _vm.Execute(new SetArtboardBoundsCommand(
                        _artboard, _artboardBefore, _artboard.Bounds, "Move artboard"));
                }

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
        PathItem shape = _vm.Tool == EditorTool.Shape
            ? ShapeLibrary.Create(_vm.CurrentShape, new ShapeParameters
            {
                Centre = new Point2D(box.X + (box.Width / 2), box.Y + (box.Height / 2)),
                Width = box.Width,
                Height = box.Height,
            })
            : rect
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
        _vm.Status = _vm.Tool == EditorTool.Shape
            ? $"{ShapeLibrary.Name(_vm.CurrentShape)} created"
            : rect ? "Rectangle created" : "Ellipse created";
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

        // Kept for the duration of the pass so a filtered object can rasterise itself in the same coordinate
        // space the canvas is drawing in, which is what makes the offscreen render line up pixel for pixel.
        _paintWorld = world;

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
                    PaintItem(context, item, _document.Orphans.Opacity, AffineTransform.Identity);
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
                PaintItem(context, item, layer.Opacity,
                    AffineTransform.CreateTranslation(artboard.X, artboard.Y));
            }
        }
    }

    /// <summary>
    /// The viewport a painter with no paint pass of its own culls against: wider than any document, so nothing is
    /// culled and the caller decides for itself what is worth drawing.
    /// </summary>
    private static readonly Rect2D WholePasteboard = new(-1e6, -1e6, 2e6, 2e6);

    /// <summary>
    /// Paints one item through the canvas's own item painter, for a caller that has no paint pass of its own - the
    /// brush editor's live preview (issue #113).
    ///
    /// **The one implementation, reached rather than copied.** <see cref="PaintItem"/> is what the canvas draws every
    /// item with, and a preview that reimplemented it would be a preview that lies - the gap the brush editor's audit
    /// recorded. So the preview calls this and gets the same switch, the same fills, gradients, strokes and nested
    /// brush art the canvas gets.
    ///
    /// It builds a **fresh** painter rather than borrowing the live one. A canvas's paint state is mid-pass by
    /// definition - the viewport it measured, the world transform a filtered object rasterises in, the width profile
    /// a drag is displaying - and a preview reading it would draw whatever the person happened to be dragging.
    ///
    /// <paramref name="paintWorld"/> is that world transform's analogue for the caller's own pass: what a filtered
    /// item rasterises against. Null - a caller with no transform of its own - draws such an item unfiltered, which
    /// is what the canvas does with no pass in force.
    /// </summary>
    internal static void PaintItemStandalone(
        DrawingContext context,
        CadDocument? document,
        LayerItem item,
        double opacity,
        AffineTransform toWorld,
        Avalonia.Matrix? paintWorld = null)
    {
        var painter = new CanvasWorkspace
        {
            _document = document,
            _paintWorld = paintWorld,
            _worldViewport = WholePasteboard,
        };

        if (document is not null)
        {
            RegisterEmbeddedFonts(document);
        }

        painter.PaintItem(context, item, opacity, toWorld);
    }

    /// <summary>
    /// Paints one item, with <paramref name="toWorld"/> carrying the frame it is **placed** in into
    /// world coordinates: the artboard origin for a top-level item, and each enclosing group's
    /// <see cref="ArtGroup.Transform"/> composed on the way down.
    ///
    /// The frame is threaded rather than recomputed because a filtered object and the off-screen test
    /// need the same answer the geometry is drawn with, and two walks that compute it separately are
    /// how the canvas and the export came to disagree in issue #159.
    /// </summary>
    private void PaintItem(DrawingContext context, LayerItem item, double opacity, AffineTransform toWorld)
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

            PaintItemCore(context, item, opacity, toWorld);
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

    private void PaintItemCore(DrawingContext context, LayerItem item, double opacity, AffineTransform toWorld)
    {

        switch (item)
        {
            case PathItem path when path.IsVisible:
                // The off-screen test is made in world coordinates, so it goes through the accumulated
                // frame: a path inside a group that moves it onto the page is on the page, and one whose
                // group moves it off is not. `WorldBounds` alone ignores every enclosing group, which is
                // the same defect as drawing at the wrong place - a picture that disappears when it is
                // scrolled to.
                Rect2D local = path.BoundingBox();
                if (!local.IsEmpty)
                {
                    Rect2D bounds = toWorld.Transform(local);
                    if (!bounds.Inflated(WidestStroke(path) + 1).Intersects(_worldViewport))
                    {
                        return; // culled (off-screen)
                    }
                }

                PaintPath(context, path, opacity * path.Opacity, toWorld);
                break;

            case TextItem text when text.IsVisible:
                PaintText(context, text, opacity);
                break;

            case ImageItem image when image.IsVisible:
                PaintImage(context, image, opacity);
                break;

            case ArtGroup group when group.IsVisible:
                // **The group's transform is drawn, not ignored.** It is the same composition the
                // exporters make: `childToDoc = toDoc.Compose(group.Transform)` as the walk descends,
                // which is outermost-second and innermost-first because a transform is a local→parent
                // map. Pushing it here puts every child, its clips, its strokes and the groups inside it
                // in the frame the file says, without baking anything into the model.
                AffineTransform childToWorld = toWorld.Compose(group.Transform);

                using (context.PushTransform(GroupTransform(group.ArtboardOffset(), group.Transform)))
                {
                    foreach (LayerItem child in group.Children)
                    {
                        PaintItem(context, child, opacity * group.Opacity, childToWorld);
                    }
                }

                break;
        }
    }

    /// <summary>
    /// A group's transform as a matrix the canvas can push.
    ///
    /// A group's transform maps its own space into the space it is placed in, and the painters below
    /// draw geometry at <c>artboard origin + local</c>. The two are joined by conjugating the transform
    /// with that origin - <c>T(origin) ∘ G ∘ T(−origin)</c> - which is the only difference between this
    /// and the exporter's bare <c>cm</c>: the exporter writes one page per artboard and never carries the
    /// origin, so its page frame starts where the canvas's world frame is offset.
    ///
    /// Successive pushes nest, so a group inside a group composes the way
    /// <c>PdfDocumentExporter.PaintItem</c> composes its transforms as it descends.
    /// </summary>
    private static Avalonia.Matrix GroupTransform(Vector2D origin, AffineTransform transform)
        => Avalonia.Matrix.CreateTranslation(-origin.X, -origin.Y)
           * new Avalonia.Matrix(transform.A, transform.B, transform.C, transform.D, transform.E, transform.F)
           * Avalonia.Matrix.CreateTranslation(origin.X, origin.Y);

    /// <summary>
    /// Draws a hatch fill: its line families, in the object's **stroke** colour, clipped to the object.
    ///
    /// The clipping happened in the generator - every segment handed back is already inside the region, holes
    /// and concavities included - so this only strokes what it is given. That is deliberate: the same segments
    /// are what an export writes, so a rendering fault cannot come from a second clipping implementation
    /// disagreeing with the first.
    /// </summary>
    private void PaintHatch(DrawingContext context, PathItem path, HatchSpec hatch, double opacity)
    {
        IReadOnlyList<FlattenedOutline> outlines = PathFlattener.Flatten(path);
        if (outlines.Count == 0)
        {
            return;
        }

        // A hatch is hatching, not a filled shape: its lines take the stroke's colour, which is the colour that
        // belongs to a line. The thickness keeps the same minimum the stroke does, so a hatch stays visible at
        // any zoom rather than fading to nothing.
        // A hatch is hatching, not a filled shape: its lines take the stroke's colour, which is the colour that
        // belongs to a line. On a path with a stack that is the bottom stroke - the outline the hatching sits
        // in - rather than an arbitrary one.
        IBrush brush = ToBrush(path.Stroke.Color, opacity);
        const double MinDevicePixels = 0.75;
        double floor = MinDevicePixels / Math.Max(_layout.Zoom, 1e-6);

        foreach (HatchSegment segment in HatchGenerator.Segments(
                     hatch, outlines, path.Fill.Rule, path.BoundingBox()))
        {
            var pen = new Pen(
                brush,
                thickness: Math.Max(segment.Line.Width, floor),
                lineCap: ToLineCap(segment.Line.Cap));

            if (!segment.Line.Dash.IsEmpty)
            {
                double factor = segment.Line.Width > 0 ? pen.Thickness / segment.Line.Width : 1.0;
                pen.DashStyle = new DashStyle(
                    segment.Line.Dash.Segments.Select(d => Math.Max(0.01, d * factor)).ToArray(),
                    segment.Line.Dash.Offset * factor);
            }

            context.DrawLine(
                pen,
                new Point(segment.A.X, segment.A.Y),
                new Point(segment.B.X, segment.B.Y));
        }
    }

    /// <summary>
    /// The widest visible stroke on a path, or zero when it has none.
    ///
    /// Used where a path is treated as one object rather than as its strokes - culling, and the node overlay's
    /// line weight. Reading the bottom stroke instead would cull a path whose second stroke reaches further than
    /// the first, which is a drawing that vanishes at some zoom levels and not others.
    /// </summary>
    private static double WidestStroke(PathItem path)
        => path.HasVisibleStroke
            ? path.Strokes.Where(s => s.HasVisibleOutline).Max(s => s.Width)
            : 0.0;

    /// <summary>
    /// The filter graphs for this path's raster effects, in order, or empty when it has none.
    ///
    /// A raster effect belongs to a **stroke**, and this renders the path as one picture - so the first stroke that
    /// carries any supplies them. A path whose strokes each carry different raster effects renders the first
    /// stroke's, which is the honest limit of drawing the path in one pass; the single-stroke case is exact.
    /// </summary>
    private static List<FilterSpec> RasterFiltersFor(PathItem path)
    {
        // **World** bounds, because the region is where the artwork is painted, not where it is stored. A path's
        // geometry is kept in its artboard's own coordinates and drawn at the artboard's offset, so a region built
        // from the stored box lands beside the line on every page that is not at the document origin - the stroke is
        // then rasterised outside its own bitmap, and the painter that reports "handled" drops it from the page.
        Geometry.Rect2D bounds = path.WorldBounds();

        foreach (StrokeSpec stroke in path.Strokes)
        {
            if (stroke.AllRasterEffects is { Count: > 0 } effects)
            {
                // The region starts from the stroke's own extent, not the path's centreline: a stroke reaches half
                // its width beyond the line, and an outer glow reaches further still. Using the centreline box clips
                // exactly the part of a glow that falls outside the line - which is the whole of an outer glow.
                Geometry.Rect2D region = bounds.Inflated(stroke.Width / 2);

                return effects
                    .Select(effect => RasterEffectFilters.ToFilter(effect, stroke.Color, region))
                    .Where(filter => filter is not null)
                    .Select(filter => filter!)
                    .ToList();
            }
        }

        return new List<FilterSpec>();
    }
    /// Draws a path, through its filter when the document has one.
    ///
    /// A filter is a raster operation, so an object that has one cannot be drawn with the same draw calls as one
    /// that does not: it is rendered offscreen over the filter's region, the engine runs over those pixels, and the
    /// bitmap is drawn in its place. When there is no filter - or the region is too large to be worth allocating -
    /// this is the plain path, so an unfiltered document is untouched.
    /// </summary>
    private void PaintPath(DrawingContext context, PathItem path, double opacity, AffineTransform toWorld)
    {
        // A stroke's raster effects are pixel operations - a blur, a shadow, a glow - so the path is rendered
        // offscreen and the effects run over those pixels, the same route an SVG filter takes.
        if (RasterFiltersFor(path) is { Count: > 0 } raster &&
            PaintFilteredPath(context, path, opacity, raster, toWorld))
        {
            return;
        }

        if (_document?.FindFilter(path.FilterId) is { } filter &&
            PaintFilteredPath(context, path, opacity, new[] { filter }, toWorld))
        {
            return;
        }

        PaintPathDirect(context, path, opacity, toWorld);
    }

    /// <summary>The path as it is drawn without a filter.</summary>
    private bool PaintFilteredPath(
        DrawingContext context, PathItem path, double opacity, IReadOnlyList<FilterSpec> filters,
        AffineTransform toWorld)
    {
        if (_paintWorld is not { } world)
        {
            return false;
        }

        // The canvas transform's scale, which is how many device pixels one model unit takes. The filter is
        // measured in model units, so this is what turns a blur radius into a number of pixels.
        double scale = Math.Sqrt(Math.Abs((world.M11 * world.M22) - (world.M12 * world.M21)));
        if (scale <= 0.01)
        {
            return false;
        }

        // ...and the canvas is not the only thing scaling what is drawn: this item's own frame is pushed on
        // the context as well, so the raster has to be sampled that much more finely or it is stretched on
        // its way to the screen. `toWorld` is the frame the painter is drawing in, the same one the culling
        // above uses - `ToWorld`'s composition, asked once and used for both (#165).
        double frameScale = Math.Sqrt(Math.Abs(toWorld.Determinant));

        StreamGeometry geometry = GetGeometry(path);
        Rect bounds = geometry.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return false;
        }

        FilterRenderer.Result? result = FilterRenderer.Render(
            filters,
            bounds,
            world,
            scale,
            ctx => PaintPathDirect(ctx, path, opacity, toWorld),
            // SVG's `FillPaint` and `StrokePaint` are the shape painted in one of the two and not the other, which
            // the pixels of both together cannot be taken apart into - so the two passes are handed over separately
            // for the graph to read. `BackgroundImage` is not passed: this renderer draws one item at a time and has
            // no backdrop picture, so the engine names it as unsupplied rather than being handed an invented one.
            ctx => PaintPathFill(ctx, path, opacity),
            ctx => PaintPathStroke(ctx, path, opacity, toWorld),
            // The shape's own box, which is what an `objectBoundingBox` primitive length is a fraction of. It is the
            // geometry's box rather than the alpha's extent: SVG's bounding box is the shape's, and for a stroked
            // path the two differ by exactly the stroke width the blur would otherwise be measured against.
            bounds,
            frameScale);

        if (result is not { } painted)
        {
            return false;
        }

        context.DrawImage(painted.Bitmap, painted.Destination);
        return true;
    }

    /// <summary>
    /// The path drawn without a filter: its fill, then every stroke bottom to top.
    ///
    /// Kept as the two halves rather than one call because a filter graph may read the fill and the stroke as
    /// separate pictures, and because they are the two things SVG's `FillPaint` and `StrokePaint` mean.
    /// </summary>
    private void PaintPathDirect(DrawingContext context, PathItem path, double opacity, AffineTransform toWorld)
    {
        PaintPathFill(context, path, opacity);
        PaintPathStroke(context, path, opacity, toWorld);
    }

    /// <summary>The path's fill alone - what a graph reading `FillPaint` sees.</summary>
    private void PaintPathFill(DrawingContext context, PathItem path, double opacity)
    {
        if (!path.Fill.IsVisible)
        {
            return;
        }

        // PDF fills implicitly close open subpaths, so honour Fill.IsVisible
        // regardless of closure (imported content relies on this).
        PaintFill(context, path, GetGeometry(path), opacity);

        if (path.Fill.Hatch is { } hatch)
        {
            // A hatch **is** how this fill is painted, so it belongs to the fill's own picture rather than being a
            // separate source a graph could ask for.
            PaintHatch(context, path, hatch, opacity);
        }
    }

    /// <summary>The path's strokes alone - what a graph reading `StrokePaint` sees.</summary>
    private void PaintPathStroke(DrawingContext context, PathItem path, double opacity, AffineTransform toWorld)
    {
        if (!path.HasVisibleStroke)
        {
            return;
        }

        StreamGeometry geometry = GetGeometry(path);
        bool anyClosed = path.SubPaths.Any(sp => sp.IsClosed);

        // Keep hairlines visible. A 0.3pt stroke is 0.08 device pixels at 27% zoom, so
        // it faded to nothing and an imported pattern looked washed out next to a
        // reference render. Viewers hold thin strokes at roughly a pixel.
        const double MinDevicePixels = 0.75;

        Pen StrokePen(StrokeSpec stroke, double thickness)
        {
            var pen = new Pen(
                ToBrush(stroke.Color, opacity * stroke.EffectiveOpacity),
                thickness: Math.Max(0.01, thickness),
                lineCap: ToLineCap(stroke.Cap),
                lineJoin: ToLineJoin(stroke.Join),
                miterLimit: stroke.MiterLimit);

            if (!stroke.Dash.IsEmpty)
            {
                // Dash lengths are in the same user-space units as the stroke width,
                // so scale them by the same factor used for the (possibly group-
                // transformed) width.
                double factor = stroke.Width > 0 ? thickness / stroke.Width : 1.0;
                pen.DashStyle = new DashStyle(
                    stroke.Dash.Segments.Select(d => Math.Max(0.01, d * factor)).ToArray(),
                    stroke.Dash.Offset * factor);
            }

            return pen;
        }

        // **Every stroke, bottom to top.** Each one states its own width, colour, cap, join, miter limit and
        // dash, because a pen is built per stroke - so the canvas and the exported file agree about a stack
        // rather than each picking the stroke it happened to read.
        for (int strokeIndex = 0; strokeIndex < path.Strokes.Count; strokeIndex++)
        {
            StrokeSpec stroke = StrokeToPaint(path, strokeIndex, path.Strokes[strokeIndex]);
            if (!stroke.HasVisibleOutline)
            {
                continue;
            }

            // A pen has one width, so a stroke that varies along its length cannot be drawn with one. It is
            // drawn as the region it covers - the same outline the exporter fills, which is what keeps the
            // canvas and the file agreeing about what a profile looks like.
            //
            // The question is asked of the **plan** rather than of `HasWidthProfile`, because a stroke with an
            // outline effect and no profile is an outline too - that is what the effect means. Asking the narrower
            // question made the canvas draw an ordinary pen stroke while the exporter and the SVG writer filled the
            // effected outline: the same document, two different pictures, and the effect invisible on screen,
            // which is how it went unnoticed until a test compared the two renderers.
            StrokeRenderPlan plan = StrokeOutlineBuilder.Plan(path, stroke);
            if (plan.IsOutline)
            {
                PaintOutline(context, path, stroke, plan, opacity * stroke.EffectiveOpacity);
            }
            else
            {
                double width = Math.Max(
                    Math.Max(0.01, stroke.Width),
                    MinDevicePixels / Math.Max(_layout.Zoom, 1e-6));

                bool aligned = stroke.Alignment != StrokeAlignment.Center && anyClosed;
                if (!aligned)
                {
                    context.DrawGeometry(null, StrokePen(stroke, width), geometry);
                }
                else
                {
                    Avalonia.Media.Geometry clip = stroke.Alignment == StrokeAlignment.Inside
                        ? geometry
                        : BuildGeometry(path, outsideClip: true);
                    using (context.PushGeometryClip(clip))
                    {
                        context.DrawGeometry(null, StrokePen(stroke, width * 2), geometry);
                    }
                }
            }

            // **The art the brush maps, drawn over the stroke the pen laid down.** A plan is a width or an
            // outline and an art brush is neither, so the artwork is a second step and its own path here rather
            // than something the plan carries. It is resolved from the same shared seam the exporter uses, so
            // the two cannot disagree about where the art goes.
            PaintStrokeArt(context, path, stroke, opacity * stroke.EffectiveOpacity, toWorld);
        }
    }

    /// <summary>
    /// Draws the artwork a stroke's art brush maps along the path, once per placement.
    ///
    /// **Why the transform is pushed rather than baked.** An <see cref="ImageItem"/> holds an axis-aligned
    /// placement and two mirrors and **no rotation**, so a turned raster placement cannot be stated by the item
    /// and has to be applied by the renderer - which is what the pushed matrix below is. The same push is what
    /// puts a vector asset's frame onto the path.
    ///
    /// The pushed matrix is <c>T(-asset origin) ∘ placement</c>, because the item's own painters draw it at its
    /// artboard origin and the placement is stated against its own frame; the frame handed to the painter is the
    /// composed one, so its off-screen test measures where the art actually lands rather than where the asset
    /// sits in its own right.
    ///
    /// **A cycle is stopped rather than followed.** An art brush names an item by id, and that item may be a path
    /// whose own stroke carries an art brush - including, through a pair of brushes, one that leads back here.
    /// The depth cap ends that as a picture that stops nesting rather than as a stack overflow.
    ///
    /// **A pattern brush draws its tiles through here too.** Its tiles are artwork placed along the path in
    /// exactly the sense this method means, and `PlacedArt.Resolve` answers for both kinds, so one loop draws
    /// them both and the two renderers cannot come to different conclusions about where a tile sits.
    ///
    /// **A scatter brush's copies come through here as well.** They are artwork placed along the path with their own
    /// turn, size and opacity, and `PlacedArt.Resolve` answers for them too - so the scatter needs no third drawing
    /// loop, only the per-copy opacity multiplied into the paint.
    /// </summary>
    private void PaintStrokeArt(
        DrawingContext context, PathItem path, StrokeSpec stroke, double opacity, AffineTransform toWorld)
    {
        if (stroke.Brush is not { } brush || _document is null || _artDepth >= MaxArtDepth ||
            (!brush.IsArt && !brush.IsPattern && !brush.IsScatter))
        {
            return;
        }

        IReadOnlyList<PlacedArt> art = PlacedArt.Resolve(_document, path, brush, 1.0, stroke.Pen);
        if (art.Count == 0)
        {
            return;
        }

        _artDepth++;
        try
        {
            foreach (PlacedArt piece in art)
            {
                // The item's own frame is its artboard-local one: its painters add `ArtboardOffset()` to the
                // coordinates it stores, so the push takes that back off before the placement carries the frame
                // onto the path.
                Vector2D origin = piece.Asset.ArtboardOffset();
                AffineTransform ontoThePath = piece.Placement.Transform.Compose(
                    AffineTransform.CreateTranslation(-origin.X, -origin.Y));

                using (context.PushTransform(MatrixOf(ontoThePath)))
                {
                    // The piece's own opacity is multiplied in rather than replacing the stroke's: a scatter brush's
                    // copies each carry their own, and a copy at 40% on a stroke at 50% is a fifth-covered pixel.
                    PaintItem(context, piece.Asset, opacity * piece.Opacity, toWorld.Compose(ontoThePath));
                }
            }
        }
        finally
        {
            _artDepth--;
        }
    }

    /// <summary>An affine transform as the matrix Avalonia pushes, which is the same six numbers.</summary>
    private static Avalonia.Matrix MatrixOf(AffineTransform transform)
        => new(transform.A, transform.B, transform.C, transform.D, transform.E, transform.F);

    /// <summary>
    /// The stroke to draw: while a width-profile grip is being dragged, the profile the pointer is
    /// dragging **instead of** the one in the model.
    ///
    /// The preview is composed here for painting rather than written into the path, because the model
    /// is the session's to change and a live drag must not be a second place the state lives. The
    /// commit happens once, on release, through the operation - so the gesture is one undo step and
    /// nothing has to be put back if it ends where it began.
    /// </summary>
    private StrokeSpec StrokeToPaint(PathItem path, int index, StrokeSpec stroke)
        => _profileDragProfile is { } preview &&
           index == _profileDragStroke &&
           ReferenceEquals(path, WidthProfileTarget)
            ? stroke with { WidthProfile = preview }
            : stroke;

    /// <summary>
    /// Paints the region a stroke covers, one fill when every loop is the stroke's colour and one fill per loop
    /// when the plan carries a colour for each.
    ///
    /// **The loops come from the plan, not from a second call.** A bristle brush's loops are its bristles, and the
    /// colours are parallel to them, so re-deriving the geometry here to paint it would be reading a second
    /// computation of one answer - equal only because the sequence is deterministic. The one-path case still goes
    /// through <see cref="BuildProfileGeometry"/> for the reason that method gives: it is where the artboard's
    /// origin is added, and everything painted under <see cref="Render"/> is already inside the world transform.
    ///
    /// The fill rule is **nonzero**, which is the plan's own rule and what makes overlapping loops fill rather
    /// than cancel: two bristles crossing is ink on ink, not a hole.
    /// </summary>
    private void PaintOutline(
        DrawingContext context, PathItem path, StrokeSpec stroke, StrokeRenderPlan plan, double opacity)
    {
        if (plan.Paints is not { } paints)
        {
            context.DrawGeometry(ToBrush(stroke.Color, opacity), null, BuildProfileGeometry(path, stroke));
            return;
        }

        Vector2D origin = path.ArtboardOffset();
        for (int i = 0; i < plan.Outlines.Count; i++)
        {
            // A plan whose colours ran short would otherwise paint nothing: the fallback is the stroke's own
            // colour, which is what a loop with no paint of its own means.
            ColorRgb paint = i < paints.Count ? paints[i] : stroke.Color;
            context.DrawGeometry(ToBrush(paint, opacity), null, LoopGeometry(plan.Outlines[i], origin));
        }
    }

    /// <summary>One loop of a plan as geometry, with the artboard's origin added - which is the frame the canvas paints in.</summary>
    private static StreamGeometry LoopGeometry(IReadOnlyList<Point2D> loop, Vector2D origin)
    {
        var geometry = new StreamGeometry();
        using StreamGeometryContext g = geometry.Open();
        g.SetFillRule(MediaFillRule.NonZero);

        if (loop.Count > 0)
        {
            g.BeginFigure(new Point(loop[0].X + origin.X, loop[0].Y + origin.Y), isFilled: true);
            for (int i = 1; i < loop.Count; i++)
            {
                g.LineTo(new Point(loop[i].X + origin.X, loop[i].Y + origin.Y));
            }

            g.EndFigure(true);
        }

        return geometry;
    }

    /// <summary>
    /// The region a variable-width stroke covers, as geometry ready to draw.
    ///
    /// Built in the same coordinates <see cref="BuildGeometry"/> uses, **including the artboard offset**,
    /// because everything painted under <see cref="Render"/> is already inside the world transform: a loop
    /// built in path-local coordinates and drawn there would land offset by the artboard's origin, which looks
    /// like a profile that draws somewhere else on the page.
    ///
    /// The fill rule is **nonzero**, whatever the path's own fill rule is. That is a property of this outline
    /// rather than of the shape: the mitred corners of an offset outline overlap, and even-odd would punch holes
    /// in precisely the corners a mitre was used to keep full.
    /// </summary>
    internal static StreamGeometry BuildProfileGeometry(PathItem path, StrokeSpec stroke)
    {
        var geometry = new StreamGeometry();
        using StreamGeometryContext g = geometry.Open();
        g.SetFillRule(MediaFillRule.NonZero);

        foreach (IReadOnlyList<Point2D> loop in ProfileLoops(path, stroke))
        {
            g.BeginFigure(new Point(loop[0].X, loop[0].Y), isFilled: true);
            for (int i = 1; i < loop.Count; i++)
            {
                g.LineTo(new Point(loop[i].X, loop[i].Y));
            }

            g.EndFigure(true);
        }

        return geometry;
    }

    /// <summary>
    /// The region a variable-width stroke covers, in the coordinates the canvas paints in.
    ///
    /// Separated from <see cref="BuildProfileGeometry"/> so the arithmetic can be tested without a rendering
    /// platform: a `StreamGeometry` cannot even be opened without Avalonia initialised, and the part worth
    /// getting right is where the points end up rather than how they are put into a geometry.
    ///
    /// The artboard's origin is added here, because path coordinates are stored relative to it and everything
    /// painted under <see cref="Render"/> is already inside the world transform. An outline built in path-local
    /// coordinates would land offset by the artboard's origin, which looks like a profile that draws somewhere
    /// else on the page.
    /// </summary>
    internal static IReadOnlyList<IReadOnlyList<Point2D>> ProfileLoops(PathItem path, StrokeSpec stroke)
    {
        // The geometry comes from the shared builder rather than from this file: the canvas and the exporter
        // must agree about what a stroke covers, and two implementations of offsetting a path differ at exactly
        // the corners a person looks at when they compare the two.
        IReadOnlyList<IReadOnlyList<Point2D>> loops = StrokeOutlineBuilder.Outline(path, stroke);

        Vector2D offset = path.ArtboardOffset();
        if (offset.X == 0 && offset.Y == 0)
        {
            return loops;
        }

        return loops.Select(loop => (IReadOnlyList<Point2D>)loop
                .Select(p => new Point2D(p.X + offset.X, p.Y + offset.Y))
                .ToList())
            .ToList();
    }

    /// <summary>
    /// Fills a path, preferring its gradient when it has one.
    ///
    /// The gradient geometry is normalised to the object's bounding box, so it is mapped onto the
    /// world-space bounds of the very geometry being filled. A freeform gradient has no ramp to
    /// express, so it is sampled into a brush over the same box; anything that still cannot be
    /// painted falls back to <see cref="FillSpec.Color"/>, which the model keeps meaningful for
    /// exactly this reason, so a gradient fill is never a hole.
    /// </summary>
    private void PaintFill(DrawingContext context, PathItem path, StreamGeometry geometry, double opacity)
    {
        FillSpec fill = path.Fill;
        if (fill.Gradient is { } gradient)
        {
            if (gradient.Kind == GradientKind.Freeform)
            {
                // Drawn into the object's own outline rather than through a brush: the field is a
                // bitmap, and clipping it to the geometry is what makes it a fill of THIS shape.
                // The opacity is already in the bitmap's alpha.
                if (GradientPaint.CreateFreeformBitmap(
                        gradient, geometry.Bounds, path.ArtboardOffset(), opacity) is { } field)
                {
                    using (context.PushGeometryClip(geometry))
                    {
                        context.DrawImage(field, geometry.Bounds);
                    }

                    return;
                }
            }
            else if (GradientPaint.CreateBrush(gradient, geometry.Bounds, opacity) is { } brush)
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
            if (image.MirrorX || image.MirrorY)
            {
                // Mirrored about the placement's own centre. The samples are not touched, which is
                // what keeps the flip lossless and undoable, and what makes flipping twice exactly
                // the identity.
                Point2D centre = new(bounds.X + (bounds.Width / 2), bounds.Y + (bounds.Height / 2));
                Avalonia.Matrix mirror = Avalonia.Matrix.CreateTranslation(-centre.X, -centre.Y)
                    * Avalonia.Matrix.CreateScale(image.MirrorX ? -1 : 1, image.MirrorY ? -1 : 1)
                    * Avalonia.Matrix.CreateTranslation(centre.X, centre.Y);
                using (context.PushTransform(mirror))
                {
                    context.DrawImage(ImageRenderer.BitmapFor(image), destination);
                }
            }
            else
            {
                context.DrawImage(ImageRenderer.BitmapFor(image), destination);
            }
        }
    }

    private void PaintText(DrawingContext context, TextItem text, double opacity)
    {
        Vector2D offset = text.ArtboardOffset();
        TextMetrics metrics = MeasureText(text);

        // The block's own space, mapped to the page: mirror first, then turn. The mirror is on the
        // context rather than baked into the glyphs, so the runs keep their text and their glyph
        // ids and a flipped block is still re-editable.
        bool mirrored = text.MirrorX || text.MirrorY;
        bool turned = Math.Abs(text.RotationRadians) > 1e-9;
        Avalonia.Matrix? blockSpace = null;
        if (mirrored || turned)
        {
            Point2D o = text.Origin + offset;
            Avalonia.Matrix m = Avalonia.Matrix.CreateTranslation(-o.X, -o.Y);
            if (mirrored)
            {
                m *= Avalonia.Matrix.CreateScale(text.XSign, text.YSign);
            }

            if (turned)
            {
                m *= Avalonia.Matrix.CreateRotation(text.RotationRadians);
            }

            blockSpace = m * Avalonia.Matrix.CreateTranslation(o.X, o.Y);
        }

        using IDisposable? pushed = blockSpace is { } block ? context.PushTransform(block) : null;

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
                // `metrics.Y[i]` is the TOP of the line, which is where the glyphs start: the run
                // is drawn from there. Subtracting the ascent as well lifted the highlight a
                // three-quarter of an em clear of the text it was supposed to cover.
                Point2D p0 = text.Origin + offset + new Vector2D(
                    metrics.X[i],
                    metrics.Y[i]);
                double w = metrics.Y[i + 1] == metrics.Y[i]
                    ? metrics.X[i + 1] - metrics.X[i]
                    : metrics.Size[i] * 0.3;

                // The line's own height, so the highlight covers the whole line box - which is what
                // a caret the height of the line is marking.
                double h = metrics.Ascent[i] + metrics.Descent[i];

                // One rectangle, in the block's own upright space. The rotation is already on the
                // context (pushed above), so turning the quad here as well - and then converting it
                // through ModelToScreen on top - drew the highlight at the screen position of the
                // model position: twice transformed, and nowhere near the characters it covers.
                context.FillRectangle(selBrush, new Rect(p0.X, p0.Y, Math.Max(0.5, w), h));
            }
        }

        // Every run is drawn where the layout says, on its line's baseline. The layout is the same
        // one the caret, the highlight and the model's bounds are computed from, so a line of mixed
        // faces is drawn on the one baseline it was measured against.
        //
        // Runs used to be placed by stacking their boxes under the line's top edge, each carrying
        // its own ascent, which is what left a line of mixed faces stair-stepping: the taller face
        // sat lower, and neither sat on a shared baseline.
        var runOffset = new int[text.Runs.Count];
        int flat = 0;
        for (int i = 0; i < text.Runs.Count; i++)
        {
            runOffset[i] = flat;
            flat += text.Runs[i].Text.Length;
        }

        foreach (TextRunBox box in metrics.Layout.Runs)
        {
            TextRun run = text.Runs[box.Run];
            TextLine line = metrics.Layout.Lines[box.Line];
            int pieceStart = box.Start - runOffset[box.Run];

            // **The run's own colour, through the one member that answers it.** A block may hold several
            // colours - what SVG states with a `<tspan fill=...>` and a PDF content stream states by setting
            // a colour partway through a text object - and this brush used to be taken once, before the loop,
            // from the block's colour, so every run of a multi-coloured line drew in the block's. The export
            // takes the same answer from `ColourOf`, which is what keeps the canvas and the page agreeing
            // about the same text.
            IBrush brush = ToBrush(text.ColourOf(run), opacity);

            // Placed by its baseline, never by the line's top edge.
            Point2D origin = text.Origin + offset
                + new Vector2D(box.X, TextLayoutEngine.RunTop(run, line));

            // **Tracking is advance, never scale.** A run that asks for letter or word spacing keeps
            // the face's own letterforms and moves the pen further between glyphs, so it is drawn one
            // glyph at a time - the only way a run-level draw can put room *between* characters, since
            // `FormattedText` carries no per-character advance. `TextRun.Advances` (and, through it,
            // `TextWrapping.Flatten`) already states those pen positions, so nothing is invented here.
            //
            // Sizing this run by `box.Width / formatted.Width` instead - which is right for the
            // substitute-face case below and was applied to every run - stretched it by the tracking
            // ratio: `FormattedText.Width` is the face's own advance with no tracking in it, so the
            // glyphs came out wider than the face rather than further apart. The model, the caret, the
            // selection highlight and the exported PDF all said otherwise, which is the one thing this
            // canvas must never do.
            if (run.LetterSpacing != 0 || run.WordSpacing != 0)
            {
                DrawTrackedSegment(context, brush, run, box, pieceStart, origin);
                continue;
            }

            // A run broken across lines - by a newline in its own text, or by wrapping in a frame -
            // is drawn once per line, each piece on its own line's baseline. Drawing it whole
            // stacked the pieces or ran them past the line they belong to.
            var segment = (TextRun)run.Clone();
            segment.Text = run.Text.Substring(pieceStart, box.Length);
            segment.AdvanceWidth = null;

            FormattedText formatted = CreateFormattedText(segment, brush);
            double natural = formatted.Width;
            double scaleX = natural > 0 ? box.Width / natural : 1.0;

            if (run.EmbeddedFont is { } embedded && run.GlyphIds is { Length: > 0 } glyphIds &&
                pieceStart >= 0 && pieceStart + box.Length <= glyphIds.Length)
            {
                var slice = new ushort[box.Length];
                Array.Copy(glyphIds, pieceStart, slice, 0, box.Length);
                if (TryDrawEmbeddedGlyphs(context, brush, text, segment, embedded, slice, origin))
                {
                    continue;
                }
            }

            if (Math.Abs(scaleX - 1.0) > 1e-9)
            {
                // Squeeze/stretch to the advance the layout placed this piece at, so a wider
                // fallback font does not reflow or overprint the layout.
                using (context.PushTransform(ScaleAbout(new Point(origin.X, origin.Y), scaleX)))
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

    /// <summary>
    /// Draws one segment of a run that carries letter or word spacing, glyph by glyph.
    ///
    /// Each glyph is drawn at the pen origin the model computes for it - the sum of the run's own
    /// advances, tracking included (<see cref="TextRun.Advances"/>) - so the drawn glyphs land on the
    /// caret, the selection highlight and the model's bounds by construction, and the drawn span is the
    /// face's own advance plus the tracking rather than the face stretched by it.
    ///
    /// Only a recorded advance that the drawn face does not have widens a glyph, exactly as in the
    /// untracked path: <see cref="TextRun.AdvanceWidth"/> is the file's statement of how far the run
    /// goes, and a substitute face is squeezed or stretched to meet it. Tracking never does that - it is
    /// added to the pen, not to the outline.
    ///
    /// Drawing a glyph at a time costs one <see cref="FormattedText"/> per character, which is why the
    /// untracked run above keeps the single run-level draw: spacing is rare and a long paragraph of it
    /// would otherwise be shaped one character at a time. It is also why the per-character positions
    /// come from the model instead of being re-derived from the face here.
    /// </summary>
    private static void DrawTrackedSegment(
        DrawingContext context, IBrush brush, TextRun run, TextRunBox box, int pieceStart, Point2D origin)
    {
        IReadOnlyList<double> advances = run.Advances();

        double modelled = 0;
        foreach (double advance in advances)
        {
            modelled += advance;
        }

        // `TextWrapping.Flatten` spreads a recorded advance proportionally over the run's tracked
        // advances, so a substituted face still ends where the file said. The same ratio is taken here,
        // for the same reason and by the same formula - not by dividing the layout box by the face.
        double advanceScale = run.AdvanceWidth is > 0 && modelled > 0
            ? run.AdvanceWidth.Value / modelled
            : 1.0;

        // A run that carries its own programme is still drawn from it, exactly as the untracked path
        // draws it. The glyph ids are only trusted when every one of them belongs to that programme.
        EmbeddedFont? program = null;
        IGlyphTypeface? typeface = null;
        ushort[]? ids = null;
        if (run.EmbeddedFont is { } embedded && run.GlyphIds is { } glyphIds &&
            pieceStart >= 0 && pieceStart + box.Length <= glyphIds.Length &&
            EmbeddedFontManager.TryGetEmbeddedGlyphTypeface(embedded.FamilyName, out IGlyphTypeface resolved) &&
            GlyphIdsBelongTo(resolved, glyphIds, pieceStart, box.Length))
        {
            program = embedded;
            typeface = resolved;
            ids = glyphIds;
        }

        double pen = 0;
        for (int i = 0; i < box.Length; i++)
        {
            int index = pieceStart + i;
            DrawOneGlyph(context, brush, run, index, new Point(origin.X + pen, origin.Y),
                advanceScale, program, typeface, ids);

            pen += advanceScale * (index < advances.Count ? advances[index] : TextMeasurement.AdvanceAtEnd(run));
        }
    }

    /// <summary>
    /// One character of a tracked run, at the pen origin the model gives it: from the run's imported
    /// programme when it has one, otherwise shaped from the resolved face.
    ///
    /// A horizontal scale is applied only for <see cref="TextRun.AdvanceWidth"/> - a substitute face
    /// meeting a recorded advance - and it is about the glyph's own origin, so the pen position the
    /// model states is not moved by it.
    /// </summary>
    private static void DrawOneGlyph(DrawingContext context, IBrush brush, TextRun run, int index,
        Point at, double advanceScale, EmbeddedFont? program, IGlyphTypeface? typeface, ushort[]? glyphIds)
    {
        if (program is not null && typeface is not null && glyphIds is not null)
        {
            DrawEmbeddedGlyph(context, brush, run, program, typeface, glyphIds[index], index, at);
            return;
        }

        var single = (TextRun)run.Clone();
        single.Text = run.Text.Substring(index, 1);
        single.AdvanceWidth = null;
        FormattedText glyph = CreateFormattedText(single, brush);

        if (Math.Abs(advanceScale - 1.0) > 1e-9)
        {
            using (context.PushTransform(ScaleAbout(at, advanceScale)))
            {
                context.DrawText(glyph, at);
            }
        }
        else
        {
            context.DrawText(glyph, at);
        }
    }

    /// <summary>A horizontal scale about a point, which leaves that point where it is.</summary>
    private static Avalonia.Matrix ScaleAbout(Point at, double scaleX)
        => Avalonia.Matrix.CreateTranslation(-at.X, -at.Y)
           * Avalonia.Matrix.CreateScale(scaleX, 1.0)
           * Avalonia.Matrix.CreateTranslation(at.X, at.Y);

    /// <summary>
    /// One glyph of a run drawn from its imported programme, on the baseline that programme asks for.
    ///
    /// The untracked path draws a whole piece as one <see cref="GlyphRun"/>; a tracked run is drawn a
    /// glyph at a time, because the room between glyphs is a pen position and a single
    /// <see cref="GlyphRun"/> takes its advances from the programme.
    /// </summary>
    private static void DrawEmbeddedGlyph(DrawingContext context, IBrush brush, TextRun run,
        EmbeddedFont embedded, IGlyphTypeface glyphTypeface, ushort glyphId, int characterIndex, Point at)
    {
        double ascent = embedded.Ascent > 0
            ? embedded.Ascent / 1000.0
            : VCCad.Core.Text.TextMeasurement.TypicalAscentEm;

        var baseline = new Point(at.X, at.Y + (ascent * run.FontSize));
        var glyphRun = new GlyphRun(glyphTypeface, run.FontSize,
            run.Text.AsMemory(characterIndex, 1), new[] { glyphId }, baseline, 0);

        context.DrawGlyphRun(brush, glyphRun);
    }

    /// <summary>Whether every glyph id of a piece belongs to the programme it came with.</summary>
    private static bool GlyphIdsBelongTo(IGlyphTypeface glyphTypeface, ushort[] glyphIds, int start, int length)
    {
        for (int i = start; i < start + length; i++)
        {
            if (glyphIds[i] >= glyphTypeface.GlyphCount)
            {
                return false;
            }
        }

        return true;
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
        if (!GlyphIdsBelongTo(glyphTypeface, glyphIds, 0, glyphIds.Length))
        {
            return false;
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

    /// <summary>
    /// The block's layout, indexed the way the editing code wants it.
    ///
    /// It holds no arithmetic of its own: the caret, the highlight, the edit box and the drawing
    /// all read the one <see cref="TextLayout"/> the model computes. They used to be three separate
    /// measurements of the same block - the model measured lines one way, the canvas drew them
    /// another and the caret came out of a third - which is how a line of mixed faces came to be
    /// drawn on three different baselines.
    /// </summary>
    private sealed class TextMetrics
    {
        public TextLayout Layout { get; init; } = TextLayout.Empty;

        /// <summary>The caret's x, per flattened character index.</summary>
        public double[] X = Array.Empty<double>();

        /// <summary>The top of the line that character sits on.</summary>
        public double[] Y = Array.Empty<double>();

        /// <summary>The baseline the character's line draws to.</summary>
        public double[] Baseline = Array.Empty<double>();

        /// <summary>The line's ascent above that baseline, leading included.</summary>
        public double[] Ascent = Array.Empty<double>();

        /// <summary>The line's descent below it.</summary>
        public double[] Descent = Array.Empty<double>();

        /// <summary>The face size of the character, for the caret's own width.</summary>
        public double[] Size = Array.Empty<double>();

        public double MaxWidth;
        public double TotalHeight;
    }

    private static TextMetrics MeasureText(TextItem text)
    {
        TextLayout layout = TextLayoutEngine.Compute(text);
        int n = layout.CaretX.Count;

        var m = new TextMetrics
        {
            Layout = layout,
            MaxWidth = layout.Width,
            TotalHeight = layout.Height,
            X = new double[n],
            Y = new double[n],
            Baseline = new double[n],
            Ascent = new double[n],
            Descent = new double[n],
            Size = new double[n],
        };

        for (int i = 0; i < n; i++)
        {
            TextLine line = layout.Lines[layout.LineOf(i)];
            m.X[i] = layout.XOf(i);
            m.Y[i] = line.Top;
            m.Baseline[i] = line.Baseline;
            m.Ascent[i] = line.Ascent;
            m.Descent[i] = line.Descent;
            m.Size[i] = SizeAt(text, i);
        }

        return m;
    }

    /// <summary>The face size of a flattened character.</summary>
    private static double SizeAt(TextItem text, int index)
    {
        int remaining = index;
        foreach (TextRun run in text.Runs)
        {
            if (remaining < run.Text.Length)
            {
                return run.FontSize;
            }

            remaining -= run.Text.Length;
        }

        return text.MaxFontSize;
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
            // The trace comes first so the box and its handles sit on top of it. It does NOT
            // depend on the box having extent: a horizontal or vertical line has a zero-height
            // or zero-width box and is still selected, and gating the trace on that box left
            // such a line with no feedback at all. Only the box and its handles need a
            // rectangle to sit on.
            PaintSelectionOutlines(context);

            Rect2D selection = ChromeRect();
            if (!selection.IsEmpty)
            {
                PaintSelectChrome(context);
            }

            // The gradient annotators sit on top of the chrome: they are what the person drags to
            // place the ramp, and the box's own handles are underneath them.
            if (GradientTargetPath() is { } gradientPath)
            {
                PaintGradientAnnotators(context, gradientPath);
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

        // The width-profile mode's handles go over whatever the tool drew: while the mode is on they
        // are what the pointer is aimed at, and on a fat stroke they sit outside the selection box.
        if (IsEditingWidthProfile)
        {
            PaintWidthProfileAnnotators(context);
        }

        // A selected shape's control points, drawn where their parameters live.
        PaintShapeHandles(context);

        // The stroke being drawn, exactly as captured - no fitting while the pointer is down.
        if (_pencilPoints.Count > 1)
        {
            PaintPencilPreview(context);
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

    /// <summary>
    /// Node tool chrome: anchors and handles only — no bounding rectangle.
    ///
    /// The nodes are stored in the path's own placement frame and the overlay is drawn outside every
    /// group's pushed transform, so each point is carried out by <see cref="SelectionEngine.ToWorld"/>
    /// first. Drawing them at their stored coordinates put the handles somewhere the artwork is not, and
    /// therefore somewhere the pointer could not grab them - the picture and the hit test have to agree
    /// about the frame as surely as the edit does (#173).
    /// </summary>
    private void PaintNodeChrome(DrawingContext context, PathItem path)
    {
        IBrush accent = new SolidColorBrush(Color.FromRgb(0x4C, 0x9A, 0xFF));
        var pen = new Pen(accent, 1.4);
        double half = 4.0;

        AffineTransform toWorld = SelectionEngine.ToWorld(path);
        Point Map(Point2D stored) => ModelToScreen(toWorld.Transform(stored));

        foreach (SubPath sub in path.SubPaths)
        {
            foreach (PathNode node in sub.Nodes)
            {
                Point anchor = Map(node.Anchor);
                if (!node.HasStraightIncoming)
                {
                    DrawHandleLine(context, anchor, Map(node.InHandle));
                }

                if (!node.HasStraightOutgoing)
                {
                    DrawHandleLine(context, anchor, Map(node.OutHandle));
                }
            }

            foreach (PathNode node in sub.Nodes)
            {
                Point anchor = Map(node.Anchor);
                if (!node.HasStraightIncoming)
                {
                    context.DrawEllipse(Brushes.White, pen, Map(node.InHandle), half, half);
                }

                if (!node.HasStraightOutgoing)
                {
                    context.DrawEllipse(Brushes.White, pen, Map(node.OutHandle), half, half);
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

        // Stored coordinates, carried out through the one composition, exactly as PaintNodeChrome does.
        AffineTransform toWorld = SelectionEngine.ToWorld(_nodePath);
        Point Map(Point2D stored) => ModelToScreen(toWorld.Transform(stored));

        PathNode node = _nodeSub.Nodes[_nodeIndex];
        Point2D dragged = _nodeIsIn ? node.InHandle : node.OutHandle;
        Point anchor = Map(_nodeAnchorStart);
        Point handle = Map(dragged);

        IBrush accent = new SolidColorBrush(Color.FromRgb(0x4C, 0x9A, 0xFF));

        // Faint line from the anchor to the (future) mirrored position.
        context.DrawLine(new Pen(accent, 1.0) { DashStyle = new DashStyle(new[] { 3.0, 3.0 }, 0) },
            anchor, Map(_handleSnapPos));

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
            double overlayWidth = path.HasVisibleStroke
                ? Math.Max(1.0, WidestStroke(path) * _layout.Zoom * 0.9)
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

    /// <summary>
    /// What a double-click at a point opens: a text block for typing, or a path for node editing.
    ///
    /// Public because a person can do it and the assistant must be able to as well, and because the
    /// pointer handler should not own a rule the registry cannot reach.
    /// </summary>
    public EditTarget EditAt(Point2D model)
    {
        if (_vm is null)
        {
            return EditTarget.None;
        }

        switch (HitTestTopItem(model))
        {
            case TextItem text:
                // Opening at the point is the whole of "double-click the word you mean": the caret
                // goes where the click landed rather than to the end of the block.
                EnterTextEdit(text, model);
                return EditTarget.Text;

            // Only from the select tool. In the node tool a double-click is already a node
            // gesture, and in the pen tool it is how a path is finished.
            case PathItem path when _vm.Tool == EditorTool.Select:
                _vm.SelectObject(path);
                _vm.Tool = EditorTool.Node;
                return EditTarget.Path;

            default:
                return EditTarget.None;
        }
    }

    private void EnterTextEdit(TextItem text, Point2D? caretAt = null)
    {
        _editingText = text;
        _editBefore = (TextItem)text.Clone();
        // A click that opened the block puts the caret on the character nearest it, rather than at
        // the end: the second click of a double-click was aimed at a word.
        _caret = caretAt is { } point
            ? IndexAtLocal(text, ToTextLocal(text, point))
            : TextEditing.Length(text);
        _editAnchor = _caret;

        // The caret is published **before** the edit is announced. Opening the block is what makes the type
        // toolbar and the text panel re-read the caret, so announcing first left both describing the run of the
        // previous caret until something else happened to re-sync them - and the run the caret is actually in is
        // the one thing this toolbar exists to show (issue #157).
        UpdateCaretInfo();

        _vm!.SelectObject(text);
        _vm.IsEditingText = true;

        // Publish the target so operations that style text can reach the block even after
        // the selection moves on (picking a font, clicking away, switching document).
        _vm.EditingText = text;
        _caretOn = true;
        UpdateCaretBlink();
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

    /// <summary>
    /// Adopts the font at the caret, so the text controls and the next keystroke agree with the block being
    /// edited rather than with whatever new text is set to. Called when a block is opened and whenever the
    /// caret moves: the controls follow the caret, not the block, because a block can hold several faces.
    /// </summary>
    private void AdoptFontAtCaret()
    {
        if (_vm is null || _editingText is null)
        {
            return;
        }

        if (TextEditing.FontAt(_editingText, _caret) is not { } font)
        {
            // An empty block has nothing to adopt, and adopting a default here would throw away the face the
            // person chose before they started typing.
            return;
        }

        if (!string.IsNullOrWhiteSpace(font.Family))
        {
            _vm.DefaultFontFamily = font.Family;
        }

        if (font.Size > 0)
        {
            _vm.DefaultFontSize = font.Size;
        }
    }

    private void UpdateCaretInfo()
    {
        AdoptFontAtCaret();
        if (_vm is null || _editingText is null)
        {
            return;
        }

        // Both coordinates of the one caret: the run index the panel names and the character offset the text
        // helpers take. They are published together so a reader never has to convert - telling the two apart at
        // a call site is exactly what the type toolbar got wrong (issue #157).
        //
        // The index comes from **`RunAt`**, the single place that answers "which run is the caret in", rather
        // than from `Locate`, which uses a different rule at a run boundary: `RunAt` says the character before
        // the caret decides (the run the next character joins) while `Locate` gives the run the caret is drawn
        // at. Two rules meant the panel and the toolbar could name different runs for one caret - the
        // disagreement this issue is about, one boundary position away from the reported case.
        _vm.TextCaretOffset = _caret;
        TextRun? at = TextEditing.RunAt(_editingText, _caret);
        _vm.TextCaretRunIndex = at is null ? 0 : Math.Max(0, _editingText.Runs.IndexOf(at));

        // The caret's run is also the run the face fields describe, and that is **shared state** now: publishing it
        // here is what keeps the panel following the caret while a person types, while a driver can still name a run
        // through `text.inspectRun` and have the panel describe it. Holding the index in one place is what stops the
        // panel and a driver describing different runs.
        _vm.InspectedRun = _vm.TextCaretRunIndex;

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

    /// <summary>
    /// Styles the selected range of the block being edited, as the Text pane and Ctrl+B do.
    ///
    /// Public because a person can do it and the assistant must be able to as well: without it a
    /// driver can read a block's runs and restyle the whole object, but cannot change part of one -
    /// and changing part of one is the case that tests the layout.
    /// </summary>
    public bool StyleSelection(Action<TextRun> style)
    {
        if (_editingText is null || Selection() is var (start2, end2) && start2 == end2)
        {
            return false;
        }

        ApplyStyleToSelection(style);
        return true;
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
    /// The grab handles on the edit box, in the block's own coordinates: the right edge at three
    /// heights, the left edge's middle, and a rotation handle above the origin.
    ///
    /// There is deliberately no vertical handle. The block's height is whatever its lines need, so a
    /// handle that pretended to set it would be a lie; the width is what the text wraps in and the
    /// rotation is what turns the block. Those are the two things about the box a person can change
    /// while the words stay where they are - and both are reachable while editing, because the text
    /// shrinks to fit the box as it is typed, so the moment the box needs fixing is the moment the
    /// person is in it.
    /// </summary>
    private static Point2D[] EditBoxHandles(TextItem text, TextMetrics metrics)
    {
        double w = text.FrameWidth > 0 ? text.FrameWidth : Math.Max(metrics.MaxWidth, 4);
        double h = Math.Max(metrics.TotalHeight, text.MaxFontSize * text.LineSpacing);
        double mid = h / 2;

        // Right edge (three), the left edge's middle, then rotation above the origin.
        return new[]
        {
            new Point2D(w, 0), new Point2D(w, mid), new Point2D(w, h), new Point2D(0, mid),
            new Point2D(0, -RotateHandleOffset),
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

    /// <summary>Applies a handle drag: the frame width, or the block's rotation.</summary>
    private void DragEditBoxHandle(Point2D world)
    {
        if (_editingText is not { } text)
        {
            return;
        }

        if (_frameResizeHandle == EditRotateHandle)
        {
            // Turn the block so its rotation handle points at the pointer. The handle sits directly
            // above the origin, so the rotation is the pointer's bearing from the origin, less the
            // quarter turn that "above" is from "right".
            Point2D origin = TextLocalToWorld(text, new Point2D(0, 0));
            var bearing = new Vector2D(world.X - origin.X, world.Y - origin.Y);
            if (Math.Sqrt((bearing.X * bearing.X) + (bearing.Y * bearing.Y)) < 1e-6)
            {
                return;
            }

            text.RotationRadians = Math.Atan2(bearing.Y, bearing.X) + (Math.PI / 2);
            AfterTextEdit();
            InvalidateVisual();
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

    /// <summary>
    /// The cursor the point should show.
    ///
    /// A handle wins over "this is text": the whole complaint this answers is that hovering a handle
    /// while editing kept showing the text caret, so nothing said the handle could be grabbed - and a
    /// control nobody knows is there is not a control.
    /// </summary>
    private Cursor CursorFor(Point2D model) => new(CursorKindFor(model));

    /// <summary>
    /// What the pointer should look like at a model point.
    ///
    /// Exposed so it can be asserted: Avalonia's <see cref="Cursor"/> does not say which kind it is, so
    /// a test that could only inspect the cursor object would be able to say "something changed" and
    /// nothing else.
    /// </summary>
    internal StandardCursorType CursorKindFor(Point2D model)
    {
        if (_editingText is { } editing)
        {
            int handle = HandleAt(editing, model);
            return handle >= 0
                ? HandleCursorKind(editing, handle)
                : StandardCursorType.Ibeam;
        }

        return HitTestTopItem(model) is TextItem
            ? StandardCursorType.Ibeam
            : StandardCursorType.Arrow;
    }

    /// <summary>
    /// The cursor a handle should show. The resize direction follows the block rather than the screen,
    /// so a box turned a quarter turn resizes up-and-down on screen when a width handle is pulled, and
    /// the cursor says so instead of pointing the wrong way.
    /// </summary>
    private static StandardCursorType HandleCursorKind(TextItem text, int handle)
    {
        if (handle == EditRotateHandle)
        {
            return StandardCursorType.Hand;
        }

        double degrees = text.RotationRadians * 180.0 / Math.PI;
        double folded = ((degrees % 180) + 180) % 180;
        bool alongTheBlock = folded < 45 || folded > 135;

        return alongTheBlock
            ? StandardCursorType.SizeWestEast
            : StandardCursorType.SizeNorthSouth;
    }

    /// <summary>
    /// The caret's index in the text being edited. Exposed for tests: "the box moved and the caret
    /// stayed with the character it was in" is the thing a box edit must not break, and it cannot be
    /// asserted by looking at a screenshot.
    /// </summary>
    internal int CaretIndexForTests => _caret;
    /// <summary>Where the edit box's handles are, in model space - for tests and for hit-testing.</summary>
    internal IReadOnlyList<Point2D> EditBoxHandlesWorld(TextItem text)
    {
        Point2D[] local = EditBoxHandles(text, MeasureText(text));
        return local.Select(handle => TextLocalToWorld(text, handle)).ToArray();
    }

    /// <summary>Index of the rotation handle in <see cref="EditBoxHandles"/>.</summary>
    private const int EditRotateHandle = 4;

    /// <summary>How far above the origin the rotation handle sits, in the block's own units.</summary>
    private const double RotateHandleOffset = 22;

    /// <summary>
    /// The two ends of the caret, in screen coordinates, or null when no block is being edited.
    ///
    /// The caret runs down the block's own vertical axis - from the top of the line to its foot -
    /// so a turned block turns the caret with it. Exposing the line is how that gets checked
    /// without pixels, which the blink would make flaky: it draws nothing half the time.
    /// </summary>
    public (Point Top, Point Bottom)? CaretLine()
    {
        if (_editingText is null)
        {
            return null;
        }

        TextMetrics metrics = MeasureText(_editingText);
        int index = Math.Clamp(_caret, 0, metrics.X.Length - 1);

        // `metrics.Y[i]` is the TOP of the line, which is where the glyphs start. It used to have
        // the ascent subtracted from it as well, which drew the caret three quarters of an em clear
        // of the text - and, because the line was also nailed to the screen's vertical, at right
        // angles to a turned block.
        //
        // The caret marks the whole LINE box, so it brackets whatever face the line carries: a 12pt
        // word beside a 36pt one gets a caret the height of the line, not of either word.
        Point2D local = new(metrics.X[index], metrics.Y[index]);
        double height = metrics.Ascent[index] + metrics.Descent[index];

        return (
            ModelToScreen(TextLocalToWorld(_editingText, local)),
            ModelToScreen(TextLocalToWorld(
                _editingText, new Point2D(local.X, local.Y + height))));
    }

    private void PaintTextCaret(DrawingContext context)
    {
        if (CaretLine() is not { } caret)
        {
            return;
        }

        // Dark, because the page is white: a white caret on white paper is no caret.
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x10)), 1.5);
        context.DrawLine(pen, caret.Top, caret.Bottom);
    }

    /// <summary>
    /// A world point in a text block's own coordinates — the space its metrics are laid
    /// out in. A rotated block has to be turned back before it can be hit-tested, or a
    /// click lands somewhere else entirely and the caret jumps.
    /// </summary>
    private static Point2D ToTextLocal(TextItem text, Point2D world)
    {
        Point2D point = world - text.ArtboardOffset();
        double dx = point.X - text.Origin.X;
        double dy = point.Y - text.Origin.Y;

        if (Math.Abs(text.RotationRadians) > 1e-9)
        {
            double cos = Math.Cos(-text.RotationRadians);
            double sin = Math.Sin(-text.RotationRadians);
            return new Point2D((dx * cos - dy * sin) * text.XSign, (dx * sin + dy * cos) * text.YSign);
        }

        // Back into the block's own space, mirror undone: the layout, the caret and the hit tests
        // all work in the space the text is set in, so a mirrored block needs no special cases
        // beyond this mapping. Without it a click on a flipped block lands on the mirrored
        // character and the caret jumps to the wrong end.
        return new Point2D(dx * text.XSign, dy * text.YSign);
    }

    /// <summary>Whether a world point falls inside a text block, rotation included.</summary>
    private static bool TextContains(TextItem text, Point2D world)
    {
        // `local` has been turned back into the block's own upright space, so it is the upright
        // extent that answers this. Measuring it against the rotated bounds put the two in
        // different frames: on a turned label every click inside the text read as a miss, and the
        // caret went to the end of the block instead of the word under the pointer.
        Point2D local = ToTextLocal(text, world);
        Rect2D box = text.LocalBounds();
        return local.X >= -1 && local.X <= box.Width + 1 &&
               local.Y >= -1 && local.Y <= box.Height + 1;
    }

    /// <summary>A world point to a screen point, through a text block's own space.</summary>
    private Point2D TextLocalToWorld(TextItem text, Point2D local)
    {
        // Mirrored in the block's own space, before the rotation: a flipped block that is also
        // turned is the flip of the upright block, turned.
        double x = text.Origin.X + (local.X * text.XSign);
        double y = text.Origin.Y + (local.Y * text.YSign);

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

        // Tool keys switch the tool and nothing else. Leaving the pen no longer ends the path it
        // was drawing: that is what closing it or Escape is for, so a look at the node tool and a
        // return carries on from the last point.
        //
        // A tool key also leaves the width-profile mode, which is not a tool: while it is on the canvas
        // ignores clicks that miss a handle, and a mode that kept the pointer while the toolbar said
        // "pen" would make the pen look broken.
        if (IsEditingWidthProfile && IsToolKey(e.Key))
        {
            EditWidthProfile(null);
        }

        switch (e.Key)
        {
            case Key.W:
                // Illustrator's width tool, on the W key: the selected stroke's profile opens on the
                // canvas, which is what profile.editMode does - one mode, two ways in.
                ToggleWidthProfileEdit();
                e.Handled = true;
                break;

            case Key.V:
                _vm.Tool = EditorTool.Select;
                e.Handled = true;
                break;

            case Key.A:
                // A toggle, not a jump: the same key that leaves the pen brings it back.
                _vm.ToggleTool(EditorTool.Node);
                e.Handled = true;
                break;

            case Key.P:
                _vm.Tool = EditorTool.Pen;
                e.Handled = true;
                break;

            case Key.T:
                _vm.Tool = EditorTool.Text;
                e.Handled = true;
                break;

            case Key.O:
                _vm.Tool = EditorTool.Artboard;
                e.Handled = true;
                break;

            case Key.M:
                _vm.Tool = EditorTool.Rectangle;
                e.Handled = true;
                break;

            case Key.L:
                _vm.Tool = EditorTool.Ellipse;
                e.Handled = true;
                break;

            // Q was in the lasso's tooltip from the day it was added and never bound, which is worse than
            // no shortcut: a person presses it, nothing happens, and they stop believing the tooltips. The
            // tools added since are bound here too, and every one of them is named in its own tip.
            case Key.Q:
                _vm.Tool = EditorTool.Lasso;
                e.Handled = true;
                break;

            case Key.C:
                _vm.Tool = EditorTool.Corner;
                e.Handled = true;
                break;

            case Key.N:
                _vm.Tool = EditorTool.Pencil;
                e.Handled = true;
                break;

            case Key.S:
                _vm.Tool = EditorTool.Shape;
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
                if (IsEditingWidthProfile)
                {
                    // The promise this mode makes: it never traps the pointer, so Escape always leaves.
                    EditWidthProfile(null);
                }
                else if (_vm.Tool == EditorTool.Artboard)
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
