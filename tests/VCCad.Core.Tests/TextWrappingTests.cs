using VCCad.Core.Model;
using VCCad.Core.Text;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// The wrap rule is shared: the model's bounds, the canvas's caret and edit box, and the
/// PDF exporter all break lines here. If they disagreed, the exported page would not be
/// the page that was on screen.
/// </summary>
public class TextWrappingTests : IDisposable
{
    private sealed class FixedMetrics : ITextMetrics
    {
        private readonly double _perChar;

        public FixedMetrics(double perChar) => _perChar = perChar;

        public IReadOnlyList<double> Advances(TextRun run)
            => Enumerable.Repeat(_perChar, run.Text.Length).ToList();

        public double Ascent(TextRun run) => 8;

        public double Descent(TextRun run) => 2;
    }

    private static TextItem Block(string text, double frameWidth = 0)
    {
        var item = new TextItem { FrameWidth = frameWidth };
        item.Runs.Add(new TextRun { Text = text, FontSize = 10, FontFamily = "Test" });
        return item;
    }

    public void Dispose() => TextMeasurement.Current = null;

    [Fact]
    public void WithoutAFrameTheWholeBlockIsOneLine()
    {
        TextMeasurement.Current = new FixedMetrics(1);
        Assert.Single(TextWrapping.Lines(Block("a very long line that would otherwise wrap")));
    }

    [Fact]
    public void AFrameBreaksAtTheLastSpaceThatFits()
    {
        TextMeasurement.Current = new FixedMetrics(1);

        // 1pt per character, 10pt frame: "aaa bbb ccc" breaks after "aaa bbb".
        List<TextWrapping.LineRange> lines = TextWrapping.Lines(Block("aaa bbb ccc", frameWidth: 10));
        Assert.Equal(2, lines.Count);
        Assert.Equal(0, lines[0].Start);
        Assert.Equal(7, lines[0].Length);
        Assert.Equal(8, lines[1].Start);
        Assert.Equal(3, lines[1].Length);
    }

    [Fact]
    public void AWordWiderThanTheFrameIsBrokenRatherThanOverflowing()
    {
        TextMeasurement.Current = new FixedMetrics(1);

        List<TextWrapping.LineRange> lines = TextWrapping.Lines(Block("aaaaaaaaaaaa", frameWidth: 5));
        Assert.True(lines.Count >= 2, "a word wider than the frame must break");
        Assert.All(lines, line => Assert.True(line.Length <= 5 || lines.Count == 1));
    }

    [Fact]
    public void AnExplicitNewlineAlwaysBreaks()
    {
        TextMeasurement.Current = new FixedMetrics(1);

        List<TextWrapping.LineRange> lines = TextWrapping.Lines(Block("ab\ncd"));
        Assert.Equal(2, lines.Count);
        Assert.Equal(0, lines[0].Start);
        Assert.Equal(2, lines[0].Length);
        Assert.Equal(3, lines[1].Start);
        Assert.Equal(2, lines[1].Length);
    }

    [Fact]
    public void ATrailingSpaceDoesNotPushTheNextWordOff()
    {
        TextMeasurement.Current = new FixedMetrics(1);

        // "aaa " is 4 characters; at a 4pt frame the space must not cost a line.
        List<TextWrapping.LineRange> lines = TextWrapping.Lines(Block("aaa bbb", frameWidth: 4));
        Assert.Equal(2, lines.Count);
        Assert.Equal("aaa", TextOf(lines[0]));
    }

    [Fact]
    public void AnEmptyBlockStillHasOneLine()
    {
        TextMeasurement.Current = new FixedMetrics(1);
        Assert.Single(TextWrapping.Lines(Block(string.Empty)));
    }

    /// <summary>
    /// **A line never ends between the halves of a surrogate pair.**
    ///
    /// An astral character is two UTF-16 code units, and the wrap rule counts code units - so a frame that breaks
    /// mid-character used to put a lone high surrogate at the end of one line and a lone low surrogate at the start
    /// of the next. Every reader of a line then had half a character: `Substring` on a segment produced a string an
    /// XML writer rejects outright ("the surrogate pair is invalid"), and the canvas would draw a replacement
    /// glyph. Inkscape's flowed-text test file reaches this exactly - its frame holds text from the supplementary
    /// plane - which is how the round trip found it. See #126.
    /// </summary>
    [Fact]
    public void ALineBreakNeverSplitsASurrogatePair()
    {
        TextMeasurement.Current = new FixedMetrics(1);

        // Four astral characters - eight code units - in a five-unit frame, so the break lands inside a pair.
        const string astral = "\U00010303\U00010303\U00010303\U00010303";
        Assert.Equal(8, astral.Length);

        List<TextWrapping.LineRange> lines = TextWrapping.Lines(Block(astral, frameWidth: 5));

        (string flat, _) = TextWrapping.Flatten(Block(astral, frameWidth: 5));
        Assert.True(lines.Count > 1, "the block must wrap to exercise a break");

        foreach (TextWrapping.LineRange line in lines)
        {
            string piece = flat.Substring(line.Start, line.Length);
            Assert.False(
                piece.Length > 0 && char.IsHighSurrogate(piece[^1]),
                $"a line may not end on a high surrogate; got a piece of {piece.Length} code units");
            Assert.False(
                piece.Length > 0 && char.IsLowSurrogate(piece[0]),
                "a line may not begin on a low surrogate");
        }

        // And nothing is lost or duplicated by moving the break: the lines still cover the whole text.
        Assert.Equal(astral, string.Concat(lines.Select(line => flat.Substring(line.Start, line.Length))));
    }

    private static string TextOf(TextWrapping.LineRange line)
    {
        TextMeasurement.Current ??= new FixedMetrics(1);
        var item = new TextItem();
        item.Runs.Add(new TextRun { Text = "aaa bbb", FontSize = 10, FontFamily = "Test" });
        (string flat, _) = TextWrapping.Flatten(item);
        return flat.Substring(line.Start, line.Length);
    }
}
