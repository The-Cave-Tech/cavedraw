using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using VCCad.Core.Color;
using VCCad.Core.Model;
using Point2D = VCCad.Geometry.Point2D;
using MediaGeometry = Avalonia.Media.Geometry;

namespace VCCad.App.Controls;

/// <summary>
/// The Inkscape/Affinity-style colour picker: a spectrum ring with an equilateral
/// triangle inscribed inside it, all three corners touching the ring's inner edge
/// (not its outer edge). The triangle's first corner sits at the selected position
/// on that inner circle and takes the ring's colour there; clockwise from it the
/// corners are white and black, and the fill is the barycentric blend of the three.
///
/// Every colour value comes from <see cref="ColorPickerModel"/> in VCCad.Core —
/// the ring angle, the three corners, the barycentric pick and the conversions are
/// never re-derived here. The model works in a normalised space (centre at the
/// origin, circumradius <see cref="Model.Radius"/>), and the control scales that
/// to its own pixels, so the same model can be shared with the automation
/// registry and a point means the same thing in both.
///
/// Pressing outside the triangle (i.e. on or near the ring) re-orients the
/// triangle to the pointer's angle and dragging carries it all the way round;
/// pressing inside the triangle selects the colour under the pointer. Releasing
/// commits.
/// </summary>
public sealed class ColorWheel : Control
{
    private const int RingTextureSize = 256;
    private const int FieldTextureSize = 128;

    /// <summary>Ring band thickness as a fraction of the control's outer radius.</summary>
    private const double RingThicknessFraction = 0.16;

    /// <summary>Transparent breathing room around the ring inside the control.</summary>
    private const double OuterMargin = 2.0;

    private static WriteableBitmap? _ringTexture;

    private ColorPickerModel _model = new(new Point2D(0.0, 0.0), 100.0, ColorRgb.Red);
    private WriteableBitmap? _fieldTexture;
    private bool _dragging;
    private bool _ringDrag;

    /// <summary>Raised on every change, including live drag.</summary>
    public event EventHandler? ColorChanged;

    /// <summary>Raised when the press/drag finishes, so the caller can commit one undo step.</summary>
    public event EventHandler? ColorCommitted;

    /// <summary>
    /// The headless picker model this control drives and draws. Set it to share the
    /// picker's state with the automation registry, so a colour set through the API
    /// and a colour clicked in the panel are the same colour. The model is expected
    /// to live in normalised coordinates (centre at the origin, radius 100).
    /// </summary>
    public ColorPickerModel Model
    {
        get => _model;
        set
        {
            _model = value ?? throw new ArgumentNullException(nameof(value));
            InvalidateVisual();
        }
    }

    /// <summary>The selected colour (opaque; the pane applies opacity separately).</summary>
    public ColorRgb Color => _model.Color.WithAlpha(1.0);

    /// <summary>The selected ring angle in degrees, clockwise from +X.</summary>
    public double AngleDegrees => _model.AngleDegrees;

    /// <summary>Adopts a colour, orienting the ring to its hue.</summary>
    public void SetColor(ColorRgb color)
    {
        _model.SetColor(color.WithAlpha(1.0));
        InvalidateVisual();
    }

    /// <summary>Redraws after the model was changed from outside the control.</summary>
    public void Refresh() => InvalidateVisual();

    // ---- screen <-> model ------------------------------------------------

    /// <summary>
    /// The control's geometry: the ring occupies the outermost band, and the
    /// triangle's circumradius is the ring's *inner* edge, so the triangle sits
    /// inside the ring rather than straddling it.
    /// </summary>
    private (Point Center, double TriangleRadius, double OuterRadius, double Scale) Geometry()
    {
        double width = Bounds.Width;
        double height = Bounds.Height;
        var center = new Point(width / 2.0, height / 2.0);
        double outerRadius = Math.Max(1.0, Math.Min(width, height) / 2.0 - OuterMargin);
        double triangleRadius = outerRadius * (1.0 - RingThicknessFraction);
        double modelRadius = Math.Max(1e-9, _model.Radius);
        return (center, triangleRadius, outerRadius, triangleRadius / modelRadius);
    }

