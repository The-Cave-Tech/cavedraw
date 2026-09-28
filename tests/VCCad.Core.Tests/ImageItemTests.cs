using VCCad.Core.Model;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// Reading an image's samples in the wrong colour space is not a subtle error: CMYK
/// samples interpreted as RGB turn a pale magenta tint black. The real document these
/// were written against has a 274x484 CMYK image whose first pixel is 07 04 04 00, and
/// it rendered black until the space was honoured.
/// </summary>
public class ImageItemTests
{
    private static ImageItem Cmyk(byte c, byte m, byte y, byte k)
    {
        var image = new ImageItem
        {
            PixelWidth = 2,
            PixelHeight = 1,
            BitsPerComponent = 8,
            ColorSpace = ImageColorSpace.Cmyk,
            Samples = new[] { c, m, y, k, c, m, y, k },
        };

        return image;
    }

    [Fact]
    public void ACmykTintIsPaleNotBlack()
    {
        // 07 04 04 00 is the first pixel of the LILLIE pattern's embedded image.
        ColorRgb colour = Cmyk(0x07, 0x04, 0x04, 0x00).PixelAt(0, 0);

        Assert.True(colour.R > 0.95, $"red channel was {colour.R}, expected near white");
        Assert.True(colour.G > 0.97, $"green channel was {colour.G}");
        Assert.True(colour.B > 0.97, $"blue channel was {colour.B}");
    }

    [Fact]
    public void PureBlackCmykIsBlack()
    {
        ColorRgb colour = Cmyk(0, 0, 0, 255).PixelAt(0, 0);
        Assert.Equal(0, colour.R, 3);
        Assert.Equal(0, colour.G, 3);
        Assert.Equal(0, colour.B, 3);
    }

    [Fact]
    public void PureCyanCmykHasNoRed()
    {
        ColorRgb colour = Cmyk(255, 0, 0, 0).PixelAt(0, 0);
        Assert.Equal(0, colour.R, 3);
        Assert.Equal(1, colour.G, 3);
        Assert.Equal(1, colour.B, 3);
    }

    [Fact]
    public void GraySamplesAreNeutral()
    {
        var image = new ImageItem
        {
            PixelWidth = 1,
            PixelHeight = 1,
            BitsPerComponent = 8,
            ColorSpace = ImageColorSpace.Gray,
            Samples = new byte[] { 128 },
        };

        ColorRgb colour = image.PixelAt(0, 0);
        Assert.Equal(colour.R, colour.G, 6);
        Assert.Equal(colour.G, colour.B, 6);
        Assert.Equal(128.0 / 255, colour.R, 6);
    }

    [Fact]
    public void AnIndexedImageUsesItsPalette()
    {
        var image = new ImageItem
        {
            PixelWidth = 1,
            PixelHeight = 1,
            BitsPerComponent = 8,
            ColorSpace = ImageColorSpace.Indexed,
            Samples = new byte[] { 1 },
            Palette = new byte[] { 0, 0, 0, 255, 128, 64 },
        };

        ColorRgb colour = image.PixelAt(0, 0);
        Assert.Equal(1.0, colour.R, 3);
        Assert.Equal(128.0 / 255, colour.G, 3);
        Assert.Equal(64.0 / 255, colour.B, 3);
    }

    [Fact]
    public void AMaskSuppliesCoverageAndItsAbsenceMeansOpaque()
    {
        ImageItem opaque = Cmyk(0, 0, 0, 0);
        Assert.False(opaque.HasMask);
        Assert.Equal(1.0, opaque.CoverageAt(0, 0), 6);

        opaque.Mask = new byte[] { 0, 51, 255, 255 };
        Assert.True(opaque.HasMask);
        Assert.Equal(0.0, opaque.CoverageAt(0, 0), 6);
        Assert.Equal(51.0 / 255, opaque.CoverageAt(1, 0), 6);
    }

    [Fact]
    public void PixelsOutsideTheImageAreBlack()
    {
        Assert.Equal(ColorRgb.Black, Cmyk(255, 255, 255, 0).PixelAt(-1, 0));
        Assert.Equal(ColorRgb.Black, Cmyk(255, 255, 255, 0).PixelAt(0, 99));
    }

    [Fact]
    public void CloningAnImageCopiesItsSamplesRatherThanSharingThem()
    {
        ImageItem original = Cmyk(1, 2, 3, 4);
        var copy = (ImageItem)original.Clone();

        Assert.Equal(original.Samples, copy.Samples);
        Assert.NotSame(original.Samples, copy.Samples);

        copy.Samples[0] = 200;
        Assert.NotEqual(copy.Samples[0], original.Samples[0]);
    }

    [Fact]
    public void RowBytesMatchesThePdfDefinition()
    {
        var image = new ImageItem
        {
            PixelWidth = 274,
            PixelHeight = 484,
            BitsPerComponent = 8,
            ColorSpace = ImageColorSpace.Cmyk,
        };

        // 274 pixels x 4 components x 8 bits, rounded up to a byte boundary.
        Assert.Equal(274 * 4, image.RowBytes);
    }
}
