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
/// The filter panel, built from the declaration.
///
/// This is the requirement #133 is built around: the panel lists the document's filters and the graph each holds,
/// offers the primitives <see cref="FilterPrimitiveRegistry"/> declares, and builds one editor per parameter that
/// declaration gives a kind - so a parameter added to the declaration grows an editor without this pane changing.
/// The tests therefore ask the registry (and <c>filter.kinds</c>, which reports it) how many controls there should
/// be and **compare**, rather than naming them.
///
/// Every action the tests drive is a click or a commit on a control, and every one of those goes through the
/// operations, so what is asserted on is the model and the declaration - not the panel's own idea of either.
/// </summary>
public class FilterPaneTests
{
    private static (FilterPane Pane, EditorViewModel ViewModel) Host(params FilterSpec[] filters)
    {
        var viewModel = new EditorViewModel();

        // Before Attach: the pane reads the document when it attaches, so a filter added afterwards would be a
        // document the pane has not seen.
        foreach (FilterSpec filter in filters)
        {
            viewModel.Document.AddFilter(filter);
        }

        var pane = new FilterPane();
        pane.Attach(viewModel);

        var window = new Window { Width = 480, Height = 760, Content = pane };
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

    /// <summary>A two-step graph: a blur that names its answer, and an offset that reads it by name.</summary>
    private static FilterSpec Chain()
        => new("drop", new[]
        {
            FilterPrimitive.Blur(3.0, input: "SourceGraphic", result: "soft"),
            FilterPrimitive.OffsetBy(2, 3, input: "soft", result: "moved"),
        });

    /// <summary>A graph with a two-input step, so a choice and both buffers are reachable.</summary>
    private static FilterSpec Graph()
        => new("drop", new[]
        {
            FilterPrimitive.Blur(3.0, input: "SourceGraphic", result: "soft"),
            FilterPrimitive.OffsetBy(2, 3, input: "soft", result: "moved"),
            FilterPrimitive.Combine("over", "moved", "SourceAlpha", "out"),
        });

    private static ListBox List(FilterPane pane, string name)
        => pane.FindControl<ListBox>(name) ?? throw new Xunit.Sdk.XunitException($"no {name}");

    private static TextBox Box(FilterPane pane, string name)
        => pane.FindControl<TextBox>(name) ?? throw new Xunit.Sdk.XunitException($"no {name}");

    private static StackPanel Editors(FilterPane pane)
        => pane.FindControl<StackPanel>("PrimitiveParameters")
           ?? throw new Xunit.Sdk.XunitException("no editor panel");

    /// <summary>
    /// The generated editors, found through the visual tree because a control inside an item template is not in the
    /// pane's namescope. The declaration's name travels on each control's Tag.
    /// </summary>
    private static List<Control> EditorsOf(FilterPane pane)
        => Editors(pane).GetVisualDescendants().OfType<Control>().Where(control => control.Tag is string).ToList();

    private static Control EditorFor(FilterPane pane, string parameter)
        => EditorsOf(pane).SingleOrDefault(control => (string?)control.Tag == parameter)
           ?? throw new Xunit.Sdk.XunitException($"no editor for '{parameter}'");

    private static void Click(FilterPane pane, string name)
    {
        Button button = pane.FindControl<Button>(name) ?? throw new Xunit.Sdk.XunitException($"no {name}");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Settle();
    }

    /// <summary>Selects a primitive row, which is what builds that step's editors.</summary>
    private static void Select(FilterPane pane, int index)
    {
        List(pane, "PrimitiveList").SelectedIndex = index;
        Settle();
    }

    /// <summary>Selects a filter row, which is what fills the primitive list.</summary>
    private static void SelectFilter(FilterPane pane, int index)
    {
        List(pane, "FilterList").SelectedIndex = index;
        Settle();
    }

    private static void Commit(Control editor, string text)
    {
        if (editor is ComboBox combo)
        {
            combo.SelectedItem = text;
        }
        else
        {
            ((TextBox)editor).Text = text;
            editor.RaiseEvent(new RoutedEventArgs(InputElement.LostFocusEvent));
        }

        Settle();
    }

    private static string Message(FilterPane pane)
        => pane.FindControl<TextBlock>("FilterMessage")?.Text ?? string.Empty;

    private static string[] KindsFilterKindsReports(EditorViewModel viewModel)
    {
        JsonElement reported = JsonSerializer.SerializeToElement(EditorOperations.Invoke(
            new AutomationContext { ViewModel = viewModel }, "filter.kinds", default));

        return reported.EnumerateArray().Select(entry => entry.GetProperty("kind").GetString()!).ToArray();
    }

    // ---------------------------------------------------------------- the library, and the graph

    /// <summary>The pane lists the document's filters, and describes the first one.</summary>
    [AvaloniaFact]
    public void ThePaneListsTheDocumentsFilters()
    {
        (FilterPane pane, _) = Host(Chain(), new FilterSpec("glow", new[] { FilterPrimitive.Blur(1, result: "g") }));

        Assert.Equal(2, List(pane, "FilterList").ItemCount);
        Assert.Equal(0, List(pane, "FilterList").SelectedIndex);
        Assert.Equal(2, List(pane, "PrimitiveList").ItemCount);
    }

    /// <summary>And follows the document when a filter is added through the operations rather than by this pane.</summary>
    [AvaloniaFact]
    public void TheFilterListFollowsTheDocument()
    {
        (FilterPane pane, EditorViewModel viewModel) = Host();

        Assert.Equal(0, List(pane, "FilterList").ItemCount);

        EditorOperations.Invoke(
            new AutomationContext { ViewModel = viewModel },
            "filter.create",
            JsonSerializer.SerializeToElement(new
            {
                name = "drop",
                primitives = new object[] { new { kind = "gaussianBlur", radius = 1.0, result = "soft" } },
            }));
        Settle();

        Assert.Equal(1, List(pane, "FilterList").ItemCount);
        Assert.Equal(1, List(pane, "PrimitiveList").ItemCount);
    }

    /// <summary>
    /// **The Add control offers exactly the primitives the declaration has** - and that is the list
    /// <c>filter.kinds</c> reports, which is what a driver reads to learn the same thing.
    /// </summary>
    [AvaloniaFact]
    public void TheKindsOfferedAreTheOnesFilterKindsReports()
    {
        (FilterPane pane, EditorViewModel viewModel) = Host();

        ComboBox kinds = pane.FindControl<ComboBox>("AddKindBox") ?? throw new Xunit.Sdk.XunitException("no kinds");

        Assert.Equal(KindsFilterKindsReports(viewModel), kinds.Items.Cast<string>().ToArray());
        Assert.Equal(FilterPrimitiveRegistry.All.Select(definition => definition.Kind).ToArray(),
            kinds.Items.Cast<string>().ToArray());
    }

    // ---------------------------------------------------------------- the editors, from the declaration

    /// <summary>
    /// **Each step's editors are exactly the parameters its kind declares** - including the buffers, which are the
    /// wiring rather than values.
    /// </summary>
    [AvaloniaFact]
    public void TheEditorsMatchWhatTheRegistryDeclares()
    {
        FilterSpec graph = Graph();
        (FilterPane pane, _) = Host(graph);

        for (int index = 0; index < graph.Primitives.Count; index++)
        {
            Select(pane, index);

            // Counted from the declaration rather than written here, so a kind that declares a new parameter grows
            // a control without this test being told about it - which is the property the panel exists to have.
            string[] declared = FilterPrimitiveRegistry.All
                .Single(definition => definition.ModelKind == graph.Primitives[index].Kind)
                .Parameters.Select(parameter => parameter.Name).OrderBy(name => name).ToArray();

            string[] shown = EditorsOf(pane).Select(control => (string)control.Tag!).OrderBy(name => name).ToArray();

            Assert.Equal(declared, shown);
        }
    }

    /// <summary>A choice is a list of the declared words; everything else is a field, and a buffer is a name.</summary>
    [AvaloniaFact]
    public void AnEditorIsTheShapeItsKindCallsFor()
    {
        (FilterPane pane, _) = Host(Graph());

        Select(pane, 2);

        Assert.IsType<ComboBox>(EditorFor(pane, "operator"));
        Assert.IsType<TextBox>(EditorFor(pane, "in"));
        Assert.IsType<TextBox>(EditorFor(pane, "in2"));
        Assert.IsType<TextBox>(EditorFor(pane, "result"));

        // The declared words, in the declaration's order, rather than a list written in the test.
        FilterParameter declared = FilterPrimitiveRegistry.Find("composite")!.Parameter("operator")!;
        Assert.Equal(declared.Choices!, ((ComboBox)EditorFor(pane, "operator")).Items.Cast<string>().ToArray());

        // And the value shown is the step's own, not the declaration's default.
        Assert.Equal("over", ((ComboBox)EditorFor(pane, "operator")).SelectedItem);

        Select(pane, 0);
        Assert.IsType<TextBox>(EditorFor(pane, "radius"));
        Assert.Equal("3", ((TextBox)EditorFor(pane, "radius")).Text);
    }

    /// <summary>
    /// **Every kind the declaration has gets one editor per parameter it declares.**
    ///
    /// Built from the model rather than through <c>filter.addPrimitive</c>, because what is pinned here is the
    /// panel's generation and not the operation's reach: the step is placed in the document and the panel is asked
    /// what its kind takes, which is the property a new primitive inherits by declaring itself.
    /// </summary>
    [AvaloniaFact]
    public void EveryDeclaredKindGetsAnEditorPerParameter()
    {
        FilterPrimitiveDefinition[] definitions = FilterPrimitiveRegistry.All.ToArray();
        var spec = new FilterSpec("kinds", definitions.Select((definition, i) => Sample(definition, i)).ToArray());
        (FilterPane pane, _) = Host(spec);

        for (int index = 0; index < definitions.Length; index++)
        {
            Select(pane, index);

            string[] declared = definitions[index].Parameters
                .Select(parameter => parameter.Name).OrderBy(name => name).ToArray();
            string[] shown = EditorsOf(pane).Select(control => (string)control.Tag!).OrderBy(name => name).ToArray();

            Assert.Equal(declared, shown);
        }
    }

    /// <summary>One step of every kind, made the way the model's own factories make one.</summary>
    private static FilterPrimitive Sample(FilterPrimitiveDefinition definition, int index) => definition.ModelKind switch
    {
        FilterPrimitiveKind.GaussianBlur => FilterPrimitive.Blur(2, result: $"r{index}"),
        FilterPrimitiveKind.Offset => FilterPrimitive.OffsetBy(1, 1, result: $"r{index}"),
        FilterPrimitiveKind.Flood => FilterPrimitive.Solid(ColorRgb.Black, 1, $"r{index}"),
        FilterPrimitiveKind.Composite => FilterPrimitive.Combine("over", "SourceGraphic", "SourceAlpha", $"r{index}"),
        FilterPrimitiveKind.Blend => FilterPrimitive.Blended("normal", "SourceGraphic", "SourceAlpha", $"r{index}"),
        FilterPrimitiveKind.Morphology => FilterPrimitive.Morph("erode", 1, result: $"r{index}"),
        FilterPrimitiveKind.ColorMatrix => FilterPrimitive.ColourMatrix(
            FilterPrimitive.IdentityMatrix, "matrix", result: $"r{index}"),
        FilterPrimitiveKind.DisplacementMap => FilterPrimitive.Displace(
            1, "A", "A", "SourceGraphic", "SourceAlpha", $"r{index}"),
        FilterPrimitiveKind.Turbulence => FilterPrimitive.Noise("turbulence", 0.05, 1, 0, result: $"r{index}"),
        FilterPrimitiveKind.SpecularLighting => FilterPrimitive.Specular(1, 1, 1, ColorRgb.White, result: $"r{index}"),
        _ => FilterPrimitive.Diffuse(1, 1, ColorRgb.White, result: $"r{index}"),
    };

    /// <summary>A step with no parameters declared builds no editors, because there is nothing to edit.</summary>
    [AvaloniaFact]
    public void AFilterWithNoPrimitiveBuildsNoEditors()
    {
        (FilterPane pane, _) = Host(new FilterSpec("empty", Array.Empty<FilterPrimitive>()));

        Assert.Empty(Editors(pane).Children);
    }

    // ---------------------------------------------------------------- adding, removing

    /// <summary>**Adding from the panel lands the step in the model, at the index given, as the kind chosen.**</summary>
    [AvaloniaFact]
    public void AddingAPrimitiveLandsItAtTheIndexWithTheKindChosen()
    {
        (FilterPane pane, EditorViewModel viewModel) = Host(new FilterSpec("drop", new[]
        {
            FilterPrimitive.Blur(3.0, input: "SourceGraphic", result: "soft"),
        }));

        ComboBox kinds = pane.FindControl<ComboBox>("AddKindBox")!;
        kinds.SelectedItem = "offset";
        Box(pane, "AddIndexBox").Text = "0";
        Click(pane, "AddPrimitiveButton");

        FilterSpec filter = viewModel.Document.FindFilter("drop")!;
        Assert.Equal(2, filter.Primitives.Count);
        Assert.Equal(FilterPrimitiveKind.Offset, filter.Primitives[0].Kind);

        // The step that was there is still there, one place later.
        Assert.Equal(FilterPrimitiveKind.GaussianBlur, filter.Primitives[1].Kind);
        Assert.Equal("soft", filter.Primitives[1].Result);

        // An empty insertion point appends, which is what the operation does with no index at all.
        Box(pane, "AddIndexBox").Text = string.Empty;
        kinds.SelectedItem = "flood";
        Click(pane, "AddPrimitiveButton");

        filter = viewModel.Document.FindFilter("drop")!;
        Assert.Equal(3, filter.Primitives.Count);
        Assert.Equal(FilterPrimitiveKind.Flood, filter.Primitives[2].Kind);
    }

    /// <summary>
    /// **Removing takes the primitive that is selected**, and a step another step reads is refused rather than
    /// removed - with the operation's reason shown, because the picture would change somewhere the edit does not
    /// point at.
    /// </summary>
    [AvaloniaFact]
    public void RemovingTakesTheSelectedPrimitiveAndRefusesAConsumedOne()
    {
        (FilterPane pane, EditorViewModel viewModel) = Host(Chain());

        Select(pane, 0);
        Click(pane, "RemovePrimitiveButton");

        Assert.Contains("nothing makes", Message(pane), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, viewModel.Document.FindFilter("drop")!.Primitives.Count);

        Select(pane, 1);
        Click(pane, "RemovePrimitiveButton");

        FilterSpec filter = viewModel.Document.FindFilter("drop")!;
        Assert.Equal("", Message(pane));
        Assert.Single(filter.Primitives);
        Assert.Equal(FilterPrimitiveKind.GaussianBlur, filter.Primitives[0].Kind);
        Assert.Equal(3.0, filter.Primitives[0].Radius, 6);
        Assert.Equal("soft", filter.Primitives[0].Result);
    }

    /// <summary>A new filter is made of the kind the Add control names, with the declaration's own required values.</summary>
    [AvaloniaFact]
    public void CreatingAFilterMakesOneOfTheChosenKind()
    {
        (FilterPane pane, EditorViewModel viewModel) = Host();

        Box(pane, "NewFilterNameBox").Text = "glow";
        pane.FindControl<ComboBox>("AddKindBox")!.SelectedItem = "offset";
        Click(pane, "CreateFilterButton");

        FilterSpec filter = viewModel.Document.FindFilter("glow")!;
        Assert.Single(filter.Primitives);
        Assert.Equal(FilterPrimitiveKind.Offset, filter.Primitives[0].Kind);
        Assert.Equal(1, List(pane, "FilterList").ItemCount);
    }

    /// <summary>A filter with no name is refused by the panel, in words, rather than creating one called "" .</summary>
    [AvaloniaFact]
    public void CreatingAFilterWithNoNameIsRefused()
    {
        (FilterPane pane, EditorViewModel viewModel) = Host();

        Click(pane, "CreateFilterButton");

        Assert.Contains("needs a name", Message(pane), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(viewModel.Document.Filters);
    }

    [AvaloniaFact]
    public void DeletingTakesTheSelectedFilter()
    {
        (FilterPane pane, EditorViewModel viewModel) = Host(Chain());

        Click(pane, "DeleteFilterButton");

        Assert.Null(viewModel.Document.FindFilter("drop"));
        Assert.Equal(0, List(pane, "FilterList").ItemCount);
    }

    /// <summary>
    /// Applying draws the selection through the filter, and clearing takes it off again - both halves of
    /// <c>filter.apply</c>, because an empty name is a request of its own rather than a missing one.
    /// </summary>
    [AvaloniaFact]
    public void ApplyingAndClearingSetTheFiltersReferenceOnTheSelection()
    {
        var viewModel = new EditorViewModel();
        var path = new PathItem { Name = "shape", Fill = FillSpec.Solid(ColorRgb.Black) };
        SubPath sub = path.AddSubPath(closed: true);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(10, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(10, 10)));
        path.Strokes.Clear();
        path.Strokes.Add(StrokeSpec.None);
        viewModel.Document.Artboards[0].Layers[0].AddItem(path);
        viewModel.Document.AddFilter(Chain());
        viewModel.SelectObject(path);

        var pane = new FilterPane();
        pane.Attach(viewModel);
        var window = new Window { Width = 480, Height = 760, Content = pane };
        window.Show();
        Settle();

        Click(pane, "ApplyFilterButton");
        Assert.Equal("drop", path.FilterId);

        Click(pane, "ClearFilterButton");
        Assert.Null(path.FilterId);
    }

    // ---------------------------------------------------------------- editing values, through the operations

    /// <summary>**Editing a number writes the model**, through the operation the driver would call.</summary>
    [AvaloniaFact]
    public void EditingANumberWritesTheModel()
    {
        (FilterPane pane, EditorViewModel viewModel) = Host(Chain());

        Select(pane, 0);
        Commit(EditorFor(pane, "radius"), "9");

        Assert.Equal(9.0, viewModel.Document.FindFilter("drop")!.Primitives[0].Radius, 6);
    }

    /// <summary>A choice writes the model by the word chosen from the declaration's own list.</summary>
    [AvaloniaFact]
    public void EditingAChoiceWritesTheModel()
    {
        (FilterPane pane, EditorViewModel viewModel) = Host(Graph());

        Select(pane, 2);
        Commit(EditorFor(pane, "operator"), "xor");

        Assert.Equal("xor", viewModel.Document.FindFilter("drop")!.Primitives[2].Operator);
    }

    /// <summary>A colour is written as bytes, the way every operation takes one.</summary>
    [AvaloniaFact]
    public void EditingAColourWritesTheModel()
    {
        (FilterPane pane, EditorViewModel viewModel) = Host(new FilterSpec("ink", new[]
        {
            FilterPrimitive.Solid(ColorRgb.Black, 1.0, "wash"),
        }));

        Select(pane, 0);
        Assert.Equal("0,0,0", ((TextBox)EditorFor(pane, "floodColor")).Text);

        Commit(EditorFor(pane, "floodColor"), "0,128,64");

        Assert.Equal(
            ColorRgb.FromBytes(0, 128, 64),
            viewModel.Document.FindFilter("ink")!.Primitives[0].FloodColor);
    }

    /// <summary>
    /// **A buffer is wiring, not a value**: editing it by name calls <c>filter.connectPrimitive</c>, and the graph
    /// the operation validates is the thing that decides whether the name is one it can read.
    /// </summary>
    [AvaloniaFact]
    public void EditingAWiringNameCallsConnectPrimitive()
    {
        (FilterPane pane, EditorViewModel viewModel) = Host(Chain());

        Select(pane, 1);
        Assert.Equal("soft", ((TextBox)EditorFor(pane, "in")).Text);

        Commit(EditorFor(pane, "in"), "SourceAlpha");

        FilterSpec filter = viewModel.Document.FindFilter("drop")!;
        Assert.Equal("SourceAlpha", filter.Primitives[1].Input);
        Assert.Equal("", Message(pane));

        Commit(EditorFor(pane, "result"), "shifted");
        Assert.Equal("shifted", viewModel.Document.FindFilter("drop")!.Primitives[1].Result);
    }

    /// <summary>A buffer naming something nothing makes is refused with the reason, and the graph is untouched.</summary>
    [AvaloniaFact]
    public void AWiringNameNothingProducesIsRefusedAndSaid()
    {
        (FilterPane pane, EditorViewModel viewModel) = Host(Chain());

        Select(pane, 1);
        Commit(EditorFor(pane, "in"), "ghost");

        Assert.Contains("ghost", Message(pane), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("anything produces", Message(pane), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("soft", viewModel.Document.FindFilter("drop")!.Primitives[1].Input);
    }

    /// <summary>
    /// **A refusal surfaces rather than being swallowed.** A radius below the declared minimum is refused by the
    /// operation, the reason is shown, and the value the model holds is the one it already had.
    /// </summary>
    [AvaloniaFact]
    public void ARefusedValueIsSaidRatherThanIgnored()
    {
        (FilterPane pane, EditorViewModel viewModel) = Host(Chain());

        Select(pane, 0);
        Commit(EditorFor(pane, "radius"), "-1");

        Assert.Contains("at least 0", Message(pane), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3.0, viewModel.Document.FindFilter("drop")!.Primitives[0].Radius, 6);
    }

    /// <summary>A value that is not a number at all is refused by the panel in words, without calling anything.</summary>
    [AvaloniaFact]
    public void AValueThatIsNotANumberIsSaidRatherThanSent()
    {
        (FilterPane pane, EditorViewModel viewModel) = Host(Chain());

        Select(pane, 0);
        Commit(EditorFor(pane, "radius"), "wide");

        Assert.Contains("takes a number", Message(pane), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3.0, viewModel.Document.FindFilter("drop")!.Primitives[0].Radius, 6);
    }

    // ---------------------------------------------------------------- the region, and the world outside the graph

    /// <summary>The filter's own settings are editable here too, because a driver can edit them.</summary>
    [AvaloniaFact]
    public void TheRegionFieldsWriteTheFilter()
    {
        (FilterPane pane, EditorViewModel viewModel) = Host(Chain());

        Box(pane, "RegionWidthBox").Text = "2";
        Box(pane, "RegionWidthBox").RaiseEvent(new RoutedEventArgs(InputElement.LostFocusEvent));
        Settle();

        Assert.Equal(2.0, viewModel.Document.FindFilter("drop")!.Width, 6);

        Box(pane, "OutputBox").Text = "soft";
        Box(pane, "OutputBox").RaiseEvent(new RoutedEventArgs(InputElement.LostFocusEvent));
        Settle();

        Assert.Equal("soft", viewModel.Document.FindFilter("drop")!.Output);
    }

    /// <summary>A region with no area is refused by the operation, with its reason shown.</summary>
    [AvaloniaFact]
    public void ARegionWithNoAreaIsRefused()
    {
        (FilterPane pane, EditorViewModel viewModel) = Host(Chain());

        Box(pane, "RegionHeightBox").Text = "0";
        Box(pane, "RegionHeightBox").RaiseEvent(new RoutedEventArgs(InputElement.LostFocusEvent));
        Settle();

        Assert.Contains("no area", Message(pane), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1.2, viewModel.Document.FindFilter("drop")!.Height, 6);
    }

    // ---------------------------------------------------------------- the gap

    /// <summary>
    /// **Reordering a step is the one thing the panel cannot do, and it says so.**
    ///
    /// There is no <c>filter.movePrimitive</c> in the registry - <c>style.reorderStrokeEffect</c> is the shape such
    /// an operation takes - so the buttons are off rather than pretending, and a person and a driver are equally
    /// unable to reorder a step. This test is the sentinel: the day the operation exists, it should fail.
    /// </summary>
    [AvaloniaFact]
    public void ReorderingIsNotOfferedBecauseNoOperationMovesAStep()
    {
        (FilterPane pane, _) = Host(Graph());

        Assert.False(EditorOperations.TryGet("filter.movePrimitive", out _));
        Assert.False(pane.FindControl<Button>("MovePrimitiveUpButton")!.IsEnabled);
        Assert.False(pane.FindControl<Button>("MovePrimitiveDownButton")!.IsEnabled);
        Assert.Contains("filter.movePrimitive", pane.FindControl<TextBlock>("MoveNote")!.Text!, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- selecting another filter

    /// <summary>Selecting another filter describes that filter's graph, and not the one before it.</summary>
    [AvaloniaFact]
    public void SelectingAnotherFilterDescribesItsOwnGraph()
    {
        (FilterPane pane, _) = Host(
            Chain(),
            new FilterSpec("glow", new[] { FilterPrimitive.Blur(1.0, result: "g") }));

        SelectFilter(pane, 1);

        Assert.Equal(1, List(pane, "PrimitiveList").ItemCount);

        Select(pane, 0);
        Assert.Equal("1", ((TextBox)EditorFor(pane, "radius")).Text);
        Assert.Equal("g", ((TextBox)EditorFor(pane, "result")).Text);
    }
}
