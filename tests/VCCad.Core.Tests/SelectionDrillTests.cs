using VCCad.Core.Model;
using VCCad.Core.Selection;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// Drilling into nested artwork: a click takes the group, and each double-click goes one level down.
///
/// The chain is the list of objects under a point, outermost first. It is what makes "one level down" mean
/// something precise - the child of the currently selected object that is under the pointer - rather than
/// "some sibling that happens to be nearby".
/// </summary>
public class SelectionDrillTests
{
    private static CadDocument Page(out Layer layer, params LayerItem[] items)
    {
        var document = new CadDocument();
        Artboard board = document.AddArtboard(new Size2D(612, 792), "Page 1", new Point2D(0, 0));
        layer = board.AddLayer("Layer 1");
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

    /// <summary>The chain runs outermost first and ends at the object under the pointer.</summary>
    [Fact]
    public void TheChainRunsFromTheOutermostGroupToTheDeepestObject()
    {
        PathItem piece = Box("piece", 100, 100, 200);

        var middle = new ArtGroup { Name = "Layer 1" };
        middle.AddItem(piece);

        var outer = new ArtGroup { Name = "Group" };
        outer.AddItem(middle);

        CadDocument document = Page(out Layer layer, outer);
        IReadOnlyList<LayerItem> chain = SelectionEngine.Chain(document.Artboards[0], new Point2D(200, 200));

        Assert.Equal(new LayerItem[] { outer, middle, piece }, chain);
    }

    /// <summary>A top-level object is a chain of one.</summary>
    [Fact]
    public void ATopLevelObjectIsAChainOfOne()
    {
        PathItem piece = Box("piece", 100, 100, 200);
        CadDocument document = Page(out Layer layer, piece);

        Assert.Equal(new LayerItem[] { piece }, SelectionEngine.Chain(document.Artboards[0], new Point2D(200, 200)));
    }

    /// <summary>Nothing under the pointer is an empty chain.</summary>
    [Fact]
    public void NothingUnderThePointerIsAnEmptyChain()
    {
        CadDocument document = Page(out Layer layer, Box("piece", 100, 100, 50));

        Assert.Empty(SelectionEngine.Chain(document.Artboards[0], new Point2D(500, 600)));
    }

    /// <summary>
    /// A single click takes the outermost object: the group is selectable with the pointer, which is what
    /// makes a whole pattern piece movable.
    /// </summary>
    [Fact]
    public void ASingleClickSelectsTheOutermostObject()
    {
        CadDocument document = Page(out Layer layer, Nested(out PathItem piece));

        IReadOnlyList<LayerItem> chain = SelectionEngine.Chain(document.Artboards[0], new Point2D(200, 200));
        LayerItem? hit = SelectionEngine.Drill(chain, Array.Empty<LayerItem>(), clickCount: 1);

        Assert.Same(chain[0], hit);
    }

    /// <summary>Each double-click descends exactly one level, and stops at the object.</summary>
    [Fact]
    public void EachDoubleClickDescendsOneLevelAndThenStops()
    {
        CadDocument document = Page(out Layer layer, Nested(out PathItem piece));
        IReadOnlyList<LayerItem> chain = SelectionEngine.Chain(document.Artboards[0], new Point2D(200, 200));

        Assert.Equal(3, chain.Count);

        // First click: the outer group. Then one level down per double-click.
        Assert.Same(chain[0], SelectionEngine.Drill(chain, Array.Empty<LayerItem>(), clickCount: 1));
        Assert.Same(chain[1], SelectionEngine.Drill(chain, new[] { chain[0] }, clickCount: 2));
        Assert.Same(chain[2], SelectionEngine.Drill(chain, new[] { chain[1] }, clickCount: 2));

        // At the bottom it stays there rather than wrapping or clearing.
        Assert.Same(chain[2], SelectionEngine.Drill(chain, new[] { chain[2] }, clickCount: 2));
    }

    /// <summary>
    /// A double-click with nothing in the chain selected yet still starts at the top - which is what a
    /// double-click on a fresh object does.
    /// </summary>
    [Fact]
    public void ADoubleClickWithNothingSelectedStartsAtTheTop()
    {
        CadDocument document = Page(out Layer layer, Nested(out PathItem piece));
        IReadOnlyList<LayerItem> chain = SelectionEngine.Chain(document.Artboards[0], new Point2D(200, 200));

        Assert.Same(chain[0], SelectionEngine.Drill(chain, Array.Empty<LayerItem>(), clickCount: 2));
    }

    /// <summary>
    /// Dragging into a branch: what is selected somewhere else in the tree is not in this chain, so the
    /// drill starts from the top rather than descending from an unrelated object.
    /// </summary>
    [Fact]
    public void ASelectionOutsideTheChainDoesNotDrillIntoIt()
    {
        PathItem other = Box("other", 400, 400, 50);
        PathItem piece = Box("piece", 100, 100, 200);

        var outer = new ArtGroup { Name = "Group" };
        outer.AddItem(piece);

        CadDocument document = Page(out Layer layer, outer, other);
        IReadOnlyList<LayerItem> chain = SelectionEngine.Chain(document.Artboards[0], new Point2D(200, 200));

        Assert.Same(chain[0], SelectionEngine.Drill(chain, new[] { other }, clickCount: 2));
    }

    /// <summary>An empty chain drills to nothing, whichever click it was.</summary>
    [Fact]
    public void AnEmptyChainDrillsToNothing()
    {
        Assert.Null(SelectionEngine.Drill(Array.Empty<LayerItem>(), Array.Empty<LayerItem>(), clickCount: 1));
        Assert.Null(SelectionEngine.Drill(Array.Empty<LayerItem>(), Array.Empty<LayerItem>(), clickCount: 2));
    }

    private static ArtGroup Nested(out PathItem piece)
    {
        piece = Box("piece", 100, 100, 200);

        var middle = new ArtGroup { Name = "Layer 1" };
        middle.AddItem(piece);

        var outer = new ArtGroup { Name = "Group" };
        outer.AddItem(middle);
        return outer;
    }
}
