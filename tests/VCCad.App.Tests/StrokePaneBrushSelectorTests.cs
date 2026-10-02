using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using VCCad.App.Automation;
using VCCad.App.ViewModels;
using VCCad.App.Views.Panes;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// The **brush selector** the stroke inspector's feature list names: the one section of issue #111 that had no
/// control, because until #99 there was nothing for it to select.
///
/// A brush is a reusable asset in the document (`CadDocument.Brushes`) and a stroke refers to it by **name**
/// (`StrokeSpec.Brush`), so the selector is a picker over the document's library rather than a number - the same
/// shape as the width-profile type box beside it.
///
/// Every test here is about **parity**, not about a combo box. The acceptance for this panel is that choosing a
/// brush and calling `brush.apply` are the same act on the same model state, so the two are driven side by side and
/// compared as model values. A test that asserted only that the control was set would pass against a pane that
/// changed nothing at all.
/// </summary>
public class StrokePaneBrushSelectorTests
{
    // ---------------------------------------------------------------- driving the pane

    /// <summary>
    /// Builds a document with one path per entry of <paramref name="morePaths"/>, selects them, and attaches the
    /// pane. The brushes are put in the library **before** the pane exists, through the operation a driver would
    /// use - not by touching the model - so the list is read from the document rather than handed to the control.
    /// </summary>
    private static (StrokePane Pane, EditorViewModel ViewModel, PathItem First, PathItem Second) Host(
        int inspected, string[] brushes, params StrokeSpec[] strokes)
    {
        var viewModel = new EditorViewModel();
        foreach (string brush in brushes)
        {
            CreateBrush(viewModel, brush);
        }

        PathItem first = Line(viewModel, strokes);
        PathItem second = Line(viewModel, strokes);

        viewModel.SelectObject(first);
        viewModel.ToggleObjectSelection(second);
        viewModel.InspectedStroke = inspected;

        var pane = new StrokePane();
        pane.Attach(viewModel);

        var window = new Window { Width = 460, Height = 900, Content = pane };
        window.Show();
        Settle();

        return (pane, viewModel, first, second);
    }

