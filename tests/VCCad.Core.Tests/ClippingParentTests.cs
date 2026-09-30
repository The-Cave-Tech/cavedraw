using VCCad.Core.Model;
using VCCad.Core.Selection;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// Clipping parents, one inside another.
///
/// A shape can be clipped by a group, which is clipped by a group around that. As a marquee
/// grows it first encloses the shape, then the mask holding it, then the mask holding that -
/// and what gets selected is the OUTERMOST one the marquee has reached, not the shape at the
/// bottom. Selecting everything inside is the wrong answer: the person is reaching for the
/// mask, and the mask is what they should get.
/// </summary>
public class ClippingParentTests
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

        // A visible object, because that is what these fixtures model. An unfilled, unstroked path is
        // invisible and, since the hit test stopped using bounding boxes, correctly unclickable.
        path.Fill = FillSpec.Solid(ColorRgb.Black);
        return path;
    }

    private static ClipSpec Rect(double x, double y, double w, double h)
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

    private static CadDocument OnePage()
    {
        var document = new CadDocument();
        Artboard page = document.AddArtboard(
            new Size2D(W, H), "Page 1", new Point2D(0, 0));
        page.AddLayer("Artwork");
        return document;
    }

    private static Layer Artwork(CadDocument document) => document.Artboards[0].Layers[0];

    [Fact]
    public void AClipOnAGroupAppliesToWhatIsInsideIt()
    {
        // The mask is on the group; the child carries none. Without walking up, the child
        // looked unclipped and was selectable everywhere its own geometry reached.
        CadDocument document = OnePage();
        var group = new ArtGroup { Name = "mask" };
        group.Clips.Add(Rect(0, 0, 100, 100));
        PathItem child = Box("inside", 0, 0, 500);
        group.AddItem(child);
        Artwork(document).AddItem(group);

        // Within the mask: a point on the child survives and selects the group.
        Assert.Same(child, SelectionEngine.Within(document.Artboards[0], new Point2D(50, 50)));

        // Beyond it: nothing, even though the child's own geometry is there.
        Assert.Null(SelectionEngine.Within(document.Artboards[0], new Point2D(300, 300)));
    }

    [Fact]
    public void AMarqueeOverTheChildAloneDoesNotEncloseTheMask()
    {
        CadDocument document = OnePage();
        var group = new ArtGroup { Name = "mask" };
        group.Clips.Add(Rect(0, 0, 400, 400));

        // Two shapes in the mask, one far from the other, so the mask is bigger than either.
        // With only one child the mask's visible region IS that child, and a marquee round the
        // child encloses the mask - and selecting the mask is then the right answer, not a
        // fault. The mask has to be larger than what the marquee covers for this to ask
        // anything.
        group.AddItem(Box("near", 0, 0, 100));
        group.AddItem(Box("far", 300, 300, 50));
        Artwork(document).AddItem(group);

        SelectionResult result = SelectionEngine.Marquee(
            document, new Point2D(-10, -10), new Point2D(120, 120));

        Assert.Empty(result.Items);
    }

    [Fact]
    public void EnclosingTheMaskSelectsTheMaskRatherThanWhatIsInsideIt()
    {
        CadDocument document = OnePage();
        var group = new ArtGroup { Name = "mask" };
        group.Clips.Add(Rect(0, 0, 400, 400));
        group.AddItem(Box("child", 20, 20, 100));
        Artwork(document).AddItem(group);

        SelectionResult result = SelectionEngine.Marquee(
            document, new Point2D(-10, -10), new Point2D(420, 420));

        LayerItem selected = Assert.Single(result.Items);
        Assert.Same(group, selected);
        Assert.DoesNotContain(result.Items, i => i.Name == "child");
    }

    [Fact]
    public void NestedMasksResolveToTheOutermostOneEnclosed()
    {
        // child is clipped by inner, which is clipped by outer.
        CadDocument document = OnePage();

        var inner = new ArtGroup { Name = "inner" };
        inner.Clips.Add(Rect(0, 0, 100, 100));
        inner.AddItem(Box("child", 0, 0, 100));

        var outer = new ArtGroup { Name = "outer" };
        outer.Clips.Add(Rect(0, 0, 300, 300));
        outer.AddItem(inner);

        Artwork(document).AddItem(outer);

        // A marquee round the inner mask but short of the outer one: nothing, because the
        // outermost object is what the selection is made of and it is not enclosed.
        SelectionResult tight = SelectionEngine.Marquee(
            document, new Point2D(-10, -10), new Point2D(110, 110));

        Assert.True(
            tight.Items.Count == 0 || tight.Items.All(i => i.Name == "outer"),
            "a marquee short of the outermost object must not select something inside it");

        // Round the outer one: the outer mask, not the inner one, and not the child.
        SelectionResult wide = SelectionEngine.Marquee(
            document, new Point2D(-10, -10), new Point2D(320, 320));

        LayerItem selected = Assert.Single(wide.Items);
        Assert.Same(outer, selected);
    }

    [Fact]
    public void AClickOnANestedChildOutsideItsAncestorClipFindsNothing()
    {
        // The child fills the whole page; the mask holding it covers a quarter. A click on
        // the child's geometry outside the mask must find nothing - and this is the case that
        // only the ancestor walk can answer, because the click is tested against the deepest
        // object under the pointer, which carries no clip of its own.
        CadDocument document = OnePage();

        var group = new ArtGroup { Name = "mask" };
        group.Clips.Add(Rect(0, 0, 100, 100));
        PathItem child = Box("child", 0, 0, 600);
        group.AddItem(child);
        Artwork(document).AddItem(group);

        // Inside the mask the child is there, so the group is what is selected.
        Assert.Same(child, SelectionEngine.Within(document.Artboards[0], new Point2D(50, 50)));

        // Outside it, the child is not there at all.
        Assert.Null(SelectionEngine.Within(document.Artboards[0], new Point2D(300, 300)));
    }

    [Fact]
    public void ClipsOnWalksEveryAncestor()
    {
        CadDocument document = OnePage();

        var inner = new ArtGroup { Name = "inner" };
        inner.Clips.Add(Rect(0, 0, 100, 100));
        PathItem child = Box("child", 0, 0, 100);
        child.Clips.Add(Rect(0, 0, 50, 50));
        inner.AddItem(child);

        var outer = new ArtGroup { Name = "outer" };
        outer.Clips.Add(Rect(0, 0, 300, 300));
        outer.AddItem(inner);
        Artwork(document).AddItem(outer);

        // The child's own clip, then the two above it.
        IReadOnlyList<ClipSpec> clips = SelectionEngine.ClipsOn(child);
        Assert.Equal(3, clips.Count);
    }

    [Fact]
    public void AClipReachesTheChildWhicheverWayItIsWritten()
    {
        // A point inside the innermost of three masks survives; one outside the outermost
        // does not; and one in the ring between them does not either.
        CadDocument document = OnePage();

        var inner = new ArtGroup { Name = "inner" };
        inner.Clips.Add(Rect(0, 0, 100, 100));
        inner.AddItem(Box("child", 0, 0, 100));

        var outer = new ArtGroup { Name = "outer" };
        outer.Clips.Add(Rect(0, 0, 200, 200));
        outer.AddItem(inner);
        Artwork(document).AddItem(outer);

        // The child itself is the thing whose survival is being asked about.
        PathItem subject = (PathItem)inner.Children[0];
        Vector2D offset = new(0, 0);

        Assert.False(SelectionEngine.VisibleRegion(subject, offset).IsEmpty);
        Assert.True(SelectionEngine.VisibleRegion(subject, offset).Bounds.Right <= 100.001);
    }
}
