using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// The gradient model's own arithmetic, at the level where a wrong answer is silent.
///
/// A gradient that is wrong is rarely wrong loudly: it renders, it exports, it has stops, and the
/// midpoint is simply the wrong colour. These assert the actual channel values rather than "a
/// gradient exists", which is the only way that class of defect is caught.
///
/// The first test exists because the bug it pins shipped: Blend rounded to a byte 0..255 and
/// handed it to ColorRgb, whose channels are 0..1, so every blend collapsed to 0 or 1 and Sample
/// returned almost black. Nothing failed - the renderer reads the stops directly instead of
/// trusting Sample, so only the fallback colour and the export path were wrong.
/// </summary>
public class GradientSpecTests
{
    private static ColorRgb Rgb(double r, double g, double b) => new(r, g, b);

    [Fact]
    public void BlendStaysInTheChannelsOwnRange()
    {
        // Channels are 0..1 in this model. A blend must not leave that range, and the midpoint of
        // white to black must be 0.5 - not 0, not 1, not 127.
        ColorRgb mid = GradientSpec.Blend(Rgb(1, 1, 1), Rgb(0, 0, 0), 0.5);

        Assert.Equal(0.5, mid.R, 6);
        Assert.Equal(0.5, mid.G, 6);
        Assert.Equal(0.5, mid.B, 6);

        foreach (double t in new[] { 0.0, 0.25, 0.5, 0.75, 1.0 })
        {
            ColorRgb c = GradientSpec.Blend(Rgb(0.2, 0.4, 0.6), Rgb(0.8, 0.6, 0.4), t);
            Assert.InRange(c.R, 0.0, 1.0);
            Assert.InRange(c.G, 0.0, 1.0);
            Assert.InRange(c.B, 0.0, 1.0);
        }
    }

    [Fact]
    public void SampleIsTheStopColourAtTheStopsAndABlendBetweenThem()
    {
        var g = new GradientSpec
        {
            Stops = new[]
            {
                new GradientStop(0.0, Rgb(1, 0, 0)),
                new GradientStop(1.0, Rgb(0, 0, 1)),
            },
        };

        Assert.Equal(1.0, g.Sample(0.0).Color.R, 6);
        Assert.Equal(0.0, g.Sample(1.0).Color.R, 6);
        Assert.Equal(1.0, g.Sample(1.0).Color.B, 6);

        // Halfway is halfway: the test that fails when the channels are mixed up.
        (ColorRgb mid, _) = g.Sample(0.5);
        Assert.Equal(0.5, mid.R, 6);
        Assert.Equal(0.5, mid.B, 6);
    }

    [Fact]
    public void MultiStopRampsArePiecewiseBetweenAdjacentStops()
    {
        var g = new GradientSpec
        {
            Stops = new[]
            {
                new GradientStop(0.0, Rgb(0, 0, 0)),
                new GradientStop(0.5, Rgb(1, 1, 1)),
                new GradientStop(1.0, Rgb(0, 0, 0)),
            },
        };

        Assert.Equal(0.5, g.Sample(0.25).Color.R, 6);   // rising half
        Assert.Equal(1.0, g.Sample(0.50).Color.R, 6);   // the peak
        Assert.Equal(0.5, g.Sample(0.75).Color.R, 6);   // falling half
    }

    [Fact]
    public void OpacityIsInterpolatedSeparatelyFromColour()
    {
        var g = new GradientSpec
        {
            Stops = new[]
            {
                new GradientStop(0.0, Rgb(1, 0, 0), Opacity: 0.0),
                new GradientStop(1.0, Rgb(1, 0, 0), Opacity: 1.0),
            },
        };

        // The colour never changes; only the opacity does. Folding opacity into an alpha channel
        // would make this indistinguishable from a colour ramp.
        (ColorRgb mid, double opacity) = g.Sample(0.5);
        Assert.Equal(1.0, mid.R, 6);
        Assert.Equal(0.5, opacity, 6);
    }

    [Fact]
    public void StopsOutOfOrderAreSortedAndDuplicatesMakeAHardEdge()
    {
        var g = new GradientSpec
        {
            Stops = new[]
            {
                new GradientStop(1.0, Rgb(0, 0, 1)),
                new GradientStop(0.5, Rgb(1, 0, 0)),
                new GradientStop(0.5, Rgb(0, 1, 0)),
                new GradientStop(0.0, Rgb(0, 0, 0)),
            },
        };

        IReadOnlyList<GradientStop> n = g.Normalised();
        Assert.Equal(3, n.Count);
        Assert.Equal(0.0, n[0].Position, 6);
        Assert.Equal(0.5, n[1].Position, 6);

        // Two stops at the same position collapse to the later one, which is what makes a hard
        // edge hard rather than a very fast ramp.
        Assert.Equal(0.0, n[1].Color.R, 6);
        Assert.Equal(1.0, n[1].Color.G, 6);
    }

