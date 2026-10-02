using VCCad.Core.Model;
using VCCad.Core.Svg;
using VCCad.Core.Text;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// SVG 1.2 flowed text: `flowRoot`, `flowRegion`, `flowPara` and `flowSpan` - text laid out inside a **region**
/// rather than placed at a point.
///
/// **The decision under test is the issue's own, and it has two halves.** A region that is one upright rectangle is
/// the one area the model's framed <see cref="TextItem"/> can hold, so the file's rectangle becomes the block's
/// origin and its frame width, and each `flowPara` is a paragraph of it. A region that is anything else - a path, a
/// circle, several shapes, several regions, a rounded rectangle, a rectangle turned by a transform - is an area a
/// single width cannot express, and it is **refused by name and kept verbatim**, because reflowing the text into a
/// different shape is the one answer that looks deliberate and is wrong.
///
/// **What the reader did before is recorded here rather than described.** Before #126 the reader met `flowRoot` as
/// an element it did not know: a probe of the first document below reported `objects: 0`, an empty element count and
/// `warnings: flowRoot`, because the unknown-element rule names a tag it cannot read. That is not "text that could
/// not be read", it is "a tag nobody knew", and the first test is that probe turned into its positive assertion.
///
/// **Asserted on the model, and on the layout the model will draw.** Flowed text depends on font metrics, so where a
/// number has to be exact the tests install the model's own fixed measurer - the same seam the canvas supplies - and
/// assert the wrap, the line order and the positions that come out of it. Nothing here asserts "the words are
/// somewhere roughly here".
/// </summary>
public class SvgFlowTextTests
{
    private static readonly string Header =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"200\" height=\"100\" viewBox=\"0 0 200 100\">";

    /// <summary>A `flowRoot` with an id, so a report about it can be matched to the element in the file.</summary>
    private static string FlowRoot(string body, string style = "font-size:10px", string extra = "")
        => $"<flowRoot id=\"root1\" {extra}style=\"{style}\">{body}</flowRoot>";

    /// <summary>A region of one upright rectangle, which is the region the model can hold.</summary>
    private static string Rect(double x = 0, double y = 0, double width = 80, double height = 40)
        => $"<flowRegion><rect x=\"{x}\" y=\"{y}\" width=\"{width}\" height=\"{height}\"/></flowRegion>";

    private static SvgImportResult Read(string body) => SvgReader.Read(Header + body + "</svg>");

    private static TextItem[] Blocks(SvgImportResult result)
        => result.Document.AllItems().OfType<TextItem>().ToArray();

    /// <summary>A measurer that sets every character at one width, so a wrap point is arithmetic and not a font.</summary>
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

    // ---------------------------------------------------------------- what the reader did, and what it does now

    /// <summary>
    /// **A `flowRoot` is read, not skipped by its element name.**
    ///
    /// Before #126 the same document produced **no objects at all**: `flowRoot` fell through the element switch to
    /// the unknown-element rule, which added the bare name `flowRoot` to the warnings and returned - so the
    /// `flowPara` text inside it was never even looked at, and a probe of this document reported
    /// `by element:` empty. The file's text was not reported as unreadable, it was reported as an unknown tag.
    ///
    /// Now one object arrives, `flowRoot` is a counted element, and the element is not named as unknown.
    /// </summary>
    [Fact]
    public void AFlowedRootIsImportedRatherThanSkippedByItsElementName()
    {
        SvgImportResult result = Read(FlowRoot(Rect() + "<flowPara>Hello</flowPara>"));

        TextItem item = Assert.Single(Blocks(result));
        Assert.Equal("Hello", Assert.Single(item.Runs).Text);
        Assert.Equal(1, result.ByElement["flowRoot"]);

        // The bare element name is the "I do not know this tag" report, and it is no longer the answer. The reports
        // this document *does* carry name the flowRoot and say what was not held.
        Assert.DoesNotContain(result.Warnings, warning => warning == "flowRoot");
        Assert.Contains(result.Warnings, warning => warning.Contains("flows into a rectangle", StringComparison.Ordinal));
    }