    private static Point ToScreen(Point2D model, Point center, double scale)
        => new(center.X + model.X * scale, center.Y + model.Y * scale);

    private static Point2D ToModel(Point screen, Point center, double scale)
        => new((screen.X - center.X) / scale, (screen.Y - center.Y) / scale);

    // ---- drawing ---------------------------------------------------------

    public override void Render(DrawingContext context)
    {
        (Point center, double triangleRadius, double outerRadius, double scale) = Geometry();
        if (outerRadius <= 1.0)
        {
            return;
        }

        // 1. The spectrum ring, occupying the outermost band.
        var ringRect = new Rect(center.X - outerRadius, center.Y - outerRadius, outerRadius * 2.0, outerRadius * 2.0);
        context.DrawImage(
            GetRingTexture(),
            new Rect(0.0, 0.0, RingTextureSize, RingTextureSize),
            ringRect);

        // 2. The inscribed triangle: a barycentric colour field clipped to the
        //    triangle, so the fill really is the blend of the three corners.
        TriangleCorners corners = _model.Corners;
        CornerColors colors = _model.CornerColors;
        Point a = ToScreen(corners.First, center, scale);
        Point b = ToScreen(corners.Second, center, scale);
        Point c = ToScreen(corners.Third, center, scale);
        StreamGeometry geometry = BuildTriangle(a, b, c);

        DrawTriangleField(context, geometry, a, b, c, corners, colors);

        // 3. The small white selection circle. No outline: it is pure white, and a dark ring
        //    around it read as a second, competing stroke.
        double markerRadius = Math.Max(4.0, triangleRadius * 0.06);
        context.DrawEllipse(
            Brushes.White,
            null,
            ToScreen(_model.MarkerPoint, center, scale),
            markerRadius,
            markerRadius);
    }

    private static StreamGeometry BuildTriangle(Point a, Point b, Point c)
    {
        var geometry = new StreamGeometry();
        using (StreamGeometryContext ctx = geometry.Open())
        {
            ctx.BeginFigure(a, isFilled: true);
            ctx.LineTo(b);
            ctx.LineTo(c);
            ctx.EndFigure(isClosed: true);
        }

        return geometry;
    }

    /// <summary>
    /// Renders the barycentric colour field to a small bitmap and draws it into
    /// the triangle's bounding box under a geometry clip. The model's
    /// <see cref="ColorTriangle.ColorAt(Barycentric, CornerColors)"/> is
    /// evaluated per pixel, so the fill is exact rather than a mesh
    /// approximation.
    /// </summary>
    private void DrawTriangleField(
        DrawingContext context,
        MediaGeometry clip,
        Point a,
        Point b,
        Point c,
        TriangleCorners corners,
        CornerColors colors)
    {
        double minX = Math.Min(a.X, Math.Min(b.X, c.X));
        double minY = Math.Min(a.Y, Math.Min(b.Y, c.Y));
        double maxX = Math.Max(a.X, Math.Max(b.X, c.X));
        double maxY = Math.Max(a.Y, Math.Max(b.Y, c.Y));
        Rect bounds = new Rect(minX, minY, maxX - minX, maxY - minY).Inflate(1.0);
        if (bounds.Width <= 0.0 || bounds.Height <= 0.0)
        {
            return;
        }

        (Point center, _, _, double scale) = Geometry();
        int size = FieldTextureSize;
        WriteableBitmap bitmap = _fieldTexture ??= new WriteableBitmap(
            new PixelSize(size, size), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);

        using (ILockedFramebuffer framebuffer = bitmap.Lock())
        {
            unsafe
            {
                byte* basePtr = (byte*)framebuffer.Address;
                for (int y = 0; y < size; y++)
                {
                    byte* row = basePtr + y * framebuffer.RowBytes;
                    double screenY = bounds.Y + (y + 0.5) / size * bounds.Height;
                    double modelY = (screenY - center.Y) / scale;
                    for (int x = 0; x < size; x++)
                    {
                        double screenX = bounds.X + (x + 0.5) / size * bounds.Width;
                        double modelX = (screenX - center.X) / scale;
                        Barycentric weights = ColorTriangle.Barycentric(new Point2D(modelX, modelY), corners);
                        ColorRgb color = ColorTriangle.ColorAt(weights, colors);
                        byte* pixel = row + x * 4;
                        pixel[0] = ToByte(color.B);
                        pixel[1] = ToByte(color.G);
                        pixel[2] = ToByte(color.R);
                        pixel[3] = 255;
                    }
                }
            }
        }

        using (context.PushGeometryClip(clip))
        {
            context.DrawImage(bitmap, new Rect(0.0, 0.0, size, size), bounds);
        }
    }

