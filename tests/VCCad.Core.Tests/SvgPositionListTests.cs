using VCCad.Core.Model;
using VCCad.Core.Serialization;
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

    /// <summary>
    /// **The offsets survive the lossless sidecar, and still move the glyphs after coming back.**
    ///
    /// The member being honoured by the layout is not enough on its own: a document is stored as JSON, and a member
    /// the serializer does not write is a per-character position the person loses the moment the document is saved
    /// and reopened - the file stays in the document, the layout geometry quietly goes back to the plain one. The
    /// assertion is therefore both halves: the list is in the bytes for a run that has one, absent for a run that
    /// does not, and the geometry after the round trip is the geometry before it.
    /// </summary>
    [Fact]
    public void TheOffsetsSurviveTheSidecar()
    {
        CadDocument document = Read("dy=\"0 5 10\"");
        string json = VccadDocumentSerializer.Serialize(document);

        Assert.Contains("PositionOffsets", json, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "PositionOffsets",
            VccadDocumentSerializer.Serialize(Read(string.Empty)),
            StringComparison.Ordinal);

        TextItem reloaded = VccadDocumentSerializer.Deserialize(json).AllItems().OfType<TextItem>().Single();
        Assert.Equal(new[] { 0.0, 5.0, 10.0 }, reloaded.Runs[0].PositionOffsets!);

        TextLayout layout = TextLayoutEngine.Compute(reloaded);
        Assert.Equal(3, layout.Glyphs.Count);
        Assert.Equal(5.0, layout.Glyphs[1].Y - layout.Glyphs[0].Y, 9);
        Assert.Equal(5.0, layout.Glyphs[2].Y - layout.Glyphs[1].Y, 9);
    }

    /// <summary>
    /// **A list's first value places the first character once, not twice.**
    ///
    /// The same attribute is read twice - as a single value that places the piece, and as the whole list, whose
    /// entries are how far each character strays from that. Adding the single value to the pen *and* keeping it as
    /// the first character's own offset moves the block down by it and then moves its first character down again,
    /// so a `dy="5 10"` put the run 5 units too low. The test above uses a list whose first entry is `0`, which is
    /// exactly why this one is needed: the defect is invisible when the first value happens to be nothing.
    /// </summary>
    [Fact]
    public void AListsFirstValuePlacesItsFirstCharacterOnce()
    {
        TextItem shifted = Block("<text x=\"10\" y=\"50\" font-size=\"10\" dy=\"5 10\">ab</text>");

        Assert.Equal(new[] { 5.0, 10.0 }, shifted.Runs[0].PositionOffsets!);

        // The same file with a list of zeros is where the characters sit when the list moves them by nothing, so it
        // is the baseline every offset is measured from - and it needs no knowledge of the face, because both blocks
        // are the same text placed the same way.
        TextItem zeroed = Block("<text x=\"10\" y=\"50\" font-size=\"10\" dy=\"0 0\">ab</text>");
        TextLayout plain = TextLayoutEngine.Compute(zeroed);
        TextLayout layout = TextLayoutEngine.Compute(shifted);

        double Base(int index) => zeroed.Origin.Y + plain.Glyphs[index].Y;

        Assert.Equal(2, layout.Glyphs.Count);
        Assert.Equal(Base(0) + 5.0, shifted.Origin.Y + layout.Glyphs[0].Y, 9);
        Assert.Equal(Base(1) + 10.0, shifted.Origin.Y + layout.Glyphs[1].Y, 9);
    }

    /// <summary>The imported document for `abc` with whatever positioning attribute the caller adds.</summary>
    private static CadDocument Read(string attribute)
        => SvgReader.Read($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"400\" height=\"400\">" +
                          $"<text x=\"10\" y=\"50\" font-size=\"10\" {attribute}>abc</text></svg>").Document;

    /// <summary>
    /// **The list survives the SVG round trip, geometry and all.**
    ///
    /// The writer stated no per-character offset at all, so a document read from a file and written back out came
    /// home with every character on the plain baseline - the round trip is supposed to be the one place fidelity is
    /// proved, and it was the place this was lost. The assertion is the geometry after a write and a read: the same
    /// differences between the glyphs as the original laid out.
    /// </summary>
    [Fact]
    public void TheOffsetsSurviveTheSvgRoundTrip()
    {
        TextItem before = Block("<text x=\"10\" y=\"50\" font-size=\"10\" dy=\"5 10\">abc</text>");
        TextLayout original = TextLayoutEngine.Compute(before);

        CadDocument document = Read("dy=\"5 10\"");
        string svg = SvgWriter.Write(document);
        Assert.Contains("dy=", svg, StringComparison.Ordinal);

        TextItem after = SvgReader.Read(svg).Document.AllItems().OfType<TextItem>().Single();
        Assert.Equal(new[] { 5.0, 10.0 }, after.Runs[0].PositionOffsets!);

        TextLayout again = TextLayoutEngine.Compute(after);
        Assert.Equal(3, again.Glyphs.Count);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(original.Glyphs[i].Y, again.Glyphs[i].Y, 9);
            Assert.Equal(original.Glyphs[i].X, again.Glyphs[i].X, 9);
        }
    }
}
