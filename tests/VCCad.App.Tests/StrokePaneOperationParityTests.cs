using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
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
/// The stroke inspector's existing fields against the operations that stand for them - the other half of issue
/// #111's acceptance, beside <see cref="StrokePaneBrushSelectorTests"/>.
///
/// Two kinds of assertion live here, and the difference matters:
///
/// - **Equivalence.** A field and its operation leave the same model state. This passes against the pane as it
///   stands, because the pane and `style.setStroke` run the same `DocumentSession` method - it is a guard against
///   the two drifting apart, not a pin on a defect.
/// - **Sentinels.** Where the operation genuinely **cannot** express what the control can, the test says so rather
///   than pretending otherwise. These are the "failing on improvement" records this repository uses for known
///   gaps: when the operation grows the parameter, the sentinel must be turned into a positive assertion, not
///   deleted. Both are reported on #111 as parity defects rather than papered over with a second implementation
///   inside the pane.
/// </summary>
public class StrokePaneOperationParityTests
{
    private static JsonElement Params(object value) => JsonSerializer.SerializeToElement(value);

    private static (StrokePane Pane, EditorViewModel ViewModel, PathItem Path) Host(int inspected,
        params StrokeSpec[] strokes)
    {
        var viewModel = new EditorViewModel();
        PathItem path = Line(viewModel, strokes);
        viewModel.SelectObject(path);
        viewModel.InspectedStroke = inspected;

        var pane = new StrokePane();
        pane.Attach(viewModel);

        var window = new Window { Width = 460, Height = 900, Content = pane };
        window.Show();
        Settle();

        return (pane, viewModel, path);
    }

    private static PathItem Line(EditorViewModel viewModel, params StrokeSpec[] strokes)
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

    private static StrokeSpec Stroke(double width)
        => new(true, ColorRgb.Black, width, StrokeCap.Butt, StrokeJoin.Miter, 4.0);

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

    private static CheckBox Check(StrokePane pane, string name)
        => pane.FindControl<CheckBox>(name) ?? throw new Xunit.Sdk.XunitException($"no check box called {name}");

    /// <summary>Types a value and commits it the way the field does - on losing focus.</summary>
    private static void Commit(StrokePane pane, string name, string text)
    {
        TextBox box = Box(pane, name);
        box.Text = text;
        box.RaiseEvent(new RoutedEventArgs(InputElement.LostFocusEvent));
        Settle();
    }

    // ---------------------------------------------------------------- equivalence

    /// <summary>**The width field and `style.setStroke` are one act.** Typing 21 and calling the operation with
    /// `index` leave the whole stroke identical, member for member - not merely the same width.</summary>
    [AvaloniaFact]
    public void DrivingTheWidthFieldAndInvokingTheOperationLeaveTheSameModelState()
    {
        (StrokePane pane, EditorViewModel viewModel, PathItem path) = Host(1, Stroke(4), Stroke(8));

        Commit(pane, "StrokeWidthBox", "21");
        StrokeSpec byControl = path.Strokes[1];
        Assert.Equal(21.0, byControl.Width, 6);

        viewModel.Undo();
        Settle();
        Assert.Equal(8.0, path.Strokes[1].Width, 6);

        EditorOperations.Invoke(new AutomationContext { ViewModel = viewModel }, "style.setStroke",
            Params(new { width = 21, index = 1 }));

        Assert.Equal(byControl, path.Strokes[1]);
    }

    /// <summary>The cap and alignment combos and `style.setStroke` are one act, the same way. Each is driven on its
    /// own, because each field commit is its own gesture and therefore its own undo step. Both land on the middle
    /// stroke of three, so a control that wrote the top of the stack would be caught rather than pass.</summary>
    [AvaloniaFact]
    public void DrivingTheCapAndAlignFieldsAndInvokingTheOperationLeaveTheSameModelState()
    {
        (StrokePane pane, EditorViewModel viewModel, PathItem path) = Host(1, Stroke(4), Stroke(8), Stroke(12));
        var context = new AutomationContext { ViewModel = viewModel };

        Combo(pane, "StrokeCapBox").SelectedIndex = 2; // Square
        Settle();
        StrokeSpec byCap = path.Strokes[1];
        Assert.Equal(StrokeCap.Square, byCap.Cap);

        viewModel.Undo();
        Settle();
        Assert.Equal(StrokeCap.Butt, path.Strokes[1].Cap);

        EditorOperations.Invoke(context, "style.setStroke", Params(new { cap = "square", index = 1 }));
        Assert.Equal(byCap, path.Strokes[1]);

        Combo(pane, "StrokeAlignBox").SelectedIndex = 2; // Outside
        Settle();
        StrokeSpec byAlign = path.Strokes[1];
        Assert.Equal(StrokeAlignment.Outside, byAlign.Alignment);

        viewModel.Undo();
        Settle();
        Assert.Equal(StrokeAlignment.Center, path.Strokes[1].Alignment);

        EditorOperations.Invoke(context, "style.setStroke", Params(new { alignment = "outside", index = 1 }));
        Assert.Equal(byAlign, path.Strokes[1]);
    }

