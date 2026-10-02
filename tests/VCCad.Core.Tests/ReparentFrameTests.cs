using VCCad.Core.Commands;
using VCCad.Core.Model;
using VCCad.Core.Selection;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// Reparenting converts geometry into the destination container's frame (issue #174).
///
/// `MoveItemsCommand` - what a drag between rows in the Layers panel, an `object.moveToLayer` call and
/// `object.arrange` all execute - used to take an item out of one container and put it in another
/// **without touching its geometry**. The numbers are then stored in a frame the geometry was never
/// expressed in, so artwork moved into or out of a transformed group changes where it appears and every
/// later operation inherits the error.
///
/// The conversion is the composition the repository states once, `Document.ToWorld` /
/// `SelectionEngine.FromWorld`: an item's world placement is what must not change, so the geometry is
/// mapped through `ToWorld(destination) ∘ FromWorld(source)`. Text and placed images cannot express a
/// general affine in their own members - an origin and an angle, or a placement rectangle - so a move
/// into a frame that turns or scales them is **refused** rather than stored in the wrong frame.
///
/// Everything is measured on **model geometry**, by the test's own arithmetic over the document's own
/// transforms.
/// </summary>
public class ReparentFrameTests
{
    private static PathItem Box(string name, Rect2D box)
    {
        PathItem path = PathFactory.CreateRectangle(name, box);
        path.Fill = FillSpec.Solid(ColorRgb.Black);
        return path;
    }

    /// <summary>
    /// An item's box in document coordinates, composed from the document's own transforms.
    ///
    /// A path is measured from its **nodes**, each carried through the frame: a path's local bounding box is
    /// not its geometry once a frame turns or shears it, and an affine image of a box is not the box of the
    /// image. A group is measured from its own local box, which its own transform carries into its placement
    /// frame - a group's transform reaches its *children* through
    /// <see cref="SelectionEngine.ToWorld"/>, not itself.
    /// </summary>
    private static Rect2D InWorld(LayerItem item)
    {
        AffineTransform world = SelectionEngine.ToWorld(item);

        if (item is PathItem path)
        {
            Rect2D nodes = Rect2D.Empty;
            foreach (SubPath sub in path.SubPaths)
            {
                foreach (PathNode node in sub.Nodes)
                {
                    Point2D point = world.Transform(node.Anchor);
                    nodes = nodes.Union(Rect2D.FromPoints(point, point));
                }
            }

            return nodes;
        }

        Rect2D box = item switch
        {
            TextItem text => text.BoundingBox(),
            ImageItem image => image.Placement,
            ArtGroup group => group.Transform.Transform(group.BoundingBox()),
            _ => Rect2D.Empty,
        };

        return box.IsEmpty ? box : world.Transform(box);
    }

    private static void AssertWorld(Rect2D expected, Rect2D actual, string what)
        => Assert.True(
            Math.Abs(actual.Left - expected.Left) <= 1e-6 &&
            Math.Abs(actual.Top - expected.Top) <= 1e-6 &&
            Math.Abs(actual.Right - expected.Right) <= 1e-6 &&
            Math.Abs(actual.Bottom - expected.Bottom) <= 1e-6,
            $"{what} should be {expected.Left},{expected.Top} to {expected.Right},{expected.Bottom} " +
            $"but is {actual.Left},{actual.Top} to {actual.Right},{actual.Bottom}");

    /// <summary>
    /// Moves an item between two containers through the command, asserting that where it is drawn did not
    /// change while it happened, and returning that world box for the caller to check further.
    /// </summary>
    private static Rect2D ThroughTheCommand(CommandStack stack, LayerItem item, IItemContainer target)
    {
        Rect2D drawn = InWorld(item);
        stack.Execute(new MoveItemsCommand(new LayerItem[] { item }, target, target.Children.Count));
        AssertWorld(drawn, InWorld(item), "the art after the move");
        return drawn;
    }

    /// <summary>
    /// Moving a path into a transformed group and back out again leaves it where it was drawn.
    ///
    /// The group is `translate(150,80) scale(2) rotate(30°)` and its square is written at 10,10..30,30, so
    /// the square is drawn at 137.3205,107.3205 to 191.9615,161.9615 document units. Dropping it on a layer
    /// removes it from the group - the frame its numbers were written in - and putting it back restores that
    /// frame; neither move may change where it is drawn.
    ///
    /// Against the old code the square kept its 10,10 numbers when it left the group, so it jumped to
    /// 10,10 in the artboard frame, over 180 units away, and the move back did not undo the damage.
    /// </summary>
    [Fact]
    public void MovingIntoAndBackOutOfATransformedGroupLeavesTheArtWhereItWasDrawn()
    {
        (_, ArtGroup group, PathItem path, Layer layer) = Page();

        // The numbers the group's own frame holds, which is what the conversion has to preserve.
        Assert.Equal(new Rect2D(10, 10, 20, 20), path.BoundingBox());

        const double left = 137.3205080756888;
        const double top = 107.32050807568876;
        const double right = 191.96152422706632;
        const double bottom = 161.96152422706632;
        Rect2D drawn = InWorld(path);
        AssertWorld(new Rect2D(left, top, right - left, bottom - top), drawn, "the square inside the group");

        var stack = new CommandStack();
        stack.Execute(new MoveItemsCommand(new LayerItem[] { path }, layer, layer.Children.Count));

        Assert.Same(layer, path.Container);
        AssertWorld(drawn, InWorld(path), "the square on the layer");

        // On the layer the numbers *are* the page's, which is the conversion having happened.
        AssertWorld(new Rect2D(left, top, right - left, bottom - top), path.BoundingBox(),
            "the stored geometry on the layer");

        stack.Execute(new MoveItemsCommand(new LayerItem[] { path }, group, group.Children.Count));

        Assert.Same(group, path.Container);
        AssertWorld(drawn, InWorld(path), "the square back in the group");
    }

