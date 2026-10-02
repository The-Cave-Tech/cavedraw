using VCCad.App.Views.Panes;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// What a selection of text blocks agrees on, and what it does not.
///
/// The two judgements worth pinning rather than assuming are the ones `StrokeSummary` already makes and this
/// repeats for text: one disagreeing member does not hide the others, and a block with no run at the inspected
/// index is a **gap** rather than a disagreement. Getting the second one wrong makes every selection of unequal
/// blocks report every face member as mixed, which makes the report useless.
/// </summary>
public class TextSummaryTests
{
    private static TextItem Block(string words, string family = "Nimbus Sans", double size = 12,
        ColorRgb? colour = null, double lineSpacing = 1.2, double frameWidth = 0, double rotationDegrees = 0,
        params (string Words, string Family)[] extraRuns)
    {
        var item = new TextItem
        {
            Origin = new Point2D(0, 0),
            Color = colour ?? ColorRgb.Black,
            LineSpacing = lineSpacing,
            FrameWidth = frameWidth,
            RotationRadians = rotationDegrees * Math.PI / 180.0,
        };
        item.Runs.Add(new TextRun { Text = words, FontFamily = family, FontSize = size });
        foreach ((string words2, string family2) in extraRuns)
        {
            item.Runs.Add(new TextRun { Text = words2, FontFamily = family2, FontSize = size });
        }

        return item;
    }

    /// <summary>
    /// **A block with no run at the inspected index is a gap, not a disagreement.** One block carries two runs and
    /// the other one, and the run at index 1 is the first block's own - reported as its value, not as mixed.
    /// </summary>
    [Fact]
    public void ABlockWithNoRunAtTheIndexIsAGapRatherThanADisagreement()
    {
        TextItem longer = Block("heading", "Nimbus Sans", extraRuns: new[] { ("caption", "Nimbus Roman") });
        TextItem shorter = Block("alone", "Nimbus Sans");

        TextSummary summary = TextSummary.Of(new[] { longer, shorter }, 1);

        Assert.Equal(2, summary.Blocks);
        Assert.Equal(1, summary.Runs);
        Assert.False(summary.FamilyMixed);
        Assert.Equal("Nimbus Roman", summary.Family);
    }

    /// <summary>And with nothing at that index there is no face to describe at all.</summary>
    [Fact]
    public void NothingAtTheIndexLeavesTheFaceEmpty()
    {
        TextSummary summary = TextSummary.Of(new[] { Block("one"), Block("two") }, 3);

        Assert.Equal(0, summary.Runs);
        Assert.Null(summary.Family);
        Assert.False(summary.FamilyMixed);
        Assert.False(summary.FontSizeMixed);
    }

    /// <summary>
    /// **One disagreeing member does not hide the others.** The words and the colour differ and are mixed, while the
    /// face and the paragraph style the blocks agree on are still reported.
    /// </summary>
    [Fact]
    public void OneDisagreeingMemberDoesNotHideTheOthers()
    {
        TextItem first = Block("one", "Nimbus Sans", 12, ColorRgb.Red);
        TextItem second = Block("two", "Nimbus Sans", 12, ColorRgb.Blue);

        TextSummary summary = TextSummary.Of(new[] { first, second }, 0);

        Assert.True(summary.ContentMixed);
        Assert.True(summary.ColorMixed);
        Assert.Null(summary.Content);
        Assert.Null(summary.Color);

        Assert.False(summary.FamilyMixed);
        Assert.Equal("Nimbus Sans", summary.Family);
        Assert.False(summary.FontSizeMixed);
        Assert.Equal(12.0, summary.FontSize);
        Assert.False(summary.LineSpacingMixed);
    }

    /// <summary>Rotation is reported in the degrees every field and operation speaks, not in the model's radians.</summary>
    [Fact]
    public void RotationIsReportedInDegrees()
    {
        TextSummary agree = TextSummary.Of(
            new[] { Block("a", rotationDegrees: 45), Block("b", rotationDegrees: 45) }, 0);
        Assert.False(agree.RotationMixed);
        Assert.Equal(45.0, agree.RotationDegrees!.Value, 6);

        TextSummary differ = TextSummary.Of(
            new[] { Block("a", rotationDegrees: 0), Block("b", rotationDegrees: 90) }, 0);
        Assert.True(differ.RotationMixed);
        Assert.Null(differ.RotationDegrees);
    }

