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

    /// <summary>A measurer that sets every character at one width, so the geometry below is arithmetic.</summary>
    private sealed class FixedAdvance(double advance) : ITextMetrics
    {
        public IReadOnlyList<double> Advances(TextRun run)
            => Enumerable.Repeat(advance, run.Text.Length).ToArray();

        public double Ascent(TextRun run) => run.FontSize * 0.8;

        public double Descent(TextRun run) => run.FontSize * 0.2;
    }

    private static void Measured(double advance, Action body)
    {
        ITextMetrics? previous = TextMeasurement.Current;
        try
        {
            TextMeasurement.Current = new FixedAdvance(advance);
            body();
        }
        finally
        {
            TextMeasurement.Current = previous;
        }
    }

    /// <summary>
    /// **A right-to-left line's places run the other way.** SVG states a place per character in the file's own order,
    /// and under `rtl` the first character is the right-hand one - so a list of 40, 30, 20 puts character 0 at 40 and
    /// the last at 20. The layout walks a line in visual order, so a fix that applied the list in the order the pen
    /// reaches the characters would draw the string mirrored.
    ///
    /// What makes it work is the same thing that makes the forward case work: a run that states its own places is
    /// placed **absolutely**, so its origin is the leftmost place it names rather than a width measured from its
    /// first value. For a forward line that leftmost place *is* the first value, which is why one rule covers both.
    /// </summary>
    [Fact]
    public void AnRtlAlongListPlacesTheCharactersItNames() => Measured(6, () =>
    {
        TextItem item = Block("<text x=\"40 30 20\" y=\"10\" font-size=\"10\" direction=\"rtl\">abc</text>");
        TextLayout layout = TextLayoutEngine.Compute(item);

        double PageX(int logical) => item.Origin.X + layout.Glyphs.Single(g => g.Index == logical).X;

        Assert.Equal(3, layout.Glyphs.Count);
        Assert.Equal(40.0, PageX(0), 9);
        Assert.Equal(30.0, PageX(1), 9);
        Assert.Equal(20.0, PageX(2), 9);

        // A placed run's advances run the way the pen walks it, so every one of them is a distance and none is
        // negative - a negative advance makes the run's own box, its caret and its far edge nonsense.
        Assert.All(layout.Glyphs, g => Assert.True(g.Advance >= 0, $"advance {g.Advance} is negative"));
    });

    /// <summary>
    /// **A right-to-left list survives the round trip.** The numbers come back in the file's own order - character 0
    /// is the right-hand place - so a writer that emitted the model's own frame would hand back a mirrored line.
    /// </summary>
    [Fact]
    public void AnRtlAlongListSurvivesTheSvgRoundTrip() => Measured(6, () =>
    {
        const string source =
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"400\" height=\"400\">" +
            "<text x=\"40 30 20\" y=\"10\" font-size=\"10\" direction=\"rtl\">abc</text></svg>";

        CadDocument document = SvgReader.Read(source).Document;
        TextItem before = document.AllItems().OfType<TextItem>().Single();
        TextLayout original = TextLayoutEngine.Compute(before);

        TextItem after = SvgReader.Read(SvgWriter.Write(document)).Document
            .AllItems().OfType<TextItem>().Single();
        TextLayout again = TextLayoutEngine.Compute(after);

        Assert.Equal(3, again.Glyphs.Count);
        for (int logical = 0; logical < 3; logical++)
        {
            Assert.Equal(
                before.Origin.X + original.Glyphs.Single(g => g.Index == logical).X,
                after.Origin.X + again.Glyphs.Single(g => g.Index == logical).X,
                6);
        }
    });

    /// <summary>
    /// **The acceptance.** Three characters with `dy="0 5 10"` sit progressively further down the page, while their
    /// along-line positions are unchanged - because a `dy` moves a character without moving where the next one starts.
    ///
    /// **The steps accumulate, which is SVG's rule and this member's own contract.** SVG 1.1 states a `dx`/`dy` list
    /// as offsets applied *in turn*: each entry moves the pen from where the previous one left it, so `0 5 10` puts
    /// the characters at 0, 5 and **15** below the baseline - not 0, 5 and 10. `TextRun.PositionOffsets` says it is
    /// "how far each character strays from `Cross`", which is exactly those places; reading the list as bare places
    /// made the member mean something other than its own documentation, and only ever showed on the third entry.
    ///
    /// This assertion previously pinned the reading in the list, so it changed deliberately when the code was
    /// corrected rather than being bent to fit it - and it failed against the old reading before the change was made.
    /// </summary>
    [Fact]
    public void ADyListLowersEachCharacterInTurnAndLeavesTheAlongLinePositionsAlone()
    {
        TextItem item = Block("<text x=\"10\" y=\"50\" font-size=\"10\" dy=\"0 5 10\">abc</text>");

        Assert.NotNull(item.Runs[0].PositionOffsets);
        Assert.Equal(new[] { 0.0, 5.0, 15.0 }, item.Runs[0].PositionOffsets!);

        TextLayout layout = TextLayoutEngine.Compute(item);
        Assert.Equal(3, layout.Glyphs.Count);

        Assert.Equal(5.0, layout.Glyphs[1].Y - layout.Glyphs[0].Y, 9);
        Assert.Equal(10.0, layout.Glyphs[2].Y - layout.Glyphs[1].Y, 9);

        // **The pen is where the face put it.** A `dy` is a shift, not an advance, so the along-line positions and
        // the advances are the same as the same text with no list at all - which is the assertion that catches a fix
        // that moved the pen instead of the glyph.
        TextLayout plain = TextLayoutEngine.Compute(Block("<text x=\"10\" y=\"50\" font-size=\"10\">abc</text>"));

        Assert.Equal(plain.Glyphs.Select(g => g.Inline), layout.Glyphs.Select(g => g.Inline));
        Assert.Equal(plain.Glyphs.Select(g => g.Advance), layout.Glyphs.Select(g => g.Advance));
    }

    /// <summary>
    /// **An absolute list across the line is kept too.** `y="50 60 70"` is the other way SVG states one place per
    /// character, and the reader used to take its first value and ignore the rest - so a file that positions each
    /// glyph exactly was flattened onto one baseline. Its places are absolute, so they are rebased on where the
    /// piece's own baseline is, exactly as the along axis rebases its own.
    /// </summary>
    [Fact]
    public void AnAcrossListOnTheAbsoluteAttributeIsKept()
    {
        TextItem item = Block("<text x=\"10\" y=\"50 60 70\" font-size=\"10\">abc</text>");

        Assert.Equal(new[] { 0.0, 10.0, 20.0 }, item.Runs[0].PositionOffsets!);

        TextLayout layout = TextLayoutEngine.Compute(item);
        Assert.Equal(3, layout.Glyphs.Count);
        Assert.Equal(10.0, layout.Glyphs[1].Y - layout.Glyphs[0].Y, 9);
        Assert.Equal(10.0, layout.Glyphs[2].Y - layout.Glyphs[1].Y, 9);
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
        Assert.Equal(new[] { 0.0, 5.0, 15.0 }, reloaded.Runs[0].PositionOffsets!);

        TextLayout layout = TextLayoutEngine.Compute(reloaded);
        Assert.Equal(3, layout.Glyphs.Count);
        Assert.Equal(5.0, layout.Glyphs[1].Y - layout.Glyphs[0].Y, 9);
        Assert.Equal(10.0, layout.Glyphs[2].Y - layout.Glyphs[1].Y, 9);
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

        Assert.Equal(new[] { 5.0, 15.0 }, shifted.Runs[0].PositionOffsets!);

        // The same file with a list of zeros is where the characters sit when the list moves them by nothing, so it
        // is the baseline every offset is measured from - and it needs no knowledge of the face, because both blocks
        // are the same text placed the same way.
        TextItem zeroed = Block("<text x=\"10\" y=\"50\" font-size=\"10\" dy=\"0 0\">ab</text>");
        TextLayout plain = TextLayoutEngine.Compute(zeroed);
        TextLayout layout = TextLayoutEngine.Compute(shifted);

        double Base(int index) => zeroed.Origin.Y + plain.Glyphs[index].Y;

        Assert.Equal(2, layout.Glyphs.Count);
        Assert.Equal(Base(0) + 5.0, shifted.Origin.Y + layout.Glyphs[0].Y, 9);
        Assert.Equal(Base(1) + 15.0, shifted.Origin.Y + layout.Glyphs[1].Y, 9);
    }

    /// <summary>
    /// **An `x` list produces exactly the places the file states, even where they contradict the advances.**
    ///
    /// This is the acceptance the issue names for the along axis: the characters are not where a face would put them
    /// - they are where the file put them - and the first entry is what places the block. Three characters at `x`
    /// 1, 2 and 3 sit at page 1, 2 and 3 whatever the face measures.
    /// </summary>
    [Fact]
    public void AnXListPlacesTheCharactersWhereTheFileSays()
    {
        TextItem item = Block("<text x=\"1 2 3\" y=\"10\" font-size=\"10\">abc</text>");

        Assert.Equal(new[] { 0.0, 1.0, 2.0 }, item.Runs[0].InlineOffsets!);

        TextLayout layout = TextLayoutEngine.Compute(item);
        Assert.Equal(3, layout.Glyphs.Count);
        Assert.Equal(1.0, item.Origin.X + layout.Glyphs[0].X, 9);
        Assert.Equal(2.0, item.Origin.X + layout.Glyphs[1].X, 9);
        Assert.Equal(3.0, item.Origin.X + layout.Glyphs[2].X, 9);
    }

    /// <summary>
    /// **A `dx` list is a running shift, which is SVG's rule for it.** Each entry moves the pen from where the last
    /// one left it, so `dx="1 2"` puts the first character one along and the second three along - not one and two,
    /// which is what reading the list as bare places would give.
    /// </summary>
    [Fact]
    public void ADxListAccumulates()
    {
        TextItem item = Block("<text x=\"0\" y=\"10\" font-size=\"10\" dx=\"1 2\">ab</text>");

        Assert.Equal(new[] { 0.0, 2.0 }, item.Runs[0].InlineOffsets!);

        TextLayout layout = TextLayoutEngine.Compute(item);
        Assert.Equal(2, layout.Glyphs.Count);
        Assert.Equal(1.0, item.Origin.X + layout.Glyphs[0].X, 9);
        Assert.Equal(3.0, item.Origin.X + layout.Glyphs[1].X, 9);
    }

    /// <summary>
    /// **A list shorter than the run applies where it reaches.** Two places for three characters leaves the third
    /// where the face puts it - past the last place - rather than repeating the last entry and stacking two
    /// characters on one spot.
    /// </summary>
    [Fact]
    public void AShortAlongListAppliesWhereItReaches()
    {
        TextItem item = Block("<text x=\"1 5\" y=\"10\" font-size=\"10\">abc</text>");

        Assert.Equal(new[] { 0.0, 4.0 }, item.Runs[0].InlineOffsets!);

        TextLayout layout = TextLayoutEngine.Compute(item);
        Assert.Equal(1.0, item.Origin.X + layout.Glyphs[0].X, 9);
        Assert.Equal(5.0, item.Origin.X + layout.Glyphs[1].X, 9);
        Assert.True(
            item.Origin.X + layout.Glyphs[2].X > 5.0,
            "the character past the list continues from the last place rather than stacking on it");
    }

    /// <summary>
    /// **The along list survives the SVG round trip, as a list.** The writer stated the run's own single `x`, so a
    /// document read from a file and written back out came home with every character where the face put it. The
    /// assertion is the list in the written element and the geometry after a write and a read.
    /// </summary>
    [Fact]
    public void TheAlongListSurvivesTheSvgRoundTrip()
    {
        const string source =
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"400\" height=\"400\">" +
            "<text x=\"1 5 9\" y=\"10\" font-size=\"10\">abc</text></svg>";

        CadDocument document = SvgReader.Read(source).Document;
        TextItem before = document.AllItems().OfType<TextItem>().Single();
        TextLayout original = TextLayoutEngine.Compute(before);

        string svg = SvgWriter.Write(document);

        string? written = System.Xml.Linq.XDocument.Parse(svg)
            .Descendants()
            .First(e => e.Name.LocalName == "tspan")
            .Attribute("x")?.Value;
        Assert.NotNull(written);
        Assert.Equal(3, written!.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);

        TextItem after = SvgReader.Read(svg).Document.AllItems().OfType<TextItem>().Single();
        Assert.Equal(new[] { 0.0, 4.0, 8.0 }, after.Runs[0].InlineOffsets!);

        TextLayout again = TextLayoutEngine.Compute(after);
        Assert.Equal(3, again.Glyphs.Count);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(
                original.Glyphs[i].X - original.Glyphs[0].X,
                again.Glyphs[i].X - again.Glyphs[0].X,
                9);
        }
    }

    /// <summary>
    /// **An accumulated list survives the round trip, geometry and all.** The model holds places while the file
    /// states steps, so the writer has to turn one into the other: writing the places as though they were steps adds
    /// them up again on the way back in, and every character after the first drifts by the one before it here.
    /// </summary>
    [Fact]
    public void AnAccumulatedAcrossListSurvivesTheSvgRoundTrip()
    {
        const string source =
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"400\" height=\"400\">" +
            "<text x=\"10\" y=\"50\" font-size=\"10\" dy=\"5 10\">abc</text></svg>";

        CadDocument document = SvgReader.Read(source).Document;
        TextItem before = document.AllItems().OfType<TextItem>().Single();
        TextLayout original = TextLayoutEngine.Compute(before);

        Assert.Equal(new[] { 5.0, 15.0 }, before.Runs[0].PositionOffsets!);

        TextItem after = SvgReader.Read(SvgWriter.Write(document)).Document
            .AllItems().OfType<TextItem>().Single();
        TextLayout again = TextLayoutEngine.Compute(after);

        Assert.Equal(new[] { 5.0, 15.0 }, after.Runs[0].PositionOffsets!);
        Assert.Equal(3, again.Glyphs.Count);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(original.Glyphs[i].Y - original.Glyphs[0].Y, again.Glyphs[i].Y - again.Glyphs[0].Y, 9);
        }
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
        Assert.Equal(new[] { 5.0, 15.0 }, after.Runs[0].PositionOffsets!);

        TextLayout again = TextLayoutEngine.Compute(after);
        Assert.Equal(3, again.Glyphs.Count);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(original.Glyphs[i].Y, again.Glyphs[i].Y, 9);
            Assert.Equal(original.Glyphs[i].X, again.Glyphs[i].X, 9);
        }
    }
}
