using VCCad.Core.Model;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// <c>/Decode</c>, and the one reading of it that has to be refused.
///
/// The array maps a stored sample onto the 0..1 range it stands for, which is how an image is
/// inverted. But writers exist - the Prianka skirt pattern is full of them - that emit
/// <c>/Decode [0 255]</c> over 8-bit samples, which is not a remap at all: it is the writer saying
/// "these bytes are the raw values". Reading it as the specification defines maps every non-zero
/// sample to the top of the range, and the picture comes out **white**. That is issue #35's second
/// fault, and it is a decision rather than a patch: the two readings differ, and one of them is
/// visibly wrong on real files.
/// </summary>
public class ImageDecodeTests
{
    private static ImageItem Grey(byte sample, double[]? decode) => new()
    {
        Name = "grey",
        PixelWidth = 1,
        PixelHeight = 1,
        BitsPerComponent = 8,
        ColorSpace = ImageColorSpace.Gray,
        Samples = new[] { sample },
        Decode = decode,
    };

    /// <summary>The fault: 128 of 255 is mid grey, and it must stay mid grey.</summary>
    [Fact]
    public void ADecodeSpanningTheSampleRangeIsTheIdentity()
    {
        ColorRgb colour = Grey(128, new[] { 0.0, 255.0 }).PixelAt(0, 0);

        Assert.Equal(128.0 / 255.0, colour.R, 3);
        Assert.NotEqual(1.0, colour.R);
    }

    /// <summary>
    /// And it stays the identity at both ends, so a black image does not become a grey one.
    /// </summary>
    [Theory]
    [InlineData(0, 0.0)]
    [InlineData(255, 1.0)]
    public void TheIdentityHoldsAtBothEnds(byte sample, double expected)
    {
        Assert.Equal(expected, Grey(sample, new[] { 0.0, 255.0 }).PixelAt(0, 0).R, 3);
    }

    /// <summary>
    /// The ordinary decode still decodes: [0 1] over 8-bit samples is normalisation, and a half
    /// range is a real remap. Only the exact sample-range case is taken as an identity.
    /// </summary>
    [Fact]
    public void AnOrdinaryDecodeStillApplies()
    {
        // Half the range: 128 of 255 becomes half of 0.5, and the top half is clipped to 1 by the
        // colour's own clamp.
        Assert.Equal(0.5 * (128.0 / 255.0), Grey(128, new[] { 0.0, 0.5 }).PixelAt(0, 0).R, 3);

        // And normalisation: the value is unchanged, because [0 1] IS the identity on a normalised
        // sample - which is the point, and why the rule has to be about the sample range and not
        // about the array being "wrong".
        Assert.Equal(128.0 / 255.0, Grey(128, new[] { 0.0, 1.0 }).PixelAt(0, 0).R, 3);
    }

    /// <summary>Inversion is a real decode and must survive the rule.</summary>
    [Fact]
    public void AnInvertingDecodeStillInverts()
    {
        Assert.Equal(1.0, Grey(0, new[] { 1.0, 0.0 }).PixelAt(0, 0).R, 3);
        Assert.Equal(0.0, Grey(255, new[] { 1.0, 0.0 }).PixelAt(0, 0).R, 3);
    }

    /// <summary>
    /// A 1-bit image whose decode is [0 1] is the identity too - the range it spans is the sample
    /// range - and must not be mistaken for a remap of a larger one.
    /// </summary>
    [Fact]
    public void ABinaryImageWithANormalisedDecodeIsUnchanged()
    {
        var image = new ImageItem
        {
            Name = "bit",
            PixelWidth = 1,
            PixelHeight = 1,
            BitsPerComponent = 1,
            ColorSpace = ImageColorSpace.Gray,
            Samples = new byte[] { 0b1000_0000 },
            Decode = new[] { 0.0, 1.0 },
        };

        Assert.Equal(1.0, image.PixelAt(0, 0).R, 3);
    }

    /// <summary>A four-bit image declaring its own range is the identity as well.</summary>
    [Fact]
    public void AFourBitImageDeclaringItsOwnRangeIsUnchanged()
    {
        var image = new ImageItem
        {
            Name = "nibble",
            PixelWidth = 1,
            PixelHeight = 1,
            BitsPerComponent = 4,
            ColorSpace = ImageColorSpace.Gray,
            Samples = new byte[] { 0x80 },
            Decode = new[] { 0.0, 15.0 },
        };

        // The first pixel of a 4-bit image is the high nibble, so 0x80 is the value 8 of 15.
        Assert.Equal(8.0 / 15.0, image.PixelAt(0, 0).R, 3);
    }
}
