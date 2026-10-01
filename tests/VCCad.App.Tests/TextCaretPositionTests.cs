using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using VCCad.App.Controls;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Core.Text;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// The caret sits **between** characters.
///
/// It is drawn from the same table the glyphs are placed from, so the two cannot disagree about where a
/// boundary is - and this asserts that, rather than asserting a pixel. With the caret at index *i*, the
/// boundary it draws at must be the advance width of the characters before it: the sum of what has already
/// been printed, which is exactly where the next character will start.
///
/// A caret that is one character out is the thing this pins: it would sit at the boundary *after* the
/// character the person clicked, which reads as the insertion point belonging to the character on its right.
/// </summary>
public class TextCaretPositionTests
{
    private static (Window Window, CanvasWorkspace Workspace, EditorViewModel Vm, TextItem Text) Host(string content)
    {
        var vm = new EditorViewModel();
        var workspace = new CanvasWorkspace();
        workspace.AttachEditor(vm);

        var window = new Window { Width = 900, Height = 700, Content = workspace };
        window.Show();
        Settle();

        var text = new TextItem { Origin = new Point2D(100, 200) };
        text.Runs.Add(new TextRun { Text = content, FontSize = 24 });
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

    /// <summary>Clicking inside a character puts the caret at the boundary nearest the click.</summary>
    [AvaloniaFact]
    public void TheCaretIsAtTheAdvanceOfWhatComesBeforeIt()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel vm, TextItem text) = Host("ABCDEF");
        try
        {
            TextLayout layout = TextLayoutEngine.Compute(text);

            // The layout's own boundaries: `caretX[i]` is where a caret at index i belongs.
            Assert.True(layout.CaretX.Count >= 3, "the layout has caret positions");
            double advanceOfFirst = layout.CaretX[1] - layout.CaretX[0];
            Assert.True(advanceOfFirst > 0, "the first character advances the pen");

            // Click in the left quarter of the first character: the boundary before it is nearest.
            workspace.EditAt(new Point2D(text.Origin.X + (advanceOfFirst * 0.25), text.Origin.Y + 12));
            Settle();
            Assert.True(vm.IsEditingText, "the block is open");

            (Point topAtStart, _) = workspace.CaretLine()!.Value;

            // Click in the right quarter of the first character: the boundary after it is nearest.
            workspace.EditAt(new Point2D(text.Origin.X + (advanceOfFirst * 0.75), text.Origin.Y + 12));
            Settle();

            (Point topAtOne, _) = workspace.CaretLine()!.Value;

            // The caret moved by exactly one character's advance - not two, and not zero.
            double shown = Math.Abs(topAtOne.X - topAtStart.X);
            double expected = advanceOfFirst * workspace.Zoom;
            Assert.Equal(expected, shown, 1);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The same property read straight off the layout: each boundary is the sum of the advances before it, so
    /// the eleventh boundary of a line is the width of the first ten characters.
    /// </summary>
    [Fact]
    public void EachBoundaryIsTheSumOfTheAdvancesBeforeIt()
    {
        var text = new TextItem();
        text.Runs.Add(new TextRun { Text = "ABCDEFGHIJ", FontSize = 20 });
        TextLayout layout = TextLayoutEngine.Compute(text);

        for (int i = 1; i < layout.CaretX.Count; i++)
        {
            Assert.True(layout.CaretX[i] >= layout.CaretX[i - 1],
                $"boundary {i} must not go backwards");
        }

        double width = layout.CaretX[^1] - layout.CaretX[0];
        Assert.True(width > 0, "ten characters have a width");
        Assert.Equal(text.BoundingBox().Width, width, 1);
    }
}
