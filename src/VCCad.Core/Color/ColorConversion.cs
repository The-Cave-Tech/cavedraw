namespace VCCad.Core.Color;

/// <summary>
/// Numeric helpers shared by the colour-space conversions.
///
/// Everything colour-related goes through these so that "wrapped hue" and
/// "channel byte" have exactly one definition. The picker rotates the triangle
/// through a full turn many times per drag; a hue that is normalised
/// inconsistently (or that carries <c>-0.0</c>) drifts visibly over a session,
/// which is why the wrap is centralised here.
/// </summary>
internal static class ColorConversion
{
    /// <summary>
    /// Wraps a hue/angle in degrees into [0, 360). Negative inputs wrap the
    /// "long way" to the same physical direction, and exact multiples of a full
    /// turn collapse to +0.0 rather than -0.0 so round trips compare equal.
    /// </summary>
    internal static double NormalizeHue(double hue)
    {
        double wrapped = hue % 360.0;
        if (wrapped < 0.0)
        {
            wrapped += 360.0;
        }

        // `-0.0 % 360` is `-0.0`, which is equal to 0.0 but formats and hashes
        // differently; collapse it so a full turn is indistinguishable from zero.
        return wrapped == 0.0 ? 0.0 : wrapped;
    }

    /// <summary>
    /// The hue, in degrees, of an RGB triple whose maximum channel is
    /// <paramref name="max"/> and whose chroma is <paramref name="delta"/>.
    /// Achromatic input (zero chroma) has no hue; 0 is returned.
    /// </summary>
    internal static double HueFromRgb(double r, double g, double b, double max, double delta)
    {
        if (delta <= 1e-12)
        {
            return 0.0;
        }

        double hue;
        if (max == r)
        {
            hue = 60.0 * (((g - b) / delta) % 6.0);
        }
        else if (max == g)
        {
            hue = 60.0 * ((b - r) / delta + 2.0);
        }
        else
        {
            hue = 60.0 * ((r - g) / delta + 4.0);
        }

        return NormalizeHue(hue);
    }

    /// <summary>
    /// Quantises a normalised [0,1] channel to a byte, rounding half away from
    /// zero so 0.5 → 128 and 0.5/255 steps are stable when a colour makes the
    /// round trip through a hex string.
    /// </summary>
    internal static byte ByteFromUnit(double value)
        => (byte)Math.Round(Math.Clamp(value, 0.0, 1.0) * 255.0, MidpointRounding.AwayFromZero);

    /// <summary>Normalised [0,1] channel for a byte.</summary>
    internal static double UnitFromByte(byte value) => value / 255.0;
}
