using VCCad.Core.Commands;
using VCCad.Core.Model;
using VCCad.Core.Selection;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// Commands that move geometry between frames convert it (issue #173).
///
/// `DeleteArtboardCommand` and `ReparentItemsCommand` take art out of an artboard and put it on the
/// pasteboard (or the other way round), and compensate the frame change by translating the stored
/// geometry. The translation is a **world** displacement and the geometry is stored in the frame its
/// groups establish, so it has to be carried into that frame - the same `SelectionEngine.DeltaInItem`
/// composition #165 and #172 stated once. Added raw, it moved a grouped object by the group's transform
/// applied to the artboard origin, so deleting a page teleported its grouped art.
///
/// Measured on **model geometry**: where the document says the art is, before and after.
/// </summary>
public class FrameConversionCommandTests
{
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

    /// <summary>A document with one artboard at <paramref name="origin"/> holding a transformed group.
    /// </summary>
    private static (CadDocument Document, Artboard Artboard, ArtGroup Group, PathItem Path) Page(Point2D origin)
    {
        var document = new CadDocument();
        var artboard = new Artboard(new Size2D(400, 400), origin) { Name = "Page 1" };
        artboard.AddLayer("Layer 1");
        document.AddArtboard(artboard);

        var group = new ArtGroup
        {
            Name = "panel",
            Transform = AffineTransform.CreateTranslation(30, 20).Compose(AffineTransform.CreateScale(2, 2)),
        };

        PathItem path = Box("piece", 5, 5, 10);
        group.AddItem(path);
        artboard.Layers[0].AddItem(group);

        return (document, artboard, group, path);
    }

    /// <summary>
    /// Where the document actually draws the path, the test's own composition of every enclosing group
    /// plus the artboard origin - not a call to the code under test.
    /// </summary>
    private static Rect2D InWorld(LayerItem item)
    {
        AffineTransform transform = AffineTransform.Identity;
        for (IItemContainer? container = item.Container;
             container is not null;
             container = (container as LayerItem)?.Container)
        {
            if (container is ArtGroup group)
            {
                transform = group.Transform.Compose(transform);
            }
        }

        Vector2D origin = item.ArtboardOffset();
        Rect2D box = ((PathItem)item).BoundingBox();
        return AffineTransform.CreateTranslation(origin.X, origin.Y).Compose(transform).Transform(box);
    }

    private static void AssertWorld(Rect2D expected, Rect2D actual, string what)
    {
        Assert.True(
            Math.Abs(actual.Left - expected.Left) <= 1e-6 &&
            Math.Abs(actual.Top - expected.Top) <= 1e-6 &&
            Math.Abs(actual.Right - expected.Right) <= 1e-6 &&
            Math.Abs(actual.Bottom - expected.Bottom) <= 1e-6,
            $"{what} should be at {expected.Left},{expected.Top} to {expected.Right},{expected.Bottom} " +
            $"but is at {actual.Left},{actual.Top} to {actual.Right},{actual.Bottom}");
    }

    /// <summary>
    /// Deleting an artboard and keeping its contents leaves the art where it was drawn.
    ///
    /// The page origin is (120,50) and the group is `translate(30,20) scale(2)`, so the 5..15 unit square
    /// is at 160..180 by 80..100 document units. Moving the page's objects to the pasteboard changes the
    /// frame they are stored in, and the compensation is a world displacement of (120,50): carried into the
    /// path's own frame it is (60,25), because the group doubles everything. Added raw from #159 onwards it
    /// was (120,50), so the square landed at 280..300 by 130..150 - 120 by 50 units away, exactly the group
    /// transform applied to the origin.
    /// </summary>
    [Fact]
    public void DeletingAnArtboardLeavesGroupedArtWhereItWasDrawn()
    {
        (CadDocument document, Artboard artboard, ArtGroup group, PathItem path) = Page(new Point2D(120, 50));
        AssertWorld(new Rect2D(160, 80, 20, 20), InWorld(path), "the square on the page");

        var stack = new CommandStack();
        stack.Execute(new DeleteArtboardCommand(document, artboard, keepChildren: true));

        Assert.Same(document.Orphans, group.Container);
        AssertWorld(new Rect2D(160, 80, 20, 20), InWorld(path), "the square on the pasteboard");

        stack.Undo();
        AssertWorld(new Rect2D(160, 80, 20, 20), InWorld(path), "the square after the undo");
    }

    /// <summary>
    /// Reparenting an orphan into an artboard keeps it where it was drawn, for the same reason in reverse.
    ///
    /// The group sits on the pasteboard, so the 5..15 unit square is at 40..60 by 30..50 document units.
    /// Moving it onto the page at (120,50) means subtracting that origin in the group's frame - (60,25) -
    /// not in world coordinates.
    /// </summary>
    [Fact]
    public void ReparentingGroupedArtIntoAnArtboardLeavesItWhereItWasDrawn()
    {
        (CadDocument document, Artboard artboard, ArtGroup group, PathItem path) = Page(new Point2D(120, 50));
        document.Orphans.AddItem(group);
        AssertWorld(new Rect2D(40, 30, 20, 20), InWorld(path), "the square on the pasteboard");

        var stack = new CommandStack();
        stack.Execute(new ReparentItemsCommand(document, artboard, new[] { (LayerItem)group }, document.Orphans));

        AssertWorld(new Rect2D(40, 30, 20, 20), InWorld(path), "the square on the page");

        stack.Undo();
        AssertWorld(new Rect2D(40, 30, 20, 20), InWorld(path), "the square after the undo");
    }

    /// <summary>
    /// The conversion is the one <see cref="SelectionEngine.DeltaInItem"/> states, so the frame arithmetic
    /// is the same composition the canvas and the operations use rather than a second rule.
    /// </summary>
    [Fact]
    public void TheCommandCarriesTheOffsetByTheSameCompositionTheEditorUses()
    {
        (_, _, _, PathItem path) = Page(new Point2D(120, 50));

        Vector2D carried = SelectionEngine.DeltaInItem(path, new Vector2D(120, 50));

        Assert.Equal(60.0, carried.X, 6);
        Assert.Equal(25.0, carried.Y, 6);
    }
}
