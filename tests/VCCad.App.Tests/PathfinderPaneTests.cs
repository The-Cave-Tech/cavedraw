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
/// The Pathfinder panel: the five boolean operations as buttons a person can press.
///
/// The panel is a **way in**, not a second implementation - every button calls the session method the
/// operation also calls - so what is tested here is the wiring: that the buttons exist by name, that
/// they disable when the selection cannot use them, and that pressing one does what the operation does.
/// A capability that exists only inside a click handler is a defect in this codebase, and this is where
/// that is pinned for these operations.
/// </summary>
public class PathfinderPaneTests
{
    private static (PathfinderPane Pane, EditorViewModel ViewModel) Host()
    {
        var viewModel = new EditorViewModel();
        var pane = new PathfinderPane();
        pane.Attach(viewModel);

        var window = new Window { Width = 600, Height = 400, Content = pane };
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

    private static PathItem Square(EditorViewModel viewModel, double x, double y, double size)
    {
        PathItem path = PathFactory.CreateRectangle("square", new Rect2D(x, y, size, size));
        path.Fill = FillSpec.Solid(ColorRgb.Black);
        viewModel.Document.Artboards[0].Layers[0].AddItem(path);
        return path;
    }

    private static double Area(PathItem path)
        => PathFlattener.Flatten(path).Sum(o => Math.Abs(o.SignedArea));

    /// <summary>Every operation the panel offers is a button with a name, findable without pixels.</summary>
    [AvaloniaFact]
    public void TheOperationsAreNamedButtons()
    {
        (PathfinderPane pane, _) = Host();

        string[] names = pane.Buttons.Select(b => b.Name).ToArray();

        Assert.Contains("PathfinderUnion", names);
        Assert.Contains("PathfinderSubtract", names);
        Assert.Contains("PathfinderIntersect", names);
        Assert.Contains("PathfinderExclude", names);
        Assert.Contains("PathfinderDivide", names);
        Assert.Contains("PathfinderRelease", names);
        Assert.Contains("PathfinderReverse", names);
    }

    /// <summary>
    /// With nothing selected the boolean buttons are disabled and their tooltip says what is needed -
    /// a button that is present and does nothing is worse than one that explains itself.
    /// </summary>
    [AvaloniaFact]
    public void WithNothingSelectedTheButtonsAreDisabledAndSayWhy()
    {
        (PathfinderPane pane, _) = Host();

        Assert.All(pane.Buttons, button => Assert.False(button.Enabled));
        Assert.Contains("Select at least 2", pane.TipFor("PathfinderUnion"));
    }

    /// <summary>Two paths selected is what a boolean needs, and that is what enables them.</summary>
    [AvaloniaFact]
    public void SelectingTwoPathsEnablesTheBooleanButtons()
    {
        (PathfinderPane pane, EditorViewModel viewModel) = Host();

        PathItem a = Square(viewModel, 0, 0, 100);
        PathItem b = Square(viewModel, 50, 50, 100);
        viewModel.SelectRange(new LayerItem[] { a, b }, additive: false);
        Settle();

        Assert.True(pane.Buttons.Single(x => x.Name == "PathfinderUnion").Enabled);
        Assert.True(pane.Buttons.Single(x => x.Name == "PathfinderDivide").Enabled);
    }

    /// <summary>A single path cannot be united with anything, so the button stays disabled.</summary>
    [AvaloniaFact]
    public void SelectingOnePathLeavesTheBooleanButtonsDisabled()
    {
        (PathfinderPane pane, EditorViewModel viewModel) = Host();

        PathItem only = Square(viewModel, 0, 0, 100);
        viewModel.SelectRange(new[] { (LayerItem)only }, additive: false);
        Settle();

        Assert.False(pane.Buttons.Single(x => x.Name == "PathfinderUnion").Enabled);
    }

    /// <summary>
    /// Pressing Union does what the operation does: the two squares become one object whose area is the
    /// union - which is the check that the panel is wired to the real thing and not to a copy of it.
    /// </summary>
    [AvaloniaFact]
    public void PressingUnionProducesTheUnion()
    {
        (PathfinderPane pane, EditorViewModel viewModel) = Host();

        PathItem a = Square(viewModel, 0, 0, 100);
        PathItem b = Square(viewModel, 50, 50, 100);
        viewModel.SelectRange(new LayerItem[] { a, b }, additive: false);
        Settle();

        Button button = Descendants(pane)
            .OfType<Button>()
            .First(candidate => candidate.Name == "PathfinderUnion");

        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Settle();

        List<PathItem> paths = viewModel.Document.Artboards[0].Layers[0].Children.OfType<PathItem>().ToList();

        PathItem result = Assert.Single(paths);
        Assert.Equal(17500, Area(result), 0);
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
