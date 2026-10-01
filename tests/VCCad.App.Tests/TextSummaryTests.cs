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
}
