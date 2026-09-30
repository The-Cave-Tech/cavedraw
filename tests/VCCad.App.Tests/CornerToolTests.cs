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
/// The corner tool: press on a corner, drag, and the distance dragged becomes the radius of the arc that
/// replaces it.
///
/// Driven through the **real pointer path**, because the gesture is the feature: a rule that only fires
/// when something else calls it is not a tool. The drag previews by restoring the pre-drag snapshot and
/// rounding again, so what is asserted is the end of the gesture - and that it is **one** undo step, not
/// one per mouse move.
/// </summary>
public class CornerToolTests
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

    private static void Settle()
    {
        for (int i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static PathItem Square(EditorViewModel viewModel)
    {
        PathItem path = PathFactory.CreateRectangle("square", new Rect2D(0, 0, 100, 100));
        path.Fill = FillSpec.Solid(ColorRgb.Black);
        viewModel.Document.Artboards[0].Layers[0].AddItem(path);
        Settle();
        return path;
    }

    private static double Distance(Point2D a, Point2D b)
        => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    /// <summary>Dragging a corner out by 40 rounds it: the corner goes, and an arc of that radius arrives.</summary>
    [AvaloniaFact]
    public void DraggingACornerRoundsItByTheDragDistance()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel) = Host();
        try
        {
            PathItem square = Square(viewModel);
            viewModel.SelectRange(new LayerItem[] { square }, additive: false);
            viewModel.Tool = EditorTool.Corner;

            var corner = new Point2D(0, 0);
            Point start = workspace.ModelToWindow(corner);

            // 40 units out along the diagonal: 40 / sqrt(2) on each axis. Window pixels are not model
            // units - the canvas has a zoom - so the target is converted rather than assumed, which the
            // first version of this test got wrong.
            Point end = workspace.ModelToWindow(new Point2D(-28.2842712, -28.2842712));

            InputInjection.Press(window, start.X, start.Y, shift: false);
            InputInjection.Move(window, end.X, end.Y, leftDown: true);
            InputInjection.Release(window, end.X, end.Y);
            Settle();

            // The corner is gone, replaced by the arc's two ends - one more node than before.
            Assert.Equal(5, square.SubPaths[0].Nodes.Count);

            List<Point2D> anchors = square.SubPaths[0].Nodes.Select(n => n.Anchor).ToList();
            Assert.DoesNotContain(anchors, a => a.NearlyEquals(corner, 1e-6));

            // At a right angle the arc's two ends are the radius from the corner and **exactly equal to
            // each other** - the zoom-independent claim, and the one that says this is a fillet rather
            // than a cut across the corner. The absolute distance is only within a percent or two of the
            // 40 asked for, because the pointer travels through window pixels and back; asserting it
            // exactly would be testing the host's zoom rather than the tool.
            List<double> reaches = anchors.Select(a => Distance(a, corner)).Where(d => d < 60).ToList();

            Assert.Equal(2, reaches.Count);
            Assert.Equal(reaches[0], reaches[1], 3);
            Assert.True(Math.Abs(reaches[0] - 40) < 2, $"the drag asked for 40, got {reaches[0]}");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The whole drag is one undo step, not one per mouse move.</summary>
    [AvaloniaFact]
    public void TheWholeDragIsOneUndoStep()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel) = Host();
        try
        {
            PathItem square = Square(viewModel);
            viewModel.SelectRange(new LayerItem[] { square }, additive: false);
            viewModel.Tool = EditorTool.Corner;

            Point start = workspace.ModelToWindow(new Point2D(0, 0));
            Point end = workspace.ModelToWindow(new Point2D(-21.2, -21.2));

            InputInjection.Press(window, start.X, start.Y, shift: false);
            InputInjection.Move(window, start.X - 10, start.Y - 10, leftDown: true);
            InputInjection.Move(window, start.X - 20, start.Y - 20, leftDown: true);
            InputInjection.Move(window, end.X, end.Y, leftDown: true);
            InputInjection.Release(window, end.X, end.Y);
            Settle();

            Assert.Equal(5, square.SubPaths[0].Nodes.Count);

            viewModel.Undo();
            Settle();

            // One undo takes the whole drag back: the square is a square again.
            Assert.Equal(4, square.SubPaths[0].Nodes.Count);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A drag that starts away from any corner does nothing at all.</summary>
    [AvaloniaFact]
    public void DraggingAwayFromACornerDoesNothing()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel) = Host();
        try
        {
            PathItem square = Square(viewModel);
            viewModel.SelectRange(new LayerItem[] { square }, additive: false);
            viewModel.Tool = EditorTool.Corner;

            Point start = workspace.ModelToWindow(new Point2D(50, 50));

            InputInjection.Press(window, start.X, start.Y, shift: false);
            InputInjection.Move(window, start.X + 40, start.Y + 40, leftDown: true);
            InputInjection.Release(window, start.X + 40, start.Y + 40);
            Settle();

            Assert.Equal(4, square.SubPaths[0].Nodes.Count);
        }
        finally
        {
            window.Close();
        }
    }
}
