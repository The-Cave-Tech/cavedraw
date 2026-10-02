using VCCad.Core.Model;
using VCCad.Core.Svg;
using VCCad.Core.Text;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// `baseline-shift`, asserted as **the geometry the layout computes and the bytes the writer emits** (#128).
///
/// This file exists because the suite could not tell the feature apart from its absence. Measured on
/// `a3e8656`: replacing `TextLayoutEngine.BaselineShiftFor`'s body with `0.0 * run.FontSize` - so the layout stops
/// honouring the shift entirely - left **1961 Core tests and 1297 App tests green**, and replacing the SVG reader's
/// bare-number unit with a plain `1.0` did the same. `SvgTextTests.PropertiesTheModelCannotHoldAreReported` asserts
/// that a run's `BaselineShift` member is 0.5 for `baseline-shift="super"`, which is a member and not a drawing: a
/// superscript that reads back correctly and is then drawn on the base line passes it. The geometry test that would
/// have caught this was written and reverted in the same session and never restored, which is the exact shape
/// `AGENTS.md` §1.1 warns about - CI cannot guard what no test asserts.
///
/// So the assertions here are deliberately of the two kinds that can fail: **where a glyph lands** and **what the
/// file says**. A member that round-trips perfectly while the layout ignores it fails on the first; a layout that
/// moves a glyph the file cannot describe fails on the second.
///
/// Where a number has to be exact the tests install a fixed measurer - the model's own measurement seam - so the
/// arithmetic under test is the file's and not the machine's.
/// </summary>
public class SvgBaselineShiftLayoutTests
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

    private static GlyphBox Glyph(TextItem text, int index)
        => TextLayoutEngine.Compute(text).Glyphs.Single(glyph => glyph.Index == index);

    /// <summary>
    /// One block of "a" then "b", where "b" carries <paramref name="shift"/> ems of baseline - the smallest block
    /// that can say "this glyph moved and that one did not".
    /// </summary>
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

    // ---------------------------------------------------------------- the geometry the layout computes

    /// <summary>
    /// **A raised run's glyph leaves the baseline and the pen does not move.** This is the assertion the suite was
    /// missing: the shift is measured against the *same block with no shift*, so it names the effect and not the
    /// absolute arithmetic. Half a 10pt em is 5pt up; the base character beside it has not moved; and the raised
    /// character's own x and advance are untouched, because a shift is across the line and never along it. A layout
    /// that ignored the member fails the first assertion, and one that moved the pen up fails the last two.
    /// </summary>
    [Fact]
    public void ARaisedGlyphLeavesTheBaselineAndThePenDoesNotMove()
    {
        Measured(6.0, () =>
        {
            GlyphBox plain = Glyph(TwoRuns(0.0), 1);
            GlyphBox raised = Glyph(TwoRuns(0.5), 1);

            Assert.Equal(5.0, plain.Y - raised.Y, 9);
            Assert.Equal(plain.X, raised.X, 9);
            Assert.Equal(plain.Advance, raised.Advance, 9);

            // And the run that said nothing is where it was: the shift is the run's own, not the line's.
            Assert.Equal(Glyph(TwoRuns(0.0), 0).Y, Glyph(TwoRuns(0.5), 0).Y, 9);
        });
    }

    /// <summary>
    /// **A lowered run's glyph sits below the line, by the same amount and in the other direction.** The sign is
    /// half the property's whole vocabulary, and reading `sub` as "no shift" leaves a subscript looking deliberate.
    /// </summary>
    [Fact]
    public void ALoweredGlyphSitsBelowTheBaseline()
    {
        Measured(6.0, () =>
        {
            GlyphBox plain = Glyph(TwoRuns(0.0), 1);
            GlyphBox lowered = Glyph(TwoRuns(-0.5), 1);

            Assert.Equal(5.0, lowered.Y - plain.Y, 9);
            Assert.Equal(plain.X, lowered.X, 9);
        });
    }

    /// <summary>
    /// **The shift is the run's own em, so it follows the run's size.** A 20pt superscript beside a 10pt one is
    /// twice as far off the line. One number stored in points could not say that, and a fixed offset that happened
    /// to look right at one size is the mistake this pins.
    /// </summary>
    [Fact]
    public void TheShiftIsTheRunsOwnEmAndFollowsItsSize()
    {
        Measured(6.0, () =>
        {
            Assert.Equal(5.0, Glyph(TwoRuns(0.0, 10), 1).Y - Glyph(TwoRuns(0.5, 10), 1).Y, 9);
            Assert.Equal(10.0, Glyph(TwoRuns(0.0, 20), 1).Y - Glyph(TwoRuns(0.5, 20), 1).Y, 9);
        });
    }

    /// <summary>
    /// **A shifted run does not grow the line box.** Every line's top, ascent, descent and baseline, the block's
    /// height and the caret positions are exactly what the same block has with no shift - so a superscript does not
    /// push the lines after it down the page, and styling one word cannot reflow a paragraph. This is SVG's own
    /// rule, and it is why the member is per run rather than a second block with a smaller ascent.
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
            Assert.Equal(plain.CaretX, raised.CaretX);
        });
    }

    /// <summary>
    /// **A shifted run in a vertical column moves across the column, not along it.** A vertical baseline runs down
    /// the page, so a superscript sits beside its column line - the page's +x - while its position down the column
    /// and the next glyph's are untouched. A shift leaves the glyph's own baseline whichever way that baseline runs.
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

    /// <summary>
    /// **The member on the model reaches the geometry through the operation's own edit path.** A run set to
    /// `super` through <see cref="DocumentSession.ApplyTextFieldsAt"/> - the one call the Text pane's combo and
    /// `text.update` both make - has to move its glyph, or the operation is a member write with a drawing attached
    /// to nothing. Measured against the same run before the edit.
    /// </summary>
    [Fact]
    public void TheShiftSetOnTheRunReachesTheGeometry()
    {
        Measured(6.0, () =>
        {
            TextItem text = TwoRuns(0.0);
            double before = Glyph(text, 1).Y;

            text.Runs[1].BaselineShift = 0.5;

            Assert.Equal(5.0, before - Glyph(text, 1).Y, 9);
        });
    }

    // ---------------------------------------------------------------- the reader

    /// <summary>
    /// **The reader resolves the property onto the run, and does not report it as a gap.** A file with a
    /// superscript used to import with the superscript on the base line and a warning - *"a baseline the model does
    /// not hold"* - to say so. The run that states a shift is a run of its own, not a second block: SVG's own rule
    /// is that a shifted run does not grow the line box, and a run is how the model says that.
    /// </summary>
    [Fact]
    public void TheReaderKeepsTheShiftOnTheRunAndDoesNotReportItAsAGap()
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
    /// **Every spelling the property has resolves to the one fraction of the em the model keeps.** A bare number is
    /// a length in the file's own user units - which is what the writer emits and what every other bare number in
    /// an SVG file is - an `em` is the run's own em, an `ex` is half of one, a percentage is per cent, and the two
    /// relative keywords are the numbers this reader gives them. For a 10pt run, five user units and half an em are
    /// the same shift; a reader that took the bare number as ems would call it five ems.
    /// </summary>
    [Theory]
    [InlineData("super", 0.5)]
    [InlineData("sub", -0.5)]
    [InlineData("0.25em", 0.25)]
    [InlineData("2.5", 0.25)]
    [InlineData("2.5px", 0.25)]
    [InlineData("25%", 0.25)]
    [InlineData("0.5ex", 0.25)]
    [InlineData("-50", -5.0)]
    public void TheReaderResolvesEverySpellingToAFractionOfTheEm(string written, double wanted)
    {
        SvgImportResult result = Read(
            $"<text x=\"10\" y=\"20\" font-size=\"10\">a<tspan baseline-shift=\"{written}\">b</tspan></text>");

        Assert.Equal(wanted, Sole(result).Runs[1].BaselineShift, 9);
        Assert.DoesNotContain(result.Warnings, warning =>
            warning.Contains("baseline-shift", StringComparison.Ordinal));
    }

    /// <summary>
    /// **A file that says nothing about a baseline and one that says `baseline` hold the same run.** `baseline` is
    /// the initial value, so it is zero on the model. The two runs differ in size, because a reader that merged
    /// equal neighbours would otherwise fold the two `tspan`s into the one text run and this would be asserting
    /// about a run that says nothing at all.
    /// </summary>
    [Fact]
    public void BaselineIsTheInitialValueAndIsNotAMember()
    {
        SvgImportResult result = Read(
            "<text x=\"10\" y=\"20\" font-size=\"10\">a" +
            "<tspan baseline-shift=\"baseline\">b</tspan>" +
            "<tspan baseline-shift=\"super\" font-size=\"14\">c</tspan></text>");

        TextItem text = Sole(result);
        Assert.Equal(2, text.Runs.Count);
        Assert.Equal(0.0, text.Runs[0].BaselineShift, 9);
        Assert.Equal(0.5, text.Runs[1].BaselineShift, 9);
        Assert.Empty(result.Warnings);
    }

    /// <summary>
    /// **The property inherits, and a child can undo it.** A `tspan` that states nothing hangs from the baseline its
    /// parent asked for; one that says `baseline` puts itself back on the line. That is the whole reason the member
    /// is a nullable "keep what was inherited" rather than a value defaulted at every element. The runs are made
    /// distinct by their sizes so the reader cannot fold them together and hide the middle one.
    /// </summary>
    [Fact]
    public void TheShiftInheritsAndAChildCanUndoIt()
    {
        SvgImportResult result = Read(
            "<text x=\"10\" y=\"20\" font-size=\"10\" baseline-shift=\"super\">" +
            "a<tspan font-size=\"11\">b</tspan><tspan baseline-shift=\"baseline\" font-size=\"12\">c</tspan></text>");

        TextItem text = Sole(result);
        Assert.Equal(3, text.Runs.Count);

        // The child that says `baseline` is back on the line while the two that inherit stay raised.
        Assert.Equal(0.5, text.Runs[0].BaselineShift, 9);
        Assert.Equal(0.5, text.Runs[1].BaselineShift, 9);
        Assert.Equal(0.0, text.Runs[2].BaselineShift, 9);
    }

    /// <summary>
    /// **A shift named as a length is a length in the file's own units, not a number of ems.** This is the unit the
    /// writer emits and the unit every other bare number in an SVG file is, so a reader that took it as ems would
    /// report five ems for `baseline-shift="5"` on a 10pt run and lose the font size on the way in - and then lose
    /// it again on the way out, because the writer would multiply the five by ten. The two spellings are asserted
    /// side by side because that is exactly the confusion: `5` and `0.5em` are the same shift for a 10pt run.
    /// </summary>
    [Fact]
    public void AShiftNamedAsALengthIsResolvedAgainstTheRunsOwnSize()
    {
        SvgImportResult length = Read(
            "<text x=\"10\" y=\"20\" font-size=\"10\">a<tspan baseline-shift=\"5\">b</tspan></text>");
        SvgImportResult ems = Read(
            "<text x=\"10\" y=\"20\" font-size=\"10\">a<tspan baseline-shift=\"0.5em\">b</tspan></text>");

        Assert.Equal(0.5, Sole(length).Runs[1].BaselineShift, 9);
        Assert.Equal(0.5, Sole(ems).Runs[1].BaselineShift, 9);

        // And the same length on a bigger run is a smaller fraction of its em, which is what "the run's own size"
        // means: 5 user units of a 20pt run is a quarter of an em.
        SvgImportResult larger = Read(
            "<text x=\"10\" y=\"20\" font-size=\"20\">a<tspan baseline-shift=\"5\">b</tspan></text>");
        Assert.Equal(0.25, Sole(larger).Runs[1].BaselineShift, 9);
    }

    /// <summary>
    /// **A value the reader cannot resolve is reported, and the inherited value stands.** A baseline named in a
    /// dialect this reader does not know is a drawing it is approximating, and the reader's rule is to say so;
    /// reading it quietly as "no shift" leaves a superscript on the line looking deliberate.
    /// </summary>
    [Fact]
    public void AnUnreadableShiftIsReportedAndTheInheritedValueStands()
    {
        SvgImportResult result = Read(
            "<text x=\"10\" y=\"20\" font-size=\"10\" baseline-shift=\"0.5em\">" +
            "a<tspan baseline-shift=\"halfway\" font-size=\"11\">b</tspan></text>");

        Assert.Equal(0.5, Sole(result).Runs[1].BaselineShift, 9);
        Assert.Contains(result.Warnings, warning =>
            warning.Contains("baseline-shift=\"halfway\"", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- the bytes the writer emits

    /// <summary>
    /// **A shift the model holds reaches the file.** The writer stated no `baseline-shift` at all, so a superscript
    /// that had been imported came back on the line. The value is the length the model's fraction names - half an em
    /// of a 10pt run is 5 - and only the run that asks for a shift states one.
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
        Assert.Equal(
            1, System.Text.RegularExpressions.Regex.Matches(svg, "baseline-shift=").Count);
    }

    /// <summary>
    /// **A document that shifts nothing writes the same bytes.** The absent-at-default rule this repository's
    /// fidelity promise rests on: the property is stated only where a run asks for a shift, so every document
    /// written before the member existed exports exactly as it did - and a run put back on the line exports as the
    /// plain run it now is, character for character and not merely equivalently.
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

            TextItem text = document.AllItems().OfType<TextItem>().Single();
            text.Runs[1].BaselineShift = 0.5;
            Assert.Contains("baseline-shift", SvgWriter.Write(document), StringComparison.Ordinal);

            text.Runs[1].BaselineShift = 0.0;
            Assert.Equal(plain, SvgWriter.Write(document));
        });
    }

    /// <summary>
    /// **A file's own spelling of a shift survives the round trip as the same fraction**, whichever of the four
    /// spellings it used: a length in user units, an em, a percentage, or a relative keyword. The writer emits a
    /// length, and the reader has to read that length back as the fraction it came from - a unit mismatch between
    /// the two loses the font size on the way in and again on the way out.
    /// </summary>
    [Theory]
    [InlineData("baseline-shift=\"5\"", 0.5)]
    [InlineData("baseline-shift=\"-5\"", -0.5)]
    [InlineData("baseline-shift=\"50%\"", 0.5)]
    [InlineData("baseline-shift=\"super\"", 0.5)]
    [InlineData("baseline-shift=\"0.5em\"", 0.5)]
    public void AShiftSurvivesARoundTripAsTheSameFraction(string attribute, double wanted)
    {
        Measured(6.0, () =>
        {
            TextItem imported = Sole(Read(
                $"<text x=\"10\" y=\"20\" font-size=\"10\">a<tspan {attribute}>b</tspan></text>"));
            Assert.Equal(wanted, imported.Runs[1].BaselineShift, 9);

            CadDocument document = CadDocument.CreateDefault();
            document.Artboards[0].Layers[0].AddItem(imported);

            Assert.Equal(wanted, Sole(SvgReader.Read(SvgWriter.Write(document))).Runs[1].BaselineShift, 9);
        });
    }

    /// <summary>
    /// **And the geometry comes back, not only the member.** The re-read file places the superscript's glyph where
    /// the original document's layout had it, measured against the base character in the same file. A round trip
    /// that preserved the fraction but lost its sign, its size or its association with the run passes the member's
    /// own test and fails here.
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
    /// **The sidecar holds the shift, and omits it at its initial value.** The model's own serialiser is the third
    /// half of the fidelity promise - a document saved and reopened holds what it held - and the member is absent
    /// at its initial value, so an ordinary document's bytes do not change.
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