    /// <summary>Nothing selected is nothing to describe, and not a selection that disagrees.</summary>
    [Fact]
    public void AnEmptySelectionHasNothingToDescribe()
    {
        TextSummary summary = TextSummary.Of(Array.Empty<TextItem>(), 0);

        Assert.True(summary.IsEmpty);
        Assert.False(summary.IsMixed);
        Assert.Equal(0, summary.Runs);
    }

    /// <summary>
    /// **Tracking, word spacing, stretch and variant are per-run members read at the inspected run** - the four #147
    /// put on <see cref="TextRun"/>, which the panel has to describe like family and size. A block whose own runs
    /// disagree about them is a disagreement, and a block with no run at the index has nothing to report rather than a
    /// value that happens to be zero.
    /// </summary>
    [Fact]
    public void SpacingAndFaceRequestsAreReadAtTheInspectedRun()
    {
        TextItem longer = Block("heading", "Nimbus Sans", extraRuns: new[] { ("caption", "Nimbus Roman") });
        longer.Runs[0].LetterSpacing = 1.5;
        longer.Runs[0].WordSpacing = 2.0;
        longer.Runs[0].FontStretch = "condensed";
        longer.Runs[0].FontVariant = "small-caps";
        longer.Runs[1].LetterSpacing = 4.0;
        longer.Runs[1].WordSpacing = 0.5;
        longer.Runs[1].FontStretch = "expanded";

        TextItem shorter = Block("alone", "Nimbus Sans");
        shorter.Runs[0].LetterSpacing = 1.5;
        shorter.Runs[0].WordSpacing = 2.0;
        shorter.Runs[0].FontStretch = "condensed";
        shorter.Runs[0].FontVariant = "small-caps";

        // At index 0 the two blocks agree, so each is the common value and nothing is mixed.
        TextSummary agreed = TextSummary.Of(new[] { longer, shorter }, 0);
        Assert.False(agreed.LetterSpacingMixed);
        Assert.Equal(1.5, agreed.LetterSpacing!.Value, 9);
        Assert.False(agreed.WordSpacingMixed);
        Assert.Equal(2.0, agreed.WordSpacing!.Value, 9);
        Assert.False(agreed.FontStretchMixed);
        Assert.Equal("condensed", agreed.FontStretch);
        Assert.False(agreed.FontVariantMixed);
        Assert.Equal("small-caps", agreed.FontVariant);

        // Index 1 is the longer block's own run: the shorter block is a gap, not a disagreement, and the run the
        // longer block does have is reported as its own value.
        TextSummary second = TextSummary.Of(new[] { longer, shorter }, 1);
        Assert.False(second.LetterSpacingMixed);
        Assert.Equal(4.0, second.LetterSpacing!.Value, 9);
        Assert.Equal("expanded", second.FontStretch);

        // And nothing at all at an index neither block has.
        TextSummary none = TextSummary.Of(new[] { longer, shorter }, 9);
        Assert.Null(none.LetterSpacing);
        Assert.False(none.LetterSpacingMixed);
        Assert.Null(none.FontStretch);
    }

    /// <summary>
    /// **A disagreement about tracking or a face request is reported as mixed with no value**, never as one block's
    /// number offered to the whole selection - the defect the mixed readout exists to remove, one level down.
    /// </summary>
    [Fact]
    public void DisagreeingSpacingAndFaceRequestsAreMixed()
    {
        TextItem first = Block("one");
        first.Runs[0].LetterSpacing = 1.0;
        first.Runs[0].WordSpacing = 1.0;
        first.Runs[0].FontStretch = "condensed";
        first.Runs[0].FontVariant = "small-caps";

        TextItem second = Block("two");
        second.Runs[0].LetterSpacing = 5.0;
        second.Runs[0].WordSpacing = 3.0;
        second.Runs[0].FontStretch = "expanded";

        TextSummary summary = TextSummary.Of(new[] { first, second }, 0);

        Assert.True(summary.IsMixed);
        Assert.True(summary.LetterSpacingMixed);
        Assert.Null(summary.LetterSpacing);
        Assert.True(summary.WordSpacingMixed);
        Assert.Null(summary.WordSpacing);
        Assert.True(summary.FontStretchMixed);
        Assert.Null(summary.FontStretch);
        Assert.True(summary.FontVariantMixed);
        Assert.Null(summary.FontVariant);
    }
}
