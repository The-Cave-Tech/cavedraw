using System.Text.Json;
using Avalonia.Headless.XUnit;
using VCCad.App.Automation;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;
using GradientStop = VCCad.Core.Model.GradientStop;

namespace VCCad.App.Tests;

/// <summary>
/// The fill rule, through the operation that owns it.
///
/// This file used to test the rule **chooser in the Colour pane** as well, and the history is worth keeping:
/// the chooser was removed when the pane was tidied, then added back on the argument that a person needed it.
/// It has been removed again, this time as the product decision it is - a rule about how an outline is filled
/// is a property of the geometry, not of the colour, and a picker that also decides it answers two unrelated
/// questions at once. `ColorsPaneContentsTests` now asserts the picker holds no rule control, so this cannot
/// be re-litigated by accident.
///
/// The coverage those pane tests carried is not dropped: what they proved - that setting the rule leaves the
/// colour alone, leaves a gradient intact, and undoes in one step - is behaviour of the **operation**, and it
/// is asserted here against the operation instead. Only their subject, the control, is gone.
/// </summary>
public class FillRulePaneTests
{
    private static PathItem Box(string name = "box")
    {
        var path = new PathItem { Name = name, Fill = FillSpec.Solid(ColorRgb.Red) };
        SubPath sub = path.AddSubPath(closed: true);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(40, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(40, 40)));
        sub.Nodes.Add(new PathNode(new Point2D(0, 40)));
        return path;
    }

    private static (EditorViewModel Vm, PathItem Path, AutomationContext Context) Selected(PathItem path)
    {
        var vm = new EditorViewModel();
        vm.Document.Artboards[0].Layers[0].AddItem(path);
        vm.SelectObject(path);
        return (vm, path, new AutomationContext { ViewModel = vm });
    }

    private static void SetEvenOdd(AutomationContext context)
        => EditorOperations.Invoke(context, "style.setFillRule",
            JsonSerializer.SerializeToElement(new { rule = "evenodd" }));

    /// <summary>The rule is settable without a colour, so a driver does not flatten a gradient to set it.</summary>
    [AvaloniaFact]
    public void TheRuleIsReachableFromTheRegistryWithoutTouchingTheColour()
    {
        (_, PathItem item, AutomationContext context) = Selected(Box());

        SetEvenOdd(context);

        Assert.Equal(FillRule.EvenOdd, item.Fill.Rule);
        Assert.Equal(ColorRgb.Red, item.Fill.Color);
        Assert.True(item.Fill.IsVisible);
    }

    /// <summary>A rule says what the outline means, not what colour it is: a gradient survives it.</summary>
    [AvaloniaFact]
    public void SettingTheRuleLeavesAGradientIntact()
    {
        PathItem path = Box();
        path.Fill = FillSpec.WithGradient(new GradientSpec
        {
            Stops = new[]
            {
                new GradientStop(0.0, ColorRgb.Red),
                new GradientStop(1.0, ColorRgb.Blue),
            },
        });

        (_, PathItem item, AutomationContext context) = Selected(path);

        SetEvenOdd(context);

        Assert.Equal(FillRule.EvenOdd, item.Fill.Rule);
        Assert.True(item.Fill.HasGradient, "the gradient must survive a rule change");
        Assert.Equal(2, item.Fill.Gradient!.Stops.Count);
    }

    /// <summary>And it is one undo step, like every other edit.</summary>
    [AvaloniaFact]
    public void SettingTheRuleUndoesInOneStep()
    {
        (EditorViewModel vm, PathItem item, AutomationContext context) = Selected(Box());

        SetEvenOdd(context);
        Assert.Equal(FillRule.EvenOdd, item.Fill.Rule);

        vm.Undo();
        Assert.Equal(FillRule.NonZero, item.Fill.Rule);
    }
}
