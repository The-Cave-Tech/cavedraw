using System.Text;
using VCCad.Core.Model;
using VCCad.Core.Svg;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// SVG `image` **export**, which is the half of #132 that was still blank.
///
/// The reader has opened a raster since `SvgImageTests`; the writer wrote none, so a document containing one exported
/// to an SVG without it and the picture went out of the file. These tests are written as a **round trip on the
/// model**: the bytes that go out are read back, and the <see cref="ImageItem"/> that comes back must hold the same
/// pixel grid, the same samples, the same coverage and the same placement - not merely an `<image>` element.
///
/// **What cannot be stated exactly is named rather than converted.** SVG states a picture as an *encoded* resource
/// and the model holds *decoded samples*, so the writer encodes: PNG for anything the model holds as samples, and
/// the file's own bytes for a raster that is still a JPEG. A colour space PNG has no type for - CMYK - and the
/// facets of the model no image format states - a decode array, a colour key, a mask that is still compressed - are
/// left out of the file and put on <see cref="SvgWriteResult.Missing"/>, which is the same surface the text
/// half of the issue uses. Dropping the whole raster and naming it is the behaviour these tests replace; naming a
/// facet a written raster could not carry is the behaviour they keep.
/// </summary>
public class SvgImageWriteTests
{
    // ---------------------------------------------------------------- the acceptance

    /// <summary>
    /// **A raster reaches the file, and the pixels come back.** A document holding an RGB raster with a soft mask
    /// exports an `<image>` with an href and the placement rectangle, and re-importing that file yields an image
    /// with the same placement, the same grid, and byte-for-byte the same samples and mask.
    ///
    /// **Before:** no `<image>` element was written at all. The raster was named on the export report and the file
    /// held nothing where the picture was.
    /// </summary>
    [Fact]
    public void ARasterIsWrittenAtItsPlacementAndReadBackWithTheSamePixels()
    {
        var image = new ImageItem
        {
            Name = "photo",
            PixelWidth = 2,
            PixelHeight = 2,
            BitsPerComponent = 8,
            ColorSpace = ImageColorSpace.Rgb,
            Samples = new byte[] { 255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 0 },
            Mask = new byte[] { 255, 128, 0, 255 },
            Placement = new Rect2D(10, 20, 40, 30),
        };

        (ImageItem returned, SvgWriteResult result, string svg) = RoundTrip(image);

        // The file states the picture: an image element, its placement, and a data URI carrying the bytes.
        Assert.Equal(1, result.ByElement["image"]);
        Assert.Contains("data:image/png;base64,", svg, StringComparison.Ordinal);

        // **`preserveAspectRatio="none"` is the placement.** Without it SVG fits the picture inside the box and
        // centres it, so a 2x2 picture in a 40x30 box would come back 30x30 at an offset - a different rectangle
        // the document never drew.
        Assert.Contains("preserveAspectRatio=\"none\"", svg, StringComparison.Ordinal);
        Assert.Contains("x=\"10\"", svg, StringComparison.Ordinal);
        Assert.Contains("y=\"20\"", svg, StringComparison.Ordinal);
        Assert.Contains("width=\"40\"", svg, StringComparison.Ordinal);
        Assert.Contains("height=\"30\"", svg, StringComparison.Ordinal);

        // And the model that comes back is the model that went in.
        Assert.Equal(new Rect2D(10, 20, 40, 30), returned.Placement);
        Assert.Equal(2, returned.PixelWidth);
        Assert.Equal(2, returned.PixelHeight);
        Assert.Equal(8, returned.BitsPerComponent);
        Assert.Equal(ImageColorSpace.Rgb, returned.ColorSpace);
        Assert.Equal(image.Samples, returned.Samples);
        Assert.Equal(image.Mask, returned.Mask);
        Assert.Equal("photo", returned.Name);

        // Nothing was left out, so the report is empty: a document the writer wrote completely says so.
        Assert.Empty(result.Missing);
    }

