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
/// Where a click lands inside a rotated text block.
///
/// The block is painted through a rotation about its origin, so anything that works out a position
/// inside it has to use the same one - and has to measure against the block's own upright extent,
/// not the axis-aligned box its rotation produces. Comparing the two frames made every click on a
/// turned label read as a miss, which sent the caret to the end of the block instead of the word
/// under the pointer.
/// </summary>
public class TextCaretPlacementTests
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

    private static TextItem Label(double x, double y, double rotation, string content)
    {
        var text = new TextItem { Name = "label", Origin = new Point2D(x, y), RotationRadians = rotation };
        text.Runs.Add(new TextRun { Text = content, FontSize = 12 });
        return text;
    }

    private static void Add(EditorViewModel viewModel, LayerItem item)
    {
        viewModel.Document.Artboards[0].Layers[0].AddItem(item);
        Settle();
    }

    /// <summary>The block's own extents, with no rotation folded in.</summary>
    private static Rect2D Upright(TextItem text)
    {
        var flat = (TextItem)text.Clone();
        flat.RotationRadians = 0;
        return flat.LocalBounds();
    }

    /// <summary>Where a point of the block's own space is drawn on the page.</summary>
    private static Point2D DrawnAt(TextItem text, double localX, double localY)
    {
        Point turned = new Point(localX, localY).Transform(Matrix.CreateRotation(text.RotationRadians));
        return new Point2D(text.Origin.X + turned.X, text.Origin.Y + turned.Y);
    }

    [AvaloniaTheory]
    [InlineData(Math.PI / 2)]
    [InlineData(-Math.PI / 2)]
    [InlineData(Math.PI)]
    [InlineData(0.4)]
    [InlineData(0)]
    public void AClickOnALabelLandsOnTheCharacterUnderIt(double rotation)
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel) = Host();
        try
        {
            TextItem text = Label(200, 200, rotation, "ABCDEFGHIJ");
            Add(viewModel, text);

            Rect2D box = Upright(text);
            int length = TextEditing.Length(text);

            // Halfway along the block, in the middle of its height: where the glyphs are.
            Point2D middle = DrawnAt(text, box.Width / 2, box.Height / 2);

            Assert.Equal(EditTarget.Text, workspace.EditAt(middle));
            Assert.InRange(viewModel.TextSelectionStart, (length / 2) - 2, (length / 2) + 2);

            // And the far end, so the answer follows the click rather than sitting at a midpoint.
            Point2D nearEnd = DrawnAt(text, box.Width - 2, box.Height / 2);
            Assert.Equal(EditTarget.Text, workspace.EditAt(nearEnd));
            Assert.True(viewModel.TextSelectionStart >= length - 2,
                $"the caret should be near the end, was {viewModel.TextSelectionStart} of {length}");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// A press inside the open block moves the caret; a press outside it closes the block. Getting
    /// the frame wrong made a click on the text itself close it.
    /// </summary>
    [AvaloniaFact]
    public void APressInsideAnOpenRotatedBlockKeepsItOpen()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel) = Host();
        try
        {
            TextItem text = Label(200, 200, Math.PI / 2, "ABCDEFGHIJ");
            Add(viewModel, text);

            Rect2D box = Upright(text);
            Assert.Equal(EditTarget.Text, workspace.EditAt(DrawnAt(text, box.Width / 2, box.Height / 2)));

            Point inside = workspace.ModelToWindow(DrawnAt(text, box.Width / 4, box.Height / 2));
            InputInjection.Press(window, inside.X, inside.Y, shift: false);
            InputInjection.Release(window, inside.X, inside.Y);
            Settle();

            Assert.True(viewModel.IsEditingText, "a press on the text must not close it");
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
