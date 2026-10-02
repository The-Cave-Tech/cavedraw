using System.Text.Json;
using VCCad.App.Automation;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// **What a recorded tablet response does to the stroke that gets drawn** (issue #107).
///
/// The curves, the derived speed and the width profile they produce are already tested as pure functions. What was
/// missing is the caller: a stroke drawn with varying pressure produced a constant-width line, because the drawing
/// path took positions and nothing else. These tests are therefore on the **geometry that is drawn** - the outline
/// <see cref="StrokeOutlineBuilder"/> resolves - and never on the spec, because a spec that round-trips perfectly is
/// exactly the state this feature was already in while nothing honoured it.
///
/// Every assertion here is paired with a **control at the other extreme**, so "the pressure is honoured" is told
/// apart from "something changed": a rising ramp and a falling one have to come out with opposite ends fat, and a
/// stroke whose samples carry the same pressures but no recorded response has to be the width it was drawn at.
/// </summary>
public class DynamicsDrawnTests
{
    private static JsonElement Params(object value) => JsonSerializer.SerializeToElement(value);

    private static AutomationContext Host(double width = 20.0)
    {
        var vm = new EditorViewModel();
        vm.CurrentStroke = StrokeSpec.Hairline(ColorRgb.Black) with { Width = width };
        return new AutomationContext { ViewModel = vm };
    }

    /// <summary>
    /// Draws a straight horizontal stroke from samples, through the operation registry, and returns the path it
    /// made. The samples are the same shape a pen reports: a position and a time, with pressure on top.
    /// </summary>
    private static PathItem Draw(
        AutomationContext context,
        IReadOnlyList<(double X, double Pressure, double TiltX, double TiltY)> samples,
        object? dynamics = null,
        double? width = null)
    {
        var described = samples
            .Select((s, i) => new
            {
                x = s.X,
                y = 0.0,
                time = (double)i,
                pressure = s.Pressure,
                tiltX = s.TiltX,
                tiltY = s.TiltY,
            })
            .ToArray();

        var parameters = new Dictionary<string, object?> { ["samples"] = described };
        if (dynamics is not null)
        {
            parameters["dynamics"] = dynamics;
        }

        if (width is { } stated)
        {
            parameters["width"] = stated;
        }

        JsonElement result = JsonSerializer.SerializeToElement(
            EditorOperations.Invoke(context, "path.drawFreehand", Params(parameters)));

        Assert.True(result.GetProperty("drawn").GetBoolean(), "the stroke should have been drawn");
        Guid id = result.GetProperty("itemId").GetGuid();
        return Assert.IsType<PathItem>(context.Document.FindItem(id));
    }

    /// <summary>The signed area the stroke actually inks, from the outline the renderers fill.</summary>
    private static double OutlineArea(PathItem path, StrokeSpec stroke)
    {
        double total = 0.0;
        foreach (IReadOnlyList<Point2D> loop in StrokeOutlineBuilder.Outline(path, stroke))
        {
            double twice = 0.0;
            for (int i = 0; i < loop.Count; i++)
            {
                Point2D a = loop[i];
                Point2D b = loop[(i + 1) % loop.Count];
                twice += (a.X * b.Y) - (b.X * a.Y);
            }

            total += Math.Abs(twice) / 2.0;
        }

        return total;
    }

    /// <summary>
    /// The width of the drawn band near one end of the stroke, measured **across** it: the vertical extent of the
    /// outline's points within <paramref name="within"/> of the end.
    ///
    /// A stroke's ends are where a width profile is easiest to read and hardest to fake: a ramp up and a ramp down
    /// put the fat end at opposite ends, and only a profile that follows the samples can do that.
    /// </summary>
    private static double WidthNearEnd(PathItem path, StrokeSpec stroke, bool atStart, double within = 8.0)
    {
        IReadOnlyList<IReadOnlyList<Point2D>> loops = StrokeOutlineBuilder.Outline(path, stroke);
        var ys = new List<double>();
        foreach (IReadOnlyList<Point2D> loop in loops)
        {
            foreach (Point2D point in loop)
            {
                bool here = atStart ? point.X <= within : point.X >= 100.0 - within;
                if (here)
                {
                    ys.Add(point.Y);
                }
            }
        }

        Assert.True(ys.Count > 0, $"the outline has no points {(atStart ? "at the start" : "at the end")}");
        return ys.Max() - ys.Min();
    }

    private static readonly (double X, double Pressure, double TiltX, double TiltY)[] Rising =
    {
        (0.0, 0.1, 0.0, 0.0),
        (100.0, 1.0, 0.0, 0.0),
    };

    private static readonly (double X, double Pressure, double TiltX, double TiltY)[] Falling =
    {
        (0.0, 1.0, 0.0, 0.0),
        (100.0, 0.1, 0.0, 0.0),
    };

    private static readonly (double X, double Pressure, double TiltX, double TiltY)[] Light =
    {
        (0.0, 0.1, 0.0, 0.0),
        (100.0, 0.1, 0.0, 0.0),
    };

