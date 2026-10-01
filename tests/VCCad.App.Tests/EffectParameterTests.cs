using System.Text.Json;
using VCCad.App.Automation;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// Setting an effect's parameters **by the names the registry declares**.
///
/// This is what lets a panel build its editors from the declaration instead of a switch: it asks the registry what
/// an effect takes, shows a control per parameter, and sets it by name. These tests are written against the
/// registry too - every numeric parameter of every declared kind is set through the operation and has to change the
/// effect - so a parameter added to a declaration without a setter is caught here rather than by a control that
/// does nothing.
/// </summary>
public class EffectParameterTests
{
    private static JsonElement Params(object value) => JsonSerializer.SerializeToElement(value);

    private static PathItem Path(params OutlineEffectSpec[] effects)
    {
        var path = new PathItem { Name = "line", Fill = FillSpec.None };
        SubPath sub = path.AddSubPath(closed: false);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(50, 0)));
        path.Strokes.Clear();
        path.Strokes.Add(new StrokeSpec(true, ColorRgb.Black, 4, StrokeCap.Butt, StrokeJoin.Miter, 4)
        {
            Effects = new EffectStack(effects),
        });
        return path;
    }

    private static AutomationContext Host(PathItem path)
    {
        var viewModel = new EditorViewModel();
        viewModel.Document.Artboards[0].Layers[0].AddItem(path);
        viewModel.SelectObject(path);
        return new AutomationContext { ViewModel = viewModel };
    }

    /// <summary>**Every numeric parameter of every declared kind can be set by name.**</summary>
    [Fact]
    public void EveryDeclaredNumericParameterIsSettable()
    {
        foreach (EffectDefinition definition in EffectRegistry.All)
        {
            foreach (EffectParameter parameter in definition.Parameters
                         .Where(p => p.Kind is EffectParameterKind.Number or EffectParameterKind.Integer))
            {
                PathItem path = definition.Raster
                    ? RasterPath(definition.RasterKind!.Value)
                    : Path(OutlineEffectSpec.Roughen(2, seed: 3));

                // Start from the kind under test, so the parameter belongs to the effect being set.
                if (!definition.Raster)
                {
                    path.Strokes[0] = path.Strokes[0] with
                    {
                        Effects = new EffectStack(new[] { new OutlineEffectSpec(definition.OutlineKind!.Value) }),
                    };
                }

                object before = definition.Raster
                    ? path.Strokes[0].AllRasterEffects![0]
                    : (object)path.Strokes[0].AllEffects[0];

                AutomationContext context = Host(path);

                // A value inside the range the registry declares and different from the default - which is what a
                // panel's control would offer. Using a fixed 12 found a real thing: `opacity` is clamped to 0..1, so
                // 12 clamps to the effect's own default and changes nothing.
                double candidate = Math.Clamp(parameter.Default + 5, parameter.Minimum, parameter.Maximum);
                if (Math.Abs(candidate - parameter.Default) < 1e-9)
                {
                    candidate = Math.Clamp(parameter.Default - 5, parameter.Minimum, parameter.Maximum);
                }

                EditorOperations.Invoke(context, "style.setEffectParameter", Params(new
                {
                    name = parameter.Name,
                    value = candidate,
                    index = 0,
                    raster = definition.Raster,
                }));

                object after = definition.Raster
                    ? path.Strokes[0].AllRasterEffects![0]
                    : (object)path.Strokes[0].AllEffects[0];

                Assert.True(!before.Equals(after),
                    $"'{definition.Kind}' declares parameter '{parameter.Name}', but setting it changed nothing");
            }
        }
    }

    private static PathItem RasterPath(RasterEffectKind kind)
    {
        PathItem path = Path();
        path.Strokes[0] = path.Strokes[0] with
        {
            RasterEffects = new RasterEffectStack(new[] { new RasterEffectSpec(kind) }),
        };
        return path;
    }

    /// <summary>A name the effect does not take is refused, and changes nothing.</summary>
    [Fact]
    public void AnUnknownParameterNameChangesNothing()
    {
        PathItem path = Path(OutlineEffectSpec.Roughen(2, seed: 3));
        AutomationContext context = Host(path);
        OutlineEffectSpec before = path.Strokes[0].AllEffects[0];

        JsonElement result = JsonSerializer.SerializeToElement(
            EditorOperations.Invoke(context, "style.setEffectParameter", Params(new
            {
                name = "sparkle",
                value = 3.0,
                index = 0,
            })));

        Assert.Equal(0, result.GetProperty("changed").GetInt32());
        Assert.Equal(before, path.Strokes[0].AllEffects[0]);
    }

    /// <summary>A parameter of the **other** family is refused too, since the lists are separate.</summary>
    [Fact]
    public void AParameterOfTheOtherFamilyChangesNothing()
    {
        PathItem path = Path(OutlineEffectSpec.Roughen(2, seed: 3));
        AutomationContext context = Host(path);

        // radius belongs to a raster effect; this is an outline one.
        EditorOperations.Invoke(context, "style.setEffectParameter", Params(new
        {
            name = "radius",
            value = 9.0,
            index = 0,
        }));

        Assert.Equal(2.0, path.Strokes[0].AllEffects[0].Size, 6);
    }

    /// <summary>Setting a value is one undo step, like every other edit here.</summary>
    [Fact]
    public void SettingIsOneUndoStep()
    {
        PathItem path = Path(OutlineEffectSpec.Roughen(2, seed: 3));
        AutomationContext context = Host(path);

        EditorOperations.Invoke(context, "style.setEffectParameter", Params(new
        {
            name = "size",
            value = 7.0,
            index = 0,
        }));

        Assert.Equal(7.0, path.Strokes[0].AllEffects[0].Size, 6);

        context.Session.Undo();

        Assert.Equal(2.0, path.Strokes[0].AllEffects[0].Size, 6);
    }
}
