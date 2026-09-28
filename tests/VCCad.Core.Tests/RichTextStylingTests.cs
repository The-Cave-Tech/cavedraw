using VCCad.Core.Model;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// Styling part of a selection, which is the whole point of a rich-text block.
///
/// The demand is specific: changing the font on part of a selection must not reflow the
/// whole block. Reflow is the failure that matters, because it is silent — the characters
/// survive and the picture moves. So every test here checks the text as well as the style.
///
/// These use the same code path the editor does. DocumentSession.UpdateSelectedText calls
/// ApplyStyle over TextSelectionStart..TextSelectionEnd, which is what a person gets after
/// dragging across some words.
/// </summary>
public class RichTextStylingTests
{
    private static TextItem Block(string text, string family = "Nimbus Sans", double size = 24)
    {
        var item = new TextItem { Name = "Block", Color = new ColorRgb(0, 0, 0) };
        item.Runs.Add(new TextRun { Text = text, FontFamily = family, FontSize = size });
        return item;
    }

    private static string Dump(TextItem item)
        => string.Join("|", item.Runs.Select(r => $"{r.Text}[{r.FontFamily}]"));

    [Fact]
    public void StylingARangeLeavesTheRestOfTheBlockAlone()
    {
        TextItem item = Block("Hello wide world");

        // Characters 6..10 are "wide".
        TextEditing.ApplyStyle(item, 6, 10, r => r.FontFamily = "Nimbus Roman");

        Assert.Equal("Hello [Nimbus Sans]|wide[Nimbus Roman]| world[Nimbus Sans]", Dump(item));

        // Nothing was inserted, dropped or reordered: the block is the same text.
        Assert.Equal("Hello wide world", TextEditing.GetText(item));
    }

    [Fact]
    public void TheRangeStyledIsExactlyTheRangeAsked()
    {
        TextItem item = Block("Hello wide world");

        TextEditing.ApplyStyle(item, 6, 10, r => r.FontFamily = "Nimbus Roman");

        TextRun changed = item.Runs.Single(r => r.FontFamily == "Nimbus Roman");
        Assert.Equal("wide", changed.Text);
    }

    [Fact]
    public void StylingTheWholeRangeMergesBackToOneRun()
    {
        TextItem item = Block("Hello wide world");
        TextEditing.ApplyStyle(item, 6, 10, r => r.FontFamily = "Nimbus Roman");

        // Put it back, and the split should heal.
        TextEditing.ApplyStyle(item, 0, TextEditing.Length(item), r => r.FontFamily = "Nimbus Sans");

        TextRun only = Assert.Single(item.Runs);
        Assert.Equal("Hello wide world", only.Text);
    }

    [Fact]
    public void StylingDoesNotChangeTheTextEvenRepeatedly()
    {
        TextItem item = Block("Hello wide world");
        for (int i = 0; i < 5; i++)
        {
            TextEditing.ApplyStyle(item, 6, 10, r => r.FontFamily = i % 2 == 0 ? "Nimbus Roman" : "Nimbus Mono");
        }

        TextEditing.ApplyStyle(item, 6, 10, r => r.FontFamily = "Nimbus Roman");
        Assert.Equal("Hello wide world", TextEditing.GetText(item));
    }

    [Fact]
    public void AnEmptyRangeStylesTheRunUnderTheCaret()
    {
        TextItem item = Block("Hello world");

        // A caret with nothing selected styles the whole run it sits in, not the block.
        TextEditing.ApplyStyle(item, 3, 3, r => r.FontSize = 40);

        Assert.Equal(40.0, Assert.Single(item.Runs).FontSize);
        Assert.Equal("Hello world", TextEditing.GetText(item));
    }

    [Fact]
    public void StylingBeyondTheEndIsClampedRatherThanDropped()
    {
        TextItem item = Block("abc");

        TextEditing.ApplyStyle(item, 1, 9999, r => r.FontFamily = "Nimbus Roman");

        Assert.Equal("a[Nimbus Sans]|bc[Nimbus Roman]", Dump(item));
        Assert.Equal("abc", TextEditing.GetText(item));
    }

    [Fact]
    public void AStyledRangeKeepsItsOwnFaceAndSize()
    {
        TextItem item = Block("Hello wide world", size: 24);

        TextEditing.ApplyStyle(item, 6, 10, r =>
        {
            r.FontFamily = "Nimbus Roman";
            r.FontSize = 12;
        });

        TextRun changed = item.Runs.Single(r => r.FontFamily == "Nimbus Roman");
        Assert.Equal(12.0, changed.FontSize);

        // The neighbours kept both their face and their size.
        foreach (TextRun other in item.Runs.Where(r => r.FontFamily != "Nimbus Roman"))
        {
            Assert.Equal(24.0, other.FontSize);
        }
    }
}
