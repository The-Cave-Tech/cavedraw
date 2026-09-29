using VCCad.Core.Model;

namespace VCCad.Core.Input;

/// <summary>
/// Records events as they are delivered, so a batch that was played can be kept.
///
/// It is an <see cref="IInputSink"/> like any other: drop it in where the editor's real input
/// path goes and a session — a person's or a replayed batch — comes out as one file. The deltas
/// are measured against the same <see cref="IInputClock"/> the replay waited on, so recording a
/// replay in <see cref="InputTiming.RealTime"/> reproduces the original deltas rather than a
/// stopwatch's noise. Under <see cref="InputTiming.AsFastAsPossible"/> nothing was actually
/// waited for, and the recorded deltas are honestly zero.
/// </summary>
public sealed class InputRecorder : IInputSink
{
    private readonly IInputClock _clock;
    private readonly List<InputEvent> _events = new();
    private TimeSpan _lastAt;

    /// <summary>Starts a recorder against a clock; the system clock when none is given.</summary>
    public InputRecorder(IInputClock? clock = null)
    {
        _clock = clock ?? new SystemInputClock();
        _lastAt = _clock.Elapsed;
    }

    /// <summary>What has been recorded so far, in order.</summary>
    public IReadOnlyList<InputEvent> Events => _events;

    /// <summary>Records one event, stamping it with its delta from the one before.</summary>
    public void Send(InputEvent input)
    {
        ArgumentNullException.ThrowIfNull(input);

        TimeSpan now = _clock.Elapsed;
        double delta = (now - _lastAt).TotalMilliseconds;

        if (!double.IsFinite(delta) || delta < 0)
        {
            delta = 0;
        }

        _lastAt = now;
        _events.Add(input with { DeltaMs = delta });
    }

    /// <summary>Forgets everything recorded.</summary>
    public void Clear()
    {
        _events.Clear();
        _lastAt = _clock.Elapsed;
    }

    /// <summary>Finishes the recording, optionally keeping the document it happened on.</summary>
    public InputBatch Finish(
        string? name = null,
        CadDocument? document = null,
        string? focusedArtboard = null,
        IReadOnlyList<string>? expected = null) => new()
        {
            Name = name,
            Document = document,
            FocusedArtboard = focusedArtboard,
            Expected = expected ?? Array.Empty<string>(),
            Events = _events.ToList(),
        };
}
