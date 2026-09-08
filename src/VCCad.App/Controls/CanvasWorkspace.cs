using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using VCCad.Core.Model;
using VCCad.Core.Viewport;
using VCCad.Geometry;

namespace VCCad.App.Controls;

/// <summary>
/// The editable pasteboard. Responsibilities:
/// <list type="bullet">
/// <item>Render the document (artboards + paths) with the Skia-backed Avalonia
/// <see cref="DrawingContext"/>. Cubic segments map to native
/// <c>CubicBezierTo</c> calls — never flattened — matching the model exactly.</item>
/// <item>Pan by dragging with the pointer and scroll the wheel; zoom with Ctrl+wheel
/// anchored under the cursor.</item>
/// <item>Enforce the product's pasteboard rule (one full screen past every object
/// in every direction) by routing every pan through
/// <see cref="VCCad.Core.Viewport.PasteboardLayout.ClampTopLeft"/>.</item>
/// </list>
/// </summary>
public sealed class CanvasWorkspace : Control
{
    private CadDocument? _document;
    private PasteboardLayout _layout = new(Rect2D.Empty);
    private Vector2D _offset;         // artwork top-left position on screen, px
    private Point _lastPointer;
    private bool _isPanning;
    private bool _hasLaidOutOnce;

    /// <summary>The document being edited; assigning invalidates the canvas.</summary>
    public CadDocument? Document
    {
        get => _document;
        set
        {
            _document = value;
            _layout = new PasteboardLayout(ComputeExtent());
            _offset = _layout.CenterInViewport(ViewportPixels);
            InvalidateVisual();
        }
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

    public CanvasWorkspace()
    {
        ClipToBounds = true;
    }

    protected override void OnSizeChanged(Avalonia.Controls.SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        // The initial view offset is computed before layout gives us real bounds;
        // on the first meaningful size, fit the artwork so it is actually visible.
        if (!_hasLaidOutOnce && e.NewSize.Width > 10 && e.NewSize.Height > 10 && _document is not null)
        {
            _hasLaidOutOnce = true;
            ZoomToFit();
        }
    }

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

    /// <summary>Redraws after an external document mutation (undo/redo/import).</summary>
    public void RefreshFromDocument()
    {
        _layout = new PasteboardLayout(ComputeExtent());
        InvalidateVisual();
    }

    private void ZoomAtCenter(double factor)
    {
        _layout.Zoom = factor;
        // Keep whatever model point is under the viewport centre centred.
        Size2D vp = ViewportPixels;
        Point2D centre = ModelPointAtScreen(new Point(vp.Width / 2, vp.Height / 2));
        Size2D extentPx = _layout.ExtentOnScreen;
        _offset = new Vector2D(vp.Width / 2 - (centre.X - _layout.Extent.Left) * _layout.Zoom,
                               vp.Height / 2 - (centre.Y - _layout.Extent.Top) * _layout.Zoom);
        InvalidateVisual();
    }

    // ------------------------------------------------------------------
    // Model ↔ screen mapping
    // ------------------------------------------------------------------

    /// <summary>Maps a model point to its on-screen pixel position.</summary>
    private Point ModelToScreen(Point2D model)
        => new((model.X - _layout.Extent.Left) * _layout.Zoom + _offset.X,
               (model.Y - _layout.Extent.Top) * _layout.Zoom + _offset.Y);

    /// <summary>Maps an on-screen point to model coordinates.</summary>
    private Point2D ModelPointAtScreen(Point screen)
        => new((screen.X - _offset.X) / _layout.Zoom + _layout.Extent.Left,
               (screen.Y - _offset.Y) / _layout.Zoom + _layout.Extent.Top);

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
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _lastPointer = e.GetPosition(this);
            _isPanning = true;
            e.Pointer.Capture(this);
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_isPanning)
        {
            return;
        }

        Point current = e.GetPosition(this);
        Vector2D delta = new(current.X - _lastPointer.X, current.Y - _lastPointer.Y);
        _lastPointer = current;

        _offset = _layout.ClampTopLeft(_offset + delta, ViewportPixels);
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _isPanning = false;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            // Zoom anchored at the cursor position.
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
            // Plain wheel scrolls vertically, like most editors.
            Vector2D delta = new(0, -e.Delta.Y * 60.0);
            _offset = _layout.ClampTopLeft(_offset + delta, ViewportPixels);
            InvalidateVisual();
        }

        e.Handled = true;
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

        // Pasteboard tint — the area beyond the artboards, Illustrator-style.
        // A light neutral tone visually separates "paper" from "desk".
        Rect2D extent = _layout.Extent;
        Point topLeft = ModelToScreen(new Point2D(extent.Left, extent.Top));
        context.FillRectangle(
            new SolidColorBrush(Color.FromArgb(40, 180, 180, 180)),
            new Rect(topLeft.X, topLeft.Y, extent.Width * _layout.Zoom, extent.Height * _layout.Zoom));

        foreach (Artboard artboard in _document.Artboards)
        {
            PaintArtboard(context, artboard);
        }
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
                    // Group transforms are not yet visualised in the seed renderer;
                    // the geometry mapper that bakes transforms (as the PDF exporter
                    // does) is part of the M6 rendering task. Children are painted
                    // with accumulated opacity only.
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

                Point start = ModelToScreen(sub.Nodes[0].Anchor);
                g.BeginFigure(start, sub.IsClosed);
                int segmentCount = sub.SegmentCount;
                for (int i = 0; i < segmentCount; i++)
                {
                    PathNode from = sub.Nodes[i];
                    PathNode to = sub.Nodes[(i + 1) % sub.Nodes.Count];
                    Point fromAnchor = ModelToScreen(from.Anchor);
                    Point p1 = ModelToScreen(from.OutHandle);
                    Point p2 = ModelToScreen(to.InHandle);
                    Point p3 = ModelToScreen(to.Anchor);

                    // A straight segment is the degenerate cubic with both handles
                    // collapsed onto their endpoints (screen-space, so the zoom
                    // scale is uniform and this equivalence is preserved).
                    if (Near(p1, fromAnchor) && Near(p2, p3))
                    {
                        g.LineTo(p3);
                    }
                    else
                    {
                        g.CubicBezierTo(p1, p2, p3);
                    }
                }

                // EndFigure(bool) closes the current figure; fills only apply to
                // figures opened with isFilled=true (i.e. the model's closed
                // subpaths), preserving the open/closed fill rule.
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

        // Avalonia fills only closed figures, so passing the brush to open paths
        // is harmless — mirroring the model's open/closed fill rule.
        context.DrawGeometry(fillVisible ? fillBrush : null, strokeVisible ? strokePen : null, geometry);
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
