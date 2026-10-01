using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using VCCad.Core.Model;

namespace VCCad.App.Picking;

/// <summary>
/// The full-screen window that receives a screen pick.
///
/// It has to cover everything and paint almost nothing. It is transparent, topmost and undecorated, and the
/// ring it draws is **hollow** with a gap at the centre: a filled marker under the cursor would be what the
/// sample reads, so the eyedropper would report its own crosshair. Leaving the centre clear is what makes the
/// reading the screen rather than this window.
///
/// If the platform refuses transparency the window is opaque, and an opaque overlay would sample itself - so
/// that case is **detected and reported** rather than returning a plausible colour off its own background.
/// </summary>
internal sealed class ScreenPickOverlay : Window
{
    private Point _cursor;
    private bool _hasCursor;
    private double _scaling = 1.0;

    public ScreenPickOverlay()
    {
        SystemDecorations = SystemDecorations.None;
        WindowStartupLocation = WindowStartupLocation.Manual;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        Cursor = new Cursor(StandardCursorType.Cross);
    }

    /// <summary>What was picked, or null when the person cancelled or the screen could not be read.</summary>
    public ColorRgb? Picked { get; private set; }

    /// <summary>Whether the platform gave this window no transparency, making the sample meaningless.</summary>
    public bool IsOpaque { get; private set; }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        if (Screens.Primary is { } screen)
        {
            _scaling = screen.Scaling <= 0 ? 1.0 : screen.Scaling;
            Position = screen.Bounds.Position;
            Width = screen.Bounds.Width / _scaling;
            Height = screen.Bounds.Height / _scaling;
        }

        IsOpaque = ActualTransparencyLevel != WindowTransparencyLevel.Transparent;
        Focus();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        _cursor = e.GetPosition(this);
        _hasCursor = true;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        // The window covers the primary screen and sits at its origin, so the screen point is the window's
        // position plus the point inside it - converted from the logical units the pointer arrives in.
        Point inside = e.GetPosition(this);
        PixelPoint at = new(
            Position.X + (int)Math.Round(inside.X * _scaling),
            Position.Y + (int)Math.Round(inside.Y * _scaling));
        Picked = ScreenColour.TrySample(at.X, at.Y, out ColorRgb colour) ? colour : null;
        Close();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Key == Key.Escape)
        {
            Picked = null;
            Close();
            e.Handled = true;
        }
    }

    /// <summary>
    /// The marker: a hollow ring with a crosshair, leaving the pixel under the cursor untouched.
    ///
    /// The colours are fixed rather than themed, because this is drawn over an unknown screen - a ring that
    /// vanishes against a photograph is worse than one that is merely bright.
    /// </summary>
    public override void Render(DrawingContext context)
    {
        base.Render(context);

        if (!_hasCursor)
        {
            return;
        }

        var centre = new Point(_cursor.X, _cursor.Y);
        var pen = new Pen(Brushes.Black, 3.0);
        var inner = new Pen(Brushes.White, 1.0);
        const double radius = 11.0;

        // A black ring under a white one, so it reads on light and dark alike.
        context.DrawEllipse(null, pen, centre, radius, radius);
        context.DrawEllipse(null, inner, centre, radius, radius);

        // The crosshair stops short of the centre, for the same reason the ring is hollow.
        const double gap = 5.0;
        const double arm = 16.0;
        context.DrawLine(pen, new Point(centre.X - arm, centre.Y), new Point(centre.X - gap, centre.Y));
        context.DrawLine(pen, new Point(centre.X + gap, centre.Y), new Point(centre.X + arm, centre.Y));
        context.DrawLine(pen, new Point(centre.X, centre.Y - arm), new Point(centre.X, centre.Y - gap));
        context.DrawLine(pen, new Point(centre.X, centre.Y + gap), new Point(centre.X, centre.Y + arm));
    }

    /// <summary>
    /// Picks from whatever window a control lives in, or refuses when it has none.
    ///
    /// One place decides this, so the pane's button and the shell's operation cannot answer differently about
    /// whether there is a window to pick with.
    /// </summary>
    public static Task<(ColorRgb? Colour, string? Refusal)> PickFor(Visual? from)
        => TopLevel.GetTopLevel(from) is Window owner
            ? PickAsync(owner)
            : Task.FromResult<(ColorRgb?, string?)>((null, "no window to pick with"));

    /// <summary>
    /// Shows the overlay over the whole screen and waits for a pick, a cancel or a close.
    ///
    /// Returns null when nothing was picked. The caller cannot tell a cancel from a refusal on this value
    /// alone - <see cref="IsOpaque"/> is how the refusal is reported, and the operation says which it was.
    /// </summary>
    public static async Task<(ColorRgb? Colour, string? Refusal)> PickAsync(Window owner)
    {
        var overlay = new ScreenPickOverlay();
        await overlay.ShowDialog(owner);

        // The platform refused transparency, so the window is opaque and an opaque overlay samples itself.
        // Reporting that is the difference between ''could not'' and a colour off this window''s background.
        return overlay.IsOpaque
            ? (null, "the platform gave no transparent overlay, so the picker could only read itself")
            : (overlay.Picked, null);
    }
}
