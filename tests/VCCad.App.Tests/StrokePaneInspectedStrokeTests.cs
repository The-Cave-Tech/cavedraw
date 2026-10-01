using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using VCCad.App.ViewModels;
using VCCad.App.Views.Panes;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// The stroke inspector describing and editing **the stroke that is being inspected** - not the top of the stack.
///
/// With the appearance stack there is no such thing as "the stroke" on a selection, and the pane used to write
/// `PathItem.Stroke`, the compatibility property meaning "the last one added". So a person could be looking at
/// stroke 2 of 3 in the appearance panel while typing a width that landed on stroke 3 - the disagreement the shared
/// `InspectedStroke` exists to prevent, in the panel it was meant to prevent it in.
///
/// Everything is asserted on the **model** after being driven through the real controls: the failure this pane is
/// most likely to have is a field that shows one stroke while the edit reaches another, and asserting the field
/// alone cannot see that. A middle stroke is inspected deliberately - inspecting the top one would pass against the
/// old code, because the top stroke is exactly what it used to write.
/// </summary>
public class StrokePaneInspectedStrokeTests
{
    private static (StrokePane Pane, EditorViewModel ViewModel, PathItem Path) Host(params StrokeSpec[] strokes)
    {
        var viewModel = new EditorViewModel();
        PathItem path = AddLine(viewModel, strokes);
        viewModel.SelectObject(path);

        var pane = new StrokePane();
        pane.Attach(viewModel);

        var window = new Window { Width = 420, Height = 640, Content = pane };
        window.Show();
        Settle();

        return (pane, viewModel, path);
    }