    /// <summary>
    /// The geometry stored on the way out is the group's own numbers, because the conversion is exact in
    /// both directions rather than a translation of the artboard origin.
    /// </summary>
    [Fact]
    public void TheConversionIsReversibleInAnAffineFrame()
    {
        (_, ArtGroup group, PathItem path, Layer layer) = Page();

        const double left = 137.3205080756888;
        const double top = 107.32050807568876;
        const double right = 191.96152422706632;
        const double bottom = 161.96152422706632;

        var stack = new CommandStack();
        stack.Execute(new MoveItemsCommand(new LayerItem[] { path }, layer, layer.Children.Count));
        AssertWorld(new Rect2D(left, top, right - left, bottom - top), path.BoundingBox(),
            "the stored geometry on the layer");

        stack.Undo();

        Assert.Same(group, path.Container);
        Assert.Equal(10.0, path.BoundingBox().Left, 6);
        Assert.Equal(30.0, path.BoundingBox().Right, 6);
        Assert.Equal(new Rect2D(10, 10, 20, 20), path.BoundingBox());
        AssertWorld(new Rect2D(left, top, right - left, bottom - top), InWorld(path),
            "the square after the undo");
    }

    /// <summary>
    /// A whole group moved to another container takes its geometry with it and leaves its children where
    /// they were drawn - the group's own transform is part of the geometry that has to be converted.
    ///
    /// A group cannot carry a general affine in its <see cref="ArtGroup.Transform"/> alone - the transform
    /// is overwritten rather than conjugated - so the conversion reaches its children, which is where the
    /// geometry actually is.
    /// </summary>
    [Fact]
    public void MovingAGroupLeavesItsChildrenWhereTheyWereDrawn()
    {
        (CadDocument document, ArtGroup group, PathItem path, _) = Page();
        var other = new ArtGroup
        {
            Name = "outer",
            Transform = AffineTransform.CreateTranslation(60, 20).Compose(AffineTransform.CreateScale(3, 3)),
        };
        document.Artboards[0].Layers[0].AddItem(other);

        Rect2D drawn = InWorld(path);
        AssertWorld(new Rect2D(137.3205080756888, 107.32050807568876, 54.64101615137753, 54.641016151377556),
            drawn, "the square");

        var stack = new CommandStack();
        Rect2D after = ThroughTheCommand(stack, group, other);

        Assert.Same(other, group.Container);
        AssertWorld(drawn, InWorld(path), "the square read back after the group moved");

        stack.Undo();
        AssertWorld(drawn, InWorld(path), "the square after the undo");
    }

    /// <summary>
    /// A path is converted through a rotation with no translation at all, which a conversion that only
    /// carried the artboard origin would get wrong: on a page at the origin that conversion is the
    /// identity, so the square would keep the 40,0 it was written at while the group turned it.
    /// </summary>
    [Fact]
    public void APathIsConvertedThroughARotation()
    {
        var document = CadDocument.CreateDefault("Page 1");
        Layer layer = document.Artboards[0].Layers[0];
        var group = new ArtGroup
        {
            Name = "turned",
            Transform = AffineTransform.CreateRotation(Math.PI / 3),
        };
        layer.AddItem(group);

        PathItem path = Box("square", new Rect2D(40, 0, 10, 10));
        layer.AddItem(path);

        Rect2D drawn = InWorld(path);
        AssertWorld(new Rect2D(40, 0, 10, 10), drawn, "the square on the layer");

        var stack = new CommandStack();
        Rect2D after = ThroughTheCommand(stack, path, group);

        Assert.Same(group, path.Container);
        AssertWorld(drawn, after, "the square in the turned group");

        // The stored geometry really did move: through a 60-degree turn the page's numbers are not the
        // group's, so keeping them would have left the square somewhere else on the page.
        Assert.NotEqual(40.0, path.BoundingBox().Left, 6);
    }

