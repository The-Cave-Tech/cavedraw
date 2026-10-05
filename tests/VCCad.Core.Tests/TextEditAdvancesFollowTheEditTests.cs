using VCCad.Core.Model;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// An edit adjusts the advance the file recorded; it does not throw it away (issue #246).
///
/// The importer records a `TJ` array as one run per piece, each carrying the advance to the next piece, and the
/// layout honours that number. Merge concatenates adjacent same-style runs - which is every run of an imported
/// header - and it kept the **first** piece's advance while the surviving run held all the text: on the Lillie page
/// header a 31-character block was laid out 124 units wide before the edit and 21 after it, with every glyph
/// compressed into that, which is what the person saw as the box collapsing.
///
/// **Both halves matter.** Dropping the advance clears the collapse but also discards the file's own spacing, so a
/// block loses 21 units when a character is added and the box does not grow at all. The advance therefore survives
/// the edit and moves by what the edit changed: plus the new character's width, minus the deleted one's, and the
/// sum of both when two pieces merge.
/// </summary>
public class TextEditAdvancesFollowTheEditTests
{
    /// <summary>The Lillie header as the importer builds it: seven pieces, each with its own advance.</summary>
    private static TextItem ImportedHeader()
    {
        var text = new TextItem { Name = "header" };
        Add(text, "Jalie ", 16.8574);
        Add(text, "3464 ", 19.406);
        Add(text, "- ", 3.599);
        Add(text, "LILLIE ", 22.052);
        Add(text, "- ", 3.599);
        Add(text, "Page ", 19.318);
        Add(text, "1/12", 18.061);
        return text;

        static void Add(TextItem text, string piece, double advance)
            => text.Runs.Add(new TextRun
            {
                Text = piece,
                FontFamily = "Nimbus Sans",
                FontSize = 9,
                AdvanceWidth = advance,
            });
    }

    /// <summary>What the file says the whole header spans: the sum of its seven pieces.</summary>
    private const double FileTotal = 102.8924;

    private static double Total(TextItem text) => text.Runs.Sum(run => run.AdvanceWidth ?? 0);

    /// <summary>The fixture is the file's shape, so a test that passes vacuously is visible.</summary>
    [Fact]
    public void AnUntouchedBlockKeepsWhatTheFileSaid()
    {
        TextItem text = ImportedHeader();

        Assert.Equal(7, text.Runs.Count);
        Assert.All(text.Runs, run => Assert.NotNull(run.AdvanceWidth));
        Assert.Equal(FileTotal, Total(text), 3);
    }

    /// <summary>
    /// Merging is where the collapse happened: the surviving run must span what the two pieces spanned, not what
    /// the first of them did.
    /// </summary>
    [Fact]
    public void MergingKeepsTheTotalTheFileStated()
    {
        TextItem text = ImportedHeader();

        TextEditing.Merge(text);

        Assert.Single(text.Runs);
        Assert.Equal(FileTotal, Total(text), 3);
        Assert.Equal("Jalie 3464 - LILLIE - Page 1/12", text.Runs[0].Text);
    }

    /// <summary>Typing: the advance grows, so the box extends to make room for the new character.</summary>
    [Fact]
    public void TypingGrowsTheAdvance()
    {
        TextItem text = ImportedHeader();

        TextEditing.Insert(text, 8, "5");

        Assert.Equal("Jalie 34564 - LILLIE - Page 1/12", TextEditing.GetText(text));

        double grew = Total(text) - FileTotal;
        Assert.True(grew > 0.5, $"the advance did not grow: +{grew:0.###}");
        Assert.True(grew < 9, $"the advance grew by more than a 9pt character could be: +{grew:0.###}");
    }

    /// <summary>Deleting: it shrinks by the character that went, and by no more.</summary>
    [Fact]
    public void DeletingShrinksTheAdvance()
    {
        TextItem text = ImportedHeader();

        // Index 8 is the '6' of "3464".
        TextEditing.DeleteRange(text, 8, 9);

        Assert.Equal("Jalie 344 - LILLIE - Page 1/12", TextEditing.GetText(text));

        double shrank = FileTotal - Total(text);
        Assert.True(shrank > 0.5, $"the advance did not shrink: -{shrank:0.###}");
        Assert.True(shrank < 9, $"the advance shrank by more than a 9pt character could be: -{shrank:0.###}");
    }

    /// <summary>
    /// And the per-character placements go: a list indexed by the characters it was written for cannot describe a
    /// run whose text has changed.
    /// </summary>
    [Fact]
    public void PerCharacterPlacementsAreForgotten()
    {
        TextItem text = ImportedHeader();
        text.Runs[1].PositionOffsets = new[] { 0.0, 1.0, 2.0, 3.0, 4.0 };
        text.Runs[1].InlineOffsets = new[] { 0.0, 1.0, 2.0, 3.0, 4.0 };

        TextEditing.Insert(text, 8, "5");

        Assert.All(text.Runs, run => Assert.Null(run.PositionOffsets));
        Assert.All(text.Runs, run => Assert.Null(run.InlineOffsets));
    }
}
