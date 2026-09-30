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
/// Dragging a shape's control points on the canvas.
///
/// Driven through the **real pointer path**, because the gesture is the feature. What the handle does to
/// the parameters is the Core tests' business; what is asserted here is that the canvas finds the handle
/// under the pointer, rebuilds the shape as it is dragged, and records the whole gesture as one undo step.
/// </summary>
public class ShapeHandleDragTests
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

    /// <summary>A star on the artboard, selected, with the node tool armed.</summary>
    private static PathItem Star(EditorViewModel viewModel, ShapeKind kind = ShapeKind.Star)
    {
        PathItem path = ShapeLibrary.Create(kind, new ShapeParameters
        {
            Centre = new Point2D(300, 300),
            Width = 200,
            Height = 200,
        });

        path.Fill = FillSpec.Solid(ColorRgb.Black);
        viewModel.Document.Artboards[0].Layers[0].AddItem(path);
        viewModel.SelectRange(new LayerItem[] { path }, additive: false);
        viewModel.Tool = EditorTool.Node;
        Settle();
        return path;
    }

    /// <summary>Where a handle is, in model space - the shape's parameters plus its artboard offset.</summary>
    private static Point2D HandleAt(PathItem path, ShapeHandle handle)
    {
        Vector2D offset = path.ArtboardOffset();
        ShapeHandlePoint point = ShapeHandles.For(path.Shape!)
            .First(h => h.Handle == handle);
        return point.Position + offset;
    }

    private static void Drag(Window window, CanvasWorkspace workspace, Point2D from, Point2D to)
    {
        Point start = workspace.ModelToWindow(from);
        Point end = workspace.ModelToWindow(to);

        InputInjection.Press(window, start.X, start.Y, shift: false);
        InputInjection.Move(window, end.X, end.Y, leftDown: true);
        InputInjection.Release(window, end.X, end.Y);
        Settle();
    }

    /// <summary>Dragging the width handle widens the shape, and rebuilds the outline.</summary>
    [AvaloniaFact]
    public void DraggingTheWidthHandleWidensTheShape()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel) = Host();
        try
        {
            PathItem star = Star(viewModel);
            double widthBefore = star.Shape!.Parameters.Width;

            // From the right edge of the box, 100 out: the width doubles with the distance from the centre.
            Drag(window, workspace, HandleAt(star, ShapeHandle.Width), new Point2D(500, 300));

            Assert.True(star.Shape!.Parameters.Width > widthBefore,
                $"the width should have grown: {widthBefore} -> {star.Shape.Parameters.Width}");
            // Within a pixel of 400: the pointer travels through window pixels and back, so the exact\n            // number is the host's zoom as much as the tool's arithmetic.\n            Assert.Equal(400, star.Shape.Parameters.Width, 1);

            // The outline was rebuilt to match, rather than the parameters changing alone.
            Assert.True(star.SubPaths[0].Nodes.Count >= 10);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The whole drag is one undo step, so a person can take a gesture back in one keystroke.</summary>
    [AvaloniaFact]
    public void TheWholeDragIsOneUndoStep()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel) = Host();
        try
        {
            PathItem star = Star(viewModel);
            double widthBefore = star.Shape!.Parameters.Width;

            Point2D handle = HandleAt(star, ShapeHandle.Width);
            Point start = workspace.ModelToWindow(handle);

            InputInjection.Press(window, start.X, start.Y, shift: false);
            InputInjection.Move(window, start.X + 20, start.Y, leftDown: true);
            InputInjection.Move(window, start.X + 40, start.Y, leftDown: true);
            InputInjection.Move(window, start.X + 60, start.Y, leftDown: true);
            InputInjection.Release(window, start.X + 60, start.Y);
            Settle();

            Assert.NotEqual(widthBefore, star.Shape!.Parameters.Width);

            viewModel.Undo();
            Settle();

            Assert.Equal(widthBefore, star.Shape!.Parameters.Width, 3);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// A drag that starts away from every handle is not a handle drag: it falls through to the node tool,
    /// which is what stops the shape's box from swallowing ordinary editing.
    /// </summary>
    [AvaloniaFact]
    public void ADragAwayFromAHandleIsNotAHandleDrag()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel) = Host();
        try
        {
            PathItem star = Star(viewModel);
            double widthBefore = star.Shape!.Parameters.Width;

            // Inside the box but clear of every handle on it - including the centre one, which is exactly\n            // where the middle of a shape is and which my first version of this test dragged by mistake.\n            Drag(window, workspace, new Point2D(250, 250), new Point2D(500, 500));

            Assert.Equal(widthBefore, star.Shape!.Parameters.Width, 6);
            Assert.Equal(300, star.Shape.Parameters.Centre.X, 6);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// A shape that has no inner ring offers no handle for one - so a heart cannot be dragged in a way that
    /// changes nothing, which is the rule that makes handles trustworthy.
    /// </summary>
    [AvaloniaFact]
    public void AHeartHasNoInnerRingHandle()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel) = Host();
        try
        {
            PathItem heart = Star(viewModel, ShapeKind.Heart);

            Assert.DoesNotContain(ShapeHandles.For(heart.Shape!), h => h.Handle == ShapeHandle.InnerRatio);

            // And dragging where a star's inner-ring handle would have been changes only the width or
            // height, or nothing at all - never an inner ratio, because a heart has none.
            Drag(window, workspace, new Point2D(300, 250), new Point2D(300, 240));

            Assert.Null(ShapeHandles.For(heart.Shape!).FirstOrDefault(h => h.Handle == ShapeHandle.InnerRatio));
        }
        finally
        {
            window.Close();
        }
    }
}
