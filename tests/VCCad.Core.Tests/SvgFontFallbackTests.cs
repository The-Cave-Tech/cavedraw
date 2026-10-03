using VCCad.Core.Model;
using VCCad.Core.Svg;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// **A font's ligatures and its own missing glyph are used** (issue #129's last two items).
///
/// Both were reported rather than honoured: a `unicode` of more than one character is a ligature - one drawing for
/// several characters - and the model looked a character up one at a time, so it could not be found; and a
/// `<missing-glyph>` is the drawing a font supplies for the characters it does not name, which the reader counted as
/// "no drawing" instead of using.
///
/// The assertions are the **geometry**: how many shapes are placed, and which drawing each one is.
/// </summary>
public class SvgFontFallbackTests
{
    private static PathItem[] Glyphs(SvgImportResult result, string font, string text)
        => result.Document.AllItems().OfType<PathItem>()
            .Where(path => path.BoundingBox().Width > 0)
            .ToArray();

    private static string Document(string font, string text)
        => "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"200\" height=\"100\">" +
           "<defs><font id=\"inline\" horiz-adv-x=\"1000\">" + font + "</font></defs>" +
           $"<text x=\"0\" y=\"50\" font-size=\"20\" font-family=\"Inline\">{text}</text></svg>";

    private const string Face = "<font-face font-family=\"Inline\" units-per-em=\"1000\"/>";

    [Fact]
    public void AMissingGlyphIsDrawnForACharacterTheFontDoesNotName()
    {
        // 'a' is a 10-unit box; the missing glyph is a 40-unit one, so the drawing that appears is identifiable.
        SvgImportResult result = SvgReader.Read(Document(
            Face +
            "<glyph unicode=\"a\" horiz-adv-x=\"100\" d=\"M 0 0 L 10 0 L 10 10 L 0 10 Z\"/>" +
            "<missing-glyph horiz-adv-x=\"100\" d=\"M 0 0 L 40 0 L 40 40 L 0 40 Z\"/>",
            "az"));

        PathItem[] shapes = Glyphs(result, "Inline", "az");
        Assert.Equal(2, shapes.Length);
        Assert.Contains(shapes, shape => Math.Abs(shape.BoundingBox().Width - 0.2) < 0.01);   // 'a': 10 units × 0.02
        Assert.Contains(shapes, shape => Math.Abs(shape.BoundingBox().Width - 0.8) < 0.01);   // missing: 40 units

        // And it is **not** counted as a character with no drawing, nor reported as one.
        Assert.DoesNotContain(result.Warnings, warning => warning.Contains("no drawing", StringComparison.Ordinal));
    }

    [Fact]
    public void ALigatureIsOneDrawingForItsWholeSequence()
    {
        // 'a' and 'b' are small boxes; the ligature `ab` is a distinct, wide one.
        SvgImportResult result = SvgReader.Read(Document(
            Face +
            "<glyph unicode=\"a\" horiz-adv-x=\"100\" d=\"M 0 0 L 10 0 L 10 10 L 0 10 Z\"/>" +
            "<glyph unicode=\"b\" horiz-adv-x=\"100\" d=\"M 0 20 L 10 20 L 10 30 L 0 30 Z\"/>" +
            "<glyph unicode=\"ab\" horiz-adv-x=\"200\" d=\"M 0 0 L 60 0 L 60 30 L 0 30 Z\"/>",
            "ab"));

        PathItem[] shapes = Glyphs(result, "Inline", "ab");

        // **One** shape, not two: the ligature covers both characters, and drawing the individual glyphs as well
        // would print the word twice.
        PathItem ligature = Assert.Single(shapes);
        Assert.Equal(1.2, ligature.BoundingBox().Width, 3);   // 60 units × 0.02

        Assert.DoesNotContain(result.Warnings, warning => warning.Contains("ligature", StringComparison.Ordinal));
    }

    /// <summary>**The control**: a font with neither still draws what it names, and reports what it does not.</summary>
    [Fact]
    public void AFontWithNeitherFallsBackToReporting()
    {
        SvgImportResult result = SvgReader.Read(Document(
            Face + "<glyph unicode=\"a\" horiz-adv-x=\"100\" d=\"M 0 0 L 10 0 L 10 10 L 0 10 Z\"/>",
            "az"));

        Assert.Single(Glyphs(result, "Inline", "az"));
        Assert.Contains(
            result.Warnings,
            warning => warning.Contains("no drawing for 1", StringComparison.Ordinal));
    }
}
