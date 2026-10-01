using VCCad.Geometry;

namespace VCCad.Core.Model;

/// <summary>What a tablet's raw signal is turned into.</summary>
public enum DynamicsTarget
{
    /// <summary>Pressure to width - the one everybody means by "tablet support".</summary>
    Width,

    /// <summary>Pressure to opacity.</summary>
    Opacity,

    /// <summary>Pressure to the scale of a scatter brush's copies.</summary>
    ScatterScale,

    /// <summary>Tilt to the angle of a calligraphic nib.</summary>
    CalligraphicAngle,

    /// <summary>How much the derived speed is smoothed before it is used.</summary>
    Smoothing,
}

/// <summary>The named curves a person picks from, before editing one.</summary>
public enum DynamicsPreset
{
    /// <summary>Output follows input exactly.</summary>
    Linear,

    /// <summary>Slow to start, then rises - light pressure stays light for longer.</summary>
    Soft,

    /// <summary>Rises quickly and then levels off - most of the range is reached early.</summary>
    Hard,

    /// <summary>The slowest start of the four, for the most control at low pressure.</summary>
    Exponential,
}

/// <summary>
/// A response curve, as the two control points of a cubic Bezier from (0,0) to (1,1).
///
/// **Control points rather than a formula**, because the issue asks for a curve editor and this is the
/// representation an editor draws and drags: the same four numbers every vector tool uses for an easing curve, so
/// the panel is a pair of handles rather than a set of coefficients. It also means a preset is data rather than
/// code - four named pairs - and a test can check a preset against its definition instead of against a formula
/// someone typed twice.
///
/// `Evaluate` takes an **x** and returns a **y**, which is the direction the input goes: x is what the tablet
/// reported, y is what the curve makes of it.
/// </summary>
public sealed record DynamicsCurve(double X1, double Y1, double X2, double Y2)
{
    /// <summary>The curve that changes nothing.</summary>
    public static DynamicsCurve Linear { get; } = new(0.0, 0.0, 1.0, 1.0);

    /// <summary>The control points a preset is defined by.</summary>
    public static DynamicsCurve FromPreset(DynamicsPreset preset) => preset switch
    {
        DynamicsPreset.Soft => new DynamicsCurve(0.4, 0.05, 0.85, 0.5),
        DynamicsPreset.Hard => new DynamicsCurve(0.15, 0.5, 0.6, 0.95),
        DynamicsPreset.Exponential => new DynamicsCurve(0.6, 0.02, 1.0, 0.6),
        _ => Linear,
    };

    /// <summary>
    /// The output for an input in 0..1, clamped outside it.
    ///
    /// The Bezier is parameterised by its own t, not by x, so y at a given x has to be found: bisection rather
    /// than a closed form, because the closed form has a degenerate case that divides by zero when the control
    /// points make the curve vertical, and a response curve is not worth a special case. Forty steps is exact to
    /// well under a millionth for the whole range, and the loop is the same length every time - which is what
    /// keeps the result identical on every machine.
    /// </summary>
    public double Evaluate(double x)
    {
        if (double.IsNaN(x))
        {
            return 0.0;
        }

        double target = Math.Clamp(x, 0.0, 1.0);
        if (target <= 0.0)
        {
            return 0.0;
        }

        if (target >= 1.0)
        {
            return 1.0;
        }

        double low = 0.0;
        double high = 1.0;
        for (int i = 0; i < 40; i++)
        {
            double mid = (low + high) / 2.0;
            if (Component(mid, X1, X2) < target)
            {
                low = mid;
            }
            else
            {
                high = mid;
            }
        }

        return Component((low + high) / 2.0, Y1, Y2);
    }

    /// <summary>One axis of a cubic Bezier from 0 to 1, with its two control values.</summary>
    private static double Component(double t, double c1, double c2)
    {
        double u = 1.0 - t;
        return (3.0 * u * u * t * c1) + (3.0 * u * t * t * c2) + (t * t * t);
    }
}

/// <summary>One target's dynamics: whether it is on, and the curve it follows.</summary>
public sealed record DynamicsTargetSpec(bool Enabled, DynamicsCurve Curve)
{
    /// <summary>An enabled target following a preset.</summary>
    public static DynamicsTargetSpec Preset(DynamicsPreset preset)
        => new(true, DynamicsCurve.FromPreset(preset));

    /// <summary>A target that never varies.</summary>
    public static DynamicsTargetSpec Off { get; } = new(false, DynamicsCurve.Linear);
}

/// <summary>One raw sample from a pen, as the input plumbing reports it.</summary>
public readonly record struct InputSample(
    Point2D Position,
    double Time,
    double Pressure = 1.0,
    double TiltX = 0.0,
    double TiltY = 0.0);

