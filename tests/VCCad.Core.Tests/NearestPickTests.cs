using VCCad.Core.Model;
using VCCad.Core.Selection;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// Picking the object the pointer is nearest, not merely one it is near.
///
/// Testing whether a click lands in a bounding box picks things the pointer is nowhere near:
/// a diagonal line has a page-sized box, so a click in the far corner of that box picks the
/// line however far away it is. Two objects can both be within the pick tolerance of a click -
/// a line passing close to a shape, a label beside a border - and the one the person meant is
/// the one they are closest to.
/// </summary>
public class NearestPickTests
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

    /// <summary>A diagonal line across a page, whose bounding box is the whole page.</summary>
    private static PathItem Diagonal(string name)
    {
        var path = new PathItem { Name = name };
        SubPath sub = path.AddSubPath(closed: false);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(600, 780)));
        return path;
    }

    private static CadDocument Page(params LayerItem[] items)
    {
        var document = new CadDocument();
        Artboard page = document.AddArtboard(new Size2D(W, H), "Page 1", new Point2D(0, 0));
        Layer layer = page.AddLayer("Artwork");
        foreach (LayerItem item in items)
        {
            layer.AddItem(item);
        }

        return document;
    }

    [Fact]
    public void TheNearerOfTwoObjectsIsPicked()
    {
        CadDocument document = Page(Box("left", 100, 100), Box("right", 400, 100));

        // Just off each one's edge, inside the pick tolerance and nearer to that one.
        SelectionResult result = SelectionEngine.Click(document, new Point2D(398, 125));

        Assert.Equal("right", Assert.Single(result.Items).Name);

        SelectionResult other = SelectionEngine.Click(document, new Point2D(152, 125));
        Assert.Equal("left", Assert.Single(other.Items).Name);
    }

    [Fact]
    public void ADiagonalLineIsNotPickedFromTheFarCornerOfItsBox()
    {
        // The whole point. This line's bounding box is the page, so a box test says a click at
        // the bottom-left corner hits it - and the line is 780 units away from there.
        CadDocument document = Page(Diagonal("slash"));

        SelectionResult far = SelectionEngine.Click(document, new Point2D(20, 780));
        Assert.Empty(far.Items);

        SelectionResult on = SelectionEngine.Click(document, new Point2D(300, 390));
        Assert.Equal("slash", Assert.Single(on.Items).Name);
    }

    [Fact]
    public void AClickWithinToleranceOfTheOutlinePicksIt()
    {
        CadDocument document = Page(Box("box", 100, 100, 50));

        // Just outside the right edge, within the pick tolerance.
        Assert.Single(SelectionEngine.Click(document, new Point2D(152, 125)).Items);

        // Well outside it.
        Assert.Empty(SelectionEngine.Click(document, new Point2D(160, 125)).Items);
    }

    [Fact]
    public void AClickInsideAShapesAreaDoesNotPickItButItsOutlineDoes()
    {
        CadDocument document = Page(Box("box", 100, 100, 200));

        // The middle of a big filled shape: the fill covers the point, the path does not.
        Assert.Empty(SelectionEngine.Click(document, new Point2D(200, 200)).Items);

        // Its outline is the geometry a click aims at.
        Assert.Equal("box", Assert.Single(
            SelectionEngine.Click(document, new Point2D(100, 200)).Items).Name);
    }

    [Fact]
    public void ATieGoesToWhicheverIsOnTop()
    {
        // Two identical shapes exactly on top of each other: the same distance, so the one
        // painted last is the one the person can see and the one they mean.
        CadDocument document = Page(Box("under", 100, 100), Box("over", 100, 100));

        Assert.Equal("over", Assert.Single(
            SelectionEngine.Click(document, new Point2D(100, 125)).Items).Name);
    }

    [Fact]
    public void AnObjectCutAwayByAClipIsNotPickedWhereItUsedToBe()
    {
        // A wide rectangle cut down to its left fifth. A click out at x=400 was inside the
        // rectangle's own geometry, but nothing is there any more.
        CadDocument document = Page();
        Artboard page = document.Artboards[0];
        Layer layer = page.Layers[0];

        PathItem wide = Box("clipped", 0, 0, 500);
        wide.Clips.Add(RectClip(0, 0, 100, 500));
        layer.AddItem(wide);

        // On its left edge, which the clip keeps.
        Assert.Equal("clipped", Assert.Single(
            SelectionEngine.Click(document, new Point2D(0, 250)).Items).Name);

        Assert.Empty(SelectionEngine.Click(document, new Point2D(400, 250)).Items);
    }

    [Fact]
    public void ProximityIsMeasuredInDocumentCoordinatesNotLocalOnes()
    {
        // An object on the right-hand page has local coordinates 100,100 and document
        // coordinates 752,100. Measuring in local ones would put the pick in the wrong place -
        // the same class of mistake as the original report.
        var document = new CadDocument();
        Artboard left = document.AddArtboard(new Size2D(W, H), "Left", new Point2D(0, 0));
        Artboard right = document.AddArtboard(new Size2D(W, H), "Right", new Point2D(652, 0));
        left.AddLayer("Artwork");
        right.AddLayer("Artwork").AddItem(Box("on-right", 100, 100, 50));

        // In document coordinates the box spans 752..802.
        Assert.Equal("on-right", Assert.Single(
            SelectionEngine.Click(document, new Point2D(754, 125)).Items).Name);

        // The same local point on the left page holds nothing.
        Assert.Empty(SelectionEngine.Click(document, new Point2D(125, 125)).Items);
    }

    [Fact]
    public void TheDistanceInsideAShapeIsTheDistanceToItsOutline()
    {
        CadDocument document = Page(Box("box", 100, 100, 100));

        // The middle of the box is 50 from every edge, and the fill's area is not its geometry.
        Assert.Equal(50, SelectionEngine.DistanceTo(
            document.Artboards[0].Layers[0].Children[0], new Point2D(150, 150)), 6);
    }

    [Fact]
    public void TheDistanceToAPointOutsideIsTheDistanceToTheEdge()
    {
        CadDocument document = Page(Box("box", 100, 100, 100));

        // 30 units to the right of the right edge.
        Assert.Equal(30, SelectionEngine.DistanceTo(
            document.Artboards[0].Layers[0].Children[0], new Point2D(230, 150)), 6);
    }

    /// <summary>A rectangular clip, in the object's own coordinates.</summary>
    private static ClipSpec RectClip(double x, double y, double w, double h)
    {
        var clip = new ClipSpec { Rule = FillRule.NonZero };
        var sub = new SubPath { IsClosed = true };
        sub.Nodes.Add(new PathNode(new Point2D(x, y)));
        sub.Nodes.Add(new PathNode(new Point2D(x + w, y)));
        sub.Nodes.Add(new PathNode(new Point2D(x + w, y + h)));
        sub.Nodes.Add(new PathNode(new Point2D(x, y + h)));
        clip.SubPaths.Add(sub);
        return clip;
    }
}