    // ---- pointer ---------------------------------------------------------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        Point point = e.GetPosition(this);
        (Point center, _, _, double scale) = Geometry();
        Point2D modelPoint = ToModel(point, center, scale);
        _dragging = true;
        _ringDrag = !ColorTriangle.Barycentric(modelPoint, _model.Corners).IsInside(0.0);
        e.Pointer.Capture(this);
        ApplyModelPoint(modelPoint);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging)
        {
            (Point center, _, _, double scale) = Geometry();
            ApplyModelPoint(ToModel(e.GetPosition(this), center, scale));
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        e.Pointer.Capture(null);
        ColorCommitted?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyModelPoint(Point2D modelPoint)
    {
        if (_ringDrag)
        {
            _model.SelectRingPoint(modelPoint);
        }
        else
        {
            _model.SelectTrianglePoint(modelPoint);
        }

        InvalidateVisual();
        ColorChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---- textures --------------------------------------------------------

    /// <summary>
    /// Builds (once) the spectrum ring: a static hue wheel with a transparent
    /// hole. Hues come from Core's <see cref="SpectrumRing"/>, so the ring and
    /// the triangle's corner colour can never disagree.
    /// </summary>
    private static WriteableBitmap GetRingTexture()
    {
        if (_ringTexture is not null)
        {
            return _ringTexture;
        }

        int size = RingTextureSize;
        var bitmap = new WriteableBitmap(
            new PixelSize(size, size), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        double center = size / 2.0;
        double outer = center;
        double inner = outer * (1.0 - RingThicknessFraction);

        using (ILockedFramebuffer framebuffer = bitmap.Lock())
        {
            unsafe
            {
                byte* basePtr = (byte*)framebuffer.Address;
                for (int y = 0; y < size; y++)
                {
                    byte* row = basePtr + y * framebuffer.RowBytes;
                    double dy = y + 0.5 - center;
                    for (int x = 0; x < size; x++)
                    {
                        double dx = x + 0.5 - center;
                        double distance = Math.Sqrt(dx * dx + dy * dy);
                        byte* pixel = row + x * 4;

                        // One-pixel coverage ramp at both edges keeps the thin
                        // ring from aliasing when it is scaled down.
                        double coverage = Math.Clamp(outer + 0.5 - distance, 0.0, 1.0)
                                          * Math.Clamp(distance - inner + 0.5, 0.0, 1.0);
                        if (coverage <= 0.0)
                        {
                            pixel[0] = pixel[1] = pixel[2] = pixel[3] = 0;
                            continue;
                        }

                        double angle = Math.Atan2(dy, dx) * 180.0 / Math.PI;
                        ColorRgb color = SpectrumRing.ColorAtAngle(angle);
                        pixel[0] = Premultiply(color.B, coverage);
                        pixel[1] = Premultiply(color.G, coverage);
                        pixel[2] = Premultiply(color.R, coverage);
                        pixel[3] = (byte)Math.Round(coverage * 255.0);
                    }
                }
            }
        }

        _ringTexture = bitmap;
        return bitmap;
    }

    private static byte Premultiply(double channel, double coverage)
        => (byte)Math.Round(Math.Clamp(channel, 0.0, 1.0) * coverage * 255.0);

    private static byte ToByte(double channel)
        => (byte)Math.Round(Math.Clamp(channel, 0.0, 1.0) * 255.0);
}
