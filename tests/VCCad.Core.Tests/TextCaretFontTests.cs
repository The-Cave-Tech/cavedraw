using VCCad.Core.Model;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// Which font applies at a caret position.
///
/// A block can hold several runs at different faces and sizes - a heading and a caption in one frame - so "the
/// font of this block" is not a single answer, and taking the first run's is wrong for most carets. What a
/// person expects when they click between two words is the font of the words around the caret, because that is
/// what they are about to type between.
/// </summary>
public class TextCaretFontTests
{
    /// <summary>"Bold" in 36pt, then "plain" in 12pt.</summary>
    private static TextItem TwoRuns()
    {
        var text = new TextItem();
        text.Runs.Add(new TextRun { Text = "BOLD", FontFamily = "Face A", FontSize = 36 });
        text.Runs.Add(new TextRun { Text = "plain", FontFamily = "Face B", FontSize = 12 });
        return text;
    }

    /// <summary>Mid-block the run before the caret decides - that is the run the next character joins.</summary>
    [Fact]
    public void TheRunBeforeTheCaretDecides()
    {
        TextItem text = TwoRuns();

        // Positions 1..4 are inside the first run; 5 is its end, which is still the first run's business.
        Assert.Equal(("Face A", 36.0, false, false), TextEditing.FontAt(text, 3));
        Assert.Equal(("Face A", 36.0, false, false), TextEditing.FontAt(text, 5));

        // Position 6 is one character into the second run.
        Assert.Equal(("Face B", 12.0, false, false), TextEditing.FontAt(text, 6));
        Assert.Equal(("Face B", 12.0, false, false), TextEditing.FontAt(text, 9));
    }

    /// <summary>
    /// At position 0 there is no character before the caret, so the one after it decides: the caret is at the
    /// very start of the block and the next character typed will push the rest along.
    /// </summary>
    [Fact]
    public void AtTheStartTheFollowingCharacterDecides()
    {
        TextItem text = TwoRuns();
        Assert.Equal(("Face A", 36.0, false, false), TextEditing.FontAt(text, 0));
    }

    /// <summary>
    /// An empty block adopts the face it was created with. It has no characters to read, but its run carries
    /// the face and size the next character will be typed in - so adopting it is what makes the panel say what
    /// the block will do. Inventing a **default** here would be the thing that discards a person's choice.
    /// </summary>
    [Fact]
    public void AnEmptyBlockAdoptsItsOwnRun()
    {
        var empty = new TextItem();
        empty.Runs.Add(new TextRun { Text = string.Empty, FontFamily = "Face A", FontSize = 36 });

        Assert.Equal(("Face A", 36.0, false, false), TextEditing.FontAt(empty, 0));
    }

    /// <summary>A block with no runs at all has nothing to adopt, and says so rather than guessing.</summary>
    [Fact]
    public void ABlockWithNoRunsAdoptsNothing()
        => Assert.Null(TextEditing.FontAt(new TextItem(), 0));

    /// <summary>Past the end is clamped rather than throwing - a caret at the end is the last run's.</summary>
    [Fact]
    public void ACaretPastTheEndIsTheLastRun()
    {
        Assert.Equal(("Face B", 12.0, false, false), TextEditing.FontAt(TwoRuns(), 99));
    }

    /// <summary>And the run itself is what both the controls and the adoption read.</summary>
    [Fact]
    public void TheRunAtTheCaretIsTheOneThatDecides()
    {
        TextItem text = TwoRuns();

        Assert.Same(text.Runs[0], TextEditing.RunAt(text, 2));
        Assert.Same(text.Runs[1], TextEditing.RunAt(text, 7));
        Assert.Same(text.Runs[0], TextEditing.RunAt(text, 0));
    }
}
