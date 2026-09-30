using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using VCCad.App.Automation;
using VCCad.App.Controls;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// The pen keeps its place.
///
/// A path is drawn over minutes, with a look at something else in between - and being sent back to
/// the last point is the difference between "step aside for a moment" and "start again". What ends
/// a path is closing it or pressing Escape; switching tools is neither.
/// </summary>
public class PenStateTests
{
    private static (Window Window, CanvasWorkspace Workspace, EditorViewModel ViewModel) Host()
    {
        var viewModel = new EditorViewModel();
        var workspace = new CanvasWorkspace();
        workspace.AttachEditor(viewModel);

        var window = new Window { Width = 900, Height = 700, Content = workspace };
        window.Show();
        Settle();
        workspace.Focus();
        Settle();
        return (window, workspace, viewModel);
    }

    private static void ClickAt(Window window, CanvasWorkspace workspace, double x, double y)
    {
        Point p = workspace.ModelToWindow(new Point2D(x, y));
        InputInjection.Press(window, p.X, p.Y, shift: false);
        InputInjection.Release(window, p.X, p.Y);
        Settle();
    }

    private static void Press(CanvasWorkspace workspace, Key key)
    {
        InputInjection.Key(workspace, key, KeyModifiers.None);
        Settle();
    }

    private static List<PathItem> Paths(EditorViewModel viewModel)
        => viewModel.Document.Artboards
            .SelectMany(a => a.Layers)
            .SelectMany(l => l.Children)
            .OfType<PathItem>()
            .ToList();

    private static int Nodes(PathItem path) => path.SubPaths[0].Nodes.Count;

    [AvaloniaFact]
    public void ThePenCarriesOnWhereItLeftOffAfterAVisitToTheNodes()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel) = Host();
        try
        {
            Press(workspace, Key.P);
            ClickAt(window, workspace, 100, 100);
            ClickAt(window, workspace, 200, 100);
            ClickAt(window, workspace, 300, 100);

            PathItem path = Assert.Single(Paths(viewModel));
            Assert.Equal(3, Nodes(path));

            // Aside to the node tool and back, the way A does it.
            Press(workspace, Key.A);
            Assert.Equal(EditorTool.Node, viewModel.Tool);
            Press(workspace, Key.A);
            Assert.Equal(EditorTool.Pen, viewModel.Tool);

            ClickAt(window, workspace, 400, 100);

            // The same path, one point longer - not a second path.
            Assert.Same(path, Assert.Single(Paths(viewModel)));
            Assert.Equal(4, Nodes(path));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ThePathSurvivesAToolChangeMadeOutsideTheCanvas()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel) = Host();
        try
        {
            Press(workspace, Key.P);
            ClickAt(window, workspace, 100, 100);
            ClickAt(window, workspace, 200, 100);

            // A toolbar button or an operation, rather than a key.
            viewModel.Tool = EditorTool.Node;
            Settle();
            viewModel.Tool = EditorTool.Pen;
            Settle();

            ClickAt(window, workspace, 300, 100);

            PathItem path = Assert.Single(Paths(viewModel));
            Assert.Equal(3, Nodes(path));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void EscapeEndsThePathAndTheNextPointStartsANewOne()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel) = Host();
        try
        {
            Press(workspace, Key.P);
            ClickAt(window, workspace, 100, 100);
            ClickAt(window, workspace, 200, 100);

            Press(workspace, Key.Escape);

            ClickAt(window, workspace, 300, 300);

            Assert.Equal(2, Paths(viewModel).Count);
            Assert.Equal(1, Nodes(Paths(viewModel)[1]));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ClosingThePathEndsItAndTheNextPointStartsANewOne()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel) = Host();
        try
        {
            Press(workspace, Key.P);
            ClickAt(window, workspace, 100, 100);
            ClickAt(window, workspace, 200, 100);
            ClickAt(window, workspace, 300, 100);

            // Back onto the first anchor: the path closes and is finished.
            ClickAt(window, workspace, 100, 100);

            PathItem closed = Paths(viewModel)[0];
            Assert.True(closed.SubPaths[0].IsClosed, "the path should have closed");

            ClickAt(window, workspace, 400, 300);

            Assert.Equal(2, Paths(viewModel).Count);
            Assert.Equal(1, Nodes(Paths(viewModel)[1]));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// Keeping the pen state means holding a reference across tools, so a path removed while
    /// another tool was active must not be carried on: there is nothing left to continue from.
    /// </summary>
    [AvaloniaFact]
    public void APathRemovedWhileAnotherToolWasActiveIsNotCarriedOn()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel) = Host();
        try
        {
            Press(workspace, Key.P);
            ClickAt(window, workspace, 100, 100);

            PathItem first = Assert.Single(Paths(viewModel));

            // Away, select it, delete it, back.
            viewModel.Tool = EditorTool.Select;
            Settle();
            viewModel.SelectObject(first);
            viewModel.RequestDeleteSelection();
            Settle();
            Assert.Empty(Paths(viewModel));

            viewModel.Tool = EditorTool.Pen;
            Settle();
            ClickAt(window, workspace, 400, 400);

            PathItem second = Assert.Single(Paths(viewModel));
            Assert.NotSame(first, second);
            Assert.Equal(1, Nodes(second));
        }
        finally
        {
            window.Close();
        }
    }

    private static void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }
}
