using System.Text.Json;
using VCCad.App.Automation;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// `style.setStroke` sets the colour it was given.
///
/// It parsed `color` into a variable and then called only the geometry half of the session's stroke API, which
/// keeps each path's existing colour. The parameter was accepted, validated and silently dropped - the worst
/// shape a bug can take, because a caller sees a well-formed reply and artwork that did not change.
///
/// Found by drawing: a garden path drawn freehand was restroked at 9 with a sandy colour and came out a thick
/// black swash, and the colour was blamed on the fill until `DrawFreehand` turned out to set `FillSpec.None`.
/// </summary>
public class StrokeOperationTests
{
    private static (AutomationContext Context, PathItem Path) Host(ColorRgb? start = null)
    {
        var vm = new EditorViewModel();
        var path = new PathItem { Name = "line", Fill = FillSpec.None };
        SubPath sub = path.AddSubPath(closed: false);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(60, 20)));
        path.Stroke = new StrokeSpec(true, start ?? ColorRgb.Black, 1, StrokeCap.Butt, StrokeJoin.Miter, 4);
        vm.Document.Artboards[0].Layers[0].AddItem(path);
        vm.SelectObject(path);

        return (new AutomationContext { ViewModel = vm }, path);
    }

    /// <summary>One selected path carrying a stack of two strokes, for the indexed edits below.</summary>
    private static (AutomationContext Context, PathItem Path) Stack()
    {
        var vm = new EditorViewModel();
        var path = new PathItem { Name = "line", Fill = FillSpec.None };
        SubPath sub = path.AddSubPath(closed: false);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(60, 20)));
        path.Strokes.Clear();
        path.Strokes.Add(new StrokeSpec(true, ColorRgb.Black, 4, StrokeCap.Butt, StrokeJoin.Miter, 4));
        path.Strokes.Add(new StrokeSpec(true, ColorRgb.Black, 8, StrokeCap.Butt, StrokeJoin.Miter, 4));
        vm.Document.Artboards[0].Layers[0].AddItem(path);
        vm.SelectObject(path);

        return (new AutomationContext { ViewModel = vm }, path);
    }

    private static JsonElement Params(object value) => JsonSerializer.SerializeToElement(value);

    [Fact]
    public void TheColourItIsGivenIsTheColourThatLands()
    {
        (AutomationContext context, PathItem path) = Host();

        EditorOperations.Invoke(
            context, "style.setStroke", Params(new { color = new[] { 190, 170, 136 }, width = 9 }));

        Assert.Equal(ColorRgb.FromBytes(190, 170, 136).R, path.Stroke.Color.R, 3);
        Assert.Equal(ColorRgb.FromBytes(190, 170, 136).G, path.Stroke.Color.G, 3);
        Assert.Equal(ColorRgb.FromBytes(190, 170, 136).B, path.Stroke.Color.B, 3);
        Assert.Equal(9.0, path.Stroke.Width, 3);
    }

    /// <summary>A colour and a width are one edit, so one undo puts both back.</summary>
    [Fact]
    public void ColourAndWidthAreOneUndoStep()
    {
        (AutomationContext context, PathItem path) = Host();

        EditorOperations.Invoke(
            context, "style.setStroke", Params(new { color = new[] { 10, 120, 40 }, width = 6 }));
        Assert.Equal(6.0, path.Stroke.Width, 3);

        context.ViewModel.ActiveSession.Undo();

        Assert.Equal(1.0, path.Stroke.Width, 3);
        Assert.Equal(ColorRgb.Black.R, path.Stroke.Color.R, 3);
    }

    /// <summary>With no colour given, each path keeps its own - which is what the geometry half is for.</summary>
    [Fact]
    public void WithNoColourEachPathKeepsItsOwn()
    {
        var start = ColorRgb.FromBytes(20, 90, 200);
        (AutomationContext context, PathItem path) = Host(start);

        EditorOperations.Invoke(context, "style.setStroke", Params(new { width = 3 }));

        Assert.Equal(3.0, path.Stroke.Width, 3);
        Assert.Equal(start.R, path.Stroke.Color.R, 3);
        Assert.Equal(start.B, path.Stroke.Color.B, 3);
    }

    /// <summary>
    /// **An omitted member is left alone when the stroke is named.** `index:1` with a cap and no width used to reset
    /// the width to this operation's default of 1, so a driver naming one member had to supply every one of them -
    /// and a width of 8 became 1 without anybody asking. The miter limit survived only because its default happened
    /// to be the value the stroke already had.
    /// </summary>
    [Fact]
    public void AnIndexOfOneWithoutAWidthLeavesTheWidthAsItWas()
    {
        (AutomationContext context, PathItem path) = Stack();

        EditorOperations.Invoke(context, "style.setStroke", Params(new { index = 1, cap = "round" }));

        Assert.Equal(StrokeCap.Round, path.Strokes[1].Cap);
        Assert.Equal(8.0, path.Strokes[1].Width, 6);

        // And the stroke nobody named is untouched, which is the other half of naming a member.
        Assert.Equal(StrokeCap.Butt, path.Strokes[0].Cap);
        Assert.Equal(4.0, path.Strokes[0].Width, 6);
    }

    /// <summary>
    /// **Without an index nothing changes.** The operation still restrokes the whole path and an omitted member still
    /// takes this operation's default, because there is no existing member to leave alone: the stack is replaced by
    /// the new stroke, which is what "set the stroke" means on a path that has one.
    /// </summary>
    [Fact]
    public void WithoutAnIndexTheDefaultsStillApplyToTheWholePath()
    {
        (AutomationContext context, PathItem path) = Stack();

        EditorOperations.Invoke(context, "style.setStroke", Params(new { cap = "round" }));

        Assert.Single(path.Strokes);
        Assert.Equal(StrokeCap.Round, path.Strokes[0].Cap);
        Assert.Equal(1.0, path.Strokes[0].Width, 6);
    }
}
