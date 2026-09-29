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
/// clicking the small swatch clears the active target; clicking the little
/// double-ended arc north-east of the circles swaps the fill and stroke colours.
/// </summary>
public sealed class FillStrokeSelector : Control
{
    /// <summary>Raised when the active target changes (true = stroke).</summary>
    public event EventHandler<bool>? TargetChanged;

    /// <summary>Raised when the transparent swatch is clicked (true = stroke).</summary>
    public event EventHandler<bool>? ClearRequested;

    /// <summary>Raised when the swap arc is clicked: flip the fill and stroke colours.</summary>
    public event EventHandler? SwapRequested;

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
        // A control with no background is only hit where it paints, so the gaps between the
        // circles — including the middle of the swap arc — would swallow clicks. Laying down
        // a transparent rectangle makes the whole control a target.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));

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

        // The swap arc sits last so it is never hidden by the circles.
        DrawSwapArc(context);
    }

    /// <summary>
    /// Where the flip arc lives: a small double-ended arrow north-east of the two circles,
    /// sitting on the same NW-SE diagonal they do.
    /// </summary>
    private (Point Center, double Radius, double HitRadius) SwapLayout()
    {
        double min = Math.Min(Bounds.Width, Bounds.Height);
        return (new Point(Bounds.Width * 0.78, Bounds.Height * 0.22), min * 0.19, min * 0.22);
    }

    /// <summary>
    /// The flip glyph: a 120-degree arc with a head at each end, rotated 45 degrees clockwise
    /// so it echoes the diagonal the ring and circle lie on.
    /// </summary>
    private void DrawSwapArc(DrawingContext context)
    {
        (Point center, double radius, _) = SwapLayout();
        if (radius <= 1.0)
        {
            return;
        }

        var brush = new SolidColorBrush(Color.FromRgb(0xD2, 0xD2, 0xDC));
        double thickness = Math.Max(1.2, radius * 0.17);

        // 315 degrees (up and to the right) is the midpoint; 120 degrees of sweep centred there.
        const double startDeg = 255.0;
        const double sweepDeg = 120.0;
        double endDeg = startDeg + sweepDeg;

        Point At(double deg) => new(
            center.X + radius * Math.Cos(deg * Math.PI / 180.0),
            center.Y + radius * Math.Sin(deg * Math.PI / 180.0));

        Point start = At(startDeg);
        Point end = At(endDeg);

        var arc = new StreamGeometry();
        using (StreamGeometryContext ctx = arc.Open())
        {
            ctx.BeginFigure(start, false);
            ctx.ArcTo(end, new Size(radius, radius), 0, false, SweepDirection.Clockwise);
            ctx.EndFigure(false);
        }

        context.DrawGeometry(null, new Pen(brush, thickness), arc);

        // A head at each end, pointing out along the tangent, so the glyph reads both ways.
        DrawArrowHead(context, brush, start, Tangent(startDeg, -1.0), radius * 0.46);
        DrawArrowHead(context, brush, end, Tangent(endDeg, 1.0), radius * 0.46);
    }

    /// <summary>Unit direction of travel around the arc at an angle (y-down, so clockwise).</summary>
    private static Vector Tangent(double degrees, double sign)
    {
        double radians = degrees * Math.PI / 180.0;
        return new Vector(-Math.Sin(radians), Math.Cos(radians)) * sign;
    }

    private static void DrawArrowHead(DrawingContext context, IBrush brush, Point tip, Vector direction, double size)
    {
        double length = direction.Length;
        if (length <= 0.0)
        {
            return;
        }

        Vector forward = direction / length;
        Vector side = new(-forward.Y, forward.X);
        Point back = tip - forward * size;
        var head = new StreamGeometry();
        using (StreamGeometryContext ctx = head.Open())
        {
            ctx.BeginFigure(tip, true);
            ctx.LineTo(back + side * (size * 0.55));
            ctx.LineTo(back - side * (size * 0.55));
            ctx.EndFigure(true);
        }

        context.DrawGeometry(brush, null, head);
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

        // The circles own their hit areas: the swap arc is only considered when the click is
        // outside all three, so it can never steal a click meant for the fill or the stroke.
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
        else
        {
            (Point swapCenter, _, double swapHit) = SwapLayout();
            if (Distance(p, swapCenter) <= swapHit)
            {
                SwapRequested?.Invoke(this, EventArgs.Empty);
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
