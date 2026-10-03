using System.Globalization;
using System.Xml.Linq;
using VCCad.Core.Model;

namespace VCCad.Core.Svg;

/// <summary>
/// The paint a glyph drawing states, read from the attributes and the inline `style` a shape carries.
///
/// Shared by both containers a document can put glyph drawings in - an OpenType programme's `SVG ` documents and the
/// `<glyph>` elements of an SVG font - because the answer has to be the same either way: a glyph that is stroked in
/// one container must not come out filled in the other. Widths stay in **font units** and are scaled by the caller,
/// which is the one place that knows the run's size.
/// </summary>
internal static class SvgGlyphPaint
{
    /// <summary>
    /// The fill and stroke the element states. Anything this cannot read is reported and the shape is left
    /// unpainted rather than given a colour the file never named.
    /// </summary>
    public static (FillSpec Fill, StrokeSpec Stroke) Read(XElement element, Action<string> warn)
    {
        string? fill = Value(element, "fill");
        string? stroke = Value(element, "stroke");
        string? width = Value(element, "stroke-width");

        FillSpec fillSpec;
        if (fill is null || fill.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            fillSpec = FillSpec.None;
        }
        else if (Colour(fill) is { } fillColour)
        {
            fillSpec = FillSpec.Solid(fillColour);
        }
        else
        {
            Unreadable(fill, "fill", warn);
            fillSpec = FillSpec.None;
        }

        StrokeSpec strokeSpec;
        if (stroke is null || stroke.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            strokeSpec = StrokeSpec.None;
        }
        else if (Colour(stroke) is { } strokeColour)
        {
            strokeSpec = StrokeSpec.Hairline(strokeColour) with
            {
                Width = double.TryParse(width, NumberStyles.Float, CultureInfo.InvariantCulture, out double w)
                    ? w
                    : 1.0,
            };
        }
        else
        {
            Unreadable(stroke, "stroke", warn);
            strokeSpec = StrokeSpec.None;
        }

        return (fillSpec, strokeSpec);
    }

    /// <summary>An attribute, or the same property from the element's inline `style`.</summary>
    public static string? Value(XElement element, string property)
        => element.Attribute(property)?.Value ?? Inline(element.Attribute("style")?.Value, property);

    private static void Unreadable(string value, string property, Action<string> warn)
        => warn(
            $"a glyph drawing states {property}=\"{value}\", which this reader cannot read, so the shape is drawn " +
            "unpainted");

    private static string? Inline(string? style, string property)
    {
        if (string.IsNullOrWhiteSpace(style))
        {
            return null;
        }

        foreach (string declaration in style.Split(';'))
        {
            int colon = declaration.IndexOf(':');
            if (colon > 0 && declaration[..colon].Trim().Equals(property, StringComparison.OrdinalIgnoreCase))
            {
                return declaration[(colon + 1)..].Trim();
            }
        }

        return null;
    }

    /// <summary>`#rgb`, `#rrggbb` and SVG's `none`, `black` and `white` - what a glyph drawing actually uses.</summary>
    private static ColorRgb? Colour(string value)
    {
        string text = value.Trim();
        if (text.StartsWith('#'))
        {
            string hex = text[1..];
            if (hex.Length == 3)
            {
                hex = string.Concat(hex.Select(c => new string(c, 2)));
            }

            if (hex.Length == 6 &&
                int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int packed))
            {
                return new ColorRgb((packed >> 16) & 0xFF, (packed >> 8) & 0xFF, packed & 0xFF);
            }

            return null;
        }

        return text.ToLowerInvariant() switch
        {
            "black" => new ColorRgb(0, 0, 0),
            "white" => new ColorRgb(255, 255, 255),
            _ => null,
        };
    }
}
