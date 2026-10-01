using VCCad.Core.Model;
using VCCad.Core.Raster;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// The **region** a raster effect's filter graph asks to be rendered over.
///
/// A filter graph carries the rectangle its result needs, and the renderer allocates its offscreen bitmap from it -
/// so a graph that asks for a single unit gets a bitmap a few pixels wide and the effect is applied to almost none
/// of the artwork. That is what these pin: the region covers the object **plus everything the effect can spread**.
/// </summary>
public class RasterEffectRegionTests
{
    private static FilterSpec Filter(RasterEffectSpec effect, Rect2D bounds)
        => RasterEffectFilters.ToFilter(effect, ColorRgb.Black, bounds)!;

    [Fact]
    public void ABlursRegionCoversTheObjectAndItsSpread()
    {
        var bounds = new Rect2D(100, 50, 200, 100);
        FilterSpec filter = Filter(RasterEffectSpec.Blur(8), bounds);

        Assert.False(filter.ObjectBoundingBox);
        Assert.Equal(100 - 8, filter.X, 6);
        Assert.Equal(50 - 8, filter.Y, 6);
        Assert.Equal(200 + 16, filter.Width, 6);
        Assert.Equal(100 + 16, filter.Height, 6);
    }

    /// <summary>A shadow's region has to cover its **displacement** as well as its blur, or it is cut off.</summary>
    [Fact]
    public void AShadowsRegionCoversItsDisplacementToo()
    {
        var bounds = new Rect2D(0, 0, 40, 40);
        FilterSpec filter = Filter(RasterEffectSpec.DropShadow(2, 10, -6), bounds);

        // The margin is the blur plus the displacement, so it reaches at least as far as the displacement alone
        // needs - which is the property that matters, since a region that stops short cuts the shadow off.
        Assert.True(filter.X <= -10, $"the region must reach the displacement: X is {filter.X}");
        Assert.True(filter.Y <= -10, $"and it covers the larger of the two offsets: Y is {filter.Y}");
        Assert.True(filter.Width >= 40 + 20, $"and cover the far side: width is {filter.Width}");
    }

    /// <summary>And the region the renderer actually computes from it contains the object, with room to spread.</summary>
    [Fact]
    public void TheRegionTheRendererComputesContainsTheObjectAndItsMargin()
    {
        var bounds = new Rect2D(100, 50, 200, 100);
        FilterSpec filter = Filter(RasterEffectSpec.Blur(8), bounds);

        (int x, int y, int width, int height) = FilterEngine.RegionPixels(filter, bounds, scale: 1.0);

        Assert.True(width >= 216, $"a blur of 8 needs room around a 200-wide object: got {width}");
        Assert.True(height >= 116, $"and around a 100-tall one: got {height}");
        Assert.True(x <= 92 && y <= 42, $"and it must start outside the object: got ({x}, {y})");
    }

    /// <summary>A glow spreads by its radius, so its region says so.</summary>
    [Fact]
    public void AGlowsRegionCoversItsRadius()
    {
        var bounds = new Rect2D(0, 0, 30, 30);
        FilterSpec filter = Filter(RasterEffectSpec.Glow(RasterEffectKind.OuterGlow, 5, ColorRgb.Black), bounds);

        Assert.Equal(0, filter.Width - (30 + 10), 6);
    }
}
