using System.Text.Json;
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
/// Double-click, which means "let me work on this one": a path hands its geometry to the node tool,
/// a text block opens with the caret where the click landed.
///
/// Driven through the real pointer path, because the thing being pinned is the gesture: the second
/// press of a double-click is what carries ClickCount, and a rule that only fires when something
/// else calls it is not the feature.
/// </summary>
public class DoubleClickEditTests
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

    private static PathItem Box(string name, double x, double y, double size = 60)
    {
        var path = new PathItem { Name = name };
        SubPath sub = path.AddSubPath(closed: true);
        sub.Nodes.Add(new PathNode(new Point2D(x, y)));
        sub.Nodes.Add(new PathNode(new Point2D(x + size, y)));
        sub.Nodes.Add(new PathNode(new Point2D(x + size, y + size)));
        sub.Nodes.Add(new PathNode(new Point2D(x, y + size)));
        return path;
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

    private static void DoubleClick(Window window, CanvasWorkspace workspace, Point2D at)
    {
        Point p = workspace.ModelToWindow(at);
        InputInjection.Click(window, p.X, p.Y, clickCount: 2, shift: false);
        Settle();
    }

    private static void Click(Window window, CanvasWorkspace workspace, Point2D at)
    {
        Point p = workspace.ModelToWindow(at);
        InputInjection.Click(window, p.X, p.Y, clickCount: 1, shift: false);
        Settle();
    }

    [AvaloniaFact]
    public void DoubleClickingAPathHandsItsGeometryToTheNodeTool()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel) = Host();
        try
        {
            PathItem box = Box("box", 100, 100);
            Add(viewModel, box);
            Assert.Equal(EditorTool.Select, viewModel.Tool);

            // On the outline: the click picks the path, and the second press opens it.
            DoubleClick(window, workspace, new Point2D(125, 100));

            Assert.Equal(EditorTool.Node, viewModel.Tool);
            Assert.Same(box, Assert.Single(viewModel.SelectedObjects));

            // And it is the path's own nodes that are now editable.
            Assert.Contains(box, viewModel.SelectedPaths());
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void DoubleClickingTextOpensItWithTheCaretWhereTheClickLanded()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel) = Host();
        try
        {
            TextItem label = Label(100, 100);
            Add(viewModel, label);

            Rect2D box = label.BoundingBox();
            Assert.True(box.Width > 20, $"the fixture needs a measurable run (width {box.Width})");
            int length = TextEditing.Length(label);

            // Near the beginning: the caret goes there, not to the end of the block.
            DoubleClick(window, workspace, new Point2D(102, 106));

            Assert.True(viewModel.IsEditingText, "a double-click must open the block");
            Assert.Same(label, viewModel.EditingText);
            Assert.True(viewModel.TextSelectionStart <= 2,
                $"the caret should be at the click, was {viewModel.TextSelectionStart}");

            // Near the end: it follows the click rather than sitting at a fixed index.
            Click(window, workspace, new Point2D(100, 400));
            DoubleClick(window, workspace, new Point2D(100 + box.Width - 1, 106));

            Assert.True(viewModel.TextSelectionStart >= length - 3,
                $"the caret should be near the end, was {viewModel.TextSelectionStart} of {length}");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void DoubleClickingEmptySpaceChangesNothing()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel) = Host();
        try
        {
            Add(viewModel, Box("box", 100, 100));

            DoubleClick(window, workspace, new Point2D(400, 400));

            Assert.Equal(EditorTool.Select, viewModel.Tool);
            Assert.Empty(viewModel.SelectedObjects);
            Assert.False(viewModel.IsEditingText);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// In the pen tool a double-click is how a path is finished, and in the node tool it is a node
    /// gesture. Neither is a request to switch tools.
    /// </summary>
    [AvaloniaFact]
    public void ADoubleClickOutsideTheSelectToolIsLeftAlone()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel) = Host();
        try
        {
            Add(viewModel, Box("box", 100, 100));
            viewModel.Tool = EditorTool.Pen;

            DoubleClick(window, workspace, new Point2D(125, 100));

            Assert.Equal(EditorTool.Pen, viewModel.Tool);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The same gesture, reachable by a driver rather than only by a double-click.</summary>
    [AvaloniaFact]
    public void TheSameGestureIsReachableFromTheRegistry()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel) = Host();
        try
        {
            PathItem box = Box("box", 100, 100);
            Add(viewModel, box);

            var context = new AutomationContext
            {
                ViewModel = viewModel,
                InputRoot = () => workspace,
            };

            EditorOperations.Invoke(context, "object.editAt",
                JsonSerializer.SerializeToElement(new { x = 125.0, y = 100.0 }));

            Assert.Equal(EditorTool.Node, viewModel.Tool);
            Assert.Same(box, Assert.Single(viewModel.SelectedObjects));
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
