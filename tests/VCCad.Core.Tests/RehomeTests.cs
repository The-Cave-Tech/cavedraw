using VCCad.Core.Model;
using VCCad.Core.Selection;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// Where an object belongs after it has been moved.
///
/// Dragging something off the page it started on is how a person moves it between pages, so
/// where it lands decides which page owns it - and a point outside every page means the
/// pasteboard, which is somewhere objects live rather than somewhere they are lost.
///
/// The interesting case is the one with no pointer. A translation that arrives through the API
/// has nothing to hover with, so the transformation vector stands in for the pointer: its END,
/// measured from the CENTRE OF THE SELECTION. Otherwise a scripted move would leave an object
/// on a page it had visibly left, and the operation and the gesture would disagree.
/// </summary>
public class RehomeTests
{
    private const double W = 612;
    private const double H = 792;

    private static PathItem Box(string name, double x, double y, double size = 50)
    {
        var path = new PathItem { Name = name };
        SubPath sub = path.AddSubPath(closed: true);
        sub.Nodes.Add(new PathNode(new Point2D(x, y)));
        sub.Nodes.Add(new PathNode(new Point2D(x + size, y)));
        sub.Nodes.Add(new PathNode(new Point2D(x + size, y + size)));
        sub.Nodes.Add(new PathNode(new Point2D(x, y + size)));
        return path;
    }

    private static CadDocument TwoPages()
    {
        var document = new CadDocument();

        foreach ((string name, double x) in new[] { ("Left", 0.0), ("Right", 652.0) })
        {
            Artboard page = document.AddArtboard(new Size2D(W, H), name, new Point2D(x, 0));
            page.AddLayer("Artwork");
        }

        return document;
    }

    private static Layer LayerOf(CadDocument document, string page)
        => document.Artboards.First(a => a.Name == page).Layers[0];

    [Fact]
    public void APointInsideAPageBelongsToThatPage()
    {
        CadDocument document = TwoPages();

        Assert.Same(
            LayerOf(document, "Left"),
            SelectionEngine.ContainerAt(document, new Point2D(100, 100)));

        Assert.Same(
            LayerOf(document, "Right"),
            SelectionEngine.ContainerAt(document, new Point2D(700, 100)));
    }

    [Fact]
    public void APointOutsideEveryPageBelongsToThePasteboard()
    {
        CadDocument document = TwoPages();

        Assert.Same(
            document.Orphans,
            SelectionEngine.ContainerAt(document, new Point2D(W + 20, 100)));
    }

    [Fact]
    public void AnObjectDraggedOntoAnotherPageIsRehomedThere()
    {
        CadDocument document = TwoPages();
        PathItem box = Box("moving", 100, 100);
        LayerOf(document, "Left").AddItem(box);

        // The pointer has been dragged onto the right-hand page.
        IItemContainer? target = SelectionEngine.RehomeTarget(
            document, new[] { box }, pointer: new Point2D(700, 150));

        Assert.Same(LayerOf(document, "Right"), target);
    }

    [Fact]
    public void AnObjectDraggedOffEveryPageGoesToThePasteboard()
    {
        CadDocument document = TwoPages();
        PathItem box = Box("leaving", 100, 100);
        LayerOf(document, "Left").AddItem(box);

        IItemContainer? target = SelectionEngine.RehomeTarget(
            document, new[] { box }, pointer: new Point2D(W + 20, 100));

        Assert.Same(document.Orphans, target);
    }

    [Fact]
    public void AnObjectDraggedWithinItsOwnPageStaysPut()
    {
        CadDocument document = TwoPages();
        PathItem box = Box("settled", 100, 100);
        LayerOf(document, "Left").AddItem(box);

        // Null means no rehoming is needed, so nothing rebuilds the tree.
        Assert.Null(SelectionEngine.RehomeTarget(
            document, new[] { box }, pointer: new Point2D(400, 400)));
    }

    [Fact]
    public void WithNoPointerTheEndOfTheVectorDecides()
    {
        // The API case. The selection's centre starts at (125,125) - a 50-unit box at 100,100 -
        // and is moved by +700 in x, which lands the centre at 825: inside the right-hand page.
        CadDocument document = TwoPages();
        PathItem box = Box("scripted", 100, 100);
        LayerOf(document, "Left").AddItem(box);

        Point2D centre = SelectionEngine.CentreOf(new[] { box });
        Assert.Equal(125, centre.X, 6);
        Assert.Equal(125, centre.Y, 6);

        IItemContainer? target = SelectionEngine.RehomeTarget(
            document,
            new[] { box },
            pointer: null,
            centreBefore: centre,
            delta: new Vector2D(700, 0));

        Assert.Same(LayerOf(document, "Right"), target);
    }

    [Fact]
    public void WithNoPointerAVectorIntoThePasteboardSendsItThere()
    {
        CadDocument document = TwoPages();
        PathItem box = Box("scripted", 100, 100);
        LayerOf(document, "Left").AddItem(box);

        Point2D centre = SelectionEngine.CentreOf(new[] { box });

        // Far enough that the centre leaves every page.
        IItemContainer? target = SelectionEngine.RehomeTarget(
            document,
            new[] { box },
            pointer: null,
            centreBefore: centre,
            delta: new Vector2D(2000, 0));

        Assert.Same(document.Orphans, target);
    }

    [Fact]
    public void WithNoPointerASmallVectorLeavesItAlone()
    {
        CadDocument document = TwoPages();
        PathItem box = Box("scripted", 100, 100);
        LayerOf(document, "Left").AddItem(box);

        Point2D centre = SelectionEngine.CentreOf(new[] { box });

        Assert.Null(SelectionEngine.RehomeTarget(
            document,
            new[] { box },
            pointer: null,
            centreBefore: centre,
            delta: new Vector2D(20, 20)));
    }

    [Fact]
    public void TheCentreIsMeasuredInDocumentCoordinatesNotLocalOnes()
    {
        // An object on the right-hand page has local coordinates 100,100 but document
        // coordinates 752,100. Using the local ones would decide the wrong page - which is the
        // same class of mistake as the original report.
        CadDocument document = TwoPages();
        PathItem box = Box("on-right", 100, 100);
        LayerOf(document, "Right").AddItem(box);

        Point2D centre = SelectionEngine.CentreOf(new[] { box });

        Assert.Equal(777, centre.X, 6);
        Assert.Same(LayerOf(document, "Right"), SelectionEngine.ContainerAt(document, centre));
    }

    [Fact]
    public void TheCentreOfSeveralObjectsSpansAllOfThem()
    {
        CadDocument document = TwoPages();
        PathItem a = Box("a", 100, 100, 50);
        PathItem b = Box("b", 300, 300, 50);
        LayerOf(document, "Left").AddItem(a);
        LayerOf(document, "Left").AddItem(b);

        Point2D centre = SelectionEngine.CentreOf(new[] { a, b });

        // Bounds run 100..350, so the centre is 225.
        Assert.Equal(225, centre.X, 6);
        Assert.Equal(225, centre.Y, 6);
    }

    [Fact]
    public void AnEmptySelectionIsRehomedNowhere()
    {
        CadDocument document = TwoPages();

        Assert.Null(SelectionEngine.RehomeTarget(
            document, Array.Empty<LayerItem>(), pointer: new Point2D(700, 100)));
    }
}
