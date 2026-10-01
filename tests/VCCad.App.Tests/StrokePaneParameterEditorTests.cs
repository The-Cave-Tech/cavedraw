using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VCCad.App.ViewModels;
using VCCad.App.Views.Panes;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// The parameter editors, built from the registry.
///
/// This is the requirement #115 is built around: "each effect type declares its parameters - the same declaration
/// the operation uses to validate them - so the panel builds its editors from the registry rather than from a
/// hand-written switch". So the tests ask the registry how many controls there should be and **compare**, rather
/// than naming them: an effect that declares a new parameter must grow an editor without anyone touching the panel.
/// </summary>
public class StrokePaneParameterEditorTests
{
    private static (StrokePane Pane, EditorViewModel ViewModel) Host()
    {
        var viewModel = new EditorViewModel();
        var pane = new StrokePane();
        pane.Attach(viewModel);

        var window = new Window { Width = 440, Height = 640, Content = pane };
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

    private static PathItem Selected(EditorViewModel viewModel, StrokeSpec stroke)
    {
        var path = new PathItem { Name = "line", Fill = FillSpec.None };
        SubPath sub = path.AddSubPath(closed: false);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(50, 0)));
        path.Strokes.Clear();
        path.Strokes.Add(stroke);

        viewModel.Document.Artboards[0].Layers[0].AddItem(path);
        viewModel.SelectObject(path);
        Settle();
        return path;
    }

    private static StrokeSpec Stroke(params OutlineEffectSpec[] effects)
        => new(true, ColorRgb.Black, 4, StrokeCap.Butt, StrokeJoin.Miter, 4) { Effects = new EffectStack(effects) };

    private static StackPanel Editors(StrokePane pane)
        => pane.FindControl<StackPanel>("EffectParameters") ?? throw new Xunit.Sdk.XunitException("no editor panel");

    private static List<TextBox> Boxes(StrokePane pane)
        => Editors(pane).GetVisualDescendants().OfType<TextBox>().ToList();

    private static void Select(StrokePane pane, int row)
    {
        (pane.FindControl<ListBox>("EffectList") ?? throw new Xunit.Sdk.XunitException("no list")).SelectedIndex = row;
        Settle();
    }

    /// <summary>A stroke with no effects offers no editors, because there is nothing to edit.</summary>
    [AvaloniaFact]
    public void AStrokeWithNoEffectsBuildsNoEditors()
    {
        (StrokePane pane, EditorViewModel viewModel) = Host();
        Selected(viewModel, new StrokeSpec(true, ColorRgb.Black, 4, StrokeCap.Butt, StrokeJoin.Miter, 4));

        Assert.Empty(Editors(pane).Children);
    }

    /// <summary>
    /// **Each effect's editors are exactly the parameters its kind declares** - and none for a colour, which is not
    /// a number and which `SetEffectParameter` does not accept.
    /// </summary>
    [AvaloniaFact]
    public void TheEditorsMatchWhatTheRegistryDeclares()
    {
        (StrokePane pane, EditorViewModel viewModel) = Host();
        Selected(viewModel, Stroke(OutlineEffectSpec.Scribble(3, 2, seed: 5)));

        Select(pane, 0);

        string[] declared = EffectRegistry.Find("scribble")!.Parameters
            .Where(p => p.Kind != EffectParameterKind.Color)
            .Select(p => p.Name)
            .OrderBy(n => n)
            .ToArray();

        string[] shown = Boxes(pane).Select(b => (string)b.Tag!).OrderBy(n => n).ToArray();

        Assert.Equal(declared, shown);
    }

    /// <summary>A kind that declares more parameters gets more editors, without the panel being told about them.</summary>
    [AvaloniaFact]
    public void AKindWithMoreParametersGetsMoreEditors()
    {
        (StrokePane pane, EditorViewModel viewModel) = Host();
        Selected(viewModel, Stroke(
            OutlineEffectSpec.OffsetPath(4),
            OutlineEffectSpec.Scribble(3, 2, seed: 5)));

        Select(pane, 0);
        int offsetPathBoxes = Boxes(pane).Count;

        Select(pane, 1);
        int scribbleBoxes = Boxes(pane).Count;

        // An offset path declares one number; a scribble declares three.
        Assert.Equal(1, offsetPathBoxes);
        Assert.Equal(3, scribbleBoxes);
    }

    /// <summary>**Editing a box writes the model**, through the session method the operation calls.</summary>
    [AvaloniaFact]
    public void EditingABoxWritesTheEffect()
    {
        (StrokePane pane, EditorViewModel viewModel) = Host();
        PathItem path = Selected(viewModel, Stroke(OutlineEffectSpec.Roughen(2, seed: 3)));

        Select(pane, 0);

        TextBox size = Boxes(pane).Single(b => (string?)b.Tag == "size");
        size.Text = "9";

        // Committing happens on losing focus, which is how every other field in this pane behaves.
        size.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Input.InputElement.LostFocusEvent));
        Settle();

        Assert.Equal(9.0, path.Strokes[0].AllEffects[0].Size, 6);
    }

    /// <summary>And a box shows the value the effect actually has, not the declaration's default.</summary>
    [AvaloniaFact]
    public void ABoxShowsTheEffectsCurrentValue()
    {
        (StrokePane pane, EditorViewModel viewModel) = Host();
        Selected(viewModel, Stroke(OutlineEffectSpec.Roughen(7.5, seed: 3)));

        Select(pane, 0);

        TextBox size = Boxes(pane).Single(b => (string?)b.Tag == "size");

        Assert.Equal("7.5", size.Text);
    }
}
