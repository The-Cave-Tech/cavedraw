using VCCad.Core.Color;
using VCCad.Core.Model;

namespace VCCad.App.Automation;

/// <summary>
/// The colour the editor is currently working in, and the recent colours beside it.
///
/// It lives here rather than inside the picker control because the picker and the automation
/// registry must agree: a colour set through the API and a colour clicked in the panel are the
/// same colour, not two states that happen to look alike. Everything the panel shows is read
/// from this, so there is nothing a person can set that a driver cannot read back.
/// </summary>
public sealed class EditorColorState
{
    /// <summary>How many recent colours are kept. Two columns, so an even number.</summary>
    public const int RecentLimit = 16;

    private readonly List<ColorRgb> _recent = new();

    /// <summary>The editor's colour state.</summary>
    public static EditorColorState Shared { get; } = new();

    /// <summary>
    /// Raised whenever the working colour or the recents change.
    ///
    /// Without this the panel only caught up when something in it was clicked, so
    /// <c>color.set</c> through the API changed the colour and the picker went on showing the
    /// old one until a person happened to touch it. A driver and a person would then be looking
    /// at two different colours, which is exactly the divergence the registry exists to prevent.
    /// </summary>
    public event EventHandler? Changed;

    /// <summary>The headless picker model: ring angle, triangle, marker and conversions.</summary>
    public ColorPickerModel Model { get; } = new(
        new VCCad.Geometry.Point2D(0, 0), 100, new ColorRgb(0.13, 0.13, 0.13));

    /// <summary>The selected colour.</summary>
    public ColorRgb Color => Model.Color;

    /// <summary>The selected opacity, 0 to 1.</summary>
    public double Alpha => Model.Alpha;

    /// <summary>The recent colours, most recent first.</summary>
    public IReadOnlyList<ColorRgb> Recent => _recent;

    /// <summary>
    /// The last colour the screen picker chose, shown beside the eyedropper as a small filled circle and
    /// applied by clicking it - the same thing clicking a recent swatch does.
    ///
    /// Kept separate from <see cref="Color"/> because it is a **result**, not a selection: a person picks a
    /// colour to see what it is before deciding to use it.
    /// </summary>
    public ColorRgb? LastPicked { get; private set; }

    /// <summary>
    /// Puts the picked colour back as it was.
    ///
    /// `LastPicked` is a **result** rather than a selection, so there is no public way to un-pick a colour - a
    /// person cannot clear a result. This exists so a test that drives a pick can put the shared state back
    /// instead of leaving its colour behind for whatever runs next.
    /// </summary>
    internal void RestorePicked(ColorRgb? colour) => LastPicked = colour;

    /// <summary>Records a colour the screen picker chose, and makes it the working colour.</summary>
    public void SetPicked(ColorRgb color)
    {
        LastPicked = color;
        Model.SetColor(color);
        Remember(color);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Selects a colour and records it as recent.</summary>
    public void SetColor(ColorRgb color)
    {
        Model.SetColor(color);
        Remember(color);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Moves the ring's selected angle, keeping saturation and value.</summary>
    public void SelectAngle(double degrees)
    {
        Model.SelectAngle(degrees);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Picks inside the triangle; outside is pulled onto the nearest edge.</summary>
    public void SelectTrianglePoint(VCCad.Geometry.Point2D point)
    {
        Model.SelectTrianglePoint(point);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Sets the opacity, clamped.</summary>
    public void SetAlpha(double alpha)
    {
        Model.SetAlpha(alpha);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Adds a colour to the recents, newest first and without duplicates.</summary>
    public void Remember(ColorRgb color)
    {
        _recent.RemoveAll(c => c.R == color.R && c.G == color.G && c.B == color.B);
        _recent.Insert(0, color);

        while (_recent.Count > RecentLimit)
        {
            _recent.RemoveAt(_recent.Count - 1);
        }

        // Raised here too, because this is reachable on its own through color.remember. Leaving
        // it out was found by comparing two screenshots byte for byte: the state changed and the
        // swatch pad did not, and nothing else would have shown that.
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Empties the recents.</summary>
    public void ClearRecent()
    {
        _recent.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
