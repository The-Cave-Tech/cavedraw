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
/// The pencil: press, move, release, and what was drawn becomes an open path of cubic segments.
///
/// Driven through the **real pointer path**, because the gesture is the feature. Two things are asserted
/// that a screenshot could not tell apart from a working tool: that the curve follows the points that
/// were drawn rather than a smoothed idea of them, and that the stroke is not fitted while the pointer is
/// down - which is the explicit request, and also the behaviour that keeps the line from wandering under
/// the hand.
/// </summary>
public class PencilToolTests
{
    /// <summary>Points a pointer would visit drawing a quarter circle, in model space, plus an offset.</summary>
    private static List<Point2D> Arc(int count, double radius, double offsetX = 200, double offsetY = 200)
    {
        var points = new List<Point2D>();
        for (int i = 0; i < count; i++)
        {
            double angle = (Math.PI / 2) * i / (count - 1);
            points.Add(new Point2D(
                offsetX + (radius * Math.Cos(angle)), offsetY - (radius * Math.Sin(angle))));
        }

        return points;
    }

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

    private static void Draw(Window window, CanvasWorkspace workspace, IReadOnlyList<Point2D> points)
    {
        Point first = workspace.ModelToWindow(points[0]);
        InputInjection.Press(window, first.X, first.Y, shift: false);

        for (int i = 1; i < points.Count; i++)
        {
            Point at = workspace.ModelToWindow(points[i]);
            InputInjection.Move(window, at.X, at.Y, leftDown: true);
        }

        Point last = workspace.ModelToWindow(points[^1]);
        InputInjection.Release(window, last.X, last.Y);
        Settle();
    }

    private static PathItem? OnlyPath(EditorViewModel viewModel)
        => viewModel.Document.Artboards[0].Layers[0].Children.OfType<PathItem>().FirstOrDefault();

    /// <summary>Drawing an arc gives an open path of a few cubic segments - not a polyline of points.</summary>
    [AvaloniaFact]
    public void DrawingAnArcGivesAPathOfSegments()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel) = Host();
        try
        {
            viewModel.Tool = EditorTool.Pencil;
            List<Point2D> drawn = Arc(60, 120);

            Draw(window, workspace, drawn);

            PathItem path = Assert.IsType<PathItem>(OnlyPath(viewModel));
            SubPath sub = Assert.Single(path.SubPaths);

            Assert.False(sub.IsClosed, "a freehand stroke is an open path");
            // The exact accuracy of the fit is the Core tests' business, on exact input. What is asserted
            // here is the wiring, and the node count is looser than it looks because the seam is a real
            // pointer: every injected move lands on a **whole window pixel**, so the captured stroke is
            // quantised to about a unit and honestly needs more segments than a mathematically smooth arc.
            Assert.True(sub.Nodes.Count >= 2, "the stroke should have become a path");
            Assert.True(sub.Nodes.Count < drawn.Count / 2,
                $"{sub.Nodes.Count} nodes from {drawn.Count} points is not much of a fit");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The fit follows the hand: every point drawn is within the tolerance of the curve that was made,
    /// measured against a dense sampling of the fitted path rather than by eye.
    /// </summary>
    [AvaloniaFact]
    public void TheCurveFollowsThePointsThatWereDrawn()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel) = Host();
        try
        {
            viewModel.Tool = EditorTool.Pencil;
            List<Point2D> drawn = Arc(40, 100);

            Draw(window, workspace, drawn);

            PathItem path = Assert.IsType<PathItem>(OnlyPath(viewModel));

            // Back to document space: the path is artboard-local, and the points were document-space.
            Vector2D offset = path.ArtboardOffset();
            List<Point2D> curve = PathFlattener
                .Flatten(path, tolerance: 0.01)
                .SelectMany(o => o.Points)
                .Select(p => new Point2D(p.X + offset.X, p.Y + offset.Y))
                .ToList();

            foreach (Point2D point in drawn)
            {
                double nearest = curve.Min(on =>
                    Math.Sqrt(Math.Pow(on.X - point.X, 2) + Math.Pow(on.Y - point.Y, 2)));

                // A window pixel is about a model unit at this zoom, so the round trip through the screen
                // adds a unit of quantisation on top of the fit's own tolerance. Asserting the fit's
                // tolerance alone here would be measuring the harness.
                Assert.True(nearest <= FreehandFitter.Tolerance + 2,
                    $"a drawn point is {nearest:0.###} from the curve; tolerance is {FreehandFitter.Tolerance:0.###}");
            }
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// **Nothing is added while the pointer is down.** Mid-drag the document is still empty, because the
    /// stroke is only fitted and committed on release - which is what stops the line being re-smoothed
    /// under the hand as the fit wobbles.
    /// </summary>
    [AvaloniaFact]
    public void NothingIsCommittedUntilThePointerIsReleased()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel) = Host();
        try
        {
            viewModel.Tool = EditorTool.Pencil;
            List<Point2D> drawn = Arc(30, 90);

            Point first = workspace.ModelToWindow(drawn[0]);
            InputInjection.Press(window, first.X, first.Y, shift: false);

            for (int i = 1; i < drawn.Count; i++)
            {
                Point at = workspace.ModelToWindow(drawn[i]);
                InputInjection.Move(window, at.X, at.Y, leftDown: true);
            }

            Settle();
            Assert.Null(OnlyPath(viewModel));

            Point last = workspace.ModelToWindow(drawn[^1]);
            InputInjection.Release(window, last.X, last.Y);
            Settle();

            Assert.NotNull(OnlyPath(viewModel));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A click with no movement draws nothing at all - not a zero-length path.</summary>
    [AvaloniaFact]
    public void AClickDrawsNothing()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel) = Host();
        try
        {
            viewModel.Tool = EditorTool.Pencil;

            Point at = workspace.ModelToWindow(new Point2D(200, 200));
            InputInjection.Press(window, at.X, at.Y, shift: false);
            InputInjection.Release(window, at.X, at.Y);
            Settle();

            Assert.Null(OnlyPath(viewModel));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The stroke is one undo step, and the stroke is selected so it can be worked on.</summary>
    [AvaloniaFact]
    public void TheStrokeIsOneUndoStepAndIsSelected()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel) = Host();
        try
        {
            viewModel.Tool = EditorTool.Pencil;
            Draw(window, workspace, Arc(30, 80));

            PathItem path = Assert.IsType<PathItem>(OnlyPath(viewModel));
            Assert.Contains(path, viewModel.SelectedObjects);

            viewModel.Undo();
            Settle();

            Assert.Null(OnlyPath(viewModel));
        }
        finally
        {
            window.Close();
        }
    }
}
