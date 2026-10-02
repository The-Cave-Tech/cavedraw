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

    /// <summary>
    /// **A radial's focus is where the ramp starts**, and it is a real change to the picture
    /// rather than a number kept on the side. A concentric radial of the same geometry is
    /// rendered beside it: at the focus's own point the focused gradient paints the FIRST stop
    /// and the concentric one paints halfway along, and further out the two disagree as well.
    /// A test that only asked whether a value was stored would pass on a canvas that recentred
    /// every gradient, which is the defect this pins.
    /// </summary>
    [Fact]
    public void ARadialFocusIsWhereTheRampStarts()
    {
        var centred = new GradientSpec
        {
            Kind = GradientKind.Radial,
            Center = new VCCad.Geometry.Point2D(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5,
            Stops = new[]
            {
                new GradientStop(0.0, ColorRgb.Black),
                new GradientStop(1.0, ColorRgb.White),
            },
        };

        GradientSpec focused = centred with { FocalPoint = new VCCad.Geometry.Point2D(0.25, 0.5) };

        using SKBitmap withFocus = Render(focused, OpaqueBlue, 100, 100);
        using SKBitmap without = Render(centred, OpaqueBlue, 100, 100);

        // The focus is (25,50) in a 100x100 box: the ramp's first stop there, and half way from
        // the centre to the edge for the concentric gradient of the same geometry.
        AssertNear(withFocus, 25, 50, 0, 0, 0, "the highlight sits at the focus");
        AssertNear(without, 25, 50, 128, 128, 128, "a concentric radial is half way out there");

        // The focus moved the highlight rather than the shape: the two also differ on the far
        // side of the centre, where a dropped focus would have left the bitmaps identical.
        Assert.NotEqual(withFocus.GetPixel(75, 50), without.GetPixel(75, 50));
    }

    /// <summary>
    /// The geometry the shader is given keeps the focus inside the ellipse: a point the model
    /// (or an operation, or a file) put outside is painted on the EDGE along the centre-to-focus
    /// ray, which is the rule the SVG reader applies. Recentring it would paint a different
    /// picture; keeping it outside would rely on the reader's mercy.
    /// </summary>
    [Fact]
    public void AFocusOutsideTheEllipseIsPaintedOnItsEdgeAlongTheRay()
    {
        var spec = new GradientSpec
        {
            Kind = GradientKind.Radial,
            Center = new VCCad.Geometry.Point2D(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5,
            FocalPoint = new VCCad.Geometry.Point2D(2.0, 2.0),
        };

        GradientGeometry geometry = GradientGeometry.For(spec, new Rect(0, 0, 100, 100));

        Assert.NotNull(geometry.Focus);
        VCCad.Geometry.Point2D focus = new(geometry.Focus!.Value.X, geometry.Focus.Value.Y);

        // The edge is 50 from the centre (50,50) and the ray is the diagonal, so the painted
        // focus is 35.36 along each axis - not the centre, and not (100,100).
        Assert.Equal(50.0 + (50.0 / Math.Sqrt(2.0)), focus.X, 4);
        Assert.Equal(50.0 + (50.0 / Math.Sqrt(2.0)), focus.Y, 4);
    }

    /// <summary>
    /// The brush the canvas paints with carries the focus in its own geometry: Avalonia's radial
    /// brush is a two-point conical whose inner circle has radius zero, so the origin IS the
    /// highlight. Asserted on the brush rather than on the model, because a stored coordinate
    /// that never reaches the brush is exactly the half-wired state.
    /// </summary>
    [AvaloniaFact]
    public void TheRadialBrushPutsItsOriginOnTheFocus()
    {
        var spec = new GradientSpec
        {
            Kind = GradientKind.Radial,
            Center = new VCCad.Geometry.Point2D(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5,
            FocalPoint = new VCCad.Geometry.Point2D(0.25, 0.5),
            Stops = new[]
            {
                new GradientStop(0.0, ColorRgb.Black),
                new GradientStop(1.0, ColorRgb.White),
            },
        };

        var brush = Assert.IsType<RadialGradientBrush>(GradientPaint.CreateBrush(spec, new Rect(0, 0, 200, 100), 1.0));

        Assert.Equal(new Point(100, 50), brush.Center.Point);
        Assert.Equal(new Point(50, 50), brush.GradientOrigin.Point);

        // And a gradient with no focus keeps the concentric brush it always had.
        var centred = Assert.IsType<RadialGradientBrush>(
            GradientPaint.CreateBrush(spec with { FocalPoint = null }, new Rect(0, 0, 200, 100), 1.0));
        Assert.Equal(centred.Center.Point, centred.GradientOrigin.Point);
    }

    /// <summary>
    /// The brush measured in PIXELS, which is what the canvas actually does with it: the highlight
    /// lands on the focus and the concentric brush of the same geometry puts the ramp's midpoint
    /// there instead. The geometry assertion above can be satisfied by a brush the renderer
    /// ignores; this one cannot.
    /// </summary>
    [AvaloniaFact]
    public void TheCanvasBrushPaintsTheHighlightOnTheFocus()
    {
        var centred = new GradientSpec
        {
            Kind = GradientKind.Radial,
            Center = new VCCad.Geometry.Point2D(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5,
            Stops = new[]
            {
                new GradientStop(0.0, ColorRgb.Black),
                new GradientStop(1.0, ColorRgb.White),
            },
        };

        GradientSpec focused = centred with { FocalPoint = new VCCad.Geometry.Point2D(0.25, 0.5) };

        int atFocus = ChannelAt(GradientPaint.CreateBrush(focused, Box100, 1.0)!, 25, 50);
        int concentric = ChannelAt(GradientPaint.CreateBrush(centred, Box100, 1.0)!, 25, 50);

        Assert.True(atFocus < 40, $"the brush should paint the first stop at the focus, got {atFocus}");
        Assert.True(concentric > 100, $"a concentric radial is half way out there, got {concentric}");
    }

    private static readonly Rect Box100 = new(0, 0, 100, 100);

    /// <summary>One colour channel of one pixel of a brush painted over a 100x100 target.</summary>
    private static byte ChannelAt(IBrush brush, int x, int y)
    {
        using var target = new RenderTargetBitmap(new PixelSize(100, 100), new Vector(96, 96));
        using (DrawingContext context = target.CreateDrawingContext())
        {
            context.FillRectangle(brush, new Rect(0, 0, 100, 100));
        }

        var pixel = new byte[4];
        System.Runtime.InteropServices.GCHandle handle = System.Runtime.InteropServices.GCHandle.Alloc(
            pixel, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            target.CopyPixels(new PixelRect(x, y, 1, 1), handle.AddrOfPinnedObject(), 4, 4);
        }
        finally
        {
            handle.Free();
        }

        return pixel[1];
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
    ///
    /// **AvaloniaFact, because the assertion builds Avalonia objects.** `CreateBrush` constructs
    /// `GradientStop`s, which are `AvaloniaObject`s, and that constructor calls the dispatcher's
    /// `VerifyAccess`. On a plain `[Fact]` this runs on a thread-pool thread and throws
    /// `InvalidOperationException: Call from invalid thread` - but only once the headless
    /// dispatcher exists, so whether it threw depended on what ran before it. It failed on the
    /// Windows runner as a lone 1-of-1309 and passed on the commit before, which is the signature.
    /// </summary>
    [AvaloniaFact]
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
