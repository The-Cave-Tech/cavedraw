using System.Text.Json;
using Avalonia.Headless.XUnit;
using VCCad.App.Automation;
using VCCad.App.Views.Panes;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;
using GradientStop = VCCad.Core.Model.GradientStop;

namespace VCCad.App.Tests;

/// <summary>
/// The fill rule from the Colour pane.
///
/// The chooser was removed when the pane was tidied, which left the rule reachable only from the
/// registry - a parity gap in the other direction. These pin that the pane sets it, that setting
/// it is about the rule and nothing else, and that one undo puts it back.
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

    private static (EditorViewModel Vm, PathItem Path) Selected(PathItem path)
    {
        var vm = new EditorViewModel();
        vm.Document.Artboards[0].Layers[0].AddItem(path);
        vm.SelectObject(path);
        return (vm, path);
    }

    [AvaloniaFact]
    public void TheRuleChooserSetsTheRuleAndLeavesTheColourAlone()
    {
        PathItem path = Box();
        (EditorViewModel vm, PathItem item) = Selected(path);

        var pane = new ColorsPane();
        pane.Attach(vm);
        try
        {
            Assert.Equal(0, pane.FillRuleBox.SelectedIndex);

            pane.FillRuleBox.SelectedIndex = 1; // Even-odd

            Assert.Equal(FillRule.EvenOdd, item.Fill.Rule);
            Assert.Equal(ColorRgb.Red, item.Fill.Color);
            Assert.True(item.Fill.IsVisible);

            vm.Undo();
            Assert.Equal(FillRule.NonZero, item.Fill.Rule);
        }
        finally
        {
            pane.Detach();
        }
    }

    /// <summary>A rule says what the outline means, not what colour it is: a gradient survives.</summary>
    [AvaloniaFact]
    public void TheRuleChooserLeavesAGradientIntact()
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

        (EditorViewModel vm, PathItem item) = Selected(path);

        var pane = new ColorsPane();
        pane.Attach(vm);
        try
        {
            pane.FillRuleBox.SelectedIndex = 1;

            Assert.Equal(FillRule.EvenOdd, item.Fill.Rule);
            Assert.True(item.Fill.HasGradient, "the gradient must survive a rule change");
            Assert.Equal(2, item.Fill.Gradient!.Stops.Count);
        }
        finally
        {
            pane.Detach();
        }
    }

    [AvaloniaFact]
    public void TheChooserShowsTheSelectedObjectsRule()
    {
        PathItem path = Box();
        path.Fill = FillSpec.Solid(ColorRgb.Red, FillRule.EvenOdd);
        (EditorViewModel vm, _) = Selected(path);

        var pane = new ColorsPane();
        pane.Attach(vm);
        try
        {
            Assert.Equal(1, pane.FillRuleBox.SelectedIndex);
        }
        finally
        {
            pane.Detach();
        }
    }

    /// <summary>
    /// The registry half of the parity rule: the rule is settable without a colour, so a driver
    /// can change it without flattening a gradient the way style.setFill would.
    /// </summary>
    [AvaloniaFact]
    public void TheRuleIsReachableFromTheRegistryWithoutTouchingTheColour()
    {
        PathItem path = Box();
        (EditorViewModel vm, PathItem item) = Selected(path);
        var context = new AutomationContext { ViewModel = vm };

        EditorOperations.Invoke(context, "style.setFillRule",
            JsonSerializer.SerializeToElement(new { rule = "evenodd" }));

        Assert.Equal(FillRule.EvenOdd, item.Fill.Rule);
        Assert.Equal(ColorRgb.Red, item.Fill.Color);
        Assert.True(item.Fill.IsVisible);
    }
}
