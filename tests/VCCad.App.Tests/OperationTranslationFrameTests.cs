using System.Text.Json;
using VCCad.App.Automation;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Core.Svg;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// An operation moves art by the distance it was asked for, inside a transformed group as well as
/// outside one (issue #172).
///
/// #165 taught the pointer gestures to carry a world delta into the frame the geometry is stored in
/// (<see cref="VCCad.Core.Selection.SelectionEngine.FromWorld"/> / <c>DeltaInItem</c>). It explicitly did
/// not touch the API translation paths, which still added a world delta straight to stored coordinates -
/// so a translate through an operation moved the art by the group transform applied to the delta, while
/// the identical drag moved it by the pointer distance. The repository's parity rule says a person and a
/// driver must do the same thing, so the two halves disagreeing is the defect, not a cosmetic difference.
///
/// Every assertion is on the **model geometry**, measured by this test's own composition of the group
/// transforms rather than by asking the code under test - a wrong delta is a wrong file, and a test that
/// only watched the canvas would pass for an operation that wrote nothing.
/// </summary>
public class OperationTranslationFrameTests
{
    // 200x200 user units at 0.75 pt per unit is a 150x150 pt page: the root group's own unit conversion.
    private const string Header = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"200\" height=\"200\">";

    /// <summary>`translate(50,50) scale(2)` on a 10-unit square: 37.5..52.5 pt, a 15 pt box.</summary>
    private const string TranslatedAndScaled = Header +
        "<g transform=\"translate(50,50) scale(2)\"><rect width=\"10\" height=\"10\" fill=\"#000000\"/></g></svg>";

    /// <summary>`translate(40,40) rotate(90)`: 22.5..30 by 30..37.5 pt. A frame of unit scale, so a fix
    /// that only divided by the scale would pass the distance and fail the direction.</summary>
    private const string Rotated = Header +
        "<g transform=\"translate(40,40) rotate(90)\"><rect width=\"10\" height=\"10\" fill=\"#000000\"/></g></svg>";

    private static JsonElement Params(object value) => JsonSerializer.SerializeToElement(value);

    /// <summary>
    /// Imports the figure through the registry, so the document is built by the same path a driver uses,
    /// and selects nothing: each test selects explicitly, as an operation with `itemIds` does.
    /// </summary>
    private static (AutomationContext Context, PathItem Rect) Host(string svg)
    {
        var viewModel = new EditorViewModel();
        var context = new AutomationContext { ViewModel = viewModel };
        EditorOperations.Invoke(context, "document.importSvg", Params(new
        {
            svgBase64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(svg)),
        }));

