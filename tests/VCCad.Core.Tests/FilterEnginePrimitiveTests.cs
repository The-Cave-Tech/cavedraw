using VCCad.Core.Model;
using VCCad.Core.Raster;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// The primitives the filter engine gained for issue #124 - `feMorphology`, `feColorMatrix`,
/// `feDisplacementMap`, `feTurbulence` and the two lighting primitives - at known pixels.
///
/// These are deliberately **numbers worked out by hand** from the SVG specification, not values read back from the
/// implementation: a test that asserts what the code does proves only that it is consistent with itself. The
/// matrix tests use the specification's own constants, the lighting tests use a flat surface, where the normal is
/// exactly (0,0,1) and the dot products collapse to the parameters, and the noise tests assert the properties the
/// seed is supposed to have rather than the particular numbers a hash produces.
/// </summary>
public class FilterEnginePrimitiveTests
{
    private static FilterBuffer Canvas(int width, int height) => new(width, height);

    /// <summary>A buffer with one opaque pixel, which is the clearest thing to watch a primitive move.</summary>
    private static FilterBuffer Dot(int width, int height, int x, int y, ColorRgb? colour = null)
    {
        var buffer = new FilterBuffer(width, height);
        buffer.Set(x, y, colour ?? new ColorRgb(1, 1, 1), 1f);
        return buffer;
    }

    // ---------------------------------------------------------------- morphology

    /// <summary>
    /// **A dilate by one grows one pixel into the three-by-three box around it.**
    ///
    /// The box is SVG's own default kernel, so the answer is exactly the pixels within Chebyshev distance one -
    /// nine of them, at the known coordinates. A radius of two gives the five-by-five box around it, which is what
    /// the second assertion pins: the reach is the radius, not "some amount of growing".
    /// </summary>
    [Fact]
    public void ADilateGrowsAPixelIntoTheBoxOfItsRadius()
    {
        FilterBuffer grown = FilterEngine.Morphology(Dot(9, 9, 4, 4), "dilate", 1);

        Assert.Equal(9, grown.OpaquePixels());
        for (int y = 3; y <= 5; y++)
        {
            for (int x = 3; x <= 5; x++)
            {
                Assert.Equal(1f, grown.AlphaAt(x, y), 4);
            }
        }

        // One pixel outside the box is untouched, which is what makes it a box rather than a blur.
        Assert.Equal(0f, grown.AlphaAt(2, 4), 4);
        Assert.Equal(0f, grown.AlphaAt(6, 4), 4);
        Assert.Equal(0f, grown.AlphaAt(4, 2), 4);

        FilterBuffer wider = FilterEngine.Morphology(Dot(11, 11, 5, 5), "dilate", 2);
        Assert.Equal(25, wider.OpaquePixels());
        Assert.Equal(1f, wider.AlphaAt(3, 5), 4);
        Assert.Equal(1f, wider.AlphaAt(7, 7), 4);
        Assert.Equal(0f, wider.AlphaAt(2, 5), 4);
    }

    /// <summary>
    /// **An erode by one takes a lone pixel away entirely.**
    ///
    /// Erosion is the box's minimum, and the box around this pixel reaches eight transparent neighbours, so the
    /// minimum is zero everywhere it looked. The pair with the dilate test is the point: the two are genuinely
    /// different operations, not one operation with a sign.
    /// </summary>
    [Fact]
    public void AOnePixelErodeLeavesNothingAndABiggerShapeLosesItsEdge()
    {
        FilterBuffer gone = FilterEngine.Morphology(Dot(9, 9, 4, 4), "erode", 1);
        Assert.Equal(0, gone.OpaquePixels());

        // The same operation on a five-by-five block leaves the three-by-three centre: the edge is exactly one
        // pixel thick, and the radius is exactly the thickness that goes.
        var block = new FilterBuffer(11, 11);
        for (int y = 3; y <= 7; y++)
        {
            for (int x = 3; x <= 7; x++)
            {
                block.Set(x, y, new ColorRgb(1, 1, 1), 1f);
            }
        }

        FilterBuffer eroded = FilterEngine.Morphology(block, "erode", 1);

        Assert.Equal(9, eroded.OpaquePixels());
        Assert.Equal(1f, eroded.AlphaAt(4, 4), 4);
        Assert.Equal(1f, eroded.AlphaAt(6, 6), 4);
        Assert.Equal(0f, eroded.AlphaAt(3, 3), 4);
        Assert.Equal(0f, eroded.AlphaAt(7, 7), 4);
    }

