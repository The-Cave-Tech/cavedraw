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
/// The edit box's handles work **while the block is open for editing**.
///
/// The complaint: with the caret showing, hovering a handle kept the text cursor and dragging did
/// nothing, so the box could not be fixed from the place it is most obviously wrong - because the text
/// shrinks to fit the frame as it is typed, the moment the frame needs widening is while typing.
///
/// Driven through the real pointer path, because the thing being pinned is the interaction: a rule that
/// only fires when something else calls it is not the feature.
/// </summary>
public class TextBoxHandleTests
{
    private const double RotateHandleIndex = 4;

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

    private static TextItem Label(double x, double y, string content = "PRIYANKA SET")
    {
        var text = new TextItem { Name = "label", Origin = new Point2D(x, y) };
        text.Runs.Add(new TextRun { Text = content, FontSize = 12 });
        return text;
    }

    private static void Add(EditorViewModel viewModel, params LayerItem[] items)
    {
        Layer layer = viewModel.Document.Artboards[0].Layers[0];
        foreach (LayerItem item in items)
        {
            layer.AddItem(item);
        }

        Settle();
    }

    private static void Settle()
    {
        for (int i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static void OpenForEditing(Window window, CanvasWorkspace workspace, TextItem text)
    {
        Point p = workspace.ModelToWindow(text.Origin);
        InputInjection.Click(window, p.X, p.Y, clickCount: 2, shift: false);
        Settle();
    }

    /// <summary>The block is open for editing, and it is the one we expect.</summary>
    private static void AssertEditing(EditorViewModel viewModel)
        => Assert.True(viewModel.IsEditingText, "the block should be open for editing");

    [AvaloniaFact]
    public void HoveringAHandleWhileEditingShowsTheResizeCursor()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel) = Host();
        try
        {
            TextItem label = Label(100, 100);
            Add(viewModel, label);
            OpenForEditing(window, workspace, label);
            AssertEditing(viewModel);

            IReadOnlyList<Point2D> handles = workspace.EditBoxHandlesWorld(label);

            // A width handle: the right edge's middle.
            Assert.Equal(StandardCursorType.Ibeam, workspace.CursorKindFor(label.Origin));
            Assert.Equal(StandardCursorType.SizeWestEast, workspace.CursorKindFor(handles[1]));

            // The rotation handle.
            Assert.Equal(StandardCursorType.Hand, workspace.CursorKindFor(handles[4]));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The cursor follows the block, not the screen: turn it a quarter and the axes swap.</summary>
    [AvaloniaFact]
    public void TheResizeCursorFollowsTheBlockWhenItIsTurned()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel) = Host();
        try
        {
            TextItem label = Label(100, 100);
            Add(viewModel, label);
            OpenForEditing(window, workspace, label);

            Point2D handle = workspace.EditBoxHandlesWorld(label)[1];
            Assert.Equal(StandardCursorType.SizeWestEast, workspace.CursorKindFor(handle));

            label.RotationRadians = Math.PI / 2;

            handle = workspace.EditBoxHandlesWorld(label)[1];
            Assert.Equal(StandardCursorType.SizeNorthSouth, workspace.CursorKindFor(handle));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// Dragging a width handle while editing widens the frame, and the edit survives it: the block is
    /// still open and the caret is still where it was in the text.
    /// </summary>
    [AvaloniaFact]
    public void DraggingAWidthHandleWhileEditingWidensTheFrameAndKeepsTheCaret()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel) = Host();
        try
        {
            TextItem label = Label(100, 100);
            Add(viewModel, label);
            OpenForEditing(window, workspace, label);

            // Put the caret somewhere specific, then remember it.
            InputInjection.Click(window, workspace.ModelToWindow(new Point2D(130, 105)).X,
                workspace.ModelToWindow(new Point2D(130, 105)).Y, clickCount: 1, shift: false);
            Settle();
            AssertEditing(viewModel);

            int caretBefore = CaretIndex(workspace);
            double widthBefore = label.FrameWidth;

            // Grab the right edge and pull it well out.
            Point2D handle = workspace.EditBoxHandlesWorld(label)[1];
            Point grab = workspace.ModelToWindow(handle);
            InputInjection.Press(window, grab.X, grab.Y, shift: false);
            InputInjection.Move(window, grab.X + 120, grab.Y, leftDown: true);
            InputInjection.Release(window, grab.X + 120, grab.Y);
            Settle();

            Assert.True(label.FrameWidth > widthBefore + 100,
                $"the frame should have widened: {widthBefore} -> {label.FrameWidth}");
            AssertEditing(viewModel);
            Assert.Equal(caretBefore, CaretIndex(workspace));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Rotation is one of the things the box handles do, and it works while editing.</summary>
    [AvaloniaFact]
    public void DraggingTheRotateHandleTurnsTheBlockWhileEditing()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel) = Host();
        try
        {
            TextItem label = Label(100, 100);
            Add(viewModel, label);
            OpenForEditing(window, workspace, label);

            int caretBefore = CaretIndex(workspace);
            Assert.Equal(0.0, label.RotationRadians, 6);

            // Drag the rotate handle from above the block to the right of it: a quarter turn.
            Point origin = workspace.ModelToWindow(label.Origin);
            Point2D handle = workspace.EditBoxHandlesWorld(label)[4];
            Point grab = workspace.ModelToWindow(handle);

            InputInjection.Press(window, grab.X, grab.Y, shift: false);
            InputInjection.Move(window, origin.X + 90, origin.Y, leftDown: true);
            InputInjection.Release(window, origin.X + 90, origin.Y);
            Settle();

            Assert.Equal(Math.PI / 2, label.RotationRadians, 2);
            AssertEditing(viewModel);
            Assert.Equal(caretBefore, CaretIndex(workspace));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// A click inside the text still places the caret: the handles take priority only where a handle
    /// is, which is what stops the fix from breaking the thing the mode exists for.
    /// </summary>
    [AvaloniaFact]
    public void ClickingInsideTheTextStillPlacesTheCaret()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel) = Host();
        try
        {
            TextItem label = Label(100, 100);
            Add(viewModel, label);
            OpenForEditing(window, workspace, label);

            Assert.Equal(0, CaretIndex(workspace));

            // Well inside the block, clear of every handle.
            Point inside = workspace.ModelToWindow(new Point2D(140, 105));
            InputInjection.Click(window, inside.X, inside.Y, clickCount: 1, shift: false);
            Settle();

            AssertEditing(viewModel);
            Assert.True(CaretIndex(workspace) > 0, "the caret should have moved into the word");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The caret's index in the text being edited, which is the editing state that must survive.</summary>
    private static int CaretIndex(CanvasWorkspace workspace) => workspace.CaretIndexForTests;
}
