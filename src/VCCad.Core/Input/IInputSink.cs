namespace VCCad.Core.Input;

/// <summary>
/// Where a replayed event goes.
///
/// The engine is deliberately not an input implementation of its own: it says *when* an event
/// should arrive, and hands it to whatever the running application uses for real input. In the
/// editor that sink forwards to the same injection path a person's events take, so a batch and
/// a hand cannot diverge; in a test it is a recorder or an assertion.
/// </summary>
public interface IInputSink
{
    /// <summary>Delivers one event, now.</summary>
    void Send(InputEvent input);
}
