using System.Text.Json;
using VCCad.App.Automation;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Core.Serialization;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// Raster stroke effects: blur, drop shadow, inner glow and outer glow.
///
/// **These are not the outline effects and the distinction is the point.** A roughen moves a point and can be
/// exported as vector geometry; a blur has no points to move, does not scale with zoom, and needs a pixel buffer.
/// They are modelled and operated separately for that reason, and what this file pins is the model and the API -
/// the drawing comes later, which is why the issue stays open.
/// </summary>
public class RasterEffectOperationTests
{
    private static JsonElement Params(object value) => JsonSerializer.SerializeToElement(value);

    private static (AutomationContext Context, CadDocument Document, PathItem Path) Host()
    {
        var vm = new EditorViewModel();
        var path = new PathItem { Name = "line", Fill = FillSpec.None };
        SubPath sub = path.AddSubPath(closed: false);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(60, 0)));
        path.Stroke = new StrokeSpec(true, ColorRgb.Black, 8, StrokeCap.Butt, StrokeJoin.Miter, 4);
        vm.Document.Artboards[0].Layers[0].AddItem(path);
        vm.SelectObject(path);

        return (new AutomationContext { ViewModel = vm }, vm.Document, path);
    }

    [Fact]
    public void ABLurCanBeAddedAndReadBack()
    {
        (AutomationContext context, _, PathItem path) = Host();

        EditorOperations.Invoke(context, "style.addRasterEffect", Params(new { kind = "blur", radius = 6.5 }));

        RasterEffectSpec effect = Assert.Single(path.Stroke.AllRasterEffects);
        Assert.Equal(RasterEffectKind.Blur, effect.Kind);
        Assert.Equal(6.5, effect.Radius, 6);

        string json = JsonSerializer.Serialize(EditorOperations.Invoke(context, "style.strokes", default));
        Assert.Contains("\"kind\":\"Blur\"", json, StringComparison.Ordinal);
        Assert.Contains("\"radius\":6.5", json, StringComparison.Ordinal);
    }

    /// <summary>A drop shadow keeps its offset, and they are all one effect rather than four.</summary>
    [Fact]
    public void ADropShadowKeepsItsOffset()
    {
        (AutomationContext context, _, PathItem path) = Host();

        EditorOperations.Invoke(context, "style.addRasterEffect",
            Params(new { kind = "dropShadow", radius = 3, offsetX = 4, offsetY = -2, opacity = 0.5 }));

        RasterEffectSpec effect = Assert.Single(path.Stroke.AllRasterEffects);
        Assert.Equal(RasterEffectKind.DropShadow, effect.Kind);
        Assert.Equal(4.0, effect.OffsetX, 6);
        Assert.Equal(-2.0, effect.OffsetY, 6);
        Assert.Equal(0.5, effect.Opacity, 6);
    }

    /// <summary>
    /// **An omitted tint stays absent rather than becoming black.** "A glow the colour of the line it comes from"
    /// is the common case, and a colour parser that reported its fallback for an absent parameter would turn that
    /// into an explicit black - a silent choice nobody made.
    /// </summary>
    [Fact]
    public void AnOmittedTintIsNotAColour()
    {
        (AutomationContext context, _, PathItem path) = Host();

        EditorOperations.Invoke(context, "style.addRasterEffect", Params(new { kind = "outerGlow", radius = 5 }));

        Assert.Null(path.Stroke.AllRasterEffects[0].Tint);
    }

    [Fact]
    public void ATintIsKeptWhenGiven()
    {
        (AutomationContext context, _, PathItem path) = Host();

        EditorOperations.Invoke(context, "style.addRasterEffect",
            Params(new { kind = "outerGlow", radius = 5, tint = new[] { 255, 0, 0 } }));

        ColorRgb tint = path.Stroke.AllRasterEffects[0].Tint!.Value;
        Assert.Equal(1.0, tint.R, 3);
        Assert.Equal(0.0, tint.G, 3);
    }

    [Fact]
    public void AnUnknownRasterEffectIsRefused()
    {
        (AutomationContext context, _, PathItem path) = Host();

        EditorOperationException error = Assert.Throws<EditorOperationException>(
            () => EditorOperations.Invoke(context, "style.addRasterEffect", Params(new { kind = "sharpen" })));

        Assert.Contains("not a raster effect", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(path.Stroke.HasRasterEffects);
    }

    [Fact]
    public void RasterEffectsCanBeCleared()
    {
        (AutomationContext context, _, PathItem path) = Host();
        EditorOperations.Invoke(context, "style.addRasterEffect", Params(new { kind = "blur" }));

        EditorOperations.Invoke(context, "style.clearRasterEffects", default);

        Assert.False(path.Stroke.HasRasterEffects);
        Assert.Null(path.Stroke.RasterEffects);
    }

    [Fact]
    public void AddingARasterEffectIsOneUndoStep()
    {
        (AutomationContext context, _, PathItem path) = Host();

        EditorOperations.Invoke(context, "style.addRasterEffect", Params(new { kind = "innerGlow", radius = 2 }));
        context.ViewModel.ActiveSession.Undo();

        Assert.False(path.Stroke.HasRasterEffects);
    }

    /// <summary>
    /// **The outline effects and the raster effects are separate lists**, and adding one does not disturb the
    /// other - which is what lets the renderers treat them differently without either having to unpick the other.
    /// </summary>
    [Fact]
    public void OutlineAndRasterEffectsAreIndependent()
    {
        (AutomationContext context, _, PathItem path) = Host();

        EditorOperations.Invoke(context, "style.addStrokeEffect", Params(new { kind = "roughen", size = 3 }));
        EditorOperations.Invoke(context, "style.addRasterEffect", Params(new { kind = "blur", radius = 2 }));

        Assert.Single(path.Stroke.AllEffects);
        Assert.Single(path.Stroke.AllRasterEffects);

        EditorOperations.Invoke(context, "style.clearStrokeEffects", default);

        Assert.False(path.Stroke.HasEffects);
        Assert.True(path.Stroke.HasRasterEffects);
    }

    [Fact]
    public void RasterEffectsSurviveSaveAndReload()
    {
        (AutomationContext context, CadDocument document, PathItem path) = Host();
        EditorOperations.Invoke(context, "style.addRasterEffect",
            Params(new { kind = "dropShadow", radius = 2.5, offsetX = 3, offsetY = 4, opacity = 0.75 }));
        EditorOperations.Invoke(context, "style.addRasterEffect",
            Params(new { kind = "outerGlow", radius = 8, tint = new[] { 0, 0, 255 } }));

        CadDocument reloaded = VccadDocumentSerializer.Deserialize(
            VccadDocumentSerializer.SerializeToBytes(document));
        StrokeSpec back = reloaded.Artboards[0].Layers[0].Children.OfType<PathItem>().Single().Stroke;

        Assert.Equal(2, back.AllRasterEffects.Count);
        Assert.Equal(RasterEffectKind.DropShadow, back.AllRasterEffects[0].Kind);
        Assert.Equal(2.5, back.AllRasterEffects[0].Radius, 6);
        Assert.Equal(3.0, back.AllRasterEffects[0].OffsetX, 6);
        Assert.Equal(4.0, back.AllRasterEffects[0].OffsetY, 6);
        Assert.Equal(0.75, back.AllRasterEffects[0].Opacity, 6);
        Assert.Null(back.AllRasterEffects[0].Tint);

        Assert.Equal(RasterEffectKind.OuterGlow, back.AllRasterEffects[1].Kind);
        Assert.Equal(1.0, back.AllRasterEffects[1].Tint!.Value.B, 3);

        // Equal as a value, which catches a stack comparing by reference.
        Assert.Equal(path.Stroke, back);
    }

    [Fact]
    public void AStrokeWithoutRasterEffectsWritesNoMember()
    {
        (AutomationContext context, CadDocument document, _) = Host();

        string json = System.Text.Encoding.UTF8.GetString(VccadDocumentSerializer.SerializeToBytes(document));

        Assert.DoesNotContain("RasterEffects", json);
    }
}
