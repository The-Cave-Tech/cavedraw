namespace VCCad.App.Controls;

/// <summary>
/// Tells one wheel notch from the next.
///
/// A wheel is a discrete control with click-in-place positions, so one click should be one step. The
/// events that reach us are not one per click, though: a smooth-scrolling driver - and a free-spinning
/// wheel - delivers a short **burst** of whole-notch deltas for a single physical click. The diary
/// from a real mouse shows six of them inside 100 ms, with gaps of 300 ms and more between clicks.
///
/// Applying a full step to each event made one click overshoot by 1.1^6, which is the whole
/// complaint: the artwork flew away, and there was no way to be precise because the smallest
/// available movement was six times too big.
///
/// So a run of whole-notch deltas closer together than <see cref="_burstGapMs"/> counts as ONE
/// notch. A trackpad is a different animal and is not routed through here: it reports fractional
/// deltas, one per frame, and those are meant to accumulate smoothly - see
/// <c>CanvasWorkspace.OnPointerWheelChanged</c>.
/// </summary>
public sealed class WheelNotches
{
    private double _burstGapMs;
    private long _lastMs = long.MinValue;

    /// <param name="burstGapMs">
    /// How close two whole-notch events have to be to belong to the same click. The mouse this was
    /// measured on delivers a click's events 8-32 ms apart and then waits at least 300 ms, so
    /// anything in the tens of milliseconds separates clicks without merging a deliberate spin - a
    /// fast wheel turned by hand sends a notch every 100 ms or so, and each of those is its own.
    /// </param>
    public WheelNotches(double burstGapMs = 60)
    {
        _burstGapMs = burstGapMs;
    }

    /// <summary>
    /// How close two whole-notch events have to be to belong to one click. Settable so a test can make
    /// the answer deterministic instead of racing the wall clock.
    /// </summary>
    public double BurstGapMs
    {
        get => _burstGapMs;
        set => _burstGapMs = value;
    }

    /// <summary>
    /// Whether this whole-notch event starts a new click, or is another event from the one just
    /// handled. Advances the clock as a side effect: ask once per event.
    /// </summary>
    public bool BeginsClick(long nowMs)
    {
        bool begins = _lastMs == long.MinValue || nowMs - _lastMs > _burstGapMs;
        _lastMs = nowMs;
        return begins;
    }

    /// <summary>Forgets the last event, so the next one is always treated as a new click.</summary>
    public void Reset() => _lastMs = long.MinValue;
}
