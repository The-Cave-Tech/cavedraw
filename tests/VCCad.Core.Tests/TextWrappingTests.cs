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

    private static string TextOf(TextWrapping.LineRange line)
    {
        TextMeasurement.Current ??= new FixedMetrics(1);
        var item = new TextItem();
        item.Runs.Add(new TextRun { Text = "aaa bbb", FontSize = 10, FontFamily = "Test" });
        (string flat, _) = TextWrapping.Flatten(item);
        return flat.Substring(line.Start, line.Length);
    }
}
