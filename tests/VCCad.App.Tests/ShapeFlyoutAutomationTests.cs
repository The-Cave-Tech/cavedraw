using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VCCad.App.Automation;
using VCCad.App.ViewModels;
using VCCad.App.Views;
using VCCad.Core.Model;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// The chrome has to be automation-visible: if a person can click it, a driver must be able to find it.
///
/// `ui.find` and `ui.dump` both walk the **window's** visual tree, popups included, because a menu is only
/// findable that way. The flyout's entries live in a `Popup`, which has its own visual tree - so these tests
/// ask the window, not the control, and they open the flyout first, because an entry that is not on screen
/// is not something a driver should be able to click.
/// </summary>
public class ShapeFlyoutAutomationTests
{
    private static (Window Window, ShapeFlyoutButton Button, EditorViewModel ViewModel) Host()
    {
        var viewModel = new EditorViewModel();
        var button = new ShapeFlyoutButton { PressMilliseconds = 20 };
        button.Attach(viewModel);

        var window = new Window { Width = 600, Height = 400, Content = button };
        window.Show();
        Settle();
        return (window, button, viewModel);
    }

    private static void Settle()
    {
        for (int i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static IEnumerable<Control> Descendants(Visual root)
    {
        foreach (Visual child in root.GetVisualChildren())
        {
            yield return child as Control ?? throw new InvalidOperationException("non-control visual");

            foreach (Control descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    /// <summary>Every control reachable from the window, which is what `ui.find` and `ui.dump` see.</summary>
    private static List<Control> Everything(Window window)
        => Descendants(window).ToList();

    /// <summary>
    /// The entries are **not** reachable while the flyout is closed, and they are once it is open. That is
    /// the honest half of "findable": a driver should not be able to click a thing nobody can see.
    /// </summary>
    [AvaloniaFact]
    public void TheEntriesAreReachableOnlyWhileTheFlyoutIsOpen()
    {
        (Window window, ShapeFlyoutButton button, _) = Host();
        try
        {
            Assert.DoesNotContain(Everything(window), c => c.Name == "ShapeFlyoutStar");

            button.OpenFlyout();
            Settle();

            Assert.Contains(Everything(window), c => c.Name == "ShapeFlyoutStar");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Every one of the nine is reachable by name while the flyout is open, and not just the first.</summary>
    [AvaloniaFact]
    public void EveryEntryIsReachableByName()
    {
        (Window window, ShapeFlyoutButton button, _) = Host();
        try
        {
            button.OpenFlyout();
            Settle();

            List<Control> all = Everything(window);

            foreach (ShapeKind kind in ShapeLibrary.All)
            {
                string name = ShapeLibrary.Name(kind);
                string expected = $"ShapeFlyout{char.ToUpperInvariant(name[0])}{name[1..]}";

                Assert.Contains(all, c => c.Name == expected);
            }
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// An entry is findable by its **displayed text** as well as its name, because a driver that cannot see
    /// has only those two things to go on.
    /// </summary>
    [AvaloniaFact]
    public void AnEntryIsFindableByItsText()
    {
        (Window window, ShapeFlyoutButton button, _) = Host();
        try
        {
            button.OpenFlyout();
            Settle();

            List<Control> all = Everything(window);

            foreach (ShapeKind kind in ShapeLibrary.All)
            {
                string name = ShapeLibrary.Name(kind);
                Assert.Contains(all, c => c is Button b && (b.Content as string ?? string.Empty) == name);
            }
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// Clicking an entry through the tree - which is what `ui.click` does - produces exactly the state
    /// `tool.set` produces. Compared directly rather than asserted twice by hand, because the point is that
    /// the two routes agree.
    /// </summary>
    [AvaloniaFact]
    public void ClickingAnEntryMatchesWhatToolSetProduces()
    {
        (Window window, ShapeFlyoutButton button, EditorViewModel viewModel) = Host();
        try
        {
            // Route one: the driver's API. `tool.set star` sets the property and the tool.
            viewModel.CurrentShape = ShapeKind.Star;
            viewModel.Tool = EditorTool.Shape;
            Settle();
            (EditorTool bySet, ShapeKind shapeBySet, string tipBySet) =
                (viewModel.Tool, viewModel.CurrentShape, ToolTip.GetTip(button.Face) as string ?? string.Empty);

            // Back to a different starting point.
            viewModel.CurrentShape = ShapeKind.Heart;
            viewModel.Tool = EditorTool.Select;
            Settle();

            // Route two: clicking the entry in the flyout, found the way `ui.find` finds it.
            button.OpenFlyout();
            Settle();

            Button entry = Everything(window)
                .OfType<Button>()
                .First(b => b.Name == "ShapeFlyoutStar");

            entry.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Settle();

            Assert.Equal(bySet, viewModel.Tool);
            Assert.Equal(shapeBySet, viewModel.CurrentShape);
            Assert.Contains(ShapeLibrary.Name(ShapeKind.Star), ToolTip.GetTip(button.Face) as string ?? string.Empty);
            Assert.False(button.IsFlyoutOpen);
            Assert.Equal(shapeBySet.ToString(), viewModel.CurrentShape.ToString());
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The tool's own button is findable too, by name and by its tooltip.</summary>
    [AvaloniaFact]
    public void TheToolbarButtonIsFindable()
    {
        (Window window, ShapeFlyoutButton button, _) = Host();
        try
        {
            List<Control> all = Everything(window);

            Assert.Contains(all, c => ReferenceEquals(c, button.Face));
            Assert.Contains("Shapes", ToolTip.GetTip(button.Face) as string ?? string.Empty);
        }
        finally
        {
            window.Close();
        }
    }
}
