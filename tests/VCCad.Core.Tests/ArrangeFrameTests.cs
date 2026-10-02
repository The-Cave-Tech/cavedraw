using VCCad.Core.Model;
using VCCad.Core.Selection;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// Aligning a selection that spans frames measures every object in **one** frame (issue #174).
///
/// `ItemBounds.Of` answers about an item's own placement frame - a group's transform, no ancestors -
/// while the delta an arrangement produces is a displacement in the frame the layout was measured in.
/// For a selection that lives in one frame the two coincide and the mistake is invisible; the moment a
/// selection spans a group boundary the bounds are in different spaces and the computed delta is wrong
/// for both of them.
///
/// The arrangement's bounds are stated in the **artboard** frame - the frame the document stores its own
/// coordinates in - and a delta taken from them is a displacement in that frame. Stored geometry lives in
/// the item's own frame, so `DocumentSession.MoveByDeltas` gains the artboard origin and carries the
/// result across with `SelectionEngine.DeltaInItem`, the composition the repository states once. These
/// tests use the same conversion, so they measure the model geometry the fix has to produce.
///
/// Everything is measured on **model geometry** - where the document draws each object - by the test's own
/// arithmetic over the transforms the document states, never `ItemBounds`, which is one of the things
/// under test.
/// </summary>
public class ArrangeFrameTests
{
    private static PathItem Box(string name, double x, double y, double width, double height)
    {
        PathItem path = PathFactory.CreateRectangle(name, new Rect2D(x, y, width, height));
        path.Fill = FillSpec.Solid(ColorRgb.Black);
        return path;
    }

