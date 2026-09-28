using VCCad.Core.Model;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// Reading samples out of an image, at every bit depth PDF allows.
///
/// A PDF packs sub-byte samples: a 1, 2 or 4 bit image holds several components in each
/// byte, most significant first. Indexing those as one byte per component does not merely
/// round the picture, it reads a different pixel — and a 1-bit scan, stamp or logo is
/// common, so those images came out as nothing at all rather than as something slightly
/// wrong.
/// </summary>
public class ImageSampleTests
{
    /// <summary>Packs sub-byte samples the way PDF does: most significant first.</summary>
    private static byte[] Pack(int[] values, int bits)
    {
        var bytes = new byte[(values.Length * bits + 7) / 8];
        for (int i = 0; i < values.Length; i++)
        {
            int bitOffset = i * bits;
            int at = bitOffset >> 3;
            int shift = 8 - bits - (bitOffset & 7);
            bytes[at] |= (byte)(values[i] << shift);
        }

        return bytes;
    }

    [Fact]
    public void OneBitSamplesDecode()
    {
        // 1011 0010 read left to right across a four-pixel wide row, then the next row.
        var image = new ImageItem
        {
            Name = "1-bit",
            PixelWidth = 4,
            PixelHeight = 2,
            BitsPerComponent = 1,
            ColorSpace = ImageColorSpace.Gray,
            Samples = Pack(new[] { 1, 0, 1, 1, 0, 0, 1, 0 }, 1),
        };

        Assert.Equal(1.0, image.SampleAt(0, 0), 6);
        Assert.Equal(0.0, image.SampleAt(1, 0), 6);
        Assert.Equal(1.0, image.SampleAt(2, 0), 6);
        Assert.Equal(1.0, image.SampleAt(3, 0), 6);

        // The second row is 0, 0, 1, 0 — the bit that lands in the middle of the byte.
        Assert.Equal(0.0, image.SampleAt(0, 1), 6);
        Assert.Equal(0.0, image.SampleAt(1, 1), 6);
        Assert.Equal(1.0, image.SampleAt(2, 1), 6);
        Assert.Equal(0.0, image.SampleAt(3, 1), 6);

        // And the colour that follows from it, rather than the transparent gap the
        // renderer used to leave because the depth was not 8.
        Assert.Equal(1.0, image.PixelAt(0, 0).R, 6);
        Assert.Equal(0.0, image.PixelAt(1, 0).R, 6);
    }

    [Fact]
    public void TwoBitSamplesDecode()
    {
        var image = new ImageItem
        {
            Name = "2-bit",
            PixelWidth = 4,
            PixelHeight = 1,
            BitsPerComponent = 2,
            ColorSpace = ImageColorSpace.Gray,
            Samples = Pack(new[] { 0, 1, 2, 3 }, 2),
        };

        Assert.Equal(0.0, image.SampleAt(0, 0), 6);
        Assert.Equal(1.0 / 3, image.SampleAt(1, 0), 6);
        Assert.Equal(2.0 / 3, image.SampleAt(2, 0), 6);
        Assert.Equal(1.0, image.SampleAt(3, 0), 6);
    }

    [Fact]
    public void FourBitSamplesDecodeAcrossTheByteBoundary()
    {
        // 0x0F 0xA5 is 0, 15, 10, 5 - the third pixel starts in the second byte.
        var image = new ImageItem
        {
            Name = "4-bit",
            PixelWidth = 4,
            PixelHeight = 1,
            BitsPerComponent = 4,
            ColorSpace = ImageColorSpace.Gray,
            Samples = new byte[] { 0x0F, 0xA5 },
        };

        Assert.Equal(0, image.RawSampleAt(0, 0, 0));
        Assert.Equal(15, image.RawSampleAt(1, 0, 0));
        Assert.Equal(10, image.RawSampleAt(2, 0, 0));
        Assert.Equal(5, image.RawSampleAt(3, 0, 0));
    }

    [Fact]
    public void SixteenBitSamplesDecodeBigEndian()
    {
        var image = new ImageItem
        {
            Name = "16-bit",
            PixelWidth = 2,
            PixelHeight = 1,
            BitsPerComponent = 16,
            ColorSpace = ImageColorSpace.Gray,
            Samples = new byte[] { 0xFF, 0xFF, 0x00, 0x00 },
        };

        Assert.Equal(1.0, image.SampleAt(0, 0, 0), 6);
        Assert.Equal(0.0, image.SampleAt(1, 0, 0), 6);
    }

    [Fact]
    public void AnIndexedImageUsesTheSampleAsAPaletteIndex()
    {
        // Two bits per pixel, so the sample is an index 0..3, not an intensity.
        var image = new ImageItem
        {
            Name = "indexed",
            PixelWidth = 4,
            PixelHeight = 1,
            BitsPerComponent = 2,
            ColorSpace = ImageColorSpace.Indexed,
            Samples = Pack(new[] { 0, 1, 2, 3 }, 2),
            Palette = new byte[]
            {
                0, 0, 0,
                255, 0, 0,
                0, 255, 0,
                0, 0, 255,
            },
        };

        // Scaling the sample to 0..1 first would have read entry 0, 0, 1, 2 - wrong at
        // every pixel but the first.
        Assert.Equal(0.0, image.PixelAt(0, 0).R, 6);
        Assert.Equal(1.0, image.PixelAt(1, 0).R, 6);
        Assert.Equal(1.0, image.PixelAt(2, 0).G, 6);
        Assert.Equal(1.0, image.PixelAt(3, 0).B, 6);
    }

    [Fact]
    public void CMYKIsStillReadComponentWise()
    {
        var image = new ImageItem
        {
            Name = "cmyk",
            PixelWidth = 1,
            PixelHeight = 1,
            BitsPerComponent = 8,
            ColorSpace = ImageColorSpace.Cmyk,
            Samples = new byte[] { 7, 4, 4, 0 },
        };

        // The file's own values, unchanged: what to do about CMYK is the export's
        // business, not the reader's.
        Assert.Equal(7, image.RawSampleAt(0, 0, 0));
        Assert.Equal(4, image.RawSampleAt(0, 0, 1));
        Assert.Equal(0, image.RawSampleAt(0, 0, 3));
    }

    [Fact]
    public void OutOfRangeReadsAreZeroRatherThanExceptions()
    {
        var image = new ImageItem
        {
            Name = "short",
            PixelWidth = 2,
            PixelHeight = 2,
            BitsPerComponent = 8,
            ColorSpace = ImageColorSpace.Gray,
            Samples = new byte[] { 128 },
        };

        // A truncated stream is a damaged file, not a reason to throw out of a paint.
        Assert.Equal(128, image.RawSampleAt(0, 0, 0));
        Assert.Equal(0, image.RawSampleAt(1, 0, 0));
        Assert.Equal(0, image.RawSampleAt(0, 1, 0));
        Assert.Equal(0, image.RawSampleAt(-1, 0, 0));
        Assert.Equal(0, image.RawSampleAt(0, 0, 9));
    }
}
