using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using VCCad.App.Automation;
using VCCad.App.ViewModels;
using VCCad.App.Views.Panes;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// Removing one effect, from the pane and from the registry.
///
/// The outline effects and the raster effects are **two lists** on the same stroke, so an index means nothing
/// without saying which - and a remove that searched the wrong one would take away an effect nobody asked about.
/// That is what these assert, on the model, in both directions.
/// </summary>
public class EffectRemoveTests
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

    private static AutomationContext Host(PathItem path, out EditorViewModel viewModel)
    {
        viewModel = new EditorViewModel();
        viewModel.Document.Artboards[0].Layers[0].AddItem(path);
        viewModel.SelectObject(path);
        return new AutomationContext { ViewModel = viewModel };
    }

    [Fact]
    public void TheOperationRemovesTheOutlineEffectAtThatIndex()
    {
        PathItem path = Path(OutlineEffectSpec.OffsetPath(4), OutlineEffectSpec.Roughen(2, seed: 3));
        AutomationContext context = Host(path, out _);

        EditorOperations.Invoke(context, "style.removeStrokeEffect", Params(new { index = 0 }));

        Assert.Equal(new[] { OutlineEffectKind.Roughen }, path.Strokes[0].AllEffects.Select(e => e.Kind).ToArray());
    }

    [Fact]
    public void TheRasterOperationRemovesFromTheOtherList()
    {
        PathItem path = Path(OutlineEffectSpec.Roughen(2, seed: 3));
        path.Strokes[0] = path.Strokes[0] with
        {
            RasterEffects = new RasterEffectStack(new[] { RasterEffectSpec.Blur(4) }),
        };

        AutomationContext context = Host(path, out _);

        EditorOperations.Invoke(context, "style.removeRasterEffect", Params(new { index = 0 }));

        // The outline effect is untouched, because the raster effects are a separate list.
        Assert.Single(path.Strokes[0].AllEffects);
        Assert.True(path.Strokes[0].AllRasterEffects is null || path.Strokes[0].AllRasterEffects.Count == 0);
    }

    [Fact]
    public void RemovingIsOneUndoStep()
    {
        PathItem path = Path(OutlineEffectSpec.OffsetPath(4), OutlineEffectSpec.Roughen(2, seed: 3));
        AutomationContext context = Host(path, out _);

        EditorOperations.Invoke(context, "style.removeStrokeEffect", Params(new { index = 0 }));
        Assert.Single(path.Strokes[0].AllEffects);

        context.Session.Undo();

        Assert.Equal(2, path.Strokes[0].AllEffects.Count);
    }

    [Fact]
    public void AnOutOfRangeIndexChangesNothing()
    {
        PathItem path = Path(OutlineEffectSpec.OffsetPath(4));
        AutomationContext context = Host(path, out _);

        JsonElement result = JsonSerializer.SerializeToElement(
            EditorOperations.Invoke(context, "style.removeStrokeEffect", Params(new { index = 7 })));

        Assert.Equal(0, result.GetProperty("changed").GetInt32());
        Assert.Single(path.Strokes[0].AllEffects);
    }

    /// <summary>**The pane's Remove takes the row that is selected**, and only that one.</summary>
    [AvaloniaFact]
    public void ThePaneRemovesTheSelectedEffect()
    {
        var viewModel = new EditorViewModel();
        var pane = new StrokePane();
        pane.Attach(viewModel);

        var window = new Window { Width = 420, Height = 600, Content = pane };
        window.Show();
        for (int i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        PathItem path = Path(OutlineEffectSpec.OffsetPath(4), OutlineEffectSpec.Roughen(2, seed: 3));
        viewModel.Document.Artboards[0].Layers[0].AddItem(path);
        viewModel.SelectObject(path);
        for (int i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        var list = pane.FindControl<ListBox>("EffectList")!;
        list.SelectedIndex = 1;

        pane.FindControl<Button>("RemoveEffectButton")!
            .RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));

        for (int i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        Assert.Equal(new[] { OutlineEffectKind.OffsetPath }, path.Strokes[0].AllEffects.Select(e => e.Kind).ToArray());
    }
}
