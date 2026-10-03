using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using VCCad.App.ViewModels;
using VCCad.App.Views.Panes;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// The symbol library panel (issue #135). Every test is about the **model** the panel left behind, never about a
/// control having been set: a button that only moved a list would pass a weaker assertion and do nothing. The counts
/// on the rows are the issue's own requirement, and a refusal has to reach the person rather than being a silent
/// no-op.
/// </summary>
public class SymbolsPaneTests
{
    private static (SymbolsPane Pane, EditorViewModel ViewModel) Host()
    {
        var viewModel = new EditorViewModel();
        var pane = new SymbolsPane();
        pane.Attach(viewModel);

        var window = new Window { Width = 420, Height = 700, Content = pane };
        window.Show();
        Settle();
        return (pane, viewModel);
    }

    private static void Settle() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();

    /// <summary>Selects a row the way a person does - through the list - so the pane's own selection state is set by the control.</summary>
    private static void Select(SymbolsPane pane, string name)
        => pane.FindControl<ListBox>("SymbolList")!.SelectedIndex = pane.ListedNames.ToList().IndexOf(name);

    private static void Click(SymbolsPane pane, string name)
        => pane.FindControl<Button>(name)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static PathItem Artwork(CadDocument document, double x)
    {
        var path = new PathItem { Name = "art", Fill = FillSpec.Solid(ColorRgb.Black) };
        SubPath sub = path.AddSubPath(closed: true);
        sub.Nodes.Add(new PathNode(new Point2D(x, 10)));
        sub.Nodes.Add(new PathNode(new Point2D(x + 20, 10)));
        sub.Nodes.Add(new PathNode(new Point2D(x + 20, 30)));
        sub.Nodes.Add(new PathNode(new Point2D(x, 30)));
        document.Artboards[0].Layers[0].AddItem(path);
        return path;
    }

    [AvaloniaFact]
    public void TheMarkerPickerSetsAndClearsAReferenceThroughTheOperation()
    {
        (SymbolsPane pane, EditorViewModel viewModel) = Host();

        // A marker definition and a path to give it to.
        ArtGroup definition = viewModel.Document.AddDefinition("arrow");
        definition.ForeignAttributes["markerWidth"] = "10";
        definition.ForeignAttributes["markerHeight"] = "10";
        definition.ForeignAttributes["refX"] = "5";
        definition.ForeignAttributes["refY"] = "5";

        var line = new PathItem { Name = "line", Stroke = StrokeSpec.Hairline(ColorRgb.Black) };
        SubPath sub = line.AddSubPath(closed: false);
        sub.Nodes.Add(new PathNode(new Point2D(20, 50)));
        sub.Nodes.Add(new PathNode(new Point2D(120, 50)));
        viewModel.Document.Artboards[0].Layers[0].AddItem(line);

        viewModel.SelectRange(new[] { line }, additive: false);
        Settle();
        pane.Refresh();

        // The picker offers the definition because it carries a marker's placement attributes, and its readout says
        // what the selected path currently names.
        Assert.Contains("arrow", pane.FindControl<ComboBox>("MarkerBox")!.Items.Cast<string>());
        Assert.Contains("end -", pane.MarkerSummary);

        pane.FindControl<ComboBox>("SlotBox")!.SelectedItem = "end";
        pane.FindControl<ComboBox>("MarkerBox")!.SelectedItem = "arrow";
        Click(pane, "SetMarkerButton");

        // The model changed **through the operation**, and the readout the person sees agrees with it.
        Assert.Equal("arrow", line.MarkerEnd);
        Assert.Contains("end arrow", pane.MarkerSummary);

        Click(pane, "ClearMarkerButton");
        Assert.Null(line.MarkerEnd);
        Assert.Contains("end -", pane.MarkerSummary);
    }

    [AvaloniaFact]
    public void TheCreateControlMakesADefinitionAndLeavesAnInstance()
    {
        (SymbolsPane pane, EditorViewModel viewModel) = Host();
        PathItem artwork = Artwork(viewModel.Document, 10);
        viewModel.SelectRange(new[] { artwork }, additive: false);
        Settle();

        pane.FindControl<TextBox>("NameBox")!.Text = "sym";
        Click(pane, "CreateButton");

        // The artwork is a definition, out of the layer, and an instance stands in its place - and the list the
        // person reads says one place uses it.
        ArtGroup definition = viewModel.Document.FindDefinition("sym")!;
        Assert.NotNull(definition);
        Assert.DoesNotContain(artwork, viewModel.Document.Artboards[0].Layers[0].Children);
        Assert.Single(viewModel.Document.AllGroups().Where(InstanceResolver.IsInstance));
        Assert.Contains("sym", pane.ListedNames);

        // The count a person reads is on the **row**, which is what the issue asks the panel to show. (The
        // assertion used to look for the count in the message line, which carries "sym created" - the test was
        // wrong about which string holds it, and the pane was right.)
        var list = pane.FindControl<ListBox>("SymbolList")!;
        string row = Assert.IsType<string>(Assert.Single(list.Items));
        Assert.Contains("sym", row);
        Assert.Contains("1 place", row);
    }

    [AvaloniaFact]
    public void ThePlaceControlAddsASecondInstanceAndTheCountFollows()
    {
        (SymbolsPane pane, EditorViewModel viewModel) = Host();
        PathItem artwork = Artwork(viewModel.Document, 10);
        viewModel.SelectRange(new[] { artwork }, additive: false);
        Settle();
        pane.FindControl<TextBox>("NameBox")!.Text = "sym";
        Click(pane, "CreateButton");

        // The list has the definition selected after a create, so Place is a single click.
        Select(pane, "sym");
        Click(pane, "PlaceButton");

        Assert.Equal(2, viewModel.Document.AllGroups().Count(InstanceResolver.IsInstance));
    }

    [AvaloniaFact]
    public void TheDeleteControlIsRefusedWhileTheDefinitionIsUsed()
    {
        (SymbolsPane pane, EditorViewModel viewModel) = Host();
        PathItem artwork = Artwork(viewModel.Document, 10);
        viewModel.SelectRange(new[] { artwork }, additive: false);
        Settle();
        pane.FindControl<TextBox>("NameBox")!.Text = "sym";
        Click(pane, "CreateButton");
        Select(pane, "sym");

        Click(pane, "DeleteButton");

        // Refused, said out loud, and the document is untouched - an instance whose definition can vanish is a
        // broken document.
        Assert.Contains("still used", pane.Message);
        Assert.NotNull(viewModel.Document.FindDefinition("sym"));
        Assert.Single(viewModel.Document.AllGroups().Where(InstanceResolver.IsInstance));
    }

    [AvaloniaFact]
    public void TheRenameControlCarriesTheInstancesWithIt()
    {
        (SymbolsPane pane, EditorViewModel viewModel) = Host();
        PathItem artwork = Artwork(viewModel.Document, 10);
        viewModel.SelectRange(new[] { artwork }, additive: false);
        Settle();
        pane.FindControl<TextBox>("NameBox")!.Text = "sym";
        Click(pane, "CreateButton");
        Select(pane, "sym");

        pane.FindControl<TextBox>("NameBox")!.Text = "symbol-a";
        Click(pane, "RenameButton");

        Assert.Null(viewModel.Document.FindDefinition("sym"));
        Assert.NotNull(viewModel.Document.FindDefinition("symbol-a"));
        Assert.All(
            viewModel.Document.AllGroups().Where(InstanceResolver.IsInstance),
            instance => Assert.Equal("symbol-a", instance.SourceId));
    }
}
