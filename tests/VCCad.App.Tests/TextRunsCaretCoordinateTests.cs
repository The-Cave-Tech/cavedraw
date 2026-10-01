using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using VCCad.App.Automation;
using VCCad.App.Controls;
using VCCad.App.ViewModels;
using VCCad.App.Views;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// `text.runs` reports the caret in **character offsets**, the coordinate its `selectionStart` and
/// `selectionEnd` already use, and separately reports the **run index** as `caretRun`.
///
/// It used to answer `caret` with the run index - a run index wearing a name that sits between two
/// character offsets - so a driver reading the reply had two coordinates under names that did not
/// distinguish them, and the one called `caret` was the wrong kind. The two coincide for the whole of
/// the first run (offset 1 is also run index 1 at a two-run block... which is the trap), so the fixture
/// places the caret **four characters into the second run**, where the two are 9 and 1 and cannot be
/// confused. `caretRun` is asserted as well as `caret`, because the fix is both: the old reply had no
/// `caretRun` at all, and asserting only the new `caret` would not pin that the run index still exists.
/// </summary>
public class TextRunsCaretCoordinateTests
{
    /// <summary>
    /// Four characters into the second run: "TITLE" is offsets 0-4, "caption" is 5-11, and the caret
    /// goes at 9, between "capt" and "ion".
    ///
    /// Deliberately not the join (offset 5) and not the second run's first position (offset 7): at the
    /// join `RunAt` names the **first** run, and at offset 1 the character offset and the run index are
    /// the same integer - the coincidence that let the misnomer sit here unnoticed.
    /// </summary>
    private const int CaretInsideTheSecondRun = 9;

    private static (Window Window, EditorView View, EditorViewModel Vm, CanvasWorkspace Workspace,
        TextItem Block) Host()
    {
        var view = new EditorView();
        var window = new Window { Width = 1100, Height = 800, Content = view };
        window.Show();
        Settle();

        EditorViewModel vm = view.ViewModel;
        CanvasWorkspace workspace = view.WorkspaceControl;

        // Two runs with different faces and sizes, so the flattened text is unambiguously in two pieces
        // and a reply that reported only the first run's index would be visible as such.
        var block = new TextItem { Origin = new Point2D(100, 120) };
        block.Runs.Add(new TextRun { Text = "TITLE", FontFamily = "No Such Family 158", FontSize = 36, Bold = true });
        block.Runs.Add(new TextRun { Text = "caption", FontFamily = "No Such Family 158", FontSize = 12, Italic = true });

        vm.Document.Artboards[0].Layers[0].AddItem(block);
        vm.SelectObject(block);
        Settle();

        return (window, view, vm, workspace, block);
    }

    private static void Settle()
    {
        for (int i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>
    /// Opens the block and puts the caret at an explicit character offset, the way a drag ends.
    ///
    /// The click only opens the edit; the offset is then published by `SetTextSelection`, so the caret
    /// this test asserts on is the one it asked for rather than whatever the click's glyph hit-test
    /// happened to produce. `TextToolbarCaretRunTests` documents why that matters: a layout pass
    /// re-syncs the caret from the drawn glyphs, so a test that settles between placing the caret and
    /// reading it can be right for the wrong reason.
    /// </summary>
    private static void OpenEditWithCaretAt(CanvasWorkspace workspace, TextItem block, int offset)
    {
        Rect2D box = block.BoundingBox();
        workspace.EditAt(new Point2D(box.Right - 4, box.Top + (box.Height / 2)));

        Assert.True(workspace.SetTextSelection(offset, offset),
            "the block should be open before the caret is placed");
    }

    /// <summary>
    /// With the caret four characters into the second run, `text.runs` says `caret` is 9 - a character
    /// offset - and `caretRun` is 1 - a run index.
    ///
    /// Against the code this test was written for, `caret` came back as 1: the run index, under the name
    /// the driver reads as an offset, with no `caretRun` to compare it to.
    /// </summary>
    [AvaloniaFact]
    public void TheCaretIsACharacterOffsetAndTheRunIndexIsReportedSeparately()
    {
        (Window window, EditorView _, EditorViewModel vm, CanvasWorkspace workspace, TextItem block) = Host();
        try
        {
            OpenEditWithCaretAt(workspace, block, CaretInsideTheSecondRun);

            Assert.True(vm.IsEditingText, "the block should be open - otherwise there is no caret to report");

            var context = new AutomationContext
            {
                ViewModel = vm,
                InputRoot = () => workspace,
            };

            // No settle between placing the caret and invoking the operation: the operation must report
            // the caret the person has, not one a layout pass has since re-derived.
            JsonElement result = JsonSerializer.SerializeToElement(
                EditorOperations.Invoke(context, "text.runs", JsonSerializer.SerializeToElement(new { })));

            // The fixture itself, so a failure elsewhere does not read as a caret bug: two runs, and the
            // caret's offset is inside the second one.
            JsonElement[] runs = result.GetProperty("runs").EnumerateArray().ToArray();
            Assert.Equal(2, runs.Length);
            Assert.Equal("TITLE", runs[0].GetProperty("text").GetString());
            Assert.Equal("caption", runs[1].GetProperty("text").GetString());

            // The character offset, in the same coordinate the neighbouring names use. This is the
            // assertion the old reply fails: it answered 1 here - the run index - while the caret really
            // sat at offset 9.
            int caret = result.GetProperty("caret").GetInt32();
            Assert.Equal(CaretInsideTheSecondRun, caret);
            Assert.Equal(9, caret);

            // And the run index, separate and named as one. The old reply had no `caretRun` at all.
            int caretRun = result.GetProperty("caretRun").GetInt32();
            Assert.Equal(1, caretRun);

            // Stated outright, because the whole defect was two coordinates under names that did not
            // distinguish them: for this caret the two are not the same number.
            Assert.NotEqual(caretRun, caret);
        }
        finally
        {
            window.Close();
        }
    }
}
