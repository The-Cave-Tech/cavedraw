using System.Text.RegularExpressions;
using VCCad.Core.Model;
using VCCad.Core.Svg;
using VCCad.Core.Text;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// The SVG writer's text output, asserted on the **exported bytes** first and on the **re-imported model** second.
///
/// The writer emitted no `text` element at all: a document holding a text block exported a file that silently did
/// not have it, and the only word about it was an entry on <see cref="SvgWriteResult.Missing"/>. That made the round
/// trip every other issue depends on - import an SVG, export it, import it again - untestable for anything with
/// words in it, because the words were the part that did not survive.
///
/// **The frame is the reader's.** SVG places a baseline and the model stores a top-left, so the two only agree if
/// the writer and the reader share one ascent between them. They do: the writer places the block one
/// <see cref="TextMeasurement.TypicalAscentEm"/> above the baseline a block that records no ascent was written
/// against, which is the same number the reader measures the origin back with - so the numbers come back.
///
/// **No unit bridge.** The root states its size in `pt` and its `viewBox` in the model's own numbers, so the
/// reader's view-box fit is the identity and a bare number in the file is one model unit. A `pt` on a font size or
/// a position would send it through the CSS ratio a second time and scale the text by 4/3; the tests below pin the
/// numbers rather than the picture, because a picture that is 33% too big still looks like a picture.
///
/// Where a number has to be exact the tests install a fixed measurer - the model's own measurement seam - so the
/// arithmetic under test is the file's and not the machine's.
/// </summary>
public class SvgWriterTextTests
{
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

    /// <summary>The same, for a test whose assertions measure the model rather than return it.</summary>
    private static void Measured(double advance, Action body)
        => Measured(advance, () =>
        {
            body();
            return 0;
        });

    private static CadDocument Document(params TextItem[] items)
    {
        CadDocument document = CadDocument.CreateDefault();
        foreach (TextItem item in items)
        {
            document.Artboards[0].Layers[0].AddItem(item);
        }

        return document;
    }

    /// <summary>
    /// The fixture: a bold 10pt "Hello" at (12.5, 34) with a 20pt hole after it, then an italic crimson " world".
    ///
    /// The hole is the interesting half. A run's advance is the whole distance to the next run, and the model states
    /// it as <see cref="TextRun.AdvanceWidth"/> plus the part of it that is white space; SVG states it as where the
    /// next run starts. Writing the second run's absolute x is what makes the reader recover both.
    /// </summary>
    private static TextItem TwoRunBlock()
    {
        var text = new TextItem { Origin = new Point2D(12.5, 34), Color = ColorRgb.Black };
        text.Runs.Add(new TextRun
        {
            Text = "Hello",
            FontFamily = "Nimbus Sans",
            FontSize = 10,
            Bold = true,
            AdvanceWidth = 50,
            GapAfter = 20,
        });

        text.Runs.Add(new TextRun
        {
            Text = " world",
            FontFamily = "Nimbus Sans",
            FontSize = 10,
            Italic = true,
            Color = ColorRgb.FromBytes(128, 0, 0),
        });

        return text;
    }

    private static TextItem SoleText(CadDocument document)
        => Assert.Single(document.AllItems().OfType<TextItem>());

    // ---------------------------------------------------------------- the exported bytes

