using VCCad.Core.Model;
using VCCad.Geometry;

namespace VCCad.Core.Color;

/// <summary>
/// Everything the picker panel displays for one selection, so the same values
/// can be returned by an operation as by the control.
/// </summary>
public sealed record ColorPickerSnapshot(
    double AngleDegrees,
    double Hue,
    double Saturation,
    double Lightness,
    ColorRgb Color,
    string Hex,
    double Alpha,
    Point2D RingPoint,
    Point2D MarkerPoint,
    TriangleCorners Corners,
    CornerColors CornerColors,
    TriangleGradient Gradient);

/// <summary>
/// The stateful ring-and-triangle colour picker, headless.
///
/// The model holds one selected angle and one selected colour, with the
/// invariant that the colour's hue is the angle (for a chromatic colour): the
/// triangle's first corner therefore always sits on the ring under the selected
/// hue and adopts that colour. Rotating the ring keeps saturation and value and
/// changes the hue, exactly as dragging the wheel does; picking inside the
/// triangle changes saturation and value and keeps the hue.
///
/// The marker ("small white circle") position and the colour at a point are
/// mutual inverses, so a click and a read-back agree.
/// </summary>
public sealed class ColorPickerModel
{
    private double _angleDegrees;

    /// <summary>
    /// Creates a picker centred on <paramref name="center"/>; the ring radius is
    /// the triangle's circumradius. The initial colour defaults to opaque red.
    /// </summary>
    public ColorPickerModel(Point2D center = default, double radius = 1.0, ColorRgb? initialColor = null)
    {
        Center = center;
        Radius = radius;
        SetColor(initialColor ?? ColorRgb.Red);
    }

    /// <summary>Centre of the ring in the same space the control draws in.</summary>
    public Point2D Center { get; set; }

    /// <summary>Ring radius; the triangle is inscribed in it.</summary>
    public double Radius { get; set; }

    /// <summary>The selected angle on the spectrum ring, in degrees [0, 360), clockwise from +X.</summary>
    public double AngleDegrees => _angleDegrees;

    /// <summary>The selected hue; the angle itself, wrapped.</summary>
    public double Hue => SpectrumRing.HueForAngle(_angleDegrees);

    /// <summary>The selected colour (alpha carried separately in <see cref="Alpha"/>).</summary>
    public ColorRgb Color { get; private set; }

    /// <summary>Panel opacity, 0..1.</summary>
    public double Alpha { get; private set; } = 1.0;

    /// <summary>The colour of the ring where the first corner touches.</summary>
    public ColorRgb HueColor => SpectrumRing.ColorAtAngle(_angleDegrees);

    /// <summary>The three triangle corners for the selected angle.</summary>
    public TriangleCorners Corners => ColorTriangle.Corners(Center, Radius, _angleDegrees);

    /// <summary>The three corner colours: hue, white, black.</summary>
    public CornerColors CornerColors => ColorTriangle.CornerColors(_angleDegrees);

    /// <summary>The triangle fill gradient: three stops at the corners.</summary>
    public TriangleGradient Gradient => ColorTriangle.Gradient(Corners, CornerColors);

    /// <summary>The point on the ring that the first corner touches.</summary>
    public Point2D RingPoint => SpectrumRing.PointAtAngle(Center, Radius, _angleDegrees);

    /// <summary>Where the small white selection circle sits for the current colour.</summary>
    public Point2D MarkerPoint => ColorTriangle.PointForColor(Color, Corners);

    /// <summary>The selected colour as the HSL the panel shows.</summary>
    public HslColor Hsl => HslColor.FromRgb(Color);

    /// <summary>The selected colour with the panel opacity applied.</summary>
    public ColorRgb ColorWithAlpha => Color.WithAlpha(Alpha);

    /// <summary>
    /// Orients the triangle to a ring angle. The barycentric position of the
    /// selected colour is preserved, so saturation and value stay put and the
    /// hue follows the ring — the drag-the-wheel behaviour. Because the angle
    /// is stored (not accumulated), returning to an angle returns the colour.
    /// </summary>
    public void SelectAngle(double angleDegrees)
    {
        Barycentric weights = ColorTriangle.WeightsForColor(Color);
        _angleDegrees = SpectrumRing.NormalizeAngle(angleDegrees);
        Color = ColorTriangle.ColorAt(weights, ColorTriangle.CornerColors(_angleDegrees));
    }

    /// <summary>Orients the triangle to the angle of a pointer position on the ring.</summary>
    public void SelectRingPoint(Point2D point)
        => SelectAngle(SpectrumRing.AngleAtPoint(Center, point));

    /// <summary>
    /// Selects the colour at a point in the triangle. A point outside the
    /// triangle is pulled to the nearest edge first, so an imprecise click still
    /// selects the closest available colour and the marker stays on the edge.
    /// </summary>
    public void SelectTrianglePoint(Point2D point)
    {
        Barycentric weights = ColorTriangle.Barycentric(point, Corners).Clamped();
        Color = ColorTriangle.ColorAt(weights, CornerColors);
    }

    /// <summary>
    /// Adopts an explicit colour. The ring orients itself to the colour's hue;
    /// an achromatic colour has no hue, so the current angle is kept and the
    /// marker lands on the white-black edge.
    /// </summary>
    public void SetColor(ColorRgb color)
    {
        Color = color.Clamped();
        HsvColor hsv = HsvColor.FromRgb(Color);
        if (hsv.S > 1e-9)
        {
            _angleDegrees = SpectrumRing.AngleForHue(hsv.H);
        }
    }

    /// <summary>Adopts an HSL colour, carrying the current alpha.</summary>
    public void SetHsl(HslColor hsl) => SetColor(hsl.ToRgb(Alpha));

    /// <summary>Adopts a hex RGB colour, carrying the current alpha.</summary>
    public void SetHex(string hex) => SetColor(HexColor.Parse(hex).WithAlpha(Alpha));

    /// <summary>Sets the panel opacity, clamped into [0,1].</summary>
    public void SetAlpha(double alpha) => Alpha = Math.Clamp(alpha, 0.0, 1.0);

    /// <summary>The value displayed in the hex field.</summary>
    public string ToHex(bool includeAlpha = false) => HexColor.Format(Color, includeAlpha);

    /// <summary>Every value the picker shows, for a UI or an automation operation.</summary>
    public ColorPickerSnapshot Snapshot()
    {
        HslColor hsl = Hsl;
        return new ColorPickerSnapshot(
            AngleDegrees,
            Hue,
            hsl.S,
            hsl.L,
            Color,
            HexColor.Format(Color),
            Alpha,
            RingPoint,
            MarkerPoint,
            Corners,
            CornerColors,
            Gradient);
    }
}
