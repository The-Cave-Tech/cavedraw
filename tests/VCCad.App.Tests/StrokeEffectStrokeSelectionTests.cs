using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VCCad.App.Automation;
using VCCad.App.ViewModels;
using VCCad.App.Views.Panes;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// The effects section acting on **the stroke being inspected**, not on the top of the stack.
///
/// The stroke inspector already showed one stroke's geometry while these buttons edited another: the session's
/// effect methods were stack-wide or first-match, so a person could see "stroke 2 of 3" and add an effect to all
/// three, or remove the effect that row 0 names from the stroke that happened to carry it first. That is the same
/// class of lie as "the panel shows one stroke and edits another", one level down.
///
/// Two halves, because capability parity is the point: a `strokeIndex` on the session methods and the effect
/// operations, and a pane whose list, buttons and registry-driven editors all pass the shared index. Everything is
/// asserted on the **model** - a pane that shows one stroke while the session edits another is exactly the failure
/// a test asserting the list alone cannot see. A **middle** stroke is inspected throughout: the top one is what the
/// old code wrote, so inspecting it would pass against the defect.
/// </summary>
public class StrokeEffectStrokeSelectionTests
{
    private static JsonElement Params(object value) => JsonSerializer.SerializeToElement(value);

    private static StrokeSpec Stroke(double width = 4) => new(
        true, ColorRgb.Black, width, StrokeCap.Butt, StrokeJoin.Miter, 4);

    // ---------------------------------------------------------------- the operations

    private static (AutomationContext Context, PathItem Path) Host(params StrokeSpec[] strokes)
    {
        var vm = new EditorViewModel();
        PathItem path = Line(vm, strokes);
        vm.SelectObject(path);
        return (new AutomationContext { ViewModel = vm }, path);
    }

