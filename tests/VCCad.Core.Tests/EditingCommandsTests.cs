using VCCad.Core.Commands;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

public class EditingCommandsTests
{
    [Fact]
    public void GeometryReplaceTranslatesAndUndoes()
    {
        CadDocument doc = CadDocument.CreateDefault();
        Layer layer = doc.Artboards[0].Layers[0];
        PathItem rect = PathFactory.CreateRectangle("r", new Rect2D(0, 0, 100, 50));
        layer.AddItem(rect);
        var stack = new CommandStack();

        PathItem before = rect.GeometrySnapshot();
        rect.TranslateGeometryBy(new Vector2D(25, -10));
        PathItem after = rect.GeometrySnapshot();

        stack.Execute(new GeometryReplaceCommand(rect, before, after, "Move swatch"));
        Assert.Equal(new Point2D(25, -10), rect.SubPaths[0].Nodes[0].Anchor);

        stack.Undo();
        Assert.Equal(new Point2D(0, 0), rect.SubPaths[0].Nodes[0].Anchor);

        stack.Redo();
        Assert.Equal(new Point2D(25, -10), rect.SubPaths[0].Nodes[0].Anchor);
    }

    [Fact]
    public void GeometrySnapshotsAreIndependent()
    {
        PathItem rect = PathFactory.CreateRectangle("r", new Rect2D(0, 0, 100, 50));
        PathItem snap = rect.GeometrySnapshot();

        rect.TranslateGeometryBy(new Vector2D(999, 999));

        Assert.Equal(new Point2D(0, 0), snap.SubPaths[0].Nodes[0].Anchor);
        Assert.Equal(new Point2D(999, 999), rect.SubPaths[0].Nodes[0].Anchor);
    }

    [Fact]
    public void RemoveItemUndoRestoresOriginalStacking()
    {
        CadDocument doc = CadDocument.CreateDefault();
        Layer layer = doc.Artboards[0].Layers[0];

        PathItem a = PathFactory.CreateRectangle("a", new Rect2D(0, 0, 10, 10));
        PathItem b = PathFactory.CreateRectangle("b", new Rect2D(0, 0, 10, 10));
        layer.AddItem(a);
        layer.AddItem(b);

        var stack = new CommandStack();
        stack.Execute(new RemoveItemCommand(a));
        Assert.Single(layer.Children);
        Assert.Equal(b, layer.Children[0]);

        stack.Undo();
        Assert.Equal(2, layer.Children.Count);
        Assert.Equal(a, layer.Children[0]); // original z-order restored
        Assert.Equal(layer, a.Container);
    }
}
