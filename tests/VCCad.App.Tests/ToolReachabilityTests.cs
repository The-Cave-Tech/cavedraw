using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VCCad.App.Automation;
using VCCad.App.Controls;
using VCCad.App.ViewModels;
using VCCad.App.Views;
using VCCad.Core.Model;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// Capability parity, for the toolbar: **every tool is reachable both ways**.
///
/// A person gets there by clicking a named control; a driver gets there by name through `tool.set`. Neither
/// may lag the other, and the only way to be sure is to enumerate the enum rather than check the ones
/// somebody remembered - which is how the corner and pencil tools were added without buttons.
///
/// `tool.get` is asserted after each of the four ways a tool can change: `tool.set`, a hotkey, the flyout,
/// and the toolbar itself. One source of truth means all four report the same thing, and a driver that sets
/// a tool and a driver that watches one cannot end up disagreeing.
/// </summary>
public class ToolReachabilityTests
{
    private static (Window Window, EditorView View, EditorViewModel ViewModel, CanvasWorkspace Workspace) Host()
    {
        var view = new EditorView();
        var window = new Window { Width = 1100, Height = 800, Content = view };
        window.Show();
        Settle();

        var workspace = Descendants(view).OfType<CanvasWorkspace>().First();
        workspace.Focus();
        Settle();
        return (window, view, view.ViewModel, workspace);
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
            if (child is Control control)
            {
                yield return control;
            }

            foreach (Control descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    /// <summary>
    /// **Every** tool, enumerated from the enum, is reachable by name in the tree and by `tool.set`.
    ///
    /// This is the criterion that catches a tool added to the enum and forgotten everywhere else.
    /// </summary>
    [AvaloniaFact]
    public void EveryToolIsReachableByANamedControlAndByToolSet()
    {
        (Window window, EditorView view, _, _) = Host();
        try
        {
            List<Control> all = Descendants(window).ToList();

            foreach (EditorTool tool in Enum.GetValues<EditorTool>())
            {
                string name = ToolbarLayout.NameFor(tool);
                Assert.True(
                    all.Any(c => c.Name == name),
                    $"{tool} has no named control ({name}) in the window, so a driver cannot find it");

                // The same tool by name through the driver's API.
                Assert.True(
                    EditorToolNames.TryResolve(tool.ToString().ToLowerInvariant(), out EditorTool resolved, out _),
                    $"tool.set cannot set {tool}");
                Assert.Equal(tool, resolved);
            }
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The nine shapes are reachable through `tool.set` by the name a person sees.</summary>
    [AvaloniaFact]
    public void EveryShapeIsReachableByItsOwnName()
    {
        (Window window, EditorView view, _, _) = Host();
        try
        {
            // The entries only exist while the flyout is open, which is the honest half of "findable": a
            // driver should not be able to click a thing nobody can see.
            var flyout = Descendants(window).OfType<ShapeFlyoutButton>().First();
            flyout.OpenFlyout();
            Settle();

            List<Control> all = Descendants(window).ToList();

            foreach (ShapeKind kind in ShapeLibrary.All)
            {
                string name = ShapeLibrary.Name(kind);

                Assert.True(EditorToolNames.TryResolve(name, out EditorTool tool, out ShapeKind? shape));

                // `rectangle` is both a tool and one of the nine shapes, and the tool name wins so that
                // every tool stays reachable by name. The rectangle *shape* is therefore reached by arming
                // the shape tool and setting its kind, which is the documented route rather than a gap.
                if (name == "rectangle")
                {
                    Assert.Equal(EditorTool.Rectangle, tool);
                    Assert.Null(shape);
                }
                else
                {
                    Assert.Equal(EditorTool.Shape, tool);
                    Assert.Equal(kind, shape);
                }

                // And the entry that picks it is on screen when the flyout is open.
                Assert.True(
                    all.Any(c => c is Button b && b.Name == $"ShapeFlyout{char.ToUpperInvariant(name[0])}{name[1..]}"),
                    $"the flyout has no named entry for {name}");
            }
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>`tool.get` reports the same thing after each of the four ways a tool can change.</summary>
    [AvaloniaFact]
    public void ToolGetAgreesWithAllFourWays()
    {
        (Window window, EditorView view, EditorViewModel viewModel, CanvasWorkspace workspace) = Host();
        try
        {
            // 1. tool.set - what the operation does.
            viewModel.Tool = EditorTool.Node;
            Settle();
            Assert.Equal("node", EditorToolNames.NameOf(viewModel.Tool, viewModel.CurrentShape));

            // 2. a hotkey.
            InputInjection.Key(workspace, Avalonia.Input.Key.L, Avalonia.Input.KeyModifiers.None);
            Settle();
            Assert.Equal("ellipse", EditorToolNames.NameOf(viewModel.Tool, viewModel.CurrentShape));

            // 3. the toolbar itself - the button a person would press, found by name.
            Button rectangle = Descendants(window)
                .OfType<Button>()
                .First(b => b.Name == ToolbarLayout.NameFor(EditorTool.Rectangle));
            rectangle.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Settle();
            Assert.Equal("rectangle", EditorToolNames.NameOf(viewModel.Tool, viewModel.CurrentShape));

            // 4. the flyout - a shape, which is the tool that was actually chosen rather than its container.
            var flyout = Descendants(window).OfType<ShapeFlyoutButton>().First();
            flyout.OpenFlyout();
            Settle();
            Finder.Descendants(window)
                .OfType<Button>()
                .First(b => b.Name == "ShapeFlyoutStar")
                .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Settle();

            Assert.Equal("star", EditorToolNames.NameOf(viewModel.Tool, viewModel.CurrentShape));
        }
        finally
        {
            window.Close();
        }
    }
}

/// <summary>Walking the window's tree, popups included - what `ui.find` does.</summary>
internal static class Finder
{
    internal static IEnumerable<Control> Descendants(Visual root)
    {
        foreach (Visual child in root.GetVisualChildren())
        {
            if (child is Control control)
            {
                yield return control;
            }

            foreach (Control descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }
}
