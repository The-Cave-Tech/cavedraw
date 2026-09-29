using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using VCCad.Core.Model;

namespace VCCad.App.Controls;

/// <summary>
/// The opacity bar of the colour picker: a horizontal bar graded from the
/// current colour fully transparent (light, against the white bar) to fully
/// opaque (dark for any dark colour), a draggable thumb, and a live value.
///
/// Kept as its own control rather than a themed <see cref="Slider"/> because the
/// gradient has to be the picker's colour, which a Fluent slider track will not
/// show. The maths is only clamp/lerp; every colour value still comes from Core.
/// </summary>
public sealed class ColorWheelOpacityBar : Control
{
    private double _value = 1.0;
    private ColorRgb _color = ColorRgb.Red;
    private bool _dragging;

    /// <summary>Raised while the value changes, including during a drag.</summary>
    public event EventHandler? ValueChanged;

    /// <summary>Raised when the drag or click finishes, so the caller can commit one undo step.</summary>
    public event EventHandler? Commit;

    /// <summary>Opacity in [0,1]. Set programmatically via <see cref="SetValue"/>.</summary>
    public double Value => _value;

    /// <summary>The colour the bar grades; the alpha channel is ignored.</summary>
    public ColorRgb Color
    {
        get => _color;
        set
        {
            _color = value.WithAlpha(1.0);
            InvalidateVisual();
        }
    }

    /// <summary>Sets the value without raising <see cref="ValueChanged"/>.</summary>
    public void SetValue(double value)
    {
        _value = Math.Clamp(value, 0.0, 1.0);
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        double width = Bounds.Width;
        double height = Bounds.Height;
        if (width <= 2 || height <= 2)
        {
            return;
        }

        var track = new Rect(0.0, 1.0, width, height - 2.0);
        var border = new Pen(new SolidColorBrush(Avalonia.Media.Color.FromRgb(0x6A, 0x6A, 0x72)), 1.0);

        // White base + colour alpha ramp = the "light to dark" grading.
        context.DrawRectangle(Brushes.White, border, track, 3.0, 3.0);
        var ramp = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0.0, 0.0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1.0, 0.0, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(ToColor(_color.WithAlpha(0.0)), 0.0),
                new GradientStop(ToColor(_color.WithAlpha(1.0)), 1.0),
            },
        };
        context.DrawRectangle(ramp, null, track, 3.0, 3.0);

        double thumbX = track.X + _value * track.Width;
        var thumbCenter = new Point(thumbX, height / 2.0);
        context.DrawEllipse(
            Brushes.White,
            new Pen(new SolidColorBrush(Avalonia.Media.Color.FromRgb(0x30, 0x30, 0x38)), 1.5),
            thumbCenter,
            6.0,
            6.0);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _dragging = true;
        e.Pointer.Capture(this);
        UpdateFromPoint(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging)
        {
            UpdateFromPoint(e.GetPosition(this));
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
        Commit?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateFromPoint(Point point)
    {
        double width = Math.Max(1.0, Bounds.Width);
        double value = Math.Clamp(point.X / width, 0.0, 1.0);
        if (Math.Abs(value - _value) <= 1e-12)
        {
            return;
        }

        _value = value;
        InvalidateVisual();
        ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    private static Avalonia.Media.Color ToColor(ColorRgb c) => Avalonia.Media.Color.FromArgb(
        (byte)Math.Round(Math.Clamp(c.A, 0.0, 1.0) * 255.0),
        (byte)Math.Round(Math.Clamp(c.R, 0.0, 1.0) * 255.0),
        (byte)Math.Round(Math.Clamp(c.G, 0.0, 1.0) * 255.0),
        (byte)Math.Round(Math.Clamp(c.B, 0.0, 1.0) * 255.0));
}
