using System.Text;
using System.Text.Json.Nodes;
using VCCad.Core.Model;
using VCCad.Core.Serialization;
using VCCad.Core.Svg;
using VCCad.Core.Text;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// The text properties #147 names: per-run spacing, per-run colour, and the face's width and variant.
///
/// #125 read SVG text and **reported** these, because the model had nowhere to keep them and a member the reader
/// sets but the sidecar drops is lost on the next save. These are the assertions that replace those reports: the
/// field arrives on the <see cref="TextRun"/>, the layout uses it, the sidecar carries it, and the file's own
/// spelling of the face's width and variant is kept so a value the model does not act on is not quietly dropped
/// either.
///
/// Every case is written as the file's value and the model's value, because "a warning appeared" cannot tell a
/// property that was kept from one that was merely mentioned.
/// </summary>
public class SvgTextModelExtensionTests
{
    private static readonly string Header =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"200\" height=\"100\" viewBox=\"0 0 200 100\">";

    private static SvgImportResult Read(string body) => SvgReader.Read(Header + body + "</svg>");

    private static TextItem Block(SvgImportResult result)
        => Assert.Single(result.Document.AllItems().OfType<TextItem>());

    /// <summary>A measurer that sets every character at one width, so a position is arithmetic and not a font.</summary>
    private sealed class FixedAdvance(double advance) : ITextMetrics
    {
        public IReadOnlyList<double> Advances(TextRun run)
            => Enumerable.Repeat(advance, run.Text.Length).ToArray();

        public double Ascent(TextRun run) => run.FontSize * 0.8;

        public double Descent(TextRun run) => run.FontSize * 0.2;
    }

    private static T Measured<T>(double advance, Func<T> body)
    {
        ITextMetrics? previous = TextMeasurement.Current;
        try
        {
            TextMeasurement.Current = new FixedAdvance(advance);
            return body();
        }
        finally
        {
            TextMeasurement.Current = previous;
        }
    }

    /// <summary>
    /// The same, for a test whose assertions measure the model.
    ///
    /// Bounds and anchoring are read back through the measurer, so a test that installed one only for the import and
    /// then asserted on a width would be measuring against the estimate instead - and would fail for a reason that
    /// has nothing to do with the file.
    /// </summary>
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

    /// <summary>Runs <paramref name="body"/> with a fixed measurer, for a test whose assertions only measure.</summary>
    private static void AssertMeasured(double advance, Action body) => Measured(advance, body);

    // ---------------------------------------------------------------- letter and word spacing

    /// <summary>
    /// **`letter-spacing` is kept as tracking, and it is not the run's advance.** The distinction is the issue's
    /// own: widening the advance stretches the glyphs, and a file that asked for room between letters wants the
    /// glyphs unchanged. Three characters eight units wide with two units of tracking are twenty wide, and the
    /// run the file states still carries no advance of its own.
    /// </summary>
    [Fact]
    public void ALetterSpacingBecomesTheRunsTrackingAndNotAWiderAdvance()
    {
        SvgImportResult result = Measured(4.0, () => Read(
            "<text x=\"0\" y=\"20\" font-size=\"10\" letter-spacing=\"2\">abc</text>"));

        TextItem item = Block(result);
        TextRun run = Assert.Single(item.Runs);

        Assert.Equal(2.0, run.LetterSpacing, 9);
        Assert.Equal(0.0, run.WordSpacing, 9);

        // Nothing follows the run, so the file states no advance for it - and tracking must not become one.
        Assert.Null(run.AdvanceWidth);

        // And the tracking is real layout: four units a character plus two between them is eighteen.
        AssertMeasured(4.0, () => Assert.Equal(18.0, TextLayoutEngine.Compute(item).Width, 9));
    }

    /// <summary>
    /// **`word-spacing` lands on the spaces and on nothing else.** "a b" is three glyph advances plus one word
    /// space: three fours and a three, fifteen.
    /// </summary>
    [Fact]
    public void AWordSpacingWidensTheSpacesAndOnlyTheSpaces()
    {
        SvgImportResult result = Measured(4.0, () => Read(
            "<text x=\"0\" y=\"20\" font-size=\"10\" word-spacing=\"3\">a b</text>"));

        TextItem item = Block(result);
        TextRun run = Assert.Single(item.Runs);

        Assert.Equal(0.0, run.LetterSpacing, 9);
        Assert.Equal(3.0, run.WordSpacing, 9);
        AssertMeasured(4.0, () => Assert.Equal(15.0, TextLayoutEngine.Compute(item).Width, 9));
    }