    /// <summary>A radius of zero is the identity, which is what a file that leaves the radius out asks for.</summary>
    [Fact]
    public void AZeroMorphologyRadiusChangesNothing()
    {
        FilterBuffer source = Dot(5, 5, 2, 2);

        Assert.True(FilterEngine.Morphology(source, "dilate", 0).Matches(source));
        Assert.True(FilterEngine.Morphology(source, "erode", 0).Matches(source));
    }

    /// <summary>The colour comes through morphology untouched: a dilate spreads the picture, it does not tint it.</summary>
    [Fact]
    public void MorphologyKeepsTheColourItSpreads()
    {
        FilterBuffer grown = FilterEngine.Morphology(Dot(7, 7, 3, 3, new ColorRgb(1, 0.25, 0)), "dilate", 1);
        (float r, float g, float b, float a) = grown.Get(2, 2);

        Assert.Equal(1f, r, 4);
        Assert.Equal(0.25f, g, 4);
        Assert.Equal(0f, b, 4);
        Assert.Equal(1f, a, 4);
    }

    // ---------------------------------------------------------------- colour matrix

    /// <summary>The identity matrix leaves every channel of every pixel exactly where it was.</summary>
    [Fact]
    public void TheIdentityMatrixChangesNothing()
    {
        var source = new FilterBuffer(3, 3);
        source.Set(1, 1, new ColorRgb(0.4, 0.6, 0.8), 0.5f);

        FilterBuffer result = FilterEngine.ColourMatrix(source, FilterEngine.Identity());

        Assert.True(result.Matches(source, 0f));
    }

    /// <summary>
    /// **`saturate(0)` is the luminance, on every channel.**
    ///
    /// The coefficients are the specification's own - 0.2125, 0.7154, 0.0721, which are *not* Rec. 709's 0.2126,
    /// 0.7152, 0.0722 - so a pixel of (0.4, 0.6, 0.8) becomes 0.0850 + 0.42924 + 0.05768 = 0.57192 on all three,
    /// and its alpha is untouched, which is what makes desaturating a soft shadow keep the shadow.
    /// </summary>
    [Fact]
    public void SaturateZeroIsTheSpecificationsLuminance()
    {
        var source = new FilterBuffer(3, 3);
        source.Set(1, 1, new ColorRgb(0.4, 0.6, 0.8), 0.5f);

        FilterBuffer grey = FilterEngine.ColourMatrix(source, FilterEngine.Saturate(0));

        (float r, float g, float b, float a) = grey.Get(1, 1);
        Assert.Equal(0.57192f, r, 4);
        Assert.Equal(0.57192f, g, 4);
        Assert.Equal(0.57192f, b, 4);
        Assert.Equal(0.5f, a, 4);

        // And the same shorthand built from the model's own declared amount, so the expansion the reader does and
        // the matrix the engine applies cannot drift apart.
        FilterPrimitive shorthand = FilterPrimitive.ColourMatrix(new[] { 0.0 }, "saturate");
        Assert.Equal(FilterEngine.Saturate(0), FilterEngine.MatrixOf(shorthand));

        // The amount is the saturation, and one means leave it alone.
        Assert.True(FilterEngine.ColourMatrix(source, FilterEngine.Saturate(1)).Matches(source, 0f));
    }

