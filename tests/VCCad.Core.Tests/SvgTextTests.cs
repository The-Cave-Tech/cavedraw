using VCCad.Core.Model;
using VCCad.Core.Svg;
using VCCad.Core.Text;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// SVG `text` and its runs.
///
/// **Asserted on the model, never on "something was read".** A reader that dropped the element and one that read it
/// both "import" a file, so what is checked here is the <see cref="TextItem"/> and the <see cref="TextRun"/>s inside
/// it: their text, their face, their size, their weight, their colour, where their baseline lands and how far apart
/// two runs sit. A gap is checked the way the layout and export use it - as the advance of the run *before* it -
/// because that is where the model keeps position.
///
/// Where a number has to be exact, the tests install a fixed measurer (the model's own measurement seam, the same
/// one the canvas supplies) so the arithmetic is the file's and not the machine's.
///
/// **A value the model cannot hold is checked to be reported**, with the test naming which one, because a heading
/// that lost its tracking and a line that lost its stroke both look deliberate on the page.
/// </summary>
public class SvgTextTests
{
    private static readonly string Header =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"200\" height=\"100\" viewBox=\"0 0 200 100\">";

    private static SvgImportResult Read(string body) => SvgReader.Read(Header + body + "</svg>");

    private static SvgImportResult ReadRoot(string svg) => SvgReader.Read(svg);

    private static TextItem[] Blocks(SvgImportResult result)
        => result.Document.AllItems().OfType<TextItem>().ToArray();

    private static TextItem Block(SvgImportResult result) => Assert.Single(Blocks(result));

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

    /// <summary>Where a run's baseline sits in the model, which is the origin plus the ascent it was placed with.</summary>
    private static double Baseline(TextItem item, TextRun run) => item.Origin.Y + (run.PlacedAscentEm * run.FontSize);

    // ---------------------------------------------------------------- content, face and colour

    /// <summary>
    /// **The text, the face, the size and the colour all arrive.** Each is checked separately, because an importer
    /// that reads the string and drops the styling is the failure this is written against - and it produces a
    /// drawing that looks deliberate.
    /// </summary>
    [Fact]
    public void ATextElementImportsItsContentFaceAndColour()
    {
        SvgImportResult result = Read(
            "<text x=\"10\" y=\"20\" font-family=\"DejaVu Sans\" font-size=\"30\" font-weight=\"bold\" " +
            "font-style=\"italic\" fill=\"#ff0000\">Hello</text>");

        TextItem item = Block(result);
        TextRun run = Assert.Single(item.Runs);

        Assert.Equal("Hello", run.Text);
        Assert.Equal("DejaVu Sans", run.FontFamily);
        Assert.Equal(30.0, run.FontSize, 9);
        Assert.True(run.Bold);
        Assert.True(run.Italic);
        Assert.Equal(new ColorRgb(1.0, 0.0, 0.0), item.Color);

        // The file places a baseline and the model stores a top-left, so the two have to be told apart and put
        // back together: the origin is one ascent above the baseline, and export puts the baseline back from it.
        Assert.Equal(10.0, item.Origin.X, 9);
        Assert.Equal(20.0, Baseline(item, run), 9);
        Assert.True(item.Origin.Y < 20.0, "the block's top-left is above its baseline");

        Assert.Equal(1, result.ByElement["text"]);
        Assert.DoesNotContain("text", result.Warnings);
    }

    /// <summary>
    /// **Two runs keep their own faces and sizes, inside one block.** A `tspan` that names a font and a size
    /// overrides those and inherits everything else, and the block keeps one origin because the file put the two
    /// words on one baseline.
    /// </summary>
    [Fact]
    public void TwoTspansKeepTheirOwnFacesAndSizes()
    {
        SvgImportResult result = Read(
            "<text x=\"0\" y=\"40\" font-family=\"Face A\" font-size=\"20\">" +
            "one<tspan font-family=\"Face B\" font-size=\"10\">two</tspan></text>");

        TextItem item = Block(result);
        Assert.Equal(2, item.Runs.Count);

        Assert.Equal("one", item.Runs[0].Text);
        Assert.Equal("Face A", item.Runs[0].FontFamily);
        Assert.Equal(20.0, item.Runs[0].FontSize, 9);

        Assert.Equal("two", item.Runs[1].Text);
        Assert.Equal("Face B", item.Runs[1].FontFamily);
        Assert.Equal(10.0, item.Runs[1].FontSize, 9);

        // Both runs sit on the file's one baseline, which is the whole reason they are one block.
        Assert.Equal(40.0, Baseline(item, item.Runs[0]), 9);
        Assert.Equal(40.0, Baseline(item, item.Runs[1]), 9);

        // A run another run follows carries its advance, because that is how the layout and export move the pen.
        Assert.NotNull(item.Runs[0].AdvanceWidth);
        Assert.Null(item.Runs[1].AdvanceWidth);
    }

