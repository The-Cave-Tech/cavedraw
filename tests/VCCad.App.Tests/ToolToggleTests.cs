using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using VCCad.App.Automation;
using VCCad.App.Controls;
using VCCad.App.ViewModels;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// The tool keys remember where they came from.
///
/// A is the one that matters: it is how a person steps aside from the pen to nudge a node, and the
/// same key gives the pen back. Being sent to the toolbar in the middle of a drawing is the thing
/// this removes.
/// </summary>
public class ToolToggleTests
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

    private static void Press(CanvasWorkspace workspace, Key key)
    {
        // Aimed at the canvas, as a batch's shortcuts are: the headless focus manager does not
        // hand the key down by itself.
        InputInjection.Key(workspace, key, KeyModifiers.None);
        Settle();
    }

    [AvaloniaFact]
    public void AFromThePenGivesTheNodesAndTheSameKeyGivesThePenBack()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel) = Host();
        try
        {
            Press(workspace, Key.P);
            Assert.Equal(EditorTool.Pen, viewModel.Tool);

            Press(workspace, Key.A);
            Assert.Equal(EditorTool.Node, viewModel.Tool);
            Assert.Equal(EditorTool.Pen, viewModel.PreviousTool);

            Press(workspace, Key.A);
            Assert.Equal(EditorTool.Pen, viewModel.Tool);

            // It keeps toggling rather than sticking on one of the two.
            Press(workspace, Key.A);
            Assert.Equal(EditorTool.Node, viewModel.Tool);
            Press(workspace, Key.A);
            Assert.Equal(EditorTool.Pen, viewModel.Tool);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void AFromTheSelectToolComesBackToTheSelectTool()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel) = Host();
        try
        {
            Assert.Equal(EditorTool.Select, viewModel.Tool);

            Press(workspace, Key.A);
            Assert.Equal(EditorTool.Node, viewModel.Tool);

            Press(workspace, Key.A);
            Assert.Equal(EditorTool.Select, viewModel.Tool);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Every switch remembers, not only the ones A makes.</summary>
    [AvaloniaFact]
    public void EveryToolSwitchRemembersWhereItCameFrom()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel) = Host();
        try
        {
            Press(workspace, Key.P);
            Press(workspace, Key.T);

            Assert.Equal(EditorTool.Text, viewModel.Tool);
            Assert.Equal(EditorTool.Pen, viewModel.PreviousTool);

            Press(workspace, Key.V);
            Assert.Equal(EditorTool.Select, viewModel.Tool);
            Assert.Equal(EditorTool.Text, viewModel.PreviousTool);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void TheToggleIsReachableFromTheRegistry()
    {
        var viewModel = new EditorViewModel { Tool = EditorTool.Pen };
        var context = new AutomationContext { ViewModel = viewModel };

        EditorOperations.Invoke(context, "tool.toggle",
            JsonSerializer.SerializeToElement(new { tool = "node" }));
        Assert.Equal(EditorTool.Node, viewModel.Tool);

        EditorOperations.Invoke(context, "tool.toggle",
            JsonSerializer.SerializeToElement(new { tool = "node" }));
        Assert.Equal(EditorTool.Pen, viewModel.Tool);

        // And the memory is readable, so a driver can see where a toggle would go.
        var reported = EditorOperations.Invoke(context, "tool.get",
            JsonSerializer.SerializeToElement(new { }));
        Assert.Equal("pen", reported.GetType().GetProperty("tool")!.GetValue(reported));
        Assert.Equal("node", reported.GetType().GetProperty("previous")!.GetValue(reported));
    }

    private static void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }
}