    private static PathItem AddLine(EditorViewModel viewModel, params StrokeSpec[] strokes)
    {
        var path = new PathItem { Name = "line", Fill = FillSpec.None };
        SubPath sub = path.AddSubPath(closed: false);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(50, 0)));

        path.Strokes.Clear();
        path.Strokes.AddRange(strokes);
        viewModel.Document.Artboards[0].Layers[0].AddItem(path);
        return path;
    }

    private static StrokeSpec Stroke(double width, StrokeCap cap = StrokeCap.Butt,
        StrokeJoin join = StrokeJoin.Miter, double miter = 4, StrokeAlignment alignment = StrokeAlignment.Center,
        DashPattern? dash = null)
        => new(true, ColorRgb.Black, width, cap, join, miter, alignment, dash ?? DashPattern.None);

    private static void Settle()
    {
        for (int i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static TextBox Box(StrokePane pane, string name)
        => pane.FindControl<TextBox>(name) ?? throw new Xunit.Sdk.XunitException($"no box called {name}");

    private static ComboBox Combo(StrokePane pane, string name)
        => pane.FindControl<ComboBox>(name) ?? throw new Xunit.Sdk.XunitException($"no combo called {name}");

    /// <summary>The pane's own label saying which stroke it is describing.</summary>
    private static TextBlock Target(StrokePane pane)
        => pane.FindControl<TextBlock>("StrokeTargetLabel") ?? throw new Xunit.Sdk.XunitException("no target label");

    /// <summary>Types a value and commits it the way the field does - on losing focus.</summary>
    private static void Commit(StrokePane pane, string name, string text)
    {
        TextBox box = Box(pane, name);
        box.Text = text;
        box.RaiseEvent(new RoutedEventArgs(InputElement.LostFocusEvent));
        Settle();
    }

    // ---------------------------------------------------------------- what is shown

    /// <summary>**The pane shows the inspected stroke, not the top of the stack.** The middle of three is the case
    /// that fails against the old code, which read `path.Stroke`.</summary>
    [AvaloniaFact]
    public void ThePaneShowsTheInspectedStrokeRatherThanTheTopOfTheStack()
    {
        (StrokePane pane, EditorViewModel viewModel, _) = Host(Stroke(4), Stroke(8), Stroke(12));

        viewModel.InspectedStroke = 1;
        Settle();

        Assert.Equal("8", Box(pane, "StrokeWidthBox").Text);
        Assert.Equal("stroke 2 of 3", Target(pane).Text);
    }

    /// <summary>The shared label is shown verbatim, so the two panels cannot be describing different strokes.</summary>
    [AvaloniaFact]
    public void ThePaneShowsTheSharedLabel()
    {
        (StrokePane pane, EditorViewModel viewModel, _) = Host(Stroke(4), Stroke(8));

        viewModel.InspectedStroke = 1;
        Settle();

        Assert.Equal(viewModel.InspectedStrokeLabel, Target(pane).Text);
        Assert.Equal("stroke 2 of 2", Target(pane).Text);
    }

    /// <summary>Every member the pane carries comes from the inspected stroke - width, miter, cap, join, align, dash.</summary>
    [AvaloniaFact]
    public void EveryFieldComesFromTheInspectedStroke()
    {
        (StrokePane pane, EditorViewModel viewModel, _) = Host(
            Stroke(4),
            Stroke(8, StrokeCap.Round, StrokeJoin.Bevel, miter: 6, alignment: StrokeAlignment.Inside,
                dash: new DashPattern(new double[] { 4, 3 })),
            Stroke(12));

        viewModel.InspectedStroke = 1;
        Settle();

        Assert.Equal("8", Box(pane, "StrokeWidthBox").Text);
        Assert.Equal("6", Box(pane, "MiterBox").Text);
        Assert.Equal(1, Combo(pane, "StrokeCapBox").SelectedIndex);
        Assert.Equal(2, Combo(pane, "StrokeJoinBox").SelectedIndex);
        Assert.Equal(1, Combo(pane, "StrokeAlignBox").SelectedIndex);
        Assert.Equal(1, Combo(pane, "StrokeDashBox").SelectedIndex);
    }

    /// <summary>Choosing a row in the real appearance panel is what moves the pane - the contract, end to end.</summary>
    [AvaloniaFact]
    public void TheAppearancePanelsChoiceMovesThePane()
    {
        var viewModel = new EditorViewModel();
        PathItem path = AddLine(viewModel, Stroke(4), Stroke(8), Stroke(12));
        viewModel.SelectObject(path);

        var inspector = new StrokePane();
        inspector.Attach(viewModel);
        var appearance = new AppearancePane();
        appearance.Attach(viewModel);

        var window = new Window
        {
            Width = 700,
            Height = 700,
            Content = new StackPanel { Children = { inspector, appearance } },
        };
        window.Show();
        Settle();

        appearance.FindControl<ListBox>("StrokeList")!.SelectedIndex = 1;
        Settle();

        Assert.Equal("8", Box(inspector, "StrokeWidthBox").Text);
        Assert.Equal("stroke 2 of 3", Target(inspector).Text);
    }

    // ---------------------------------------------------------------- where an edit lands

    /// <summary>**An edit lands on the inspected stroke and leaves the others alone** - the middle one, so a pane
    /// that still wrote the top of the stack would change the wrong stroke and this would see it.</summary>
    [AvaloniaFact]
    public void AnEditLandsOnTheInspectedStrokeAndLeavesTheOthersAlone()
    {
        (StrokePane pane, EditorViewModel viewModel, PathItem path) = Host(Stroke(4), Stroke(8), Stroke(12));

        viewModel.InspectedStroke = 1;
        Settle();

        Commit(pane, "StrokeWidthBox", "21");

        Assert.Equal(21.0, path.Strokes[1].Width, 6);
        Assert.Equal(4.0, path.Strokes[0].Width, 6);
        Assert.Equal(12.0, path.Strokes[2].Width, 6);
    }

    /// <summary>The case the issue names: two strokes, the second inspected, and the edit lands on the second.</summary>
    [AvaloniaFact]
    public void AnEditOnTheSecondOfTwoStrokesLandsOnIt()
    {
        (StrokePane pane, EditorViewModel viewModel, PathItem path) = Host(Stroke(4), Stroke(8));

        viewModel.InspectedStroke = 1;
        Settle();

        Commit(pane, "StrokeWidthBox", "9");

        Assert.Equal(9.0, path.Strokes[1].Width, 6);
        Assert.Equal(4.0, path.Strokes[0].Width, 6);
    }

    /// <summary>The other fields land there too, not only the width - a combo applies on selection changed.</summary>
    [AvaloniaFact]
    public void AComboEditLandsOnTheInspectedStroke()
    {
        (StrokePane pane, EditorViewModel viewModel, PathItem path) = Host(Stroke(4), Stroke(8), Stroke(12));

        viewModel.InspectedStroke = 1;
        Settle();

        Combo(pane, "StrokeCapBox").SelectedIndex = 2; // Square
        Settle();

        Assert.Equal(StrokeCap.Square, path.Strokes[1].Cap);
        Assert.Equal(StrokeCap.Butt, path.Strokes[0].Cap);
        Assert.Equal(StrokeCap.Butt, path.Strokes[2].Cap);
    }

    /// <summary>Every edit reaches every selected path, at the same index in each stack - what issue 15 asks for.</summary>
    [AvaloniaFact]
    public void AnEditReachesEverySelectedPath()
    {
        (StrokePane pane, EditorViewModel viewModel, PathItem first) = Host(Stroke(4), Stroke(8));
        PathItem second = AddLine(viewModel, Stroke(4), Stroke(8));
        viewModel.ToggleObjectSelection(second);

        viewModel.InspectedStroke = 1;
        Settle();

        Commit(pane, "StrokeWidthBox", "7");

        Assert.Equal(7.0, first.Strokes[1].Width, 6);
        Assert.Equal(7.0, second.Strokes[1].Width, 6);
        Assert.Equal(4.0, first.Strokes[0].Width, 6);
        Assert.Equal(4.0, second.Strokes[0].Width, 6);
    }

    /// <summary>A path whose stack is shorter than the index is a gap, not an error: it is skipped.</summary>
    [AvaloniaFact]
    public void APathWithNoStrokeAtThatIndexIsSkipped()
    {
        (StrokePane pane, EditorViewModel viewModel, PathItem first) = Host(Stroke(4), Stroke(8));
        PathItem shortStack = AddLine(viewModel, Stroke(5));
        viewModel.ToggleObjectSelection(shortStack);

        viewModel.InspectedStroke = 1;
        Settle();

        Commit(pane, "StrokeWidthBox", "7");

        Assert.Equal(7.0, first.Strokes[1].Width, 6);
        Assert.Equal(5.0, shortStack.Strokes[0].Width, 6);
    }

    /// <summary>One gesture is one undo step, across the whole selection - not one per path.</summary>
    [AvaloniaFact]
    public void OneGestureIsOneUndoStep()
    {
        (StrokePane pane, EditorViewModel viewModel, PathItem first) = Host(Stroke(4), Stroke(8));
        PathItem second = AddLine(viewModel, Stroke(4), Stroke(8));
        viewModel.ToggleObjectSelection(second);

        viewModel.InspectedStroke = 1;
        Settle();
        Commit(pane, "StrokeWidthBox", "7");

        viewModel.Undo();

        Assert.Equal(8.0, first.Strokes[1].Width, 6);
        Assert.Equal(8.0, second.Strokes[1].Width, 6);
    }

    // ---------------------------------------------------------------- nothing inspected

    /// <summary>**Nothing inspected is a state, not a crash**: the pane describes nothing, and typing does not fall
    /// back to editing the top of the stack - which is the defect this pane had.</summary>
    [AvaloniaFact]
    public void NothingInspectedDescribesNothingAndEditsNothing()
    {
        (StrokePane pane, EditorViewModel viewModel, PathItem path) = Host(Stroke(4), Stroke(8));

        // No row has been chosen, so there is no stroke to describe.
        Assert.Equal(-1, viewModel.InspectedStroke);
        Assert.Equal(string.Empty, Box(pane, "StrokeWidthBox").Text);
        Assert.Equal("none", Target(pane).Text);

        Commit(pane, "StrokeWidthBox", "30");

        Assert.Equal(4.0, path.Strokes[0].Width, 6);
        Assert.Equal(8.0, path.Strokes[1].Width, 6);
    }

    /// <summary>With nothing selected the fields still become the style for objects drawn next, as they always did.</summary>
    [AvaloniaFact]
    public void WithNothingSelectedTheFieldsSetTheCurrentStyle()
    {
        (StrokePane pane, EditorViewModel viewModel, _) = Host(Stroke(4), Stroke(8));

        viewModel.ClearSelection();
        Settle();

        Assert.Equal(string.Empty, Box(pane, "StrokeWidthBox").Text);

        Commit(pane, "StrokeWidthBox", "3");

        Assert.Equal(3.0, viewModel.ActiveSession.CurrentStroke.Width, 6);
    }

    /// <summary>And an index that outlives its stack - a shorter selection - is guarded rather than thrown on.</summary>
    [AvaloniaFact]
    public void AnIndexPastTheEndIsGuarded()
    {
        (StrokePane pane, EditorViewModel viewModel, _) = Host(Stroke(4), Stroke(8));

        viewModel.InspectedStroke = 1;
        Settle();

        // The stack shrinks under the index; the property clamps on read, and the pane re-reads it.
        viewModel.ActiveSession.RemoveStroke(1);
        Settle();

        Assert.Null(Record.Exception(() => Commit(pane, "StrokeWidthBox", "5")));
    }
}