    /// <summary>
    /// **Every spelling of a raster this model holds survives the round trip.** The sample packing is the part
    /// that cannot be guessed: a 1-bit grey, a 4-bit grey and a 4-bit index all pack several components into a
    /// byte, and a 16-bit sample is two bytes with the high one first. Reading those as one byte a component does
    /// not round the picture, it reads the wrong pixel - so each is asserted on the samples themselves rather than
    /// on a colour read from one corner.
    /// </summary>
    [Theory]
    [InlineData("rgb8")]
    [InlineData("rgb8-mask")]
    [InlineData("rgb16")]
    [InlineData("gray1")]
    [InlineData("gray4")]
    [InlineData("gray8")]
    [InlineData("gray8-mask")]
    [InlineData("gray16-mask")]
    [InlineData("indexed1")]
    [InlineData("indexed4-mask")]
    [InlineData("indexed8")]
    [InlineData("jpeg")]
    public void EverySpellingOfARasterSurvivesTheRoundTrip(string spelling)
    {
        ImageItem image = Raster(spelling);
        (ImageItem returned, SvgWriteResult result, _) = RoundTrip(image);

        Assert.Empty(result.Missing);
        Assert.Equal(image.Placement, returned.Placement);
        Assert.Equal(image.PixelWidth, returned.PixelWidth);
        Assert.Equal(image.PixelHeight, returned.PixelHeight);
        Assert.Equal(image.BitsPerComponent, returned.BitsPerComponent);
        Assert.Equal(image.ColorSpace, returned.ColorSpace);
        Assert.Equal(image.PaletteBase, returned.PaletteBase);
        Assert.Equal(image.Filter, returned.Filter);
        Assert.Equal(image.Samples, returned.Samples);
        Assert.Equal(image.Mask, returned.Mask);
        Assert.Equal(image.Palette, returned.Palette);
    }

    /// <summary>
    /// **A JPEG goes out as its own bytes, not as a re-encode.** The model keeps a JPEG compressed - the decoder
    /// cannot open one, and re-encoding somebody else's photograph would be a lossy rewrite - so the bytes are the
    /// picture and a `data:image/jpeg` URI is the exact spelling of them.
    /// </summary>
    [Fact]
    public void AJPEGKeepsItsOwnBytes()
    {
        ImageItem image = Raster("jpeg");
        (ImageItem returned, SvgWriteResult result, string svg) = RoundTrip(image);

        Assert.Contains("data:image/jpeg;base64,", svg, StringComparison.Ordinal);
        Assert.Contains(Convert.ToBase64String(image.Samples), svg, StringComparison.Ordinal);
        Assert.Equal("DCTDecode", returned.Filter);
        Assert.Equal(image.Samples, returned.Samples);
        Assert.Empty(result.Missing);
    }

    /// <summary>
    /// **A flip is state, and it is written as the transform that states it.** The model never resamples a mirrored
    /// raster, and the reader reads a flip back out of the determinant of the element's own transform - so the
    /// writer places the picture in its own frame and carries the flip, rather than drawing it the right way round
    /// and saying nothing.
    /// </summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void AMirroredRasterKeepsItsFlip(bool mirrorX, bool mirrorY)
    {
        ImageItem image = Raster("rgb8");
        image.MirrorX = mirrorX;
        image.MirrorY = mirrorY;

        (ImageItem returned, SvgWriteResult result, string svg) = RoundTrip(image);

        Assert.Contains("transform=", svg, StringComparison.Ordinal);
        Assert.Equal(image.Placement, returned.Placement);
        Assert.Equal(image.Samples, returned.Samples);
        Assert.Equal(mirrorX, returned.MirrorX);
        Assert.Equal(mirrorY, returned.MirrorY);
        Assert.Empty(result.Missing);
    }

    /// <summary>
    /// **Two flips are the one flip that cannot travel, and that is said rather than discovered.** The reader reads
    /// a flip from the **sign** of the element transform's determinant, and a raster flipped on both axes has a
    /// positive determinant - the same sign as no flip at all, and indistinguishable from a quarter turn's. The
    /// picture is written (unflipped) rather than dropped, and the value is named.
    /// </summary>
    [Fact]
    public void ARasterFlippedOnBothAxesIsWrittenUnflippedAndSaid()
    {
        ImageItem image = Raster("rgb8");
        image.MirrorX = true;
        image.MirrorY = true;

        (ImageItem returned, SvgWriteResult result, _) = RoundTrip(image);

        Assert.NotNull(returned);
        Assert.Equal(image.Samples, returned.Samples);
        Assert.False(returned.MirrorX);
        Assert.False(returned.MirrorY);
        Assert.Contains(result.Missing, entry => entry.Contains("both axes", StringComparison.Ordinal));
    }

    /// <summary>
    /// **A clip on a raster is drawn, not dropped.** A clip is part of the artwork rather than a rendering detail,
    /// and the reader resolves one for every element that can be drawn - so the writer states it on the image the
    /// same way it states one on a path.
    /// </summary>
    [Fact]
    public void AClipOnARasterIsWritten()
    {
        ImageItem image = Raster("rgb8");
        var clip = new ClipSpec();
        clip.SubPaths.Add(new SubPath());
        clip.SubPaths[0].Nodes.Add(new PathNode(new Point2D(0, 0)));
        clip.SubPaths[0].Nodes.Add(new PathNode(new Point2D(10, 0)));
        clip.SubPaths[0].Nodes.Add(new PathNode(new Point2D(10, 10)));
        image.Clips.Add(clip);

        (ImageItem returned, SvgWriteResult result, string svg) = RoundTrip(image);

        Assert.Contains("clip-path=\"url(#", svg, StringComparison.Ordinal);
        Assert.Single(returned.Clips);
        Assert.Empty(result.Missing);
    }