    private static (StrokePane Pane, EditorViewModel ViewModel, PathItem Path) One(
        int inspected, string[] brushes, params StrokeSpec[] strokes)
    {
        var viewModel = new EditorViewModel();
        foreach (string brush in brushes)
        {
            CreateBrush(viewModel, brush);
        }

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

    /// <summary>Puts a brush in the document through the operation a driver would use - not by touching the model.</summary>
    private static void CreateBrush(EditorViewModel viewModel, string name,
        double angle = 90, double roundness = 0.25, double diameter = 20)
        => EditorOperations.Invoke(
            new AutomationContext { ViewModel = viewModel },
            "brush.create",
            Params(new { name, angle, roundness, diameter }));

    private static JsonElement Params(object value) => JsonSerializer.SerializeToElement(value);

    private static void Settle()
    {
        for (int i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static ComboBox Combo(StrokePane pane, string name)
        => pane.FindControl<ComboBox>(name) ?? throw new Xunit.Sdk.XunitException($"no combo called {name}");

    private static TextBlock Text(StrokePane pane, string name)
        => pane.FindControl<TextBlock>(name) ?? throw new Xunit.Sdk.XunitException($"no text block called {name}");

    private static bool Visible(StrokePane pane, string name)
        => pane.FindControl<Control>(name)?.IsVisible ?? throw new Xunit.Sdk.XunitException($"no control called {name}");

    // ---------------------------------------------------------------- what the control shows

    /// <summary>
    /// **Reading the model back shows what the control shows.** The inspected stroke carries a brush, and the
    /// selector is on that brush rather than on nothing or on the other stroke's.
    /// </summary>
    [AvaloniaFact]
    public void TheSelectorShowsTheInspectedStrokesBrush()
    {
        var viewModel = new EditorViewModel();
        CreateBrush(viewModel, "Chisel");
        BrushSpec chisel = viewModel.Document.FindBrush("Chisel")!;

        PathItem path = Line(viewModel, Stroke(4), Stroke(8) with { Brush = chisel });
        viewModel.SelectObject(path);
        viewModel.InspectedStroke = 1;

        var pane = new StrokePane();
        pane.Attach(viewModel);
        var window = new Window { Width = 460, Height = 900, Content = pane };
        window.Show();
        Settle();

        Assert.Equal("Chisel", Combo(pane, "StrokeBrushBox").SelectedItem);
        Assert.Contains("20", Text(pane, "BrushSummary").Text);
    }

    /// <summary>A stroke with no brush is the plain state, not an empty control: the selector says so.</summary>
    [AvaloniaFact]
    public void AStrokeWithNoBrushShowsThePlainState()
    {
        (StrokePane pane, _, _, _) = Host(0, Array.Empty<string>(), Stroke(4));

        Assert.Equal("(none)", Combo(pane, "StrokeBrushBox").SelectedItem);
    }

    /// <summary>The library is offered by existing: a brush in the document appears in the list without anyone
    /// adding it to a switch in the panel.</summary>
    [AvaloniaFact]
    public void EveryBrushInTheDocumentIsOffered()
    {
        (StrokePane pane, _, _, _) = Host(0, new[] { "Chisel", "Flat" }, Stroke(4));

        Assert.Equal(3, Combo(pane, "StrokeBrushBox").ItemCount); // (none), Chisel, Flat
    }

    /// <summary>
    /// A stroke whose brush the document does **not** carry still shows its name: the panel describes the stroke,
    /// and hiding the name would be the panel describing the library instead. `brush.missing` reports the same gap.
    /// </summary>
    [AvaloniaFact]
    public void AStrokeNamingAMissingBrushStillShowsTheName()
    {
        var viewModel = new EditorViewModel();
        var orphan = new BrushSpec("Gone", 90, 0.25, 20);
        PathItem path = Line(viewModel, Stroke(4) with { Brush = orphan });
        viewModel.SelectObject(path);
        viewModel.InspectedStroke = 0;

        var pane = new StrokePane();
        pane.Attach(viewModel);
        var window = new Window { Width = 460, Height = 900, Content = pane };
        window.Show();
        Settle();

        Assert.Equal("Gone", Combo(pane, "StrokeBrushBox").SelectedItem);
        Assert.Contains("not in the document", Text(pane, "BrushSummary").Text);
    }

    // ---------------------------------------------------------------- where the edit lands

    /// <summary>Choosing a brush lands on the inspected stroke of **every** selected path, and on no other stroke of
    /// the stack - the granularity `style.inspectStroke` exists for.</summary>
    [AvaloniaFact]
    public void ChoosingABrushLandsOnTheInspectedStrokeOfEverySelectedPath()
    {
        (StrokePane pane, _, PathItem first, PathItem second) = Host(1, new[] { "Chisel" }, Stroke(4), Stroke(8));

        Combo(pane, "StrokeBrushBox").SelectedItem = "Chisel";
        Settle();

        Assert.Equal("Chisel", first.Strokes[1].Brush?.Name);
        Assert.Equal("Chisel", second.Strokes[1].Brush?.Name);
        Assert.Null(first.Strokes[0].Brush);
        Assert.Null(second.Strokes[0].Brush);
    }

    /// <summary>Choosing "(none)" takes the brush off and leaves the stroke's own width - the brush modulates the
    /// stroke rather than replacing it, so clearing it is not a delete of the stroke.</summary>
    [AvaloniaFact]
    public void ChoosingNoneClearsTheBrushAndKeepsTheWidth()
    {
        (StrokePane pane, EditorViewModel viewModel, PathItem path) = One(0, new[] { "Chisel" }, Stroke(9));
        EditorOperations.Invoke(new AutomationContext { ViewModel = viewModel }, "brush.apply",
            Params(new { name = "Chisel", strokeIndex = 0 }));
        Settle();

        Combo(pane, "StrokeBrushBox").SelectedItem = "(none)";
        Settle();

        Assert.Null(path.Strokes[0].Brush);
        Assert.Equal(9.0, path.Strokes[0].Width, 6);

        // And the asset itself is untouched: clearing a use is not deleting the brush.
        Assert.NotNull(viewModel.Document.FindBrush("Chisel"));
    }

    /// <summary>A selection that disagrees about the brush says **mixed**, rather than showing one path's brush as
    /// everyone's.</summary>
    [AvaloniaFact]
    public void TheBrushIsMixedWhenTheSelectionDisagrees()
    {
        var viewModel = new EditorViewModel();
        CreateBrush(viewModel, "Chisel");
        BrushSpec chisel = viewModel.Document.FindBrush("Chisel")!;

        PathItem first = Line(viewModel, Stroke(4) with { Brush = chisel });
        PathItem second = Line(viewModel, Stroke(4));
        viewModel.SelectObject(first);
        viewModel.ToggleObjectSelection(second);
        viewModel.InspectedStroke = 0;

        var pane = new StrokePane();
        pane.Attach(viewModel);
        var window = new Window { Width = 460, Height = 900, Content = pane };
        window.Show();
        Settle();

        Assert.Equal(-1, Combo(pane, "StrokeBrushBox").SelectedIndex);
        Assert.Equal("mixed", Combo(pane, "StrokeBrushBox").PlaceholderText);
        Assert.False(Visible(pane, "BrushSummary"));
        Assert.Contains("brush", Text(pane, "MixedLabel").Text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Two paths with the same brush agree, and agreement is not reported as mixed.</summary>
    [AvaloniaFact]
    public void TheSameBrushOnEveryPathIsNotMixed()
    {
        var viewModel = new EditorViewModel();
        CreateBrush(viewModel, "Chisel");
        BrushSpec chisel = viewModel.Document.FindBrush("Chisel")!;

        PathItem first = Line(viewModel, Stroke(4) with { Brush = chisel });
        PathItem second = Line(viewModel, Stroke(4) with { Brush = chisel });
        viewModel.SelectObject(first);
        viewModel.ToggleObjectSelection(second);
        viewModel.InspectedStroke = 0;

        var pane = new StrokePane();
        pane.Attach(viewModel);
        var window = new Window { Width = 460, Height = 900, Content = pane };
        window.Show();
        Settle();

        Assert.Equal("Chisel", Combo(pane, "StrokeBrushBox").SelectedItem);
        Assert.True(Visible(pane, "BrushSummary"));
    }

    // ---------------------------------------------------------------- parity: the control and the operation

    /// <summary>
    /// **The acceptance for this panel.** Driving the control and invoking the operation the control stands for
    /// leave **the same model state**, and reading the model back shows what the control shows.
    ///
    /// The two are run one after the other on one document, undoing between them, so the comparison is between two
    /// model values rather than between two documents that were built the same way. Undo is also the honest reset:
    /// it is the gesture a person would use to get back to the state the operation is about to produce.
    /// </summary>
    [AvaloniaFact]
    public void DrivingTheControlAndInvokingTheOperationLeaveTheSameModelState()
    {
        (StrokePane pane, EditorViewModel viewModel, PathItem path) = One(1, new[] { "Chisel" }, Stroke(4), Stroke(8));

        // By hand.
        Combo(pane, "StrokeBrushBox").SelectedItem = "Chisel";
        Settle();
        StrokeSpec byControl = path.Strokes[1];

        // Reading the model back shows what the control shows.
        Assert.Equal("Chisel", byControl.Brush?.Name);
        Assert.Equal("Chisel", Combo(pane, "StrokeBrushBox").SelectedItem);
        Assert.Equal(byControl.Brush, viewModel.Document.FindBrush("Chisel"));

        viewModel.Undo();
        Settle();
        Assert.Null(path.Strokes[1].Brush);

        // By operation - the driver's half, on the same selection and the same inspected index.
        EditorOperations.Invoke(new AutomationContext { ViewModel = viewModel }, "brush.apply",
            Params(new { name = "Chisel", strokeIndex = 1 }));

        // One model state, member for member: not "both have a brush called Chisel" but the whole stroke.
        Assert.Equal(byControl, path.Strokes[1]);
    }

    /// <summary>The clearing half of the same parity: "(none)" and `brush.clear` are one act.</summary>
    [AvaloniaFact]
    public void ClearingByTheControlAndByTheOperationLeaveTheSameModelState()
    {
        (StrokePane pane, EditorViewModel viewModel, PathItem path) = One(1, new[] { "Chisel" }, Stroke(4), Stroke(8));
        var context = new AutomationContext { ViewModel = viewModel };

        EditorOperations.Invoke(context, "brush.apply", Params(new { name = "Chisel", strokeIndex = 1 }));
        Settle();
        Assert.Equal("Chisel", path.Strokes[1].Brush?.Name);

        Combo(pane, "StrokeBrushBox").SelectedItem = "(none)";
        Settle();
        StrokeSpec byControl = path.Strokes[1];
        Assert.Null(byControl.Brush);

        viewModel.Undo();
        Settle();

        EditorOperations.Invoke(context, "brush.clear", Params(new { strokeIndex = 1 }));
        Assert.Equal(byControl, path.Strokes[1]);
    }

    /// <summary>What the operation **reports** is what the control shows - the read half of the parity, through
    /// `style.strokes` rather than through the control's own item list.</summary>
    [AvaloniaFact]
    public void TheOperationReportsTheBrushTheControlShows()
    {
        var viewModel = new EditorViewModel();
        CreateBrush(viewModel, "Chisel", angle: 35, roundness: 0.2, diameter: 24);
        PathItem path = Line(viewModel, Stroke(4));
        viewModel.SelectObject(path);
        viewModel.InspectedStroke = 0;

        var pane = new StrokePane();
        pane.Attach(viewModel);
        var window = new Window { Width = 460, Height = 900, Content = pane };
        window.Show();
        Settle();

        Combo(pane, "StrokeBrushBox").SelectedItem = "Chisel";
        Settle();

        JsonElement strokes = JsonSerializer.SerializeToElement(
            EditorOperations.Invoke(new AutomationContext { ViewModel = viewModel }, "style.strokes", default));

        JsonElement brush = strokes[0].GetProperty("strokes")[0].GetProperty("brush");
        Assert.Equal(Combo(pane, "StrokeBrushBox").SelectedItem, brush.GetProperty("name").GetString());
        Assert.Equal(35.0, brush.GetProperty("angle").GetDouble(), 6);
        Assert.Equal(0.2, brush.GetProperty("roundness").GetDouble(), 6);
        Assert.Equal(24.0, brush.GetProperty("diameter").GetDouble(), 6);
    }

    /// <summary>One gesture is one undo step, so a brush choice can be taken back in one move.</summary>
    [AvaloniaFact]
    public void ChoosingABrushIsOneUndoStep()
    {
        (StrokePane pane, EditorViewModel viewModel, PathItem path) = One(1, new[] { "Chisel" }, Stroke(4), Stroke(8));

        Combo(pane, "StrokeBrushBox").SelectedItem = "Chisel";
        Settle();

        viewModel.Undo();

        Assert.Null(path.Strokes[1].Brush);
    }
}