    /// <summary>
    /// **The two add up where both apply.** "a b c" is five glyph advances, two word spaces and five units of
    /// tracking: twenty plus five plus six is thirty-one.
    /// </summary>
    [Fact]
    public void LetterAndWordSpacingAddUpOnTheSameRun()
    {
        SvgImportResult result = Measured(4.0, () => Read(
            "<text x=\"0\" y=\"20\" font-size=\"10\" letter-spacing=\"1\" word-spacing=\"3\">a b c</text>"));

        TextItem item = Block(result);
        TextRun run = Assert.Single(item.Runs);

        Assert.Equal(1.0, run.LetterSpacing, 9);
        Assert.Equal(3.0, run.WordSpacing, 9);
        AssertMeasured(4.0, () => Assert.Equal(31.0, TextLayoutEngine.Compute(item).Width, 9));
    }

    /// <summary>
    /// **A relative spacing is resolved against the run's own size on the way in**, because what is kept is a
    /// length: ten per cent of a twenty-unit em is two, and ten per cent of ten is one.
    /// </summary>
    [Fact]
    public void APercentageSpacingResolvesAgainstTheRunsOwnSize()
    {
        SvgImportResult result = Read(
            "<text x=\"0\" y=\"20\" font-size=\"20\" letter-spacing=\"10%\" word-spacing=\"-5%\">a b</text>" +
            "<text x=\"0\" y=\"60\" font-size=\"10\" letter-spacing=\"10%\">ab</text>");

        TextItem[] blocks = result.Document.AllItems().OfType<TextItem>().ToArray();
        Assert.Equal(2, blocks.Length);

        Assert.Equal(2.0, blocks[0].Runs[0].LetterSpacing, 9);
        Assert.Equal(-1.0, blocks[0].Runs[0].WordSpacing, 9);
        Assert.Equal(1.0, blocks[1].Runs[0].LetterSpacing, 9);
    }

    /// <summary>
    /// **The tracking reaches the caret, not only the bounds.** A caret between the first and second character of
    /// a tracked run sits where the file's room puts it, so the edit box and the text cannot disagree.
    /// </summary>
    [Fact]
    public void TheTrackingMovesTheCaretWithTheGlyphs()
    {
        SvgImportResult result = Read(
            "<text x=\"0\" y=\"20\" font-size=\"10\" letter-spacing=\"2\">abc</text>");

        TextItem item = Block(result);
        Measured(4.0, () =>
        {
            TextLayout layout = TextLayoutEngine.Compute(item);

            // Four units of "a" plus its two of tracking.
            Assert.Equal(6.0, layout.XOf(1), 9);
            Assert.Equal(12.0, layout.XOf(2), 9);
        });
    }

    /// <summary>
    /// **A run with no spacing of its own is unaffected.** The initial value is zero and adds nothing, which is
    /// what keeps every document and every other test in this suite measuring exactly as it did.
    /// </summary>
    [Fact]
    public void ARunWithNoSpacingMeasuresAsTheFaceItself()
    {
        SvgImportResult result = Read("<text x=\"0\" y=\"20\" font-size=\"10\">abc</text>");

        TextItem item = Block(result);
        Assert.Equal(0.0, Assert.Single(item.Runs).LetterSpacing, 9);
        Measured(4.0, () => Assert.Equal(12.0, TextLayoutEngine.Compute(item).Width, 9));
    }

    // ---------------------------------------------------------------- the face's width and variant

