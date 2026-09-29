namespace VCCad.Core.Input;

/// <summary>
/// Sends each event to several sinks, for a replay that must reach the real input path *and*
/// be kept.
///
/// Recording a batch that is being played is exactly this: the event goes to the window so
/// something happens, and to the recorder so the same session can be saved and replayed. Without
/// a tee the caller would have to choose one, which is how a played batch ends up unrecordable.
/// </summary>
public sealed class TeeInputSink : IInputSink
{
    private readonly IInputSink[] _sinks;

    /// <summary>Sends every event to each sink, in the order given.</summary>
    public TeeInputSink(params IInputSink[] sinks)
    {
        ArgumentNullException.ThrowIfNull(sinks);
        _sinks = sinks;

        foreach (IInputSink sink in _sinks)
        {
            ArgumentNullException.ThrowIfNull(sink);
        }
    }

    /// <inheritdoc />
    public void Send(InputEvent input)
    {
        foreach (IInputSink sink in _sinks)
        {
            sink.Send(input);
        }
    }
}
