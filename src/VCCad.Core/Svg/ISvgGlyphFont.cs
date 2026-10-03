using VCCad.Core.Model;

namespace VCCad.Core.Svg;

/// <summary>
/// One drawing of one glyph: its path data and the paint it states, both in **font units** - the caller scales them
/// and places them, because the caller is the one that knows the run's size and where the baseline is.
/// </summary>
public readonly record struct GlyphShape(string PathData, FillSpec Fill, StrokeSpec Stroke);

/// <summary>
/// A source of glyph drawings a document carries: either an SVG-in-OpenType programme named by `@font-face`, or the
/// `<glyph>` elements an SVG font declares inside the document itself.
///
/// The two are the same fact in different containers - a family name, a table from code point to glyph, a drawing
/// per glyph, and how far the pen moves after one - so the placement in <see cref="SvgGlyphText"/> is written once
/// against this and both sources reach it. The alternative was two copies of "scale out of font units, flip about
/// the baseline, paint with what the drawing states", which is exactly the kind of arithmetic that then disagrees
/// between the two.
/// </summary>
public interface ISvgGlyphFont
{
    /// <summary>The em square the drawings' coordinates are in.</summary>
    int UnitsPerEm { get; }

    /// <summary>The glyph a code point maps to, or 0 when the face has none.</summary>
    int GlyphFor(int codePoint);

    /// <summary>How far the pen moves after a glyph, in font units; 0 when the face states none.</summary>
    int AdvanceFor(int glyphId);

    /// <summary>
    /// The shapes that draw a glyph, in font units. Anything the source cannot draw is reported through
    /// <paramref name="warn"/> rather than dropped.
    /// </summary>
    IReadOnlyList<GlyphShape> Glyphs(int glyphId, Action<string> warn);
}