/// <summary>
/// Turns raw pen samples into the values a stroke varies by.
///
/// **Speed is derived, not reported.** The platform sends positions and times, not speed, so it is computed from
/// successive samples - which means it is as jittery as the samples are, and a jittery speed produces a visibly
/// jittery line. Smoothing it is therefore part of deriving it rather than a refinement afterwards, which is why
/// it has its own tests.
///
/// Everything here is a pure function of the samples: no state, no time source, nothing read from the document.
/// A derivation that consulted a clock would produce a different line on a slow machine than on a fast one.
/// </summary>
public static class StrokeDynamics
{
    /// <summary>
    /// The speed at each sample, in points per unit time, smoothed over a window.
    ///
    /// The first sample has no predecessor and so no speed; it takes the second sample's, because a stroke that
    /// started fast should not start with a zero that the curve then turns into no width at all.
    /// </summary>
    public static double[] Speeds(IReadOnlyList<InputSample> samples, int smoothing = 3)
    {
        var raw = new double[samples.Count];
        if (samples.Count == 0)
        {
            return raw;
        }

        for (int i = 1; i < samples.Count; i++)
        {
            double dt = samples[i].Time - samples[i - 1].Time;
            double dx = samples[i].Position.X - samples[i - 1].Position.X;
            double dy = samples[i].Position.Y - samples[i - 1].Position.Y;
            double distance = Math.Sqrt((dx * dx) + (dy * dy));

            // A repeated sample, or two samples stamped at the same instant, has no speed rather than an infinite
            // one - and dividing by it would produce exactly that.
            raw[i] = dt > 1e-9 ? distance / dt : 0.0;
        }

        if (samples.Count > 1)
        {
            raw[0] = raw[1];
        }

        return Smooth(raw, smoothing);
    }

    /// <summary>
    /// A centred moving average over the window.
    ///
    /// Centred rather than trailing, so the smoothed value at a sample describes the stroke around it rather than
    /// only what came before - a trailing average lags, and the lag shows up as a line that thins after the corner
    /// instead of through it. A window of one leaves the input alone, which is what makes smoothing optional.
    /// </summary>
    public static double[] Smooth(IReadOnlyList<double> values, int smoothing)
    {
        var smoothed = new double[values.Count];
        if (values.Count == 0)
        {
            return smoothed;
        }

        int window = Math.Max(1, smoothing);
        if (window == 1)
        {
            for (int i = 0; i < values.Count; i++)
            {
                smoothed[i] = values[i];
            }

            return smoothed;
        }

        int half = window / 2;
        for (int i = 0; i < values.Count; i++)
        {
            double total = 0.0;
            int count = 0;
            for (int j = i - half; j <= i + half; j++)
            {
                if (j < 0 || j >= values.Count)
                {
                    continue;
                }

                total += values[j];
                count++;
            }

            smoothed[i] = count == 0 ? values[i] : total / count;
        }

        return smoothed;
    }

    /// <summary>
    /// The width multiplier at each sample: pressure through the width curve, times speed through it when the
    /// speed target is on.
    ///
    /// Multiplied rather than chosen between, because the two answer different questions - pressure is how hard
    /// the pen is pressed and speed is how fast it is moving - and a stroke that lightened under pressure *and*
    /// thinned when drawn quickly should do both.
    /// </summary>
    public static double[] WidthScales(
        IReadOnlyList<InputSample> samples,
        DynamicsSpec? dynamics,
        int smoothing = 3)
    {
        var scales = new double[samples.Count];
        if (samples.Count == 0)
        {
            return scales;
        }

        DynamicsSpec spec = dynamics ?? DynamicsSpec.None;
        double[] speeds = Speeds(samples, smoothing);
        double fastest = speeds.Length > 0 ? speeds.Max() : 0.0;

        for (int i = 0; i < samples.Count; i++)
        {
            // A target that is **off** contributes a factor of one, not the pressure itself. Passing the input
            // through is the right answer for a curve preview and the wrong answer here: with no dynamics at all a
            // 5%-pressure sample would draw at 5% width, which is the opposite of ignoring pressure.
            double fromPressure = spec.For(DynamicsTarget.Width).Enabled
                ? spec.Apply(DynamicsTarget.Width, samples[i].Pressure)
                : 1.0;

            // Speed is normalised against the fastest sample of this stroke, because "fast" is relative: the same
            // speed is quick for a careful sketch and slow for a huge one, and an absolute threshold would make a
            // stroke's appearance depend on the document's units rather than on how it was drawn.
            double fromSpeed = spec.For(DynamicsTarget.Smoothing).Enabled && fastest > 1e-9
                ? spec.Apply(DynamicsTarget.Smoothing, speeds[i] / fastest)
                : 1.0;

            scales[i] = fromPressure * fromSpeed;
        }

        return scales;
    }

