using VCCad.Core.Model;

namespace VCCad.Core.Color;

/// <summary>
/// A colour in HSV space: <c>H</c> in degrees [0, 360), <c>S</c> and <c>V</c> in [0, 1].
///
/// The spectrum ring is the slice <c>S = 1, V = 1</c> of HSV, and the picker
/// triangle is the <c>V</c>-scaled slice between that point, white and black.
/// HSV is therefore the natural space for the wheel maths, even though the panel
/// displays HSL.
/// </summary>
public readonly record struct HsvColor(double H, double S, double V)
{
    /// <summary>Converts an RGB colour to HSV. The alpha channel is not part of HSV.</summary>
    public static HsvColor FromRgb(ColorRgb color)
    {
        double r = Math.Clamp(color.R, 0.0, 1.0);
        double g = Math.Clamp(color.G, 0.0, 1.0);
        double b = Math.Clamp(color.B, 0.0, 1.0);

        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;

        double s = max <= 1e-12 ? 0.0 : delta / max;
        return new HsvColor(
            ColorConversion.HueFromRgb(r, g, b, max, delta),
            Math.Clamp(s, 0.0, 1.0),
            max);
    }

    /// <summary>Converts back to RGB, carrying <paramref name="alpha"/> through unchanged.</summary>
    public ColorRgb ToRgb(double alpha = 1.0)
    {
        double h = ColorConversion.NormalizeHue(H);
        double s = Math.Clamp(S, 0.0, 1.0);
        double v = Math.Clamp(V, 0.0, 1.0);

        double c = v * s;
        double hp = h / 60.0;
        double x = c * (1.0 - Math.Abs((hp % 2.0) - 1.0));
        double m = v - c;

        (double r, double g, double b) = hp switch
        {
            >= 0.0 and < 1.0 => (c, x, 0.0),
            >= 1.0 and < 2.0 => (x, c, 0.0),
            >= 2.0 and < 3.0 => (0.0, c, x),
            >= 3.0 and < 4.0 => (0.0, x, c),
            >= 4.0 and < 5.0 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };

        return new ColorRgb(r + m, g + m, b + m, alpha);
    }

    /// <summary>
    /// The fully saturated, full value colour for a hue — one pixel of the
    /// spectrum ring. <see cref="HslColor"/> with L = 0.5 is the same family.
    /// </summary>
    public static ColorRgb HueColor(double hue) => new HsvColor(hue, 1.0, 1.0).ToRgb();

    /// <summary>A copy with the hue wrapped into [0,360) and S/V clamped into [0,1].</summary>
    public HsvColor Normalized()
        => new(ColorConversion.NormalizeHue(H), Math.Clamp(S, 0.0, 1.0), Math.Clamp(V, 0.0, 1.0));
}
