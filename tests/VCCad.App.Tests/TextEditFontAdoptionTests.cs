using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using VCCad.App.Controls;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// Opening a text block adopts the font **at the caret**.
///
/// The editor holds a face and a size for new text, and the text controls show them. Before this they stayed
/// at whatever new text was configured as, so the panel said one thing while the block was set in another and
/// the first character typed arrived in the wrong face. A block can hold several runs, so the answer is not
/// "the block's font" - it is the font of the character the caret is next to.
/// </summary>
public class TextEditFontAdoptionTests
{
    private static (Window Window, CanvasWorkspace Workspace, EditorViewModel Vm, TextItem Text) Host()
    {
        var vm = new EditorViewModel();
        var workspace = new CanvasWorkspace();
        workspace.AttachEditor(vm);

        var window = new Window { Width = 900, Height = 700, Content = workspace };
        window.Show();
        Settle();

        var text = new TextItem { Origin = new Point2D(100, 100) };
        text.Runs.Add(new TextRun { Text = "TITLE", FontFamily = "Face A", FontSize = 36 });
        text.Runs.Add(new TextRun { Text = "caption", FontFamily = "Face B", FontSize = 12 });
        vm.Document.Artboards[0].Layers[0].AddItem(text);
        vm.SelectObject(text);
        Settle();

        return (window, workspace, vm, text);
    }

    private static void Settle()
    {
        for (int i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>Clicking into the second run adopts the second run's face and size, not the first's.</summary>
    [AvaloniaFact]
    public void OpeningNearTheSecondRunAdoptsItsFont()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel vm, TextItem text) = Host();
        try
        {
            // Set the editor somewhere else first, so the assertion is about adoption rather than about the
            // values happening to be right.
            vm.DefaultFontFamily = "Somewhere Else";
            vm.DefaultFontSize = 9;

            Rect2D box = text.BoundingBox();
            var inside = new Point2D(box.Right - 4, box.Top + (box.Height / 2));
            workspace.EditAt(inside);
            Settle();

            Assert.True(vm.IsEditingText, "the block should be open");
            Assert.Equal("Face B", vm.DefaultFontFamily);
            Assert.Equal(12.0, vm.DefaultFontSize, 3);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>And entering at the very start adopts the run of the character after the caret.</summary>
    [AvaloniaFact]
    public void OpeningAtTheStartAdoptsTheFirstRun()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel vm, TextItem text) = Host();
        try
        {
            vm.DefaultFontFamily = "Somewhere Else";
            vm.DefaultFontSize = 9;

            Rect2D box = text.BoundingBox();
            workspace.EditAt(new Point2D(box.Left + 0.5, box.Top + (box.Height / 2)));
            Settle();

            Assert.True(vm.IsEditingText, "the block should be open");
            Assert.Equal("Face A", vm.DefaultFontFamily);
            Assert.Equal(36.0, vm.DefaultFontSize, 3);
        }
        finally
        {
            window.Close();
        }
    }
}
