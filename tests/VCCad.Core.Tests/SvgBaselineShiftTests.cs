using VCCad.Core.Model;
using VCCad.Core.Svg;
using VCCad.Core.Text;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// `baseline-shift`: the baseline a run hangs from (#128).
///
/// **The assertions are on the geometry the layout computes and the bytes the writer emits**, never on the member
/// that was set. A member that round-trips perfectly while the layout draws every glyph on the line's own baseline
/// is exactly the defect this file exists to catch, and it is this campaign's signature: `baseline-shift` was on
/// the model for nobody, the reader reported *"a baseline the model does not hold"*, and the drawing was the base
/// line's. So the central test lays a superscript and its base out together and measures where the glyphs land,
/// against the same block laid out with no shift.
///
/// **A shifted run does not grow the line box.** SVG's own rule, and the reason the shift is a run member rather
/// than a second block with a smaller ascent: a superscript draws above the ascent of the text beside it without
/// moving the block, the line or the lines after it. The tests below pin both halves - the glyph moves, the line
/// does not.
///
/// Where a number has to be exact the tests install a fixed measurer, the model's own measurement seam, so the
/// arithmetic under test is the file's and not the machine's.
/// </summary>
public class SvgBaselineShiftTests
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

    private static void Measured(double advance, Action body)
        => Measured(advance, () =>
        {
            body();
            return 0;
        });

    private static SvgImportResult Read(string body)
        => SvgReader.Read(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"200\" height=\"100\">" + body + "</svg>");

    private static TextItem Sole(SvgImportResult result)
        => Assert.Single(result.Document.AllItems().OfType<TextItem>());

    /// <summary>One block of "a" then "b", where "b" carries <paramref name="shift"/> ems of baseline.</summary>
    private static TextItem TwoRuns(double shift, double size = 10)
    {
        var item = new TextItem { Origin = new Point2D(0, 0) };
        item.Runs.Add(new TextRun { Text = "a", FontFamily = "Nimbus Sans", FontSize = size });
        item.Runs.Add(new TextRun
        {
            Text = "b",
            FontFamily = "Nimbus Sans",
            FontSize = size,
            BaselineShift = shift,
        });

        return item;
    }

    private static GlyphBox Glyph(TextItem text, int index)
        => TextLayoutEngine.Compute(text).Glyphs.Single(glyph => glyph.Index == index);

    // ---------------------------------------------------------------- the geometry the layout computes

    /// <summary>
    /// **The gap, established: a shifted run's glyphs leave the line's baseline, and the pen does not move.**
    ///
    /// This is the measurement that was missing. The superscript is asserted against the *same block laid out with
    /// no shift*, so the test names the effect and not the absolute arithmetic: the glyph is half a 10pt em higher
    /// (0.5 em x 10pt = 5pt), its horizontal pen position and its advance are untouched, and the base beside it has
    /// not moved at all. A reader that resolved the property but a layout that ignored it fails on the first of
    /// those; a layout that moved the whole pen up fails on the second.
    /// </summary>
    [Fact]
    public void ASuperscriptGlyphLeavesTheBaselineAndThePenDoesNotMove()
    {
        Measured(6.0, () =>
        {
            GlyphBox baseLine = Glyph(TwoRuns(0.0), 1);
            GlyphBox raised = Glyph(TwoRuns(0.5), 1);

            // The base character, which says nothing, is where it always was.
            GlyphBox plainBase = Glyph(TwoRuns(0.5), 0);
            Assert.Equal(Glyph(TwoRuns(0.0), 0).Y, plainBase.Y, 9);

            // Raised by exactly half the run's own em, and by nothing else.
            Assert.Equal(5.0, baseLine.Y - raised.Y, 9);
            Assert.Equal(baseLine.X, raised.X, 9);
            Assert.Equal(baseLine.Advance, raised.Advance, 9);
        });
    }

    /// <summary>
    /// **A subscript lowers, by the same amount and in the other direction.** The sign is the whole of the
    /// property's vocabulary after the size, and reading `sub` as "no shift" would leave a subscript sitting on the
    /// line looking deliberate.
    /// </summary>
    [Fact]
    public void ASubscriptGlyphSitsBelowTheBaseline()
    {
        Measured(6.0, () =>
        {
            GlyphBox baseLine = Glyph(TwoRuns(0.0), 1);
            GlyphBox lowered = Glyph(TwoRuns(-0.5), 1);

            Assert.Equal(5.0, lowered.Y - baseLine.Y, 9);
            Assert.Equal(baseLine.X, lowered.X, 9);
        });
    }

    /// <summary>
    /// **The shift is the run's own em and follows its size.** A 20pt superscript beside a 10pt base is twice as
    /// far off the line as a 10pt one, which is what "half an em" means and what a single number stored in points
    /// could not say. The member is the fraction; the layout multiplies it by the run's own size.
    /// </summary>
    [Fact]
    public void TheShiftIsTheRunsOwnEmAndFollowsItsSize()
    {
        Measured(6.0, () =>
        {
            double small = Glyph(TwoRuns(0.0, 10), 1).Y - Glyph(TwoRuns(0.5, 10), 1).Y;
            double large = Glyph(TwoRuns(0.0, 20), 1).Y - Glyph(TwoRuns(0.5, 20), 1).Y;

            Assert.Equal(5.0, small, 9);
            Assert.Equal(10.0, large, 9);
        });
    }

    /// <summary>
    /// **A shifted run does not grow the line box.** Every line, its ascent, its descent and the block's height are
    /// exactly what the same block has with no shift at all - so a superscript over a word does not push the lines
    /// after it down the page, and a person who styles one word cannot reflow a paragraph.
    /// </summary>
    [Fact]
    public void AShiftedRunDoesNotGrowTheLine()
    {
        Measured(6.0, () =>
        {
            TextLayout plain = TextLayoutEngine.Compute(TwoRuns(0.0));
            TextLayout raised = TextLayoutEngine.Compute(TwoRuns(0.5));

            TextLine wanted = Assert.Single(plain.Lines);
            TextLine shifted = Assert.Single(raised.Lines);

            Assert.Equal(wanted.Top, shifted.Top, 9);
            Assert.Equal(wanted.Ascent, shifted.Ascent, 9);
            Assert.Equal(wanted.Descent, shifted.Descent, 9);
            Assert.Equal(wanted.Baseline, shifted.Baseline, 9);
            Assert.Equal(plain.Height, raised.Height, 9);

            // And the caret, which is a pen position along the line, is unmoved: a shift is across the line.
            Assert.Equal(plain.CaretX, raised.CaretX);
        });
    }

    /// <summary>
    /// **A shifted run in a vertical column moves across the column, not along it.** A vertical baseline runs down
    /// the page, so a superscript sits beside its column line - the page's +x - and its pen position down the
    /// column, and so the next glyph after it, are untouched. This is the axis the whole read is about: a shift
    /// moves a glyph off its own baseline whichever way that baseline runs.
    /// </summary>
    [Fact]
    public void AShiftedRunInAColumnMovesAcrossTheColumn()
    {
        Measured(6.0, () =>
        {
            TextItem Column(double shift)
            {
                TextItem text = TwoRuns(shift);
                text.WritingMode = TextWritingMode.VerticalRl;
                return text;
            }

            GlyphBox plain = Glyph(Column(0.0), 1);
            GlyphBox raised = Glyph(Column(0.5), 1);

            Assert.Equal(5.0, raised.X - plain.X, 9);
            Assert.Equal(plain.Y, raised.Y, 9);
            Assert.Equal(plain.Advance, raised.Advance, 9);
        });
    }

    // ---------------------------------------------------------------- the reader

    /// <summary>
    /// **The gap, established on the reading side: `baseline-shift` used to be a reported loss.** A file with a
    /// superscript imported with the superscript on the base line and a warning to say the baseline was not held.
    /// It is now a member, and the run that states one is a run of its own - not a second block, because SVG's own
    /// rule is that a shifted run does not grow the line box and the model states that with a run.
    /// </summary>
    [Fact]
    public void ABaselineShiftIsKeptOnTheRunAndIsNotReportedAsALoss()
    {
        SvgImportResult result = Read(
            "<text x=\"10\" y=\"20\" font-size=\"10\">a<tspan baseline-shift=\"super\">b</tspan></text>");

        TextItem text = Sole(result);
        Assert.Equal(2, text.Runs.Count);
        Assert.Equal(0.0, text.Runs[0].BaselineShift, 9);
        Assert.Equal(0.5, text.Runs[1].BaselineShift, 9);

        Assert.DoesNotContain(result.Warnings, warning =>
            warning.Contains("baseline-shift", StringComparison.Ordinal));
    }

    /// <summary>
    /// **The value space the property has, resolved to the one unit the model keeps.** A bare number and an `em`
    /// are that many ems, an `ex` is half of one, a percentage is per cent, and the two relative keywords are the
    /// numbers this reader gives them - positive for `super`, negative for `sub`. Every one of these is the
    /// fraction of the run's own em the layout then multiplies by the run's own size.
    /// </summary>
    [Theory]
    [InlineData("super", 0.5)]
    [InlineData("sub", -0.5)]
    [InlineData("baseline", 0.0)]
    [InlineData("0.25em", 0.25)]
    [InlineData("0.25", 0.25)]
    [InlineData("25%", 0.25)]
    [InlineData("0.5ex", 0.25)]
    [InlineData("-10", -10.0)]
    public void EveryValueThePropertyHasIsResolvedToAFractionOfTheEm(string written, double wanted)
    {
        SvgImportResult result = Read(
            $"<text x=\"10\" y=\"20\" font-size=\"10\">a<tspan baseline-shift=\"{written}\">b</tspan></text>");

        Assert.Equal(wanted, Sole(result).Runs[1].BaselineShift, 9);
        Assert.DoesNotContain(result.Warnings, warning =>
            warning.Contains("baseline-shift", StringComparison.Ordinal));
    }

    /// <summary>
    /// **A file that says nothing about a baseline and one that says `baseline` hold the same run.** `baseline` is
    /// the initial value, so it is absence on the model and nothing reaches the drawing or the bytes - which is what
    /// stops a document written by a tool that states every property from growing a member on every run.
    /// </summary>
    [Fact]
    public void BaselineIsTheInitialValueAndIsNotAMember()
    {
        SvgImportResult plain = Read("<text x=\"10\" y=\"20\" font-size=\"10\">a<tspan>b</tspan></text>");
        SvgImportResult stated = Read(
            "<text x=\"10\" y=\"20\" font-size=\"10\" baseline-shift=\"baseline\">a<tspan>b</tspan></text>");

        Assert.Equal(0.0, Sole(plain).Runs[1].BaselineShift, 9);
        Assert.Equal(0.0, Sole(stated).Runs[1].BaselineShift, 9);
        Assert.Empty(stated.Warnings);
    }

    /// <summary>
    /// **A value the reader cannot resolve is reported, and the inherited value stands.** A file that names a
    /// baseline in some dialect this reader does not know is a file whose drawing it is approximating, and saying
    /// so is the rule the whole reader follows; reading it as "no shift" quietly would leave a superscript on the
    /// line looking deliberate.
    /// </summary>
    [Fact]
    public void AnUnreadableBaselineShiftIsReportedAndTheInheritedValueStands()
    {
        SvgImportResult result = Read(
            "<text x=\"10\" y=\"20\" font-size=\"10\" baseline-shift=\"0.5em\">" +
            "a<tspan baseline-shift=\"halfway\">b</tspan></text>");

        Assert.Equal(0.5, Sole(result).Runs[1].BaselineShift, 9);
        Assert.Contains(result.Warnings, warning =>
            warning.Contains("baseline-shift=\"halfway\"", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- the bytes the writer emits

    /// <summary>
    /// **The gap, established on the writing side: a shift the model holds reaches the file.** The writer wrote no
    /// `baseline-shift` at all, so a superscript that had been imported came back on the line. The value is the
    /// length the model's fraction names - 0.5 em of a 10pt run is 5 - and the base run beside it states nothing.
    /// </summary>
    [Fact]
    public void AShiftedRunReachesTheExportedBytes()
    {
        string svg = Measured(6.0, () =>
        {
            CadDocument document = CadDocument.CreateDefault();
            document.Artboards[0].Layers[0].AddItem(TwoRuns(0.5));
            return SvgWriter.Write(document);
        });

        Assert.Contains("baseline-shift=\"5\"", svg, StringComparison.Ordinal);

        // Only the run that asks for one states it: the base run's tspan is the plain one.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(svg, "baseline-shift=").Cast<System.Text.RegularExpressions.Match>());
    }

    /// <summary>
    /// **A document that shifts nothing serialises to the same bytes.** This is the absent-at-default rule at the
    /// heart of this repository's fidelity promise: the writer states the property only where a run asks for a
    /// shift, so every document written before this existed exports exactly as it did, and a run put back on the
    /// line exports as the plain run it now is.
    /// </summary>
    [Fact]
    public void ABlockThatShiftsNothingWritesTheSameBytes()
    {
        Measured(6.0, () =>
        {
            CadDocument document = CadDocument.CreateDefault();
            document.Artboards[0].Layers[0].AddItem(TwoRuns(0.0));
            string plain = SvgWriter.Write(document);

            Assert.DoesNotContain("baseline-shift", plain, StringComparison.Ordinal);

            document.AllItems().OfType<TextItem>().Single().Runs[1].BaselineShift = 0.5;
            Assert.Contains("baseline-shift", SvgWriter.Write(document), StringComparison.Ordinal);

            // Put back, and the file is what it was - character for character - rather than merely equivalent.
            document.AllItems().OfType<TextItem>().Single().Runs[1].BaselineShift = 0.0;
            Assert.Equal(plain, SvgWriter.Write(document));
        });
    }

    /// <summary>
    /// **The value comes back as the value it was.** A shift written in ems, a shift written as a length in the
    /// file's own user units, and a shift written as a percentage of the line's height all read back as the same
    /// fraction of the run's em, and writing that fraction back produces the same length - so a file's own
    /// spelling is not turned into a different drawing by the round trip.
    /// </summary>
    [Theory]
    [InlineData("baseline-shift=\"5\"", 0.5)]
    [InlineData("baseline-shift=\"-5\"", -0.5)]
    [InlineData("baseline-shift=\"50%\"", 0.5)]
    [InlineData("baseline-shift=\"super\"", 0.5)]
    public void AShiftWrittenAsTheFileSpellsItSurvivesARoundTrip(string attribute, double wanted)
    {
        Measured(6.0, () =>
        {
            SvgImportResult first = Read(
                $"<text x=\"10\" y=\"20\" font-size=\"10\">a<tspan {attribute}>b</tspan></text>");
            TextItem imported = Sole(first);
            Assert.Equal(wanted, imported.Runs[1].BaselineShift, 9);

            CadDocument document = CadDocument.CreateDefault();
            document.Artboards[0].Layers[0].AddItem(imported);
            SvgImportResult second = SvgReader.Read(SvgWriter.Write(document));

            Assert.Equal(wanted, Sole(second).Runs[1].BaselineShift, 9);
        });
    }

    /// <summary>
    /// **And the geometry comes back too, not only the member.** The exported file re-read places the superscript's
    /// glyph where the original document's layout had it, measured against the base character in the same file. A
    /// round trip that preserved the fraction but lost the sign, the size or the association with the run would pass
    /// the member's own test and fail here.
    /// </summary>
    [Fact]
    public void TheShiftedPositionSurvivesARoundTrip()
    {
        Measured(6.0, () =>
        {
            CadDocument document = CadDocument.CreateDefault();
            document.Artboards[0].Layers[0].AddItem(TwoRuns(0.5));

            TextItem source = document.AllItems().OfType<TextItem>().Single();
            double wanted = Glyph(source, 0).Y - Glyph(source, 1).Y;

            TextItem back = Sole(SvgReader.Read(SvgWriter.Write(document)));

            Assert.Equal(wanted, Glyph(back, 0).Y - Glyph(back, 1).Y, 6);
        });
    }

    /// <summary>
    /// **The sidecar keeps the shift, and keeps it out of a document that has none.** The model's own serialiser is
    /// the third half of the fidelity promise - a document saved and reopened has to hold what it held - and the
    /// member is absent at its initial value so an ordinary document's bytes do not change.
    /// </summary>
    [Fact]
    public void TheSidecarHoldsTheShiftAndOmitsItAtTheInitialValue()
    {
        CadDocument document = CadDocument.CreateDefault();
        document.Artboards[0].Layers[0].AddItem(TwoRuns(0.0));

        string plain = VCCad.Core.Serialization.VccadDocumentSerializer.Serialize(document);
        Assert.DoesNotContain("BaselineShift", plain, StringComparison.Ordinal);

        document.AllItems().OfType<TextItem>().Single().Runs[1].BaselineShift = 0.5;
        string written = VCCad.Core.Serialization.VccadDocumentSerializer.Serialize(document);
        Assert.Contains("BaselineShift", written, StringComparison.Ordinal);

        CadDocument back = VCCad.Core.Serialization.VccadDocumentSerializer.Deserialize(written);
        Assert.Equal(0.5, back.AllItems().OfType<TextItem>().Single().Runs[1].BaselineShift, 9);
    }
}