    /// <summary>
    /// **The gap, established: a text block has to reach the file.** The five things a `text` element has to say
    /// about a run are its characters, where it starts, how big it is set, which face it names and what colour it
    /// is - and each is asserted on the bytes rather than on "an element was written", because a `text` element
    /// holding the wrong words is the silent substitution this exporter exists to avoid.
    /// </summary>
    [Fact]
    public void ATextBlockReachesTheExportedBytes()
    {
        string svg = Measured(6.0, () => SvgWriter.Write(Document(TwoRunBlock())));

        Assert.Contains("<text", svg, StringComparison.Ordinal);
        Assert.DoesNotContain("<image", svg, StringComparison.Ordinal);

        // The words themselves, one `tspan` per run.
        Assert.Contains(">Hello</tspan>", svg, StringComparison.Ordinal);
        Assert.Contains(" world</tspan>", svg, StringComparison.Ordinal);

        // Where each run starts, on the block's one baseline: the top-left 34 plus one 0.928em ascent of a 10pt run.
        Assert.Contains("x=\"12.5\"", svg, StringComparison.Ordinal);
        Assert.Contains("x=\"62.5\"", svg, StringComparison.Ordinal);
        Assert.Contains("y=\"43.28\"", svg, StringComparison.Ordinal);

        // The face, the weight and the slant, per run.
        Assert.Contains("font-family=\"'Nimbus Sans'\"", svg, StringComparison.Ordinal);
        Assert.Contains("font-size=\"10\"", svg, StringComparison.Ordinal);
        Assert.Contains("font-weight=\"bold\"", svg, StringComparison.Ordinal);
        Assert.Contains("font-style=\"italic\"", svg, StringComparison.Ordinal);

        // The second run's own colour, which the block does not paint.
        Assert.Contains("fill=\"#800000\"", svg, StringComparison.Ordinal);
        Assert.Contains("fill=\"#000000\"", svg, StringComparison.Ordinal);

        // And the block is on one baseline, so it is one element - not one per run.
        Assert.Single(Regex.Matches(svg, "<text").Cast<Match>());
        Assert.Equal(2, Regex.Matches(svg, "<tspan").Count);
    }

    /// <summary>
    /// **And the numbers come back.** The origin, the runs, their faces, their sizes, their weights, their colours
    /// and the advance the first run carries are all read back off the file - which is the half that says the file
    /// means what the document meant rather than merely being well-formed.
    /// </summary>
    [Fact]
    public void ATextBlockSurvivesARoundTrip()
    {
        Measured(6.0, () =>
        {
            CadDocument back = SvgReader.Read(SvgWriter.Write(Document(TwoRunBlock()))).Document;
            TextItem text = SoleText(back);

            Assert.Equal(12.5, text.Origin.X, 6);
            Assert.Equal(34.0, text.Origin.Y, 6);
            Assert.Equal(ColorRgb.Black, text.Color);
            Assert.Equal(2, text.Runs.Count);

            Assert.Equal("Hello", text.Runs[0].Text);
            Assert.Equal(" world", text.Runs[1].Text);
            Assert.Equal("Nimbus Sans", text.Runs[0].FontFamily);
            Assert.Equal(10.0, text.Runs[0].FontSize, 6);
            Assert.Equal(10.0, text.Runs[1].FontSize, 6);
            Assert.True(text.Runs[0].Bold);
            Assert.True(text.Runs[1].Italic);

            // Each run's own colour, not the block's painted over the whole line.
            Assert.Equal(ColorRgb.Black, text.ColourOf(text.Runs[0]));
            Assert.Equal(ColorRgb.FromBytes(128, 0, 0), text.ColourOf(text.Runs[1]));

            // The room the file left before the second run, kept as the advance of the first - which is where the
            // model keeps position, so the layout puts the second run back where it was.
            Assert.Equal(50.0, text.Runs[0].AdvanceWidth!.Value, 6);
            Assert.Equal(20.0, text.Runs[0].GapAfter, 6);

            // The ascent the reader measures the top-left back with, recorded on the run.
            Assert.Equal(TextMeasurement.TypicalAscentEm, text.Runs[0].PlacedAscentEm, 6);
            Assert.Equal(TextMeasurement.TypicalAscentEm, text.Runs[1].PlacedAscentEm, 6);
        });
    }

    /// <summary>
    /// **A document whose text the writer wrote completely reports nothing**, and a plain block is such a document.
    /// This is the half that keeps <see cref="SvgWriteResult.Missing"/> worth reading: a report that fires on every
    /// text block is one a driver learns to skip, and the losses it is there to name are then invisible.
    /// </summary>
    [Fact]
    public void APlainBlockReportsNoLosses()
    {
        SvgWriteResult result = Measured(6.0, () => SvgWriter.WriteResult(Document(TwoRunBlock())));

        Assert.Contains("<text", result.Svg, StringComparison.Ordinal);
        Assert.Empty(result.Missing);
        Assert.Equal(1, result.ByElement["text"]);
        Assert.Equal(2, result.ByElement["tspan"]);
    }