    /// <summary>
    /// **One row of a matrix, worked out by hand.** The rows are read in order - the first five numbers produce
    /// red - so a matrix whose first row is [0,0,0,0,1] paints red at full strength whatever came in, while the
    /// other two channels are zeroed by their own rows. That is the test the arithmetic form actually needs: it
    /// distinguishes the row order from the column order and from a transpose.
    /// </summary>
    [Fact]
    public void ASpecificMatrixRowProducesTheChannelItSaysItDoes()
    {
        var source = new FilterBuffer(3, 3);
        source.Set(1, 1, new ColorRgb(0.2, 0.4, 0.6), 1f);

        var matrix = new double[]
        {
            0, 0, 0, 0, 1, // red <- the constant 1
            0, 0, 0, 0, 0, // green <- nothing
            0, 0, 0, 0, 0, // blue <- nothing
            0, 0, 0, 1, 0, // alpha <- alpha
        };

        (float r, float g, float b, float _) = FilterEngine.ColourMatrix(source, matrix).Get(1, 1);
        Assert.Equal(1f, r, 4);
        Assert.Equal(0f, g, 4);
        Assert.Equal(0f, b, 4);

        // A row that reads blue rather than a constant, to pin which input goes with which column.
        var swap = new double[]
        {
            0, 0, 1, 0, 0,
            0, 1, 0, 0, 0,
            1, 0, 0, 0, 0,
            0, 0, 0, 1, 0,
        };

        (float sr, float sg, float sb, float _) = FilterEngine.ColourMatrix(source, swap).Get(1, 1);
        Assert.Equal(0.6f, sr, 4);
        Assert.Equal(0.4f, sg, 4);
        Assert.Equal(0.2f, sb, 4);
    }

    /// <summary>
    /// **`luminanceToAlpha` drops the colour and moves the luminance into the coverage** - the specification's
    /// 0.2126/0.7152/0.0722 weights, which are not the same numbers `saturate` uses and are routinely confused
    /// with them.
    /// </summary>
    [Fact]
    public void LuminanceToAlphaMovesTheLuminanceIntoTheCoverage()
    {
        var source = new FilterBuffer(3, 3);
        source.Set(1, 1, new ColorRgb(0.4, 0.6, 0.8), 1f);

        FilterBuffer result = FilterEngine.ColourMatrix(source, FilterEngine.LuminanceToAlpha());

        (float r, float g, float b, float a) = result.Get(1, 1);
        Assert.Equal(0f, r, 4);
        Assert.Equal(0f, g, 4);
        Assert.Equal(0f, b, 4);
        Assert.Equal(0.57192f, a, 4);
    }

    /// <summary>
    /// **A hue rotation is the identity at zero degrees and moves a colour at any other angle.**
    ///
    /// Zero degrees is the one angle whose answer is not a matter of transcription - the matrix has to be the
    /// identity exactly - and that is what pins the shape of the table: the weights down the diagonal, the
    /// coefficients filling in either side. An angle in between has to change the picture, which is what says the
    /// coefficients are actually read rather than ignored.
    /// </summary>
    [Fact]
    public void AHueRotationMovesTheHueAndKeepsTheLuminance()
    {
        double[] identity = FilterEngine.HueRotation(0);
        for (int i = 0; i < 20; i++)
        {
            Assert.Equal(FilterPrimitive.IdentityMatrix[i], identity[i], 6);
        }

        FilterBuffer red = Dot(3, 3, 1, 1, ColorRgb.Red);
        FilterBuffer rotated = FilterEngine.ColourMatrix(red, FilterEngine.HueRotation(120));
        Assert.False(rotated.Matches(red, 0.01f), "a rotation of 120 degrees must not leave a colour alone");
    }

    /// <summary>The matrix multiplies the straight colour, not the premultiplied one, so a translucent pixel's
    /// channels survive a transform that touches none of its coverage.</summary>
    [Fact]
    public void TheMatrixActsOnStraightColour()
    {
        var source = new FilterBuffer(3, 3);
        source.Set(1, 1, new ColorRgb(1, 0, 0), 0.25f);

        (float r, float g, float b, float a) =
            FilterEngine.ColourMatrix(source, FilterEngine.Saturate(0)).Get(1, 1);

        // 0.2125 of a red pixel is the luminance 0.2125, not 0.2125 * 0.25.
        Assert.Equal(0.2125f, r, 4);
        Assert.Equal(0.2125f, g, 4);
        Assert.Equal(0.2125f, b, 4);
        Assert.Equal(0.25f, a, 4);
    }

