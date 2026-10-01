using VCCad.Core.Model;
using VCCad.Core.Raster;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// The filter engine, at known pixels.
///
/// A filter is a raster operation, so these assert **pixels** rather than that something ran: a blur's spread, an
/// offset's displacement, a flood's colour, a composite's arithmetic for each operator, a blend's result for each
/// mode. The numbers are worked out by hand from the SVG specification's formulas, not read back from the engine -
/// a test that asserts what the code does proves only that it is consistent with itself.
/// </summary>
public class FilterEngineTests
{
    private static FilterBuffer Canvas(int width, int height)
        => new(width, height);

    /// <summary>A buffer with one opaque white pixel, which is the clearest thing to watch a filter move.</summary>
    private static FilterBuffer Dot(int width, int height, int x, int y, ColorRgb? colour = null)
    {
        var buffer = new FilterBuffer(width, height);
        ColorRgb ink = colour ?? new ColorRgb(1, 1, 1);
        buffer.Set(x, y, ink, 1f);
        return buffer;
    }

    // ---------------------------------------------------------------- blur

    [Fact]
    public void ABlurSpreadsOnePixelAndIsSymmetric()
    {
        FilterBuffer blurred = FilterEngine.Blur(Dot(9, 9, 4, 4), 1.0);

        // The centre is no longer fully opaque, because its energy has gone outward.
        Assert.True(blurred.AlphaAt(4, 4) < 1.0f);
        Assert.True(blurred.AlphaAt(4, 4) > 0.05f);

        // And it went outward, equally in every direction - which is what makes it a blur rather than a smear.
        Assert.Equal(blurred.AlphaAt(3, 4), blurred.AlphaAt(5, 4), 4);
        Assert.Equal(blurred.AlphaAt(4, 3), blurred.AlphaAt(4, 5), 4);
        Assert.Equal(blurred.AlphaAt(3, 4), blurred.AlphaAt(4, 3), 4);

        // Further out is dimmer than closer in.
        Assert.True(blurred.AlphaAt(2, 4) < blurred.AlphaAt(3, 4));
    }

    /// <summary>A blur conserves what it spreads: the alpha it took from the centre is alpha elsewhere.</summary>
    [Fact]
    public void ABlurKeepsTheTotal()
    {
        FilterBuffer source = Dot(21, 21, 10, 10);
        FilterBuffer blurred = FilterEngine.Blur(source, 2.0);

        float total = 0;
        for (int y = 0; y < blurred.Height; y++)
        {
            for (int x = 0; x < blurred.Width; x++)
            {
                total += blurred.AlphaAt(x, y);
            }
        }

        // The kernel is normalised and the region is large enough to hold the result, so the sum survives.
        Assert.Equal(1.0f, total, 2);
    }

    [Fact]
    public void AZeroBlurChangesNothing()
    {
        FilterBuffer source = Dot(5, 5, 2, 2);
        Assert.True(FilterEngine.Blur(source, 0).Matches(source));
    }

    // ---------------------------------------------------------------- offset

    [Fact]
    public void AnOffsetMovesThePictureAndNothingElse()
    {
        FilterBuffer moved = FilterEngine.OffsetBy(Dot(11, 11, 2, 2), 3, 4);

        Assert.Equal(1f, moved.AlphaAt(5, 6), 4);
        Assert.Equal(0f, moved.AlphaAt(2, 2), 4);
        Assert.Equal(1, moved.OpaquePixels());
    }

    /// <summary>Pushed off the region, a pixel is gone - the region is the filter's canvas.</summary>
    [Fact]
    public void AnOffsetOffTheRegionLosesThePixel()
    {
        Assert.Equal(0, FilterEngine.OffsetBy(Dot(5, 5, 1, 1), 20, 0).OpaquePixels());
    }

    // ---------------------------------------------------------------- flood

    [Fact]
    public void AFloodFillsTheRegionWithItsColourAndOpacity()
    {
        var primitive = FilterPrimitive.Solid(new ColorRgb(0, 0.5, 1), 0.25);
        FilterBuffer flooded = FilterEngine.Flood(new FilterBuffer(4, 3), primitive);

        (float r, float g, float b, float a) = flooded.Get(2, 1);
        Assert.Equal(0f, r, 4);
        Assert.Equal(0.5f, g, 4);
        Assert.Equal(1f, b, 4);
        Assert.Equal(0.25f, a, 4);

        // Every pixel, not just the one.
        Assert.Equal(12, flooded.OpaquePixels());
    }

