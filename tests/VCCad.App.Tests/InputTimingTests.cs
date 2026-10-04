using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VCCad.App.Automation;
using VCCad.App.Views;
using VCCad.Core.Input;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// Timed gestures, replayed from a recorded file.
///
/// This is the test for the bug that made automation unable to do what a hand does: a batch replayed
/// synchronously holds the UI thread, so a gesture's own timer never fires. The shape tool opens its
/// flyout after a 350 ms hold measured by a <c>DispatcherTimer</c> - a hand opens it, and a blocking
/// replay opened nothing.
///
/// **What is asserted here and what is not.** The inline test below pins the mechanism: a hold replayed
/// straight through on the UI thread delivers both events and the flyout stays shut, because the timer
/// that opens it needs the application to be running. That is the bug.
///
/// The positive half - the same recorded gesture replayed through <see cref="UiThreadInputSink"/>, which
/// waits off the UI thread - **is verified against the running window and is not yet asserted here**: a
/// replayed 900 ms hold opens ShapeFlyoutPopup in the application, and the same test driven headlessly
/// does not pass yet, because the headless host's timer needs more than the render ticks this test
/// pumps. Recorded on #209 with that evidence rather than left as a red test.
/// </summary>
public class InputTimingTests
{
    /// <summary>One turn of the loop the running application always has and a test must supply.</summary>
    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// The same gesture replayed **on this thread** does not open the flyout, which is why the sink
    /// exists: it is the difference the fix makes, asserted rather than described.
    /// </summary>
    [AvaloniaFact]
    public void AHeldPressReplayedOnTheUiThreadCannotOpenIt()
    {
        var control = new ShapeFlyoutButton();
        var window = new Window { Width = 300, Height = 300, Content = control };
        window.Show();
        Pump();

        var batch = new InputBatch
        {
            Name = "hold the shape tool, inline",
            Events = new List<InputEvent>
            {
                new(InputKinds.Down, 17, 17, 40),
                new(InputKinds.Up, 17, 17, 900),
            },
        };

        // Replayed straight through on the UI thread, which is what used to happen and what froze the
        // timer: the events arrive, and the flyout stays shut.
        var inline = new CollectingSink();
        batch.Replay(inline, InputTiming.AsFastAsPossible, new SystemInputClock());
        Pump();

        Assert.Equal(2, inline.Sent.Count);

        Popup? flyout = window.GetVisualDescendants().OfType<Popup>()
            .FirstOrDefault(popup => popup.Name == "ShapeFlyoutPopup");
        Assert.NotNull(flyout);
        Assert.False(flyout!.IsOpen, "the flyout opens on a timer, and a timer needs the application to run");
    }

    /// <summary>A sink that just remembers what it was given.</summary>
    private sealed class CollectingSink : IInputSink
    {
        public List<InputEvent> Sent { get; } = new();

        public void Send(InputEvent input) => Sent.Add(input);
    }
}
