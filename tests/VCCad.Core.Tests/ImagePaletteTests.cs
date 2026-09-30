using VCCad.Core.Model;
using VCCad.Core.Serialization;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// An indexed image is only half a colour space: the palette has to be read through the space it
/// indexes into.
///
/// `[/Indexed /DeviceCMYK 251 ...]` holds **four** bytes per entry. Reading those as RGB takes every
/// index three bytes into the wrong place, which paints a photograph in arbitrary saturated colours
/// - the "renders dark green" in issue #35 - and no amount of clamping hides it. The entry size is
/// the whole point of these tests.
/// </summary>
public class ImagePaletteTests
{
    private static ImageItem Indexed(ImageColorSpace paletteBase, byte[] palette, params byte[] samples) => new()
    {
        Name = "palette",
        PixelWidth = samples.Length,
        PixelHeight = 1,
        BitsPerComponent = 8,
        ColorSpace = ImageColorSpace.Indexed,
        Palette = palette,
        PaletteBase = paletteBase,
        Samples = samples,
    };

    [Fact]
    public void ACmykPaletteIsReadFourBytesToAnEntry()
    {
        // Entry 0 is 50% black; entry 1 is pure cyan. Read as three-byte RGB entries the first would
        // be black (the three zero bytes) and the second would be a mix of the wrong fields.
        var image = Indexed(
            ImageColorSpace.Cmyk,
            new byte[]
            {
                0, 0, 0, 128,     // entry 0: C=0 M=0 Y=0 K=128 (50% black)
                255, 0, 0, 0,     // entry 1: C=255 M=0 Y=0 K=0 (cyan)
            },
            0, 1);

        ColorRgb half = image.PixelAt(0, 0);
        ColorRgb cyan = image.PixelAt(1, 0);

        // K=128 is 50.2% ink, so the ink that is NOT laid down - the light - is 49.8%.
        Assert.Equal(1.0 - (128.0 / 255.0), half.R, 3);
        Assert.Equal(half.R, half.G, 3);
        Assert.Equal(half.R, half.B, 3);

        Assert.Equal(0.0, cyan.R, 3);
        Assert.Equal(1.0, cyan.G, 3);
        Assert.Equal(1.0, cyan.B, 3);
    }

    [Fact]
    public void AnRgbPaletteIsUnaffected()
    {
        var image = Indexed(
            ImageColorSpace.Rgb,
            new byte[] { 255, 0, 0, 0, 0, 255 },
            0, 1);

        Assert.Equal(1.0, image.PixelAt(0, 0).R, 3);
        Assert.Equal(0.0, image.PixelAt(0, 0).B, 3);
        Assert.Equal(1.0, image.PixelAt(1, 0).B, 3);
    }

    [Fact]
    public void AGreyPaletteTakesOneByteToAnEntry()
    {
        var image = Indexed(ImageColorSpace.Gray, new byte[] { 64, 255 }, 0, 1);

        Assert.Equal(64.0 / 255.0, image.PixelAt(0, 0).R, 3);
        Assert.Equal(1.0, image.PixelAt(1, 0).R, 3);
    }

    [Fact]
    public void AnIndexPastTheEndOfThePaletteIsBlackRatherThanSamplesFromElsewhere()
    {
        var image = Indexed(ImageColorSpace.Cmyk, new byte[] { 0, 0, 0, 128 }, 0, 7);

        Assert.Equal(1.0 - (128.0 / 255.0), image.PixelAt(0, 0).R, 3);
        Assert.Equal(ColorRgb.Black, image.PixelAt(1, 0));
    }

    [Fact]
    public void EntrySizeFollowsTheBaseSpace()
    {
        Assert.Equal(1, Indexed(ImageColorSpace.Gray, Array.Empty<byte>(), 0).PaletteEntryBytes);
        Assert.Equal(3, Indexed(ImageColorSpace.Rgb, Array.Empty<byte>(), 0).PaletteEntryBytes);
        Assert.Equal(4, Indexed(ImageColorSpace.Cmyk, Array.Empty<byte>(), 0).PaletteEntryBytes);
    }

    /// <summary>
    /// Through the lossless sidecar, because a palette whose base is lost is a palette read as RGB
    /// again on the next load - the colours would come back wrong after any save.
    /// </summary>
    [Fact]
    public void ThePaletteBaseSurvivesASerializerRoundTrip()
    {
        var image = Indexed(
            ImageColorSpace.Cmyk,
            new byte[] { 0, 0, 0, 128, 255, 0, 0, 0 },
            0, 1);

        CadDocument document = CadDocument.CreateDefault("palette");
        document.Artboards[0].Layers[0].AddItem(image);

        byte[] json = VccadDocumentSerializer.SerializeToBytes(document);
        CadDocument again = VccadDocumentSerializer.Deserialize(json);

        ImageItem reloaded = again.Artboards[0].Layers[0].Children.OfType<ImageItem>().Single();

        Assert.Equal(ImageColorSpace.Cmyk, reloaded.PaletteBase);
        Assert.Equal(4, reloaded.PaletteEntryBytes);
        Assert.Equal(image.PixelAt(1, 0).G, reloaded.PixelAt(1, 0).G, 3);
    }
}
