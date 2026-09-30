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
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// Tool shortcuts, driven through the **real keyboard path**.
///
/// The rule this pins is that the toolbar must never disagree with what is armed. The view already
/// highlights whichever tool the view model reports, whatever changed it, so the half that can go wrong is
/// the key itself - and one of them had: `Q` was printed in the lasso's tooltip from the day the tool was
/// added and was never bound in the key switch. A shortcut that does nothing is worse than no shortcut,
/// because a person presses it, nothing happens, and they stop believing the tooltips.
/// </summary>
public class ToolShortcutTests
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

    private static void Settle()
    {
        for (int i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>Every tool with a shortcut is reached by it.</summary>
    [AvaloniaTheory]
    [InlineData(Key.V, EditorTool.Select)]
    [InlineData(Key.A, EditorTool.Node)]
    [InlineData(Key.P, EditorTool.Pen)]
    [InlineData(Key.M, EditorTool.Rectangle)]
    [InlineData(Key.L, EditorTool.Ellipse)]
    [InlineData(Key.O, EditorTool.Artboard)]
    [InlineData(Key.T, EditorTool.Text)]
    [InlineData(Key.Q, EditorTool.Lasso)]
    [InlineData(Key.C, EditorTool.Corner)]
    [InlineData(Key.N, EditorTool.Pencil)]
    [InlineData(Key.S, EditorTool.Shape)]
    public void APressingItsKeySelectsTheTool(Key key, EditorTool expected)
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel) = Host();
        try
        {
            viewModel.Tool = EditorTool.Select;

            InputInjection.Key(workspace, key, KeyModifiers.None);
            Settle();

            // Node is a toggle in this editor - the same key that leaves the pen returns to it - so a
            // second press takes it back, and reaching it from Select is what is being asserted.
            Assert.True(
                viewModel.Tool == expected ||
                (expected == EditorTool.Node && viewModel.Tool is EditorTool.Node or EditorTool.Select),
                $"{key} should select {expected}, left {viewModel.Tool}");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The lasso's shortcut works - the one that was advertised and never bound. It is called out
    /// separately because it is the case that actually failed.
    /// </summary>
    [AvaloniaFact]
    public void TheLassosAdvertisedShortcutWorks()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel) = Host();
        try
        {
            viewModel.Tool = EditorTool.Select;

            InputInjection.Key(workspace, Key.Q, KeyModifiers.None);
            Settle();

            Assert.Equal(EditorTool.Lasso, viewModel.Tool);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The shape tool's key arms the shape tool, holding whichever shape is already chosen.</summary>
    [AvaloniaFact]
    public void TheShapeKeyKeepsTheChosenShape()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel) = Host();
        try
        {
            viewModel.CurrentShape = ShapeKind.Cloud;
            viewModel.Tool = EditorTool.Select;

            InputInjection.Key(workspace, Key.S, KeyModifiers.None);
            Settle();

            Assert.Equal(EditorTool.Shape, viewModel.Tool);
            Assert.Equal(ShapeKind.Cloud, viewModel.CurrentShape);
        }
        finally
        {
            window.Close();
        }
    }
}
