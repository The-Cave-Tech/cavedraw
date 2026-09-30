using VCCad.Core.Model;
using VCCad.Core.Text;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// The layout engine: one layout, one baseline per line, metrics from the faces actually on the line.
///
/// Changing a font in the middle of a paragraph is the case that shows every one of these up. A line
/// carrying a 12pt word and a 36pt one has ONE baseline; the line box is the taller face's, plus the
/// leading the block asks for; and the pen moves by each run's own advance.
/// </summary>
public class TextLayoutTests : IDisposable
{
    /// <summary>Fixed metrics, so every number below is arithmetic rather than a shaper's opinion.</summary>
    private sealed class Fixed : ITextMetrics
    {
        public IReadOnlyList<double> Advances(TextRun run)
            => Enumerable.Repeat(run.FontSize * 0.5, run.Text.Length).ToList();

        public double Ascent(TextRun run) => run.FontSize * 0.8;

        public double Descent(TextRun run) => run.FontSize * 0.2;
    }

    public TextLayoutTests() => TextMeasurement.Current = new Fixed();

    public void Dispose() => TextMeasurement.Current = null;

    private static TextItem Block(double spacing = 1.0, TextAlignment alignment = TextAlignment.Left)
    {
        return new TextItem { Origin = new Point2D(0, 0), LineSpacing = spacing, Alignment = alignment };
    }

    private static TextRun Run(string text, double size) => new() { Text = text, FontSize = size, FontFamily = "Test" };

    [Fact]
    public void TwoFacesOnOneLineShareTheLineAndItsBaseline()
    {
        TextItem text = Block();
        text.Runs.Add(Run("AB", 12));   // ascent 9.6, descent 2.4, advance 6 each
        text.Runs.Add(Run("CD", 24));   // ascent 19.2, descent 4.8, advance 12 each

        TextLayout layout = TextLayoutEngine.Compute(text);

        TextLine line = Assert.Single(layout.Lines);

        // The line is the taller face's box: 19.2 above the baseline, 4.8 below, one line.
        Assert.Equal(19.2, line.Ascent, 6);
        Assert.Equal(4.8, line.Descent, 6);
        Assert.Equal(19.2, line.Baseline, 6);
        Assert.Equal(36, line.Width, 6);

        // Both runs are on that one line, so both draw to that one baseline.
        Assert.Equal(2, layout.Runs.Count);
        Assert.All(layout.Runs, box => Assert.Equal(0, box.Line));
        Assert.Equal(12, layout.Runs[1].X, 6);
    }

    /// <summary>The rule that makes mixed faces sit on a line rather than stair-step.</summary>
    [Fact]
    public void EachRunIsPlacedByItsOwnAscentBelowTheSharedBaseline()
    {
        TextItem text = Block();
        TextRun small = Run("AB", 12);
        TextRun large = Run("CD", 24);
        text.Runs.Add(small);
        text.Runs.Add(large);

        TextLayout layout = TextLayoutEngine.Compute(text);
        TextLine line = Assert.Single(layout.Lines);

        double smallTop = TextLayoutEngine.RunTop(small, line);
        double largeTop = TextLayoutEngine.RunTop(large, line);

        // The tops differ by exactly the difference in their ascents, so the baselines coincide.
        Assert.Equal(9.6, smallTop, 6);
        Assert.Equal(0, largeTop, 6);
        Assert.Equal(line.Baseline, smallTop + TextMeasurement.Ascent(small), 6);
        Assert.Equal(line.Baseline, largeTop + TextMeasurement.Ascent(large), 6);
    }

    [Fact]
    public void ALineIsMeasuredFromTheFacesOnItNotTheBlocksLargest()
    {
        TextItem text = Block();
        text.Runs.Add(Run("BIG", 36));
        text.Runs.Add(Run("\n", 36));
        text.Runs.Add(Run("small", 12));

        TextLayout layout = TextLayoutEngine.Compute(text);

        Assert.Equal(2, layout.Lines.Count);
        Assert.Equal(36, layout.Lines[0].Height, 6);
        Assert.Equal(12, layout.Lines[1].Height, 6);
        Assert.Equal(48, layout.Height, 6);

        // Measuring both as the block's largest font would give 36 x 1.2 twice.
        Assert.NotEqual(86.4, layout.Height, 1);
    }

    [Fact]
    public void LeadingIsSplitAboveAndBelowTheGlyphs()
    {
        TextItem text = Block(spacing: 2.0);
        text.Runs.Add(Run("AB", 12));

        TextLayout layout = TextLayoutEngine.Compute(text);
        TextLine line = Assert.Single(layout.Lines);

        // 12pt of glyphs, doubled to 24: six above the ascent and six below the descent.
        Assert.Equal(9.6 + 6, line.Ascent, 6);
        Assert.Equal(2.4 + 6, line.Descent, 6);
        Assert.Equal(24, line.Height, 6);

        // So the baseline is half the leading below where it would otherwise be.
        Assert.Equal(15.6, line.Baseline, 6);
    }

    [Fact]
    public void CaretsFollowTheRunBoundaryWhenAFaceChanges()
    {
        TextItem text = Block();
        text.Runs.Add(Run("AB", 12));   // 6 each
        text.Runs.Add(Run("CD", 24));   // 12 each

        TextLayout layout = TextLayoutEngine.Compute(text);

        Assert.Equal(0, layout.XOf(0), 6);
        Assert.Equal(6, layout.XOf(1), 6);
        Assert.Equal(12, layout.XOf(2), 6);   // the face changes here
        Assert.Equal(24, layout.XOf(3), 6);
        Assert.Equal(36, layout.XOf(4), 6);
        Assert.All(new[] { 0, 1, 2, 3, 4 }, i => Assert.Equal(0, layout.LineOf(i)));
    }

