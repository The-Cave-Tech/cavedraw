using VCCad.Core.Commands;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

public class CommandStackTests
{
    [Fact]
    public void ExecuteUndoRedoRestoresLayerCount()
    {
        CadDocument doc = CadDocument.CreateDefault();
        Artboard artboard = doc.Artboards[0];
        var stack = new CommandStack();
        int baseline = artboard.Layers.Count;

        stack.Execute(new AddLayerCommand(artboard, "Scripted"));
        Assert.Equal(baseline + 1, artboard.Layers.Count);
        Assert.True(stack.CanUndo);
        Assert.Equal("Add layer", stack.UndoDescription);

        Assert.True(stack.Undo());
        Assert.Equal(baseline, artboard.Layers.Count);

        Assert.True(stack.Redo());
        Assert.Equal(baseline + 1, artboard.Layers.Count);
    }

    [Fact]
    public void ExecuteOnFreshCommandAppliesThenUndoRestoresIdentity()
    {
        CadDocument doc = CadDocument.CreateDefault();
        var stack = new CommandStack();

        stack.Execute(new AddArtboardCommand(doc, PageSizes.LetterPortrait, "Letter"));
        Artboard added = doc.Artboards[^1];
        Guid id = added.Id;

        Assert.Equal(2, doc.Artboards.Count);
        stack.Undo();
        Assert.Single(doc.Artboards);
        stack.Redo();
        Assert.Equal(id, doc.Artboards[^1].Id); // identity survives redo
    }

    [Fact]
    public void UndoRestoresZOrderThroughCapturedIndex()
    {
        Layer layer = CadDocument.CreateDefault().Artboards[0].Layers[0];
        var stack = new CommandStack();

        PathItem a = PathFactory.CreateRectangle("a", new Rect2D(0, 0, 10, 10));
        PathItem b = PathFactory.CreateRectangle("b", new Rect2D(0, 0, 10, 10));

        stack.Execute(new AddItemCommand(layer, a));
        stack.Execute(new AddItemCommand(layer, b));
        Assert.Equal(new[] { a, b }, layer.Children);

        stack.Undo();
        stack.Undo();
        Assert.Empty(layer.Children);

        stack.Redo();
        stack.Redo();
        Assert.Equal(new[] { a, b }, layer.Children); // stacking restored exactly
        Assert.Equal(layer, a.Container);
    }

    [Fact]
    public void SetFillCapturesPreviousValueAcrossCycles()
    {
        CadDocument doc = CadDocument.CreateDefault();
        Layer layer = doc.Artboards[0].Layers[0];
        PathItem rect = PathFactory.CreateRectangle("swatch", new Rect2D(0, 0, 10, 10));
        layer.AddItem(rect);
        Assert.False(rect.Fill.IsVisible);

        var stack = new CommandStack();
        stack.Execute(new SetFillCommand(rect, FillSpec.Solid(ColorRgb.Red), rect.Fill));
        Assert.Equal(ColorRgb.Red, rect.Fill.Color);

        stack.Undo();
        Assert.False(rect.Fill.IsVisible);

        stack.Redo();
        Assert.Equal(ColorRgb.Red, rect.Fill.Color);
    }

    [Fact]
    public void SetStrokeReplacesWidthAndJoin()
    {
        PathItem line = PathFactory.CreateLine("l", new Point2D(0, 0), new Point2D(10, 0));
        var stack = new CommandStack();
        var original = line.Stroke;

        var newStroke = new StrokeSpec(true, ColorRgb.Black, 4.0, StrokeCap.Round, StrokeJoin.Bevel, 2.0);
        stack.Execute(new SetStrokeCommand(line, newStroke, original));
        Assert.Equal(4.0, line.Stroke.Width, 12);
        Assert.Equal(StrokeJoin.Bevel, line.Stroke.Join);

        stack.Undo();
        Assert.Equal(original, line.Stroke);
    }

    [Fact]
    public void NewEditsDiscardRedoTail()
    {
        CadDocument doc = CadDocument.CreateDefault();
        Artboard artboard = doc.Artboards[0];
        var stack = new CommandStack();

        stack.Execute(new AddLayerCommand(artboard, "x"));
        stack.Undo();
        Assert.True(stack.CanRedo);

        stack.Execute(new AddLayerCommand(artboard, "y")); // invalidates redo
        Assert.False(stack.CanRedo);
        Assert.False(stack.Redo());
        Assert.Equal(2, artboard.Layers.Count);
    }

    [Fact]
    public void HistoryIsEvictedBeyondTheLimit()
    {
        CadDocument doc = CadDocument.CreateDefault();
        Artboard artboard = doc.Artboards[0];
        int baseline = artboard.Layers.Count;
        var stack = new CommandStack(limit: 2);

        for (int i = 0; i < 3; i++)
        {
            stack.Execute(new AddLayerCommand(artboard, $"L{i}"));
        }

        Assert.Equal(baseline + 3, artboard.Layers.Count);

        // Two undoable steps survive; the oldest was evicted.
        Assert.True(stack.Undo());
        Assert.True(stack.Undo());
        Assert.False(stack.CanUndo);
        Assert.Equal(baseline + 1, artboard.Layers.Count);
    }
}
