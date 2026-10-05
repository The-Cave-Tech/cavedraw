using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using VCCad.App.Controls;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// Every caret offset moves the drawn caret (issue #254).
///
/// The model's offset is right at every step - `text.caret` reports 9, 10, 11, 12, 13 for successive presses - but
/// the **drawn** caret does not follow it: measured on the running application, offsets 8 to 13 give
/// 511.7, 579.2, 646.8, 646.8, 712.8, 712.8, so two of the five presses leave the screen unchanged and the rendered
/// frames for those pairs are byte-identical.
///
/// This asserts the value the painter reads - `CanvasWorkspace.CaretLine`, which `PaintTextCaret` draws directly -
/// rather than the offset, because the offset was never the problem. It is the assertion this issue should have had
/// the first time, instead of one about the layout engine, which is a different calculation.
/// </summary>
public class CaretMovesForEveryOffsetTests
{
    /// <summary>The Lillie header as the importer builds it: seven pieces, each with its own advance.</summary>
    private static TextItem ImportedHeader()
    {
        var text = new TextItem { Name = "header", Origin = new Point2D(60, 80) };
        // **The advances the file actually states**, read from the running application with text.runs - not the
        // run's own measured width, which is what the first version of this fixture used and why it could not
        // reproduce anything. Each is the distance to the next piece, a TJ adjustment's gap included:
        Add(text, "Jalie ", 21.006);
        Add(text, "3464 ", 22.518);
        Add(text, "- ", 5.499);
        Add(text, "LILLIE ", 28.521);
        Add(text, "- ", 5.499);
        Add(text, "Page ", 23.517);
        Add(text, "1/12", 17.514);
        return text;

        static void Add(TextItem text, string piece, double advance)
            => text.Runs.Add(new TextRun
            {
                Text = piece,
                FontFamily = "Nimbus Sans",
                FontSize = 9,
                AdvanceWidth = advance,
            });
    }

    private static (Window Window, CanvasWorkspace Workspace, TextItem Text) Host()
    {
        var viewModel = new EditorViewModel();
        var workspace = new CanvasWorkspace();
        workspace.AttachEditor(viewModel);

        var window = new Window { Width = 1000, Height = 700, Content = workspace };
        window.Show();
        Settle();

        TextItem text = ImportedHeader();
        viewModel.Document.Artboards[0].Layers[0].AddItem(text);
        Settle();

        Assert.True(workspace.BeginTextEdit(text), "the block could not be opened for editing");
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

    [AvaloniaFact]
    public void EveryOffsetMovesTheDrawnCaret()
    {
        (Window window, CanvasWorkspace workspace, TextItem text) = Host();

        var positions = new List<(int Offset, double X)>();
        for (int offset = 6; offset <= 14; offset++)
        {
            Assert.True(workspace.SetTextSelection(offset, offset), $"the caret would not go to offset {offset}");
            Settle();

            (Point Top, Point Bottom)? line = workspace.CaretLine();
            Assert.NotNull(line);
            positions.Add((offset, line!.Value.Top.X));
        }

        // **Each offset is further right than the one before it.** The defect is that two of them are not.
        for (int i = 1; i < positions.Count; i++)
        {
            (int previous, double previousX) = positions[i - 1];
            (int offset, double x) = positions[i];

            Assert.True(
                x > previousX + 0.5,
                $"the caret at offset {previous} is at x={previousX:0.#} and at offset {offset} is at x={x:0.#} - " +
                "a press of Right that moves nothing on screen");
        }

        window.Close();
    }
}
