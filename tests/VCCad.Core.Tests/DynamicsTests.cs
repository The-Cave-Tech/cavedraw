using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// Tablet dynamics: the response curves, the derived speed, and what pressure does to a stroke's width.
///
/// The curves are the whole feature - everything else is plumbing - so they are tested at their ends, at known
/// points, and against their definitions rather than against a formula typed a second time. The speed tests are
/// the issue's own: speed is **derived** from position and time, so it is as jittery as the samples are, and
/// smoothing is part of deriving it rather than a refinement after the fact.
/// </summary>
public class DynamicsTests
{
    // ---------------------------------------------------------------- the curves

    [Fact]
    public void ACurvePassesThroughBothEnds()
    {
        foreach (DynamicsPreset preset in Enum.GetValues<DynamicsPreset>())
        {
            DynamicsCurve curve = DynamicsCurve.FromPreset(preset);

            // Whatever happens in between, no pressure must mean no output and full pressure must mean all of it.
            // A curve that missed either end would make a stroke start or finish at a width nobody asked for.
            Assert.Equal(0.0, curve.Evaluate(0.0), 9);
            Assert.Equal(1.0, curve.Evaluate(1.0), 9);
        }
    }

    [Fact]
    public void TheLinearCurveIsTheIdentity()
    {
        DynamicsCurve curve = DynamicsCurve.Linear;

        foreach (double x in new[] { 0.0, 0.1, 0.25, 0.5, 0.75, 0.9, 1.0 })
        {
            Assert.Equal(x, curve.Evaluate(x), 6);
        }
    }

    [Fact]
    public void ACurveIsMonotonic()
    {
        DynamicsCurve curve = DynamicsCurve.FromPreset(DynamicsPreset.Soft);
        double previous = -1.0;

        for (int i = 0; i <= 100; i++)
        {
            double y = curve.Evaluate(i / 100.0);
            Assert.True(y >= previous - 1e-9, $"the curve went backwards at {i}: {y} after {previous}");
            previous = y;
        }
    }

    [Fact]
    public void CurveInputsAreClamped()
    {
        DynamicsCurve curve = DynamicsCurve.FromPreset(DynamicsPreset.Hard);

        // A pressure below nothing or above everything is what it is nearest to, not an error and not an
        // extrapolation off the end of the curve.
        Assert.Equal(0.0, curve.Evaluate(-3.0), 9);
        Assert.Equal(1.0, curve.Evaluate(9.0), 9);
    }

    /// <summary>
    /// **The presets, against their definitions.** Each is a pair of control points, so the definition is those
    /// numbers - and what makes them meaningful is their order: at half pressure an exponential curve gives least
    /// and a hard one gives most.
    /// </summary>
    [Fact]
    public void ThePresetsAreOrderedAsTheirNamesSay()
    {
        double exponential = DynamicsCurve.FromPreset(DynamicsPreset.Exponential).Evaluate(0.5);
        double soft = DynamicsCurve.FromPreset(DynamicsPreset.Soft).Evaluate(0.5);
        double linear = DynamicsCurve.FromPreset(DynamicsPreset.Linear).Evaluate(0.5);
        double hard = DynamicsCurve.FromPreset(DynamicsPreset.Hard).Evaluate(0.5);

        Assert.True(exponential < soft, $"exponential should be the slowest start: {exponential} vs {soft}");
        Assert.True(soft < linear, $"soft should be below linear: {soft} vs {linear}");
        Assert.True(linear < hard, $"hard should be above linear: {linear} vs {hard}");
    }

    [Fact]
    public void ThePresetControlPointsAreWhatTheySayTheyAre()
    {
        Assert.Equal(new DynamicsCurve(0.0, 0.0, 1.0, 1.0), DynamicsCurve.FromPreset(DynamicsPreset.Linear));
        Assert.Equal(new DynamicsCurve(0.4, 0.05, 0.85, 0.5), DynamicsCurve.FromPreset(DynamicsPreset.Soft));
        Assert.Equal(new DynamicsCurve(0.15, 0.5, 0.6, 0.95), DynamicsCurve.FromPreset(DynamicsPreset.Hard));
        Assert.Equal(new DynamicsCurve(0.6, 0.02, 1.0, 0.6), DynamicsCurve.FromPreset(DynamicsPreset.Exponential));
    }

    // ---------------------------------------------------------------- the spec

    [Fact]
    public void AnOffTargetPassesItsInputThrough()
    {
        DynamicsSpec spec = DynamicsSpec.None;

        Assert.True(spec.IsEmpty);
        Assert.Equal(0.4, spec.Apply(DynamicsTarget.Width, 0.4), 6);
    }

    [Fact]
    public void PressureToWidthUsesTheCurve()
    {
        DynamicsSpec spec = DynamicsSpec.PressureToWidth(DynamicsPreset.Hard);

        Assert.False(spec.IsEmpty);
        Assert.Equal(
            DynamicsCurve.FromPreset(DynamicsPreset.Hard).Evaluate(0.5),
            spec.Apply(DynamicsTarget.Width, 0.5),
            6);

        // And the other targets are untouched, so turning width dynamics on does not turn everything on.
        Assert.Equal(0.5, spec.Apply(DynamicsTarget.Opacity, 0.5), 6);
    }

