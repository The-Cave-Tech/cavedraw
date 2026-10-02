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
/// **A vertical column's selection runs down the column.**
///
/// The highlight is computed from `TextMetrics`, which pairs `XOf(i)` - the across position, which a column does not
/// change as its pen runs down - with the line's top, which every character on the line shares. So the width
/// collapses to its half-pixel floor and the selection draws as a dot at the top of the column.
///
/// The selection is made with `text.select`, which is the route a driver is meant to use: it reaches the canvas's
/// own `_caret`/`_editAnchor`, which is what the painter reads. Setting the view model's properties does not, and a
/// scripted synthetic double-click does not either - both were tried and neither moved anything.
/// </summary>
public class VerticalSelectionBoundsTests
{
    private const int Width = 900, Height = 700;

    private static JsonElement Params(object value) => JsonSerializer.SerializeToElement(value);

    private static void Settle()
    {
        for (int i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    /// <summary>Opens the block and selects all of it, then reports the boxes the canvas would paint.</summary>
    private static Rect2D? SelectionBox(TextWritingMode mode)
    {
        var viewModel = new EditorViewModel();
        var workspace = new CanvasWorkspace();
        workspace.AttachEditor(viewModel);

        var window = new Window { Width = Width, Height = Height, Content = workspace };
        window.Show();
        Settle();

        var text = new TextItem { Name = "label", Origin = new Point2D(120, 120), WritingMode = mode };
        text.Runs.Add(new TextRun { Text = "abc", FontSize = 36 });
        viewModel.Document.Artboards[0].Layers[0].AddItem(text);
        viewModel.SelectObject(text);
        Settle();

        var context = new AutomationContext { ViewModel = viewModel, InputRoot = () => workspace };

        EditorOperations.Invoke(context, "text.edit", Params(new { }));
        Settle();

        EditorOperations.Invoke(context, "text.select", Params(new { start = 0, end = 3 }));
        Settle();

        Assert.True(viewModel.IsEditingText, "text.edit must open the block");

        return workspace.TextSelectionBounds();
    }

    /// <summary>
    /// **The plumbing, asserted.** `text.select` reaches the canvas's own range and the boxes come back - which is
    /// the part that took three attempts to establish, and the part that is right today.
    /// </summary>
    [AvaloniaFact]
    public void ASelectionReportsItsBoxes()
    {
        Rect2D? box = SelectionBox(TextWritingMode.HorizontalTb);

        Assert.True(box is not null, "a selection must report boxes; null means the range did not reach the canvas");
        Assert.True(box!.Value.Width > 0 && box.Value.Height > 0, "the boxes must have an extent");
    }

    /// <summary>
    /// **The acceptance.** A column's characters are separated down the page, so its selection boxes must be taller
    /// than they are wide. It was measured at **64.80 wide by 43.20 tall** before the metrics took a vertical
    /// block's positions from the glyphs - the layout's own `GlyphBox.X` across and `GlyphBox.Y` down - and swapped
    /// the box dimensions in the painter and this readout together.
    /// </summary>
    [AvaloniaFact]
    public void AVerticalSelectionPaintsABoxTallerThanItIsWide()
    {
        Rect2D? box = SelectionBox(TextWritingMode.VerticalRl);

        Assert.True(box is not null, "the selection must report boxes; null means the range did not reach the canvas");

        Assert.True(box!.Value.Height > box.Value.Width,
            $"a column's selection must run down the page: it reported " +
            $"{box.Value.Width:F2} wide by {box.Value.Height:F2} tall");
    }
}