    /// <summary>
    /// **A colour change starts a new block, because the model has one colour per block and not per run.** This is
    /// the same split the PDF importer makes, and it is the honest answer: two runs of different colours cannot be
    /// one object without one of them being repainted.
    /// </summary>
    [Fact]
    public void TwoColouredTspansBecomeTwoBlocksWithTheirOwnColours()
    {
        SvgImportResult result = Read(
            "<text x=\"0\" y=\"40\" font-size=\"20\">" +
            "<tspan fill=\"#ff0000\">red</tspan><tspan fill=\"#0000ff\">blue</tspan></text>");

        TextItem[] blocks = Blocks(result);
        Assert.Equal(2, blocks.Length);

        Assert.Equal("red", Assert.Single(blocks[0].Runs).Text);
        Assert.Equal(new ColorRgb(1.0, 0.0, 0.0), blocks[0].Color);

        Assert.Equal("blue", Assert.Single(blocks[1].Runs).Text);
        Assert.Equal(new ColorRgb(0.0, 0.0, 1.0), blocks[1].Color);
    }

    /// <summary>
    /// **The face a run is drawn with is the one the file asked for.** Left in the run, so the font report can say
    /// what the document wanted rather than what this machine happened to have.
    /// </summary>
    [Fact]
    public void TheFamilyTheFileAsksForIsTheOneTheRunCarries()
    {
        SvgImportResult result = Read(
            "<text x=\"0\" y=\"10\" font-family=\"'Some Uninstalled Face', serif\">hi</text>");

        TextRun run = Assert.Single(Block(result).Runs);
        Assert.Equal("Some Uninstalled Face", run.FontFamily);

        // The fallback chain is the file's and the model holds one family, which is said out loud rather than
        // silently reduced to one name.
        Assert.Contains(result.Warnings, w => w.Contains("font-family=", StringComparison.Ordinal) &&
                                             w.Contains("list", StringComparison.Ordinal));
    }

    /// <summary>A generic family names no face, and the model holds the face a run is drawn with.</summary>
    [Fact]
    public void AGenericFamilyIsReported()
    {
        SvgImportResult result = Read("<text x=\"0\" y=\"10\" font-family=\"sans-serif\">hi</text>");

        Assert.Equal("sans-serif", Assert.Single(Block(result).Runs).FontFamily);
        Assert.Contains(result.Warnings, w => w.Contains("generic family", StringComparison.Ordinal));
    }

