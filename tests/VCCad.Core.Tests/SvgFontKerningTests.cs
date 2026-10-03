using VCCad.Core.Model;
using VCCad.Core.Svg;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// **`&lt;hkern&gt;` moves the pen between a pair** (issue #129's last item).
///
/// A kerning pair is not a drawing: it is the advance between two characters, so it is asserted as a **position** -
/// where the second character lands with and without the pair stated - rather than as anything having been read.
/// </summary>
public class SvgFontKerningTests
{
    private const string Face = "<font-face font-family=\"Inline\" units-per-em=\"1000\"/>";

    /// <summary>Two squares of **different widths**, so each glyph can be identified from the geometry itself.</summary>
    private const string Glyphs =
        "<glyph unicode=\"a\" horiz-adv-x=\"600\" d=\"M 0 0 L 10 0 L 10 10 L 0 10 Z\"/>" +
        "<glyph unicode=\"b\" horiz-adv-x=\"600\" d=\"M 0 20 L 30 20 L 30 30 L 0 30 Z\"/>";

    private static (PathItem A, PathItem B) Placed(string hkern)
    {
        SvgImportResult result = SvgReader.Read(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"200\" height=\"100\">" +
            $"<defs><font id=\"inline\" horiz-adv-x=\"600\">{Face}{Glyphs}{hkern}</font></defs>" +
            "<text x=\"0\" y=\"50\" font-size=\"20\" font-family=\"Inline\">ab</text></svg>");

        PathItem[] shapes = result.Document.AllItems().OfType<PathItem>()
            .Where(path => path.BoundingBox().Width > 0)
            .ToArray();

        Assert.Equal(2, shapes.Length);

        // Identified by their own drawings - `a` is the narrow one - rather than by where they sit on a page whose
        // Y axis is flipped, which is how an earlier version of this test had them the wrong way round.
        PathItem a = shapes.Single(shape => Math.Abs(shape.BoundingBox().Width - 0.2) < 0.01);
        PathItem b = shapes.Single(shape => Math.Abs(shape.BoundingBox().Width - 0.6) < 0.01);
        return (a, b);
    }

    [Fact]
    public void AKerningPairPullsTheSecondCharacterCloser()
    {
        (PathItem plainA, PathItem plainB) = Placed(string.Empty);
        double apart = plainB.BoundingBox().Left - plainA.BoundingBox().Left;

        // `k="200"` in a 1000-unit em at font-size 20 is 4pt of advance removed from the pair.
        (PathItem kernedA, PathItem kernedB) = Placed("<hkern u1=\"a\" u2=\"b\" k=\"200\"/>");

        Assert.Equal(apart - 4.0, kernedB.BoundingBox().Left - kernedA.BoundingBox().Left, 3);

        // The first character does not move: kerning is the gap, not the text's position.
        Assert.Equal(plainA.BoundingBox().Left, kernedA.BoundingBox().Left, 3);
    }

    [Fact]
    public void APairTheFontDoesNotNameIsNotMoved()
    {
        // The pair is stated for `a`+`c`, and the text is `a`+`b`: the pen must not move.
        (PathItem plainA, PathItem plainB) = Placed(string.Empty);
        (PathItem otherA, PathItem otherB) = Placed("<hkern u1=\"a\" u2=\"c\" k=\"200\"/>");

        Assert.Equal(
            plainB.BoundingBox().Left - plainA.BoundingBox().Left,
            otherB.BoundingBox().Left - otherA.BoundingBox().Left,
            3);
    }

    [Fact]
    public void BothSidesOfAPairMayListCharacters()
    {
        // `u1="x,a"` and `u2="b"` state the pair for `a`+`b` as well as `x`+`b`; the text uses `a`+`b`.
        (PathItem plainA, PathItem plainB) = Placed(string.Empty);
        (PathItem listedA, PathItem listedB) = Placed("<hkern u1=\"x,a\" u2=\"b\" k=\"100\"/>");

        Assert.Equal(
            (plainB.BoundingBox().Left - plainA.BoundingBox().Left) - 2.0,
            listedB.BoundingBox().Left - listedA.BoundingBox().Left,
            3);
    }

    [Fact]
    public void APairNamedByGlyphIsReportedRatherThanIgnored()
    {
        SvgImportResult result = SvgReader.Read(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"200\" height=\"100\">" +
            $"<defs><font id=\"inline\" horiz-adv-x=\"600\">{Face}{Glyphs}" +
            "<hkern g1=\"a\" g2=\"b\" k=\"200\"/></font></defs>" +
            "<text x=\"0\" y=\"50\" font-size=\"20\" font-family=\"Inline\">ab</text></svg>");

        // No glyph-name table to resolve `g1`/`g2` against, so the pair is not kerned - and that is said out loud
        // rather than being a silent difference from what the file asked for.
        Assert.Contains(
            result.Warnings,
            warning => warning.Contains("glyph name", StringComparison.Ordinal) &&
                warning.Contains("hkern", StringComparison.Ordinal));
    }
}
