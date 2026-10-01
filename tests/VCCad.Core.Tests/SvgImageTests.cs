using System.IO.Compression;
using System.Text;
using VCCad.Core.Model;
using VCCad.Core.Svg;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// SVG `image` import, and the PNG reader behind it.
///
/// **Asserted on the model, and on the bytes it holds.** An importer that drew nothing and an importer that drew
/// the right picture both "read" the element, so what is checked here is the <see cref="ImageItem"/>: its pixel
/// grid, its samples, its colour at a pixel, and where on the artboard it landed. A reference the document cannot
/// resolve is checked to be **reported** rather than dropped, because a picture that quietly went missing is the
/// worst kind of import bug - the drawing still looks deliberate.
/// </summary>
public class SvgImageTests
{
    private static readonly string Header =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" " +
        "width=\"200\" height=\"100\" viewBox=\"0 0 200 100\">";

    private static SvgImportResult Read(string body, string? baseDirectory = null)
        => SvgReader.Read(Header + body + "</svg>", baseDirectory);

    private static ImageItem FirstImage(SvgImportResult result)
        => result.Document.AllItems().OfType<ImageItem>().First();

    private static string DataUri(byte[] bytes)
        => "data:image/png;base64," + Convert.ToBase64String(bytes);

    // ---------------------------------------------------------------- a data URI

    /// <summary>
    /// **A `data:` image arrives with the picture, not just with a reference.** The geometry comes from the
    /// element, the samples from the PNG, and the colour at a pixel says the bytes were actually decoded rather
    /// than carried along hopefully.
    /// </summary>
    [Fact]
    public void ADataUriImageArrivesWithItsGeometryAndPayload()
    {
        byte[] png = RedAndBluePng();
        SvgImportResult result = Read(
            $"<image x=\"10\" y=\"20\" width=\"40\" height=\"40\" xlink:href=\"{DataUri(png)}\"/>");

        ImageItem image = FirstImage(result);
        Assert.Equal(1, result.ByElement["image"]);

        // The pixel grid is the file's, and the samples are decoded RGB with the alpha kept beside them.
        Assert.Equal(2, image.PixelWidth);
        Assert.Equal(2, image.PixelHeight);
        Assert.Equal(8, image.BitsPerComponent);
        Assert.Equal(ImageColorSpace.Rgb, image.ColorSpace);
        Assert.Equal(12, image.Samples.Length);
        Assert.Equal(4, image.Mask.Length);

        // And the picture is the picture: two red pixels and two that are blue but half transparent.
        ColorRgb first = image.PixelAt(0, 0);
        Assert.Equal(1.0, first.R, 6);
        Assert.Equal(0.0, first.G, 6);
        Assert.Equal(1.0, image.CoverageAt(0, 0), 6);

        ColorRgb last = image.PixelAt(1, 1);
        Assert.Equal(1.0, last.B, 6);
        Assert.Equal(0.5, image.CoverageAt(1, 1), 2);
    }

    /// <summary>The element's box is the placement, and a square picture in a square box fills it exactly.</summary>
    [Fact]
    public void AnImagesGeometryIsItsElementBox()
    {
        SvgImportResult result = Read(
            $"<image x=\"10\" y=\"20\" width=\"40\" height=\"40\" href=\"{DataUri(RedAndBluePng())}\"/>");

        Assert.Equal(new Rect2D(10, 20, 40, 40), FirstImage(result).Placement);
    }

    /// <summary>
    /// **`preserveAspectRatio` decides what the picture fills.** The default fits the picture inside the box and
    /// centres it, which is what a viewer draws - stretching it to the box would be a different picture, and one
    /// that is wrong in a way that looks like the artwork.
    /// </summary>
    [Fact]
    public void APreservedAspectRatioDecidesWhatTheImageFills()
    {
        // A 4x2 picture in a 40x40 box: fitted, it is 40x20 with ten units of space above and below.
        SvgImportResult fitted = Read(
            $"<image x=\"10\" y=\"20\" width=\"40\" height=\"40\" href=\"{DataUri(StripPng())}\"/>");

        Assert.Equal(new Rect2D(10, 30, 40, 20), FirstImage(fitted).Placement);

        // `none` is the case that stretches, and only when the file asks for it.
        SvgImportResult stretched = Read(
            $"<image x=\"10\" y=\"20\" width=\"40\" height=\"40\" preserveAspectRatio=\"none\" " +
            $"href=\"{DataUri(StripPng())}\"/>");

        Assert.Equal(new Rect2D(10, 20, 40, 40), FirstImage(stretched).Placement);
    }

