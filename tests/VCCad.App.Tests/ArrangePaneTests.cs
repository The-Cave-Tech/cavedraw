using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VCCad.App.ViewModels;
using VCCad.App.Views;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// The Align panel: the six alignments and the distributions as buttons a person can press.
///
/// Tested on the **wiring** rather than the arithmetic, which the Core tests own: that the buttons exist
/// by name, that they disable when the selection cannot use them, and that pressing one arranges the
/// objects. A capability that exists only inside a click handler is a defect in this codebase.
/// </summary>
public class ArrangePaneTests
{
    private static (ArrangePane Pane, EditorViewModel ViewModel) Host()
    {
        var viewModel = new EditorViewModel();
        var pane = new ArrangePane();
        pane.Attach(viewModel);

        var window = new Window { Width = 600, Height = 500, Content = pane };
        window.Show();
        Settle();
        return (pane, viewModel);
    }

    private static void Settle()
    {
        for (int i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static PathItem Box(EditorViewModel viewModel, string name, double x, double y, double width, double height)
    {
        PathItem path = PathFactory.CreateRectangle(name, new Rect2D(x, y, width, height));
        path.Fill = FillSpec.Solid(ColorRgb.Black);
        viewModel.Document.Artboards[0].Layers[0].AddItem(path);
        return path;
    }

    /// <summary>Every arrangement this panel offers is a button with a name, findable without pixels.</summary>
    [AvaloniaFact]
    public void EveryArrangementIsANamedButton()
    {
        (ArrangePane pane, _) = Host();

        string[] names = pane.Buttons.Select(b => b.Name).ToArray();

        foreach (string expected in new[]
                 {
                     "AlignLeft", "AlignCentre", "AlignRight",
                     "AlignTop", "AlignMiddle", "AlignBottom",
                     "DistributeHorizontal", "DistributeHorizontalEnd",
                     "DistributeVertical", "DistributeVerticalEnd",
                 })
        {
            Assert.Contains(expected, names);
        }
    }

    /// <summary>
    /// With nothing selected every button is disabled and says what it needs: aligning needs two objects,
    /// distributing needs three, because two are already evenly spaced.
    /// </summary>
    [AvaloniaFact]
    public void WithNothingSelectedTheButtonsAreDisabledAndSayWhy()
    {
        (ArrangePane pane, _) = Host();

        Assert.All(pane.Buttons, button => Assert.False(button.Enabled));
        Assert.Equal("Select at least 2 object(s)", pane.TipFor("AlignLeft"));
        Assert.Equal("Select at least 3 object(s)", pane.TipFor("DistributeHorizontal"));
    }

    /// <summary>Two objects can be aligned but not distributed, which is the distinction the tips make.</summary>
    [AvaloniaFact]
    public void TwoObjectsEnableAligningButNotDistributing()
    {
        (ArrangePane pane, EditorViewModel viewModel) = Host();

        PathItem a = Box(viewModel, "a", 0, 0, 20, 20);
        PathItem b = Box(viewModel, "b", 100, 40, 20, 20);
        viewModel.SelectRange(new LayerItem[] { a, b }, additive: false);
        Settle();

        Assert.True(pane.Buttons.Single(x => x.Name == "AlignLeft").Enabled);
        Assert.False(pane.Buttons.Single(x => x.Name == "DistributeHorizontal").Enabled);
    }

    /// <summary>Three objects enable distributing, and pressing it really distributes.</summary>
    [AvaloniaFact]
    public void PressingDistributeEvenOutTheGaps()
    {
        (ArrangePane pane, EditorViewModel viewModel) = Host();

        PathItem a = Box(viewModel, "a", 0, 0, 20, 20);
        PathItem b = Box(viewModel, "b", 100, 0, 60, 20);
        PathItem c = Box(viewModel, "c", 220, 0, 40, 20);
        viewModel.SelectRange(new LayerItem[] { a, b, c }, additive: false);
        Settle();

        Button button = Descendants(pane)
            .OfType<Button>()
            .First(candidate => candidate.Name == "DistributeHorizontal");

        Assert.True(button.IsEnabled);
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Settle();

        double gap1 = ItemBounds.Of(b).X - (ItemBounds.Of(a).X + ItemBounds.Of(a).Width);
        double gap2 = ItemBounds.Of(c).X - (ItemBounds.Of(b).X + ItemBounds.Of(b).Width);

        Assert.Equal(gap1, gap2, 6);
        Assert.Equal(70, gap1, 6);
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        yield return parent;
        foreach (Control child in parent.GetVisualChildren().OfType<Control>())
        {
            foreach (Control descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }
}
