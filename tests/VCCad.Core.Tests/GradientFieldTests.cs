using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// The gradients that have to be sampled instead of written as a PDF shading.
///
/// A conical sweep has no PDF shading type at all, and free-form shadings are meshes; both are
/// sampled from one definition so the canvas and the exporter cannot paint different fields. What
/// these pin is the geometry of that sampling - where a conical ramp starts and which way it runs,
/// and that a freeform sample is the point field itself.
/// </summary>
public class GradientFieldTests
{
    private static readonly Rect2D Box = new(0, 0, 100, 100);

    private static GradientSpec Conical(double angle = 0) => new()
    {
        Kind = GradientKind.Conical,
        Angle = angle,
        Center = new Point2D(0.5, 0.5),
        Stops = new[]
        {
            new GradientStop(0.0, new ColorRgb(1, 0, 0)),
            new GradientStop(1.0, new ColorRgb(0, 0, 1)),
        },
    };

    [Fact]
    public void ASweepStartsAtItsAngleAndRunsClockwise()
    {
        GradientSpec spec = Conical();

        // u=1, v=0.5 is the centre line to the right: angle 0, so the ramp's start colour.
        (ColorRgb right, _) = GradientField.Sample(spec, Box, 0.99, 0.5)!.Value;

        // And straight down from the centre is a quarter turn later, so a quarter of the way round.
        (ColorRgb down, _) = GradientField.Sample(spec, Box, 0.5, 0.99)!.Value;

        Assert.Equal(1.0, right.R, 2);
        Assert.True(right.B < 0.05, $"the sweep should start red, was B={right.B}");

        Assert.InRange(down.R, 0.65, 0.85);
        Assert.InRange(down.B, 0.15, 0.35);
    }

    [Fact]
    public void TheSweepsAngleMovesWhereItStarts()
    {
        // Starting a quarter turn later puts the ramp's start colour where the sweep used to be a
        // quarter of the way round.
        GradientSpec turned = Conical(90);

        (ColorRgb down, _) = GradientField.Sample(turned, Box, 0.5, 0.99)!.Value;

        Assert.True(down.R > 0.95, $"a 90-degree sweep should start red pointing down, was R={down.R}");
        Assert.True(down.B < 0.05, $"was B={down.B}");
    }

    [Fact]
    public void TheSweepTurnsOnceAndDoesNotRunOffTheRamp()
    {
        GradientSpec spec = Conical();

        foreach (double degrees in new[] { 0, 45, 90, 135, 180, 225, 270, 315, 360, 400 })
        {
            double radians = degrees * Math.PI / 180.0;
            double u = 0.5 + (0.49 * Math.Cos(radians));
            double v = 0.5 + (0.49 * Math.Sin(radians));

            (ColorRgb colour, _) = GradientField.Sample(spec, Box, u, v)!.Value;

            Assert.InRange(colour.R, 0.0, 1.0);
            Assert.InRange(colour.B, 0.0, 1.0);
        }
    }

    [Fact]
    public void AFreeformGradientIsSampledAsItsOwnPointField()
    {
        var spec = new GradientSpec
        {
            Kind = GradientKind.Freeform,
            FreeformMode = FreeformMode.Points,
            Points = new[]
            {
                new FreeformPoint(new Point2D(10, 10), new ColorRgb(1, 0, 0)),
                new FreeformPoint(new Point2D(90, 90), new ColorRgb(0, 0, 1)),
            },
        };

        (ColorRgb nearStart, _) = GradientField.Sample(spec, Box, 0.1, 0.1)!.Value;
        (ColorRgb nearEnd, _) = GradientField.Sample(spec, Box, 0.9, 0.9)!.Value;

        Assert.True(nearStart.R > nearStart.B, "the point's own colour should be where the point is");
        Assert.True(nearEnd.B > nearEnd.R, "and the other one where the other point is");
    }

    [Fact]
    public void TheRampKindsAreNotSampledHere()
    {
        Assert.False(GradientField.NeedsSampling(new GradientSpec { Kind = GradientKind.Linear }));
        Assert.Null(GradientField.Sample(new GradientSpec { Kind = GradientKind.Linear }, Box, 0.5, 0.5));
        Assert.Null(GradientField.Rgb(new GradientSpec { Kind = GradientKind.Radial }, Box));
    }

    [Fact]
    public void ASampledGradientBecomesPixelsWithTheRightShape()
    {
        (byte[] rgb, int width, int height) = GradientField.Rgb(Conical(), new Rect2D(0, 0, 200, 100), grid: 64)!.Value;

        // The grid follows the longer side, so a 2:1 box is 64 by 32.
        Assert.Equal(64, width);
        Assert.Equal(32, height);
        Assert.Equal(width * height * 3, rgb.Length);

        // The sweep's start colour is red: the middle of the right-hand edge is red-dominant.
        int rightMiddle = (((height / 2) * width) + (width - 1)) * 3;
        Assert.True(rgb[rightMiddle] > 200, $"the right edge should be red, was {rgb[rightMiddle]}");
    }

    [Fact]
    public void AFreeformGradientWithNothingInItProducesNoPixels()
    {
        var empty = new GradientSpec { Kind = GradientKind.Freeform, Points = Array.Empty<FreeformPoint>() };

        Assert.Null(GradientField.Rgb(empty, Box));
    }

    /// <summary>
    /// Which edge the first row belongs to.
    ///
    /// The canvas draws a bitmap with its first row at the origin; a PDF image draws its first row
    /// along the <c>+v</c> edge. Sampling the wrong way up does not fail - it renders the gradient
    /// upside down, which a second renderer catches and a passing test does not unless it asks.
    /// </summary>
    [Fact]
    public void TheFirstRowCanBeSampledFromEitherEdge()
    {
        var spec = new GradientSpec
        {
            Kind = GradientKind.Freeform,
            FreeformMode = FreeformMode.Points,
            Points = new[]
            {
                new FreeformPoint(new Point2D(10, 10), new ColorRgb(1, 0, 0)),
                new FreeformPoint(new Point2D(90, 90), new ColorRgb(0, 0, 1)),
            },
        };

        var origin = new Point2D(0, 0);
        var u = new Vector2D(100, 0);
        var v = new Vector2D(0, 100);

        (byte[] atOrigin, int width, int height) = GradientField.Rgb(spec, origin, u, v, grid: 16)!.Value;
        (byte[] atFarEdge, int farWidth, int farHeight) = GradientField.Rgb(
            spec, origin, u, v, grid: 16, firstRowAtOrigin: false)!.Value;

        Assert.Equal(width, farWidth);
        Assert.Equal(height, farHeight);

        // Asking for the other edge is exactly reversing the rows: same field, same size, turned over.
        for (int y = 0; y < height; y++)
        {
            int from = y * width * 3;
            int to = (height - 1 - y) * width * 3;

            for (int i = 0; i < width * 3; i++)
            {
                Assert.Equal(atOrigin[from + i], atFarEdge[to + i]);
            }
        }
    }
}
