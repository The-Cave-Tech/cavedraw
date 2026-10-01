using VCCad.Core.Model;
using VCCad.Core.Raster;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// Raster effects as filter graphs, asserted at known pixels.
///
/// These effects were **model-only** before this: a blur or a shadow could be set, saved and re-opened, and changed
/// nothing on screen or in an export. The graphs are what makes them render, and they are evaluated by the same
/// engine the SVG filters use - so these check the arithmetic that was missing rather than a new evaluator.
/// </summary>
public class RasterEffectFiltersTests
{
    /// <summary>An opaque square of the given colour, which is the artwork a shadow falls from.</summary>
    private static FilterBuffer Square(int size = 21, int at = 8, int extent = 5, ColorRgb? colour = null)
    {
        var buffer = new FilterBuffer(size, size);
        ColorRgb ink = colour ?? new ColorRgb(0, 0, 1);
        for (int y = at; y < at + extent; y++)
        {
            for (int x = at; x < at + extent; x++)
            {
                buffer.Set(x, y, ink, 1f);
            }
        }

        return buffer;
    }

    private static FilterBuffer Apply(FilterBuffer source, params RasterEffectSpec[] effects)
        => RasterEffectFilters.Apply(source, effects, new ColorRgb(0, 0, 1));

    [Fact]
    public void ABlurSpreadsTheArtwork()
    {
        FilterBuffer source = Square();
        FilterBuffer blurred = Apply(source, RasterEffectSpec.Blur(2));

        // The artwork's own edge softens outward: a pixel just outside it now has some alpha.
        Assert.True(blurred.AlphaAt(7, 10) > 0.01f);
        Assert.True(blurred.AlphaAt(7, 10) < source.AlphaAt(8, 10));
    }

    /// <summary>**A drop shadow lands behind the artwork, displaced by its offsets.**</summary>
    [Fact]
    public void ADropShadowIsDisplacedAndBehind()
    {
        FilterBuffer source = Square();
        FilterBuffer shadowed = Apply(source, RasterEffectSpec.DropShadow(1, 4, 4));

        // Four pixels below and right of the square, and one beyond its corner, there is now ink.
        Assert.True(shadowed.AlphaAt(16, 16) > 0.05f);

        // The artwork itself is unchanged where it was, because the shadow goes behind it.
        Assert.Equal(1f, shadowed.AlphaAt(10, 10), 3);
    }

    /// <summary>A shadow's colour is its tint, and when it has none it is the colour of the line it falls from.</summary>
    [Fact]
    public void AShadowTakesItsTintOrTheStrokesColour()
    {
        ColorRgb stroke = new(1, 0, 0);
        FilterBuffer source = Square(colour: stroke);

        // The stroke's colour is passed in, because a graph cannot ask the caller what the line was drawn in -
        // and using the helper's default blue here was why this failed first.
        FilterBuffer tinted = RasterEffectFilters.Apply(
            source, new[] { RasterEffectSpec.DropShadow(1, 4, 4, new ColorRgb(0, 1, 0)) }, stroke);
        (float r, float g, float _, float _) = tinted.Get(16, 16);
        Assert.True(g > r, $"a tinted shadow uses its tint: got ({r}, {g})");

        FilterBuffer untinted = RasterEffectFilters.Apply(
            source, new[] { RasterEffectSpec.DropShadow(1, 4, 4) }, stroke);
        (float r2, float g2, float _, float _) = untinted.Get(16, 16);
        Assert.True(r2 > g2, $"an untinted shadow is the colour of the line it falls from: got ({r2}, {g2})");
    }

    /// <summary>
    /// **An outer glow lights outside the artwork; an inner glow does not.**
    ///
    /// An opaque artwork shows its own colour over an inner glow, which is correct - the glow is *inside*, and the
    /// inside is already painted. So the pixel that tells them apart is just outside the edge.
    /// </summary>
    [Fact]
    public void TheTwoGlowsFallOnOppositeSidesOfTheEdge()
    {
        FilterBuffer source = Square(extent: 5, at: 8);
        ColorRgb gold = new(1, 0.8, 0);

        FilterBuffer outer = Apply(source, RasterEffectSpec.Glow(RasterEffectKind.OuterGlow, 2, gold));
        FilterBuffer inner = Apply(source, RasterEffectSpec.Glow(RasterEffectKind.InnerGlow, 2, gold));

        // Just outside the square: the outer glow has reached it, the inner one has not.
        Assert.True(outer.AlphaAt(7, 10) > 0.01f, $"the outer glow should reach outside it: {outer.AlphaAt(7, 10)}");
        Assert.Equal(0f, inner.AlphaAt(7, 10), 3);

        // And inside, both leave the opaque artwork as it was drawn.
        Assert.Equal(1f, outer.AlphaAt(10, 10), 3);
        Assert.Equal(1f, inner.AlphaAt(10, 10), 3);
    }

    /// <summary>The order is the picture here too: a blur then a shadow is not a shadow then a blur.</summary>
    [Fact]
    public void TheOrderOfEffectsChangesTheResult()
    {
        FilterBuffer source = Square();

        FilterBuffer blurThenShadow = Apply(
            source, RasterEffectSpec.Blur(2), RasterEffectSpec.DropShadow(1, 4, 4));
        FilterBuffer shadowThenBlur = Apply(
            source, RasterEffectSpec.DropShadow(1, 4, 4), RasterEffectSpec.Blur(2));

        Assert.False(blurThenShadow.Matches(shadowThenBlur, tolerance: 0.02f));
    }

    /// <summary>A stroke with no raster effects is left exactly as it was, which is what keeps the ordinary case
    /// unchanged.</summary>
    [Fact]
    public void NoEffectsChangeNothing()
    {
        FilterBuffer source = Square();
        Assert.True(RasterEffectFilters.Apply(source, Array.Empty<RasterEffectSpec>(), ColorRgb.Black).Matches(source));
    }
}