    /// <summary>
    /// **`-inkscape-font-specification` names the face Inkscape actually chose, and it wins over the family list.**
    /// It is how a file made by Inkscape records the answer to "which face is this really", and ignoring it draws a
    /// different design than the file's author saw.
    /// </summary>
    [Fact]
    public void TheInkscapeFontSpecificationNamesTheFaceThatIsUsed()
    {
        SvgImportResult result = Read(
            "<text x=\"0\" y=\"10\" font-family=\"sans-serif\" " +
            "style=\"-inkscape-font-specification:'DejaVu Sans, Bold'\">hi</text>");

        TextRun run = Assert.Single(Block(result).Runs);

        Assert.Equal("DejaVu Sans", run.FontFamily);
        Assert.True(run.Bold);

        // The generic family the specification overrode is not what is drawn, so it is not reported as a loss.
        Assert.DoesNotContain(result.Warnings, w => w.Contains("generic family", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- position

    /// <summary>
    /// **A run another run follows carries its advance even when the two are adjacent.** Export moves its pen by
    /// `AdvanceWidth`, so a run without one puts everything after it in the same place - the second word written on
    /// top of the first, which reads as one word and is the failure this pins.
    /// </summary>
    [Fact]
    public void AdjacentRunsCarryTheirAdvanceSoExportDoesNotStackThem()
    {
        SvgImportResult result = Measured(4.0, () => Read(
            "<text x=\"10\" y=\"20\"><tspan>AB</tspan><tspan font-style=\"italic\">CD</tspan></text>"));

        TextItem item = Block(result);
        Assert.Equal(2, item.Runs.Count);

        // Eight units of "AB", and no room beyond them: the file put nothing between the two.
        Assert.Equal(8.0, item.Runs[0].AdvanceWidth!.Value, 9);
        Assert.Equal(0.0, item.Runs[0].GapAfter, 9);

        TextLayout layout = TextLayoutEngine.Compute(item);
        Assert.Equal(8.0, layout.Runs.Single(box => box.Run == 1).X, 9);
        Assert.Equal(18.0, item.Origin.X + layout.Runs.Single(box => box.Run == 1).X, 9);
    }

    /// <summary>
    /// **An absolute position lands where the file says.** The second `tspan` names `x="80"`, which the model keeps
    /// as the advance of the run before it - so the run starts at 80 with the fixed measurer, in the model's own
    /// layout and in export, without any width being frozen from a font this machine may not have.
    /// </summary>
    [Fact]
    public void AnAbsolutePositionOnATspanPlacesItWhereTheFileSays()
    {
        SvgImportResult result = Measured(4.0, () => Read(
            "<text x=\"10\" y=\"20\">first<tspan x=\"80\">second</tspan></text>"));

        TextItem item = Block(result);
        Assert.Equal(2, item.Runs.Count);

        // Where the model's own layout puts the second run's pen.
        TextLayout layout = TextLayoutEngine.Compute(item);
        double secondX = layout.Runs.Single(box => box.Run == 1).X;

        Assert.Equal(80.0, item.Origin.X + secondX, 9);
        Assert.Equal(70.0, item.Runs[0].AdvanceWidth!.Value, 9);
    }

    /// <summary>
    /// **`dx` is the room the file left, and it is kept as room.** "AB" at four units a character is eight wide, so
    /// `dx="5"` starts the next run at thirteen - and the gap is recorded as the gap, which is what export puts
    /// back between the glyphs rather than as one lump after them.
    /// </summary>
    [Fact]
    public void ADxIsKeptAsTheRoomBetweenTwoRuns()
    {
        SvgImportResult result = Measured(4.0, () => Read(
            "<text x=\"10\" y=\"20\">AB<tspan dx=\"5\">CD</tspan></text>"));

        TextItem item = Block(result);
        Assert.Equal(2, item.Runs.Count);

        Assert.Equal(5.0, item.Runs[0].GapAfter, 9);
        Assert.Equal(13.0, item.Runs[0].AdvanceWidth!.Value, 9);

        TextLayout layout = TextLayoutEngine.Compute(item);
        Assert.Equal(13.0, layout.Runs.Single(box => box.Run == 1).X, 9);
    }

    /// <summary>
    /// **`dy` moves to another baseline, and a baseline the model cannot put inside one block starts another.** The
    /// two pieces sit three units apart vertically, which is what a reader of the file expects and what a single
    /// block with invented leading could not say.
    /// </summary>
    [Fact]
    public void ADyStartsANewBlockOnItsOwnBaseline()
    {
        SvgImportResult result = Measured(4.0, () => Read(
            "<text x=\"10\" y=\"40\" font-size=\"10\">line1<tspan dy=\"3\">line2</tspan></text>"));

        TextItem[] blocks = Blocks(result);
        Assert.Equal(2, blocks.Length);

        Assert.Equal(40.0, Baseline(blocks[0], blocks[0].Runs[0]), 9);
        Assert.Equal(43.0, Baseline(blocks[1], blocks[1].Runs[0]), 9);

        // The second line carries on from where the first one ended: a `dy` moves down, not left.
        Assert.Equal(30.0, blocks[1].Origin.X, 9);
    }

    /// <summary>
    /// **`text-anchor` places the string at the point the file means.** `middle` puts the middle of the string
    /// there and `end` puts its end there, which the model says with the block's alignment and an origin moved back
    /// by the width its own layout gives it - so a caret, a bounds box and export all agree with the canvas.
    /// </summary>
    [Fact]
    public void TextAnchorPlacesTheStringAtStartMiddleAndEnd()
    {
        Measured(4.0, () =>
        {
            SvgImportResult result = Read(
                "<text x=\"100\" y=\"20\" font-size=\"10\" text-anchor=\"start\">abcd</text>" +
                "<text x=\"100\" y=\"40\" font-size=\"10\" text-anchor=\"middle\">abcd</text>" +
                "<text x=\"100\" y=\"60\" font-size=\"10\" text-anchor=\"end\">abcd</text>");

            TextItem[] blocks = Blocks(result);
            Assert.Equal(3, blocks.Length);

            // Four characters at four units each.
            Assert.Equal(TextAlignment.Left, blocks[0].Alignment);
            Assert.Equal(100.0, blocks[0].Origin.X, 9);

            Assert.Equal(TextAlignment.Center, blocks[1].Alignment);
            Assert.Equal(92.0, blocks[1].Origin.X, 9);

            Assert.Equal(TextAlignment.Right, blocks[2].Alignment);
            Assert.Equal(84.0, blocks[2].Origin.X, 9);

            // And the anchored edge really is the anchor: the box on that side ends at a hundred.
            Assert.Equal(16.0, blocks[2].LocalBounds().Width, 9);
            Assert.Equal(100.0, blocks[2].Origin.X + blocks[2].LocalBounds().Width, 9);
            Assert.Equal(100.0, blocks[1].Origin.X + (blocks[1].LocalBounds().Width / 2.0), 9);
        });
    }

    /// <summary>
    /// **A nested `tspan` inherits and overrides only what it states.** The outer one changes the size, the inner
    /// one changes the family: the middle run keeps the outer size and the inherited colour.
    /// </summary>
    [Fact]
    public void ANestedTspanInheritsAndOverridesOnlyWhatItStates()
    {
        SvgImportResult result = Read(
            "<text x=\"0\" y=\"20\" fill=\"#00ff00\" font-family=\"Face A\" font-size=\"20\">" +
            "a<tspan font-size=\"10\">b<tspan font-family=\"Face B\">c</tspan></tspan></text>");

        TextItem item = Block(result);
        Assert.Equal(3, item.Runs.Count);

        Assert.Equal(("a", "Face A", 20.0), (item.Runs[0].Text, item.Runs[0].FontFamily, item.Runs[0].FontSize));
        Assert.Equal(("b", "Face A", 10.0), (item.Runs[1].Text, item.Runs[1].FontFamily, item.Runs[1].FontSize));
        Assert.Equal(("c", "Face B", 10.0), (item.Runs[2].Text, item.Runs[2].FontFamily, item.Runs[2].FontSize));
        Assert.Equal(new ColorRgb(0.0, 1.0, 0.0), item.Color);
    }

    /// <summary>A transform a block cannot hold stays on a group, where it is exact instead of decomposed.</summary>
    [Fact]
    public void ATransformedTextKeepsItsTransformOnAGroup()
    {
        SvgImportResult result = Read(
            "<text id=\"label\" transform=\"scale(2,3)\" x=\"0\" y=\"10\">hi</text>");

        // The file's own group is the one inside the reader's root transform, which carries the file's own units
        // into the model's points.
        ArtGroup group = result.Document.AllItems().OfType<ArtGroup>().Last();
        Assert.Equal("label", group.Name);
        Assert.Equal(2.0, group.Transform.A, 9);
        Assert.Equal(3.0, group.Transform.D, 9);
        Assert.Single(group.Children.OfType<TextItem>());
    }

    /// <summary>Two baselines of one text element stay together, inside the container the file has for them.</summary>
    [Fact]
    public void TwoBaselinesOfOneTextElementStayInOneGroup()
    {
        SvgImportResult result = Read(
            "<text id=\"lines\" x=\"10\" y=\"20\" font-size=\"10\">" +
            "<tspan x=\"10\" y=\"20\">first</tspan><tspan x=\"10\" y=\"30\">second</tspan></text>");

        // The file's own group is the one inside the reader's root transform, which carries the file's own units
        // into the model's points.
        ArtGroup group = result.Document.AllItems().OfType<ArtGroup>().Last();
        Assert.Equal(2, group.Children.OfType<TextItem>().Count());

        // One text element, counted once however many baselines it holds.
        Assert.Equal(1, result.ByElement["text"]);
    }

    // ---------------------------------------------------------------- white space

    /// <summary>
    /// **White space collapses across the whole element, not per text node.** `a <tspan>b</tspan> c` has one space
    /// between each, and a reader that trimmed each node on its own would either glue the words together or leave
    /// two spaces where the file has one.
    /// </summary>
    [Fact]
    public void WhiteSpaceCollapsesAcrossElementBoundaries()
    {
        SvgImportResult result = Read("<text x=\"0\" y=\"10\"> a <tspan>b</tspan> c </text>");

        TextItem item = Block(result);
        TextRun run = Assert.Single(item.Runs);
        Assert.Equal("a b c", run.Text);
    }

    /// <summary>`xml:space="preserve"` is how Inkscape asks for white space to be kept, and it says so on the root.</summary>
    [Fact]
    public void XmlSpacePreserveOnTheRootKeepsWhiteSpace()
    {
        SvgImportResult result = ReadRoot(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"200\" height=\"100\" viewBox=\"0 0 200 100\" " +
            "xml:space=\"preserve\"><text x=\"0\" y=\"10\"> a b </text></svg>");

        Assert.Equal(" a b ", Assert.Single(Block(result).Runs).Text);
    }

    /// <summary>A `white-space` declaration is CSS's spelling of the same thing and beats the attribute.</summary>
    [Fact]
    public void AWhiteSpaceDeclarationIsHonoured()
    {
        SvgImportResult result = Read(
            "<text x=\"0\" y=\"10\" xml:space=\"preserve\" style=\"white-space:normal\"> a  b </text>");

        Assert.Equal("a b", Assert.Single(Block(result).Runs).Text);
    }

    /// <summary>
    /// **An empty or white-space-only text element invents no run.** A run of nothing is an object a person can
    /// select, move and wonder about, and the file has not drawn one.
    /// </summary>
    [Theory]
    [InlineData("<text x=\"0\" y=\"10\"/>")]
    [InlineData("<text x=\"0\" y=\"10\">   </text>")]
    [InlineData("<text x=\"0\" y=\"10\">\n\t </text>")]
    public void AnEmptyTextElementInventsNoRun(string body)
    {
        SvgImportResult result = Read(body);

        Assert.Empty(Blocks(result));
        Assert.False(result.ByElement.ContainsKey("text"), "no object is invented for text with nothing in it");
    }

    // ---------------------------------------------------------------- what the model cannot hold

    /// <summary>
    /// **Every property with real layout behind it that the model has no field for is reported, with its value.**
    /// Each is named here rather than checked as "a warning appeared", because the point of the report is that a
    /// person can act on the one that matters to them.
    /// </summary>
    [Fact]
    public void PropertiesTheModelCannotHoldAreReported()
    {
        SvgImportResult result = Read(
            "<text x=\"0\" y=\"10\" letter-spacing=\"2\" word-spacing=\"3\" font-stretch=\"condensed\" " +
            "font-variant=\"small-caps\" text-decoration=\"underline\" baseline-shift=\"super\" " +
            "dominant-baseline=\"middle\" writing-mode=\"vertical-rl\" direction=\"rtl\" " +
            "font-weight=\"600\" font-style=\"oblique\" textLength=\"50\" lengthAdjust=\"spacing\" rotate=\"15\">hi</text>");

        // The run is still imported: the text is the file's, and what is missing is said rather than dropped.
        Assert.Single(Block(result).Runs);

        string[] expected =
        {
            "letter-spacing=\"2\"",
            "word-spacing=\"3\"",
            "font-stretch=\"condensed\"",
            "font-variant=\"small-caps\"",
            "text-decoration=\"underline\"",
            "baseline-shift=\"super\"",
            "dominant-baseline=\"middle\"",
            "writing-mode=\"vertical-rl\"",
            "direction=\"rtl\"",
            "font-weight=\"600\"",
            "font-style=\"oblique\"",
            "textLength=\"50\"",
            "lengthAdjust=\"spacing\"",
            "rotate=\"15\"",
        };

        foreach (string wanted in expected)
        {
            Assert.True(
                result.Warnings.Any(w => w.Contains(wanted, StringComparison.Ordinal)),
                $"expected a warning naming {wanted}; got: {string.Join(" | ", result.Warnings)}");
        }
    }

    /// <summary>
    /// **A property at its initial value is not a loss and is not reported.** `font-stretch:normal`, which Inkscape
    /// writes on every text element it makes, says nothing - and reporting it would bury the file that really is
    /// condensed under a page of noise.
    /// </summary>
    [Fact]
    public void PropertiesAtTheirInitialValueAreNotReported()
    {
        SvgImportResult result = Read(
            "<text x=\"0\" y=\"10\" letter-spacing=\"0\" word-spacing=\"normal\" font-stretch=\"normal\" " +
            "font-variant=\"normal\" text-decoration=\"none\" baseline-shift=\"baseline\" " +
            "dominant-baseline=\"auto\" writing-mode=\"horizontal-tb\" direction=\"ltr\">hi</text>");

        Assert.Single(Block(result).Runs);
        Assert.Empty(result.Warnings);
    }

    /// <summary>
    /// **A position per character is reported, because a run is placed as a whole.** `x="1 2 3"` is real SVG - one
    /// position for each glyph - and flattening it to the first would move two characters and say nothing.
    /// </summary>
    [Fact]
    public void APositionPerCharacterIsReported()
    {
        SvgImportResult result = Read("<text x=\"1 2 3\" y=\"10\">abc</text>");

        Assert.Equal(1.0, Block(result).Origin.X, 9);
        Assert.Contains(result.Warnings, w => w.Contains("a position per character", StringComparison.Ordinal) &&
                                             w.Contains("x=\"1 2 3\"", StringComparison.Ordinal));
    }

    /// <summary>
    /// **Text the file paints with a stroke is reported, and text it does not paint is not drawn.** The model fills
    /// a run: it has a colour and no stroke, so outlined text is a gap that has to be said rather than a run painted
    /// with something the file did not name.
    /// </summary>
    [Fact]
    public void StrokedAndUnpaintedTextIsReported()
    {
        SvgImportResult stroked = Read(
            "<text x=\"0\" y=\"10\" fill=\"none\" stroke=\"#000000\" stroke-width=\"1\">hi</text>");

        Assert.Empty(Blocks(stroked));
        Assert.Contains(stroked.Warnings, w => w.Contains("fills text and never strokes it", StringComparison.Ordinal));

        SvgImportResult unpainted = Read("<text x=\"0\" y=\"10\" fill=\"none\">hi</text>");

        Assert.Empty(Blocks(unpainted));
        Assert.Contains(unpainted.Warnings, w => w.Contains("neither a fill nor a stroke", StringComparison.Ordinal));
    }

    /// <summary>
    /// **A gradient fill is reported and flattened to its midpoint**, which is the colour the model already uses
    /// wherever a gradient cannot be painted. Text is filled with one colour per block, so a gradient over a run is
    /// a design the model cannot keep.
    /// </summary>
    [Fact]
    public void AGradientFilledTextIsReportedAndFlattened()
    {
        SvgImportResult result = Read(
            "<defs><linearGradient id=\"g\"><stop offset=\"0\" stop-color=\"#ff0000\"/>" +
            "<stop offset=\"1\" stop-color=\"#0000ff\"/></linearGradient></defs>" +
            "<text x=\"0\" y=\"20\" font-size=\"10\" fill=\"url(#g)\">hi</text>");

        TextItem item = Block(result);
        Assert.Contains(result.Warnings, w => w.Contains("gradient 'g'", StringComparison.Ordinal));

        // The midpoint is halfway between red and blue, and it is opaque - the alternative is a run with a paint
        // the file never named.
        Assert.Equal(1.0, item.Color.A, 6);
        Assert.Equal(0.5, item.Color.R, 3);
        Assert.Equal(0.5, item.Color.B, 3);
    }

    /// <summary>Text on a path is not text at a point, and the reader says so rather than dropping the string.</summary>
    [Fact]
    public void TextOnAPathIsReported()
    {
        SvgImportResult result = Read(
            "<defs><path id=\"p\" d=\"M0 0 L100 0\"/></defs>" +
            "<text x=\"0\" y=\"10\"><textPath href=\"#p\">curved</textPath></text>");

        Assert.Empty(Blocks(result));
        Assert.Contains(result.Warnings, w => w.Contains("textPath", StringComparison.Ordinal));
    }

    /// <summary>A webfont the document carries is a face this reader does not load, and it says so.</summary>
    [Fact]
    public void AWebfontTheDocumentCarriesIsReported()
    {
        SvgImportResult result = Read(
            "<style>@font-face { font-family: 'Carried'; src: url(data:font/ttf;base64,AAAA); }</style>" +
            "<text x=\"0\" y=\"10\" font-family=\"Carried\">hi</text>");

        Assert.Single(Block(result).Runs);
        Assert.Contains(result.Warnings, w => w.Contains("@font-face", StringComparison.Ordinal));
    }

    /// <summary>
    /// **A face the model cannot distinguish is reported, not rounded.** A specification that names a semi-bold cut
    /// is a design choice a file made, and drawing it with the bold cut is a substitution worth naming.
    /// </summary>
    [Fact]
    public void ASemiBoldSpecificationIsReported()
    {
        SvgImportResult result = Read(
            "<text x=\"0\" y=\"10\" style=\"-inkscape-font-specification:'Some Face, Semi-Bold'\">hi</text>");

        Assert.True(Assert.Single(Block(result).Runs).Bold);
        Assert.Contains(result.Warnings, w => w.Contains("neither 400 nor 700", StringComparison.Ordinal));
    }

    /// <summary>A text element with no position at all starts at the file's own origin, as SVG says.</summary>
    [Fact]
    public void ATextWithNoPositionStartsAtTheOrigin()
    {
        SvgImportResult result = Read("<text font-size=\"10\">hi</text>");

        TextItem item = Block(result);
        Assert.Equal(0.0, item.Origin.X, 9);
        Assert.Equal(0.0, Baseline(item, item.Runs[0]), 9);
    }

    /// <summary>
    /// **Metadata inside text is not an unknown element.** A `title` is what a screen reader reads, not something
    /// the file draws, and warning about it would teach a person to ignore the warnings that matter.
    /// </summary>
    [Fact]
    public void AMetadataChildOfTextIsNotReported()
    {
        SvgImportResult result = Read("<text x=\"0\" y=\"10\"><title>a heading</title>hi</text>");

        Assert.Equal("hi", Assert.Single(Block(result).Runs).Text);
        Assert.Empty(result.Warnings);
    }

    /// <summary>
    /// **Text reached through a `use` is read with the styling in force where it is used.** A symbol holding a label
    /// is a real shape in real files, and the inherited font has to travel through the enclosing group, the
    /// instance and the definition the way paint does.
    /// </summary>
    [Fact]
    public void TextInsideAUsedDefinitionIsImportedWithTheInheritedFace()
    {
        SvgImportResult result = Read(
            "<symbol id=\"s\"><text x=\"0\" y=\"0\">label</text></symbol>" +
            "<g font-family=\"Face A\" font-size=\"9\"><use href=\"#s\" x=\"20\" y=\"30\"/></g>");

        TextItem item = Block(result);
        Assert.Equal("label", Assert.Single(item.Runs).Text);
        Assert.Equal("Face A", item.Runs[0].FontFamily);
        Assert.Equal(9.0, item.Runs[0].FontSize, 9);

        ArtGroup instance = result.Document.AllItems().OfType<ArtGroup>().Single(g => g.SourceId == "s");
        Assert.Equal(20.0, instance.Transform.Transform(new Point2D(0, 0)).X, 9);
    }

    /// <summary>
    /// **A piece with nothing between it and the one before it is the same run.** The element boundary is not a run
    /// boundary, and splitting one face into a run per `tspan` would be structure the file has not got.
    /// </summary>
    [Fact]
    public void ATspanThatChangesNothingDoesNotSplitTheRun()
    {
        SvgImportResult result = Read("<text x=\"0\" y=\"10\">one<tspan>two</tspan></text>");

        Assert.Equal("onetwo", Assert.Single(Assert.Single(Blocks(result)).Runs).Text);
    }

    /// <summary>Every unit a position can be written in resolves, including the two that need the font size.</summary>
    [Fact]
    public void PositionsResolveThroughTheirUnits()
    {
        SvgImportResult result = Measured(4.0, () => Read(
            "<text x=\"1in\" y=\"2em\" font-size=\"10\">hi</text>"));

        TextItem item = Block(result);
        Assert.Equal(96.0, item.Origin.X, 9);
        Assert.Equal(20.0, Baseline(item, item.Runs[0]), 9);
    }

    /// <summary>The line-height a file declares is the block's line spacing, which is what makes a kept break work.</summary>
    [Fact]
    public void LineHeightBecomesTheBlocksLineSpacing()
    {
        SvgImportResult result = ReadRoot(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"200\" height=\"100\" viewBox=\"0 0 200 100\" " +
            "xml:space=\"preserve\"><text x=\"0\" y=\"10\" font-size=\"10\" style=\"line-height:1.5\">a\nb</text></svg>");

        TextItem item = Block(result);
        Assert.Equal(1.5, item.LineSpacing, 9);
        Assert.Equal("a\nb", Assert.Single(item.Runs).Text);
    }
}
