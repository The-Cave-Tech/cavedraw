using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
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
/// The document's undo shortcut, wherever focus happens to be.
///
/// It used to be handled by the canvas alone, so a field in a panel took Ctrl+Z for its own text
/// undo and the document did not undo until the person clicked back on the artwork. That is the
/// reported symptom: translate a selection, then Ctrl+Z does nothing until it is deselected.
/// </summary>
public class UndoShortcutTests
{
    /// <summary>A document with one moved rectangle - one undo step on the stack.</summary>
    private static (EditorViewModel ViewModel, PathItem Rect) MovedRect()
    {
        var viewModel = new EditorViewModel();
        Layer layer = viewModel.Document.Artboards[0].Layers[0];
        PathItem rect = PathFactory.CreateRectangle("rect", new Rect2D(0, 0, 50, 50));
        layer.AddItem(rect);

        var context = new AutomationContext { ViewModel = viewModel };
        EditorOperations.Invoke(context, "selection.set",
            JsonSerializer.SerializeToElement(new { itemIds = new[] { rect.Id } }));
        EditorOperations.Invoke(context, "object.move",
            JsonSerializer.SerializeToElement(new { dx = 120, dy = 0 }));

        return (viewModel, rect);
    }

    private static KeyEventArgs CtrlZ(object source) => new()
    {
        RoutedEvent = InputElement.KeyDownEvent,
        Key = Key.Z,
        KeyModifiers = KeyModifiers.Control,
        Source = source,
    };

    [AvaloniaFact]
    public void TheDocumentUndoesWhileAPanelFieldHasFocus()
    {
        (EditorViewModel viewModel, PathItem rect) = MovedRect();
        Assert.Equal(120, rect.WorldBounds().X, 3);

        // A single-line field with focus, like the Transform pane's X box.
        var field = new TextBox();
        var window = new Window { Width = 400, Height = 300, Content = field };
        window.Show();
        Settle();
        try
        {
            UndoShortcuts.Install(window, viewModel);
            field.Focus();
            Settle();

            field.RaiseEvent(CtrlZ(field));
            Settle();

            Assert.Equal(0, rect.WorldBounds().X, 3);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A pane's list has focus and the key must still reach the document.</summary>
    [AvaloniaFact]
    public void TheDocumentUndoesWhileAListHasFocus()
    {
        (EditorViewModel viewModel, PathItem rect) = MovedRect();

        var list = new ListBox { ItemsSource = new[] { "a", "b", "c" } };
        var window = new Window { Width = 400, Height = 300, Content = list };
        window.Show();
        Settle();
        try
        {
            UndoShortcuts.Install(window, viewModel);
            list.Focus();
            Settle();

            Assert.True(UndoShortcuts.TryHandle(CtrlZ(list), viewModel));
            Assert.Equal(0, rect.WorldBounds().X, 3);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The canvas is left alone: it routes the key into its own text editing, and its own handler
    /// is what performs the undo.
    /// </summary>
    [AvaloniaFact]
    public void TheCanvasKeepsItsOwnHandling()
    {
        var viewModel = new EditorViewModel();
        var canvas = new CanvasWorkspace();
        canvas.AttachEditor(viewModel);

        var window = new Window { Width = 400, Height = 300, Content = canvas };
        window.Show();
        Settle();
        try
        {
            UndoShortcuts.Install(window, viewModel);

            Assert.False(UndoShortcuts.TryHandle(CtrlZ(canvas), viewModel));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// A multi-line field with edits of its own keeps the shortcut - that is prose, and the
    /// field's undo is what the person means there.
    /// </summary>
    [AvaloniaFact]
    public void AProseFieldWithItsOwnHistoryKeepsCtrlZ()
    {
        var viewModel = new EditorViewModel();
        var field = new TextBox { AcceptsReturn = true, Text = "note" };
        var window = new Window { Width = 400, Height = 300, Content = field };
        window.Show();
        Settle();
        try
        {
            field.Focus();
            field.CaretIndex = field.Text!.Length;
            InputInjection.Type(field, " more");
            Settle();

            Assert.True(field.CanUndo, "the field must have something of its own to undo");
            Assert.False(UndoShortcuts.TryHandle(CtrlZ(field), viewModel));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// ...but a field with nothing to undo does not swallow the document's shortcut, which is the
    /// case that made the shortcut look dead.
    /// </summary>
    [AvaloniaFact]
    public void AProseFieldWithNothingToUndoStillLetsTheDocumentUndo()
    {
        (EditorViewModel viewModel, PathItem rect) = MovedRect();

        var field = new TextBox { AcceptsReturn = true };
        var window = new Window { Width = 400, Height = 300, Content = field };
        window.Show();
        Settle();
        try
        {
            field.Focus();
            Settle();
            Assert.False(field.CanUndo, "a field nobody has typed into has no history");

            UndoShortcuts.Install(window, viewModel);
            field.RaiseEvent(CtrlZ(field));
            Settle();

            Assert.Equal(0, rect.WorldBounds().X, 3);
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
