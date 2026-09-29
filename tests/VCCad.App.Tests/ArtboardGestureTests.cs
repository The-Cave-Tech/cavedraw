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

    /// <summary>A model point on an artboard's name label, which sits just above its top-left.</summary>
    private static Point2D LabelPoint(CanvasWorkspace workspace, Artboard board)
    {
        Point topLeft = workspace.ModelToWindow(new Point2D(board.X, board.Y));
        return workspace.WindowToModel(new Point(topLeft.X + 20, topLeft.Y - 8));
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
            PathItem shape = Box("shape", 50, 50, 100);
            board.Layers[0].AddItem(shape);

            double boardXBefore = board.X;
            Rect2D objectBefore = shape.WorldBounds();

            Point2D grab = LabelPoint(workspace, board);
            Drag(window, workspace, grab, new Point2D(grab.X + 120, grab.Y));

            double boardMoved = board.X - boardXBefore;
            double objectMoved = shape.WorldBounds().X - objectBefore.X;

            Assert.True(boardMoved > 50, $"the name must move the artboard (moved {boardMoved})");
            Assert.Equal(boardMoved, objectMoved, 1);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The page body belongs to the artwork. Dragging it must not move the page - only the
    /// name label does that.
    /// </summary>
    [AvaloniaFact]
    public void DraggingThePageBodyDoesNotMoveThePage()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel) = Host();
        try
        {
            CadDocument document = viewModel.ActiveSession.Document;
            Artboard board = document.Artboards[0];
            board.X = 300;
            board.Y = 200;

            viewModel.Tool = EditorTool.Artboard;
            double boardXBefore = board.X;

            Point2D body = new(board.X + (board.Width / 2), board.Y + (board.Height / 2));
            Drag(window, workspace, body, new Point2D(body.X + 120, body.Y));

            Assert.Equal(boardXBefore, board.X, 2);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// With the page's own gesture moved onto its name, the Artboard tool's page body is
    /// free to behave like the Select tool: a marquee there selects what it covers.
    /// </summary>
    [AvaloniaFact]
    public void TheArtboardToolMarqueesObjectsOnThePage()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel) = Host();
        try
        {
            CadDocument document = viewModel.ActiveSession.Document;
            Artboard board = document.Artboards[0];
            board.Layers[0].AddItem(Box("a", 100, 100));
            board.Layers[0].AddItem(Box("b", 220, 100));

            viewModel.Tool = EditorTool.Artboard;

            // From empty page over both boxes: the page body is the artwork's, not the page's.
            Drag(window, workspace, new Point2D(80, 80), new Point2D(320, 220));

            Assert.Equal(2, viewModel.SelectedObjects.Count);
        }
        finally
        {
            window.Close();
        }
    }
}