    // ---------------------------------------------------------------- speed

    /// <summary>
    /// Speed is distance over time between successive samples, so a stroke drawn twice as fast reports twice the
    /// speed - which is the only thing the samples can mean.
    /// </summary>
    [Fact]
    public void SpeedIsDistanceOverTime()
    {
        var samples = new[]
        {
            new InputSample(new Point2D(0, 0), 0.0),
            new InputSample(new Point2D(10, 0), 1.0),
            new InputSample(new Point2D(30, 0), 2.0),   // twice as far in the same time
        };

        double[] speeds = StrokeDynamics.Speeds(samples, smoothing: 1);

        Assert.Equal(10.0, speeds[1], 6);
        Assert.Equal(20.0, speeds[2], 6);
    }

    /// <summary>A sample with no time between it and the last has no speed, rather than an infinite one.</summary>
    [Fact]
    public void ARepeatedSampleHasNoSpeed()
    {
        var samples = new[]
        {
            new InputSample(new Point2D(0, 0), 0.0),
            new InputSample(new Point2D(5, 0), 0.0),
        };

        double[] speeds = StrokeDynamics.Speeds(samples, smoothing: 1);

        Assert.Equal(0.0, speeds[1], 9);
        Assert.False(double.IsInfinity(speeds[1]) || double.IsNaN(speeds[1]));
    }

    /// <summary>
    /// **Speed smoothing reduces the variance of a deliberately jittery input.** This is the test the issue asks
    /// for by name: the samples alternate slow and fast, which is what a hand-drawn line looks like numerically,
    /// and the smoothed series has to be calmer than the raw one without losing the overall shape.
    /// </summary>
    [Fact]
    public void SmoothingReducesTheVarianceOfAJitteryInput()
    {
        var samples = new List<InputSample> { new(new Point2D(0, 0), 0.0) };
        double x = 0.0;
        double time = 0.0;

        for (int i = 0; i < 20; i++)
        {
            // Alternate a short step and a long one, at a constant timestep: a jittery speed with a steady shape.
            x += i % 2 == 0 ? 1.0 : 14.0;
            time += 1.0;
            samples.Add(new InputSample(new Point2D(x, 0), time));
        }

        double[] raw = StrokeDynamics.Speeds(samples, smoothing: 1);
        double[] smoothed = StrokeDynamics.Speeds(samples, smoothing: 5);

        double rawVariance = Variance(raw.Skip(1).ToArray());
        double smoothedVariance = Variance(smoothed.Skip(1).ToArray());

        Assert.True(rawVariance > 0, "the input has to be jittery for this to mean anything");
        Assert.True(smoothedVariance < rawVariance / 2.0,
            $"smoothing should reduce the variance a lot: {rawVariance} became {smoothedVariance}");

        // And the average is broadly preserved: smoothing calms the signal rather than damping it. Not exactly,
        // because a centred window has fewer samples to average at each end and those ends are clamped rather than
        // wrapped - which pulls the mean slightly towards the edge values.
        Assert.Equal(raw.Skip(1).Average(), smoothed.Skip(1).Average(), raw.Skip(1).Average() * 0.05);
    }

    private static double Variance(double[] values)
    {
        double mean = values.Average();
        return values.Select(v => (v - mean) * (v - mean)).Average();
    }

    [Fact]
    public void AWindowOfOneLeavesTheSpeedAlone()
    {
        double[] values = { 1.0, 9.0, 2.0, 8.0 };

        Assert.Equal(values, StrokeDynamics.Smooth(values, 1));
    }

    // ---------------------------------------------------------------- width from pressure

    /// <summary>
    /// **A stroke drawn with varying pressure varies in width, in the direction the pressure went.** This is the
    /// headline behaviour, and it is asserted on the width profile the pressure produced - because that profile is
    /// what the stroke keeps, exports and re-opens.
    /// </summary>
    [Fact]
    public void MorePressureGivesMoreWidth()
    {
        double[] pressures = { 0.1, 0.3, 0.6, 1.0 };
        var samples = pressures
            .Select((p, i) => new InputSample(new Point2D(i * 10.0, 0), i, p))
            .ToArray();

        WidthProfileSpec profile = StrokeDynamics.WidthProfile(
            samples, baseWidth: 20, DynamicsSpec.PressureToWidth(DynamicsPreset.Linear));

        double[] widths = profile.Points.Select(p => p.LeftWidth).ToArray();

        // Monotonic, and the ends are the widths the pressures name: 0.1 of 20 then all of 20.
        for (int i = 1; i < widths.Length; i++)
        {
            Assert.True(widths[i] > widths[i - 1], $"width should grow with pressure: {widths[i - 1]} then {widths[i]}");
        }

        Assert.Equal(2.0, widths[0], 6);
        Assert.Equal(20.0, widths[^1], 6);
    }