    // ---------------------------------------------------------------- sentinels

    /// <summary>
    /// **SENTINEL.** The dash combo can go back to **solid**; `style.setStroke` cannot ask for that.
    ///
    /// An omitted `dash` and an empty one are read identically - the operation only builds a pattern when the array
    /// has at least one number - so `{"dash":[]}` is "not given" rather than "no dash", and a driver has no way to
    /// turn a dashed stroke solid on one stroke of the stack. This is a real parity gap: a person can do it and a
    /// driver cannot, which is the defect this repository treats as a bug. Reported on #111.
    ///
    /// When the operation grows a way to say "no dash", this becomes a positive assertion that the two agree.
    /// </summary>
    [AvaloniaFact]
    public void TheDashControlCanGoSolidWhereTheOperationCannotAskForIt()
    {
        var dashed = Stroke(4) with { Dash = new DashPattern(new double[] { 4, 3 }) };
        (StrokePane pane, EditorViewModel viewModel, PathItem path) = Host(0, dashed);

        Assert.False(path.Strokes[0].Dash.IsEmpty);

        Combo(pane, "StrokeDashBox").SelectedIndex = 0; // Solid
        Settle();
        Assert.True(path.Strokes[0].Dash.IsEmpty);

        viewModel.Undo();
        Settle();
        Assert.False(path.Strokes[0].Dash.IsEmpty);

        // The driver's half asks the same thing, and the stroke stays dashed.
        EditorOperations.Invoke(new AutomationContext { ViewModel = viewModel }, "style.setStroke",
            Params(new { index = 0, dash = Array.Empty<double>() }));

        Assert.False(path.Strokes[0].Dash.IsEmpty);
    }

    /// <summary>
    /// **SENTINEL.** The dynamics checkbox can switch **one target** off while leaving the others as they are;
    /// `style.setDynamics` can only switch one on.
    ///
    /// The operation builds its request with `enabled: true` hard-coded, so a driver passing `enabled: false` is
    /// not refused and not obeyed - the target ends up **on**, which is worse than a refusal because the caller is
    /// told nothing. `style.clearDynamics` is not the answer either: it removes the whole response, so using it to
    /// switch one target off would silently take the others with it. Reported on #111.
    ///
    /// When the operation grows an `enabled` parameter, the last assertion flips to `Assert.False`.
    /// </summary>
    [AvaloniaFact]
    public void TheDynamicsCheckboxCanSwitchOneTargetOffWhereTheOperationCannot()
    {
        var responding = Stroke(4) with { Dynamics = DynamicsSpec.PressureToWidth(DynamicsPreset.Soft) };
        (StrokePane pane, EditorViewModel viewModel, PathItem path) = Host(0, responding);

        Assert.True(path.Strokes[0].Dynamics!.For(DynamicsTarget.Width).Enabled);

        Check(pane, "DynamicsWidthEnabled").IsChecked = false;
        Settle();
        Assert.False(path.Strokes[0].Dynamics!.For(DynamicsTarget.Width).Enabled);

        viewModel.Undo();
        Settle();
        Assert.True(path.Strokes[0].Dynamics!.For(DynamicsTarget.Width).Enabled);

        EditorOperations.Invoke(new AutomationContext { ViewModel = viewModel }, "style.setDynamics",
            Params(new { target = "width", preset = "soft", enabled = false, strokeIndex = 0 }));

        Assert.True(path.Strokes[0].Dynamics!.For(DynamicsTarget.Width).Enabled);
    }
}
