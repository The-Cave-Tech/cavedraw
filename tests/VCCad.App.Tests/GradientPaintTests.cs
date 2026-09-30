using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using SkiaSharp;
using VCCad.App.Controls;
using VCCad.Core.Model;
using Xunit;
using GradientStop = VCCad.Core.Model.GradientStop;

namespace VCCad.App.Tests;

/// <summary>
/// The gradient shader, measured in pixels.
///
/// A colour question is best answered by reading the pixels back, so every test here drives Skia
/// itself - an <c>SKSurface</c>, no window, no control - and asserts what came out. That also
/// means these tests exercise the same stop maths the canvas paints with, because both are built
/// from <see cref="GradientPaint.BuildStops"/>.
/// </summary>
public class GradientPaintTests
{
    private const int Width = 100;
    private const int Height = 4;
    private const int Tolerance = 6;

    private static readonly Rect Box = new(0, 0, Width, Height);
    private static readonly SKColor OpaqueBlue = new(0, 0, 255, 255);

    private static GradientSpec Linear(params GradientStop[] stops) => new()
    {
        Kind = GradientKind.Linear,
        Start = new VCCad.Geometry.Point2D(0.0, 0.5),
        End = new VCCad.Geometry.Point2D(1.0, 0.5),
        Stops = stops,
    };

    private static SKBitmap Render(GradientSpec spec, SKColor backdrop, int width = Width, int height = Height)
        => Render(spec, new Rect(0, 0, width, height), backdrop, width, height);

    private static SKBitmap Render(GradientSpec spec, Rect box, SKColor backdrop, int width, int height)
    {
        SKShader? shader = GradientPaint.CreateShader(spec, box, 1.0);
        Assert.NotNull(shader);

        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using SKSurface surface = SKSurface.Create(info);
        surface.Canvas.Clear(backdrop);
        using (var paint = new SKPaint { Shader = shader, IsAntialias = false })
        {
            surface.Canvas.DrawRect(SKRect.Create(0, 0, width, height), paint);
        }

        using SKImage image = surface.Snapshot();
        return SKBitmap.FromImage(image);
    }

    private static void AssertNear(SKBitmap bitmap, int x, byte r, byte g, byte b, string because)
    {
        SKColor actual = bitmap.GetPixel(x, Height / 2);
        Assert.True(
            Math.Abs(actual.Red - r) <= Tolerance &&
            Math.Abs(actual.Green - g) <= Tolerance &&
            Math.Abs(actual.Blue - b) <= Tolerance,
            $"{because}: expected ~({r},{g},{b}) at x={x}, got ({actual.Red},{actual.Green},{actual.Blue})");
    }

    private static void AssertNear(SKBitmap bitmap, int x, int y, byte r, byte g, byte b, string because)
    {
        SKColor actual = bitmap.GetPixel(x, y);
        Assert.True(
            Math.Abs(actual.Red - r) <= Tolerance &&
            Math.Abs(actual.Green - g) <= Tolerance &&
            Math.Abs(actual.Blue - b) <= Tolerance,
            $"{because}: expected ~({r},{g},{b}) at ({x},{y}), got ({actual.Red},{actual.Green},{actual.Blue})");
    }

    [Fact]
    public void AThreeStopRampPaintsEachStopAtItsPosition()
    {
        GradientSpec spec = Linear(
            new GradientStop(0.0, ColorRgb.Red),
            new GradientStop(0.5, ColorRgb.Green),
            new GradientStop(1.0, ColorRgb.Blue));

        using SKBitmap bitmap = Render(spec, OpaqueBlue);

        AssertNear(bitmap, 0, 255, 0, 0, "first stop");
        AssertNear(bitmap, 50, 0, 255, 0, "middle stop");
        AssertNear(bitmap, 99, 0, 0, 255, "last stop");

        // Halfway between two stops is an even RGB blend, not a lighter one: the model blends
        // component-wise rather than in linear light, and so must the shader.
        AssertNear(bitmap, 25, 128, 128, 0, "midpoint of the first segment");
    }

