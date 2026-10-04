using System.Diagnostics;
using Avalonia.Threading;
using VCCad.Core.Input;

namespace VCCad.App.Automation;

/// <summary>
/// A clock that **keeps the interface alive while a replayed gesture waits**.
///
/// Replaying a batch synchronously on the UI thread froze the very timers a gesture depends on. A long
/// press is a *hold* that a <see cref="DispatcherTimer"/> measures: the shape tool opens its flyout
/// after 350 ms of holding, and pressing it by hand works, but a batch that slept for its deltas left
/// the dispatcher no chance to tick, so the flyout never opened for a driver. The same freeze hides
/// every other timed behaviour - a tooltip, a repeat button, a double-click - and it makes a held
/// press meaningless, because a press that holds while the application is stopped is not a hold.
///
/// So the wait is served in slices and the dispatcher is pumped between them. A gesture that waits
/// 900 ms is then 900 ms of a *running* application rather than 900 ms of a frozen one, which is the
/// only way a replayed gesture and a hand's gesture can come to the same thing.
///
/// The clock lives here rather than in <c>VCCad.Core</c> because pumping a dispatcher means knowing
/// about Avalonia, and Core does not.
/// </summary>
internal sealed class DispatchingInputClock : IInputClock
{
    /// <summary>How long a single sleep lasts before the dispatcher gets a turn.</summary>
    private static readonly TimeSpan Slice = TimeSpan.FromMilliseconds(10);

    private readonly SystemInputClock _inner = new();

    /// <inheritdoc />
    public TimeSpan Elapsed => _inner.Elapsed;

    /// <inheritdoc />
    public void Wait(TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero)
        {
            return;
        }

        TimeSpan remaining = delay;
        while (remaining > TimeSpan.Zero)
        {
            TimeSpan step = remaining < Slice ? remaining : Slice;
            Thread.Sleep(step);
            remaining -= step;

            // Only the UI thread can pump it, and a batch is normally replayed there.
            if (Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.RunJobs();
            }
        }
    }
}