        return (context, Assert.Single(viewModel.Document.AllPaths()));
    }

    /// <summary>Invokes an operation and returns the item it should name, re-found by id after any edit.</summary>
    private static PathItem InvokeFor(AutomationContext context, Guid id, string op, object p)
    {
        EditorOperations.Invoke(context, op, Params(p));
        return EditorOperations.AllItems(context.ViewModel.Document).OfType<PathItem>().Single(i => i.Id == id);
    }

    /// <summary>
    /// An item's box in **document/world** coordinates, composed here rather than asked of the code
    /// under test: every enclosing group's transform, outermost last, plus the artboard origin. This is
    /// the frame the pointer moves in and the frame a person sees the artwork in.
    /// </summary>
    private static Rect2D InWorld(LayerItem item)
    {
        Rect2D box = item switch
        {
            PathItem path => path.BoundingBox(),
            TextItem text => text.BoundingBox(),
            ImageItem image => image.Placement,
            _ => Rect2D.Empty,
        };

        if (box.IsEmpty)
        {
            return box;
        }

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

        Rect2D mapped = transform.Transform(box);
        Vector2D origin = item.ArtboardOffset();
        return new Rect2D(mapped.X + origin.X, mapped.Y + origin.Y, mapped.Width, mapped.Height);
    }

    /// <summary>
    /// Asserts the box moved by a vector, reporting **both** components so a direction error and a
    /// magnitude error read differently. The operation is exact arithmetic, so the tolerance is far
    /// smaller than the 15-point error the un-converted frame produces.
    /// </summary>
    private static void AssertMovedBy(Vector2D expected, Rect2D before, Rect2D after)
    {
        var moved = new Vector2D(after.Left - before.Left, after.Top - before.Top);
        Assert.True(
            Math.Abs(moved.X - expected.X) <= 0.5 && Math.Abs(moved.Y - expected.Y) <= 0.5,
            $"the art should have moved {expected.X},{expected.Y} but moved {moved.X},{moved.Y}");
    }

    /// <summary>
    /// `object.move` inside a `translate(50,50) scale(2)` group moves the art by the distance asked for.
    ///
    /// The frame has a scale of 1.5 in world points, so a 30 pt request is 20 units of the square's own
    /// coordinates. Adding the world delta to stored coordinates - today's behaviour - moves the artwork
    /// 45 pt, and the person who scripted 30 sees the art slide 15 pt past where they asked. It also
    /// **tears the rect out of its group**, because the rehome check compares containers rather than
    /// artboards; a drag of the same object leaves it in the group, so the operation and the gesture
    /// disagree about the document's structure as well as about the distance. Both halves are asserted:
    /// the distance, and that the rect is still where the file put it.
    /// </summary>
    [Fact]
    public void AMoveOperationInsideAScaledGroupMovesTheArtByTheDistance()
    {
        (AutomationContext context, PathItem rect) = Host(TranslatedAndScaled);
        IItemContainer? containerBefore = rect.Container;
        Rect2D before = InWorld(rect);
        Assert.Equal(37.5, before.Left, 3);
        Assert.Equal(37.5, before.Top, 3);

        rect = InvokeFor(context, rect.Id, "object.move", new { dx = 30.0, dy = 0.0, itemIds = new[] { rect.Id } });

        Rect2D after = InWorld(rect);
        AssertMovedBy(new Vector2D(30, 0), before, after);
        Assert.Equal(15.0, after.Width, 3);
        Assert.Equal(15.0, after.Height, 3);
        Assert.Same(containerBefore, rect.Container);
    }

    /// <summary>
    /// `object.move` inside a `translate(40,40) rotate(90)` group moves in the direction asked for, as
    /// well as the distance.
    ///
    /// This frame has a unit scale, so a fix that only divided by the frame's scale would pass a
    /// distance-only assertion: applying the world delta to stored coordinates sends the square 22.5 pt
    /// **down the page** for a pointer that moved 30 pt to the right. Both components are asserted, so
    /// the frame has to be turned as well as scaled.
    /// </summary>
    [Fact]
    public void AMoveOperationInsideARotatedGroupMovesInTheDirectionItMoved()
    {
        (AutomationContext context, PathItem rect) = Host(Rotated);
        IItemContainer? containerBefore = rect.Container;
        Rect2D before = InWorld(rect);
        Assert.Equal(22.5, before.Left, 3);
        Assert.Equal(30.0, before.Top, 3);

        rect = InvokeFor(context, rect.Id, "object.move", new { dx = 30.0, dy = 0.0, itemIds = new[] { rect.Id } });

        Rect2D after = InWorld(rect);
        AssertMovedBy(new Vector2D(30, 0), before, after);
        Assert.Same(containerBefore, rect.Container);
    }

    /// <summary>
    /// `object.transform`'s translation is the same conversion: it is a second registry entry onto the
    /// same arithmetic, and a fix applied to one and not the other is exactly the half-fixed state #172
    /// is about.
    /// </summary>
    [Fact]
    public void ATransformTranslationInsideAScaledGroupMovesTheArtByTheDistance()
    {
        (AutomationContext context, PathItem rect) = Host(TranslatedAndScaled);
        Rect2D before = InWorld(rect);

        // `object.transform` acts on the selection and takes no itemIds, so the selection is made the
        // way a driver makes it.
        EditorOperations.Invoke(context, "selection.set", Params(new { itemIds = new[] { rect.Id } }));
        rect = InvokeFor(context, rect.Id, "object.transform",
            new { translateX = 30.0, translateY = 0.0 });

        AssertMovedBy(new Vector2D(30, 0), before, InWorld(rect));
        Assert.Equal(before.Width, InWorld(rect).Width, 3);
    }

    /// <summary>
    /// `object.setPosition` puts the selection's top-left at the document point it was given. Its delta
    /// is derived from the selection's bounds, so the bounds and the delta have to be in the same frame
    /// as the request - document space - or the top-left lands at the group transform applied to the
    /// point that was asked for.
    /// </summary>
    [Fact]
    public void ASetPositionInsideAScaledGroupPutsTheTopLeftWhereItWasAsked()
    {
        (AutomationContext context, PathItem rect) = Host(TranslatedAndScaled);

        rect = InvokeFor(context, rect.Id, "object.setPosition",
            new { x = 67.5, y = 37.5, itemIds = new[] { rect.Id } });

        Rect2D after = InWorld(rect);
        Assert.True(
            Math.Abs(after.Left - 67.5) <= 0.5 && Math.Abs(after.Top - 37.5) <= 0.5,
            $"the top-left should be at 67.5,37.5 but is at {after.Left},{after.Top}");
    }

    /// <summary>
    /// `path.moveNode` takes a **document** point and moves the stored node to it, so the point has to be
    /// carried into the path's own frame before it is compared with the node. Subtracting only the
    /// artboard origin - which is what the absolute-position path did - lands the node at the group
    /// transform applied to the requested point.
    /// </summary>
    [Fact]
    public void AMovedNodeInsideAScaledGroupLandsOnTheDocumentPointItWasGiven()
    {
        (AutomationContext context, PathItem rect) = Host(TranslatedAndScaled);

        EditorOperations.Invoke(context, "path.moveNode",
            Params(new { itemId = rect.Id, sub = 0, node = 0, x = 60.0, y = 45.0 }));

        Point2D anchor = rect.SubPaths[0].Nodes[0].Anchor;
        AffineTransform toWorld = VCCad.Core.Selection.SelectionEngine.ToWorld(rect);
        Point2D world = toWorld.Transform(anchor);

        Assert.True(
            Math.Abs(world.X - 60.0) <= 0.5 && Math.Abs(world.Y - 45.0) <= 0.5,
            $"the node should be at 60,45 in document space but is at {world.X},{world.Y}");
    }
}
