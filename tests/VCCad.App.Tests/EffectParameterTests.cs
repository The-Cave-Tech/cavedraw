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

                // And the same name reads back, because the panel shows a box per declared parameter and would
                // otherwise show the declaration's default rather than what the effect actually holds.
                Assert.NotNull(context.Session.EffectParameterValue(definition.Raster, 0, parameter.Name));
            }
        }
    }

    /// <summary>
    /// **Every parameter the outline effects gained reaches the geometry, not only the model.**
    ///
    /// A name the model stores and the effect ignores is invisible from the model: the value is set, read back,
    /// saved and exported while the outline never moves - exactly the defect the registry exists to prevent. So
    /// each one is set through the operation the panel calls, and the outline the effect draws is compared.
    /// </summary>
    [Theory]
    [InlineData("roughen", "detail", 4.0)]
    [InlineData("zigZag", "ridges", 3.0)]
    [InlineData("zigZag", "smooth", 1.0)]
    [InlineData("offsetPath", "join", 1.0)]
    [InlineData("offsetPath", "join", 2.0)]
    [InlineData("scribble", "density", 4.0)]
    [InlineData("scribble", "overlap", 0.5)]
    [InlineData("scribble", "width", 3.0)]
    [InlineData("scribble", "curviness", 3.0)]
    [InlineData("scribble", "scatter", 2.0)]
    public void EveryParameterTheOutlineEffectsGainedReachesTheGeometry(string kind, string name, double value)
    {
        PathItem path = Path(Base(kind));
        AutomationContext context = Host(path);

        IReadOnlyList<IReadOnlyList<Point2D>> before = Outline(path.Strokes[0].AllEffects[0]);

        EditorOperations.Invoke(context, "style.setEffectParameter", Params(new { name, value, index = 0 }));

        IReadOnlyList<IReadOnlyList<Point2D>> after = Outline(path.Strokes[0].AllEffects[0]);

        Assert.False(
            Same(before, after),
            $"'{kind}' accepts '{name}' but the outline it draws is unchanged");
    }

    /// <summary>A base spec of the kind, sized so the parameter under test has something to act on.</summary>
    private static OutlineEffectSpec Base(string kind) => kind switch
    {
        "roughen" => OutlineEffectSpec.Roughen(3, seed: 4),
        "zigZag" => OutlineEffectSpec.ZigZag(4, seed: 4),
        "offsetPath" => OutlineEffectSpec.OffsetPath(5),

        // A scribble of one pass draws the loop unchanged, so the wandering parameters need a second pass to
        // have anywhere to show up - which is what the effect itself says a scribble is.
        _ => OutlineEffectSpec.Scribble(3, passes: 2, seed: 4),
    };

    /// <summary>A 100x100 square, so every effect has segments and corners to act on.</summary>
    private static IReadOnlyList<Point2D> Square()
        => new[] { new Point2D(0, 0), new Point2D(100, 0), new Point2D(100, 100), new Point2D(0, 100) };

    private static IReadOnlyList<IReadOnlyList<Point2D>> Outline(OutlineEffectSpec effect)
        => OutlineEffects.Apply(new[] { Square() }, new[] { effect });

    /// <summary>Point-for-point equality, so "the outline changed" has to be about the points.</summary>
    private static bool Same(IReadOnlyList<IReadOnlyList<Point2D>> a, IReadOnlyList<IReadOnlyList<Point2D>> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Count; i++)
        {
            if (a[i].Count != b[i].Count)
            {
                return false;
            }

            for (int j = 0; j < a[i].Count; j++)
            {
                if (Math.Abs(a[i][j].X - b[i][j].X) > 1e-9 || Math.Abs(a[i][j].Y - b[i][j].Y) > 1e-9)
                {
                    return false;
                }
            }
        }

        return true;
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
