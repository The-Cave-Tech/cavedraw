using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using VCCad.App.Controls;
using VCCad.App.ViewModels;

namespace VCCad.App.Views;

/// <summary>
/// The document's undo and redo shortcuts, wherever focus happens to be.
///
/// The canvas handles Ctrl+Z itself, which is right while the canvas is focused - and it is not,
/// much of the time. A field in a panel takes the key for its own text undo, and any other
/// focused control lets it go nowhere at all, so the shortcut silently died and only came back
/// when the person clicked on the artwork again. That is the defect this removes.
///
/// It is a TUNNELING handler so the document sees the key before the focused control does, with
/// two deliberate exceptions: the canvas, which has its own text-editing key path, and a
/// multi-line field that has edits of its own, where the person is writing prose and means the
/// field's undo.
/// </summary>
public static class UndoShortcuts
{
    /// <summary>Installs the shortcuts on a root, so everything inside it is covered.</summary>
    public static void Install(InputElement root, EditorViewModel viewModel)
    {
        root.AddHandler(
            InputElement.KeyDownEvent,
            (_, e) => TryHandle(e, viewModel),
            RoutingStrategies.Tunnel);
    }

    /// <summary>True when the key was the document's and has been handled.</summary>
    public static bool TryHandle(KeyEventArgs e, EditorViewModel viewModel)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            return false;
        }

        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        bool undo = e.Key == Key.Z && !shift;
        bool redo = e.Key == Key.Y || (e.Key == Key.Z && shift);
        if (!undo && !redo)
        {
            return false;
        }

        // **An open text edit owns the key, wherever the focus is** (issue #252). Deferring is not enough here:
        // this is a tunneling handler on the window root, so returning false only lets the key reach the canvas
        // when the canvas happens to be on its route - and the ordinary case is that focus is in the text panel or
        // the toolbar, where the canvas never sees it and the document undo runs against a stack that has no entry
        // for the edit in progress. So the key is given to the canvas that is editing.
        if (viewModel.IsEditingText)
        {
            CanvasWorkspace? editing = e.Source is Visual visual
                ? TopLevel.GetTopLevel(visual)?.GetVisualDescendants().OfType<CanvasWorkspace>().FirstOrDefault()
                : null;

            if (editing is not null && editing.StepTextEdit(redo))
            {
                e.Handled = true;
                return true;
            }
        }

        // The canvas owns this key: it routes it into its own text editing.
        if (e.Source is CanvasWorkspace)
        {
            return false;
        }

        // A prose field with edits of its own keeps them.
        if (e.Source is TextBox { AcceptsReturn: true } field && (field.CanUndo || field.CanRedo))
        {
            return false;
        }

        if (redo)
        {
            viewModel.Redo();
        }
        else
        {
            viewModel.Undo();
        }

        e.Handled = true;
        return true;
    }
}
