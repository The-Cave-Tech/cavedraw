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

    [Fact]
    public void GroupAndUngroupRoundTrip()
    {
        CadDocument doc = CadDocument.CreateDefault();
        Layer layer = doc.Artboards[0].Layers[0];
        PathItem a = PathFactory.CreateRectangle("a", new Rect2D(0, 0, 10, 10));
        PathItem b = PathFactory.CreateRectangle("b", new Rect2D(20, 0, 10, 10));
        PathItem c = PathFactory.CreateRectangle("c", new Rect2D(40, 0, 10, 10));
        layer.AddItem(a);
        layer.AddItem(b);
        layer.AddItem(c);

        var stack = new CommandStack();
        var groupCommand = new GroupItemsCommand(layer, new[] { a, b });
        stack.Execute(groupCommand);

        ArtGroup group = groupCommand.Group!;
        Assert.Equal(new LayerItem[] { group, c }, layer.Children);
        Assert.Equal(new LayerItem[] { a, b }, group.Children);

        stack.Undo();
        Assert.Equal(new LayerItem[] { a, b, c }, layer.Children);

        stack.Redo();
        Assert.Equal(new LayerItem[] { group, c }, layer.Children);

        stack.Execute(new UngroupItemsCommand(group));
        Assert.Equal(new LayerItem[] { a, b, c }, layer.Children);
        Assert.Empty(group.Children);

        stack.Undo();
        Assert.Equal(new LayerItem[] { group, c }, layer.Children);
        Assert.Equal(new LayerItem[] { a, b }, group.Children);
    }

    [Fact]
    public void DeleteArtboardKeepChildrenOrphansThemAndUndoRestores()
    {
        CadDocument doc = CadDocument.CreateDefault();
        Artboard artboard = doc.Artboards[0];
        PathItem path = PathFactory.CreateRectangle("r", new Rect2D(10, 10, 20, 20));
        artboard.Layers[0].AddItem(path);

        var stack = new CommandStack();
        stack.Execute(new DeleteArtboardCommand(doc, artboard, keepChildren: true));

        Assert.Empty(doc.Artboards);
        Assert.Single(doc.Orphans.Children);
        Assert.Equal(path, doc.Orphans.Children[0]);

        stack.Undo();
        Assert.Single(doc.Artboards);
        Assert.Empty(doc.Orphans.Children);
        Assert.Single(artboard.Layers[0].Children);
    }

    [Fact]
    public void DeleteArtboardWithoutKeepingChildrenRemovesThem()
    {
        CadDocument doc = CadDocument.CreateDefault();
        Artboard artboard = doc.Artboards[0];
        artboard.Layers[0].AddItem(PathFactory.CreateRectangle("r", new Rect2D(10, 10, 20, 20)));

        var stack = new CommandStack();
        stack.Execute(new DeleteArtboardCommand(doc, artboard, keepChildren: false));

        Assert.Empty(doc.Artboards);
        Assert.Empty(doc.Orphans.Children);

        stack.Undo();
        Assert.Single(doc.Artboards);
        Assert.Single(doc.Artboards[0].Layers[0].Children);
    }

    [Fact]
    public void CompositeCommandUndoesInReverseOrderAsOneStep()
    {
        CadDocument doc = CadDocument.CreateDefault();
        Layer layer = doc.Artboards[0].Layers[0];

        PathItem a = PathFactory.CreateRectangle("a", new Rect2D(0, 0, 10, 10));
        PathItem b = PathFactory.CreateRectangle("b", new Rect2D(0, 0, 10, 10));
        PathItem c = PathFactory.CreateRectangle("c", new Rect2D(0, 0, 10, 10));
        layer.AddItem(a);
        layer.AddItem(b);
        layer.AddItem(c);

        var stack = new CommandStack();
        stack.Execute(new CompositeCommand(
            "Delete three",
            new[] { new RemoveItemCommand(a), new RemoveItemCommand(b), new RemoveItemCommand(c) }));

        Assert.Empty(layer.Children);
        Assert.True(stack.CanUndo);

        stack.Undo();
        Assert.Equal(3, layer.Children.Count);
        Assert.Equal(new[] { a, b, c }, layer.Children);
        Assert.False(stack.CanUndo); // one composite step, not three
    }
}