    [Fact]
    public void ASingleStopIsASolidColour()
    {
        var g = new GradientSpec { Stops = new[] { new GradientStop(0.5, Rgb(0.25, 0.5, 0.75)) } };

        Assert.Equal(0.25, g.Sample(0.0).Color.R, 6);
        Assert.Equal(0.25, g.Sample(1.0).Color.R, 6);
        Assert.Equal(0.25, g.Sample(0.5).Color.R, 6);
    }

    [Theory]
    [InlineData(GradientSpread.Pad, -0.5, 0.0)]      // holds the first stop
    [InlineData(GradientSpread.Pad, 1.5, 1.0)]       // holds the last stop
    [InlineData(GradientSpread.Reflect, -0.25, 0.25)] // mirrors
    [InlineData(GradientSpread.Reflect, 1.25, 0.75)]
    [InlineData(GradientSpread.Repeat, 1.25, 0.25)]   // wraps
    [InlineData(GradientSpread.Repeat, -0.25, 0.75)]
    public void SpreadMapsPositionBeforeSampling(GradientSpread spread, double t, double expected)
    {
        var g = new GradientSpec
        {
            Spread = spread,
            Stops = new[]
            {
                new GradientStop(0.0, Rgb(0, 0, 0)),
                new GradientStop(1.0, Rgb(1, 1, 1)),
            },
        };

        // The ramp is 0..1 in R, so the mapped position is directly readable off the colour.
        Assert.Equal(expected, g.SampleWithSpread(t).Color.R, 6);
    }

    [Fact]
    public void SpreadingNeverLeavesTheChannelRange()
    {
        var g = new GradientSpec
        {
            Spread = GradientSpread.Reflect,
            Stops = new[]
            {
                new GradientStop(0.0, Rgb(0, 0, 0)),
                new GradientStop(1.0, Rgb(1, 1, 1)),
            },
        };

        for (double t = -4.0; t <= 4.0; t += 0.05)
        {
            Assert.InRange(g.SampleWithSpread(t).Color.R, 0.0, 1.0);
        }
    }

    [Fact]
    public void ABiasedMidpointPushesTheBlendTowardTheSecondStop()
    {
        GradientStop first = new(0.0, Rgb(0, 0, 0), Midpoint: 0.8);
        var biased = new GradientSpec
        {
            Stops = new[] { first, new GradientStop(1.0, Rgb(1, 1, 1)) },
        };
        var plain = new GradientSpec
        {
            Stops = new[] { new GradientStop(0.0, Rgb(0, 0, 0)), new GradientStop(1.0, Rgb(1, 1, 1)) },
        };

        // Illustrator's diamond control: pushing the midpoint later keeps the ramp darker longer.
        Assert.True(biased.Sample(0.5).Color.R < plain.Sample(0.5).Color.R);
    }

    [Fact]
    public void WithGradientUsesTheGradientsOwnMidpointAsTheFallbackColour()
    {
        var g = new GradientSpec
        {
            Stops = new[]
            {
                new GradientStop(0.0, Rgb(0, 0, 0)),
                new GradientStop(1.0, Rgb(1, 1, 1)),
            },
        };

        FillSpec fill = FillSpec.WithGradient(g);

        // The fallback must be the midpoint, which is only true if Sample works - so this also
        // guards the bug at the top of this file from the direction a caller would see it.
        Assert.True(fill.HasGradient);
        Assert.Equal(0.5, fill.Color.R, 6);
    }

    [Fact]
    public void FillSpecAFillWithoutAGradientHasNoGradient()
    {
        FillSpec solid = FillSpec.Solid(Rgb(1, 0, 0));

        Assert.False(solid.HasGradient);
        Assert.Null(solid.Gradient);
    }

    [Fact]
    public void TheDefaultGradientIsATwoStopBlackToWhiteRamp()
    {
        GradientSpec d = GradientSpec.Default;

        Assert.Equal(GradientKind.Linear, d.Kind);
        Assert.Equal(GradientSpread.Pad, d.Spread);
        Assert.Equal(2, d.Stops.Count);
        Assert.Equal(0.0, d.Start.X, 6);
        Assert.Equal(0.5, d.Start.Y, 6);
        Assert.Equal(1.0, d.End.X, 6);
    }
}
