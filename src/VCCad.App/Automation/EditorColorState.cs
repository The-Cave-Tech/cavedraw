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

    /// <summary>The headless picker model: ring angle, triangle, marker and conversions.</summary>
    public ColorPickerModel Model { get; } = new(
        new VCCad.Geometry.Point2D(0, 0), 100, new ColorRgb(0.13, 0.13, 0.13));

    /// <summary>The selected colour.</summary>
    public ColorRgb Color => Model.Color;

    /// <summary>The selected opacity, 0 to 1.</summary>
    public double Alpha => Model.Alpha;

    /// <summary>The recent colours, most recent first.</summary>
    public IReadOnlyList<ColorRgb> Recent => _recent;

    /// <summary>Selects a colour and records it as recent.</summary>
    public void SetColor(ColorRgb color)
    {
        Model.SetColor(color);
        Remember(color);
    }

    /// <summary>Moves the ring's selected angle, keeping saturation and value.</summary>
    public void SelectAngle(double degrees) => Model.SelectAngle(degrees);

    /// <summary>Picks inside the triangle; outside is pulled onto the nearest edge.</summary>
    public void SelectTrianglePoint(VCCad.Geometry.Point2D point) => Model.SelectTrianglePoint(point);

    /// <summary>Sets the opacity, clamped.</summary>
    public void SetAlpha(double alpha) => Model.SetAlpha(alpha);

    /// <summary>Adds a colour to the recents, newest first and without duplicates.</summary>
    public void Remember(ColorRgb color)
    {
        _recent.RemoveAll(c => c.R == color.R && c.G == color.G && c.B == color.B);
        _recent.Insert(0, color);

        while (_recent.Count > RecentLimit)
        {
            _recent.RemoveAt(_recent.Count - 1);
        }
    }

    /// <summary>Empties the recents.</summary>
    public void ClearRecent() => _recent.Clear();
}
