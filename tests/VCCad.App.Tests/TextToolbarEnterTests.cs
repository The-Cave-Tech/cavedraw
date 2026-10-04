using System;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using VCCad.App.ViewModels;
using VCCad.App.Views;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// Enter commits, in every field of the text toolbar.
///
/// The size field always did; the tracking fields only committed when focus left them, so a value typed and
/// confirmed with Enter stayed in the box and the text did not change - and then the value arrived later, when
/// the person moved to another field for an unrelated reason (#216). A test that sets a property and reads the
/// model afterwards cannot see that, because it never blurs anything either: it has to press the key a person
/// presses.
///
/// Two things this class learned the hard way, both recorded because the next person will meet them:
///
/// 1. EditorView creates and keeps its own EditorViewModel and ignores DataContext. An earlier version of this
///    test built a view model of its own, so the fields changed one model and the assertions read another - and
///    all three tests failed, including the blur case that had always worked.
/// 2. The window is closed after every test. Three of these left a live window in the shared headless session
///    and eighty-four other tests in this suite failed, which only shows up when the whole suite runs.
/// </summary>
public class TextToolbarEnterTests : IDisposable
{
    private Window? _window;

    public void Dispose()
    {
        _window?.Close();
        _window = null;
        Settle();
    }

    private static void Settle()
    {
        for (int i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private (EditorView View, EditorViewModel Model, TextItem Text) Host()
    {
        var view = new EditorView();
        _window = new Window { Width = 900, Height = 700, Content = view };
        _window.Show();
        Settle();

        EditorViewModel viewModel = view.ViewModel;
        var text = new TextItem { Origin = new Point2D(10, 10) };
        text.Runs.Add(new TextRun { Text = "Sample", FontSize = 12, FontFamily = "Nimbus Sans" });
        viewModel.Document.Artboards[0].Layers[0].AddItem(text);
        viewModel.SelectObject(text);
        Settle();

        return (view, viewModel, text);
    }

    private static void Enter(TextBox box)
    {
        box.Text = box.Text ?? string.Empty;
        box.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = Key.Enter,
        });
        Settle();
    }

    private static double Degrees(double radians) => radians * 180.0 / Math.PI;

    [AvaloniaFact]
    public void EnterInTheRotationFieldTurnsTheTextWithoutLosingFocus()
    {
        (EditorView view, _, TextItem text) = Host();
        TextBox rotation = view.FindControl<TextBox>("TtRotation")!;

        rotation.Text = "45";
        Enter(rotation);

        Assert.Equal(45.0, Degrees(text.RotationRadians), 3);
    }

    [AvaloniaFact]
    public void EnterInTheLineSpacingFieldChangesTheLeadingWithoutLosingFocus()
    {
        (EditorView view, _, TextItem text) = Host();
        TextBox spacing = view.FindControl<TextBox>("TtLineSpacing")!;

        spacing.Text = "2.5";
        Enter(spacing);

        Assert.Equal(2.5, text.LineSpacing, 3);
    }

    /// <summary>
    /// Paragraph spacing, on a block that really has two paragraphs, commits on Enter.
    ///
    /// This is #217: the field was reported as inert, and it was not - the commit was, because it only committed
    /// when focus left it. The change that fixed #216 fixed this too, and this is the test that says so rather
    /// than my word for it.
    /// </summary>
    [AvaloniaFact]
    public void EnterInTheParagraphSpacingFieldStoresItOnATwoParagraphBlock()
    {
        (EditorView view, _, TextItem text) = Host();
        text.Runs[0].Text = "One\nTwo";
        Settle();

        TextBox spacing = view.FindControl<TextBox>("TtParagraphSpacing")!;
        spacing.Text = "12";
        Enter(spacing);

        Assert.Equal(12.0, text.ParagraphSpacing, 3);
    }

    /// <summary>
    /// Leaving a field still commits - Enter is an addition, not a replacement, and a person who types and then
    /// clicks elsewhere must not lose the value.
    /// </summary>
    [AvaloniaFact]
    public void LeavingTheRotationFieldStillCommits()
    {
        (EditorView view, _, TextItem text) = Host();
        TextBox rotation = view.FindControl<TextBox>("TtRotation")!;

        rotation.Text = "30";
        rotation.RaiseEvent(new RoutedEventArgs(InputElement.LostFocusEvent));
        Settle();

        Assert.Equal(30.0, Degrees(text.RotationRadians), 3);
    }
}
