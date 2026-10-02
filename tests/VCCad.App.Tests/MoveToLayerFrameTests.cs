using System.Text.Json;
using VCCad.App.Automation;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Core.Selection;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// Reparenting through the operations converts geometry into the destination container's frame
/// (issue #174).
///
/// `object.moveToLayer` ends at <see cref="VCCad.Core.Commands.MoveItemsCommand"/>, which used to move an
/// item between containers without touching its geometry. Filing art onto a page that sits elsewhere in
/// the sheet therefore stored one frame's numbers in another, so the art jumped and every later operation
/// inherited the error - the worse half of the defect, because the document keeps the wrong numbers.
///
/// This is the **operation** path on purpose: the parity rule says a person and a driver must be able to do
/// the same thing, so the fix has to hold for the registry and not only for the command behind it.
/// Measured on **model geometry**, composed by this test rather than asked of the code under test.
/// </summary>
public class MoveToLayerFrameTests
{
    private static JsonElement Params(object value) => JsonSerializer.SerializeToElement(value);

    private static (AutomationContext Context, EditorViewModel ViewModel) Page()
    {
        var viewModel = new EditorViewModel();
        return (new AutomationContext { ViewModel = viewModel }, viewModel);
    }

    /// <summary>Creates a rectangle through the registry, at a point in **document** space.</summary>
    private static PathItem CreateRectangle(AutomationContext context, double x, double y, double w, double h)
    {
        object result = EditorOperations.Invoke(context, "object.create", Params(new
        {
            type = "rectangle",
            x,
            y,
            width = w,
            height = h,
        }))!;

        string id = result.GetType().GetProperty("itemId")!.GetValue(result)!.ToString()!;
        return context.ViewModel.Document.AllPaths().Single(p => p.Id.ToString() == id);
    }

    private static PathItem Square(string name, Rect2D box)
    {
        PathItem path = PathFactory.CreateRectangle(name, box);
        path.Fill = FillSpec.Solid(ColorRgb.Black);
        return path;
    }

    /// <summary>An item's box in document coordinates, composed from the document's own transforms.</summary>
    private static Rect2D InWorld(PathItem path)
    {
        Rect2D nodes = Rect2D.Empty;
        AffineTransform world = SelectionEngine.ToWorld(path);
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

    private static void AssertWorld(Rect2D expected, Rect2D actual, string what)
        => Assert.True(
            Math.Abs(actual.Left - expected.Left) <= 1e-6 &&
            Math.Abs(actual.Top - expected.Top) <= 1e-6 &&
            Math.Abs(actual.Right - expected.Right) <= 1e-6 &&
            Math.Abs(actual.Bottom - expected.Bottom) <= 1e-6,
            $"{what} should be {expected.Left},{expected.Top} to {expected.Right},{expected.Bottom} " +
            $"but is {actual.Left},{actual.Top} to {actual.Right},{actual.Bottom}");

    /// <summary>
    /// `object.moveToLayer` onto a page that sits at a different origin leaves the art where it was drawn.
    ///
    /// Page 1 is at (0,0) and page 2 at (600,400), so the rectangle's stored numbers have to gain the origin
    /// it is losing. Against the old code they did not, and the rectangle appeared 600,400 units away on
    /// page 2 - which is what a driver saw when it filed art onto a second page.
    /// </summary>
    [Fact]
    public void MoveToLayerBetweenPagesLeavesTheArtWhereItWasDrawn()
    {
        (AutomationContext context, EditorViewModel viewModel) = Page();
        CadDocument document = viewModel.Document;

        Artboard second = document.AddArtboard(PageSizes.A4Landscape, "Page 2", new Point2D(600, 400));
        Layer target = second.AddLayer("Layer 1");

        PathItem rect = CreateRectangle(context, 40, 60, 30, 20);
        Rect2D drawn = InWorld(rect);
        AssertWorld(new Rect2D(40, 60, 30, 20), drawn, "the rectangle on page 1");

        EditorOperations.Invoke(context, "object.moveToLayer", Params(new
        {
            layerId = target.Id,
            itemIds = new[] { rect.Id },
        }));

        PathItem moved = document.AllPaths().Single(p => p.Id == rect.Id);
        Assert.Same(target, moved.Container);
        AssertWorld(drawn, InWorld(moved), "the rectangle on page 2");

        // The numbers it now stores are page 2's, which is the conversion having happened.
        AssertWorld(new Rect2D(-560, -340, 30, 20), moved.BoundingBox(), "the stored geometry");
    }

    /// <summary>
    /// The same reparent through `DocumentSession.MoveItems` - the path the Layers panel drop takes, and the
    /// one that reaches a **group** rather than a layer - leaves a path inside a transformed group where it
    /// was drawn, and puts it back on the way out.
    ///
    /// The group is `translate(150,80) scale(2) rotate(30°)`; its square is drawn at 137.3205,107.3205 to
    /// 191.9615,161.9615 document units. Against the old code the square kept its 10,10 numbers when it left
    /// the group, so it jumped to 10,10 in the page's frame, over 180 units away.
    /// </summary>
    [Fact]
    public void MoveItemsIntoAndOutOfATransformedGroupLeavesTheArtWhereItWasDrawn()
    {
        (AutomationContext context, EditorViewModel viewModel) = Page();
        _ = context;
        Layer layer = viewModel.Document.Artboards[0].Layers[0];

        var group = new ArtGroup
        {
            Name = "panel",
            Transform = AffineTransform.CreateTranslation(150, 80)
                .Compose(AffineTransform.CreateScale(2, 2))
                .Compose(AffineTransform.CreateRotation(Math.PI / 6)),
        };
        layer.AddItem(group);

        PathItem square = Square("square", new Rect2D(10, 10, 20, 20));
        group.AddItem(square);

        const double left = 137.3205080756888;
        const double top = 107.32050807568876;
        const double right = 191.96152422706632;
        const double bottom = 161.96152422706632;
        var drawn = new Rect2D(left, top, right - left, bottom - top);
        AssertWorld(drawn, InWorld(square), "the square inside the group");

        // Out of the group, onto the page it is drawn on.
        viewModel.ActiveSession.MoveItems(new LayerItem[] { square }, layer, layer.Children.Count);

        Assert.Same(layer, square.Container);
        AssertWorld(drawn, InWorld(square), "the square on the layer");

        // And back in, which has to restore the frame its numbers were written in.
        viewModel.ActiveSession.MoveItems(new LayerItem[] { square }, group, group.Children.Count);

        Assert.Same(group, square.Container);
        AssertWorld(drawn, InWorld(square), "the square back in the group");
        AssertWorld(new Rect2D(10, 10, 20, 20), square.BoundingBox(), "the restored stored geometry");
    }
}
