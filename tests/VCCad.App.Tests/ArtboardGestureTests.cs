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
/// Artboard gestures, driven through the real pointer path against a headless window.
///
/// These live at the control level on purpose: the defects they pin are in the canvas glue
/// (which point starts the gesture, how far the contents travel), not in the document model,
/// so a model-level test would pass while the app stayed broken.
/// </summary>
public class ArtboardGestureTests
{
    /// <summary>The editor's default document, its canvas attached and laid out.</summary>
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

    /// <summary>Presses at a model point, drags to another and releases, as a pointer would.</summary>
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

    private static void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// Moving a page carries its contents with it. The children are stored relative to the
    /// artboard origin, so moving the artboard already moves them; translating each child's
    /// local geometry by the same delta as well moved the artwork twice as far as the page
    /// and walked it off the sheet.
    /// </summary>
    [AvaloniaFact]
    public void DraggingAnArtboardCarriesItsObjectsTheSameDistance()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel) = Host();
        try
        {
            CadDocument document = viewModel.ActiveSession.Document;
            Artboard board = document.Artboards[0];
            board.X = 300;
            board.Y = 200;

            // A shape with area: WorldBounds() of a horizontal line is degenerate and is
            // measured separately, so a line here would make this test say nothing.
            var shape = new PathItem { Name = "shape" };
            SubPath sub = shape.AddSubPath(closed: true);
            sub.Nodes.Add(new PathNode(new Point2D(50, 50)));
            sub.Nodes.Add(new PathNode(new Point2D(150, 50)));
            sub.Nodes.Add(new PathNode(new Point2D(150, 150)));
            sub.Nodes.Add(new PathNode(new Point2D(50, 150)));
            board.Layers[0].AddItem(shape);

            viewModel.Tool = EditorTool.Artboard;

            double boardXBefore = board.X;
            Rect2D objectBefore = shape.WorldBounds();

            Point2D grab = new(board.X + (board.Width / 2), board.Y + (board.Height / 2));
            Drag(window, workspace, grab, new Point2D(grab.X + 120, grab.Y));

            double boardMoved = board.X - boardXBefore;
            double objectMoved = shape.WorldBounds().X - objectBefore.X;

            Assert.True(boardMoved > 50, $"the drag must actually move the artboard (moved {boardMoved})");
            Assert.Equal(boardMoved, objectMoved, 1);
        }
        finally
        {
            window.Close();
        }
    }
}
