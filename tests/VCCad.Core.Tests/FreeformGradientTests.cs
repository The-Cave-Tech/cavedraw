using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// The freeform field itself, before anything tries to paint it.
///
/// Freeform has no ramp to sample by position - the colour depends on WHERE the point is, not on how
/// far along a line - so the semantics have to be decided and pinned before a renderer can be judged
/// against them. Points mode is a smooth field; Lines mode is a set of ramps along the drawn lines.
/// </summary>
public class FreeformGradientTests
{
    private static readonly ColorRgb Red = new(1, 0, 0);
    private static readonly ColorRgb Blue = new(0, 0, 1);

    private static GradientSpec Points(params FreeformPoint[] points) => new()
    {
        Kind = GradientKind.Freeform,
        FreeformMode = FreeformMode.Points,
        Points = points,
    };

    [Fact]
    public void APointOnAColourPointTakesThatColourOutright()
    {
        GradientSpec spec = Points(
            new FreeformPoint(new Point2D(0, 0), Red),
            new FreeformPoint(new Point2D(100, 0), Blue));

        (ColorRgb colour, double opacity) = spec.SampleAt(new Point2D(100, 0))!.Value;

        Assert.Equal(1.0, colour.B, 6);
        Assert.Equal(0.0, colour.R, 6);
        Assert.Equal(1.0, opacity, 6);
    }

    [Fact]
    public void MidwayBetweenTwoPointsIsAnEvenBlend()
    {
        GradientSpec spec = Points(
            new FreeformPoint(new Point2D(0, 0), Red),
            new FreeformPoint(new Point2D(100, 0), Blue));

        (ColorRgb colour, _) = spec.SampleAt(new Point2D(50, 0))!.Value;

        Assert.Equal(0.5, colour.R, 6);
        Assert.Equal(0.5, colour.B, 6);
    }

    /// <summary>
    /// The weight is inverse-square, so the nearer colour point dominates rather than the field
    /// being a straight line between them.
    /// </summary>
    [Fact]
    public void TheNearerColourPointDominates()
    {
        GradientSpec spec = Points(
            new FreeformPoint(new Point2D(0, 0), Red),
            new FreeformPoint(new Point2D(100, 0), Blue));

        (ColorRgb near, _) = spec.SampleAt(new Point2D(25, 0))!.Value;
        (ColorRgb far, _) = spec.SampleAt(new Point2D(75, 0))!.Value;

        Assert.True(near.R >= 0.9, $"red should dominate a quarter of the way along (R was {near.R})");
        Assert.True(far.B >= 0.9, $"blue should dominate three quarters along (B was {far.B})");
    }

    [Fact]
    public void OpacityBlendsTheSameWayTheColourDoes()
    {
        GradientSpec spec = Points(
            new FreeformPoint(new Point2D(0, 0), Red, Opacity: 0),
            new FreeformPoint(new Point2D(100, 0), Blue, Opacity: 1));

        (_, double atStart) = spec.SampleAt(new Point2D(0, 0))!.Value;
        (_, double midway) = spec.SampleAt(new Point2D(50, 0))!.Value;
        (_, double atEnd) = spec.SampleAt(new Point2D(100, 0))!.Value;

        Assert.Equal(0, atStart, 6);
        Assert.Equal(1, atEnd, 6);
        Assert.InRange(midway, 0.4, 0.6);
    }

    [Fact]
    public void ASingleColourPointPaintsItsColourEverywhere()
    {
        GradientSpec spec = Points(new FreeformPoint(new Point2D(10, 10), Blue));

        (ColorRgb colour, _) = spec.SampleAt(new Point2D(900, 900))!.Value;

        Assert.Equal(1.0, colour.B, 6);
    }

    [Fact]
    public void NothingToBlendGivesNullSoTheFlatColourStillPaints()
    {
        GradientSpec none = new() { Kind = GradientKind.Freeform, Points = Array.Empty<FreeformPoint>() };

        Assert.Null(none.SampleAt(new Point2D(0, 0)));
    }

    /// <summary>A linear gradient has no point field: SampleAt is a freeform question only.</summary>
    [Fact]
    public void ALinearGradientHasNoPointSample()
    {
        GradientSpec spec = new() { Kind = GradientKind.Linear };

        Assert.Null(spec.SampleAt(new Point2D(0, 0)));
    }

    private static GradientSpec Lines(params (FreeformPoint Point, int Index)[] points)
    {
        var spec = new GradientSpec
        {
            Kind = GradientKind.Freeform,
            FreeformMode = FreeformMode.Lines,
            Points = points.Select(p => p.Point).ToList(),
        };

        return spec with { Lines = new[] { (0, 1) } };
    }

    [Fact]
    public void OnALineTheRampRunsBetweenItsTwoEndColours()
    {
        GradientSpec spec = Lines(
            (new FreeformPoint(new Point2D(0, 0), Red), 0),
            (new FreeformPoint(new Point2D(100, 0), Blue), 1));

        (ColorRgb middle, _) = spec.SampleAt(new Point2D(50, 0))!.Value;

        Assert.Equal(0.5, middle.R, 6);
        Assert.Equal(0.5, middle.B, 6);
    }

    [Fact]
    public void PastALineEndTheRampClampsToThatEnd()
    {
        GradientSpec spec = Lines(
            (new FreeformPoint(new Point2D(0, 0), Red), 0),
            (new FreeformPoint(new Point2D(100, 0), Blue), 1));

        (ColorRgb beyond, _) = spec.SampleAt(new Point2D(400, 0))!.Value;

        Assert.Equal(1.0, beyond.B, 6);
        Assert.Equal(0.0, beyond.R, 6);
    }

    [Fact]
    public void ALineWithNoLengthIsSkippedRatherThanDividingByZero()
    {
        var spec = new GradientSpec
        {
            Kind = GradientKind.Freeform,
            FreeformMode = FreeformMode.Lines,
            Points = new[]
            {
                new FreeformPoint(new Point2D(5, 5), Red),
                new FreeformPoint(new Point2D(5, 5), Blue),
            },
        };

        spec = spec with { Lines = new[] { (0, 1) } };

        Assert.Null(spec.SampleAt(new Point2D(5, 5)));
    }
}
