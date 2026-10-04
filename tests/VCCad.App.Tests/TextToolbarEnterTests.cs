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
/// the person moved to another field for an unrelated reason (#216). That is invisible to a test that sets a
/// property and reads the model afterwards, because such a test never blurs anything either: it has to press
/// the key a person presses.
/// </summary>
public class TextToolbarEnterTests
{
    private static (EditorView View, EditorViewModel Model, TextItem Text) Host()
    {
        // The view makes its own view model and ignores DataContext, so the text has to go into **its** model:
        // the first version of this test wrote into a model of its own and asserted on that, which is why three
        // assertions failed with "nothing applied" - including the blur case that has always worked.
        var view = new EditorView();
        var window = new Window { Width = 900, Height = 700, Content = view };
        window.Show();
        Settle();

        EditorViewModel viewModel = view.ViewModel;
        var text = new TextItem { Origin = new Point2D(10, 10) };
        text.Runs.Add(new TextRun { Text = "Sample", FontSize = 12, FontFamily = "Nimbus Sans" });
        viewModel.Document.Artboards[0].Layers[0].AddItem(text);
        viewModel.SelectObject(text);
        Settle();

        return (view, viewModel, text);
    }

    private static void Settle()
    {
        for (int i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
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