    /// <summary>
    /// **Text is not carried across the unit bridge.**
    ///
    /// The root states its size in `pt` and its `viewBox` in the model's own numbers, so one bare number in the
    /// file is one model unit and the reader's view-box fit is the identity. Writing `font-size="18pt"` would be
    /// read back as 24 user units - an 18pt run coming back a third too large, which is exactly the class of
    /// mistake an earlier change made and a test caught. The numbers are asserted, not the picture.
    /// </summary>
    [Fact]
    public void TextIsNotSentThroughTheUnitBridge()
    {
        var text = new TextItem { Origin = new Point2D(0, 0) };
        text.Runs.Add(new TextRun
        {
            Text = "A",
            FontFamily = "Nimbus Sans",
            FontSize = 18,
            LetterSpacing = 2.5,
            WordSpacing = 1.25,
        });

        SvgWriteResult result = Measured(10.0, () => SvgWriter.WriteResult(Document(text)));

        // Bare numbers, and no `pt` on any of the three text lengths.
        Assert.Contains("font-size=\"18\"", result.Svg, StringComparison.Ordinal);
        Assert.Contains("letter-spacing=\"2.5\"", result.Svg, StringComparison.Ordinal);
        Assert.Contains("word-spacing=\"1.25\"", result.Svg, StringComparison.Ordinal);
        Assert.DoesNotContain("font-size=\"18pt\"", result.Svg, StringComparison.Ordinal);
        Assert.DoesNotContain("letter-spacing=\"2.5pt\"", result.Svg, StringComparison.Ordinal);

        CadDocument back = Measured(10.0, () => SvgReader.Read(result.Svg).Document);
        TextRun run = Assert.Single(SoleText(back).Runs);

        Assert.Equal(18.0, run.FontSize, 6);
        Assert.Equal(2.5, run.LetterSpacing, 6);
        Assert.Equal(1.25, run.WordSpacing, 6);
    }

    /// <summary>
    /// **A block that does not record the ascent it was placed with comes back at its own origin.**
    ///
    /// The reader measures a block's top-left one `TypicalAscentEm` above the baseline, so the writer has to put
    /// the baseline that far below the top-left. Writing the model's own face ascent instead - a different number -
    /// would move every imported block, which is the whole of what "the frame the reader reads it back in" means.
    /// </summary>
    [Fact]
    public void ABlockWithNoRecordedAscentComesBackAtItsOwnOrigin()
    {
        Measured(6.0, () =>
        {
            TextItem block = TwoRunBlock();
            Assert.Equal(0.0, block.Runs[0].PlacedAscentEm, 6);

            TextItem back = SoleText(SvgReader.Read(SvgWriter.Write(Document(block))).Document);

            Assert.Equal(block.Origin.X, back.Origin.X, 6);
            Assert.Equal(block.Origin.Y, back.Origin.Y, 6);
        });
    }