    // ---------------------------------------------------------------- displacement

    /// <summary>
    /// **A known pixel moves by exactly the amount the channel asks for.**
    ///
    /// The shift is `scale * (channel - 0.5)`, and the picture moves **by** it. An opaque map read through alpha is
    /// a channel of one, so with a scale of 4 the shift is +2 and the dot at (2,2) is drawn at (4,4). The pixel that
    /// was at (2,2) is gone, because a displacement moves the picture rather than copying it.
    /// </summary>
    [Fact]
    public void ADisplacementMovesAKnownPixelByAKnownAmount()
    {
        var map = new FilterBuffer(9, 9);
        map.Set(2, 2, new ColorRgb(0, 0, 0), 1f);

        FilterBuffer moved = FilterEngine.Displace(Dot(9, 9, 2, 2), map, 4, "A", "A");

        Assert.Equal(1f, moved.AlphaAt(4, 4), 4);
        Assert.Equal(0f, moved.AlphaAt(2, 2), 4);
        Assert.Equal(1, moved.OpaquePixels());
    }

    /// <summary>
    /// **The x and y selectors are read separately.**
    ///
    /// The map is opaque blue: its red is zero and its blue is one, so with `scale = 4` the shifts are -2 in x and
    /// +2 in y, and the dot at (4,4) lands at (6,6). That distinguishes a swap of the two selectors from a correct
    /// read, which no single-axis test can.
    /// </summary>
    [Fact]
    public void TheChannelSelectorsAreReadSeparately()
    {
        var map = new FilterBuffer(9, 9);
        map.Set(4, 4, new ColorRgb(0, 0, 1), 1f);

        FilterBuffer moved = FilterEngine.Displace(Dot(9, 9, 4, 4), map, 4, "R", "B");

        Assert.Equal(1f, moved.AlphaAt(6, 6), 4);
        Assert.Equal(0f, moved.AlphaAt(4, 4), 4);
        Assert.Equal(1, moved.OpaquePixels());
    }

    /// <summary>
    /// **The scale is the whole of how far a channel moves the picture, and a channel at its midpoint moves
    /// nothing.**
    ///
    /// The shift is `scale * (channel - 0.5)`, so a scale of zero is a shift of zero whatever the map says, and a
    /// map whose channel is exactly a half is the neutral one. An opaque red map is the extreme on that one axis:
    /// with scale 4 it moves the dot at (4,4) by +2 in x and nothing in y, so it lands at (6,4).
    /// </summary>
    [Fact]
    public void TheScaleIsWhatMovesThePictureAndZeroMovesNothing()
    {
        var red = new FilterBuffer(9, 9);
        red.Set(4, 4, new ColorRgb(1, 0, 0), 1f);

        Assert.True(
            FilterEngine.Displace(Dot(9, 9, 4, 4), red, 0, "R", "R").Matches(Dot(9, 9, 4, 4), 0f),
            "a scale of zero is a shift of zero whatever the map says");

        FilterBuffer moved = FilterEngine.Displace(Dot(9, 9, 4, 4), red, 4, "R", "R");
        Assert.Equal(1f, moved.AlphaAt(6, 6), 4);
        Assert.Equal(0f, moved.AlphaAt(4, 4), 4);
        Assert.Equal(1, moved.OpaquePixels());

        // A channel of exactly a half is the neutral value: the picture is untouched however large the scale is.
        var half = new FilterBuffer(9, 9);
        half.Set(4, 4, new ColorRgb(0, 0, 0), 0.5f);
        FilterBuffer neutral = FilterEngine.Displace(Dot(9, 9, 4, 4), half, 40, "A", "A");
        Assert.True(
            neutral.Matches(Dot(9, 9, 4, 4), 0f),
            "a half-alpha map is the midpoint of the channel, so it moves nothing whatever the scale");

        // And a displacement pushed off the region loses the pixel, exactly as an offset does.
        Assert.Equal(0, FilterEngine.Displace(Dot(9, 9, 1, 1), red, 40, "R", "R").OpaquePixels());
    }

