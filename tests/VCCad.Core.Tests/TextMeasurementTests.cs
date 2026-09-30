using VCCad.Core.Model;
using VCCad.Core.Text;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// The model's text bounds must come from the installed measurer, not a guess.
///
/// A block's reported height and the box drawn round it have to agree; when the model
/// estimated character widths with `fontSize * 0.6` while the canvas asked the shaper,
/// the two disagreed and the edit box missed the text it was supposed to enclose.
/// </summary>
public class TextMeasurementTests : IDisposable
{
    /// <summary>A measurer where every character is a fixed width, so the arithmetic is checkable.</summary>
    private sealed class FixedMetrics : ITextMetrics
    {
        private readonly double _perChar;
        private readonly double _ascent;

        public FixedMetrics(double perChar, double ascent = 8)
        {
            _perChar = perChar;
            _ascent = ascent;
        }

        public IReadOnlyList<double> Advances(TextRun run)
            => Enumerable.Repeat(_perChar, run.Text.Length).ToList();

        public double Ascent(TextRun run) => _ascent;

        public double Descent(TextRun run) => 2;
    }

    private static TextItem Block(string text, double frameWidth = 0, double spacing = 1.0)
    {
        var item = new TextItem { Origin = new Point2D(0, 0), LineSpacing = spacing, FrameWidth = frameWidth };
        item.Runs.Add(new TextRun { Text = text, FontSize = 10, FontFamily = "Test" });
        return item;
    }

    public void Dispose() => TextMeasurement.Current = null;

    [Fact]
    public void WithoutAMeasurerTheEstimateIsUsed()
    {
        TextMeasurement.Current = null;
        Assert.False(TextMeasurement.IsReal);

        // The fallback is fontSize * 0.6, so ten 10pt characters are 60pt wide.
        Assert.Equal(60, Block("0123456789").BoundingBox().Width, 3);
    }

    [Fact]
    public void TheInstalledMeasurerDecidesTheWidth()
    {
        TextMeasurement.Current = new FixedMetrics(perChar: 5);

        // Ten characters at 5pt each, not the 60pt the estimate would give.
        Assert.Equal(50, Block("0123456789").BoundingBox().Width, 3);
        Assert.True(TextMeasurement.IsReal);
    }

    [Fact]
    public void AFramedBlockKeepsItsFrameWidthAndWrapsInsideIt()
    {
        TextMeasurement.Current = new FixedMetrics(perChar: 1);

        // 30 characters at 1pt each in a 10pt frame: three lines.
        TextItem block = Block("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", frameWidth: 10, spacing: 1.0);
        Rect2D bounds = block.BoundingBox();

        Assert.Equal(10, bounds.Width, 3);
        Assert.Equal(30, bounds.Height, 3);
    }

    [Fact]
    public void ATrailingSpaceDoesNotWrapTheLineEarly()
    {
        TextMeasurement.Current = new FixedMetrics(perChar: 1);

        // "aaaa aaaa " is 10 characters; the space at the end must not push it to a
        // second line, or the block reports a line more height than it draws.
        TextItem block = Block("aaaa aaaa ", frameWidth: 9, spacing: 1.0);
        Assert.Equal(10, block.BoundingBox().Height, 3);
    }

    /// <summary>
    /// The rotation belongs to the bounds the page sees, not to the block's own rectangle.
    ///
    /// The two are not interchangeable. A caller working inside the block turns a point back into
    /// that block's space, where it is upright; measuring it against the rotated box compares two
    /// different frames, and on a turned label that made every click inside the text read as a
    /// miss and sent the caret to the end of the block.
    /// </summary>
    [Fact]
    public void RotationFoldsIntoTheBoundsButNotIntoTheBlocksOwnRectangle()
    {
        TextMeasurement.Current = new FixedMetrics(perChar: 2);

        TextItem block = Block("0123456789"); // 20pt wide, one 10pt line
        block.RotationRadians = Math.PI / 2;

        Rect2D upright = block.LocalBounds();
        Assert.Equal(20, upright.Width, 3);
        Assert.Equal(10, upright.Height, 3);

        // A quarter turn: the same rectangle the other way round.
        Rect2D turned = block.BoundingBox();
        Assert.Equal(10, turned.Width, 3);
        Assert.Equal(20, turned.Height, 3);
    }

    [Fact]
    public void ParagraphSpacingAddsLeadingBetweenParagraphsOnly()
    {
        TextMeasurement.Current = new FixedMetrics(perChar: 1);

        TextItem block = Block("aa\nbb", spacing: 1.0);
        block.ParagraphSpacing = 5;

        // Two lines of 10pt each, plus 5pt between them — not before the first.
        Assert.Equal(25, block.BoundingBox().Height, 3);
    }

    [Fact]
    public void AnEmptyBlockHasFiniteBounds()
    {
        // With no measurer and no characters, bounds must be finite rather than NaN —
        // bounds feed hit-testing and the Objects pane, and a NaN would poison both.
        TextMeasurement.Current = null;
        Assert.Equal(0, Block(string.Empty).BoundingBox().Width, 3);
        Assert.True(double.IsFinite(Block(string.Empty).BoundingBox().Height));
    }
}
