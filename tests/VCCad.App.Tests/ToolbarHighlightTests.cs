using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VCCad.App.Automation;
using VCCad.App.ViewModels;
using VCCad.App.Views;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// The toolbar says which tool is active, whatever changed it.
///
/// The claim is narrow and easy to get wrong in a way nobody notices: a shortcut key or an operation sets
/// the tool without any click on the toolbar, and the highlight has to follow anyway. It does, because the
/// highlight reads `EditorViewModel.Tool` rather than the button that was pressed - but that is a fact about
/// the wiring, and this pins it so a change to either half fails here.
///
/// The active button is found by **contrast**, not by reading a private brush: among the tool buttons, one
/// is painted differently from all the rest, and that one has to be the tool that is set. A first attempt
/// used "not transparent" as the test for active, which is true of about a third of the buttons in the
/// window - the assertion failed on "New document" and "Bold".
/// </summary>
public class ToolbarHighlightTests
{
    private static (Window Window, EditorView View, EditorViewModel ViewModel) Host()
    {
        var view = new EditorView();
        var window = new Window { Width = 1100, Height = 800, Content = view };
        window.Show();
        Settle();
        return (window, view, view.ViewModel);
    }

    private static void Settle()
    {
        for (int i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.GetVisualChildren().OfType<Control>())
        {
            yield return child;
            foreach (Control descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    private static string? GetTip(Control control) => ToolTip.GetTip(control) as string;

    /// <summary>The toolbar's own buttons, identified by the tips the layout table declares.</summary>
    private static List<Button> ToolButtons(EditorView view)
    {
        string[] tips = ToolbarLayout.All
            .Where(e => e.Tip is not null)
            .Select(e => e.Tip!)
            .ToArray();

        // The compound shape button names the armed shape after its key - "Shapes (S): star (hold for the
        // rest)" - so it is matched on its stable prefix rather than exactly.
        bool IsToolTip(string? tip)
            => tip is not null &&
               (tips.Contains(tip) || tip.StartsWith("Shapes (S)", StringComparison.Ordinal));

        return Descendants(view)
            .OfType<Button>()
            .Where(b => IsToolTip(GetTip(b)))
            .ToList();
    }

    private static Button ToolButtonFor(EditorView view, string key)
        => ToolButtons(view).First(b => (GetTip(b) ?? string.Empty).Contains($"({key})"));

    /// <summary>
    /// The button painted differently from the rest. There is exactly one by construction - if the toolbar
    /// highlighted two tools, or none, this returns null and the caller's assertion says so.
    /// </summary>
    private static Button? ActiveButton(EditorView view)
    {
        List<Button> buttons = ToolButtons(view);

        foreach (Button button in buttons)
        {
            if (buttons.All(other => ReferenceEquals(other, button) || !SamePaint(other, button)))
            {
                return button;
            }
        }

        return null;
    }

    private static bool SamePaint(Button a, Button b)
        => (a.Background as ISolidColorBrush)?.Color == (b.Background as ISolidColorBrush)?.Color;

    /// <summary>Setting the tool highlights its button, and only its button.</summary>
    [AvaloniaTheory]
    [InlineData(EditorTool.Node, "A")]
    [InlineData(EditorTool.Pencil, "N")]
    [InlineData(EditorTool.Corner, "C")]
    [InlineData(EditorTool.Lasso, "Q")]
    [InlineData(EditorTool.Ellipse, "L")]
    [InlineData(EditorTool.Rectangle, "M")]
    [InlineData(EditorTool.Text, "T")]
    public void SettingTheToolHighlightsItsButton(EditorTool tool, string key)
    {
        (Window window, EditorView view, EditorViewModel viewModel) = Host();
        try
        {
            viewModel.Tool = tool;
            Settle();

            Button? active = ActiveButton(view);

            Assert.NotNull(active);
            Assert.Contains($"({key})", GetTip(active!) ?? string.Empty);

            // And it really is the one for this tool, not merely the only one painted.
            Assert.Same(ToolButtonFor(view, key), active);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// A shortcut moves the highlight, which is the half that had a real defect: the lasso's tip advertised
    /// `Q` for months and the key was never bound, so the toolbar could not have followed it.
    /// </summary>
    [AvaloniaFact]
    public void AShortcutMovesTheHighlight()
    {
        (Window window, EditorView view, EditorViewModel viewModel) = Host();
        try
        {
            var workspace = Descendants(view).OfType<VCCad.App.Controls.CanvasWorkspace>().First();
            workspace.Focus();
            Settle();

            viewModel.Tool = EditorTool.Select;
            Settle();
            Assert.Contains("(V)", GetTip(ActiveButton(view)!) ?? string.Empty);

            InputInjection.Key(workspace, Avalonia.Input.Key.Q, Avalonia.Input.KeyModifiers.None);
            Settle();

            Assert.Equal(EditorTool.Lasso, viewModel.Tool);
            Assert.Contains("(Q)", GetTip(ActiveButton(view)!) ?? string.Empty);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The shape button is on the toolbar and answers to its shortcut like every other tool.</summary>
    [AvaloniaFact]
    public void TheShapeShortcutHighlightsTheCompoundButton()
    {
        (Window window, EditorView view, EditorViewModel viewModel) = Host();
        try
        {
            var workspace = Descendants(view).OfType<VCCad.App.Controls.CanvasWorkspace>().First();
            workspace.Focus();
            Settle();

            viewModel.Tool = EditorTool.Select;
            Settle();

            InputInjection.Key(workspace, Avalonia.Input.Key.S, Avalonia.Input.KeyModifiers.None);
            Settle();

            Assert.Equal(EditorTool.Shape, viewModel.Tool);

            // The compound button draws the armed shape, so its tip names the shape rather than a tool.
            Button? active = ActiveButton(view);
            Assert.NotNull(active);
            Assert.Contains("Shapes", GetTip(active!) ?? string.Empty);
        }
        finally
        {
            window.Close();
        }
    }
}
