using VCCad.Geometry;

namespace VCCad.Core.Viewport;

/// <summary>
/// Pure logic for the pasteboard panning contract (product requirement 1):
/// <em>the workspace is scrollable to at least one full screen past the extents
/// of all objects in every direction</em>, Illustrator-style.
///
/// Model: the "artwork" (union of artboards and their content) occupies a
/// rectangle in model points. On screen it is scaled by <see cref="Zoom"/> and
/// offset by a translation. The legal positions of the artwork's top-left corner
/// are constrained so that:
/// <list type="bullet">
/// <item>the artwork's top-left may sit at the right/bottom viewport edge (leaving
/// a blank full viewport on the left/top), and</item>
/// <item>the artwork's bottom-right may sit at the top/left viewport edge (leaving
/// a blank full viewport on the right/bottom).</item>
/// </list>
/// Everything in between is reachable, which guarantees a full screen of scroll
/// room beyond the artwork on every side regardless of artwork size. Clamping is
/// expressed purely in screen pixels so the UI layer never re-derives the rules.
/// </summary>
public sealed class PasteboardLayout
{
    private readonly Rect2D _extent;
    private double _zoom = 1.0;

    /// <summary>
    /// Creates the layout for a given artwork extent (model points).
    /// </summary>
    public PasteboardLayout(Rect2D extent)
    {
        _extent = extent.IsEmpty ? Rect2D.Empty : extent;
    }

    /// <summary>The artwork extent in model points (empty when no artwork exists).</summary>
    public Rect2D Extent => _extent;

    /// <summary>Zoom factor mapping model points to screen pixels (1 px per pt at 1×).</summary>
    public double Zoom
    {
        get => _zoom;
        set => _zoom = MathUtils.Clamp(value, 0.02, 64.0);
    }

    /// <summary>The artwork size on screen, in pixels.</summary>
    public Size2D ExtentOnScreen => new(_extent.Width * _zoom, _extent.Height * _zoom);

    /// <summary>
    /// Most extreme allowed positions of the artwork top-left corner relative to
    /// the viewport top-left. At <see cref="MinTopLeft"/> the artwork's right/bottom
    /// edge is at the viewport's left/top edge (a full blank viewport on the right
    /// and below); at <see cref="MaxTopLeft"/> the artwork's top-left edge is at the
    /// viewport's right/bottom edge (a full blank viewport on the left and above).
    /// </summary>
    public Vector2D MinTopLeft(Size2D viewportPixels)
        => new(-_extent.Width * _zoom, -_extent.Height * _zoom);

    public Vector2D MaxTopLeft(Size2D viewportPixels)
        => new(viewportPixels.Width, viewportPixels.Height);

    /// <summary>Clamps a desired artwork top-left corner into the legal pan range
    /// for the given viewport. Returns the nearest legal position.</summary>
    public Vector2D ClampTopLeft(Vector2D desired, Size2D viewportPixels)
    {
        double minX = MinTopLeft(viewportPixels).X;
        double maxX = MaxTopLeft(viewportPixels).X;
        double minY = MinTopLeft(viewportPixels).Y;
        double maxY = MaxTopLeft(viewportPixels).Y;

        // Keep numeric sanity for the empty-artwork case (both extremes collapse).
        if (maxX < minX)
        {
            maxX = minX;
        }

        if (maxY < minY)
        {
            maxY = minY;
        }

        return new Vector2D(
            MathUtils.Clamp(desired.X, minX, maxX),
            MathUtils.Clamp(desired.Y, minY, maxY));
    }

    /// <summary>Default fit margin: ~1 cm of screen space at 96 dpi (96/2.54 ≈ 37.8 px).</summary>
    public const double DefaultFitMarginPixels = 37.8;

    /// <summary>
    /// Returns the zoom level that fits the artwork inside the viewport leaving a
    /// fixed pixel margin (default ~1 cm at 96 dpi) on each side, or 1× when the
    /// artwork is empty.
    /// </summary>
    public double ZoomToFit(Size2D viewportPixels, double marginPixels = DefaultFitMarginPixels)
    {
        if (_extent.IsEmpty)
        {
            return 1.0;
        }

        // Never let the margin consume the whole viewport.
        double margin = Math.Min(marginPixels, Math.Min(viewportPixels.Width, viewportPixels.Height) / 2.0 - 1.0);
        margin = Math.Max(1.0, margin);

        double fitX = (viewportPixels.Width - 2.0 * margin) / _extent.Width;
        double fitY = (viewportPixels.Height - 2.0 * margin) / _extent.Height;
        return MathUtils.Clamp(Math.Min(fitX, fitY), 0.02, 64.0);
    }

    /// <summary>
    /// The artwork top-left corner that centres the artwork in the viewport —
    /// the starting position after "zoom to fit".
    /// </summary>
    public Vector2D CenterInViewport(Size2D viewportPixels)
        => new((viewportPixels.Width - _extent.Width * _zoom) / 2.0,
               (viewportPixels.Height - _extent.Height * _zoom) / 2.0);
}
