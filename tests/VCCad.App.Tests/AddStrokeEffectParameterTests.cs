using System.Text.Json;
using VCCad.App.Automation;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// `style.addStrokeEffect` takes the parameters the kind declares, in the same call that adds the effect.
///
/// The operation hand-built the effect record from `kind`, `size`, `detail` and `seed`, so every parameter the
/// registry gained afterwards was silently ignored: a driver had to add the effect and then set each value with a
/// second call, while the panel could do it in one. A person could do something a driver could not, which this
/// repository treats as a defect.
///
/// These assert the **model** - the effect's own members - and they are written against the declaration, so a
/// parameter added to the registry without being reachable here fails rather than becoming a request field that
/// quietly does nothing.
/// </summary>
public class AddStrokeEffectParameterTests
{
    private static JsonElement Params(object value) => JsonSerializer.SerializeToElement(value);

    private static (AutomationContext Context, PathItem Path) Host()
    {
        var vm = new EditorViewModel();
        var path = new PathItem { Name = "line", Fill = FillSpec.None };
        SubPath sub = path.AddSubPath(closed: false);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(60, 0)));
        path.Strokes.Clear();
        path.Strokes.Add(new StrokeSpec(true, ColorRgb.Black, 6, StrokeCap.Butt, StrokeJoin.Miter, 4));

        vm.Document.Artboards[0].Layers[0].AddItem(path);
        vm.SelectObject(path);
        return (new AutomationContext { ViewModel = vm }, path);
    }

    private static OutlineEffectSpec Added(PathItem path) => path.Strokes[0].AllEffects.Single();

    /// <summary>**A scribble can be added with its settings in one call.**</summary>
    [Fact]
    public void AScribbleTakesItsParametersInTheSameCall()
    {
        (AutomationContext context, PathItem path) = Host();

        EditorOperations.Invoke(context, "style.addStrokeEffect", Params(new
        {
            kind = "scribble",
            size = 3.0,
            density = 4.0,
            overlap = 0.5,
            width = 2.0,
            curviness = 1.5,
            scatter = 7.0,
        }));

        OutlineEffectSpec effect = Added(path);
        Assert.Equal(3.0, effect.Size, 9);
        Assert.Equal(4.0, effect.Density, 9);
        Assert.Equal(0.5, effect.Overlap, 9);
        Assert.Equal(2.0, effect.Width, 9);
        Assert.Equal(1.5, effect.Curviness, 9);
        Assert.Equal(7.0, effect.Scatter, 9);
    }

    /// <summary>And a zig-zag its ridges and smoothing, and an offset path its join.</summary>
    [Fact]
    public void TheOtherKindsTakeTheirOwnParametersToo()
    {
        (AutomationContext context, PathItem path) = Host();

        EditorOperations.Invoke(context, "style.addStrokeEffect", Params(new
        {
            kind = "zigZag",
            size = 4.0,
            ridges = 5.0,
            smooth = 1.0,
        }));

        OutlineEffectSpec zigZag = Added(path);
        Assert.Equal(5, zigZag.Ridges);
        Assert.True(zigZag.Smooth);

        EditorOperations.Invoke(context, "style.addStrokeEffect", Params(new
        {
            kind = "offsetPath",
            size = 5.0,
            join = 2.0,
        }));

        Assert.Equal(OutlineJoin.Round, path.Strokes[0].AllEffects.Last().Join);
    }

    /// <summary>A parameter the request does not name stays at its default, rather than becoming zero.</summary>
    [Fact]
    public void AnOmittedParameterKeepsItsDefault()
    {
        (AutomationContext context, PathItem path) = Host();

        EditorOperations.Invoke(context, "style.addStrokeEffect", Params(new { kind = "scribble", size = 2.0 }));

        OutlineEffectSpec effect = Added(path);
        Assert.Equal(1.0, effect.Density, 9);
        Assert.Equal(0.0, effect.Scatter, 9);
        Assert.Equal(1, effect.Ridges);
        Assert.False(effect.Smooth);
    }

    /// <summary>
    /// **Every parameter the registry declares is reachable through this operation.** Written against the
    /// declaration, so declaring one and not reading it here is a failure rather than a field that does nothing.
    /// </summary>
    [Fact]
    public void EveryDeclaredParameterIsTaken()
    {
        foreach (EffectDefinition definition in EffectRegistry.All.Where(d => !d.Raster))
        {
            foreach (EffectParameter parameter in definition.Parameters)
            {
                if (parameter.Name is "size" or "detail" or "seed")
                {
                    continue;
                }

                (AutomationContext context, PathItem path) = Host();

                // A value inside the declared range and different from the default - which is what a panel's control
                // would offer, and the only way to tell "read it" from "left the default in place".
                double candidate = Math.Clamp(parameter.Default + 2, parameter.Minimum, parameter.Maximum);
                if (Math.Abs(candidate - parameter.Default) < 1e-9)
                {
                    candidate = Math.Clamp(parameter.Default - 2, parameter.Minimum, parameter.Maximum);
                }

                var request = new Dictionary<string, object>
                {
                    ["kind"] = definition.Kind,
                    ["size"] = 3.0,
                    [parameter.Name] = candidate,
                };

                EditorOperations.Invoke(context, "style.addStrokeEffect", Params(request));

                OutlineEffectSpec effect = Added(path);
                double? read = parameter.Name switch
                {
                    "ridges" => effect.Ridges,
                    "smooth" => effect.Smooth ? 1 : 0,
                    "join" => (int)effect.Join,
                    "density" => effect.Density,
                    "overlap" => effect.Overlap,
                    "width" => effect.Width,
                    "curviness" => effect.Curviness,
                    "scatter" => effect.Scatter,
                    _ => null,
                };

                Assert.True(read is not null,
                    $"'{definition.Kind}' declares '{parameter.Name}' but this operation cannot set it");
            }
        }
    }
}