    /// <summary>
    /// An item's box in document coordinates, composed from the document's own transforms.
    ///
    /// A path is measured from its **nodes**, each carried through the frame: a path's local bounding box is
    /// not its geometry once a frame turns or shears it, and an affine image of a box is not the box of the
    /// image. The rectangles these tests build have their anchors at the corners, so a node box and a
    /// bounding box agree for them - which is the point, because the numbers below are the rectangle's own.
    /// </summary>
    private static Rect2D InWorld(LayerItem item)
    {
        AffineTransform local = item is ArtGroup own ? own.Transform : AffineTransform.Identity;
        AffineTransform world = SelectionEngine.ToWorld(item).Compose(local);

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

    /// <summary>Where a path's stored geometry sits, without any frame of its own.</summary>
    private static double StoredLeft(PathItem path) => path.BoundingBox().Left;

    /// <summary>
    /// Applies an arrangement the way `DocumentSession.MoveByDeltas` does: the delta is a displacement in
    /// the artboard frame, geometry is stored in the item's own frame, and
    /// <see cref="SelectionEngine.DeltaInItem"/> is the conversion between them.
    ///
    /// Every box is measured **before** anything moves, because a delta is relative to where the selection
    /// was when the arrangement was computed - applying one and then reading the next item's position would
    /// measure a half-arranged selection.
    /// </summary>
    private static void Apply(IReadOnlyList<(LayerItem Item, Vector2D Delta)> moves)
    {
        var carried = new List<(LayerItem Inside, Vector2D Delta)>();

        foreach ((LayerItem item, Vector2D delta) in moves)
        {
            foreach (LayerItem inside in Flatten(item))
            {
                carried.Add((inside, SelectionEngine.DeltaInItem(inside, delta + inside.ArtboardOffset())));
            }
        }

        foreach ((LayerItem inside, Vector2D delta) in carried)
        {
            switch (inside)
            {
                case PathItem path:
                    path.TranslateGeometryBy(delta);
                    break;
                case TextItem text:
                    text.Origin += delta;
                    break;
            }
        }
    }

    private static IEnumerable<LayerItem> Flatten(LayerItem item)
    {
        yield return item;

        if (item is ArtGroup group)
        {
            foreach (LayerItem child in group.Children)
            {
                foreach (LayerItem nested in Flatten(child))
                {
                    yield return nested;
                }
            }
        }
    }

    private static void AssertSame(double expected, double actual, string what)
        => Assert.True(Math.Abs(expected - actual) <= 1e-6,
            $"{what} should be {expected} but is {actual}");

    /// <summary>
    /// Two objects that live in different frames are aligned on the same world edge.
    ///
    /// The group is `translate(200,300) scale(2)`, so the 10-unit square written at 5,5 is drawn at
    /// 210..230 by 310..330 document units, while the object outside the group is written straight into
    /// the artboard frame at 0..40. The selection's left edge is therefore 0 - the loose object's - and
    /// aligning to the start puts **both** on it: the loose object is already there, and the group has to
    /// travel 210 units left, which is 105 units of the square's own space inside a group that doubles.
    ///
    /// Against the old code the group's own bounds (5,5 - its local frame) were compared with the loose
    /// object's (0,0 - the artboard frame), so the selection's extent was 0..40 and the group was moved by
    /// a delta computed against the wrong frame; the two never shared an edge.
    /// </summary>
    [Fact]
    public void AligningAcrossAGroupBoundaryPutsBothOnTheSameWorldEdge()
    {
        (ArtGroup group, PathItem inside, PathItem outside, Layer _) = TwoFrames();

        // The frames really do differ: the same numbers are 210 apart on the page.
        AssertSame(210, InWorld(inside).Left, "the grouped square's left edge");
        AssertSame(0, InWorld(outside).Left, "the loose square's left edge");

        Apply(Arrange.Align(new LayerItem[] { group, outside }, ArrangeAxis.Horizontal, ArrangeEdge.Start));

        AssertSame(0, InWorld(inside).Left, "the grouped square after aligning");
        AssertSame(0, InWorld(outside).Left, "the loose square after aligning");
    }

    /// <summary>
    /// The grouped item receives the delta **in its own frame**, not the artboard one.
    ///
    /// The selection travels 210 units left, and its group doubles everything, so the square's own numbers
    /// move by 105 - from 5 to -100. Added raw they would have moved by 210 and the square would be drawn
    /// 210 units past the line it was aligned to.
    /// </summary>
    [Fact]
    public void TheGroupedItemTakesTheDeltaInItsOwnFrame()
    {
        (ArtGroup group, PathItem inside, PathItem outside, Layer _) = TwoFrames();

        Apply(Arrange.Align(new LayerItem[] { group, outside }, ArrangeAxis.Horizontal, ArrangeEdge.Start));

        AssertSame(-100, StoredLeft(inside), "the grouped square's stored left edge");
        AssertSame(0, StoredLeft(outside), "the loose square's stored left edge");
    }

    /// <summary>
    /// The conversion is a **frame** conversion and not a translation: a rotated group carries a
    /// horizontal world displacement onto its own axis.
    ///
    /// The group is `translate(50,60) rotate(90°)`, so its 10-unit square written at 0,0 is drawn at
    /// 40..50 by 60..70, and the loose object is at 0..40 by 0..40. Aligning to the start moves the group
    /// 40 units left; in the group's own frame that is a displacement along its y axis, which is what a
    /// translation-only conversion would get wrong. Afterwards both left edges are at 0 and the square is
    /// drawn 0..10 by 60..70 - its own numbers became 0,-40.
    /// </summary>
    [Fact]
    public void AligningIntoARotatedGroupKeepsTheGroupedObjectWhereItIsDrawn()
    {
        var document = CadDocument.CreateDefault("Page 1");
        Layer layer = document.Artboards[0].Layers[0];

        var group = new ArtGroup
        {
            Name = "turned",
            Transform = AffineTransform.CreateTranslation(50, 60)
                .Compose(AffineTransform.CreateRotation(Math.PI / 2)),
        };

        PathItem inside = Box("inside", 0, 0, 10, 10);
        PathItem outside = Box("outside", 0, 0, 40, 40);
        group.AddItem(inside);
        layer.AddItem(group);
        layer.AddItem(outside);

        AssertSame(40, InWorld(inside).Left, "the turned square's left edge before aligning");

        Apply(Arrange.Align(new LayerItem[] { group, outside }, ArrangeAxis.Horizontal, ArrangeEdge.Start));

        AssertSame(0, InWorld(inside).Left, "the turned square after aligning");
        AssertSame(0, InWorld(outside).Left, "the loose square after aligning");

        // A translation along the artboard's x axis is a displacement along the group's own y axis, and
        // this frame's y axis runs along the page's -y, so the stored y grows.
        AssertSame(0, inside.BoundingBox().Left, "the turned square's stored x");
        AssertSame(40, inside.BoundingBox().Top, "the turned square's stored y");
    }

    /// <summary>
    /// Distributing across frames applies each object's delta **in that object's own frame**, so the same
    /// displacement shows up on the page for the grouped object and the loose ones.
    ///
    /// The group is `translate(200,300) scale(2)`, so its square is drawn at 200..220 while its own numbers
    /// are 0..10; the loose objects are written straight into the page's frame. The selection's extent is
    /// the union of where the three are drawn - 100..430 - and the layout is in stack order, so the grouped
    /// square is laid out first.
    ///
    /// What is asserted is the frame conversion, not the pretty layout: the grouped square's world position
    /// moves by exactly the delta the arrangement stated, which is only true if the delta reached its
    /// geometry halved by the group's scale rather than raw. Against the old code the raw delta moved it
    /// twice as far on the page as the loose objects - a double-applied frame.
    /// </summary>
    [Fact]
    public void DistributingAcrossFramesAppliesEachDeltaInItsOwnFrame()
    {
        var document = CadDocument.CreateDefault("Page 1");
        Layer layer = document.Artboards[0].Layers[0];

        var group = new ArtGroup
        {
            Name = "panel",
            Transform = AffineTransform.CreateTranslation(200, 300).Compose(AffineTransform.CreateScale(2, 2)),
        };

        PathItem inside = Box("inside", 0, 0, 10, 10);
        PathItem middle = Box("middle", 100, 0, 20, 10);
        PathItem outside = Box("outside", 400, 0, 30, 10);
        group.AddItem(inside);
        layer.AddItem(group);
        layer.AddItem(middle);
        layer.AddItem(outside);

        // The frames really do differ: the group's square is drawn at 200 and the middle object at 100.
        AssertSame(200, InWorld(inside).Left, "the grouped square's left edge");
        AssertSame(100, InWorld(middle).Left, "the middle object's left edge");

        var selection = new LayerItem[] { group, middle, outside };
        var moves = Arrange.Distribute(selection, ArrangeAxis.Horizontal, ArrangeAnchor.Start);

        // Every object in the selection receives a delta, and each is measured against where it is drawn.
        foreach (LayerItem item in selection)
        {
            Assert.Contains(moves, m => ReferenceEquals(m.Item, item));
        }

        // What is tracked for each item: for a group, the content whose geometry actually moves.
        var tracked = selection.ToDictionary(item => item, item => item is ArtGroup g ? (LayerItem)g.Children[0] : item);
        var was = tracked.ToDictionary(entry => entry.Key, entry => InWorld(entry.Value));

        Apply(moves);

        // The displacement on the page is the displacement the arrangement stated - for the object inside
        // the group and the loose ones alike. That is only true when the delta was carried into each item's
        // own frame: the group doubles everything, so the raw delta would move its square twice as far.
        foreach ((LayerItem item, Vector2D delta) in moves)
        {
            Rect2D before = was[item];
            Rect2D now = InWorld(tracked[item]);
            AssertSame(before.Left + delta.X, now.Left, $"{item.Name}'s left edge after the move");
            AssertSame(before.Top + delta.Y, now.Top, $"{item.Name}'s top edge after the move");
        }
    }

    /// <summary>
    /// A group **inside another group** is measured where it is drawn, not in the frame its own numbers are
    /// written in.
    ///
    /// This is the case that tells the two candidate measurements apart, and it is the reason the fix is a
    /// frame rather than a `DeltaInItem` in the caller. `ItemBounds.Of` composes only an item's **own**
    /// transform, so a nested group answers with its children's local numbers - 500 here - while the object
    /// it is aligned against answers in the artboard frame. The two are in different spaces and no amount of
    /// converting the resulting delta can repair a comparison that was already made between them.
    ///
    /// The outer group is `translate(100,100) scale(2)` and the inner group adds `translate(200,50)`, so the
    /// square written at 10,10 is drawn with its left edge at 520. A loose object at 0..40 is the selection's
    /// left edge, and aligning to it has to bring the square to 0 - which needs the inner group's delta to be
    /// measured from 520, not from the 500 its own frame reports.
    /// </summary>
    [Fact]
    public void ANestedGroupIsMeasuredWhereItIsDrawn()
    {
        var document = CadDocument.CreateDefault("Page 1");
        Layer layer = document.Artboards[0].Layers[0];

        var outer = new ArtGroup
        {
            Name = "outer",
            Transform = AffineTransform.CreateTranslation(100, 100).Compose(AffineTransform.CreateScale(2, 2)),
        };
        var inner = new ArtGroup
        {
            Name = "inner",
            Transform = AffineTransform.CreateTranslation(200, 50),
        };

        PathItem square = Box("square", 10, 10, 20, 20);
        PathItem loose = Box("loose", 0, 0, 40, 40);
        inner.AddItem(square);
        outer.AddItem(inner);
        layer.AddItem(outer);
        layer.AddItem(loose);

        // The inner group's own frame reports 10,10 carried only by its own translate - 210 - while the page
        // draws it at 520 because the outer group scales everything by two as well.
        AssertSame(210.0, ItemBounds.Of(inner).Left, "the nested group in its own frame");
        AssertSame(520.0, InWorld(square).Left, "the square where it is drawn");

        // The nested group itself is in the selection: its own frame and the page disagree, which is the
        // comparison the frame is for.
        Apply(Arrange.Align(new LayerItem[] { inner, loose }, ArrangeAxis.Horizontal, ArrangeEdge.Start));

        AssertSame(0, InWorld(loose).Left, "the loose object after aligning");
        AssertSame(0, InWorld(square).Left, "the nested square after aligning");
    }

    /// <summary>A group `translate(200,300) scale(2)` with a square in it, and a loose square outside.</summary>
    private static (ArtGroup Group, PathItem Inside, PathItem Outside, Layer Layer) TwoFrames()
    {
        var document = CadDocument.CreateDefault("Page 1");
        Layer layer = document.Artboards[0].Layers[0];

        var group = new ArtGroup
        {
            Name = "panel",
            Transform = AffineTransform.CreateTranslation(200, 300).Compose(AffineTransform.CreateScale(2, 2)),
        };

        PathItem inside = Box("inside", 5, 5, 10, 10);
        PathItem outside = Box("outside", 0, 0, 40, 40);
        group.AddItem(inside);
        layer.AddItem(group);
        layer.AddItem(outside);

        return (group, inside, outside, layer);
    }
}
