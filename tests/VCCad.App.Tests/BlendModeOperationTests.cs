using System.Text.Json;
using VCCad.App.Automation;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// `object.setBlendMode` - the driver's half of the blend mode, which has to be the same operation the canvas
/// would call.
/// </summary>
public class BlendModeOperationTests
{
    private static JsonElement Params(object value) => JsonSerializer.SerializeToElement(value);

    private static (AutomationContext Context, PathItem Path) Host()
    {
        var vm = new EditorViewModel();
        var path = new PathItem { Name = "shape", Fill = FillSpec.Solid(ColorRgb.Black) };
        SubPath sub = path.AddSubPath(closed: true);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(10, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(10, 10)));
        path.Strokes.Clear();
        path.Strokes.Add(StrokeSpec.None);

        vm.Document.Artboards[0].Layers[0].AddItem(path);
        vm.SelectObject(path);
        return (new AutomationContext { ViewModel = vm }, path);
    }

    [Fact]
    public void ABlendModeCanBeSetAndCleared()
    {
        (AutomationContext context, PathItem path) = Host();

        EditorOperations.Invoke(context, "object.setBlendMode", Params(new { mode = "multiply" }));
        Assert.Equal(BlendMode.Multiply, path.BlendMode);

        EditorOperations.Invoke(context, "object.setBlendMode", Params(new { mode = "normal" }));
        Assert.Equal(BlendMode.Normal, path.BlendMode);
    }

    /// <summary>An unknown mode is refused rather than silently painted normally.</summary>
    [Fact]
    public void AnUnknownModeIsRefused()
    {
        (AutomationContext context, PathItem path) = Host();

        EditorOperationException error = Assert.Throws<EditorOperationException>(
            () => EditorOperations.Invoke(context, "object.setBlendMode", Params(new { mode = "sparkly" })));

        Assert.Contains("not a blend mode", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(BlendMode.Normal, path.BlendMode);
    }

    /// <summary>The hyphenated names are the ones a file uses, so they are the ones the operation takes.</summary>
    [Fact]
    public void TheHyphenatedNamesAreAccepted()
    {
        (AutomationContext context, PathItem path) = Host();

        EditorOperations.Invoke(context, "object.setBlendMode", Params(new { mode = "color-dodge" }));

        Assert.Equal(BlendMode.ColorDodge, path.BlendMode);
    }
}
