using Avalonia.Threading;
using VCCad.Core.Input;

namespace VCCad.App.Automation;

/// <summary>
/// Delivers replayed events on the UI thread while the waiting happens on another.
///
/// This is the fix for timed gestures, and it is the reason a replayed hold behaves like a hand's. The
/// shape tool opens its flyout after a 350 ms hold measured by a <c>DispatcherTimer</c>. When a batch
/// was replayed synchronously, the UI thread sat inside the operation, the platform timer was never
/// signalled, and the hold did nothing - measured: a 900 ms hold in one blocking batch opened nothing,
/// while pressing, waiting *outside* the application, and releasing opened <c>ShapeFlyoutPopup</c>.
///
/// So the wait moved off the UI thread. Every event is still delivered *on* it, because that is where
/// input belongs; the sleeps between them happen on the caller's thread, which is what leaves the
/// application free to run its own timers.
///
/// It is public to the test assembly so a test can replay a recorded gesture the same way the
/// application does, rather than through a second implementation that might behave differently.
/// </summary>
internal sealed class UiThreadInputSink : IInputSink
{
    private readonly IInputSink _inner;
    private readonly Action? _sent;

    /// <summary>Wraps a sink so every event it receives is delivered on the UI thread.</summary>
    /// <param name="inner">Where the event goes once it is on the UI thread.</param>
    /// <param name="sent">Optional: called after each delivery, for progress reporting.</param>
    public UiThreadInputSink(IInputSink inner, Action? sent = null)
    {
        _inner = inner;
        _sent = sent;
    }

    /// <inheritdoc />
    public void Send(InputEvent input)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            _inner.Send(input);
        }
        else
        {
            // Blocking here is right: this thread has nothing else to do, and the UI thread is free
            // precisely because the caller is not holding it while it waits.
            Dispatcher.UIThread.Invoke(() => _inner.Send(input));
        }

        _sent?.Invoke();
    }
}