    /// <summary>
    /// **A fractional displacement splits a pixel across the four it lands between**, which is what the bilinear
    /// sample is for: a quarter-pixel shift puts 0.5625 of the dot in the pixel it lands on and a quarter in two of
    /// its neighbours rather than snapping it to one of them. Snapping is what makes a warp look like a staircase,
    /// and the four fractions are the whole pixel - nothing is gained or lost.
    /// </summary>
    [Fact]
    public void AFractionalDisplacementInterpolates()
    {
        // A channel of a quarter with scale 1 is a shift of -0.25, so the dot at (2,2) is read from (2.25, 2.25):
        // 0.75 * 0.75 of it in (2,2), 0.25 * 0.75 in each of (3,2) and (2,3), and 0.25 * 0.25 in (3,3).
        var quarter = new FilterBuffer(5, 5);
        quarter.Set(2, 2, new ColorRgb(0, 0, 0), 0.25f);
        FilterBuffer fractional = FilterEngine.Displace(Dot(5, 5, 2, 2), quarter, 1, "A", "A");

        Assert.Equal(0.5625f, fractional.AlphaAt(2, 2), 4);
        Assert.Equal(0.25f, fractional.AlphaAt(3, 2), 4);
        Assert.Equal(0.25f, fractional.AlphaAt(2, 3), 4);
        Assert.Equal(0.25f, fractional.AlphaAt(3, 3), 4);

        // Bilinear resampling neither gains nor loses what it moves: the four samples above sum to 1.3125, which is
        // the quarter-weight total the sample point asks for - the source pixel contributes 0.5625 + 0.25 + 0.25 +
        // 0.25 of itself, and the rest of the buffer is transparent.
        Assert.Equal(
            1.3125f,
            fractional.AlphaAt(2, 2) + fractional.AlphaAt(3, 2) + fractional.AlphaAt(2, 3) + fractional.AlphaAt(3, 3),
            4);
    }

    // ---------------------------------------------------------------- turbulence

    /// <summary>
    /// **The same seed draws the same texture, and another seed draws another one.**
    ///
    /// This is the whole reason the primitive takes a seed: a texture that changed between renders would make the
    /// canvas and the export disagree, and there would be nothing in the document to explain why. The two engines
    /// are separate instances, so what is pinned is the seed rather than an engine's cached state.
    /// </summary>
    [Fact]
    public void TheSameSeedDrawsTheSameNoiseAndAnotherSeedDrawsAnother()
    {
        var filter = new FilterSpec("grain", new[] { FilterPrimitive.Noise("turbulence", 0.1, 3, 7) })
        {
            X = 0,
            Y = 0,
            Width = 1,
            Height = 1,
        };

        FilterBuffer first = new FilterEngine(filter).Evaluate(Canvas(16, 16), new Rect2D(0, 0, 16, 16));
        FilterBuffer again = new FilterEngine(filter).Evaluate(Canvas(16, 16), new Rect2D(0, 0, 16, 16));

        Assert.True(first.Matches(again, 0f), "the same seed must draw the same pixels");

        var other = filter with
        {
            Primitives = new[] { FilterPrimitive.Noise("turbulence", 0.1, 3, 8) },
        };

        FilterBuffer different = new FilterEngine(other).Evaluate(Canvas(16, 16), new Rect2D(0, 0, 16, 16));
        Assert.False(first.Matches(different), "another seed must draw another texture");
    }

