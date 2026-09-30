using VCCad.Core.Model;
using VCCad.Core.Selection;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// What a click selects when the artwork is nested.
///
/// **The deepest object wins, not the outermost.** A group is a container, not a thing you click: a person
/// clicking a pattern piece means the piece, and answering with the group that holds two hundred of them
/// makes every piece unselectable.
///
/// This is not a hypothetical. It is what happened the moment the importer started reproducing the file's
/// grouping: page 1 of the LILLIE sample put the pattern inside `Group` inside `Group` inside the file's own
/// `Layer 1` group, and a real pointer click on a piece selected the outer group - 544 x 1716 points of it.
/// The person's report was "I can't select anything on the page except the red text", and the text was
/// selectable only because it happened to sit outside the group.
/// </summary>
public class PickDepthTests
{
    private static CadDocument Page(params LayerItem[] items)
    {
        var document = new CadDocument();
        Artboard board = document.AddArtboard(new Size2D(612, 792), "Page 1", new Point2D(0, 0));
        Layer layer = board.AddLayer("Layer 1");
        foreach (LayerItem item in items)
        {
            layer.AddItem(item);
        }

        return document;
    }

    private static PathItem Box(string name, double x, double y, double size)
    {
        var path = new PathItem { Name = name };
        SubPath sub = path.AddSubPath(closed: true);
        sub.Nodes.Add(new PathNode(new Point2D(x, y)));
        sub.Nodes.Add(new PathNode(new Point2D(x + size, y)));
        sub.Nodes.Add(new PathNode(new Point2D(x + size, y + size)));
        sub.Nodes.Add(new PathNode(new Point2D(x, y + size)));
        path.Fill = FillSpec.Solid(ColorRgb.Black);
        return path;
    }

    /// <summary>A click on a child selects the child, not the group holding it.</summary>
    [Fact]
    public void AClickSelectsTheChildNotItsGroup()
    {
        PathItem piece = Box("piece", 100, 100, 200);
        var group = new ArtGroup { Name = "Group" };
        group.AddItem(piece);
        CadDocument document = Page(group);

        Assert.Same(piece, SelectionEngine.Within(document.Artboards[0], new Point2D(200, 200)));
    }

    /// <summary>
    /// And through any depth of nesting - which is the shape the importer now produces: a form group, an
    /// optional-content group inside it, and the artwork inside that.
    /// </summary>
    [Fact]
    public void AClickSelectsTheChildThroughNestedGroups()
    {
        PathItem piece = Box("piece", 100, 100, 200);

        var inner = new ArtGroup { Name = "Layer 1" };
        inner.AddItem(piece);

        var outer = new ArtGroup { Name = "Group" };
        outer.AddItem(inner);

        CadDocument document = Page(outer);

        Assert.Same(piece, SelectionEngine.Within(document.Artboards[0], new Point2D(200, 200)));
    }

    /// <summary>The frontmost child wins when two overlap - a click picks what is visible.</summary>
    [Fact]
    public void TheFrontmostChildWins()
    {
        PathItem under = Box("under", 100, 100, 200);
        PathItem over = Box("over", 150, 150, 100);

        var group = new ArtGroup { Name = "Group" };
        group.AddItem(under);
        group.AddItem(over);

        CadDocument document = Page(group);

        Assert.Same(over, SelectionEngine.Within(document.Artboards[0], new Point2D(200, 200)));
        Assert.Same(under, SelectionEngine.Within(document.Artboards[0], new Point2D(120, 120)));
    }

    /// <summary>Nothing under the pointer is still nothing, however deep the tree goes.</summary>
    [Fact]
    public void AClickOnEmptySpaceFindsNothing()
    {
        var group = new ArtGroup { Name = "Group" };
        group.AddItem(Box("piece", 100, 100, 50));
        CadDocument document = Page(group);

        Assert.Null(SelectionEngine.Within(document.Artboards[0], new Point2D(500, 600)));
    }
}