    /// <summary>
    /// **A block that does record its ascent is written where the model says it is, and the block is reported.**
    ///
    /// A PDF import records the ascent each run was actually placed with. Writing the reader's own 0.928em instead
    /// would be a picture the model never asked for; writing the recorded one is truthful and costs the origin,
    /// because the reader measures the origin back with its own ascent - so the block is named on
    /// <see cref="SvgWriteResult.Missing"/> rather than quietly moved.
    /// </summary>
    [Fact]
    public void ARecordedAscentIsWrittenAndItsCostIsReported()
    {
        var text = new TextItem { Origin = new Point2D(0, 0) };
        text.Runs.Add(new TextRun { Text = "Hi", FontSize = 10, PlacedAscentEm = 0.75 });

        SvgWriteResult result = Measured(6.0, () => SvgWriter.WriteResult(Document(text)));

        // 0 + 0.75em of a 10pt run, which is where the model put the baseline.
        Assert.Contains("y=\"7.5\"", result.Svg, StringComparison.Ordinal);
        Assert.Contains(result.Missing, entry => entry.Contains("recorded ascent", StringComparison.Ordinal));

        // And the characters are in the file regardless - the report is about a number, not about the words.
        Assert.Contains(">Hi</tspan>", result.Svg, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- what SVG cannot state

    /// <summary>
    /// **An embedded programme and its glyph ids are not in the file, and the file says so.**
    ///
    /// A PDF import carries the font programme and the glyph ids it drew with, neither of which an SVG `text`
    /// element can hold - SVG names a face and the viewer supplies it. Writing the decoded characters against the
    /// family name is the honest half; pretending the programme travelled with them would not be, so the loss is
    /// named while the words are still written.
    /// </summary>
    [Fact]
    public void AnEmbeddedProgrammeIsNotInTheFileAndIsReported()
    {
        var text = new TextItem { Name = "Label", Origin = new Point2D(5, 6) };
        text.Runs.Add(new TextRun
        {
            Text = "AB",
            FontFamily = "Nimbus Sans",
            FontSize = 10,
            SourceFont = "NimbusSans-Regular",
            EmbeddedFont = new EmbeddedFont { FamilyName = "ABCDEF+NimbusSans", Ascent = 800 },
            RawCodes = "\u0001\u0002",
            GlyphIds = new ushort[] { 1, 2 },
        });

        SvgWriteResult result = Measured(6.0, () => SvgWriter.WriteResult(Document(text)));

        Assert.Contains(">AB</tspan>", result.Svg, StringComparison.Ordinal);
        Assert.Contains(result.Missing, entry => entry.Contains("embedded programme", StringComparison.Ordinal));
        Assert.Contains(result.Missing, entry => entry.Contains("'NimbusSans-Regular'", StringComparison.Ordinal));
    }

    /// <summary>
    /// **A trailing advance is named, because SVG has no attribute for it.**
    ///
    /// A run's advance is stated to the reader as the room before the run that follows it. Nothing follows the last
    /// run, so an advance it carries past what the face sets has nowhere to go - `textLength` is a measured length
    /// the reader reports rather than holds - and the number is named rather than dropped, while the text is still
    /// written.
    /// </summary>
    [Fact]
    public void ATrailingAdvanceNoRunFollowsIsReported()
    {
        var text = new TextItem { Origin = new Point2D(0, 0) };
        text.Runs.Add(new TextRun { Text = "Hello", FontSize = 10, AdvanceWidth = 50 });

        SvgWriteResult result = Measured(6.0, () => SvgWriter.WriteResult(Document(text)));

        Assert.Contains(">Hello</tspan>", result.Svg, StringComparison.Ordinal);
        Assert.Contains(result.Missing, entry => entry.Contains("no run follows it", StringComparison.Ordinal));
    }

    /// <summary>
    /// **A block set on several baselines is written one element per baseline, and the block is reported.**
    ///
    /// An explicit newline, or a frame that wrapped the text, is a second baseline - and SVG states a baseline at a
    /// time, so the content is all there and the reader reads one block back per line. Writing it all onto one
    /// baseline would collapse the line break and draw the words on top of each other, which is worse than a
    /// structure the caller is told about.
    /// </summary>
    [Fact]
    public void ABlockOnTwoBaselinesIsWrittenOneElementPerBaselineAndReported()
    {
        var text = new TextItem { Origin = new Point2D(10, 20) };
        text.Runs.Add(new TextRun { Text = "one\ntwo", FontSize = 10 });

        SvgWriteResult result = Measured(6.0, () => SvgWriter.WriteResult(Document(text)));

        Assert.Contains(">one</tspan>", result.Svg, StringComparison.Ordinal);
        Assert.Contains(">two</tspan>", result.Svg, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(result.Svg, "<text").Count);
        Assert.Contains(result.Missing, entry => entry.Contains("2 baselines", StringComparison.Ordinal));

        // The content reaches the model - as two blocks, which is what the report said would happen.
        CadDocument back = Measured(6.0, () => SvgReader.Read(result.Svg).Document);
        Assert.Equal(2, back.AllItems().OfType<TextItem>().Count());
    }

    /// <summary>
    /// **A frame is written as the flowed text it is read from, so its width comes back.**
    ///
    /// SVG has no attribute on a `text` element that says where to break, so a framed block written that way comes
    /// back as one block per line - a model that has changed shape, which the corpus round trip caught the moment
    /// the reader learned to read a region (#126). A `flowRoot` holds the region instead, so the frame is **written**
    /// rather than named: the words, the width and the block's paragraph structure all reach the file.
    ///
    /// This test was `AWrapWidthIsReportedRatherThanInvented`, which pinned the old loss.
    /// </summary>
    [Fact]
    public void AWrapWidthIsWrittenAsTheFlowedFormRatherThanNamed()
    {
        var text = new TextItem { Origin = new Point2D(0, 0), FrameWidth = 60 };
        text.Runs.Add(new TextRun { Text = "Hello", FontSize = 10 });

        Measured(10.0, () =>
        {
            SvgWriteResult result = SvgWriter.WriteResult(Document(text));

            Assert.Contains("<flowRoot", result.Svg, StringComparison.Ordinal);
            Assert.Contains("<flowRegion", result.Svg, StringComparison.Ordinal);
            Assert.Contains("width=\"60\"", result.Svg, StringComparison.Ordinal);
            Assert.Contains(">Hello</flowPara>", result.Svg, StringComparison.Ordinal);
            Assert.DoesNotContain(result.Missing, entry => entry.Contains("wrap width", StringComparison.Ordinal));

            // And the frame comes back, which is the whole point of writing it at all.
            TextItem back = SvgReader.Read(result.Svg).Document.AllItems().OfType<TextItem>().Single();
            Assert.Equal(60.0, back.FrameWidth, 9);
            Assert.Equal("Hello", back.PlainText);
        });
    }

    /// <summary>
    /// **A centred block comes back at its own origin.**
    ///
    /// The reader moves an anchored block's origin back by the width its own layout gives the text, so the file has
    /// to state the x at the anchor rather than at the line's left edge. Getting that wrong shifts every centred or
    /// right-aligned block by half its width - which looks deliberate, and is the reason the anchor is asserted
    /// separately from the plain left-aligned case.
    /// </summary>
    [Fact]
    public void ACentredBlockComesBackAtItsOwnOrigin()
    {
        Measured(6.0, () =>
        {
            TextItem block = TwoRunBlock();
            block.Alignment = TextAlignment.Center;

            string svg = SvgWriter.Write(Document(block));
            Assert.Contains("text-anchor=\"middle\"", svg, StringComparison.Ordinal);

            TextItem back = SoleText(SvgReader.Read(svg).Document);
            Assert.Equal(TextAlignment.Center, back.Alignment);
            Assert.Equal(block.Origin.X, back.Origin.X, 6);
            Assert.Equal(block.Origin.Y, back.Origin.Y, 6);
        });
    }

    /// <summary>
    /// **A space that is content survives SVG's white-space collapse.**
    ///
    /// The reader collapses runs of white space and strips the ends, the way the specification says - which is
    /// right for a file that means it and lossy for a block whose spacing was set deliberately. Asking for the
    /// spaces to be kept is what brings them back, and without it the file would hold different words from the
    /// document.
    /// </summary>
    [Fact]
    public void ADeliberateSpaceSurvivesTheWhiteSpaceCollapse()
    {
        var text = new TextItem { Origin = new Point2D(0, 0) };
        text.Runs.Add(new TextRun { Text = "a  b", FontSize = 10 });

        SvgWriteResult result = Measured(6.0, () => SvgWriter.WriteResult(Document(text)));
        Assert.Contains("xml:space=\"preserve\"", result.Svg, StringComparison.Ordinal);

        TextItem back = SoleText(Measured(6.0, () => SvgReader.Read(result.Svg).Document));
        Assert.Equal("a  b", Assert.Single(back.Runs).Text);
    }

    // ---------------------------------------------------------------- the frames

    /// <summary>
    /// **A rotated block is written as a transform about its own origin**, because SVG has no per-element turn -
    /// and the reader puts a transform on a group, so the block comes back inside one. The geometry is what has to
    /// survive: turning the re-imported origin into the artboard has to land on the origin the model started with.
    /// </summary>
    [Fact]
    public void ARotatedBlockIsWrittenAsATransformAndComesBackInAGroup()
    {
        var text = new TextItem { Origin = new Point2D(100, 50), RotationRadians = Math.PI / 2 };
        text.Runs.Add(new TextRun { Text = "up", FontSize = 10 });

        string svg = Measured(6.0, () => SvgWriter.Write(Document(text)));
        Assert.Contains("transform=\"matrix(", svg, StringComparison.Ordinal);

        CadDocument back = Measured(6.0, () => SvgReader.Read(svg).Document);
        TextItem item = SoleText(back);

        // The block's own turn is carried by the group it now sits in, and the group's fixed point is the origin.
        var group = Assert.IsType<ArtGroup>(item.Container);
        Point2D origin = group.Transform.Transform(item.Origin);
        Assert.Equal(100.0, origin.X, 6);
        Assert.Equal(50.0, origin.Y, 6);
    }

    /// <summary>
    /// **A mirrored block is written as a mirror about its own origin**, and the reader carries it the same way.
    /// The model holds the mirror as state rather than as reversed characters - a run has to keep its full string
    /// and a glyph id per character - so the file has to state it as geometry.
    /// </summary>
    [Fact]
    public void AMirroredBlockIsWrittenAsATransformAboutItsOrigin()
    {
        var text = new TextItem { Origin = new Point2D(100, 50), MirrorX = true };
        text.Runs.Add(new TextRun { Text = "flip", FontSize = 10 });

        string svg = Measured(6.0, () => SvgWriter.Write(Document(text)));

        // A horizontal mirror about x = 100 is `matrix(-1,0,0,1,200,0)`.
        Assert.Contains("matrix(-1,0,0,1,200,0)", svg, StringComparison.Ordinal);

        // The text is still the string it was, in order - the mirror is the frame, not the characters.
        Assert.Contains(">flip</tspan>", svg, StringComparison.Ordinal);
    }

    /// <summary>
    /// **A block inside a group is written inside it**, at its own place - the walk recurses, and a reader of the
    /// file must not have to know at what depth the block sat.
    /// </summary>
    [Fact]
    public void ABlockInsideAGroupIsWrittenWhereTheGroupPutsIt()
    {
        CadDocument document = CadDocument.CreateDefault();
        var group = new ArtGroup { Name = "panel", Transform = AffineTransform.CreateTranslation(30, 40) };
        var text = new TextItem { Origin = new Point2D(1, 2) };
        text.Runs.Add(new TextRun { Text = "deep", FontSize = 10 });
        group.AddItem(text);
        document.Artboards[0].Layers[0].AddItem(group);

        SvgWriteResult result = Measured(6.0, () => SvgWriter.WriteResult(document));

        Assert.Contains("transform=\"matrix(1,0,0,1,30,40)\"", result.Svg, StringComparison.Ordinal);
        Assert.Contains(">deep</tspan>", result.Svg, StringComparison.Ordinal);
        Assert.Empty(result.Missing);

        CadDocument back = Measured(6.0, () => SvgReader.Read(result.Svg).Document);
        TextItem item = SoleText(back);
        Point2D inArtboard = AffineTransform.CreateTranslation(30, 40).Transform(item.Origin);
        Assert.Equal(31.0, inArtboard.X, 6);
        Assert.Equal(42.0, inArtboard.Y, 6);
    }

    /// <summary>
    /// **A block with no characters is reported, not written as an empty element.** An empty `text` element is one
    /// the reader makes no item from, so writing it would look like output and produce nothing.
    /// </summary>
    [Fact]
    public void ABlockWithNoCharactersIsReported()
    {
        SvgWriteResult result = SvgWriter.WriteResult(Document(new TextItem { Name = "Empty" }));

        Assert.DoesNotContain("<text", result.Svg, StringComparison.Ordinal);
        Assert.Contains(result.Missing, entry => entry.Contains("no characters", StringComparison.Ordinal));
    }
}