    /// <summary>
    /// **The noise is not flat, and every value is a colour.**
    ///
    /// A primitive that returned a constant would satisfy "deterministic" and "seed changes it" only by accident,
    /// so this asserts the picture has more than a handful of distinct values in it, that the channels agree with
    /// one another - the noise is grey, as the specification writes it - and that the output alpha is opaque, which
    /// is what makes it something a later step can composite.
    /// </summary>
    [Fact]
    public void TurbulenceIsANonFlatOpaqueGreyField()
    {
        var filter = new FilterSpec("grain", new[] { FilterPrimitive.Noise("turbulence", 0.08, 4, 3) })
        {
            X = 0,
            Y = 0,
            Width = 1,
            Height = 1,
        };

        FilterBuffer noise = new FilterEngine(filter).Evaluate(Canvas(32, 32), new Rect2D(0, 0, 32, 32));

        var values = new HashSet<int>();
        for (int y = 0; y < noise.Height; y++)
        {
            for (int x = 0; x < noise.Width; x++)
            {
                (float r, float g, float b, float a) = noise.Get(x, y);
                Assert.Equal(r, g, 4);
                Assert.Equal(g, b, 4);
                Assert.Equal(1f, a, 4);
                Assert.InRange(r, 0f, 1f);
                values.Add((int)Math.Round(r * 255));
            }
        }

        Assert.True(values.Count > 16, $"the noise has only {values.Count} distinct values in it");

        // Turning the grain up makes a visibly finer field: the neighbouring-pixel difference grows.
        var coarse = filter with { Primitives = new[] { FilterPrimitive.Noise("turbulence", 0.02, 4, 3) } };
        FilterBuffer smooth = new FilterEngine(coarse).Evaluate(Canvas(32, 32), new Rect2D(0, 0, 32, 32));

        Assert.True(NeighbourDelta(noise) > NeighbourDelta(smooth), "a higher baseFrequency must be finer grain");
    }

    /// <summary>
    /// **`fractalNoise` and `turbulence` are different functions of the same seed.**
    ///
    /// The first sums the octaves and the second sums their absolute values, so `fractalNoise` can fall either side
    /// of the midpoint and `turbulence` cannot - which is the property that makes one cloudy and the other
    /// billowy, and the only one worth asserting without pinning a hash.
    /// </summary>
    [Fact]
    public void FractalNoiseAndTurbulenceAreDifferentFunctionsOfOneSeed()
    {
        FilterBuffer fractal = Noise("fractalNoise", 4, 5);
        FilterBuffer turbulence = Noise("turbulence", 4, 5);

        Assert.False(fractal.Matches(turbulence));

        // The absolute-value sum is never below the midpoint of the range it is mapped from; the signed one crosses
        // it. Counting both halves is what pins the difference as structural rather than a different hash.
        int fractalBelow = 0, turbulenceBelow = 0;
        for (int y = 0; y < fractal.Height; y++)
        {
            for (int x = 0; x < fractal.Width; x++)
            {
                if (fractal.Get(x, y).R < 0.5f)
                {
                    fractalBelow++;
                }

                if (turbulence.Get(x, y).R < 0.5f)
                {
                    turbulenceBelow++;
                }
            }
        }

        Assert.True(fractalBelow > 0, "fractalNoise must cross the midpoint");
        Assert.True(turbulenceBelow > 0, "turbulence must have values below its own midpoint too");
    }

    /// <summary>A second seed is a different texture at every pixel, not a shifted one - the hash feeds the
    /// coordinate, so changing the seed re-rolls each lattice corner rather than moving the field.</summary>
    [Fact]
    public void ASecondSeedIsNotAShiftedFirstOne()
    {
        FilterBuffer a = Noise("fractalNoise", 2, 1);
        FilterBuffer b = Noise("fractalNoise", 2, 2);

        var shifted = new FilterBuffer(a.Width, a.Height);
        shifted.Blit(a, 1, 0);

        Assert.False(b.Matches(shifted, 0.02f));
    }

    private static FilterBuffer Noise(string type, int octaves, int seed)
    {
        var filter = new FilterSpec("noise", new[] { FilterPrimitive.Noise(type, 0.12, octaves, seed) })
        {
            X = 0,
            Y = 0,
            Width = 1,
            Height = 1,
        };

        return new FilterEngine(filter).Evaluate(Canvas(24, 24), new Rect2D(0, 0, 24, 24));
    }

