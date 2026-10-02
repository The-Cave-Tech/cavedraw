using VCCad.Core.Model;
using VCCad.Core.Svg;
using VCCad.Core.Text;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// **A `dy` list moves each character across the line, and moves nothing else.**
///
/// SVG states a per-character position with a list on `y`/`dy` (and on `x`/`dx` for a vertical column). The reader
/// used to warn that the model "places a run as a whole" and take the first value, which was accurate: there was no
/// member for the rest. `TextRun.PositionOffsets` is that member now.
///
/// The assertion is **geometry**, because the member existing is not the point - a `dy` list that is stored and
/// never applied is exactly the defect shape this project keeps finding ("data can be stored, round-tripped and
/// asserted perfectly while the step that honours it never runs").
/// </summary>
public class SvgPositionListTests
{
    private const string Head = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"400\" height=\"400\">";

    private static TextItem Block(string body) =>
        SvgReader.Read(Head + body + "</svg>").Document.AllItems().OfType<TextItem>().Single();

    /// <summary>
    /// **The acceptance.** Three characters with `dy="0 5 10"` sit 5 units further down the page than the one
    /// before, in order, while their along-line positions are unchanged - because a `dy` moves a character without
    /// moving where the next one starts.
    /// </summary>
    [Fact]
    public void ADyListLowersEachCharacterInTurnAndLeavesTheAlongLinePositionsAlone()
    {
        TextItem item = Block("<text x=\"10\" y=\"50\" font-size=\"10\" dy=\"0 5 10\">abc</text>");

        Assert.NotNull(item.Runs[0].PositionOffsets);
        Assert.Equal(new[] { 0.0, 5.0, 10.0 }, item.Runs[0].PositionOffsets!);

        TextLayout layout = TextLayoutEngine.Compute(item);
        Assert.Equal(3, layout.Glyphs.Count);

        Assert.Equal(5.0, layout.Glyphs[1].Y - layout.Glyphs[0].Y, 9);
        Assert.Equal(5.0, layout.Glyphs[2].Y - layout.Glyphs[1].Y, 9);

        // **The pen is where the face put it.** A `dy` is a shift, not an advance, so the along-line positions and
        // the advances are the same as the same text with no list at all - which is the assertion that catches a fix
        // that moved the pen instead of the glyph.
        TextLayout plain = TextLayoutEngine.Compute(Block("<text x=\"10\" y=\"50\" font-size=\"10\">abc</text>"));

        Assert.Equal(plain.Glyphs.Select(g => g.Inline), layout.Glyphs.Select(g => g.Inline));
        Assert.Equal(plain.Glyphs.Select(g => g.Advance), layout.Glyphs.Select(g => g.Advance));
    }

    /// <summary>
    /// **A single position still places the whole run, and records no list.** The common case must be untouched:
    /// `dy="5"` moves the block, which the pen already did, and leaves nothing per-character behind.
    /// </summary>
    [Fact]
    public void ASinglePositionRecordsNoList()
    {
        TextItem item = Block("<text x=\"10\" y=\"50\" font-size=\"10\" dy=\"5\">abc</text>");

        Assert.Null(item.Runs[0].PositionOffsets);

        TextLayout layout = TextLayoutEngine.Compute(item);
        Assert.Equal(3, layout.Glyphs.Count);
        Assert.Equal(layout.Glyphs[0].Y, layout.Glyphs[1].Y, 9);
        Assert.Equal(layout.Glyphs[1].Y, layout.Glyphs[2].Y, 9);
    }
}
