using VCCad.Core.Model;

namespace VCCad.Core.Color;

/// <summary>
/// A colour in HSL space: <c>H</c> in degrees [0, 360), <c>S</c> and <c>L</c> in [0, 1].
///
/// HSL is the representation the picker panel shows and edits, so it must round
/// trip against RGB tightly enough that "read the HSL values, retype them" does
/// not shift the colour. Achromatic colours (greys) carry no meaningful hue; the
/// conversion reports 0 and the round trip still returns the same grey.
/// </summary>
public readonly record struct HslColor(double H, double S, double L)
{
    /// <summary>Pure black.</summary>
    public static HslColor Black { get; } = new(0.0, 0.0, 0.0);

    /// <summary>Pure white.</summary>
    public static HslColor White { get; } = new(0.0, 0.0, 1.0);

    /// <summary>Converts an RGB colour to HSL. The alpha channel is not part of HSL.</summary>
    public static HslColor FromRgb(ColorRgb color)
    {
        double r = Math.Clamp(color.R, 0.0, 1.0);
        double g = Math.Clamp(color.G, 0.0, 1.0);
        double b = Math.Clamp(color.B, 0.0, 1.0);

        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;
        double l = (max + min) / 2.0;

        if (delta <= 1e-12)
        {
            // Grey: hue is undefined (reported as 0), saturation is exactly 0.
            return new HslColor(0.0, 0.0, l);
        }

        // S = chroma / (1 - |2L - 1|); the denominator vanishes only for a grey,
        // which the branch above already handled.
        double denominator = 1.0 - Math.Abs(2.0 * l - 1.0);
        double s = denominator <= 1e-12 ? 0.0 : delta / denominator;
        return new HslColor(
            ColorConversion.HueFromRgb(r, g, b, max, delta),
            Math.Clamp(s, 0.0, 1.0),
            l);
    }

    /// <summary>
    /// Converts back to RGB, carrying <paramref name="alpha"/> through unchanged
    /// (HSL has no alpha channel of its own).
    /// </summary>
    public ColorRgb ToRgb(double alpha = 1.0)
    {
        double h = ColorConversion.NormalizeHue(H);
        double s = Math.Clamp(S, 0.0, 1.0);
        double l = Math.Clamp(L, 0.0, 1.0);

        double c = (1.0 - Math.Abs(2.0 * l - 1.0)) * s;
        double hp = h / 60.0;
        double x = c * (1.0 - Math.Abs((hp % 2.0) - 1.0));

        (double r, double g, double b) = hp switch
        {
            >= 0.0 and < 1.0 => (c, x, 0.0),
            >= 1.0 and < 2.0 => (x, c, 0.0),
            >= 2.0 and < 3.0 => (0.0, c, x),
            >= 3.0 and < 4.0 => (0.0, x, c),
            >= 4.0 and < 5.0 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };

        double m = l - c / 2.0;
        return new ColorRgb(r + m, g + m, b + m, alpha);
    }

    /// <summary>A copy with the hue wrapped into [0,360) and S/L clamped into [0,1].</summary>
    public HslColor Normalized()
        => new(ColorConversion.NormalizeHue(H), Math.Clamp(S, 0.0, 1.0), Math.Clamp(L, 0.0, 1.0));
}