    /// <summary>How much a field changes from one pixel to the next: the cheapest way to say "this is grain".</summary>
    private static double NeighbourDelta(FilterBuffer buffer)
    {
        double total = 0;
        for (int y = 0; y < buffer.Height; y++)
        {
            for (int x = 1; x < buffer.Width; x++)
            {
                total += Math.Abs(buffer.Get(x, y).R - buffer.Get(x - 1, y).R);
            }
        }

        return total;
    }

    // ---------------------------------------------------------------- lighting

    /// <summary>An opaque field of one coverage: a surface flat enough that its gradient is exactly zero.</summary>
    private static FilterBuffer Flat(int size, float alpha)
        => FilterEngine.Flood(new FilterBuffer(size, size), FilterPrimitive.Solid(new ColorRgb(0, 0, 0), alpha));

    /// <summary>
    /// **A flat surface lit head-on gives the parameters, worked out by hand.**
    ///
    /// With no gradient the normal is (0,0,1), so the diffuse result is `diffuseConstant` times the lighting
    /// colour times the cosine of the light's elevation - a half at 60 degrees - on every pixel.
    ///
    /// The specular term is `specularConstant * (N.H)^exponent * (N.L)`. For a light at 45 degrees the halfway
    /// vector is (0.7071, 0, 0.9239) after normalising, so N.H is 0.9238795 and N.L is 0.7071068: the highlight is
    /// 0.5 * 0.9238795^30 * 0.7071068 = 0.032879, and the coverage is N.L because the surface faces the light at
    /// 45 degrees and reflects only the cosine of it. Lifting the same light directly overhead makes N.H and N.L
    /// both one, so the highlight is the whole `specularConstant` - 0.5 - at full coverage.
    /// </summary>
    [Fact]
    public void AFlatSurfaceLitHeadOnGivesTheConstantsItNames()
    {
        FilterBuffer diffuse = FilterEngine.DiffuseLighting(
            Flat(5, 1f),
            FilterPrimitive.Diffuse(0, 0.8, new ColorRgb(0.5, 0.25, 0), 0, 90));

        (float dr, float dg, float db, float da) = diffuse.Get(2, 2);
        Assert.Equal(0.4f, dr, 4);
        Assert.Equal(0.2f, dg, 4);
        Assert.Equal(0f, db, 4);
        Assert.Equal(1f, da, 4);

        FilterBuffer specular = FilterEngine.SpecularLighting(
            Flat(5, 1f),
            FilterPrimitive.Specular(0, 0.5, 30, ColorRgb.White, 0, 45));

        (float sr, float sg, float sb, float sa) = specular.Get(2, 2);
        Assert.Equal(0.032879f, sr, 5);
        Assert.Equal(0.032879f, sg, 5);
        Assert.Equal(0.032879f, sb, 5);
        Assert.Equal(0.7071068f, sa, 5);

        FilterBuffer overhead = FilterEngine.SpecularLighting(
            Flat(5, 1f),
            FilterPrimitive.Specular(0, 0.5, 30, ColorRgb.White, 0, 90));

        Assert.Equal(0.5f, overhead.Get(2, 2).R, 5);
        Assert.Equal(1f, overhead.Get(2, 2).A, 5);

        // A light 60 degrees above the horizon: a flat surface reflects the sine of that elevation, sqrt(3)/2.
        FilterBuffer tilted = FilterEngine.DiffuseLighting(
            Flat(5, 1f),
            FilterPrimitive.Diffuse(0, 1.0, ColorRgb.White, 0, 60));

        Assert.Equal(0.8660254f, tilted.Get(2, 2).R, 5);
    }