    // ---------------------------------------------------------------- composite

    /// <summary>
    /// **Each operator's arithmetic**, with alpha values chosen so the answer is unambiguous: A is opaque red, B is
    /// half-transparent blue.
    /// </summary>
    [Theory]
    [InlineData("over", 1.0f, 1.0f, 0.0f)]      // A covers B entirely; B contributes nothing.
    [InlineData("in", 0.5f, 1.0f, 0.0f)]        // A clipped to B's coverage: half a red pixel.
    [InlineData("out", 0.5f, 1.0f, 0.0f)]       // A outside B: still half, because B's coverage is what is removed.
    [InlineData("atop", 0.5f, 1.0f, 0.0f)]      // A over B, keeping B's coverage: half a red pixel.
    [InlineData("xor", 0.5f, 1.0f, 0.0f)]       // A outside B: half a red pixel, B contributing nothing where A is.
    public void EachCompositeOperatorHasTheArithmeticItSays(string op, float alpha, float r, float b)
    {
        FilterBuffer a = Dot(3, 3, 1, 1, new ColorRgb(1, 0, 0));
        FilterBuffer backdrop = new FilterBuffer(3, 3);
        backdrop.Set(1, 1, new ColorRgb(0, 0, 1), 0.5f);

        FilterBuffer result = FilterEngine.Composite(a, backdrop, op);
        (float pr, float pg, float pb, float pa) = result.Get(1, 1);

        Assert.Equal(alpha, pa, 4);
        Assert.Equal(r, pr, 4);
        Assert.Equal(0f, pg, 4);
        Assert.Equal(b, pb, 4);
    }

    /// <summary>An unknown operator is treated as `over`, the specification's default, rather than producing
    /// nothing at all.</summary>
    [Fact]
    public void AnUnknownCompositeOperatorFallsBackToOver()
    {
        FilterBuffer a = Dot(3, 3, 1, 1, new ColorRgb(1, 0, 0));
        FilterBuffer b = new FilterBuffer(3, 3);

        FilterBuffer unknown = FilterEngine.Composite(a, b, "nonsense");
        FilterBuffer over = FilterEngine.Composite(a, b, "over");

        Assert.True(unknown.Matches(over));
    }

    // ---------------------------------------------------------------- blend

    /// <summary>**Each blend mode's result**, on opaque colours where the arithmetic is exact.</summary>
    [Theory]
    [InlineData("normal", 0.25f, 0.25f)]    // the source, unchanged - it is opaque, so it simply covers the backdrop
    [InlineData("multiply", 0.25f, 0.125f)] // 0.5 * 0.25
    [InlineData("screen", 0.25f, 0.625f)]   // 0.5 + 0.25 - 0.125
    [InlineData("darken", 0.25f, 0.25f)]
    [InlineData("lighten", 0.25f, 0.5f)]
    public void EachBlendModeHasTheResultItSays(string mode, float source, float expected)
    {
        // The backdrop is mid-grey and opaque; the source is the lighter or darker value under test.
        var backdrop = new FilterBuffer(3, 3);
        backdrop.Set(1, 1, new ColorRgb(0.5, 0.5, 0.5), 1f);
        FilterBuffer first = Dot(3, 3, 1, 1, new ColorRgb(source, source, source));

        // `in` is the source being blended, `in2` is the backdrop it lands on.
        FilterBuffer result = FilterEngine.Blend(first, backdrop, mode);

        Assert.Equal(expected, result.Get(1, 1).R, 4);
        Assert.Equal(1f, result.Get(1, 1).A, 4);
    }

    /// <summary>A blend over a transparent backdrop is the source, not the source darkened toward black - which is
    /// the mistake that makes every shadow go grey.</summary>
    [Fact]
    public void ABlendOverNothingIsTheSource()
    {
        FilterBuffer source = Dot(3, 3, 1, 1, new ColorRgb(0.4, 0.4, 0.4));
        FilterBuffer result = FilterEngine.Blend(source, new FilterBuffer(3, 3), "multiply");

        Assert.Equal(0.4f, result.Get(1, 1).R, 4);
        Assert.Equal(1f, result.Get(1, 1).A, 4);
    }

