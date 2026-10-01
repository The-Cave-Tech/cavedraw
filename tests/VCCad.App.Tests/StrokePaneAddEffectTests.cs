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
/// Adding an effect from the stroke pane.
///
/// The kinds offered come from `EffectRegistry`, so this asserts the property the issue asks for - a new effect
/// appears in the panel **by existing** rather than by being added to a switch in the panel. The test is written
/// against the registry for the same reason the operation-parity test is: a test that repeated the eight names
/// would pass forever while the two drifted.
/// </summary>
public class StrokePaneAddEffectTests
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

    private static PathItem Selected(EditorViewModel viewModel)
    {
        var path = new PathItem { Name = "line", Fill = FillSpec.None };
        SubPath sub = path.AddSubPath(closed: false);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(50, 0)));
        path.Strokes.Clear();
        path.Strokes.Add(new StrokeSpec(true, ColorRgb.Black, 4, StrokeCap.Butt, StrokeJoin.Miter, 4));

        viewModel.Document.Artboards[0].Layers[0].AddItem(path);
        viewModel.SelectObject(path);

        // The Add button puts the effect on the **inspected** stroke, which is what the appearance panel publishes
        // when a row is chosen; with nothing inspected there is no stroke it could honestly land on.
        viewModel.InspectedStroke = 0;
        Settle();
        return path;
    }

    private static ComboBox Kinds(StrokePane pane)
        => pane.FindControl<ComboBox>("EffectKindBox") ?? throw new Xunit.Sdk.XunitException("no kind box");

    private static void Click(StrokePane pane, string name)
    {
        Button button = pane.FindControl<Button>(name) ?? throw new Xunit.Sdk.XunitException($"no button {name}");
        button.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        Settle();
    }

    /// <summary>The pane offers exactly what the registry declares - no more, and none missing.</summary>
    [AvaloniaFact]
    public void ThePaneOffersEveryDeclaredKind()
    {
        (StrokePane pane, _) = Host();

        string[] offered = Kinds(pane).Items.OfType<string>().OrderBy(n => n).ToArray();
        string[] declared = EffectRegistry.All.Select(d => d.Kind).OrderBy(n => n).ToArray();

        Assert.Equal(declared, offered);
    }

    /// <summary>
    /// **Every declared effect can be added from the pane**, and lands in the family its declaration names.
    ///
    /// This is the issue's own acceptance test in the shape the panel can carry it: the choices offered and the
    /// effects that arrive are the same list, because both come from the registry.
    /// </summary>
    [AvaloniaFact]
    public void EveryDeclaredKindCanBeAdded()
    {
        foreach (EffectDefinition definition in EffectRegistry.All)
        {
            (StrokePane pane, EditorViewModel viewModel) = Host();
            PathItem path = Selected(viewModel);

            Kinds(pane).SelectedItem = definition.Kind;
            Click(pane, "AddEffectButton");

            StrokeSpec stroke = path.Strokes[0];
            if (definition.Raster)
            {
                Assert.True(stroke.AllRasterEffects.Count == 1,
                    $"'{definition.Kind}' should have added one raster effect");
                Assert.Equal(definition.RasterKind, stroke.AllRasterEffects[0].Kind);
            }
            else
            {
                Assert.Equal(1, stroke.AllEffects.Count);
                Assert.Equal(definition.OutlineKind, stroke.AllEffects[0].Kind);
            }

            // The list follows the model, which is what stops it showing an effect that is not there.
            Assert.Equal(1, pane.FindControl<ListBox>("EffectList")!.ItemCount);
        }
    }

    /// <summary>Adding twice keeps both, in the order they were added.</summary>
    [AvaloniaFact]
    public void AddingTwiceKeepsBothInOrder()
    {
        (StrokePane pane, EditorViewModel viewModel) = Host();
        PathItem path = Selected(viewModel);

        Kinds(pane).SelectedItem = "offsetPath";
        Click(pane, "AddEffectButton");

        Kinds(pane).SelectedItem = "roughen";
        Click(pane, "AddEffectButton");

        Assert.Equal(
            new[] { OutlineEffectKind.OffsetPath, OutlineEffectKind.Roughen },
            path.Strokes[0].AllEffects.Select(e => e.Kind).ToArray());
    }

    /// <summary>The effect is added with the model's own defaults rather than a value the panel invented.</summary>
    [AvaloniaFact]
    public void TheEffectArrivesWithTheModelsDefaults()
    {
        (StrokePane pane, EditorViewModel viewModel) = Host();
        PathItem path = Selected(viewModel);

        Kinds(pane).SelectedItem = "dropShadow";
        Click(pane, "AddEffectButton");

        RasterEffectSpec added = path.Strokes[0].AllRasterEffects[0];
        RasterEffectSpec expected = new(RasterEffectKind.DropShadow);

        Assert.Equal(expected.Radius, added.Radius, 6);
        Assert.Equal(expected.OffsetX, added.OffsetX, 6);
        Assert.Equal(expected.Opacity, added.Opacity, 6);
        Assert.Null(added.Tint);
    }
}