    /// <summary>
    /// A width profile built from the samples, so that what was drawn with pressure can be stored, exported and
    /// re-opened as an ordinary profile.
    ///
    /// This is where dynamics meet the rest of the stroke subsystem: a stroke does not keep its pressure, it keeps
    /// the **width profile** the pressure produced. That also means a drawing made with a tablet is exported as
    /// real geometry rather than as a note saying how it was made.
    /// </summary>
    public static WidthProfileSpec WidthProfile(
        IReadOnlyList<InputSample> samples,
        double baseWidth,
        DynamicsSpec? dynamics,
        string name = "Pressure",
        int smoothing = 3)
    {
        double[] scales = WidthScales(samples, dynamics, smoothing);
        var points = new List<WidthPoint>(samples.Count);

        for (int i = 0; i < samples.Count; i++)
        {
            double position = samples.Count <= 1 ? 0.0 : i / (double)(samples.Count - 1);
            double width = Math.Max(0.0, baseWidth * scales[i]);
            points.Add(WidthPoint.Even(position, width));
        }

        return new WidthProfileSpec(name, points);
    }

    /// <summary>
    /// The nib angle in degrees: the direction the pen is laid over in.
    ///
    /// **A pen that is not tilted is not at an arbitrary angle.** `atan2(0, 0)` is zero, so mapping the direction
    /// through the curve would put an upright pen at the middle of the range - and the middle of 0..180 is 90, a
    /// nib standing on its end. An upright pen gets zero degrees, and the direction is only consulted once there is
    /// a direction to consult.
    /// </summary>
    public static double CalligraphicAngle(InputSample sample, DynamicsSpec? dynamics)
    {
        DynamicsSpec spec = dynamics ?? DynamicsSpec.None;
        if (!spec.For(DynamicsTarget.CalligraphicAngle).Enabled)
        {
            return 0.0;
        }

        if (Math.Abs(sample.TiltX) < 1e-9 && Math.Abs(sample.TiltY) < 1e-9)
        {
            return 0.0;
        }

        double tilt = Math.Atan2(sample.TiltY, sample.TiltX);
        double normalised = (tilt + Math.PI) / (2.0 * Math.PI);
        return spec.Apply(DynamicsTarget.CalligraphicAngle, normalised) * 180.0;
    }
}
///
/// Curves are **document state**, because a curve is how a drawing is meant to respond to a pen - the same reason
/// a width profile is. A document that did not carry its curves would draw differently on the machine that
/// happened to have the right settings.
///
/// The five targets are all "input in, output out" through a curve, which is why they are one type rather than
/// five: width and opacity differ in what the output is used for, not in how it is derived. A
/// <see cref="DynamicsSpec"/> that is null on a stroke means no dynamics at all, which is not the same as one with
/// every target switched off - the first stores nothing, the second stores a decision.
/// </summary>
public sealed class DynamicsSpec
{
    private readonly DynamicsTargetSpec[] _targets;

    public DynamicsSpec(IEnumerable<DynamicsTargetSpec> targets)
        => _targets = targets.ToArray();

    /// <summary>The spec an untouched stroke has: nothing varies.</summary>
    public static DynamicsSpec None { get; } = new(Enum.GetValues<DynamicsTarget>().Select(_ => DynamicsTargetSpec.Off));

    /// <summary>Pressure drives width, following a preset - the setup nearly every pen user wants.</summary>
    public static DynamicsSpec PressureToWidth(DynamicsPreset preset = DynamicsPreset.Soft)
    {
        var targets = Enum.GetValues<DynamicsTarget>().Select(_ => DynamicsTargetSpec.Off).ToArray();
        targets[(int)DynamicsTarget.Width] = DynamicsTargetSpec.Preset(preset);
        return new DynamicsSpec(targets);
    }

    /// <summary>The spec for one target, or an off one when the list is short. Never null.</summary>
    public DynamicsTargetSpec For(DynamicsTarget target)
    {
        int index = (int)target;
        return index >= 0 && index < _targets.Length ? _targets[index] : DynamicsTargetSpec.Off;
    }

    /// <summary>Whether anything at all varies.</summary>
    public bool IsEmpty => _targets.All(t => !t.Enabled);

    /// <summary>The output for an input on one target: the curve when it is on, the input itself when it is not.</summary>
    public double Apply(DynamicsTarget target, double input)
    {
        DynamicsTargetSpec spec = For(target);
        return spec.Enabled ? spec.Curve.Evaluate(input) : Math.Clamp(input, 0.0, 1.0);
    }

    public bool Equals(DynamicsSpec? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (_targets.Length != other._targets.Length)
        {
            return false;
        }

        for (int i = 0; i < _targets.Length; i++)
        {
            if (_targets[i] != other._targets[i])
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj) => Equals(obj as DynamicsSpec);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (DynamicsTargetSpec target in _targets)
        {
            hash.Add(target);
        }

        return hash.ToHashCode();
    }
}