    /// <summary>
    /// A text block cannot store the conjugate of a scale in its origin and angle, so the move is refused
    /// and the block stays exactly where it was - the rule that generated #165 and #172 rather than a
    /// second rule invented here.
    /// </summary>
    [Fact]
    public void MovingTextIntoAScaledGroupIsRefusedRatherThanStoredWrong()
    {
        var document = CadDocument.CreateDefault("Page 1");
        Layer layer = document.Artboards[0].Layers[0];
        var group = new ArtGroup
        {
            Name = "panel",
            Transform = AffineTransform.CreateScale(2, 2),
        };
        layer.AddItem(group);

        var text = new TextItem { Name = "words", Origin = new Point2D(50, 60) };
        text.PlainText = "words";
        layer.AddItem(text);

        Point2D drawn = SelectionEngine.ToWorld(text).Transform(text.Origin);

        var stack = new CommandStack();
        var command = new MoveItemsCommand(new LayerItem[] { text }, group, 0);

        FrameConversionException error = Assert.Throws<FrameConversionException>(() => stack.Execute(command));
        Assert.Contains("TextItem", error.Message);

        // Nothing moved: still on its layer, its origin untouched.
        Assert.Same(layer, text.Container);
        Assert.Equal(new Point2D(50, 60), text.Origin);
        Assert.Equal(drawn, SelectionEngine.ToWorld(text).Transform(text.Origin));
    }

    /// <summary>
    /// A frame that collapses the plane has no inverse, so there is no honest destination for the
    /// geometry: the move is refused and the document is left exactly as it was.
    ///
    /// The group is `scale(1,0)`, which paints its contents on a line and from which no world position can
    /// be recovered. Added without conversion - the old behaviour - the square would have been stored in a
    /// frame it was never drawn in and would have vanished onto that line.
    /// </summary>
    [Fact]
    public void MovingIntoAFrameThatIsNotInvertibleIsRefused()
    {
        var document = CadDocument.CreateDefault("Page 1");
        Layer layer = document.Artboards[0].Layers[0];
        var collapsing = new ArtGroup
        {
            Name = "collapsing",
            Transform = AffineTransform.CreateScale(1, 0),
        };
        layer.AddItem(collapsing);

        PathItem path = Box("square", new Rect2D(10, 20, 30, 40));
        layer.AddItem(path);

        var stack = new CommandStack();
        FrameConversionException error = Assert.Throws<FrameConversionException>(
            () => stack.Execute(new MoveItemsCommand(new LayerItem[] { path }, collapsing, 0)));

        Assert.Contains("collapsing", error.Message);
        Assert.Contains("invertib", error.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Same(layer, path.Container);
        Assert.Equal(new Rect2D(10, 20, 30, 40), path.BoundingBox());
        Assert.Equal(0, stack.Depth);
    }

    /// <summary>
    /// A move between two artboards that sit at different origins is a frame change even though no group is
    /// involved, and the art has to stay where it is drawn.
    ///
    /// Page 1 is at (0,0) and page 2 at (600,400); the square is drawn at 10,20..40,60 on page 1 and must
    /// still be drawn there once it is stored on page 2 - which means its numbers change by the origin it
    /// gained.
    /// </summary>
    [Fact]
    public void MovingBetweenArtboardsKeepsTheArtWhereItIsDrawn()
    {
        CadDocument document = TwoPages();
        Artboard first = document.Artboards[0];
        Artboard second = document.Artboards[1];

        PathItem path = Box("square", new Rect2D(10, 20, 30, 40));
        first.Layers[0].AddItem(path);
        AssertWorld(new Rect2D(10, 20, 30, 40), InWorld(path), "the square on page 1");

        var stack = new CommandStack();
        stack.Execute(new MoveItemsCommand(new LayerItem[] { path }, second.Layers[0], 0));

        Assert.Same(second.Layers[0], path.Container);
        AssertWorld(new Rect2D(10, 20, 30, 40), InWorld(path), "the square on page 2");
        Assert.Equal(-590, path.BoundingBox().Left, 6);

        stack.Undo();
        Assert.Same(first.Layers[0], path.Container);
        AssertWorld(new Rect2D(10, 20, 30, 40), InWorld(path), "the square after the undo");
    }

    /// <summary>A page with a transformed group holding a square, the square's layer, and the document.</summary>
    private static (CadDocument Document, ArtGroup Group, PathItem Path, Layer Layer) Page()
    {
        var document = CadDocument.CreateDefault("Page 1");
        Layer layer = document.Artboards[0].Layers[0];

        var group = new ArtGroup
        {
            Name = "panel",
            Transform = AffineTransform.CreateTranslation(150, 80)
                .Compose(AffineTransform.CreateScale(2, 2))
                .Compose(AffineTransform.CreateRotation(Math.PI / 6)),
        };

        PathItem path = Box("square", new Rect2D(10, 10, 20, 20));
        group.AddItem(path);
        layer.AddItem(group);

        return (document, group, path, layer);
    }

    private static CadDocument TwoPages()
    {
        var document = new CadDocument();

        var first = new Artboard(new Size2D(500, 400), new Point2D(0, 0)) { Name = "Page 1" };
        first.AddLayer("Layer 1");
        document.AddArtboard(first);

        var second = new Artboard(new Size2D(500, 400), new Point2D(600, 400)) { Name = "Page 2" };
        second.AddLayer("Layer 1");
        document.AddArtboard(second);

        return document;
    }
}
