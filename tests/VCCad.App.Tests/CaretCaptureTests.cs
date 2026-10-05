using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using VCCad.App.Controls;
using VCCad.App.ViewModels;
using VCCad.App.Views;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// A screen capture shows the caret, it does not sample the blink (issue #249).
///
/// The caret blinks at 530 ms and the paint is gated on that state, so a capture taken between two ticks had no
/// caret in it - and a driver reading the picture cannot tell "the caret is somewhere else" from "the blink is
/// off". That is exactly the distinction a caret screenshot is taken to make, so half of all captures answered
/// nothing at all.
///
/// The blink is asserted through the state the painter reads rather than through pixels: the caret's geometry can
/// be blinked away but the fact that a capture asked for it cannot, and a pixel test would re-introduce the
/// coin toss it is meant to remove.
/// </summary>
public class CaretCaptureTests
{
    private static (Window Window, CanvasWorkspace Workspace, TextItem Text) Host()
    {
        var viewModel = new EditorViewModel();
        var workspace = new CanvasWorkspace();
        workspace.AttachEditor(viewModel);

        var window = new Window { Width = 900, Height = 700, Content = workspace };
        window.Show();
        Settle();

        var text = new TextItem { Name = "caption", Origin = new Point2D(120, 140) };
        text.Runs.Add(new TextRun { Text = "Jalie 3464", FontSize = 18, FontFamily = "Nimbus Sans" });
        viewModel.Document.Artboards[0].Layers[0].AddItem(text);
        Settle();

        return (window, workspace, text);
    }

    private static void Settle()
    {
        for (int i = 0; i < 4; i++)
        {
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>The blink is off, the block is being edited, and a capture shows the caret anyway.</summary>
    [AvaloniaFact]
    public void ACaptureShowsTheCaretWhileABlockIsBeingEdited()
    {
        (Window window, CanvasWorkspace workspace, TextItem text) = Host();
        Assert.True(workspace.BeginTextEdit(text), "the block could not be opened for editing");

        // **The blink is off**, which is the state half of all captures used to catch.
        workspace.CaretOnForTests = false;
        Assert.NotNull(workspace.CaretLine());

        byte[]? frame = ScreenCapture.CaptureWindow(window);

        Assert.NotNull(frame);
        Assert.NotEmpty(frame!);
        Assert.True(workspace.CaretOnForTests, "the capture left the caret hidden");

        window.Close();
    }

    /// <summary>And a capture with nothing being edited invents no caret.</summary>
    [AvaloniaFact]
    public void ACaptureInventsNoCaretWhenNothingIsBeingEdited()
    {
        (Window window, CanvasWorkspace workspace, _) = Host();

        Assert.Null(workspace.CaretLine());
        byte[]? frame = ScreenCapture.CaptureWindow(window);

        Assert.NotNull(frame);
        Assert.Null(workspace.CaretLine());

        window.Close();
    }
}
