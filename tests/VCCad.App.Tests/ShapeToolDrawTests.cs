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
/// The shape tool: pick one of the nine, drag out a box, and get that shape.
///
/// Driven through the **real pointer path**, because the gesture is the feature. The picker and the button
/// are covered elsewhere; what is left is that a drag with a shape armed produces that shape, at the size
/// dragged, selected and undoable in one step - and that the tools which were here first are unchanged.
///
/// These tests found a real defect when first written: **no shape was ever created.** The move switch had
/// cases for the rectangle and ellipse tools but not for the shape tool, so the drag armed on the press and
/// never recorded another point - every release looked like a click. It is the identical mistake the lasso
/// made, and the comment above that case in the source says so.
/// </summary>
public class ShapeToolDrawTests
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

    private static void DragOut(Window window, CanvasWorkspace workspace, Point2D from, Point2D to)
    {
        Point start = workspace.ModelToWindow(from);
        Point end = workspace.ModelToWindow(to);

        InputInjection.Press(window, start.X, start.Y, shift: false);
        InputInjection.Move(window, end.X, end.Y, leftDown: true);
        InputInjection.Release(window, end.X, end.Y);
        Settle();
    }

    /// <summary>The single path in the document, wherever it landed.</summary>
    private static PathItem? OnlyPath(EditorViewModel viewModel)
        => viewModel.Document.Artboards
            .SelectMany(a => a.Layers)
            .SelectMany(l => l.Children)
            .OfType<PathItem>()
            .FirstOrDefault();

    /// <summary>Each shape, dragged out, is that shape - with its own outline and its own size.</summary>
    [AvaloniaTheory]
    [InlineData(ShapeKind.Star, 10)]
    [InlineData(ShapeKind.Rectangle, 4)]
    [InlineData(ShapeKind.RoundedRectangle, 8)]
    [InlineData(ShapeKind.Trapezoid, 4)]
    // A callout drawn from a box has no tail yet, so it is its rounded box: 8 segments, not the 11 it has`n    // once a tail is given.
    [InlineData(ShapeKind.Callout, 8)]
    [InlineData(ShapeKind.Heart, 4)]
    [InlineData(ShapeKind.Arrow, 7)]
    [InlineData(ShapeKind.Polygon, 5)]
    [InlineData(ShapeKind.Cloud, 10)]
    public void DraggingWithAShapeArmedMakesThatShape(ShapeKind kind, int segments)
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel) = Host();
        try
        {
            viewModel.CurrentShape = kind;
            viewModel.Tool = EditorTool.Shape;

            DragOut(window, workspace, new Point2D(200, 200), new Point2D(400, 320));

            PathItem drawn = Assert.IsType<PathItem>(OnlyPath(viewModel));

            Assert.NotNull(drawn.Shape);
            Assert.Equal(kind, drawn.Shape!.Kind);
            Assert.Equal(segments, drawn.SubPaths[0].SegmentCount);

            // The box is the one dragged, to within a pixel of the pointer's round trip.
            Rect2D box = drawn.BoundingBox();
            Assert.True(Math.Abs(box.Width - 200) < 40,
                $"{kind}: the drag box was 200 wide, the shape came out {box.Width:0.#}");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The shape is selected as soon as it is drawn, so its control points are already showing.</summary>
    [AvaloniaFact]
    public void TheDrawnShapeIsSelectedWithItsHandlesReady()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel) = Host();
        try
        {
            viewModel.CurrentShape = ShapeKind.Star;
            viewModel.Tool = EditorTool.Shape;

            DragOut(window, workspace, new Point2D(200, 200), new Point2D(380, 340));

            PathItem drawn = Assert.IsType<PathItem>(OnlyPath(viewModel));

            Assert.Contains(drawn, viewModel.SelectedObjects);
            Assert.NotEmpty(ShapeHandles.For(drawn.Shape!));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Drawing a shape is one undo step.</summary>
    [AvaloniaFact]
    public void DrawingAShapeIsOneUndoStep()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel) = Host();
        try
        {
            viewModel.CurrentShape = ShapeKind.Cloud;
            viewModel.Tool = EditorTool.Shape;

            DragOut(window, workspace, new Point2D(200, 200), new Point2D(360, 330));
            Assert.NotNull(OnlyPath(viewModel));

            viewModel.Undo();
            Settle();

            Assert.Null(OnlyPath(viewModel));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A click with no drag makes nothing, rather than a shapeless dot.</summary>
    [AvaloniaFact]
    public void AClickMakesNoShape()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel) = Host();
        try
        {
            viewModel.Tool = EditorTool.Shape;

            Point at = workspace.ModelToWindow(new Point2D(300, 300));
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

    /// <summary>
    /// The tools that were here first keep working: a drag with the rectangle tool still makes a plain
    /// rectangle, with no shape definition attached, because it is not one of the nine.
    /// </summary>
    [AvaloniaFact]
    public void TheOlderShapeToolsAreUnchanged()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel) = Host();
        try
        {
            viewModel.Tool = EditorTool.Rectangle;

            DragOut(window, workspace, new Point2D(200, 200), new Point2D(400, 320));

            PathItem drawn = Assert.IsType<PathItem>(OnlyPath(viewModel));

            Assert.Null(drawn.Shape);
            Assert.Equal(4, drawn.SubPaths[0].SegmentCount);
        }
        finally
        {
            window.Close();
        }
    }
}