    /// <summary>An image is a coordinate like any other, so its percentages resolve against the viewport.</summary>
    [Fact]
    public void ImagePercentagesResolveAgainstTheViewport()
    {
        SvgImportResult result = Read(
            $"<image x=\"10%\" y=\"20%\" width=\"50%\" height=\"40%\" href=\"{DataUri(StripPng())}\"/>");

        // 10% of 200 by 20% of 100 gives a 100x40 box, and a 4x2 picture is fitted into it at 80x40, centred.
        Rect2D placement = FirstImage(result).Placement;
        Assert.Equal(30.0, placement.X, 6);
        Assert.Equal(20.0, placement.Y, 6);
        Assert.Equal(80.0, placement.Width, 6);
        Assert.Equal(40.0, placement.Height, 6);
    }

    /// <summary>An image inside a `use` is placed where the instance puts it, like every other child.</summary>
    [Fact]
    public void AnImageIsPlacedByTheGroupItSitsIn()
    {
        SvgImportResult result = Read(
            "<g transform=\"translate(100,10)\">" +
            $"<image x=\"0\" y=\"0\" width=\"20\" height=\"20\" href=\"{DataUri(RedAndBluePng())}\"/></g>");

        ArtGroup group = result.Document.Artboards[0].Layers[0].Children.OfType<ArtGroup>().Single();
        Assert.Equal(new Rect2D(0, 0, 20, 20), FirstImage(result).Placement);
        Assert.Equal(new Point2D(100, 10), group.Transform.Transform(new Point2D(0, 0)));
    }

    // ---------------------------------------------------------------- references that cannot be resolved

    /// <summary>
    /// **A reference that cannot be resolved is reported, and nothing is invented in its place.** A file beside
    /// the document that is not there is the common case, and it is named in the import's list of things the
    /// document asked for and did not get - there is no object, because the model has no picture to hold.
    /// </summary>
    [Fact]
    public void AnImageWhoseReferenceCannotBeResolvedIsReported()
    {
        SvgImportResult result = Read("<image x=\"0\" y=\"0\" width=\"10\" height=\"10\" href=\"missing.png\"/>");

        Assert.Empty(result.Document.AllItems().OfType<ImageItem>());
        Assert.False(result.ByElement.ContainsKey("image"), "no object is invented for a reference that failed");
        Assert.Contains(result.Missing, m => m.Contains("missing.png", StringComparison.Ordinal));
    }

    /// <summary>
    /// **A network reference is reported, never fetched.** Importing a document must not turn into a request for
    /// somebody else's server: the result would depend on the weather, and the file's own content is the only thing
    /// an importer is entitled to read.
    /// </summary>
    [Fact]
    public void ARemoteReferenceIsNeverFetched()
    {
        SvgImportResult result = Read(
            "<image x=\"0\" y=\"0\" width=\"10\" height=\"10\" href=\"https://example.invalid/logo.png\"/>");

        Assert.Empty(result.Document.AllItems().OfType<ImageItem>());
        Assert.Contains(result.Missing, m => m.Contains("never fetched", StringComparison.Ordinal));
    }

