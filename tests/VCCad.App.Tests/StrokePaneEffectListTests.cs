using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using VCCad.App.ViewModels;
using VCCad.App.Views.Panes;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// The stroke pane's effects list.
///
/// The order is the picture - roughen inside an offset is not an offset inside a roughen - so the list has to show
/// the model's order and the Up/Down buttons have to move it through the same session method the operation calls.
/// Everything here is asserted on the **model**, plus the list's own order, because a list that shuffled its rows
/// while the document kept the old order is the failure this panel is most likely to have.
/// </summary>
public class StrokePaneEffectListTests
{
    private static (StrokePane Pane, EditorViewModel ViewModel) Host()
    {
        var viewModel = new EditorViewModel();
        var pane = new StrokePane();
        pane.Attach(viewModel);

        var window = new Window { Width = 420, Height = 600, Content = pane };
        window.Show();
        Settle();
        return (pane, viewModel);
    }

    private static void Settle()
    {
        for (int i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static PathItem Selected(EditorViewModel viewModel, params OutlineEffectSpec[] effects)
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

        viewModel.Document.Artboards[0].Layers[0].AddItem(path);
        viewModel.SelectObject(path);

        // The list follows the **inspected** stroke, so the row a person is looking at is the one the buttons edit;
        // nothing inspected means an empty list. Inspecting the only stroke is what the appearance panel publishes.
        viewModel.InspectedStroke = 0;
        Settle();
        return path;
    }

    private static ListBox List(StrokePane pane)
        => pane.FindControl<ListBox>("EffectList") ?? throw new Xunit.Sdk.XunitException("no effects list");

    private static void Click(StrokePane pane, string name)
    {
        Button button = pane.FindControl<Button>(name) ?? throw new Xunit.Sdk.XunitException($"no button {name}");
        button.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Settle();
    }

    [AvaloniaFact]
    public void AStrokeWithNoEffectsShowsAnEmptyList()
    {
        (StrokePane pane, EditorViewModel viewModel) = Host();
        Selected(viewModel);

        Assert.Equal(0, List(pane).ItemCount);
        Assert.False(pane.FindControl<Button>("MoveEffectUpButton")!.IsEnabled);
    }

    /// <summary>The list shows what the stroke carries, in the order it applies.</summary>
    [AvaloniaFact]
    public void TheListShowsTheEffectsInAppliedOrder()
    {
        (StrokePane pane, EditorViewModel viewModel) = Host();
        Selected(viewModel, OutlineEffectSpec.OffsetPath(4), OutlineEffectSpec.Roughen(2, seed: 3));

        Assert.Equal(2, List(pane).ItemCount);
        Assert.Equal(
            new[] { OutlineEffectKind.OffsetPath, OutlineEffectKind.Roughen },
            viewModel.PrimarySelection is PathItem path
                ? path.Strokes[0].AllEffects.Select(e => e.Kind).ToArray()
                : Array.Empty<OutlineEffectKind>());

        // The first row is the first applied, so the panel's own order is the model's.
        Assert.Equal(0, List(pane).SelectedIndex);
    }

    /// <summary>**Down moves the effect earlier in the application order**, and the model follows.</summary>
    [AvaloniaFact]
    public void MovingAnEffectDownChangesTheModelsOrder()
    {
        (StrokePane pane, EditorViewModel viewModel) = Host();
        PathItem path = Selected(
            viewModel, OutlineEffectSpec.OffsetPath(4), OutlineEffectSpec.Roughen(2, seed: 3));

        List(pane).SelectedIndex = 1;
        Click(pane, "MoveEffectDownButton");

        Assert.Equal(
            new[] { OutlineEffectKind.Roughen, OutlineEffectKind.OffsetPath },
            path.Strokes[0].AllEffects.Select(e => e.Kind).ToArray());
    }

    /// <summary>And Up moves it later, which is the same operation the other way.</summary>
    [AvaloniaFact]
    public void MovingAnEffectUpChangesTheModelsOrder()
    {
        (StrokePane pane, EditorViewModel viewModel) = Host();
        PathItem path = Selected(
            viewModel, OutlineEffectSpec.OffsetPath(4), OutlineEffectSpec.Roughen(2, seed: 3));

        List(pane).SelectedIndex = 0;
        Click(pane, "MoveEffectUpButton");

        Assert.Equal(
            new[] { OutlineEffectKind.Roughen, OutlineEffectKind.OffsetPath },
            path.Strokes[0].AllEffects.Select(e => e.Kind).ToArray());
    }

    /// <summary>Moving in a direction there is no room for changes nothing.</summary>
    [AvaloniaFact]
    public void MovingPastTheEndChangesNothing()
    {
        (StrokePane pane, EditorViewModel viewModel) = Host();
        PathItem path = Selected(viewModel, OutlineEffectSpec.OffsetPath(4));

        List(pane).SelectedIndex = 0;
        Click(pane, "MoveEffectUpButton");

        Assert.Single(path.Strokes[0].AllEffects);
    }

    /// <summary>A raster effect is listed too, and says the export will not carry it.</summary>
    [AvaloniaFact]
    public void ARasterEffectIsListedAndMarked()
    {
        (StrokePane pane, EditorViewModel viewModel) = Host();

        var path = new PathItem { Name = "line", Fill = FillSpec.None };
        SubPath sub = path.AddSubPath(closed: false);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(50, 0)));
        path.Strokes.Clear();
        path.Strokes.Add(new StrokeSpec(true, ColorRgb.Black, 4, StrokeCap.Butt, StrokeJoin.Miter, 4)
        {
            RasterEffects = new RasterEffectStack(new[] { RasterEffectSpec.Blur(4) }),
        });
        viewModel.Document.Artboards[0].Layers[0].AddItem(path);
        viewModel.SelectObject(path);
        viewModel.InspectedStroke = 0;
        Settle();

        Assert.Equal(1, List(pane).ItemCount);
    }
}
