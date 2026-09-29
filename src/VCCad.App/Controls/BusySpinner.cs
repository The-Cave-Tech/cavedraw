using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace VCCad.App.Controls;

/// <summary>
/// A small spinning pie that says the assistant has control of the document.
///
/// The editor is locked while a turn runs, and the only other sign of it was the
/// Cancel button inside the diagnostics panel: easy to have closed, and silent while
/// a long step is in flight. This is the signal that stays in the corner of the view
/// for as long as the model is working - a pie with a slice out of it, turning.
///
/// It is drawn rather than built from a progress bar because an indeterminate
/// progress bar is a sweeping highlight, not a spinner, and because the missing slice
/// is what makes the rotation legible: a plain disc looks static however fast it
/// turns.
/// </summary>
public sealed class BusySpinner : Control
{
    /// <summary>Degrees of the disc left out, so the turn is visible.</summary>
    private const double MissingSlice = 55.0;

    private static readonly IBrush Cheese = new SolidColorBrush(Color.FromRgb(0xF2, 0xA9, 0x3B));
    private static readonly IBrush Crust = new SolidColorBrush(Color.FromRgb(0x8A, 0x4B, 0x08));

    private readonly RotateTransform _rotation = new();
    private readonly DispatcherTimer _timer;
    private double _angle;
    private bool _spinning;

    public BusySpinner()
    {
        Width = 30;
        Height = 30;
        IsVisible = false;
        IsHitTestVisible = false;
        RenderTransformOrigin = RelativePoint.Center;
        RenderTransform = _rotation;
        ToolTip.SetTip(this, "The assistant has control of the document");

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _timer.Tick += (_, _) => Advance();
    }

    /// <summary>Degrees the pie turns per frame. Twelve at 50 ms is roughly one turn per 1.5 s.</summary>
    public double DegreesPerTick { get; set; } = 12.0;

    /// <summary>
    /// Whether the pie is turning. Setting it also shows or hides the control, so
    /// there is exactly one thing for a caller to flip.
    /// </summary>
    public bool IsSpinning
    {
        get => _spinning;
        set
        {
            if (_spinning == value)
            {
                return;
            }

            _spinning = value;
            IsVisible = value;
            if (value)
            {
                _timer.Start();
            }
            else
            {
                _timer.Stop();
            }
        }
    }

    /// <summary>The current rotation in degrees, 0 through 360.</summary>
    public double Angle => _angle;

    /// <summary>Advances the rotation by one frame. The timer calls this; tests call it directly.</summary>
    public void Advance()
    {
        _angle = (_angle + DegreesPerTick) % 360.0;
        _rotation.Angle = _angle;
    }

    public override void Render(DrawingContext context)
    {
        double radius = (Math.Min(Bounds.Width, Bounds.Height) / 2.0) - 2.0;
        if (radius <= 1.0)
        {
            return;
        }

        var centre = new Point(Bounds.Width / 2.0, Bounds.Height / 2.0);

        // The body runs from the end of the missing slice all the way round to its
        // start, then back to the centre: the disc with a wedge taken out of it.
        var pie = new StreamGeometry();
        using (StreamGeometryContext shape = pie.Open())
        {
            shape.BeginFigure(OnCircle(centre, radius, MissingSlice), isFilled: true);
            shape.ArcTo(OnCircle(centre, radius, 360.0), new Size(radius, radius), 0,
                isLargeArc: true, SweepDirection.Clockwise);
            shape.LineTo(centre);
            shape.EndFigure(true);
        }

        context.DrawGeometry(Cheese, new Pen(Crust, 1.5), pie);
    }

    /// <summary>A point on the circle, measured clockwise from twelve o'clock.</summary>
    private static Point OnCircle(Point centre, double radius, double degrees)
    {
        double radians = (degrees - 90.0) * Math.PI / 180.0;
        return new Point(
            centre.X + (radius * Math.Cos(radians)),
            centre.Y + (radius * Math.Sin(radians)));
    }
}
