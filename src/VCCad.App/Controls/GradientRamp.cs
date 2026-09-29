using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using VCCad.Core.Model;
using VCCad.Geometry;
using ModelStop = VCCad.Core.Model.GradientStop;

namespace VCCad.App.Controls;

/// <summary>
/// The gradient ramp: a strip of the colour sequence with one marker per stop.
///
/// The whole rectangle takes the pointer, not just the painted strip. A control with no background
/// is hit only where it paints, so the padding above and below the strip - where a marker's round
/// edges actually land - used to be dead: a click there fell through to whatever was behind the
/// pane. Laying the background down first is what makes the control whole.
///
/// Interactions: click the strip to add a stop where you clicked, drag a marker to move it,
/// double-click a marker to hand it to the colour picker, click a marker to select it.
/// </summary>
public sealed class GradientRamp : Control
{
    /// <summary>Radius of a stop marker, in control pixels.</summary>
    public const double MarkerRadius = 5.0;

    /// <summary>How far a click may miss a marker's centre and still count as hitting it.</summary>
    private const double MarkerSlop = 4.0;

    private const double Inset = 9.0;
    private const double StripTop = 11.0;
    private const double StripHeight = 20.0;

    private static readonly IBrush MarkerFill = new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF6));
    private static readonly IBrush MarkerOutline = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1E));
    private static readonly IBrush SelectedOutline = new SolidColorBrush(Color.FromRgb(0x4C, 0x9A, 0xFF));
    private static readonly IBrush StripBorder = new SolidColorBrush(Color.FromRgb(0x6A, 0x6A, 0x72));

    private GradientSpec _spec = GradientSpec.Default;
    private int _dragIndex = -1;

    /// <summary>The gradient being edited.</summary>
    public GradientSpec Spec
    {
        get => _spec;
        set
        {
            _spec = value ?? GradientSpec.Default;
            if (SelectedIndex >= _spec.Stops.Count)
            {
                SelectedIndex = _spec.Stops.Count - 1;
            }

            InvalidateVisual();
        }
    }

    /// <summary>Index into <see cref="GradientSpec.Stops"/> of the selected stop, or -1.</summary>
    public int SelectedIndex { get; private set; } = -1;

    /// <summary>Raised on every live change, including each step of a drag.</summary>
    public event EventHandler<GradientSpec>? SpecChanged;

    /// <summary>Raised when an edit settles, so the caller can commit one undo step.</summary>
    public event EventHandler<GradientSpec>? SpecCommitted;

    /// <summary>Raised on double-click, carrying the stop index the person wants to recolour.</summary>
    public event EventHandler<int>? StopActivated;

    /// <summary>The strip the ramp is drawn in, in control coordinates.</summary>
    public Rect StripBounds => new(Inset, StripTop, Math.Max(0, Bounds.Width - (Inset * 2)), StripHeight);

    public override void Render(DrawingContext context)
    {
        // The control's whole rectangle, deliberately, before anything else.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));

        Rect strip = StripBounds;
        if (strip.Width <= 1.0 || strip.Height <= 1.0)
        {
            return;
        }

        // The strip always shows the colour sequence left to right, whatever the gradient's real
        // kind is: it is a legend for the stops, not a picture of the geometry.
        GradientSpec ramp = _spec with
        {
            Kind = GradientKind.Linear,
            Spread = GradientSpread.Pad,
            Start = new Point2D(0.0, 0.5),
            End = new Point2D(1.0, 0.5),
        };

        IBrush? brush = GradientPaint.CreateBrush(ramp, strip, 1.0);
        if (brush is not null)
        {
            context.DrawRectangle(brush, null, strip, 2.0, 2.0);
        }

        context.DrawRectangle(null, new Pen(StripBorder, 1.0), strip, 2.0, 2.0);

        double markerY = MarkerY();
        for (int i = 0; i < _spec.Stops.Count; i++)
        {
            double x = PositionToX(_spec.Stops[i].Position);
            bool selected = i == SelectedIndex;
            context.DrawEllipse(
                MarkerFill,
                new Pen(selected ? SelectedOutline : MarkerOutline, selected ? 2.0 : 1.2),
                new Point(x, markerY),
                MarkerRadius,
                MarkerRadius);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        Point point = e.GetPosition(this);
        int marker = MarkerAt(point);
        bool leftDown = e.GetCurrentPoint(this).Properties.IsLeftButtonPressed;

        if (marker >= 0)
        {
            Select(marker);
            if (e.ClickCount >= 2)
            {
                StopActivated?.Invoke(this, marker);
            }
            else if (leftDown)
            {
                _dragIndex = marker;
                e.Pointer.Capture(this);
            }
        }
        else if (leftDown)
        {
            // Anywhere else in the control - including the padding above and below the strip -
            // adds a stop at the nearest ramp position.
            AddStop(XToPosition(point.X));
        }

        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (_dragIndex < 0 || _dragIndex >= _spec.Stops.Count)
        {
            return;
        }

        double position = XToPosition(e.GetPosition(this).X);
        var stops = _spec.Stops.ToList();
        stops[_dragIndex] = stops[_dragIndex] with { Position = position };
        _spec = _spec with { Stops = stops };
        SpecChanged?.Invoke(this, _spec);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_dragIndex < 0)
        {
            return;
        }

        _dragIndex = -1;
        e.Pointer.Capture(null);
        SpecCommitted?.Invoke(this, _spec);
        e.Handled = true;
    }

    /// <summary>The stop index under a point, or -1. Nearest marker wins when they overlap.</summary>
    public int MarkerAt(Point point)
    {
        double markerY = MarkerY();
        int best = -1;
        double bestDistance = double.MaxValue;
        for (int i = 0; i < _spec.Stops.Count; i++)
        {
            double dx = point.X - PositionToX(_spec.Stops[i].Position);
            double dy = point.Y - markerY;
            double distance = Math.Sqrt((dx * dx) + (dy * dy));
            if (distance <= MarkerRadius + MarkerSlop && distance < bestDistance)
            {
                best = i;
                bestDistance = distance;
            }
        }

        return best;
    }

    /// <summary>Selects a stop without editing anything.</summary>
    public void Select(int index)
    {
        SelectedIndex = index >= 0 && index < _spec.Stops.Count ? index : -1;
        InvalidateVisual();
    }

    /// <summary>
    /// Inserts a stop at a ramp position, coloured with whatever the ramp already shows there, and
    /// selects it. The colour comes from the shader's own blend so a new stop is invisible until it
    /// is moved.
    /// </summary>
    public void AddStop(double position)
    {
        position = Math.Clamp(position, 0.0, 1.0);
        (ColorRgb colour, double opacity) = GradientPaint.Sample(_spec, position);
        var stops = _spec.Stops.ToList();
        stops.Add(new ModelStop(position, colour, opacity));
        stops.Sort((a, b) => a.Position.CompareTo(b.Position));

        _spec = _spec with { Stops = stops };
        SelectedIndex = stops.FindIndex(s => Math.Abs(s.Position - position) < 1e-9);
        InvalidateVisual();
        SpecChanged?.Invoke(this, _spec);
        SpecCommitted?.Invoke(this, _spec);
    }

    private double MarkerY() => StripBounds.Y;

    private double PositionToX(double position)
    {
        Rect strip = StripBounds;
        return strip.X + (Math.Clamp(position, 0.0, 1.0) * strip.Width);
    }

    private double XToPosition(double x)
    {
        Rect strip = StripBounds;
        return strip.Width <= 0 ? 0.0 : Math.Clamp((x - strip.X) / strip.Width, 0.0, 1.0);
    }
}
