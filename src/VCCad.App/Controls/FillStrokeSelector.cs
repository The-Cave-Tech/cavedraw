using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using VCCad.Core.Model;

namespace VCCad.App.Controls;

/// <summary>
/// The Photoshop/Illustrator fill &amp; stroke target diagram: a solid fill circle
/// (south-east) overlapping a stroke ring (north-west), the selected one drawn on
/// top, plus a small "transparent" swatch (white circle with a red NE→SW slash) in
/// the south-west corner. Clicking a circle chooses which colour is being edited;
/// clicking the small swatch clears the active target.
/// </summary>
public sealed class FillStrokeSelector : Control
{
    /// <summary>Raised when the active target changes (true = stroke).</summary>
    public event EventHandler<bool>? TargetChanged;

    /// <summary>Raised when the transparent swatch is clicked (true = stroke).</summary>
    public event EventHandler<bool>? ClearRequested;

    private ColorRgb _fill = ColorRgb.Black;
    private ColorRgb _stroke = ColorRgb.Black;
    private bool _fillVisible = true;
    private bool _strokeVisible = true;
    private bool _strokeSelected;

    public bool StrokeSelected => _strokeSelected;

    public void SetState(ColorRgb fill, bool fillVisible, ColorRgb stroke, bool strokeVisible, bool strokeSelected)
    {
        _fill = fill;
        _fillVisible = fillVisible;
        _stroke = stroke;
        _strokeVisible = strokeVisible;
        _strokeSelected = strokeSelected;
        InvalidateVisual();
    }

    // ---- geometry --------------------------------------------------------

    private (Point Stroke, Point Fill, Point None, double R) Layout()
    {
        double w = Bounds.Width;
        double h = Bounds.Height;
        double r = Math.Min(w, h) * 0.30;
        return (
            new Point(w * 0.36, h * 0.36),
            new Point(w * 0.64, h * 0.64),
            new Point(w * 0.20, h * 0.80),
            r);
    }

    public override void Render(DrawingContext context)
    {
        (Point strokeCenter, Point fillCenter, Point noneCenter, double r) = Layout();

        void DrawStroke()
        {
            if (_strokeVisible)
            {
                context.DrawEllipse(null, new Pen(new SolidColorBrush(ToColor(_stroke)), r * 0.55), strokeCenter, r, r);
            }
            else
            {
                context.DrawEllipse(Brushes.White, new Pen(new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xA3)), 1), strokeCenter, r, r);
                DrawSlash(context, strokeCenter, r * 0.95, Brushes.Red);
            }

        }

        void DrawFill()
        {
            if (_fillVisible)
            {
                context.DrawEllipse(new SolidColorBrush(ToColor(_fill)), new Pen(new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x42)), 1), fillCenter, r, r);
            }
            else
            {
                context.DrawEllipse(Brushes.White, new Pen(new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xA3)), 1), fillCenter, r, r);
                DrawSlash(context, fillCenter, r * 0.95, Brushes.Red);
            }

        }

        // The selected target is drawn last (higher z-order).
        if (_strokeSelected)
        {
            DrawFill();
            DrawStroke();
        }
        else
        {
            DrawStroke();
            DrawFill();
        }

        // Transparent swatch.
        double nr = r * 0.5;
        context.DrawEllipse(Brushes.White, new Pen(new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xA3)), 1), noneCenter, nr, nr);
        DrawSlash(context, noneCenter, nr * 0.9, Brushes.Red);
    }

    private static void DrawSlash(DrawingContext context, Point center, double radius, IBrush brush)
    {
        var pen = new Pen(brush, 1.6);
        double d = radius * 0.7;
        context.DrawLine(pen, new Point(center.X + d, center.Y - d), new Point(center.X - d, center.Y + d));
    }

    private static Color ToColor(ColorRgb c) => Color.FromArgb(
        (byte)Math.Round(Math.Clamp(c.A, 0, 1) * 255),
        (byte)Math.Round(Math.Clamp(c.R, 0, 1) * 255),
        (byte)Math.Round(Math.Clamp(c.G, 0, 1) * 255),
        (byte)Math.Round(Math.Clamp(c.B, 0, 1) * 255));

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        (Point strokeCenter, Point fillCenter, Point noneCenter, double r) = Layout();
        Point p = e.GetPosition(this);

        double dFill = Distance(p, fillCenter);
        double dStroke = Distance(p, strokeCenter);
        double dNone = Distance(p, noneCenter);

        if (dNone <= r * 0.7)
        {
            ClearRequested?.Invoke(this, _strokeSelected);
        }
        else if (dFill <= r * 1.2 && dFill <= dStroke)
        {
            if (_strokeSelected)
            {
                _strokeSelected = false;
                TargetChanged?.Invoke(this, false);
                InvalidateVisual();
            }
        }
        else if (dStroke <= r * 1.2)
        {
            if (!_strokeSelected)
            {
                _strokeSelected = true;
                TargetChanged?.Invoke(this, true);
                InvalidateVisual();
            }
        }

        e.Handled = true;
    }

    private static double Distance(Point a, Point b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