    private static PathItem Line(EditorViewModel vm, params StrokeSpec[] strokes)
    {
        var path = new PathItem { Name = "line", Fill = FillSpec.None };
        SubPath sub = path.AddSubPath(closed: false);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(60, 0)));
        path.Strokes.Clear();
        path.Strokes.AddRange(strokes);
        vm.Document.Artboards[0].Layers[0].AddItem(path);
        return path;
    }

    /// <summary>**An added effect lands on the named stroke and leaves its neighbours' lists untouched.**</summary>
    [Fact]
    public void AddingWithAStrokeIndexLandsOnThatStrokeOnly()
    {
        (AutomationContext context, PathItem path) = Host(Stroke(4), Stroke(8), Stroke(12));

        EditorOperations.Invoke(context, "style.addStrokeEffect",
            Params(new { kind = "roughen", size = 3, strokeIndex = 1 }));

        Assert.Equal(OutlineEffectKind.Roughen, Assert.Single(path.Strokes[1].AllEffects).Kind);
        Assert.Empty(path.Strokes[0].AllEffects);
        Assert.Empty(path.Strokes[2].AllEffects);
    }

    /// <summary>The raster list is the other half: naming a stroke must land there too, and only there.</summary>
    [Fact]
    public void AddingARasterEffectWithAStrokeIndexLandsOnThatStrokeOnly()
    {
        (AutomationContext context, PathItem path) = Host(Stroke(4), Stroke(8), Stroke(12));

        EditorOperations.Invoke(context, "style.addRasterEffect",
            Params(new { kind = "blur", radius = 5, strokeIndex = 1 }));

        Assert.Equal(RasterEffectKind.Blur, Assert.Single(path.Strokes[1].AllRasterEffects).Kind);
        Assert.Empty(path.Strokes[0].AllRasterEffects);
        Assert.Empty(path.Strokes[2].AllRasterEffects);
    }

    /// <summary>
    /// Naming no stroke is still the stack-wide request it always was, so a caller that already worked is
    /// unaffected by the index being added.
    /// </summary>
    [Fact]
    public void WithoutAStrokeIndexTheEffectStillReachesEveryStroke()
    {
        (AutomationContext context, PathItem path) = Host(Stroke(4), Stroke(8), Stroke(12));

        EditorOperations.Invoke(context, "style.addStrokeEffect", Params(new { kind = "roughen" }));

        Assert.All(path.Strokes, stroke => Assert.Single(stroke.AllEffects));
    }

    /// <summary>A parameter edit names the stroke as well as the effect, so it cannot land on a neighbour's list.</summary>
    [Fact]
    public void SettingAParameterWithAStrokeIndexChangesOnlyThatStroke()
    {
        (AutomationContext context, PathItem path) = Host(
            Stroke(4) with { Effects = new EffectStack(new[] { OutlineEffectSpec.Roughen(2, seed: 3) }) },
            Stroke(8) with { Effects = new EffectStack(new[] { OutlineEffectSpec.Roughen(2, seed: 3) }) },
            Stroke(12));

        EditorOperations.Invoke(context, "style.setEffectParameter",
            Params(new { name = "size", value = 9.0, index = 0, strokeIndex = 1 }));

        Assert.Equal(9.0, path.Strokes[1].AllEffects[0].Size, 6);
        Assert.Equal(2.0, path.Strokes[0].AllEffects[0].Size, 6);
    }

    /// <summary>Removal acts on the named stroke's list, not on the first stroke that has an effect at that index.</summary>
    [Fact]
    public void RemovingWithAStrokeIndexRemovesFromThatStrokeOnly()
    {
        (AutomationContext context, PathItem path) = Host(
            Stroke(4) with { Effects = new EffectStack(new[] { OutlineEffectSpec.Roughen(2, seed: 3) }) },
            Stroke(8) with { Effects = new EffectStack(new[] { OutlineEffectSpec.Roughen(2, seed: 3) }) },
            Stroke(12) with { Effects = new EffectStack(new[] { OutlineEffectSpec.Roughen(2, seed: 3) }) });

        EditorOperations.Invoke(context, "style.removeStrokeEffect",
            Params(new { index = 0, strokeIndex = 1 }));

        Assert.Empty(path.Strokes[1].AllEffects);
        Assert.Single(path.Strokes[0].AllEffects);
        Assert.Single(path.Strokes[2].AllEffects);
    }

    /// <summary>And a move reorders the named stroke's list, leaving a neighbour's order as it was.</summary>
    [Fact]
    public void ReorderingWithAStrokeIndexReordersThatStrokeOnly()
    {
        (AutomationContext context, PathItem path) = Host(
            Stroke(4) with
            {
                Effects = new EffectStack(new[] { OutlineEffectSpec.OffsetPath(4), OutlineEffectSpec.Roughen(2, seed: 3) }),
            },
            Stroke(8) with
            {
                Effects = new EffectStack(new[] { OutlineEffectSpec.OffsetPath(4), OutlineEffectSpec.Roughen(2, seed: 3) }),
            });

        EditorOperations.Invoke(context, "style.reorderStrokeEffect",
            Params(new { from = 0, to = 1, strokeIndex = 1 }));

        Assert.Equal(
            new[] { OutlineEffectKind.Roughen, OutlineEffectKind.OffsetPath },
            path.Strokes[1].AllEffects.Select(e => e.Kind).ToArray());
        Assert.Equal(
            new[] { OutlineEffectKind.OffsetPath, OutlineEffectKind.Roughen },
            path.Strokes[0].AllEffects.Select(e => e.Kind).ToArray());
    }

    /// <summary>A path whose stack is shorter than the index is a gap, as `StrokeSummary` says: it is skipped.</summary>
    [Fact]
    public void APathWithNoStrokeAtThatIndexIsSkipped()
    {
        (AutomationContext context, PathItem path) = Host(Stroke(4), Stroke(8), Stroke(12));
        PathItem shortStack = Line(context.ViewModel, Stroke(5));
        context.ViewModel.ToggleObjectSelection(shortStack);

        object? result = EditorOperations.Invoke(context, "style.addStrokeEffect",
            Params(new { kind = "roughen", strokeIndex = 1 }));

        Assert.Single(path.Strokes[1].AllEffects);
        Assert.Empty(shortStack.Strokes[0].AllEffects);
        Assert.Contains("\"changed\":1", JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    /// <summary>The named edit is still one command, so one undo puts the stroke back as it was.</summary>
    [Fact]
    public void AddingToTheNamedStrokeIsOneUndoStep()
    {
        (AutomationContext context, PathItem path) = Host(Stroke(4), Stroke(8), Stroke(12));

        EditorOperations.Invoke(context, "style.addStrokeEffect",
            Params(new { kind = "roughen", strokeIndex = 1 }));
        context.ViewModel.ActiveSession.Undo();

        Assert.Empty(path.Strokes[1].AllEffects);
    }

    // ---------------------------------------------------------------- the pane

    private static (StrokePane Pane, EditorViewModel ViewModel, PathItem Path) PaneHost(
        int inspected, params StrokeSpec[] strokes)
    {
        var viewModel = new EditorViewModel();
        PathItem path = Line(viewModel, strokes);

        // The selection (and which stroke is being inspected) is made **before** Attach: attaching is what
        // subscribes the pane, and attaching first leaves it never having seen the selection.
        viewModel.SelectObject(path);
        viewModel.InspectedStroke = inspected;

        var pane = new StrokePane();
        pane.Attach(viewModel);

        var window = new Window { Width = 440, Height = 640, Content = pane };
        window.Show();
        Settle();
        return (pane, viewModel, path);
    }

    private static void Settle()
    {
        for (int i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static ListBox List(StrokePane pane)
        => pane.FindControl<ListBox>("EffectList") ?? throw new Xunit.Sdk.XunitException("no effects list");

    private static ComboBox Kinds(StrokePane pane)
        => pane.FindControl<ComboBox>("EffectKindBox") ?? throw new Xunit.Sdk.XunitException("no kind box");

    private static void Click(StrokePane pane, string name)
    {
        Button button = pane.FindControl<Button>(name) ?? throw new Xunit.Sdk.XunitException($"no button {name}");
        button.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Settle();
    }

    /// <summary>
    /// The list describes the inspected stroke's effects, not the top of the stack's.
    ///
    /// The three strokes deliberately disagree: none, two and one. The top of the stack has one effect, so a list
    /// still reading it would show one row where the inspected middle stroke has two.
    /// </summary>
    [AvaloniaFact]
    public void TheEffectListShowsTheInspectedStrokesEffects()
    {
        (StrokePane pane, _, PathItem path) = PaneHost(
            1,
            Stroke(4),
            Stroke(8) with
            {
                Effects = new EffectStack(new[] { OutlineEffectSpec.OffsetPath(4), OutlineEffectSpec.Roughen(2, seed: 3) }),
            },
            Stroke(12) with { Effects = new EffectStack(new[] { OutlineEffectSpec.ZigZag(2) }) });

        Assert.Equal(2, List(pane).ItemCount);
        Assert.Equal(
            new[] { OutlineEffectKind.OffsetPath, OutlineEffectKind.Roughen },
            path.Strokes[1].AllEffects.Select(e => e.Kind).ToArray());
    }

    /// <summary>**Add lands on the inspected stroke** and leaves its neighbours' lists alone.</summary>
    [AvaloniaFact]
    public void AddingFromThePaneLandsOnTheInspectedStroke()
    {
        (StrokePane pane, _, PathItem path) = PaneHost(1, Stroke(4), Stroke(8), Stroke(12));

        Kinds(pane).SelectedItem = "roughen";
        Click(pane, "AddEffectButton");

        Assert.Equal(OutlineEffectKind.Roughen, Assert.Single(path.Strokes[1].AllEffects).Kind);
        Assert.Empty(path.Strokes[0].AllEffects);
        Assert.Empty(path.Strokes[2].AllEffects);
    }

    /// <summary>The raster half of the same thing, because those are two lists the model keeps separately.</summary>
    [AvaloniaFact]
    public void AddingARasterEffectFromThePaneLandsOnTheInspectedStroke()
    {
        (StrokePane pane, _, PathItem path) = PaneHost(1, Stroke(4), Stroke(8), Stroke(12));

        Kinds(pane).SelectedItem = "blur";
        Click(pane, "AddEffectButton");

        Assert.Equal(RasterEffectKind.Blur, Assert.Single(path.Strokes[1].AllRasterEffects).Kind);
        Assert.Empty(path.Strokes[0].AllRasterEffects);
        Assert.Empty(path.Strokes[2].AllRasterEffects);
    }

    /// <summary>
    /// A registry-built parameter editor reads and writes the inspected stroke's effect: two strokes can carry the
    /// same kind, and the box must show - and change - the one the row belongs to.
    /// </summary>
    [AvaloniaFact]
    public void AParameterEditFromThePaneChangesOnlyTheInspectedStroke()
    {
        (StrokePane pane, _, PathItem path) = PaneHost(
            1,
            Stroke(4) with { Effects = new EffectStack(new[] { OutlineEffectSpec.Roughen(2, seed: 3) }) },
            Stroke(8) with { Effects = new EffectStack(new[] { OutlineEffectSpec.Roughen(7.5, seed: 3) }) });

        List(pane).SelectedIndex = 0;

        TextBox size = pane.FindControl<StackPanel>("EffectParameters")!
            .GetVisualDescendants().OfType<TextBox>().Single(b => (string?)b.Tag == "size");

        // The box shows the inspected stroke's value, not the first match's.
        Assert.Equal("7.5", size.Text);

        size.Text = "9";
        size.RaiseEvent(new RoutedEventArgs(InputElement.LostFocusEvent));
        Settle();

        Assert.Equal(9.0, path.Strokes[1].AllEffects[0].Size, 6);
        Assert.Equal(2.0, path.Strokes[0].AllEffects[0].Size, 6);
    }

    /// <summary>Remove takes the row off the inspected stroke, not off the first stroke carrying that index.</summary>
    [AvaloniaFact]
    public void RemovingFromThePaneActsOnTheInspectedStroke()
    {
        (StrokePane pane, _, PathItem path) = PaneHost(
            1,
            Stroke(4) with { Effects = new EffectStack(new[] { OutlineEffectSpec.Roughen(2, seed: 3) }) },
            Stroke(8) with { Effects = new EffectStack(new[] { OutlineEffectSpec.Roughen(2, seed: 3) }) });

        List(pane).SelectedIndex = 0;
        Click(pane, "RemoveEffectButton");

        Assert.Empty(path.Strokes[1].AllEffects);
        Assert.Single(path.Strokes[0].AllEffects);
    }

    /// <summary>And a move reorders the inspected stroke's list, leaving the neighbour's order alone.</summary>
    [AvaloniaFact]
    public void MovingFromThePaneActsOnTheInspectedStroke()
    {
        (StrokePane pane, _, PathItem path) = PaneHost(
            1,
            Stroke(4) with
            {
                Effects = new EffectStack(new[] { OutlineEffectSpec.OffsetPath(4), OutlineEffectSpec.Roughen(2, seed: 3) }),
            },
            Stroke(8) with
            {
                Effects = new EffectStack(new[] { OutlineEffectSpec.OffsetPath(4), OutlineEffectSpec.Roughen(2, seed: 3) }),
            });

        List(pane).SelectedIndex = 1;
        Click(pane, "MoveEffectDownButton");

        Assert.Equal(
            new[] { OutlineEffectKind.Roughen, OutlineEffectKind.OffsetPath },
            path.Strokes[1].AllEffects.Select(e => e.Kind).ToArray());
        Assert.Equal(
            new[] { OutlineEffectKind.OffsetPath, OutlineEffectKind.Roughen },
            path.Strokes[0].AllEffects.Select(e => e.Kind).ToArray());
    }

    /// <summary>
    /// **With nothing inspected the effects section describes nothing and edits nothing** - the same rule the
    /// geometry fields follow, rather than falling back to editing every stroke of the selection.
    /// </summary>
    [AvaloniaFact]
    public void WithNothingInspectedNothingChanges()
    {
        (StrokePane pane, EditorViewModel viewModel, PathItem path) = PaneHost(
            -1,
            Stroke(4) with { Effects = new EffectStack(new[] { OutlineEffectSpec.Roughen(2, seed: 3) }) },
            Stroke(8));

        Assert.Equal(-1, viewModel.InspectedStroke);
        Assert.Equal(0, List(pane).ItemCount);

        Kinds(pane).SelectedItem = "offsetPath";
        Click(pane, "AddEffectButton");
        Click(pane, "RemoveEffectButton");
        Click(pane, "MoveEffectUpButton");

        Assert.Equal(OutlineEffectKind.Roughen, Assert.Single(path.Strokes[0].AllEffects).Kind);
        Assert.Empty(path.Strokes[1].AllEffects);
    }
}