    /// <summary>
    /// **The width axis the file asks for is kept, and no longer reported as unkeepable.** The model names one
    /// family per run and does not pick a face by width, so what is kept is the file's own word - but it is kept,
    /// which is the difference between a gap somebody can act on and a heading that quietly lost its width.
    /// </summary>
    [Fact]
    public void TheFontStretchTheFileAsksForIsKeptOnTheRun()
    {
        SvgImportResult result = Read(
            "<text x=\"0\" y=\"20\" font-size=\"10\" font-stretch=\"semi-condensed\">hi</text>");

        TextRun run = Assert.Single(Block(result).Runs);
        Assert.Equal("semi-condensed", run.FontStretch);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("font-stretch", StringComparison.Ordinal));
    }

    /// <summary>A percentage width is the same fact in another unit, and it survives the same way.</summary>
    [Fact]
    public void APercentageFontStretchIsKeptAsTheFileWroteIt()
    {
        SvgImportResult result = Read(
            "<text x=\"0\" y=\"20\" font-size=\"10\" font-stretch=\"110%\">hi</text>");

        Assert.Equal("110%", Assert.Single(Block(result).Runs).FontStretch);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("font-stretch", StringComparison.Ordinal));
    }

    /// <summary>
    /// **The variant the file asks for is kept, and small caps is not silently dropped.** A run now says the face
    /// is wanted in small caps even though nothing yet selects such a face, which is the honest half: the value
    /// survives a save and a round trip instead of vanishing.
    /// </summary>
    [Fact]
    public void TheFontVariantTheFileAsksForIsKeptOnTheRun()
    {
        SvgImportResult result = Read(
            "<text x=\"0\" y=\"20\" font-size=\"10\" font-variant=\"small-caps\">hi</text>");

        TextRun run = Assert.Single(Block(result).Runs);
        Assert.Equal("small-caps", run.FontVariant);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("font-variant", StringComparison.Ordinal));
    }

    /// <summary>
    /// **A width or variant at its initial value is stored as nothing, because it says nothing.** Keeping
    /// "normal" would add a member to every document Inkscape writes without changing a single drawing.
    /// </summary>
    [Fact]
    public void AWidthOrVariantAtItsInitialValueIsKeptAsAbsent()
    {
        SvgImportResult result = Read(
            "<text x=\"0\" y=\"20\" font-size=\"10\" font-stretch=\"normal\" font-variant=\"normal\">hi</text>");

        TextRun run = Assert.Single(Block(result).Runs);
        Assert.Null(run.FontStretch);
        Assert.Null(run.FontVariant);
        Assert.Empty(result.Warnings);
    }

    /// <summary>
    /// **A width or variant the file inherits is kept on the run that inherits it.** Both are inherited
    /// properties: an outer `text` that states a width states it for every run inside, and a reader that only
    /// recorded the element it was written on would lose it for all the others.
    /// </summary>
    [Fact]
    public void AWidthAndVariantAreInheritedByTheRunsInside()
    {
        SvgImportResult result = Read(
            "<text x=\"0\" y=\"20\" font-size=\"10\" font-stretch=\"condensed\" font-variant=\"small-caps\">" +
            "one<tspan font-stretch=\"expanded\">two</tspan></text>");

        TextItem item = Block(result);
        Assert.Equal(2, item.Runs.Count);

        Assert.Equal("condensed", item.Runs[0].FontStretch);
        Assert.Equal("small-caps", item.Runs[0].FontVariant);

        // The inner span overrides the width it states and inherits the variant it does not.
        Assert.Equal("expanded", item.Runs[1].FontStretch);
        Assert.Equal("small-caps", item.Runs[1].FontVariant);
    }

    /// <summary>
    /// **A width the specification's style words named is kept too.** Inkscape records the face it chose as
    /// `-inkscape-font-specification:'Nimbus Sans, Semi-Condensed'`, and the width is only in those words: a
    /// reader that kept the family and the slant and dropped the width would draw a wider face than the author
    /// saw.
    /// </summary>
    [Fact]
    public void AWidthTheFontSpecificationNamesIsKeptOnTheRun()
    {
        SvgImportResult result = Read(
            "<text x=\"0\" y=\"20\" font-size=\"10\" " +
            "style=\"-inkscape-font-specification:'Nimbus Sans, Semi-Condensed'\">hi</text>");

        TextRun run = Assert.Single(Block(result).Runs);
        Assert.Equal("Nimbus Sans", run.FontFamily);
        Assert.Equal("semi-condensed", run.FontStretch);
    }

    /// <summary>A width or variant that is not a width or variant is reported, not stored as if it were one.</summary>
    [Fact]
    public void AWidthOrVariantTheReaderCannotResolveIsReported()
    {
        SvgImportResult result = Read(
            "<text x=\"0\" y=\"20\" font-size=\"10\" font-stretch=\"stretchy\" font-variant=\"wobbly\">hi</text>");

        TextRun run = Assert.Single(Block(result).Runs);
        Assert.Null(run.FontStretch);
        Assert.Null(run.FontVariant);
        Assert.Contains(result.Warnings, w => w.Contains("font-stretch=\"stretchy\"", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, w => w.Contains("font-variant=\"wobbly\"", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- per-run colour

    /// <summary>
    /// **A colour change still splits the block, and each block carries the file's colour exactly.** The run field
    /// is on the model, but the user agent's painter and the PDF exporter paint a block in the block's colour, so a
    /// two-coloured line is still two objects - and the colour of each is the colour the file named, which is the
    /// half that has to be right. The split is the same one the PDF importer makes.
    /// </summary>
    [Fact]
    public void TwoColouredTspansBecomeTwoBlocksWithTheirOwnColours()
    {
        SvgImportResult result = Read(
            "<text x=\"0\" y=\"40\" font-size=\"20\">" +
            "<tspan fill=\"#ff0000\">red</tspan><tspan fill=\"#0000ff\">blue</tspan></text>");

        TextItem[] blocks = result.Document.AllItems().OfType<TextItem>().ToArray();
        Assert.Equal(2, blocks.Length);

        Assert.Equal("red", Assert.Single(blocks[0].Runs).Text);
        Assert.Equal(new ColorRgb(1.0, 0.0, 0.0), blocks[0].Color);

        Assert.Equal("blue", Assert.Single(blocks[1].Runs).Text);
        Assert.Equal(new ColorRgb(0.0, 0.0, 1.0), blocks[1].Color);
    }

    /// <summary>
    /// **A run that states no colour of its own carries none**, so a document whose text has one colour - which is
    /// nearly all of them - grows no member and loads and saves exactly as it did. The block's colour is the
    /// answer for those runs, and <see cref="TextItem.ColourOf"/> is where that is decided.
    /// </summary>
    [Fact]
    public void ARunThatInheritsTheBlocksColourCarriesNoColourOfItsOwn()
    {
        SvgImportResult result = Read(
            "<text x=\"0\" y=\"40\" font-size=\"20\" fill=\"#00ff00\">one<tspan>two</tspan></text>");

        TextItem item = Block(result);
        Assert.All(item.Runs, run => Assert.Null(run.Color));
        Assert.Equal(new ColorRgb(0.0, 1.0, 0.0), item.Color);
        Assert.All(item.Runs, run => Assert.Equal(new ColorRgb(0.0, 1.0, 0.0), item.ColourOf(run)));
    }

    /// <summary>
    /// **The colour of a run is its own when it has one, and the block's when it does not.** This is the model
    /// fact per-run colour turns on, and it is asserted on a block built by hand because the reader's own split
    /// gives every imported block one colour.
    /// </summary>
    [Fact]
    public void TheColourOfARunIsItsOwnOrDefaultsToTheBlock()
    {
        var block = new TextItem { Color = ColorRgb.Red };
        block.Runs.Add(new TextRun { Text = "a" });
        block.Runs.Add(new TextRun { Text = "b", Color = ColorRgb.Blue });

        Assert.Equal(ColorRgb.Red, block.ColourOf(block.Runs[0]));
        Assert.Equal(ColorRgb.Blue, block.ColourOf(block.Runs[1]));
    }

    // ---------------------------------------------------------------- the sidecar

    private static CadDocument DocumentWith(TextItem text)
    {
        CadDocument document = CadDocument.CreateDefault();
        document.Artboards[0].Layers[0].AddItem(text);
        return document;
    }

    private static TextItem RoundTrip(TextItem text)
    {
        byte[] json = VccadDocumentSerializer.SerializeToBytes(DocumentWith(text));
        CadDocument back = VccadDocumentSerializer.Deserialize(json);
        return back.Artboards[0].Layers[0].Children.OfType<TextItem>().Single();
    }

    /// <summary>
    /// **Every property comes back from the sidecar.** One assert per member, so a dropped one names itself -
    /// the defect this whole class exists for is a reader that sets a field the lossless store then loses.
    /// </summary>
    [Fact]
    public void TheSidecarCarriesSpacingColourAndTheFaceRequest()
    {
        var text = new TextItem { Color = ColorRgb.Black };
        text.Runs.Add(new TextRun
        {
            Text = "tracked",
            FontFamily = "Face A",
            FontSize = 11,
            LetterSpacing = 1.5,
            WordSpacing = 2.25,
            Color = new ColorRgb(0.25, 0.5, 0.75),
            FontStretch = "condensed",
            FontVariant = "small-caps",
        });

        TextRun back = Assert.Single(RoundTrip(text).Runs);

        Assert.Equal(1.5, back.LetterSpacing, 9);
        Assert.Equal(2.25, back.WordSpacing, 9);
        Assert.Equal(new ColorRgb(0.25, 0.5, 0.75), back.Color);
        Assert.Equal("condensed", back.FontStretch);
        Assert.Equal("small-caps", back.FontVariant);
    }

    /// <summary>
    /// **A run that says none of it grows no member.** This is the compatibility rule the rest of the serializer
    /// follows: a sidecar written now for a document that has none of these reads exactly as one written before
    /// they existed, so nothing that already loads starts to look different.
    /// </summary>
    [Fact]
    public void APlainRunWritesNoneOfTheNewMembers()
    {
        var text = new TextItem();
        text.Runs.Add(new TextRun { Text = "plain" });

        string json = VccadDocumentSerializer.Serialize(DocumentWith(text));

        Assert.DoesNotContain("LetterSpacing", json);
        Assert.DoesNotContain("WordSpacing", json);
        Assert.DoesNotContain("FontStretch", json);
        Assert.DoesNotContain("FontVariant", json);

        // The colour member is absent too. Its name is the same as the block's, so it is looked for on the run
        // object itself rather than anywhere in the file.
        JsonObject run = (JsonObject)JsonNode.Parse(json)!
            ["Artboards"]![0]!["Layers"]![0]!["Items"]![0]!["Runs"]![0]!;
        Assert.False(run.ContainsKey("Color"));
        foreach (string member in new[] { "LetterSpacing", "WordSpacing", "FontStretch", "FontVariant" })
        {
            Assert.DoesNotContain(member, run.Select(pair => pair.Key));
        }
    }

    /// <summary>
    /// **A document with none of this writes none of it, and keeps writing the same bytes.** The bytes are the
    /// contract the rest of the store keeps: a member added to `TextRunDto` without an absence condition would
    /// change every text block in every document in the world, and a round trip that grows one on the way back
    /// would change it on every save.
    /// </summary>
    [Fact]
    public void AnOrdinaryTextDocumentSerialisesToItsOldBytes()
    {
        var text = new TextItem { Name = "label", Origin = new Point2D(3, 4) };
        text.Runs.Add(new TextRun { Text = "one" });
        text.Runs.Add(new TextRun { Text = "two", Bold = true });

        // The same document, twice, so the ids are the same and only the members can differ.
        CadDocument document = DocumentWith(text);
        byte[] first = VccadDocumentSerializer.SerializeToBytes(document);
        byte[] second = VccadDocumentSerializer.SerializeToBytes(document);

        Assert.Equal(first, second);

        // And a load/save cycle returns the same bytes rather than growing a member that was absent.
        Assert.Equal(first, VccadDocumentSerializer.SerializeToBytes(VccadDocumentSerializer.Deserialize(first)));

        // The byte image has no member the feature adds.
        string json = Encoding.UTF8.GetString(first);
        Assert.DoesNotContain("LetterSpacing", json);
        Assert.DoesNotContain("WordSpacing", json);
        Assert.DoesNotContain("FontStretch", json);
        Assert.DoesNotContain("FontVariant", json);
    }

    /// <summary>
    /// **A sidecar written before these members existed loads with the defaults.** The older file is this build's
    /// own bytes with the five members taken out of every run, which is exactly the shape the store wrote before
    /// #147 - so a file on disk loads with no tracking, no word spacing and no colour of its own, and not with a
    /// NaN or a zero that changes how it is drawn.
    /// </summary>
    [Fact]
    public void AnOlderSidecarLoadsWithNoSpacingAndNoRunColour()
    {
        var text = new TextItem();
        text.Runs.Add(new TextRun { Text = "old", FontFamily = "Nimbus Sans", FontSize = 12 });

        JsonNode sidecar = JsonNode.Parse(VccadDocumentSerializer.Serialize(DocumentWith(text)))!;
        JsonArray runs = (JsonArray)sidecar["Artboards"]![0]!["Layers"]![0]!["Items"]![0]!["Runs"]!;
        foreach (JsonNode run in runs)
        {
            run.AsObject().Remove("LetterSpacing");
            run.AsObject().Remove("WordSpacing");
            run.AsObject().Remove("Color");
            run.AsObject().Remove("FontStretch");
            run.AsObject().Remove("FontVariant");
        }

        string older = sidecar.ToJsonString();
        Assert.DoesNotContain("LetterSpacing", older);
        Assert.DoesNotContain("FontStretch", older);

        CadDocument back = VccadDocumentSerializer.Deserialize(older);
        TextRun loaded = Assert.Single(back.Artboards[0].Layers[0].Children.OfType<TextItem>().Single().Runs);

        Assert.Equal("old", loaded.Text);
        Assert.Equal(0.0, loaded.LetterSpacing, 9);
        Assert.Equal(0.0, loaded.WordSpacing, 9);
        Assert.Null(loaded.Color);
        Assert.Null(loaded.FontStretch);
        Assert.Null(loaded.FontVariant);
    }
}
