using System.Text.Json;
using VCCad.App.Automation;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// The operations accept exactly what the registry declares.
///
/// This is the acceptance test #115 names - "the panel's editors matching the parameters the operation accepts,
/// asserted by **comparing the two lists** rather than by eyeballing". Now that the declaration exists and the
/// operations read it, the comparison is mechanical: whatever the registry says is an effect, the operation takes;
/// whatever it does not, the operation refuses, and it refuses a raster kind asked of the outline operation too.
///
/// It is written against the registry rather than against a hand-copied list of names on purpose: a test that
/// repeated the names would pass forever while the two drifted.
/// </summary>
public class EffectOperationParityTests
{
    private static JsonElement Params(object value) => JsonSerializer.SerializeToElement(value);

    private static AutomationContext Host(out PathItem path)
    {
        var vm = new EditorViewModel();
        path = new PathItem { Name = "line", Fill = FillSpec.None };
        SubPath sub = path.AddSubPath(closed: false);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(50, 0)));
        path.Strokes.Clear();
        path.Strokes.Add(new StrokeSpec(true, ColorRgb.Black, 4, StrokeCap.Butt, StrokeJoin.Miter, 4));

        vm.Document.Artboards[0].Layers[0].AddItem(path);
        vm.SelectObject(path);
        return new AutomationContext { ViewModel = vm };
    }

    /// <summary>Every outline effect the registry declares is one the outline operation takes.</summary>
    [Fact]
    public void TheOutlineOperationTakesEveryDeclaredOutlineEffect()
    {
        foreach (EffectDefinition definition in EffectRegistry.All.Where(d => !d.Raster))
        {
            AutomationContext context = Host(out PathItem path);
            int before = path.Strokes[0].AllEffects.Count;

            EditorOperations.Invoke(context, "style.addStrokeEffect", Params(new { kind = definition.Kind }));

            Assert.True(path.Strokes[0].AllEffects.Count > before,
                $"the registry declares '{definition.Kind}' but adding it added nothing");
        }
    }

    /// <summary>And every raster effect is one the raster operation takes.</summary>
    [Fact]
    public void TheRasterOperationTakesEveryDeclaredRasterEffect()
    {
        foreach (EffectDefinition definition in EffectRegistry.All.Where(d => d.Raster))
        {
            AutomationContext context = Host(out PathItem path);

            EditorOperations.Invoke(context, "style.addRasterEffect", Params(new { kind = definition.Kind }));

            Assert.NotEmpty(path.Strokes[0].RasterEffects);
        }
    }

    /// <summary>**The aliases the registry declares are accepted**, which is why they live there rather than in a switch.</summary>
    [Theory]
    [InlineData("gaussianBlur")]
    [InlineData("drop_shadow")]
    [InlineData("shadow")]
    [InlineData("zig_zag")]
    [InlineData("offset_path")]
    [InlineData("outer_glow")]
    public void TheDeclaredAliasesAreAccepted(string alias)
    {
        AutomationContext context = Host(out PathItem path);
        EffectDefinition definition = EffectRegistry.Find(alias)!;

        string operation = definition.Raster ? "style.addRasterEffect" : "style.addStrokeEffect";
        EditorOperations.Invoke(context, operation, Params(new { kind = alias }));

        Assert.True(definition.Raster
            ? path.Strokes[0].RasterEffects.Count > 0
            : path.Strokes[0].AllEffects.Count > 0);
    }

    /// <summary>An effect nothing declares is refused, and the message names what this build does have.</summary>
    [Fact]
    public void AnUndeclaredEffectIsRefused()
    {
        AutomationContext context = Host(out _);

        EditorOperationException error = Assert.Throws<EditorOperationException>(
            () => EditorOperations.Invoke(context, "style.addStrokeEffect", Params(new { kind = "sparkle" })));

        // Named from the registry, so the message cannot list an effect that no longer exists.
        foreach (EffectDefinition definition in EffectRegistry.All.Where(d => !d.Raster))
        {
            Assert.Contains(definition.Kind, error.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>**A raster kind asked of the outline operation is refused.** The two families are not interchangeable,
    /// and before the registry existed each operation only knew its own names.</summary>
    [Fact]
    public void ARasterKindIsRefusedByTheOutlineOperation()
    {
        AutomationContext context = Host(out PathItem path);

        Assert.Throws<EditorOperationException>(
            () => EditorOperations.Invoke(context, "style.addStrokeEffect", Params(new { kind = "blur" })));

        Assert.Empty(path.Strokes[0].AllEffects);
    }

    /// <summary>And the other way round.</summary>
    [Fact]
    public void AnOutlineKindIsRefusedByTheRasterOperation()
    {
        AutomationContext context = Host(out PathItem path);

        Assert.Throws<EditorOperationException>(
            () => EditorOperations.Invoke(context, "style.addRasterEffect", Params(new { kind = "roughen" })));

        // Null rather than empty when a stroke carries none, which is why this is a count and not Assert.Empty.
        Assert.True(path.Strokes[0].RasterEffects is null || path.Strokes[0].RasterEffects.Count == 0);
    }
}