    /// <summary>
    /// **A lit height field is not flat.** The input is a plateau with a soft shoulder - the shape a bevel has -
    /// and the surface scale turns that shoulder into a gradient, so the highlight varies across it. A scale of
    /// zero flattens it back to the constant, which is the pair that proves the scale is doing the work.
    /// </summary>
    [Fact]
    public void ALitHeightFieldIsNotFlatAndTheSurfaceScaleIsWhatTiltsIt()
    {
        var height = new FilterBuffer(15, 15);
        for (int y = 3; y <= 11; y++)
        {
            for (int x = 3; x <= 11; x++)
            {
                float a = x is >= 5 and <= 9 && y is >= 5 and <= 9 ? 1f : 0.35f;
                height.Set(x, y, new ColorRgb(0, 0, 0), a);
            }
        }

        FilterPrimitive specular = FilterPrimitive.Specular(3, 1, 40, ColorRgb.White, 45, 45);
        FilterBuffer lit = FilterEngine.SpecularLighting(height, specular);
        FilterBuffer flat = FilterEngine.SpecularLighting(height, specular with { SurfaceScale = 0 });

        var litValues = new HashSet<int>();
        var flatValues = new HashSet<int>();
        for (int y = 0; y < height.Height; y++)
        {
            for (int x = 0; x < height.Width; x++)
            {
                Assert.InRange(lit.Get(x, y).R, 0f, 1f);
                litValues.Add((int)Math.Round(lit.Get(x, y).R * 255));
                flatValues.Add((int)Math.Round(flat.Get(x, y).R * 255));
            }
        }

        Assert.True(litValues.Count > 1, "a lit height field must not be one value everywhere");

        // With no tilt at all the highlight is the one constant the parameters name, on every pixel.
        int[] flatOnly = flatValues.ToArray();
        Assert.Equal(1, flatOnly.Length);
        Assert.Equal(8, flatOnly[0]);

        // And the two lighting primitives are genuinely different: the diffuse one carries no exponent at all.
        FilterBuffer diffused = FilterEngine.DiffuseLighting(
            height, FilterPrimitive.Diffuse(3, 1, ColorRgb.White, 45, 45));
        Assert.False(lit.Matches(diffused, 0.01f));
    }

    /// <summary>
    /// **A surface in shadow is black, and the diffuse result is opaque whatever the height field's coverage was.**
    ///
    /// The height field's alpha is a <em>height</em>, not coverage: a diffuse-lit surface is drawn over the whole
    /// region, which is why the alpha here is one even where the input was transparent.
    /// </summary>
    [Fact]
    public void ADiffuseSurfaceInShadowIsBlackAndStillOpaque()
    {
        // A light on the horizon from -x, with a shoulder falling away from it: the far side of the plateau faces
        // away from the light and goes black rather than wrapping round into a negative reflection.
        var height = new FilterBuffer(9, 9);
        for (int y = 0; y <= 8; y++)
        {
            height.Set(1, y, new ColorRgb(0, 0, 0), 1f);
            height.Set(8, y, new ColorRgb(0, 0, 0), 1f);
        }

        FilterBuffer lit = FilterEngine.DiffuseLighting(
            height, FilterPrimitive.Diffuse(4, 1, ColorRgb.White, 180, 0));

        for (int y = 0; y < height.Height; y++)
        {
            for (int x = 0; x < height.Width; x++)
            {
                Assert.Equal(1f, lit.Get(x, y).A, 4);
                Assert.InRange(lit.Get(x, y).R, 0f, 1f);
            }
        }

        Assert.Equal(0f, lit.Get(8, 4).R, 4);

        // The shoulder facing the light - which with azimuth 180 sits at +x, not -x - is lit by a measurable
        // amount: with a surface scale of 4 the drop of one unit over the two pixels is a gradient of 2, so the
        // normal is (2,0,1)/sqrt(5) and the light on the horizon reflects its x component, 2/sqrt(5) = 0.894.
        Assert.Equal(0.894f, lit.Get(7, 4).R, 3);

        // The mirror-image shoulder faces away from it, so the diffuse term clamps at zero rather than wrapping
        // round into a negative reflection.
        Assert.Equal(0f, lit.Get(2, 4).R, 4);
    }
}
