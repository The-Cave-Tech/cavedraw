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

    /// <summary>A glyph named by a **sequence** of characters - a ligature, `unicode="fi"`.</summary>
    private readonly Dictionary<string, int> _bySequence = new(StringComparer.Ordinal);

    /// <summary>The glyph a font declares for characters it does not name, or 0 when it declares none.</summary>
    private int _missingGlyph;

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

    /// <summary>
    /// The glyph a character maps to: what the font names for it, the font's own
    /// <c>&lt;missing-glyph&gt;</c> when it names nothing and declares one, or 0.
    ///
    /// Returning the missing glyph rather than 0 is what makes a font's own fallback drawing *used*: the caller
    /// counts a 0 as a character with no drawing and reports it, and a font that supplies a box for its unnamed
    /// characters is saying it has one.
    /// </summary>
    public int GlyphFor(int codePoint)
        => _byCodePoint.TryGetValue(codePoint, out int id) ? id : _missingGlyph;

    /// <summary>
    /// The glyph a whole **sequence** names - a ligature, `unicode="fi"` - or 0 when the font has none.
    ///
    /// A ligature is one drawing for several characters, so the caller has to ask before it asks for the first
    /// character on its own.
    /// </summary>
    public int GlyphForSequence(string sequence)
        => sequence.Length > 1 && _bySequence.TryGetValue(sequence, out int id) ? id : 0;

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
    /// The glyphs, in document order, with the character each single one is named by, the **sequence** each ligature
    /// is named by, and the font's own missing-glyph when it declares one.
    ///
    /// A `unicode` of more than one character is a ligature: one drawing for several characters. It is registered
    /// against the whole sequence, because that is how it has to be looked up - asking for the first character alone
    /// would find a single-character glyph or nothing.
    /// </summary>
    private void Collect(XElement font)
    {
        int fontAdvance = Int(font.Attribute("horiz-adv-x")?.Value) ?? 0;

        foreach (XElement glyph in font.Descendants())
        {
            if (glyph.Name.LocalName is not ("glyph" or "missing-glyph"))
            {
                continue;
            }

            bool missing = glyph.Name.LocalName == "missing-glyph";
            string unicode = glyph.Attribute("unicode")?.Value ?? string.Empty;
            if (!missing && unicode.Length == 0)
            {
                continue;
            }

            _glyphs.Add(glyph);
            _advances.Add(Int(glyph.Attribute("horiz-adv-x")?.Value) ?? fontAdvance);
            int id = _glyphs.Count;

            if (missing)
            {
                _missingGlyph = id;
            }
            else if (unicode.Length == 1)
            {
                // A font may map one drawing to several code points; the first name wins, as it did before.
                _byCodePoint.TryAdd(unicode[0], id);
            }
            else
            {
                _bySequence.TryAdd(unicode, id);
            }
        }

        if (_advances.Any(advance => advance <= 0))
        {
            _warn(
                $"the SVG font '{FamilyName}' states no advance for some of its glyphs, and those characters are "
                + "placed where the file's own text puts them");
        }
    }

    private static int? Int(string? value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : null;
}
