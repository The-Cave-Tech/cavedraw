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
}
