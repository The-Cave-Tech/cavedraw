using VCCad.App.ViewModels;
using VCCad.App.Views;
using Avalonia.Headless.XUnit;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// The drawn glyphs for tools that have no icon asset.
///
/// The defect this guards against is a tool borrowing a neighbour's icon - which is what the corner and
/// pencil tools did when they were added, leaving two pairs of identical buttons told apart only by their
/// tooltips. A tool that looks exactly like another is a tool nobody finds, because a person scans icons
/// and reads a tooltip only once they are already looking at the right one.
/// </summary>
public class ToolMarksTests
{
    /// <summary>
    /// The tools that were given their own glyphs have one, and **no two share it**. This is the assertion
    /// that would have failed when the corner tool was added wearing the node tool's icon.
    /// </summary>
    [Fact]
    public void ToolsWithGlyphsHaveTheirOwn()
    {
        string? lasso = ToolMarks.Glyph(EditorTool.Lasso);
        string? corner = ToolMarks.Glyph(EditorTool.Corner);
        string? pencil = ToolMarks.Glyph(EditorTool.Pencil);

        Assert.False(string.IsNullOrWhiteSpace(lasso));
        Assert.False(string.IsNullOrWhiteSpace(corner));
        Assert.False(string.IsNullOrWhiteSpace(pencil));

        Assert.Equal(3, new[] { lasso, corner, pencil }.Distinct().Count());
    }

    /// <summary>A glyph is a drawable path, not a stub: it must parse as geometry.</summary>
    [AvaloniaTheory]
    [InlineData(EditorTool.Lasso)]
    [InlineData(EditorTool.Corner)]
    [InlineData(EditorTool.Pencil)]
    public void AGlyphParses(EditorTool tool)
    {
        string glyph = ToolMarks.Glyph(tool)!;
        Assert.NotNull(Avalonia.Media.Geometry.Parse(glyph));
    }

    /// <summary>
    /// A tool with an icon asset reports no glyph, which is how the toolbar knows to use the asset. Null is
    /// a real answer here - inventing a glyph for a tool that has a good icon would replace it with a worse
    /// one.
    /// </summary>
    [Theory]
    [InlineData(EditorTool.Select)]
    [InlineData(EditorTool.Node)]
    [InlineData(EditorTool.Pen)]
    [InlineData(EditorTool.Rectangle)]
    [InlineData(EditorTool.Shape)]
    public void ToolsWithAssetsReportNoGlyph(EditorTool tool)
    {
        Assert.Null(ToolMarks.Glyph(tool));
    }

    /// <summary>
    /// Every tool either has a glyph or has an icon asset - none is left with neither, which would be a
    /// blank button. The list of tools that use assets is the toolbar's own, so this fails if a new tool is
    /// added and nobody gives it a face.
    /// </summary>
    [Fact]
    public void EveryToolHasAFace()
    {
        EditorTool[] withAssets =
        {
            EditorTool.Select, EditorTool.Node, EditorTool.Pen, EditorTool.Rectangle,
            EditorTool.Shape, EditorTool.Ellipse, EditorTool.Artboard, EditorTool.Text,
        };

        foreach (EditorTool tool in Enum.GetValues<EditorTool>())
        {
            bool hasGlyph = !string.IsNullOrWhiteSpace(ToolMarks.Glyph(tool));
            bool hasAsset = withAssets.Contains(tool);

            Assert.True(hasGlyph || hasAsset,
                $"{tool} has neither a glyph nor an icon asset, so its toolbar button would be blank");
            Assert.False(hasGlyph && hasAsset,
                $"{tool} has both a glyph and an asset; the toolbar would have to guess which to draw");
        }
    }

    /// <summary>The stroke colour is a colour a path can parse, since the toolbar builds one from it.</summary>
    [Fact]
    public void TheStrokeIsAColour()
    {
        Assert.NotNull(Avalonia.Media.Color.Parse(ToolMarks.Stroke));
    }
}