    /// <summary>
    /// **The paragraphs arrive in order, on their own lines, with their own styling, inside one block.**
    ///
    /// One `flowRoot` is one text object - that is what keeps an edit to the frame an edit to one thing - so the two
    /// paragraphs are two baselines of one <see cref="TextItem"/>, separated by the newline the model sets a line
    /// with. The second paragraph's `flowSpan` changes the weight, which is where one run ends and the next begins.
    /// </summary>
    [Fact]
    public void TwoParagraphsKeepTheirOrderTheirOwnStylesAndTheLineBetweenThem()
    {
        // Measured, so "two baselines and no third" is arithmetic and not a font: eleven characters at four units
        // is forty-four, which fits the eighty-unit frame. The frame is the region's own either way.
        Measured(4.0, () =>
        {
            SvgImportResult result = Read(FlowRoot(
                Rect() +
                "<flowPara style=\"font-size:10px;fill:#ff0000\">first</flowPara>" +
                "<flowPara style=\"font-size:20px;fill:#0000ff\">second <flowSpan " +
                "style=\"font-weight:bold\">half</flowSpan></flowPara>"));

            TextItem item = Assert.Single(Blocks(result));

            // One block, and its colour is its first piece's - the rule the `text` reader follows for a run's own
            // colour, and what keeps a later span's colour attributable to the run rather than to the block.
            Assert.Equal(new ColorRgb(1.0, 0.0, 0.0), item.Color);
            Assert.Equal("first\nsecond half", item.PlainText);

            Assert.Equal(3, item.Runs.Count);

            // The break belongs to the paragraph that ended, so it is carried by that paragraph's run.
            Assert.Equal("first\n", item.Runs[0].Text);
            Assert.Equal(10.0, item.Runs[0].FontSize, 9);
            Assert.Equal(new ColorRgb(1.0, 0.0, 0.0), item.ColourOf(item.Runs[0]));

            Assert.Equal("second ", item.Runs[1].Text);
            Assert.Equal(20.0, item.Runs[1].FontSize, 9);
            Assert.False(item.Runs[1].Bold);
            Assert.Equal(new ColorRgb(0.0, 0.0, 1.0), item.ColourOf(item.Runs[1]));

            Assert.Equal("half", item.Runs[2].Text);
            Assert.Equal(20.0, item.Runs[2].FontSize, 9);
            Assert.True(item.Runs[2].Bold);
            Assert.Equal(new ColorRgb(0.0, 0.0, 1.0), item.ColourOf(item.Runs[2]));

            // Two paragraphs on two baselines, and no third line invented between them.
            Assert.Equal(2, TextLayoutEngine.Compute(item).Lines.Count);
        });
    }

    /// <summary>
    /// **The file's rectangle becomes the block's frame, and the block wraps at that width.**
    ///
    /// This is the issue's "map a rectangular region to the text width and honour it exactly": the origin is the
    /// rectangle's own corner, in the file's own units, and the frame is its width. With a measurer that sets every
    /// character at four units, sixteen units of frame is four characters - so "abcd" fills the line and "efgh" is
    /// wrapped onto the next one by the model's own layout, not by a number measured from a face.
    /// </summary>
    [Fact]
    public void ARectangularRegionBecomesTheBlocksFrameAndWrapsAtItsWidth()
    {
        Measured(4.0, () =>
        {
            SvgImportResult result = Read(FlowRoot(
                Rect(x: 10, y: 20, width: 16, height: 50) +
                "<flowPara>abcd efgh</flowPara>"));

            TextItem item = Assert.Single(Blocks(result));

            Assert.Equal(10.0, item.Origin.X, 9);
            Assert.Equal(20.0, item.Origin.Y, 9);
            Assert.Equal(16.0, item.FrameWidth, 9);

            TextLayout layout = TextLayoutEngine.Compute(item);
            Assert.Equal(2, layout.Lines.Count);
            Assert.Equal("abcd", Slice(item, layout, 0));
            Assert.Equal("efgh", Slice(item, layout, 1));
        });
    }

    private static string Slice(TextItem item, TextLayout layout, int line)
    {
        TextLine placed = layout.Lines[line];
        return item.PlainText.Substring(placed.Start, placed.Length);
    }

