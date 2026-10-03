using System.Globalization;
using System.Xml.Linq;
using VCCad.Core.Model;

namespace VCCad.Core.Svg;

/// <summary>
/// An **SVG font declared in the document itself**: a `&lt;font&gt;` element holding a `&lt;font-face&gt;` and one
/// `&lt;glyph&gt;` per character, with the drawing as path data on the glyph rather than in a separate programme.
///
/// This is the other container a file can put glyph definitions in, and it is the older one - SVG 1.1's own font
/// element, deprecated in SVG 2 and the reason a webfont usually arrives as OpenType now. Both containers are
/// supported because a file that carries its glyphs is a file whose picture this reader can draw, and the container
/// it chose is not something the reader should have an opinion about.
///
/// **The coordinates are font units with Y up**, the same as an OpenType programme's SVG table, so the placement in
/// <see cref="SvgGlyphText"/> is the same code: only the lookup and the drawing differ.
///
/// A glyph element usually states **no paint at all** — in SVG 1.1 the glyph supplies the shape and the text element
/// supplies the paint — so a glyph that states none is drawn with the run's own colour, which is what the file means.
/// </summary>
internal sealed class SvgFontElements : ISvgGlyphFont
{
    private readonly Dictionary<int, int> _byCodePoint = new();
    private readonly List<XElement> _glyphs = new();
    private readonly List<int> _advances = new();
    private readonly Action<string> _warn;

    private SvgFontElements(string family, int unitsPerEm, Action<string> warn)
    {
        FamilyName = family;
        UnitsPerEm = unitsPerEm > 0 ? unitsPerEm : 1000;
        _warn = warn;
    }

    /// <summary>The family the `&lt;font-face&gt;` names, which is what a text element's `font-family` matches.</summary>
    public string FamilyName { get; }

    public int UnitsPerEm { get; }

    /// <summary>Every `&lt;font&gt;` a document declares, by the family it names, or empty when it declares none.</summary>
    public static IEnumerable<SvgFontElements> Read(XElement root, Action<string> warn)
    {
        foreach (XElement font in root.DescendantsAndSelf())
        {
            if (font.Name.LocalName != "font")
            {
                continue;
            }

            XElement? face = font.Elements().FirstOrDefault(e => e.Name.LocalName == "font-face");
            string family = face?.Attribute("font-family")?.Value
                ?? font.Attribute("id")?.Value
                ?? string.Empty;
            if (family.Length == 0)
            {
                warn("an SVG font declares no family, so nothing can name it and its glyphs are not used");
                continue;
            }

            int unitsPerEm = Int(face?.Attribute("units-per-em")?.Value) ?? Int(font.Attribute("horiz-adv-x")?.Value) ?? 1000;
            var elements = new SvgFontElements(family, unitsPerEm, warn);
            elements.Collect(font);
            if (elements._glyphs.Count > 0)
            {
                yield return elements;
            }
        }
    }

    public int GlyphFor(int codePoint) => _byCodePoint.TryGetValue(codePoint, out int id) ? id : 0;

    public int AdvanceFor(int glyphId)
        => glyphId > 0 && glyphId <= _advances.Count ? _advances[glyphId - 1] : 0;

    IReadOnlyList<GlyphShape> ISvgGlyphFont.Glyphs(int glyphId, Action<string> warn)
    {
        if (glyphId <= 0 || glyphId > _glyphs.Count)
        {
            return Array.Empty<GlyphShape>();
        }

        XElement glyph = _glyphs[glyphId - 1];
        string data = glyph.Attribute("d")?.Value ?? string.Empty;
        if (data.Length == 0)
        {
            return Array.Empty<GlyphShape>();
        }

        (FillSpec fill, StrokeSpec stroke) = SvgGlyphPaint.Read(glyph, warn);
        return new[] { new GlyphShape(data, fill, stroke) };
    }

    /// <summary>
    /// The glyphs, in document order, and the code point each is named by.
    ///
    /// A `unicode` of more than one character is a **ligature**, which the model has no way to look up - it asks for
    /// one character at a time - so it is reported rather than mapped to a single character's drawing.
    /// </summary>
    private void Collect(XElement font)
    {
        int fontAdvance = Int(font.Attribute("horiz-adv-x")?.Value) ?? 0;
        int ligatures = 0;

        foreach (XElement glyph in font.Descendants())
        {
            if (glyph.Name.LocalName != "glyph")
            {
                continue;
            }

            string unicode = glyph.Attribute("unicode")?.Value ?? string.Empty;
            if (unicode.Length == 0)
            {
                continue;
            }

            if (unicode.Length > 1)
            {
                ligatures++;
                continue;
            }

            _glyphs.Add(glyph);
            _advances.Add(Int(glyph.Attribute("horiz-adv-x")?.Value) ?? fontAdvance);
            _byCodePoint[unicode[0]] = _glyphs.Count;
        }

        if (font.Descendants().Any(e => e.Name.LocalName == "missing-glyph"))
        {
            _warn(
                $"the SVG font '{FamilyName}' declares a <missing-glyph>, and this reader draws no glyph for a "
                + "character the font does not name");
        }

        if (ligatures > 0)
        {
            _warn(
                $"the SVG font '{FamilyName}' names {ligatures} ligatures, and the model looks a character up one "
                + "at a time, so they are not used");
        }

        if (_advances.Any(a => a <= 0))
        {
            _warn(
                $"the SVG font '{FamilyName}' states no advance for some of its glyphs, and those characters are "
                + "placed where the file's own text puts them");
        }
    }

    private static int? Int(string? value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : null;
}
