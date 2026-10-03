using VCCad.Core.Model;
using VCCad.Core.Svg;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// **An SVG font declared in the document is drawn with, as well as an OpenType one.**
///
/// `<font>` with a `<font-face>` and one `<glyph>` per character is SVG 1.1's own way of carrying glyph drawings, and
/// the older of the two containers the reader supports. The glyph states the shape and the text element states the
/// paint, so a glyph that names no paint is drawn in the run's own colour - which is what the file means.
///
/// The fixture is written here rather than taken from a corpus because no corpus file uses the inline form; the
/// assertions are the geometry, so a reader that found the font and drew nothing would not pass.
/// </summary>
public class SvgFontElementsTests
{
    [Fact]
    public void AnInlineSVGFontDrawsItsTextAsItsOwnGlyphs()
    {
        SvgImportResult result = SvgReader.Read(InlineFontDocument);

        Assert.Empty(result.Document.AllItems().OfType<TextItem>());

        PathItem[] glyphs = result.Document.AllItems().OfType<PathItem>().ToArray();
        Assert.Equal(2, glyphs.Length);

        // The 'a' drawing is a 500x500 square in a 1000-unit em at font-size 20, so it is 10 points wide and sits
        // from the baseline (y=50) up the page to y=40 - the Y flip that tells a glyph drawing from a shape.
        Rect2D first = glyphs[0].BoundingBox();
        Assert.Equal(10.0, first.Left, 6);
        Assert.Equal(40.0, first.Top, 6);
        Assert.Equal(20.0, first.Right, 6);
        Assert.Equal(50.0, first.Bottom, 6);

        // The second character is placed one advance along: 600 units of the em is 12 points, so its own left edge
        // is 22 - where a reader that ignored the advance would put it on top of the first.
        Rect2D second = glyphs[1].BoundingBox();
        Assert.Equal(22.0, second.Left, 6);
        Assert.Equal(30.0, second.Right, 6);

        // The glyph names no paint, so the run's own colour is what draws it.
        Assert.True(glyphs[0].Fill.IsVisible);
        Assert.Contains(
            result.Warnings, w => w.Contains("drawn as outlines", StringComparison.Ordinal));
    }

    private const string InlineFontDocument =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"200\" height=\"100\">" +
        "<defs><font id=\"inline\" horiz-adv-x=\"1000\">" +
        "<font-face font-family=\"Inline\" units-per-em=\"1000\"/>" +
        "<glyph unicode=\"a\" horiz-adv-x=\"600\" d=\"M 0 0 L 0 500 L 500 500 L 500 0 Z\"/>" +
        "<glyph unicode=\"b\" horiz-adv-x=\"600\" d=\"M 0 0 L 0 400 L 400 400 L 400 0 Z\"/>" +
        "</font></defs>" +
        "<text x=\"10\" y=\"50\" font-size=\"20\" font-family=\"Inline\">ab</text></svg>";
}