    /// <summary>
    /// **An empty `flowPara` is a blank line, not nothing.**
    ///
    /// The file drew a paragraph with no words in it, and the space it takes is the drawing: dropping it lifts every
    /// paragraph below it by a line, which is a page that looks deliberate and is not the file's. The model's
    /// spelling of a blank line is a newline with nothing between the two around it.
    /// </summary>
    [Fact]
    public void AnEmptyFlowParaKeepsItsBlankLine()
    {
        SvgImportResult result = Read(FlowRoot(
            Rect() + "<flowPara>first</flowPara><flowPara/><flowPara>second</flowPara>"));

        TextItem item = Assert.Single(Blocks(result));
        Assert.Equal("first\n\nsecond", item.PlainText);
        Assert.Equal(3, TextLayoutEngine.Compute(item).Lines.Count);
    }

    /// <summary>
    /// **The region's height is named as the thing the model has no field for.**
    ///
    /// A <see cref="TextItem"/> holds a frame **width** and grows downwards to fit what it sets; it has no frame
    /// height, so a block that outgrows its region is drawn in full where the file would crop it. That is a real
    /// difference from the file, and the reader says so instead of leaving somebody to notice it on the page.
    /// </summary>
    [Fact]
    public void TheRegionsHeightIsNamedAsWhatTheModelDoesNotHold()
    {
        SvgImportResult result = Read(FlowRoot(Rect(width: 80, height: 40) + "<flowPara>hi</flowPara>"));

        Assert.Contains(result.Warnings, warning =>
            warning.Contains("flows into a rectangle 80 by 40", StringComparison.Ordinal) &&
            warning.Contains("not held", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- what the model cannot hold

    /// <summary>
    /// **A region the model cannot hold is refused by name, and the element is kept verbatim.**
    ///
    /// A path, a circle, several shapes, several regions, a rounded rectangle: each is an area whose wrapping a
    /// single width cannot express. The refusal names the element **and** the shape that caused it, because
    /// "flowRoot" alone sends a person looking at the wrong thing, and the element itself is kept on the document so
    /// the text and the region go back out with the file rather than living on only in a warning.
    /// </summary>
    [Theory]
    [InlineData("<flowRegion><path d=\"M0 0 L50 0 L50 20 Z\"/></flowRegion><flowPara>curved</flowPara>", "<path>")]
    [InlineData("<flowRegion><circle cx=\"10\" cy=\"10\" r=\"10\"/></flowRegion><flowPara>round</flowPara>", "<circle>")]
    [InlineData(
        "<flowRegion><rect width=\"40\" height=\"20\"/><rect x=\"40\" width=\"40\" height=\"20\"/></flowRegion>" +
        "<flowPara>two</flowPara>",
        "2 shapes")]
    [InlineData("<flowRegion><rect width=\"80\" height=\"40\" rx=\"5\"/></flowRegion><flowPara>rounded</flowPara>", "rounded <rect>")]
    [InlineData("<flowRegion><rect width=\"80\" height=\"40\"/></flowRegion><flowPara>one</flowPara>" +
                "<flowRegion><rect width=\"80\" height=\"40\"/></flowRegion>",
        "2 <flowRegion>s")]
    [InlineData("<flowRegion><rect width=\"80\" height=\"40\" transform=\"rotate(30)\"/></flowRegion>" +
                "<flowPara>turned</flowPara>",
        "transform")]
    [InlineData("<flowRegion><title>a region</title></flowRegion><flowPara>empty</flowPara>", "no shape in it")]
    [InlineData("<flowPara>nothing to flow into</flowPara>", "no <flowRegion>")]
    [InlineData("<flowRegion><rect width=\"0\" height=\"40\"/></flowRegion><flowPara>no area</flowPara>", "no area")]
    public void ARegionTheModelCannotHoldIsRefusedByNameAndKept(string body, string reason)
    {
        SvgImportResult result = Read(FlowRoot(body));

        // No text is invented for it, and the element is not counted as imported artwork either.
        Assert.Empty(Blocks(result));
        Assert.False(result.ByElement.ContainsKey("flowRoot"));

        Assert.Contains(result.Warnings, warning =>
            warning.Contains("the flowRoot 'root1'", StringComparison.Ordinal) &&
            warning.Contains(reason, StringComparison.Ordinal) &&
            warning.Contains("kept verbatim", StringComparison.Ordinal));

        // Kept, words and region together, exactly as the file wrote them.
        string kept = Assert.Single(result.Document.SvgExtras);
        Assert.Contains("id=\"root1\"", kept, StringComparison.Ordinal);
        Assert.Contains("<flowPara", kept, StringComparison.Ordinal);
    }

    /// <summary>
    /// **A refused `flowRoot` goes back out with the file.**
    ///
    /// Keeping it is only half of the honest answer; the other half is that an export carries it. The written
    /// document is read again, and the refusal and the kept element are both still there - so the round trip is a
    /// refusal twice, never a quiet deletion.
    /// </summary>
    [Fact]
    public void ARefusedFlowRootGoesBackOutWithTheFile()
    {
        SvgImportResult first = Read(
            "<rect x=\"0\" y=\"0\" width=\"10\" height=\"10\" fill=\"#000000\"/>" +
            FlowRoot("<flowRegion><path d=\"M0 0 L50 0 L50 20 Z\"/></flowRegion><flowPara>curved</flowPara>"));

        string written = SvgWriter.Write(first.Document);
        Assert.Contains("curved", written, StringComparison.Ordinal);
        Assert.Contains("<flowRegion", written, StringComparison.Ordinal);

        SvgImportResult again = SvgReader.Read(written);
        Assert.Empty(Blocks(again));
        Assert.Contains(again.Document.SvgExtras, xml => xml.Contains("curved", StringComparison.Ordinal));
    }

    /// <summary>
    /// **Flowed text is aligned the way the file aligns it, inside the region.**
    ///
    /// Flowed text is aligned with `text-align`, not with `text-anchor`: the anchor places a whole string at a point
    /// and the alignment aligns the lines inside the frame, which is exactly what the model's framed block means. A
    /// reader that only read the anchor would left-align every centred paragraph in the file - a substitution the
    /// file did not ask for.
    /// </summary>
    [Fact]
    public void ACentredFlowParagraphIsCentredInsideTheRegion()
    {
        Measured(4.0, () =>
        {
            SvgImportResult result = Read(FlowRoot(
                Rect(x: 10, y: 20, width: 40, height: 20) +
                "<flowPara style=\"text-align:center\">ab</flowPara>"));

            TextItem item = Assert.Single(Blocks(result));
            Assert.Equal(TextAlignment.Center, item.Alignment);

            // Two characters at four units in a forty-unit frame: the line starts sixteen units in from the
            // region's left edge, which is the origin the file gave.
            Assert.Equal(10.0, item.Origin.X, 9);
            Assert.Equal(16.0, TextLayoutEngine.Compute(item).Runs.Single().X, 9);
        });
    }

    /// <summary>
    /// **The block is aligned the way its first paragraph is, and a later paragraph that disagrees is named.**
    ///
    /// A block has one alignment; the file may give each paragraph its own. The first paragraph is the one the block
    /// can be set by, and the difference for the rest is said once rather than repeated for every paragraph.
    /// </summary>
    [Fact]
    public void ParagraphsThatDisagreeAboutAlignmentAreNamedOnce()
    {
        SvgImportResult result = Read(FlowRoot(
            Rect() +
            "<flowPara style=\"text-align:center\">one</flowPara>" +
            "<flowPara style=\"text-align:right\">two</flowPara>" +
            "<flowPara style=\"text-align:left\">three</flowPara>"));

        TextItem item = Assert.Single(Blocks(result));
        Assert.Equal(TextAlignment.Center, item.Alignment);

        string[] reported = result.Warnings
            .Where(warning => warning.Contains("aligned differently", StringComparison.Ordinal))
            .ToArray();

        Assert.Single(reported);
        Assert.Contains("centred", reported[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// **`text-align="justify"` is reported, and the inherited alignment stands.** Justifying stretches the lines
    /// of a paragraph to the region, and the model aligns a block without stretching its lines - so it is a value
    /// the reader cannot honour, named rather than quietly read as an alignment it is not.
    /// </summary>
    [Fact]
    public void AJustifiedFlowParagraphIsReported()
    {
        SvgImportResult result = Read(FlowRoot(
            Rect() + "<flowPara style=\"text-align:justify\">words</flowPara>"));

        TextItem item = Assert.Single(Blocks(result));
        Assert.Equal(TextAlignment.Left, item.Alignment);
        Assert.Contains(result.Warnings, warning =>
            warning.Contains("text-align=\"justify\"", StringComparison.Ordinal) &&
            warning.Contains("stretches", StringComparison.Ordinal));
    }

    /// <summary>
    /// **A `flowSpan` that states a position is reported.** Text in a region is placed by the region and wrapped by
    /// the frame, so a span that names an `x` or a `y` is asking for something the flowing layout has no room for.
    /// </summary>
    [Fact]
    public void AFlowSpanThatStatesAPositionIsReported()
    {
        SvgImportResult result = Read(FlowRoot(
            Rect() + "<flowPara>one<flowSpan x=\"40\" y=\"12\">two</flowSpan></flowPara>"));

        Assert.Equal("onetwo", Assert.Single(Blocks(result)).PlainText);
        Assert.Contains(result.Warnings, warning =>
            warning.Contains("states a position", StringComparison.Ordinal));
    }

    /// <summary>
    /// **A `flowRoot` with no characters imports nothing and says so.** A block with no runs is an object a person
    /// can select, move and wonder about, and the file has not drawn one.
    /// </summary>
    [Fact]
    public void AFlowedRootWithNoCharactersIsReportedAndNotInvented()
    {
        SvgImportResult result = Read(FlowRoot(Rect() + "<flowPara/>"));

        Assert.Empty(Blocks(result));
        Assert.Contains(result.Warnings, warning =>
            warning.Contains("holds no characters", StringComparison.Ordinal));
    }

    /// <summary>
    /// **Flowed text the model cannot paint is not drawn, and the reason is named.** A run is filled: it has a
    /// colour and no stroke, so text the file only strokes is reported rather than turned into a run painted with
    /// something the file did not name - the answer the `text` reader gives the same file.
    /// </summary>
    [Fact]
    public void StrokedFlowedTextIsReportedAndNotDrawn()
    {
        SvgImportResult result = Read(FlowRoot(
            Rect() + "<flowPara>outlined</flowPara>",
            style: "font-size:10px;fill:none;stroke:#000000;stroke-width:1"));

        Assert.Empty(Blocks(result));
        Assert.Contains(result.Warnings, warning =>
            warning.Contains("fills text and never strokes it", StringComparison.Ordinal));
    }

    /// <summary>
    /// **A gradient-filled `flowRoot` is reported and flattened to the gradient's midpoint** - the colour the model
    /// already uses wherever a gradient cannot be painted, resolved once against the block's own box.
    /// </summary>
    [Fact]
    public void GradientFilledFlowedTextIsReportedAndFlattened()
    {
        SvgImportResult result = Read(
            "<defs><linearGradient id=\"g\"><stop offset=\"0\" stop-color=\"#ff0000\"/>" +
            "<stop offset=\"1\" stop-color=\"#0000ff\"/></linearGradient></defs>" +
            FlowRoot(Rect() + "<flowPara>faded</flowPara>", style: "font-size:10px;fill:url(#g)"));

        TextItem item = Assert.Single(Blocks(result));
        Assert.Contains(result.Warnings, warning => warning.Contains("gradient 'g'", StringComparison.Ordinal));

        Assert.Equal(1.0, item.Color.A, 6);
        Assert.Equal(0.5, item.Color.R, 3);
        Assert.Equal(0.5, item.Color.B, 3);
    }

    // ---------------------------------------------------------------- the file's own spellings

    /// <summary>
    /// **The form Inkscape writes for text in a frame is read.**
    ///
    /// `flowRoot` with `xml:space="preserve"`, the face and the size in the `style` attribute, `transform` carrying
    /// the frame to where it belongs, an id on every element: this is the markup those files actually carry, and the
    /// text, the face, the size and the region all have to come out of it.
    /// </summary>
    [Fact]
    public void TheFormInkscapeWritesForTextInAFrameIsRead()
    {
        SvgImportResult result = SvgReader.Read(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"400\" height=\"200\" viewBox=\"0 0 400 200\">" +
            "<flowRoot xml:space=\"preserve\" id=\"flowRoot2985\" " +
            "style=\"font-style:normal;font-weight:normal;font-size:20px;line-height:1.25;" +
            "font-family:'Some Face';letter-spacing:0px;word-spacing:0px;fill:#000000;fill-opacity:1;stroke:none\" " +
            "transform=\"translate(20,30)\">" +
            "<flowRegion id=\"flowRegion2987\">" +
            "<rect id=\"rect2989\" width=\"200\" height=\"60\" x=\"5\" y=\"5\"/></flowRegion>" +
            "<flowPara id=\"flowPara2991\">This is text in a frame</flowPara></flowRoot></svg>");

        TextItem item = Assert.Single(Blocks(result));
        TextRun run = Assert.Single(item.Runs);

        Assert.Equal("This is text in a frame", run.Text);
        Assert.Equal("Some Face", run.FontFamily);
        Assert.Equal(20.0, run.FontSize, 9);

        // The frame is the file's rectangle in the file's own space, inside the group its transform makes.
        Assert.Equal(5.0, item.Origin.X, 9);
        Assert.Equal(5.0, item.Origin.Y, 9);
        Assert.Equal(200.0, item.FrameWidth, 9);
        Assert.Equal(1.25, item.LineSpacing, 9);

        ArtGroup group = result.Document.AllItems().OfType<ArtGroup>().Last();
        Assert.Equal(20.0, group.Transform.E, 9);
        Assert.Equal(30.0, group.Transform.F, 9);
        Assert.Single(group.Children.OfType<TextItem>());
    }

    /// <summary>
    /// **A flowed block goes back out in the file's own flowed form, so the frame comes back too.**
    ///
    /// A `text` element has no attribute that says where to break, so a frame written that way returns as one block
    /// per line - a model that has changed shape. The `flowRoot` form holds the region instead, so the width, the
    /// block's origin, its paragraphs and its runs all survive: an import of Inkscape's flowed text is an import and
    /// not a conversion.
    ///
    /// The one number the model does not hold is the region's **height**, and the height written is the block's own
    /// laid-out height. That is named rather than passed off as the file's.
    /// </summary>
    [Fact]
    public void AFlowedBlockIsWrittenBackAsAFlowedRoot()
    {
        SvgImportResult result = Read(FlowRoot(
            Rect(x: 10, y: 20, width: 80, height: 40) +
            "<flowPara>words here</flowPara>"));

        SvgWriteResult written = SvgWriter.WriteResult(result.Document);

        Assert.Contains("<flowRoot", written.Svg, StringComparison.Ordinal);
        Assert.Contains("<flowRegion", written.Svg, StringComparison.Ordinal);
        Assert.Contains("words here", written.Svg, StringComparison.Ordinal);
        Assert.DoesNotContain(written.Missing, entry => entry.Contains("wrap width", StringComparison.Ordinal));
        Assert.Contains(written.Missing, entry => entry.Contains("height", StringComparison.Ordinal));

        // And it reads back as the block that was written, frame and origin included.
        TextItem back = Assert.Single(Blocks(SvgReader.Read(written.Svg)));
        Assert.Equal("words here", back.PlainText);
        Assert.Equal(10.0, back.Origin.X, 9);
        Assert.Equal(20.0, back.Origin.Y, 9);
        Assert.Equal(80.0, back.FrameWidth, 9);
    }

    /// <summary>A `flowRoot` inside a group is read where the group puts it, like anything else in the file.</summary>
    [Fact]
    public void AFlowedRootInsideAGroupIsRead()
    {
        SvgImportResult result = Read(
            "<g transform=\"translate(7,9)\">" + FlowRoot(Rect() + "<flowPara>grouped</flowPara>") + "</g>");

        TextItem item = Assert.Single(Blocks(result));
        Assert.Equal("grouped", Assert.Single(item.Runs).Text);

        ArtGroup group = result.Document.AllItems().OfType<ArtGroup>().Last();
        Assert.Contains(item, group.Children);
        Assert.Equal(7.0, group.Transform.E, 9);
    }
}
