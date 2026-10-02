using System.Globalization;
using VCCad.Core.Model;

namespace VCCad.Core.Svg;

/// <summary>
/// SVG colour syntax: `#rgb`, `#rrggbb`, `rgb()`, `none`, and the named colours that appear in real files.
///
/// The named list is deliberately the ones a drawing actually uses rather than all 147: a file naming `papayawhip`
/// is not going to be misread as something else if it becomes black, and a table of every name is a table nobody
/// reads. `currentColor` resolves to the inherited colour at the point it is used, which is what makes it
/// inherited at all.
/// </summary>
internal static class SvgColour
{
    private static readonly Dictionary<string, ColorRgb> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["black"] = new(0, 0, 0),
        ["white"] = new(1, 1, 1),
        ["red"] = new(1, 0, 0),
        ["green"] = new(0, 0.5019607843137255, 0),
        ["lime"] = new(0, 1, 0),
        ["blue"] = new(0, 0, 1),
        ["yellow"] = new(1, 1, 0),
        ["cyan"] = new(0, 1, 1),
        ["aqua"] = new(0, 1, 1),
        ["magenta"] = new(1, 0, 1),
        ["fuchsia"] = new(1, 0, 1),
        ["gray"] = new(0.5019607843137255, 0.5019607843137255, 0.5019607843137255),
        ["grey"] = new(0.5019607843137255, 0.5019607843137255, 0.5019607843137255),
        ["silver"] = new(0.7529411764705882, 0.7529411764705882, 0.7529411764705882),
        ["maroon"] = new(0.5019607843137255, 0, 0),
        ["olive"] = new(0.5019607843137255, 0.5019607843137255, 0),
        ["navy"] = new(0, 0, 0.5019607843137255),
        ["teal"] = new(0, 0.5019607843137255, 0.5019607843137255),
        ["purple"] = new(0.5019607843137255, 0, 0.5019607843137255),
        ["orange"] = new(1, 0.6470588235294118, 0),
        ["pink"] = new(1, 0.7529411764705882, 0.796078431372549),
        ["brown"] = new(0.6470588235294118, 0.16470588235294117, 0.16470588235294117),
        ["gold"] = new(1, 0.8431372549019608, 0),
        ["indigo"] = new(0.29411764705882354, 0, 0.5098039215686274),
        ["violet"] = new(0.9333333333333333, 0.5098039215686274, 0.9333333333333333),
        ["darkblue"] = new(0, 0, 0.5450980392156862),
        ["darkgreen"] = new(0, 0.39215686274509803, 0),
        ["darkred"] = new(0.5450980392156862, 0, 0),
        ["darkgray"] = new(0.6627450980392157, 0.6627450980392157, 0.6627450980392157),
        ["lightgray"] = new(0.8274509803921568, 0.8274509803921568, 0.8274509803921568),
        ["lightgrey"] = new(0.8274509803921568, 0.8274509803921568, 0.8274509803921568),
    };

    /// <summary>
    /// Whether a value is SVG's `currentColor` keyword - the one value that is not a colour at all but a reference
    /// to the `color` property in force where it is written.
    ///
    /// It is answered separately from <see cref="Parse"/> because the resolved colour and the *fact that it was the
    /// keyword* are two different things, and the model records both (issue #135).
    /// </summary>
    public static bool IsCurrentColor(string value)
        => value.Trim().Equals("currentColor", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The colour, with `currentColor` standing for <paramref name="inForce"/> - the `color` property where the
    /// value was written. Null when it is a form this reader does not know.
    /// </summary>
    public static ColorRgb? Parse(string value, ColorRgb inForce)
        => IsCurrentColor(value) ? inForce : Parse(value);

    /// <summary>The colour, or null when it is a form this reader does not know.</summary>
    public static ColorRgb? Parse(string value)
    {
        string text = value.Trim();
        if (text.Length == 0)
        {
            return null;
        }

        if (text[0] == '#')
        {
            return ParseHex(text[1..]);
        }

        if (text.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
        {
            return ParseRgbFunction(text);
        }

        return Named.TryGetValue(text, out ColorRgb named) ? named : null;
    }

    private static ColorRgb? ParseHex(string hex)
    {
        hex = hex.Trim();
        if (hex.Length == 3)
        {
            // #abc is #aabbcc, each digit doubled rather than padded: reading it as 0xa0b0c0 is a different colour.
            hex = new string(new[] { hex[0], hex[0], hex[1], hex[1], hex[2], hex[2] });
        }

        if (hex.Length != 6 || !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int value))
        {
            return null;
        }

        return new ColorRgb(
            ((value >> 16) & 0xFF) / 255.0,
            ((value >> 8) & 0xFF) / 255.0,
            (value & 0xFF) / 255.0);
    }

    private static ColorRgb? ParseRgbFunction(string text)
    {
        int open = text.IndexOf('(');
        int close = text.IndexOf(')');
        if (open < 0 || close < open)
        {
            return null;
        }

        string[] parts = text[(open + 1)..close]
            .Split(new[] { ',', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            return null;
        }

        var channels = new double[3];
        for (int i = 0; i < 3; i++)
        {
            bool percent = parts[i].EndsWith('%');
            string number = percent ? parts[i][..^1] : parts[i];
            if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
            {
                return null;
            }

            channels[i] = Math.Clamp(percent ? parsed / 100.0 : parsed / 255.0, 0.0, 1.0);
        }

        return new ColorRgb(channels[0], channels[1], channels[2]);
    }
}
