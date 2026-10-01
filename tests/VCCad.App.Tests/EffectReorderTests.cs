using System.Text.Json;
using VCCad.App.Automation;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Core.Raster;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// Reordering an effect within a stroke's list.
///
/// The issue is explicit about why this is not a tidy-up: "effects compose in order, and roughen before a drop
/// shadow is not the same picture as a drop shadow before roughen". The model keeps the list in application order,
/// so the assertions are on the **order** - which *is* the rendered result, because the outline builder walks the
/// list from the start.
/// </summary>
public class EffectReorderTests
{
    private static JsonElement Params(object value) => JsonSerializer.SerializeToElement(value);

    private static (AutomationContext Context, PathItem Path) Host()
    {
        var vm = new EditorViewModel();
        var path = new PathItem { Name = "line", Fill = FillSpec.None };
        SubPath sub = path.AddSubPath(closed: false);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(50, 0)));
        path.Strokes.Clear();
        path.Strokes.Add(new StrokeSpec(true, ColorRgb.Black, 4, StrokeCap.Butt, StrokeJoin.Miter, 4));

        vm.Document.Artboards[0].Layers[0].AddItem(path);
        vm.SelectObject(path);
        return (new AutomationContext { ViewModel = vm }, path);
    }

    private static void Add(AutomationContext context, string kind)
        => EditorOperations.Invoke(context, "style.addStrokeEffect", Params(new { kind }));

    [Fact]
    public void ReorderingChangesTheAppliedOrder()
    {
        (AutomationContext context, PathItem path) = Host();
        Add(context, "offsetPath");
        Add(context, "roughen");

        Assert.Equal(new[] { OutlineEffectKind.OffsetPath, OutlineEffectKind.Roughen },
            path.Strokes[0].AllEffects.Select(e => e.Kind).ToArray());

        EditorOperations.Invoke(context, "style.reorderStrokeEffect", Params(new { from = 0, to = 1 }));

        Assert.Equal(new[] { OutlineEffectKind.Roughen, OutlineEffectKind.OffsetPath },
            path.Strokes[0].AllEffects.Select(e => e.Kind).ToArray());
    }

    /// <summary>
    /// **The two orders render differently.** Not asserted from a picture but from the outline the renderer
    /// consumes, which is the thing the order actually changes - and the effects chosen are ones whose result
    /// cannot coincide, because a roughen moves points and an offset moves edges.
    /// </summary>
    [Fact]
    public void TheTwoOrdersProduceDifferentGeometry()
    {
        (AutomationContext context, PathItem path) = Host();
        Add(context, "roughen");
        Add(context, "offsetPath");

        string first = Describe(StrokeOutlineBuilder.Plan(path, path.Strokes[0]));

        EditorOperations.Invoke(context, "style.reorderStrokeEffect", Params(new { from = 0, to = 1 }));

        string second = Describe(StrokeOutlineBuilder.Plan(path, path.Strokes[0]));

        Assert.NotEqual(first, second);
    }

    /// <summary>The plan's outlines as text, which is enough to tell two geometries apart.</summary>
    private static string Describe(StrokeRenderPlan plan)
        => string.Join(";", plan.Outlines.Select(
            loop => string.Join(",", loop.Select(p => $"{p.X:0.####},{p.Y:0.####}"))));

    [Fact]
    public void RasterEffectsReorderTheSameWay()
    {
        (AutomationContext context, PathItem path) = Host();
        EditorOperations.Invoke(context, "style.addRasterEffect", Params(new { kind = "blur" }));
        EditorOperations.Invoke(context, "style.addRasterEffect", Params(new { kind = "outerGlow" }));

        Assert.Equal(new[] { RasterEffectKind.Blur, RasterEffectKind.OuterGlow },
            Enumerable.Range(0, path.Strokes[0].RasterEffects!.Count)
                .Select(i => path.Strokes[0].RasterEffects![i].Kind).ToArray());

        EditorOperations.Invoke(context, "style.reorderRasterEffect", Params(new { from = 1, to = 0 }));

        Assert.Equal(new[] { RasterEffectKind.OuterGlow, RasterEffectKind.Blur },
            Enumerable.Range(0, path.Strokes[0].RasterEffects!.Count)
                .Select(i => path.Strokes[0].RasterEffects![i].Kind).ToArray());
    }

    /// <summary>One gesture is one undo step, which is the rule for every edit in this repository.</summary>
    [Fact]
    public void ReorderingIsOneUndoStep()
    {
        (AutomationContext context, PathItem path) = Host();
        Add(context, "offsetPath");
        Add(context, "roughen");

        EditorOperations.Invoke(context, "style.reorderStrokeEffect", Params(new { from = 0, to = 1 }));
        Assert.Equal(OutlineEffectKind.Roughen, path.Strokes[0].AllEffects.First().Kind);

        context.Session.Undo();

        Assert.Equal(OutlineEffectKind.OffsetPath, path.Strokes[0].AllEffects.First().Kind);
    }

    /// <summary>An index that is not there changes nothing rather than throwing on a stale list.</summary>
    [Fact]
    public void AnOutOfRangeIndexChangesNothing()
    {
        (AutomationContext context, PathItem path) = Host();
        Add(context, "roughen");

        JsonElement result = JsonSerializer.SerializeToElement(
            EditorOperations.Invoke(context, "style.reorderStrokeEffect", Params(new { from = 5, to = 0 })));

        Assert.Equal(0, result.GetProperty("changed").GetInt32());
        Assert.Single(path.Strokes[0].AllEffects);
    }
}