    [Fact]
    public void SpreadDecidesWhatHappensPastBothEndsOfTheRamp()
    {
        GradientStop[] stops =
        {
            new(0.0, ColorRgb.Black),
            new(1.0, ColorRgb.White),
        };

        // The ramp occupies the middle half of the object, so x=5 and x=95 are both outside it.
        var box = new Rect(0, 0, Width, Height);

        GradientSpec Pad() => Linear(stops) with
        {
            Spread = GradientSpread.Pad,
            Start = new VCCad.Geometry.Point2D(0.25, 0.5),
            End = new VCCad.Geometry.Point2D(0.75, 0.5),
        };

        using (SKBitmap bitmap = Render(Pad() with { Spread = GradientSpread.Pad }, box, OpaqueBlue, Width, Height))
        {
            AssertNear(bitmap, 5, 0, 0, 0, "Pad holds the first stop before the ramp");
            AssertNear(bitmap, 95, 255, 255, 255, "Pad holds the last stop after the ramp");
        }

        // Before the ramp at t=-0.4: Reflect mirrors it back to 0.4 (dark), Repeat wraps it to
        // 0.6 (light). Both are then mirrored the other way after the ramp.
        using (SKBitmap bitmap = Render(Pad() with { Spread = GradientSpread.Reflect }, box, OpaqueBlue, Width, Height))
        {
            AssertNear(bitmap, 5, 102, 102, 102, "Reflect mirrors the ramp back on itself");
            AssertNear(bitmap, 95, 153, 153, 153, "Reflect mirrors the other end");
        }

        using (SKBitmap bitmap = Render(Pad() with { Spread = GradientSpread.Repeat }, box, OpaqueBlue, Width, Height))
        {
            AssertNear(bitmap, 5, 153, 153, 153, "Repeat wraps around");
            AssertNear(bitmap, 95, 102, 102, 102, "Repeat wraps the other end");
        }
    }

    [Fact]
    public void AStopAtHalfOpacityShowsTheBackdropThroughIt()
    {
        GradientSpec spec = Linear(
            new GradientStop(0.0, ColorRgb.Red, Opacity: 0.5),
            new GradientStop(1.0, ColorRgb.Red, Opacity: 0.5));

        using SKBitmap bitmap = Render(spec, OpaqueBlue);

        // Half of red over blue, all the way across.
        AssertNear(bitmap, 50, 128, 0, 128, "a 50% stop composites with the backdrop");
    }

    [Fact]
    public void StopOpacityAndTheColoursOwnAlphaBothApply()
    {
        GradientSpec ViaOpacity = Linear(
            new GradientStop(0.0, ColorRgb.Red, Opacity: 0.5),
            new GradientStop(1.0, ColorRgb.Red, Opacity: 0.5));

        // The same half-transparent red, this time carried in the colour's own alpha channel.
        GradientSpec ViaColourAlpha = Linear(
            new GradientStop(0.0, ColorRgb.Red.WithAlpha(0.5)),
            new GradientStop(1.0, ColorRgb.Red.WithAlpha(0.5)));

        using SKBitmap a = Render(ViaOpacity, OpaqueBlue);
        using SKBitmap b = Render(ViaColourAlpha, OpaqueBlue);

        AssertNear(a, 50, 128, 0, 128, "opacity channel");
        AssertNear(b, 50, 128, 0, 128, "colour alpha channel");

        // Both together are multiplicative, not one or the other.
        GradientSpec Both = Linear(
            new GradientStop(0.0, ColorRgb.Red.WithAlpha(0.5), Opacity: 0.5),
            new GradientStop(1.0, ColorRgb.Red.WithAlpha(0.5), Opacity: 0.5));
        using SKBitmap c = Render(Both, OpaqueBlue);
        AssertNear(c, 50, 64, 0, 191, "alpha and opacity multiply");
    }

