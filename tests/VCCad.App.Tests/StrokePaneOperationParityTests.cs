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
/// - **Parity.** Where the operation could not express what the control could, the test said so rather than
///   pretending otherwise, as the "failing on improvement" records this repository uses. All three gaps it pinned
///   (an empty dash read as "not given", `enabled:false` hard-coded away, one undo step per path for a brush) are
///   **closed**, so each is now a positive assertion: the dash is empty, the dynamics target is off, the undo
///   depth is one. They are kept, not deleted, because they are what catches the gap coming back.
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

    // ------------------------------------------------- parity (the three closed gaps)

    /// <summary>
    /// **PARITY.** The dash combo can go back to **solid**, and so can `style.setStroke`.
    ///
    /// This pinned a gap: an omitted `dash` and an empty one were read identically, so `{"dash":[]}` meant "not
    /// given" and a driver could never take a dashed stroke back to Solid the way the combo can. The operation now
    /// reads a **given** empty array as a request for no dash - the same "empty means none" reading
    /// `style.setWidthProfile` takes of its points list - and carries it to the session as an explicit clear,
    /// because an empty `DashPattern` is value-equal to the default one and `null` already means "leave it alone".
    ///
    /// What it asserts now: the control's Solid and the operation's `dash:[]` reach the **same** model state, an
    /// empty dash on the stroke.
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

        // The driver's half asks the same thing, and gets the same stroke the control left behind.
        EditorOperations.Invoke(new AutomationContext { ViewModel = viewModel }, "style.setStroke",
            Params(new { index = 0, dash = Array.Empty<double>() }));

        Assert.True(path.Strokes[0].Dash.IsEmpty);
    }

    /// <summary>
    /// **PARITY.** The dynamics checkbox can switch **one target** off while leaving the others as they are, and so
    /// can `style.setDynamics`.
    ///
    /// This pinned a gap: the operation built its request with `enabled: true` hard-coded, so a driver passing
    /// `enabled: false` was neither refused nor obeyed - the target ended up **on**, which is worse than a refusal
    /// because the caller is told nothing. `style.clearDynamics` is not a substitute: it removes the whole
    /// response, taking every other target with it.
    ///
    /// What it asserts now: `enabled:false` switches the named target off **and leaves opacity dynamics on** -
    /// switching one off must not disturb the others, which is the half a blanket clear would get wrong.
    /// </summary>
    [AvaloniaFact]
    public void TheDynamicsCheckboxCanSwitchOneTargetOffWhereTheOperationCannot()
    {
        // Width responds, and so does opacity: the second is what proves switching one target off does not take the
        // others with it, which is the property `style.clearDynamics` cannot express.
        var responding = Stroke(4) with
        {
            Dynamics = new DynamicsSpec(Enum.GetValues<DynamicsTarget>().Select(target => target switch
            {
                DynamicsTarget.Width => DynamicsTargetSpec.Preset(DynamicsPreset.Soft),
                DynamicsTarget.Opacity => DynamicsTargetSpec.Preset(DynamicsPreset.Soft),
                _ => DynamicsTargetSpec.Off,
            })),
        };
        (StrokePane pane, EditorViewModel viewModel, PathItem path) = Host(0, responding);

        Assert.True(path.Strokes[0].Dynamics!.For(DynamicsTarget.Width).Enabled);
        Assert.True(path.Strokes[0].Dynamics!.For(DynamicsTarget.Opacity).Enabled);

        Check(pane, "DynamicsWidthEnabled").IsChecked = false;
        Settle();
        Assert.False(path.Strokes[0].Dynamics!.For(DynamicsTarget.Width).Enabled);

        viewModel.Undo();
        Settle();
        Assert.True(path.Strokes[0].Dynamics!.For(DynamicsTarget.Width).Enabled);

        EditorOperations.Invoke(new AutomationContext { ViewModel = viewModel }, "style.setDynamics",
            Params(new { target = "width", preset = "soft", enabled = false, strokeIndex = 0 }));

        Assert.False(path.Strokes[0].Dynamics!.For(DynamicsTarget.Width).Enabled);
        Assert.True(path.Strokes[0].Dynamics!.For(DynamicsTarget.Opacity).Enabled);
    }

    /// <summary>
    /// **PARITY.** Choosing a brush over a selection of two paths costs **one** undo step, which is what issue #111
    /// asks for - "one gesture is one undo step".
    ///
    /// This pinned a gap: `brush.apply` wrote one `SetStrokesCommand` per path rather than composing them, so the
    /// operation - and the pane control that drives it - put one entry on the stack per selected path, and one
    /// Undo took the brush off one path and left it on the other. The consequence was visible rather than
    /// bookkeeping.
    ///
    /// What it asserts now: the depth moves by exactly **1**, and that single Undo takes the brush off **both**
    /// paths - the same `CompositeCommand` composition `style.setWidthProfile` and `style.setStroke` already use.
    /// </summary>
    [AvaloniaFact]
    public void ChoosingABrushOverTwoPathsCostsOneUndoStepPerPath()
    {
        var viewModel = new EditorViewModel();
        PathItem first = Line(viewModel, Stroke(4));
        PathItem second = Line(viewModel, Stroke(4));
        viewModel.SelectObject(first);
        viewModel.ToggleObjectSelection(second);
        viewModel.InspectedStroke = 0;

        EditorOperations.Invoke(new AutomationContext { ViewModel = viewModel }, "brush.create",
            Params(new { name = "Chisel", angle = 90, roundness = 0.25, diameter = 20 }));

        var pane = new StrokePane();
        pane.Attach(viewModel);
        var window = new Window { Width = 460, Height = 900, Content = pane };
        window.Show();
        Settle();

        int before = viewModel.ActiveSession.UndoDepth;

        Combo(pane, "StrokeBrushBox").SelectedItem = "Chisel";
        Settle();

        Assert.Equal("Chisel", first.Strokes[0].Brush?.Name);
        Assert.Equal("Chisel", second.Strokes[0].Brush?.Name);

        // One gesture, one entry - the assertion this test exists to keep true.
        Assert.Equal(1, viewModel.ActiveSession.UndoDepth - before);

        viewModel.Undo();
        Assert.Equal(0, new[] { first, second }.Count(p => p.Strokes[0].Brush is not null));
    }
}