    [Fact]
    public void AnExplicitBreakStartsANewLineAndKeepsTheCaretOnTheRightOne()
    {
        TextItem text = Block();
        text.Runs.Add(Run("AB\n", 12));
        text.Runs.Add(Run("CD", 24));

        TextLayout layout = TextLayoutEngine.Compute(text);

        Assert.Equal(2, layout.Lines.Count);
        Assert.Equal(0, layout.LineOf(0));
        Assert.Equal(0, layout.LineOf(2));   // after "AB", still on the first line
        Assert.Equal(1, layout.LineOf(3));   // "C" is on the second
        Assert.Equal(0, layout.XOf(3), 6);
    }

    [Fact]
    public void FrameWrappingKeepsEachWrappedLinesOwnMetrics()
    {
        TextItem text = Block();
        text.FrameWidth = 12;   // two 12pt characters per line
        text.Runs.Add(Run("AA", 12));
        text.Runs.Add(Run("BB", 24));

        TextLayout layout = TextLayoutEngine.Compute(text);

        Assert.True(layout.Lines.Count >= 2, $"expected a wrap, got {layout.Lines.Count} line(s)");

        // The line carrying the 24pt face is taller than the one that is not.
        double tallest = layout.Lines.Max(l => l.Height);
        double shortest = layout.Lines.Min(l => l.Height);
        Assert.Equal(24, tallest, 6);
        Assert.True(shortest < tallest, $"lines should differ in height: {shortest} vs {tallest}");
        Assert.Equal(12, layout.Width, 6);
    }

    [Fact]
    public void AlignmentShiftsTheWholeLineAndItsCarets()
    {
        TextItem text = Block(alignment: TextAlignment.Center);
        text.Runs.Add(Run("AA", 12));   // 12 wide
        text.Runs.Add(Run("\n", 12));
        text.Runs.Add(Run("BBBB", 12)); // 24 wide, so the block is 24 and the first line shifts

        TextLayout layout = TextLayoutEngine.Compute(text);

        Assert.Equal(24, layout.Width, 6);
        Assert.Equal(6, layout.XOf(0), 6);       // (24 - 12) / 2
        Assert.Equal(0, layout.Lines[1].Width - 24, 6);
        Assert.Equal(0, layout.CaretX[3], 6);    // the second line starts flush
    }

    [Fact]
    public void ParagraphSpacingFollowsAnExplicitBreakOnly()
    {
        TextItem text = Block();
        text.ParagraphSpacing = 10;
        text.Runs.Add(Run("AA\nBB", 12));

        TextLayout layout = TextLayoutEngine.Compute(text);

        Assert.Equal(2, layout.Lines.Count);
        Assert.Equal(0, layout.Lines[0].Top, 6);
        Assert.Equal(12 + 10, layout.Lines[1].Top, 6);
        Assert.Equal(34, layout.Height, 6);
    }

    [Fact]
    public void AnEmptyBlockStillHasOneLineWithAHeight()
    {
        TextLayout layout = TextLayoutEngine.Compute(Block());

        TextLine line = Assert.Single(layout.Lines);
        Assert.True(line.Height > 0, "an empty line the caret can sit on still has a height");
        Assert.Equal(1, layout.CaretX.Count);
        Assert.Equal(0, layout.CaretX[0], 6);
    }

    /// <summary>
    /// The real flow: one run, a range of it restyled, and the block laid out afterwards.
    ///
    /// Changing a font in the middle of a word splits the run in three, and the line those pieces
    /// sit on has to be one line, on one baseline, measured by the largest face in it.
    /// </summary>
    [Fact]
    public void RestylingPartOfARunKeepsItOnOneLineAndOneBaseline()
    {
        TextItem text = Block();
        text.Runs.Add(Run("Hello world", 12));

        // " lo" set at 30pt, which is wider and taller than the words either side of it.
        TextEditing.ApplyStyle(text, 4, 8, run => run.FontSize = 30);

        Assert.Equal(3, text.Runs.Count);
        Assert.Equal("Hello world", TextEditing.GetText(text));

        TextLayout layout = TextLayoutEngine.Compute(text);

        TextLine line = Assert.Single(layout.Lines);

        // The 30pt piece decides the line box; every piece sits on that one baseline.
        Assert.Equal(30 * 0.8, line.Ascent, 6);
        Assert.Equal(30 * 0.2, line.Descent, 6);

        Assert.Equal(3, layout.Runs.Count);
        Assert.All(layout.Runs, box => Assert.Equal(0, box.Line));

        foreach (TextRunBox box in layout.Runs)
        {
            TextRun run = text.Runs[box.Run];
            double top = TextLayoutEngine.RunTop(run, line);
            Assert.Equal(line.Baseline, top + TextMeasurement.Ascent(run), 6);
        }

        // The three pieces run left to right without a gap, and the middle one is the widest.
        Assert.Equal(0, layout.Runs[0].X, 6);
        Assert.Equal(layout.Runs[0].X + layout.Runs[0].Width, layout.Runs[1].X, 6);
        Assert.Equal(layout.Runs[1].X + layout.Runs[1].Width, layout.Runs[2].X, 6);
        Assert.Equal(layout.Width, layout.Runs[2].X + layout.Runs[2].Width, 6);
    }
}
