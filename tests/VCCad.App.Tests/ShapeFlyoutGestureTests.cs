using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using VCCad.App.Automation;
using VCCad.App.ViewModels;
using VCCad.App.Views;
using VCCad.Core.Model;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// The flyout's gesture rules, driven through the **real pointer path**.
///
/// These tests could not be written until the control's handlers were attached correctly. They were attached
/// with `+=`, and a `Button`'s own class handler marks a pointer press as handled on the way through - that
/// is how it tracks a click - and a plain subscription is skipped once an event is handled. So every
/// gesture rule below was dead code: the flyout could not be opened by a long press **in the application
/// either**, not merely in a test. The counters that proved it are still on the control.
///
/// One rule is driven by the entry button's Click rather than by a pointer: the entries live in a `Popup`,
/// which has its own visual tree, so a synthetic pointer from the window cannot reach them. That is stated
/// here rather than glossed, because it is the one place these tests do not go through the pointer.
/// </summary>
public class ShapeFlyoutGestureTests
{
    private static (Window Window, ShapeFlyoutButton Button, EditorViewModel ViewModel) Host()
    {
        var viewModel = new EditorViewModel();
        var button = new ShapeFlyoutButton { PressMilliseconds = 10 };
        button.Attach(viewModel);

        var window = new Window { Width = 600, Height = 400, Content = button };
        window.Show();
        Settle();
        return (window, button, viewModel);
    }

    private static void Settle()
    {
        for (int i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    /// <summary>
    /// Pumps the dispatcher until a condition holds or the budget runs out.
    ///
    /// The long press is a real `DispatcherTimer`, and a timer only expires if its clock is allowed to
    /// advance and the dispatcher is then given a chance to run what it posted. Sleeping without pumping
    /// leaves the callback queued forever; pumping without sleeping never reaches the due time. Both have to
    /// happen, which is why this is not a `RunJobs()` call.
    /// </summary>
    private static void PumpUntil(Func<bool> condition, int budgetMs = 3000)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && clock.ElapsedMilliseconds < budgetMs)
        {
            // All three, because a dispatcher timer in the headless platform is driven by the render tick
            // rather than by the job queue: advancing the clock and draining jobs is not enough on its own.
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }

        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>The face's centre, in window coordinates - where a person would aim.</summary>
    private static Point FaceCentre(Window window, ShapeFlyoutButton button)
        => button.Face.TranslatePoint(
            new Point(button.Face.Bounds.Width / 2, button.Face.Bounds.Height / 2), window) ?? new Point(0, 0);

    /// <summary>A short click arms the shape already showing; it does not open the flyout.</summary>
    [AvaloniaFact]
    public void AShortClickArmsTheToolWithoutOpening()
    {
        (Window window, ShapeFlyoutButton button, EditorViewModel viewModel) = Host();
        try
        {
            Point at = FaceCentre(window, button);

            InputInjection.Press(window, at.X, at.Y, shift: false);
            InputInjection.Release(window, at.X, at.Y);
            Settle();

            Assert.Equal(1, button.PressCount);
            Assert.Equal(1, button.ReleaseCount);
            Assert.False(button.IsFlyoutOpen);
            Assert.Equal(EditorTool.Shape, viewModel.Tool);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// **A long press opens the flyout.** This is the rule the whole flyout exists for, and the one that was
    /// dead code until the handlers were attached with `handledEventsToo`.
    ///
    /// The interval is shortened so the test is not slow, but it is a real `DispatcherTimer` on its own
    /// path: nothing here calls `OpenFlyout`, because a test that calls it proves only that `OpenFlyout`
    /// works.
    /// </summary>
    [AvaloniaFact]
    public void ALongPressOpensTheFlyout()
    {
        (Window window, ShapeFlyoutButton button, EditorViewModel viewModel) = Host();
        try
        {
            button.PressMilliseconds = 20;
            Point at = FaceCentre(window, button);

            InputInjection.Press(window, at.X, at.Y, shift: false);

            Assert.Equal(1, button.PressCount);
            Assert.False(button.IsFlyoutOpen, "the flyout should not be open the instant the button goes down");

            PumpUntil(() => button.IsFlyoutOpen);

            Assert.True(button.IsFlyoutOpen, "holding the button should open the flyout");

            // Opening is not choosing. The tool is not armed and the shape is not changed, because a person
            // who opens the flyout and then dismisses it must end up exactly where they started - which is
            // the rule `ClosingWithoutChoosingChangesNothing` pins from the other side.
            Assert.Equal(EditorTool.Select, viewModel.Tool);
            Assert.Equal(ShapeKind.Rectangle, viewModel.CurrentShape);

            InputInjection.Release(window, at.X, at.Y);
            Settle();
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// Releasing on the button after a long press leaves the flyout open. The press that opened it was not a
    /// choice, and reading its release as one would open and dismiss the flyout in a single gesture.
    /// </summary>
    [AvaloniaFact]
    public void ReleasingOnTheButtonLeavesTheFlyoutOpen()
    {
        (Window window, ShapeFlyoutButton button, EditorViewModel viewModel) = Host();
        try
        {
            button.PressMilliseconds = 20;
            Point at = FaceCentre(window, button);

            InputInjection.Press(window, at.X, at.Y, shift: false);
            PumpUntil(() => button.IsFlyoutOpen);
            Assert.True(button.IsFlyoutOpen);

            InputInjection.Release(window, at.X, at.Y);
            Settle();

            Assert.True(button.IsFlyoutOpen, "releasing on the button should not dismiss the flyout");
            Assert.Equal(ShapeKind.Rectangle, viewModel.CurrentShape);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// Choosing an entry arms that shape and closes the flyout. Driven by the entry's Click rather than a
    /// pointer, because the entries are in a popup with its own visual tree.
    /// </summary>
    [AvaloniaFact]
    public void ChoosingAnEntryArmsItAndClosesTheFlyout()
    {
        (Window window, ShapeFlyoutButton button, EditorViewModel viewModel) = Host();
        try
        {
            button.OpenFlyout();
            Assert.True(button.IsFlyoutOpen);

            Button entry = button.Entries.First(e => e.Name == "ShapeFlyoutHeart");
            entry.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Settle();

            Assert.Equal(ShapeKind.Heart, viewModel.CurrentShape);
            Assert.Equal(EditorTool.Shape, viewModel.Tool);
            Assert.False(button.IsFlyoutOpen);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Closing without choosing changes nothing: cancelling is not a choice.</summary>
    [AvaloniaFact]
    public void ClosingWithoutChoosingChangesNothing()
    {
        (Window window, ShapeFlyoutButton button, EditorViewModel viewModel) = Host();
        try
        {
            button.OpenFlyout();
            Assert.True(button.IsFlyoutOpen);

            button.CloseFlyout();

            Assert.False(button.IsFlyoutOpen);
            Assert.Equal(ShapeKind.Rectangle, viewModel.CurrentShape);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Every entry closes the flyout, whichever shape it is.</summary>
    [AvaloniaFact]
    public void EveryEntryClosesTheFlyout()
    {
        (Window window, ShapeFlyoutButton button, EditorViewModel viewModel) = Host();
        try
        {
            foreach (Button entry in button.Entries)
            {
                button.OpenFlyout();
                entry.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                Settle();

                Assert.False(button.IsFlyoutOpen, $"{entry.Name} should close the flyout");
                Assert.Equal(EditorTool.Shape, viewModel.Tool);
            }
        }
        finally
        {
            window.Close();
        }
    }
}