using VCCad.App.ViewModels;
using VCCad.App.Views;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// What the tool toolbar contains.
///
/// The rule for this toolbar is that **every tool exists on it**, and that is exactly the kind of rule that
/// quietly stops being true: the corner and pencil tools were added to `EditorTool`, given glyphs, wired
/// into the press/move/release switches - and nobody noticed they needed buttons as well. These tests make
/// that failure impossible to miss, because the toolbar is now built from the table they check.
/// </summary>
public class ToolbarLayoutTests
{
    /// <summary>Every tool has a button, and no tool has two.</summary>
    [Fact]
    public void EveryToolIsOnTheToolbarExactlyOnce()
    {
        List<EditorTool> onBar = ToolbarLayout.Tools.ToList();

        foreach (EditorTool tool in Enum.GetValues<EditorTool>())
        {
            Assert.True(onBar.Contains(tool), $"{tool} is a tool with no button on the toolbar");
            Assert.Equal(1, onBar.Count(t => t == tool));
        }

        Assert.Equal(Enum.GetValues<EditorTool>().Length, onBar.Count);
    }

    /// <summary>Every entry that is a tool has a tip, because a tool with no tip is a guess.</summary>
    [Fact]
    public void EveryToolEntryHasATip()
    {
        foreach (ToolbarEntry entry in ToolbarLayout.All)
        {
            if (entry.Tool is null)
            {
                continue;
            }

            Assert.False(string.IsNullOrWhiteSpace(entry.Tip), $"{entry.Tool} has no tooltip");
        }
    }

    /// <summary>
    /// Every tool entry has an icon or a drawn glyph - and not both, so the toolbar never has to guess
    /// which to draw.
    /// </summary>
    [Fact]
    public void EveryToolEntryHasAFace()
    {
        foreach (ToolbarEntry entry in ToolbarLayout.All)
        {
            if (entry.Tool is null || entry.IsShapeFlyout)
            {
                // The compound button draws the armed shape, not a face of its own.
                continue;
            }

            bool hasIcon = !string.IsNullOrWhiteSpace(entry.Icon);
            bool hasGlyph = ToolMarks.Glyph(entry.Tool.Value) is not null;

            Assert.True(hasIcon || hasGlyph, $"{entry.Tool} would be a blank button");
            Assert.False(hasIcon && hasGlyph, $"{entry.Tool} has both an icon and a glyph");
        }
    }

    /// <summary>A separator carries no tool and no face; it is only a line.</summary>
    [Fact]
    public void SeparatorsCarryNothingButALine()
    {
        foreach (ToolbarEntry entry in ToolbarLayout.All.Where(e => e.IsSeparator))
        {
            Assert.Null(entry.Tool);
            Assert.Null(entry.Icon);
            Assert.Null(entry.Tip);
            Assert.False(entry.IsShapeFlyout);
        }
    }

    /// <summary>The shape tool is the compound button, and there is exactly one of it.</summary>
    [Fact]
    public void TheShapeToolIsTheCompoundButton()
    {
        List<ToolbarEntry> flyouts = ToolbarLayout.All.Where(e => e.IsShapeFlyout).ToList();

        Assert.Single(flyouts);
        Assert.Equal(EditorTool.Shape, flyouts[0].Tool);
    }

    /// <summary>
    /// The tips name the shortcut each tool answers to, and the lasso's does - the shortcut that was
    /// advertised for months and never bound until #64.
    /// </summary>
    [Theory]
    [InlineData(EditorTool.Select, "V")]
    [InlineData(EditorTool.Node, "A")]
    [InlineData(EditorTool.Pen, "P")]
    [InlineData(EditorTool.Rectangle, "M")]
    [InlineData(EditorTool.Ellipse, "L")]
    [InlineData(EditorTool.Artboard, "O")]
    [InlineData(EditorTool.Text, "T")]
    [InlineData(EditorTool.Lasso, "Q")]
    [InlineData(EditorTool.Corner, "C")]
    [InlineData(EditorTool.Pencil, "N")]
    [InlineData(EditorTool.Shape, "S")]
    public void TheTipNamesTheShortcut(EditorTool tool, string key)
    {
        ToolbarEntry entry = ToolbarLayout.All.First(e => e.Tool == tool);

        Assert.Contains($"({key})", entry.Tip);
    }
}