    [Fact]
    public void AnEllipticalRadialIsSquashedByTheRadiusRatio()
    {
        var spec = new GradientSpec
        {
            Kind = GradientKind.Radial,
            Center = new VCCad.Geometry.Point2D(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.25,
            Stops = new[]
            {
                new GradientStop(0.0, ColorRgb.Black),
                new GradientStop(1.0, ColorRgb.White),
            },
        };

        using SKBitmap bitmap = Render(spec, OpaqueBlue, 100, 100);

        // 25px from the centre is halfway out horizontally (radius 50) but at the edge
        // vertically (radius 25).
        AssertNear(bitmap, 75, 50, 128, 128, 128, "halfway out along X");
        AssertNear(bitmap, 50, 75, 255, 255, 255, "at the edge along Y");
    }

    [Fact]
    public void ABiasedMidpointMovesTheHalfwayColour()
    {
        // Illustrator's diamond: a midpoint of 0.9 pushes the blend late, so halfway across the
        // segment is still nearly the first stop. An even blend would be ~128.
        GradientSpec spec = Linear(
            new GradientStop(0.0, ColorRgb.Black, Midpoint: 0.9),
            new GradientStop(1.0, ColorRgb.White));

        using SKBitmap bitmap = Render(spec, OpaqueBlue);

        SKColor midpoint = bitmap.GetPixel(50, Height / 2);
        Assert.True(midpoint.Red < 40, $"a biased midpoint should stay dark halfway across, got {midpoint.Red}");
    }

    [Fact]
    public void ARotatedEllipticalRadialSquashesAlongTheRotatedAxis()
    {
        var spec = new GradientSpec
        {
            Kind = GradientKind.Radial,
            Center = new VCCad.Geometry.Point2D(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.25,
            Rotation = 90,
            Stops = new[]
            {
                new GradientStop(0.0, ColorRgb.Black),
                new GradientStop(1.0, ColorRgb.White),
            },
        };

        using SKBitmap bitmap = Render(spec, OpaqueBlue, 100, 100);

        // Rotated a quarter turn, the short axis is now X.
        AssertNear(bitmap, 75, 50, 255, 255, 255, "at the edge along the rotated short axis");
        AssertNear(bitmap, 50, 75, 128, 128, 128, "halfway out along the rotated long axis");
    }

    [Fact]
    public void AFreeformGradientHasNoShaderSoTheFillColourIsUsedInstead()
    {
        var spec = new GradientSpec
        {
            Kind = GradientKind.Freeform,
            Points = new[]
            {
                new FreeformPoint(new VCCad.Geometry.Point2D(1, 1), ColorRgb.Red),
                new FreeformPoint(new VCCad.Geometry.Point2D(2, 2), ColorRgb.Blue),
            },
        };

        // Null is the contract: the caller falls back to FillSpec.Color, which the model
        // guarantees is always meaningful even on a gradient.
        Assert.Null(GradientPaint.CreateShader(spec, Box, 1.0));
        Assert.Null(GradientPaint.CreateBrush(spec, Box, 1.0));
    }

    /// <summary>
    /// The same stops through Avalonia's own renderer, which is what the canvas actually paints
    /// with. Skia is driven headless into a bitmap, so this is a real composite with no window.
    /// </summary>
    [AvaloniaFact]
    public void TheBrushTheCanvasPaintsWithRampsTheSameWay()
    {
        GradientSpec spec = Linear(
            new GradientStop(0.0, ColorRgb.Red),
            new GradientStop(0.5, ColorRgb.Green),
            new GradientStop(1.0, ColorRgb.Blue));

        IBrush? brush = GradientPaint.CreateBrush(spec, Box, 1.0);
        Assert.NotNull(brush);

        using var target = new RenderTargetBitmap(new PixelSize(Width, Height), new Vector(96, 96));
        using (DrawingContext context = target.CreateDrawingContext())
        {
            context.FillRectangle(brush!, new Rect(0, 0, Width, Height));
        }

        byte[] pixels = CopyPixels(target);
        AssertNear(pixels, 0, 255, 0, 0, "the brush paints the first stop");
        AssertNear(pixels, 50, 0, 255, 0, "the brush paints the middle stop");
        AssertNear(pixels, 99, 0, 0, 255, "the brush paints the last stop");
    }

    private static byte[] CopyPixels(RenderTargetBitmap bitmap)
    {
        int stride = bitmap.PixelSize.Width * 4;
        var buffer = new byte[stride * bitmap.PixelSize.Height];
        GCHandle handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(
                new PixelRect(0, 0, bitmap.PixelSize.Width, bitmap.PixelSize.Height),
                handle.AddrOfPinnedObject(),
                buffer.Length,
                stride);
        }
        finally
        {
            handle.Free();
        }

        return buffer;
    }

    /// <summary>
    /// Conical is a sweep, and it is the one kind a brush expresses exactly: a conic brush with the
    /// ramp's own stops, so the canvas paints it without falling back to the flat colour.
    /// </summary>
    [Fact]
    public void AConicalGradientBuildsASweepBrushAndShader()
    {
        var spec = new GradientSpec
        {
            Kind = GradientKind.Conical,
            Angle = 45,
            Stops = new[]
            {
                new GradientStop(0.0, ColorRgb.Red),
                new GradientStop(1.0, ColorRgb.Blue),
            },
        };

        Assert.IsType<ConicGradientBrush>(GradientPaint.CreateBrush(spec, Box, 1.0));
        Assert.NotNull(GradientPaint.CreateShader(spec, Box, 1.0));
    }

    private static void AssertNear(byte[] bgra, int x, byte r, byte g, byte b, string because)
    {
        int offset = (x * 4) + (1 * (Width * 4));
        byte blue = bgra[offset];
        byte green = bgra[offset + 1];
        byte red = bgra[offset + 2];
        Assert.True(
            Math.Abs(red - r) <= Tolerance && Math.Abs(green - g) <= Tolerance && Math.Abs(blue - b) <= Tolerance,
            $"{because}: expected ~({r},{g},{b}) at x={x}, got ({red},{green},{blue})");
    }
}
