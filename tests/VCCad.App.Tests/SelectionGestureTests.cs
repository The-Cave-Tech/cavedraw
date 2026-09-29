using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using VCCad.App.Automation;
using VCCad.App.Controls;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// Multi-object selection gestures, driven through the real pointer path against a headless
/// window. They live at the control level because the rule being pinned is about the press:
/// what happens to the rest of the selection when the press lands on one of its members.
/// </summary>
public class SelectionGestureTests
{
    private static (Window Window, CanvasWorkspace Workspace, EditorViewModel ViewModel) Host()
    {
        var viewModel = new EditorViewModel();
        var workspace = new CanvasWorkspace();
        workspace.AttachEditor(viewModel);

        var window = new Window { Width = 900, Height = 700, Content = workspace };
        window.Show();
        Settle();
        return (window, workspace, viewModel);
    }

    private static void Drag(Window window, CanvasWorkspace workspace, Point2D from, Point2D to)
    {
        Point a = workspace.ModelToWindow(from);
        Point b = workspace.ModelToWindow(to);

        InputInjection.Press(window, a.X, a.Y, shift: false);
        InputInjection.Move(window, (a.X + b.X) / 2.0, (a.Y + b.Y) / 2.0, leftDown: true);
        InputInjection.Move(window, b.X, b.Y, leftDown: true);
        InputInjection.Release(window, b.X, b.Y);
        Settle();
    }

    private static void Click(Window window, CanvasWorkspace workspace, Point2D at)
    {
        Point p = workspace.ModelToWindow(at);
        InputInjection.Press(window, p.X, p.Y, shift: false);
        InputInjection.Release(window, p.X, p.Y);
        Settle();
    }

    private static void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static PathItem Box(string name, double x, double y, double size = 50)
    {
        var path = new PathItem { Name = name };
        SubPath sub = path.AddSubPath(closed: true);
        sub.Nodes.Add(new PathNode(new Point2D(x, y)));
        sub.Nodes.Add(new PathNode(new Point2D(x + size, y)));
        sub.Nodes.Add(new PathNode(new Point2D(x + size, y + size)));
        sub.Nodes.Add(new PathNode(new Point2D(x, y + size)));
        return path;
    }

    /// <summary>Selects both boxes with a marquee and returns them.</summary>
    private static (PathItem A, PathItem B) TwoSelected(
        Window window, CanvasWorkspace workspace, EditorViewModel viewModel)
    {
        CadDocument document = viewModel.ActiveSession.Document;
        Artboard board = document.Artboards[0];
        PathItem a = Box("a", 100, 100);
        PathItem b = Box("b", 300, 100);
        board.Layers[0].AddItem(a);
        board.Layers[0].AddItem(b);

        Drag(window, workspace, new Point2D(60, 60), new Point2D(420, 220));
        Assert.Equal(2, viewModel.SelectedObjects.Count);
        return (a, b);
    }

    /// <summary>
    /// Pressing a member of a multi-selection and dragging moves the whole selection. The
    /// press used to replace the selection with the object under the pointer, so the drag
    /// moved one object and silently dropped the rest.
    /// </summary>
    [AvaloniaFact]
    public void DraggingOneOfSeveralSelectedObjectsMovesThemAll()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel) = Host();
        try
        {
            (PathItem a, PathItem b) = TwoSelected(window, workspace, viewModel);

            double aBefore = a.WorldBounds().X;
            double bBefore = b.WorldBounds().X;

            Point2D grab = new(a.WorldBounds().Left + 25, a.WorldBounds().Top + 25);
            Drag(window, workspace, grab, new Point2D(grab.X + 120, grab.Y));

            double aMoved = a.WorldBounds().X - aBefore;
            double bMoved = b.WorldBounds().X - bBefore;

            Assert.Equal(2, viewModel.SelectedObjects.Count);
            Assert.True(aMoved > 50, $"the clicked object must move (moved {aMoved})");

            // The invariant is that the whole selection travels together; the exact distance
            // goes through the model-to-window mapping, so it is not the model delta itself.
            Assert.Equal(aMoved, bMoved, 1);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The other half of that rule: a press on a member that never moves is a click, and a
    /// click reduces the selection to the object that was clicked.
    /// </summary>
    [AvaloniaFact]
    public void AClickOnOneOfSeveralSelectedObjectsKeepsOnlyThatOne()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel) = Host();
        try
        {
            (PathItem a, _) = TwoSelected(window, workspace, viewModel);

            Click(window, workspace, new Point2D(a.WorldBounds().Left + 25, a.WorldBounds().Top + 25));

            Assert.Same(a, Assert.Single(viewModel.SelectedObjects));
        }
        finally
        {
            window.Close();
        }
    }
}