    // ---------------------------------------------------------------- what cannot be stated

    /// <summary>
    /// **A raster the writer cannot encode is named, not converted.** PNG has no CMYK colour type, and converting
    /// the samples to RGB would put a picture in the file that is not the document's - the very conversion the
    /// model refuses on import. The report names the item and the reason, which is the acceptable half of the rule
    /// and is strictly better than the blank report this replaces.
    /// </summary>
    [Fact]
    public void ARasterInASpacePNGCannotStateIsNamedRatherThanConverted()
    {
        var image = new ImageItem
        {
            Name = "scan",
            PixelWidth = 1,
            PixelHeight = 1,
            ColorSpace = ImageColorSpace.Cmyk,
            Samples = new byte[] { 0, 255, 255, 0 },
            Placement = new Rect2D(0, 0, 10, 10),
        };

        (ImageItem? returned, SvgWriteResult result, string svg) = RoundTrip(image);

        Assert.Null(returned);
        Assert.DoesNotContain("<image", svg, StringComparison.Ordinal);

        string entry = Assert.Single(result.Missing);
        Assert.StartsWith("image 'scan'", entry, StringComparison.Ordinal);
        Assert.Contains("CMYK", entry, StringComparison.Ordinal);
    }

    /// <summary>An image whose samples are still compressed by something the writer cannot state is named too.</summary>
    [Fact]
    public void ARasterStillInAnotherFilterIsNamed()
    {
        var image = new ImageItem
        {
            Name = "packed",
            PixelWidth = 1,
            PixelHeight = 1,
            Filter = "CCITTFaxDecode",
            Samples = new byte[] { 1, 2, 3, 4 },
            Placement = new Rect2D(0, 0, 10, 10),
        };

        (ImageItem? returned, SvgWriteResult result, _) = RoundTrip(image);

        Assert.Null(returned);
        string entry = Assert.Single(result.Missing);
        Assert.Contains("CCITTFaxDecode", entry, StringComparison.Ordinal);
    }

    /// <summary>
    /// **A facet the model holds and no image format states is reported, and the picture is still written.** The
    /// file's colour key makes one colour paint nothing, and nothing in a PNG written from samples says so - so the
    /// picture is written (losing the whole raster over the key would help nobody) and the key is named, exactly as
    /// a text run whose embedded programme cannot be written keeps its characters.
    /// </summary>
    [Fact]
    public void AColourKeyIsReportedAndThePictureIsStillWritten()
    {
        ImageItem image = Raster("rgb8");
        image.ColourKey = new double[] { 1, 1, 1, 1, 1, 1 };

        (ImageItem returned, SvgWriteResult result, _) = RoundTrip(image);

        Assert.NotNull(returned);
        Assert.Equal(image.Samples, returned.Samples);
        Assert.Contains(result.Missing, entry => entry.Contains("colour key", StringComparison.Ordinal));
    }