    /// <summary>With no dynamics, pressure is ignored and the stroke is the width it was told to be.</summary>
    [Fact]
    public void WithoutDynamicsPressureIsIgnored()
    {
        var samples = new[]
        {
            new InputSample(new Point2D(0, 0), 0, 0.05),
            new InputSample(new Point2D(10, 0), 1, 1.0),
        };

        WidthProfileSpec profile = StrokeDynamics.WidthProfile(samples, baseWidth: 12, dynamics: null);

        Assert.All(profile.Points, point => Assert.Equal(12.0, point.LeftWidth, 6));
    }

    /// <summary>
    /// **Where a width point sits is distance travelled, not sample number.** A profile is read by arc length along
    /// the path, so samples bunched into the first twentieth of a line must not each claim an equal share of it -
    /// that is what puts the taper in a corner the pen only passed through.
    /// </summary>
    [Fact]
    public void WidthPointsSitWhereTheSamplesWereDrawn()
    {
        var samples = new[]
        {
            new InputSample(new Point2D(0, 0), 0),
            new InputSample(new Point2D(5, 0), 1),
            new InputSample(new Point2D(95, 0), 2),
            new InputSample(new Point2D(100, 0), 3),
        };

        double[] positions = StrokeDynamics.Positions(samples);

        Assert.Equal(new[] { 0.0, 0.05, 0.95, 1.0 }, positions.Select(p => Math.Round(p, 6)).ToArray());
    }

    /// <summary>A pen held still is at no distance along the line, so it shares a position rather than a NaN.</summary>
    [Fact]
    public void SamplesThatDoNotMoveHaveNoDistanceBetweenThem()
    {
        var samples = new[]
        {
            new InputSample(new Point2D(7, 7), 0),
            new InputSample(new Point2D(7, 7), 1),
            new InputSample(new Point2D(7, 7), 2),
        };

        double[] positions = StrokeDynamics.Positions(samples);

        Assert.All(positions, p => Assert.False(double.IsNaN(p) || double.IsInfinity(p)));
        Assert.Equal(0.0, positions[0], 9);
        Assert.Equal(1.0, positions[^1], 9);
    }

    // ---------------------------------------------------------------- tilt

    /// <summary>
    /// **Tilt changes the calligraphic angle**, and an untilted pen has none - which is what makes the value worth
    /// reporting rather than a constant.
    /// </summary>
    [Fact]
    public void TiltChangesTheCalligraphicAngle()
    {
        var untilted = new InputSample(new Point2D(0, 0), 0, 1.0, 0, 0);
        var tilted = new InputSample(new Point2D(0, 0), 0, 1.0, 45, 0);

        DynamicsSpec dynamics = new(new[]
        {
            DynamicsTargetSpec.Off,
            DynamicsTargetSpec.Off,
            DynamicsTargetSpec.Off,
            DynamicsTargetSpec.Preset(DynamicsPreset.Linear),
            DynamicsTargetSpec.Off,
        });

        Assert.Equal(0.0, StrokeDynamics.CalligraphicAngle(untilted, dynamics), 6);
        Assert.True(StrokeDynamics.CalligraphicAngle(tilted, dynamics) > 0.0,
            "a tilted pen should turn the nib");
    }

    /// <summary>And with the target off, tilt is ignored rather than half-applied.</summary>
    [Fact]
    public void TiltIsIgnoredWhenTheTargetIsOff()
    {
        var tilted = new InputSample(new Point2D(0, 0), 0, 1.0, 45, 20);

        Assert.Equal(0.0, StrokeDynamics.CalligraphicAngle(tilted, DynamicsSpec.None), 6);
        Assert.Equal(0.0, StrokeDynamics.CalligraphicAngle(tilted, null), 6);
    }

    /// <summary>
    /// **A whole stroke's nib angle comes from the mean tilt direction, not the mean of the angles.** A pen rolled
    /// either side of straight back is pointing backwards on average; averaging the two angles instead - one near
    /// +175, one near -175 - would call that straight ahead and stand the nib across the line.
    /// </summary>
    [Fact]
    public void AStrokesNibAngleIsTheMeanTiltDirection()
    {
        var rolled = new[]
        {
            new InputSample(new Point2D(0, 0), 0, 1.0, -170, 30),
            new InputSample(new Point2D(10, 0), 1, 1.0, -170, -30),
        };

        DynamicsSpec dynamics = new(new[]
        {
            DynamicsTargetSpec.Off,
            DynamicsTargetSpec.Off,
            DynamicsTargetSpec.Off,
            DynamicsTargetSpec.Preset(DynamicsPreset.Linear),
            DynamicsTargetSpec.Off,
        });

        // The mean direction is straight back, which a linear curve puts at the far end of 0..180; the two samples
        // on their own sit at opposite ends of the range, so neither of them is that answer.
        Assert.Equal(180.0, StrokeDynamics.CalligraphicAngle(rolled, dynamics), 6);
        Assert.Equal(175.0, StrokeDynamics.CalligraphicAngle(rolled[0], dynamics), 0);
        Assert.Equal(5.0, StrokeDynamics.CalligraphicAngle(rolled[1], dynamics), 0);
    }
}