    // ---------------------------------------------------------------- the graph

    /// <summary>
    /// **One result feeding two consumers.** Both consumers must see the same buffer: a walk that treated the
    /// primitives as a pipeline would hand the second one the first's output instead, and the picture would be
    /// wrong in a way no per-primitive test could see.
    /// </summary>
    [Fact]
    public void OneResultFeedsTwoConsumers()
    {
        var filter = new FilterSpec("shared", new[]
        {
            FilterPrimitive.Blur(1.0, input: "SourceAlpha", result: "soft"),

            // `soft` feeds this one...
            FilterPrimitive.Combine("in", "soft", "soft", "doubled"),

            // ...and this one, which reads it after another step has run.
            FilterPrimitive.Combine("out", "soft", "SourceAlpha", "outside"),
        })
        {
            Output = "doubled",
        };

        FilterBuffer source = Dot(9, 9, 4, 4);
        FilterBuffer result = new FilterEngine(filter).Evaluate(source, new Rect2D(0, 0, 9, 9));

        // `in` of soft with itself squares the coverage, so the centre is dimmer than the blur alone.
        FilterBuffer blurOnly = FilterEngine.Blur(source, 1.0);
        Assert.True(result.AlphaAt(4, 4) < blurOnly.AlphaAt(4, 4));
        Assert.True(result.AlphaAt(4, 4) > 0f);

        // And the named output is what came back, not the last primitive.
        FilterBuffer outside = FilterEngine.Composite(blurOnly, source, "out");
        Assert.NotEqual(outside.AlphaAt(4, 4), result.AlphaAt(4, 4), 3);
    }

    /// <summary>A graph that refers back to itself terminates rather than recursing until the stack runs out.</summary>
    [Fact]
    public void ACycleDoesNotRunAway()
    {
        var filter = new FilterSpec("cycle", new[]
        {
            // `loop` is its own input, and nothing defines it.
            FilterPrimitive.Blur(1.0, input: "loop", result: "loop"),
            FilterPrimitive.Blur(1.0, input: "loop"),
        })
        {
            // The region stated exactly, so the assertion is about the walk terminating and not about the default
            // ten per cent of margin.
            X = 0,
            Y = 0,
            Width = 1,
            Height = 1,
        };

        FilterBuffer result = new FilterEngine(filter).Evaluate(Dot(5, 5, 2, 2), new Rect2D(0, 0, 5, 5));

        Assert.Equal(5, result.Width);
        Assert.Equal(5, result.Height);
    }

    /// <summary>The implicit input is the previous primitive's result, which is why a file that names nothing
    /// works: a blur after a flood blurs the flood.</summary>
    [Fact]
    public void AnAbsentInputIsThePreviousResult()
    {
        var filter = new FilterSpec("chained", new[]
        {
            FilterPrimitive.Solid(new ColorRgb(1, 1, 1), 1.0, "ink"),
            FilterPrimitive.Blur(1.5),
        });

        FilterBuffer result = new FilterEngine(filter).Evaluate(new FilterBuffer(9, 9), new Rect2D(0, 0, 9, 9));

        // The flood covered everything, so the blur smooths a solid field: opaque in the middle, softer at the
        // region's edge where there is nothing outside to pull in.
        Assert.Equal(1f, result.AlphaAt(4, 4), 2);
        Assert.True(result.AlphaAt(0, 0) < 1f);
    }

    [Fact]
    public void TheEngineIsDeterministic()
    {
        var filter = new FilterSpec("det", new[]
        {
            FilterPrimitive.Blur(1.2, "SourceAlpha", result: "soft"),
            FilterPrimitive.OffsetBy(2, 1, "soft", "moved"),
            FilterPrimitive.Blended("multiply", "SourceGraphic", "moved"),
        });

        FilterBuffer source = Dot(12, 12, 5, 5, new ColorRgb(1, 0.5, 0));
        var engine = new FilterEngine(filter);
        Rect2D bounds = new(0, 0, 12, 12);

        Assert.True(engine.Evaluate(source, bounds).Matches(engine.Evaluate(source, bounds)));
    }