    private static readonly (double X, double Pressure, double TiltX, double TiltY)[] Heavy =
    {
        (0.0, 1.0, 0.0, 0.0),
        (100.0, 1.0, 0.0, 0.0),
    };

    /// <summary>
    /// **Pressure drawn into a stroke widens it, in the direction the pressure went.** This is the issue's headline
    /// test, asserted on the outline the canvas and the PDF export both fill.
    /// </summary>
    [Fact]
    public void PressureChangesTheWidthOfTheStrokeThatIsDrawn()
    {
        AutomationContext context = Host();
        PathItem rising = Draw(context, Rising, new { target = "width", preset = "linear" });

        double atStart = WidthNearEnd(rising, rising.Stroke, atStart: true);
        double atEnd = WidthNearEnd(rising, rising.Stroke, atStart: false);

        Assert.True(atEnd > atStart * 2.0,
            $"a stroke pressed harder towards the end should be wider there: {atStart} became {atEnd}");
    }

    /// <summary>
    /// The control the first test needs: the **same samples with the ramp reversed** end up fat at the other end.
    /// Without this, "the width changed" is all the first test proves, and a constant width with a bulge anywhere
    /// would satisfy it.
    /// </summary>
    [Fact]
    public void TheSameSamplesWithTheRampReversedAreFatAtTheOtherEnd()
    {
        AutomationContext context = Host();
        PathItem falling = Draw(context, Falling, new { target = "width", preset = "linear" });

        double atStart = WidthNearEnd(falling, falling.Stroke, atStart: true);
        double atEnd = WidthNearEnd(falling, falling.Stroke, atStart: false);

        Assert.True(atStart > atEnd * 2.0,
            $"a stroke pressed harder at the start should be wider there: {atStart} then {atEnd}");
    }

    /// <summary>
    /// **The width follows the curve, not merely the presence of samples.** A linear curve over 0.1..1.0 of a
    /// twenty-point stroke inks the mean of the two ends' bands - so the drawn area is the midpoint of the two
    /// constant-pressure controls, which no "it changed" behaviour can land on.
    /// </summary>
    [Fact]
    public void TheDrawnAreaIsWhatTheCurveAsksFor()
    {
        AutomationContext context = Host();
        PathItem rising = Draw(context, Rising, new { target = "width", preset = "linear" });
        PathItem light = Draw(context, Light, new { target = "width", preset = "linear" });
        PathItem heavy = Draw(context, Heavy, new { target = "width", preset = "linear" });

        double ramped = OutlineArea(rising, rising.Stroke);
        double thin = OutlineArea(light, light.Stroke);
        double thick = OutlineArea(heavy, heavy.Stroke);

        // 100 long, 20 wide: the fully pressed band is 2000 and the 10%-pressure one is 200, so a linear ramp from
        // one to the other is 1100.
        Assert.Equal(2000.0, thick, 0);
        Assert.Equal(200.0, thin, 0);
        Assert.Equal(1100.0, ramped, 0);
    }

    /// <summary>
    /// **With no response recorded, pressure is ignored rather than passed through.** The same varying samples that
    /// produced a taper above produce a constant-width line here - which is the difference between "the pen is
    /// honoured" and "the pen is obeyed whether anybody asked or not".
    /// </summary>
    [Fact]
    public void WithoutARecordedResponseTheDrawnStrokeKeepsItsWidth()
    {
        AutomationContext context = Host();
        PathItem plain = Draw(context, Rising);

        Assert.False(plain.Stroke.HasWidthProfile);
        Assert.Equal(2000.0, OutlineArea(plain, plain.Stroke), 0);
    }

    /// <summary>
    /// **Tilt turns the nib of the stroke that is drawn.** The calligraphic nib's angle is document state, so tilt
    /// reaching the drawing means the angle on the drawn stroke changes - and the inked area changes with it, which
    /// is what makes this a fact about geometry rather than about a number.
    /// </summary>
    [Fact]
    public void TiltTurnsTheNibOfTheStrokeThatIsDrawn()
    {
        var untiltedSamples = new (double, double, double, double)[] { (0.0, 1.0, 0.0, 0.0), (100.0, 1.0, 0.0, 0.0) };
        var tiltedSamples = new (double, double, double, double)[] { (0.0, 1.0, 45.0, 0.0), (100.0, 1.0, 45.0, 0.0) };

        AutomationContext untilted = Host();
        untilted.ViewModel.CurrentStroke = untilted.ViewModel.CurrentStroke with
        {
            Width = 20,
            Brush = BrushSpec.Calligraphic("Chisel", 0, 0.25, 20),
        };
        PathItem straight = Draw(untilted, untiltedSamples, new { target = "angle", preset = "linear" });

        AutomationContext tilted = Host();
        tilted.ViewModel.CurrentStroke = tilted.ViewModel.CurrentStroke with
        {
            Width = 20,
            Brush = BrushSpec.Calligraphic("Chisel", 0, 0.25, 20),
        };
        PathItem laidOver = Draw(tilted, tiltedSamples, new { target = "angle", preset = "linear" });

        Assert.NotNull(straight.Stroke.Brush);
        Assert.NotNull(laidOver.Stroke.Brush);

        // An upright pen leaves the nib where it was authored; a pen laid to the right turns it to the middle of the
        // range, which this mapping states as 90 degrees.
        Assert.Equal(0.0, straight.Stroke.Brush!.AngleDegrees, 6);
        Assert.Equal(90.0, laidOver.Stroke.Brush!.AngleDegrees, 6);

        // And the drawing differs, because a nib is not symmetric: a quarter-round nib across the direction of
        // travel inks a different band from one along it.
        double straightInk = OutlineArea(straight, straight.Stroke);
        double turnedInk = OutlineArea(laidOver, laidOver.Stroke);
        Assert.True(Math.Abs(straightInk - turnedInk) > 1.0,
            $"turning the nib should change what is inked: {straightInk} vs {turnedInk}");
    }

