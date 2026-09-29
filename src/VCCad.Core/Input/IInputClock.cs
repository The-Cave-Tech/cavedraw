using System.Diagnostics;

namespace VCCad.Core.Input;

/// <summary>
/// The clock a replay waits against.
///
/// It is an interface so timing can be tested without sleeping: a test clock advances when it is
/// asked to wait, and records what it was asked, so "the deltas were honoured" is an assertion
/// rather than a stopwatch reading.
/// </summary>
public interface IInputClock
{
    /// <summary>Time since the replay began.</summary>
    TimeSpan Elapsed { get; }

    /// <summary>Waits for the requested interval.</summary>
    void Wait(TimeSpan delay);
}

/// <summary>The wall clock a real replay uses.</summary>
public sealed class SystemInputClock : IInputClock
{
    private readonly Stopwatch _watch = Stopwatch.StartNew();

    /// <inheritdoc />
    public TimeSpan Elapsed => _watch.Elapsed;

    /// <inheritdoc />
    public void Wait(TimeSpan delay)
    {
        if (delay > TimeSpan.Zero)
        {
            Thread.Sleep(delay);
        }
    }
}