    /// <summary>A file beside the document is read, which is what makes a real Inkscape file portable.</summary>
    [Fact]
    public void AnExternalFileBesideTheDocumentIsRead()
    {
        string directory = Path.Combine(Path.GetTempPath(), "vccad-svg-image-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            File.WriteAllBytes(Path.Combine(directory, "dot.png"), RedAndBluePng());

            SvgImportResult result = Read(
                "<image x=\"1\" y=\"2\" width=\"2\" height=\"2\" href=\"dot.png\"/>", directory);

            ImageItem image = FirstImage(result);
            Assert.Equal(2, image.PixelWidth);
            Assert.Equal(new Rect2D(1, 2, 2, 2), image.Placement);
            Assert.Empty(result.Missing);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>An element with no area draws nothing in SVG, and saying so beats drawing a guess.</summary>
    [Fact]
    public void AnImageWithNoAreaIsReported()
    {
        SvgImportResult result = Read(
            $"<image x=\"0\" y=\"0\" width=\"0\" height=\"10\" href=\"{DataUri(RedAndBluePng())}\"/>");

        Assert.Empty(result.Document.AllItems().OfType<ImageItem>());
        Assert.Contains(result.Warnings, w => w.Contains("no area", StringComparison.Ordinal));
    }

    /// <summary>
    /// **A turn cannot be expressed by a rectangle, so it is reported.** The model places an image with a box; a
    /// silently unrotated picture is a drawing the file did not ask for.
    /// </summary>
    [Fact]
    public void ARotatedImageIsReportedRatherThanDrawnStraight()
    {
        SvgImportResult result = Read(
            $"<image x=\"0\" y=\"0\" width=\"10\" height=\"10\" transform=\"rotate(30)\" " +
            $"href=\"{DataUri(RedAndBluePng())}\"/>");

        Assert.Single(result.Document.AllItems().OfType<ImageItem>());
        Assert.Contains(result.Warnings, w => w.Contains("turned or skewed", StringComparison.Ordinal));
    }

    /// <summary>A flip survives as state - the samples are never resampled - and it is not reported as a loss.</summary>
    [Fact]
    public void AMirroredImageKeepsItsSamplesAndSaysSo()
    {
        SvgImportResult result = Read(
            $"<image x=\"10\" y=\"0\" width=\"10\" height=\"10\" transform=\"scale(-1,1)\" " +
            $"href=\"{DataUri(RedAndBluePng())}\"/>");

        ImageItem image = FirstImage(result);
        Assert.True(image.MirrorX);
        Assert.False(image.MirrorY);
        Assert.Equal(12, image.Samples.Length);
    }

    /// <summary>An element with no picture at all is reported rather than skipped in silence.</summary>
    [Fact]
    public void AnImageWithNoReferenceIsReported()
    {
        SvgImportResult result = Read("<image x=\"0\" y=\"0\" width=\"10\" height=\"10\"/>");

        Assert.Contains(result.Missing, m => m.Contains("no href", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- the raster formats

    /// <summary>
    /// **A JPEG is kept compressed.** This reader cannot open one, and re-encoding the samples would be a lossy
    /// rewrite of somebody else's picture - so the bytes travel with the compressor that produced them, and the
    /// frame header supplies the size the model needs to place it.
    /// </summary>
    [Fact]
    public void AJPEGKeepsItsCompressedBytes()
    {
        byte[] jpeg = TinyJpeg();
        SvgImportResult result = Read(
            "<image x=\"0\" y=\"0\" width=\"8\" height=\"8\" " +
            $"href=\"data:image/jpeg;base64,{Convert.ToBase64String(jpeg)}\"/>");

        ImageItem image = FirstImage(result);
        Assert.Equal("DCTDecode", image.Filter);
        Assert.Equal(4, image.PixelWidth);
        Assert.Equal(2, image.PixelHeight);
        Assert.Equal(ImageColorSpace.Rgb, image.ColorSpace);
        Assert.Equal(jpeg.Length, image.Samples.Length);
        Assert.Equal(jpeg, image.Samples);
    }

    /// <summary>A format this reader does not open is reported, not decoded into noise.</summary>
    [Fact]
    public void AnUnsupportedFormatIsReported()
    {
        SvgImportResult result = Read(
            "<image x=\"0\" y=\"0\" width=\"8\" height=\"8\" " +
            "href=\"data:image/gif;base64,R0lGODlhAQABAAAAACw=\"/>");

        Assert.Empty(result.Document.AllItems().OfType<ImageItem>());
        Assert.Contains(result.Missing, m => m.Contains("does not open", StringComparison.Ordinal));
    }

    /// <summary>An interlaced PNG is reported rather than read as though its rows were in order.</summary>
    [Fact]
    public void AnInterlacedPngIsReported()
    {
        byte[] interlaced = EncodePng(2, 2, colorType: 2, bitDepth: 8, RgbPixels(), interlace: 1);
        SvgImportResult result = Read(
            $"<image x=\"0\" y=\"0\" width=\"8\" height=\"8\" href=\"{DataUri(interlaced)}\"/>");

        Assert.Empty(result.Document.AllItems().OfType<ImageItem>());
        Assert.Contains(result.Missing, m => m.Contains("interlaced", StringComparison.Ordinal));
    }

    /// <summary>A grey PNG keeps one component per pixel, which is the space the file used.</summary>
    [Fact]
    public void AGreyPngStaysGrey()
    {
        byte[] grey = EncodePng(2, 2, colorType: 0, bitDepth: 8, new byte[] { 0, 85, 170, 255 });
        SvgImportResult result = Read(
            $"<image x=\"0\" y=\"0\" width=\"2\" height=\"2\" href=\"{DataUri(grey)}\"/>");

        ImageItem image = FirstImage(result);
        Assert.Equal(ImageColorSpace.Gray, image.ColorSpace);
        Assert.Equal(4, image.Samples.Length);
        Assert.Equal(0.0, image.PixelAt(0, 0).R, 6);
        Assert.Equal(1.0, image.PixelAt(1, 1).R, 6);
    }

    /// <summary>An indexed PNG keeps its palette, and the index is a palette lookup rather than an intensity.</summary>
    [Fact]
    public void AnIndexedPngKeepsItsPalette()
    {
        byte[] palette = { 255, 0, 0, 0, 0, 255 };
        byte[] indexed = EncodePng(2, 1, colorType: 3, bitDepth: 8, new byte[] { 0, 1 }, palette);

        SvgImportResult result = Read(
            $"<image x=\"0\" y=\"0\" width=\"2\" height=\"1\" href=\"{DataUri(indexed)}\"/>");

        ImageItem image = FirstImage(result);
        Assert.Equal(ImageColorSpace.Indexed, image.ColorSpace);
        Assert.Equal(palette, image.Palette);
        Assert.Equal(1.0, image.PixelAt(0, 0).R, 6);
        Assert.Equal(1.0, image.PixelAt(1, 0).B, 6);
    }

    /// <summary>Bytes that are not an image at all are reported.</summary>
    [Fact]
    public void BytesThatAreNotAnImageAreReported()
    {
        SvgImportResult result = Read(
            $"<image x=\"0\" y=\"0\" width=\"2\" height=\"2\" href=\"{DataUri(new byte[] { 1, 2, 3, 4 })}\"/>");

        Assert.Empty(result.Document.AllItems().OfType<ImageItem>());
        Assert.NotEmpty(result.Missing);
    }

    // ---------------------------------------------------------------- the pictures these tests use

    /// <summary>Two red pixels and two half-transparent blue ones, as a real RGBA PNG.</summary>
    private static byte[] RedAndBluePng()
    {
        var pixels = new byte[]
        {
            255, 0, 0, 255, 255, 0, 0, 255,
            255, 0, 0, 255, 0, 0, 255, 128,
        };

        return EncodePng(2, 2, colorType: 6, bitDepth: 8, pixels);
    }

    /// <summary>A 4x2 picture, which is the shape that makes `preserveAspectRatio` visible.</summary>
    private static byte[] StripPng()
    {
        var pixels = new byte[4 * 2 * 3];
        for (int i = 0; i < pixels.Length; i += 3)
        {
            pixels[i] = 255;
        }

        return EncodePng(4, 2, colorType: 2, bitDepth: 8, pixels);
    }

    private static byte[] RgbPixels() => new byte[2 * 2 * 3];

    /// <summary>
    /// A PNG, encoded here rather than pasted in as base64.
    ///
    /// A decoder test whose fixture is a blob nobody can read tests the blob as much as the decoder; building the
    /// file makes the scanlines, the palette and the colour type visible in the test that needs them.
    /// </summary>
    private static byte[] EncodePng(
        int width, int height, int colorType, int bitDepth, byte[] pixels, byte[]? palette = null, int interlace = 0)
    {
        var output = new MemoryStream();
        output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });

        var header = new byte[13];
        WriteBigEndian(header, 0, width);
        WriteBigEndian(header, 4, height);
        header[8] = (byte)bitDepth;
        header[9] = (byte)colorType;
        header[12] = (byte)interlace;
        Chunk(output, "IHDR", header);

        if (palette is not null)
        {
            Chunk(output, "PLTE", palette);
        }

        int rowBytes = pixels.Length / height;
        var raw = new byte[(rowBytes + 1) * height];
        for (int y = 0; y < height; y++)
        {
            // Filter 0 - "none" - so the decoder's unfiltering is exercised on the honest path.
            raw[y * (rowBytes + 1)] = 0;
            Buffer.BlockCopy(pixels, y * rowBytes, raw, (y * (rowBytes + 1)) + 1, rowBytes);
        }

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        Chunk(output, "IDAT", compressed.ToArray());
        Chunk(output, "IEND", Array.Empty<byte>());
        return output.ToArray();
    }

    private static void Chunk(Stream output, string kind, byte[] data)
    {
        var header = new byte[4];
        WriteBigEndian(header, 0, data.Length);
        output.Write(header);

        byte[] name = Encoding.ASCII.GetBytes(kind);
        output.Write(name);
        output.Write(data);

        var crc = new byte[4];
        WriteBigEndian(crc, 0, (int)Crc32(name, data));
        output.Write(crc);
    }

    private static void WriteBigEndian(byte[] target, int at, int value)
    {
        target[at] = (byte)(value >> 24);
        target[at + 1] = (byte)(value >> 16);
        target[at + 2] = (byte)(value >> 8);
        target[at + 3] = (byte)value;
    }

    private static uint Crc32(byte[] name, byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in name.Concat(data))
        {
            crc ^= b;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            }
        }

        return crc ^ 0xFFFFFFFF;
    }

    /// <summary>A JPEG header with a frame and no scan, which is as much as placing it needs.</summary>
    private static byte[] TinyJpeg()
    {
        var bytes = new List<byte> { 0xFF, 0xD8 };

        bytes.AddRange(new byte[] { 0xFF, 0xE0, 0x00, 0x10 });
        bytes.AddRange(Encoding.ASCII.GetBytes("JFIF\0"));
        bytes.AddRange(new byte[] { 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00 });

        // SOF0: eight bits, two rows, four columns, three components.
        bytes.AddRange(new byte[] { 0xFF, 0xC0, 0x00, 0x11, 0x08, 0x00, 0x02, 0x00, 0x04, 0x03 });
        bytes.AddRange(new byte[] { 0x01, 0x11, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01 });

        bytes.AddRange(new byte[] { 0xFF, 0xD9 });
        return bytes.ToArray();
    }
}