    /// <summary>
    /// **The record and the geometry are two halves, and both land.** The stroke keeps the response it was drawn
    /// under - which is what the pane shows and `style.strokes` reports - beside the width profile the pressure
    /// produced, which is what draws. Clearing the response removes the note about how the line was made and leaves
    /// the line, which is the same division `style.clearDynamics` makes everywhere else.
    /// </summary>
    [Fact]
    public void TheDrawnStrokeRecordsTheResponseBesideTheGeometry()
    {
        AutomationContext context = Host();
        PathItem drawn = Draw(context, Rising, new { target = "width", preset = "linear" });

        Assert.True(drawn.Stroke.HasDynamics);
        Assert.Equal(
            DynamicsCurve.FromPreset(DynamicsPreset.Linear),
            drawn.Stroke.Dynamics!.For(DynamicsTarget.Width).Curve);

        // The registry is the one place a person and a driver both reach, so what was drawn is reported there.
        JsonElement strokes = JsonSerializer.SerializeToElement(
            EditorOperations.Invoke(context, "style.strokes", Params(new { itemId = drawn.Id })));
        Assert.Contains("\"target\":\"Width\"", strokes.GetRawText(), StringComparison.Ordinal);
        Assert.Contains("\"profile\":", strokes.GetRawText(), StringComparison.Ordinal);

        // Clearing the recorded response takes the note away and leaves the geometry the pen produced.
        context.ViewModel.SelectObject(drawn);
        EditorOperations.Invoke(context, "style.clearDynamics", default);

        Assert.False(drawn.Stroke.HasDynamics);
        Assert.True(drawn.Stroke.HasWidthProfile);
        Assert.Equal(1100.0, OutlineArea(drawn, drawn.Stroke), 0);
    }

    /// <summary>
    /// **A curve with no pen data is refused rather than quietly doing nothing.** `points` says where the pointer
    /// went and nothing about pressure, so a response asked for alongside it cannot be honoured - and silently
    /// ignoring it is the very defect this operation exists to remove.
    /// </summary>
    [Fact]
    public void AResponseWithoutPenSamplesIsRefusedByName()
    {
        AutomationContext context = Host();

        EditorOperationException refused = Assert.Throws<EditorOperationException>(() =>
            EditorOperations.Invoke(context, "path.drawFreehand", Params(new
            {
                points = new[] { new[] { 0.0, 0.0 }, new[] { 100.0, 0.0 } },
                dynamics = new { target = "width", preset = "linear" },
            })));

        Assert.Contains("samples", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// And a stroke drawn from points alone invents no profile, even when the tool's current stroke carries a
    /// response: a pen nobody described is not a pen at full pressure.
    /// </summary>
    [Fact]
    public void PointsAloneRecordNoResponseAndDrawNoProfile()
    {
        AutomationContext context = Host();
        context.ViewModel.CurrentStroke = context.ViewModel.CurrentStroke with
        {
            Width = 20,
            Dynamics = DynamicsSpec.PressureToWidth(DynamicsPreset.Linear),
        };

        JsonElement result = JsonSerializer.SerializeToElement(EditorOperations.Invoke(
            context, "path.drawFreehand",
            Params(new { points = new[] { new[] { 0.0, 0.0 }, new[] { 100.0, 0.0 } }, width = 20.0 })));
        PathItem path = Assert.IsType<PathItem>(context.Document.FindItem(result.GetProperty("itemId").GetGuid()));

        Assert.False(path.Stroke.HasWidthProfile);
        Assert.Equal(2000.0, OutlineArea(path, path.Stroke), 0);
    }

    /// <summary>
    /// A stroke drawn **without** a recorded response keeps the nib the brush states, so tilt is not secretly obeyed.
    /// </summary>
    [Fact]
    public void WithoutARecordedResponseTiltLeavesTheNibAlone()
    {
        AutomationContext context = Host();
        context.ViewModel.CurrentStroke = context.ViewModel.CurrentStroke with
        {
            Width = 20,
            Brush = BrushSpec.Calligraphic("Chisel", 30, 0.25, 20),
        };

        var tilted = new (double, double, double, double)[] { (0.0, 1.0, 45.0, 0.0), (100.0, 1.0, 45.0, 0.0) };
        PathItem drawn = Draw(context, tilted);

        Assert.Equal(30.0, drawn.Stroke.Brush!.AngleDegrees, 6);
    }
}