    // ---------------------------------------------------------------- the region

    /// <summary>The default region is ten per cent of margin: a blur reaches into it, and is cut off at its edge.</summary>
    [Fact]
    public void TheDefaultRegionGivesTheBlurTenPercentOfMargin()
    {
        var filter = new FilterSpec("blurred", new[] { FilterPrimitive.Blur(2.0, "SourceAlpha") });

        (int x, int y, int width, int height) = FilterEngine.RegionPixels(filter, new Rect2D(0, 0, 100, 100), 1.0);

        Assert.Equal(-10, x);
        Assert.Equal(-10, y);
        Assert.Equal(120, width);
        Assert.Equal(120, height);
    }

    /// <summary>
    /// **The region decides whether a blur spreads or is clipped**, and the way to see it is the total: a blur whose
    /// tail runs past the region loses that energy, and the same blur in a region with room keeps all of it.
    ///
    /// This is not a corner of the format. The default margin is ten per cent of the shape, and a shadow blur is
    /// routinely larger than that, so the default is exactly what makes a shadow look cut off.
    /// </summary>
    [Fact]
    public void ARegionTooSmallForTheBlurClipsItsEnergy()
    {
        FilterPrimitive[] primitives = { FilterPrimitive.Blur(4.0, "SourceAlpha") };
        FilterBuffer source = Dot(40, 40, 2, 2);
        Rect2D bounds = new(0, 0, 40, 40);

        var tight = new FilterSpec("tight", primitives) { X = -0.1, Y = -0.1, Width = 1.2, Height = 1.2 };
        var wide = new FilterSpec("wide", primitives) { X = -1.0, Y = -1.0, Width = 3.0, Height = 3.0 };

        FilterBuffer tightResult = new FilterEngine(tight).Evaluate(source, bounds);
        FilterBuffer wideResult = new FilterEngine(wide).Evaluate(source, bounds);

        // The source sits six pixels inside the tight region's left edge, while the blur reaches twelve - so a
        // measurable part of it falls outside and is gone.
        float clipped = Total(tightResult);
        float whole = Total(wideResult);

        Assert.True(clipped < whole - 0.01f, $"the tight region kept {clipped} of {whole}");
        Assert.Equal(1.0f, whole, 2);
    }

    private static float Total(FilterBuffer buffer)
    {
        float total = 0;
        for (int y = 0; y < buffer.Height; y++)
        {
            for (int x = 0; x < buffer.Width; x++)
            {
                total += buffer.AlphaAt(x, y);
            }
        }

        return total;
    }

    /// <summary>And a region that starts inside the shape clips the effect rather than growing the buffer.</summary>
    [Fact]
    public void ARegionInsideTheShapeClipsTheEffect()
    {
        var filter = new FilterSpec("clipped", new[] { FilterPrimitive.Blur(2.0, "SourceAlpha") })
        {
            X = 0.25,
            Y = 0.25,
            Width = 0.5,
            Height = 0.5,
        };

        (int x, int y, int width, int height) = FilterEngine.RegionPixels(filter, new Rect2D(0, 0, 100, 100), 1.0);

        Assert.Equal(25, x);
        Assert.Equal(25, y);
        Assert.Equal(50, width);
        Assert.Equal(50, height);
    }

    /// <summary>A filter evaluated at 2x blurs twice as many pixels, which is what makes it scale with a zoom.</summary>
    [Fact]
    public void TheScaleChangesThePixelSizeOfTheEffect()
    {
        var filter = new FilterSpec("scaled", new[] { FilterPrimitive.Blur(1.0, "SourceAlpha") });

        FilterBuffer one = new FilterEngine(filter, 1.0).Evaluate(Dot(20, 20, 10, 10), new Rect2D(0, 0, 20, 20));
        FilterBuffer two = new FilterEngine(filter, 2.0).Evaluate(Dot(40, 40, 20, 20), new Rect2D(0, 0, 20, 20));

        Assert.Equal(24, one.Width);
        Assert.Equal(48, two.Width);

        // The same relative point is soft in both, because the blur scaled with the buffer.
        Assert.True(one.AlphaAt(12, 12) > 0f);
        Assert.True(two.AlphaAt(24, 24) > 0f);
    }
}