    /// <summary>
    /// **A hidden raster is written visible and said so.** SVG's `display="none"` is not a hidden element to this
    /// reader - it is an element it never reads - so writing the raster hidden would delete it outright. Losing the
    /// flag is the smaller loss, and it is named. The text writer takes the same decision for the same reason.
    /// </summary>
    [Fact]
    public void AHiddenRasterLosesItsFlagAndSaysSo()
    {
        ImageItem image = Raster("rgb8");
        image.IsVisible = false;

        (ImageItem returned, SvgWriteResult result, _) = RoundTrip(image);

        Assert.NotNull(returned);
        Assert.True(returned.IsVisible);
        Assert.Contains(result.Missing, entry => entry.Contains("hidden", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- the fixtures

    private static (ImageItem Returned, SvgWriteResult Result, string Svg) RoundTrip(ImageItem image)
    {
        CadDocument document = CadDocument.CreateDefault();
        document.Artboards[0].Layers[0].AddItem(image);

        SvgWriteResult result = SvgWriter.WriteResult(document);
        ImageItem? returned = SvgReader.Read(result.Svg).Document.AllItems().OfType<ImageItem>().SingleOrDefault();
        return (returned!, result, result.Svg);
    }

    /// <summary>
    /// One raster of each spelling the model holds, with samples that make the packing visible: a byte that reads
    /// as the wrong component is a byte that comes back different.
    /// </summary>
    private static ImageItem Raster(string spelling)
    {
        var image = new ImageItem { Name = spelling, Placement = new Rect2D(5, 7, 20, 14) };

        switch (spelling)
        {
            case "rgb8":
                image.PixelWidth = 3;
                image.PixelHeight = 2;
                image.BitsPerComponent = 8;
                image.ColorSpace = ImageColorSpace.Rgb;
                image.Samples = Pixels(3, 2, 3);
                break;

            case "rgb8-mask":
                image.PixelWidth = 3;
                image.PixelHeight = 2;
                image.BitsPerComponent = 8;
                image.ColorSpace = ImageColorSpace.Rgb;
                image.Samples = Pixels(3, 2, 3);
                image.Mask = new byte[] { 0, 60, 120, 180, 240, 255 };
                break;

            case "rgb16":
                image.PixelWidth = 2;
                image.PixelHeight = 1;
                image.BitsPerComponent = 16;
                image.ColorSpace = ImageColorSpace.Rgb;

                // Two bytes a component, high byte first, which is the order PDF and PNG both use.
                image.Samples = new byte[]
                {
                    0x12, 0x34, 0x56, 0x78, 0x9A, 0xBC,
                    0xDE, 0xF0, 0x11, 0x22, 0x33, 0x44,
                };
                break;

            case "gray1":
                image.PixelWidth = 8;
                image.PixelHeight = 1;
                image.BitsPerComponent = 1;
                image.ColorSpace = ImageColorSpace.Gray;
                image.Samples = new byte[] { 0b1011_0010 };
                break;

            case "gray4":
                image.PixelWidth = 2;
                image.PixelHeight = 1;
                image.BitsPerComponent = 4;
                image.ColorSpace = ImageColorSpace.Gray;
                image.Samples = new byte[] { 0xAB };
                break;

            case "gray8":
                image.PixelWidth = 3;
                image.PixelHeight = 2;
                image.BitsPerComponent = 8;
                image.ColorSpace = ImageColorSpace.Gray;
                image.Samples = new byte[] { 0, 40, 80, 120, 160, 255 };
                break;

            case "gray8-mask":
                image.PixelWidth = 2;
                image.PixelHeight = 2;
                image.BitsPerComponent = 8;
                image.ColorSpace = ImageColorSpace.Gray;
                image.Samples = new byte[] { 10, 20, 30, 40 };
                image.Mask = new byte[] { 255, 0, 128, 64 };
                break;

            case "gray16-mask":
                image.PixelWidth = 1;
                image.PixelHeight = 2;
                image.BitsPerComponent = 16;
                image.ColorSpace = ImageColorSpace.Gray;
                image.Samples = new byte[] { 0x01, 0x02, 0x03, 0x04 };
                image.Mask = new byte[] { 200, 100 };
                break;

            case "indexed1":
                image.PixelWidth = 4;
                image.PixelHeight = 1;
                image.BitsPerComponent = 1;
                image.ColorSpace = ImageColorSpace.Indexed;
                image.PaletteBase = ImageColorSpace.Rgb;
                image.Palette = new byte[] { 255, 0, 0, 0, 0, 255 };
                image.Samples = new byte[] { 0b0101_0000 };
                break;

            case "indexed4-mask":
                image.PixelWidth = 3;
                image.PixelHeight = 1;
                image.BitsPerComponent = 4;
                image.ColorSpace = ImageColorSpace.Indexed;
                image.PaletteBase = ImageColorSpace.Rgb;
                image.Palette = new byte[] { 10, 20, 30, 40, 50, 60, 70, 80, 90 };
                image.Samples = new byte[] { 0x01, 0x20 };

                // One opacity per palette entry, which is what a PNG `tRNS` states - so this is the mask an
                // indexed raster can carry rather than one that varies within an entry.
                image.Mask = new byte[] { 255, 128, 0 };
                break;

            case "indexed8":
                image.PixelWidth = 2;
                image.PixelHeight = 1;
                image.BitsPerComponent = 8;
                image.ColorSpace = ImageColorSpace.Indexed;
                image.PaletteBase = ImageColorSpace.Rgb;
                image.Palette = new byte[] { 1, 2, 3, 4, 5, 6 };
                image.Samples = new byte[] { 1, 0 };
                break;

            case "jpeg":
                image.PixelWidth = 4;
                image.PixelHeight = 2;
                image.BitsPerComponent = 8;
                image.ColorSpace = ImageColorSpace.Rgb;
                image.Filter = "DCTDecode";
                image.Samples = TinyJpeg();
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(spelling), spelling, "no such raster fixture");
        }

        return image;
    }

    /// <summary>Distinct sample bytes, so a component read out of place is a byte that came back different.</summary>
    private static byte[] Pixels(int width, int height, int components)
    {
        var samples = new byte[width * height * components];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = (byte)((i * 37) + 1);
        }

        return samples;
    }

    /// <summary>A JPEG header with a frame and no scan: a 4x2 picture with three components, which is as much as
    /// placing it needs - and its bytes are what a JPEG data URI has to carry.</summary>
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
