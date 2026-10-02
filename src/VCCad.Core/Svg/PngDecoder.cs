using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using VCCad.Core.Model;

namespace VCCad.Core.Svg;

/// <summary>
/// Reads a PNG into the model's raster form.
///
/// **Why this lives here.** The model has no PNG filter: an <see cref="ImageItem"/> holds samples that a reader
/// has already decoded (or a compressed stream it can name), and PNG's per-scanline filters are not one of the
/// compressors it knows. A `data:` image is therefore opened here, once, so that what reaches the model is the
/// picture - and so an SVG behaves like a PDF, which also decodes what it can and keeps the compressed bytes only
/// when it genuinely cannot.
///
/// The decode deliberately stops short of what it cannot do rather than guessing: an **interlaced** PNG stores its
/// rows in seven passes and is reported instead of being read as if it were sequential, which would produce a
/// shredded picture that looks like a rendering fault rather than a missing feature.
/// </summary>
internal static class PngDecoder
{
    private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

    /// <summary>Reads a PNG, or returns null and says why it could not be read.</summary>
    public static ImageItem? Decode(byte[] png, string name, out string? problem)
    {
        problem = null;

        if (png.Length < Signature.Length)
        {
            problem = "the PNG is shorter than its signature";
            return null;
        }

        for (int i = 0; i < Signature.Length; i++)
        {
            if (png[i] != Signature[i])
            {
                problem = "the bytes are not a PNG";
                return null;
            }
        }

        int width = 0;
        int height = 0;
        int bitDepth = 0;
        int colorType = 0;
        byte[]? palette = null;
        byte[]? transparency = null;
        var compressed = new MemoryStream();
        bool header = false;

        int at = Signature.Length;
        while (at + 8 <= png.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(at, 4));
            if (length < 0 || at + 12 + length > png.Length)
            {
                problem = "a PNG chunk runs past the end of the data";
                return null;
            }

            string kind = Encoding.ASCII.GetString(png, at + 4, 4);
            int start = at + 8;

            switch (kind)
            {
                case "IHDR":
                    if (length < 13)
                    {
                        problem = "the PNG's header is truncated";
                        return null;
                    }

                    width = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(start, 4));
                    height = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(start + 4, 4));
                    bitDepth = png[start + 8];
                    colorType = png[start + 9];
                    header = true;

                    if (png[start + 12] != 0)
                    {
                        problem = "the PNG is interlaced";
                        return null;
                    }

                    break;

                case "PLTE":
                    palette = png[start..(start + length)];
                    break;

                case "tRNS":
                    transparency = png[start..(start + length)];
                    break;

                case "IDAT":
                    compressed.Write(png, start, length);
                    break;
            }

            at = start + length + 4;
            if (kind == "IEND")
            {
                break;
            }
        }

        if (!header || width <= 0 || height <= 0)
        {
            problem = "the PNG has no usable header";
            return null;
        }

        int channels = colorType switch
        {
            0 => 1,
            2 => 3,
            3 => 1,
            4 => 2,
            6 => 4,
            _ => 0,
        };

        if (channels == 0 || !DepthAllowed(colorType, bitDepth))
        {
            problem = $"the PNG uses colour type {colorType} at {bitDepth} bits, which this reader does not open";
            return null;
        }

        if (colorType == 3 && palette is null)
        {
            problem = "the PNG is indexed but carries no palette";
            return null;
        }

        byte[] data;
        try
        {
            using var source = new MemoryStream(compressed.ToArray());
            using var inflate = new ZLibStream(source, CompressionMode.Decompress);
            using var inflated = new MemoryStream();
            inflate.CopyTo(inflated);
            data = inflated.ToArray();
        }
        catch (InvalidDataException)
        {
            problem = "the PNG's compressed data could not be read";
            return null;
        }

        // PNG packs sub-byte samples, so a row is measured in bits and a filter's "bytes per pixel" is at least
        // one - a 1-bit image steps one byte at a time through the filter, not one bit.
        int bitsPerPixel = channels * bitDepth;
        int rowBytes = ((width * bitsPerPixel) + 7) / 8;
        int step = Math.Max(1, bitsPerPixel / 8);

        if (data.Length < (long)(rowBytes + 1) * height)
        {
            problem = "the PNG's image data is shorter than its header says";
            return null;
        }

        byte[] samples;
        if (!Unfilter(data, rowBytes, step, height, out samples, out problem))
        {
            return null;
        }

        var image = new ImageItem
        {
            Name = name,
            PixelWidth = width,
            PixelHeight = height,
            BitsPerComponent = bitDepth,
        };

        // **The colour space is set before the coverage is, for an indexed PNG.** A palette entry's opacity is
        // looked up through the palette *index*, and `RawSampleAt` measures that index by stepping `Components`
        // samples a pixel - which is three while the item is still the default RGB. Every index past the first
        // therefore read a byte that is not the index, and an indexed PNG with a `tRNS` imported with the wrong
        // coverage: no crash, no report, and a picture with the transparency in the wrong places.
        if (colorType == 3)
        {
            image.ColorSpace = ImageColorSpace.Indexed;
            image.PaletteBase = ImageColorSpace.Rgb;
            image.Palette = palette!;
        }

        if (!Separate(samples, colorType, bitDepth, width, height, transparency, image, out problem))
        {
            return null;
        }

        return image;
    }

    /// <summary>Which bit depths each PNG colour type is allowed to use.</summary>
    private static bool DepthAllowed(int colorType, int depth) => colorType switch
    {
        0 => depth is 1 or 2 or 4 or 8 or 16,
        2 => depth is 8 or 16,
        3 => depth is 1 or 2 or 4 or 8,
        4 or 6 => depth is 8 or 16,
        _ => false,
    };

    /// <summary>
    /// Undoes the per-scanline filter PNG applies before compressing.
    ///
    /// Each row names the filter that produced it and is reconstructed against the row above and the byte already
    /// rebuilt within this one - so this has to run in order, and the previous row is kept rather than re-decoded.
    /// </summary>
    private static bool Unfilter(
        byte[] data, int rowBytes, int step, int height, out byte[] samples, out string? problem)
    {
        problem = null;
        samples = new byte[rowBytes * height];

        var prior = new byte[rowBytes];
        var current = new byte[rowBytes];
        int at = 0;

        for (int y = 0; y < height; y++)
        {
            int filter = data[at++];
            if (filter > 4)
            {
                problem = $"a PNG row names filter {filter}, which does not exist";
                return false;
            }

            for (int i = 0; i < rowBytes; i++)
            {
                int raw = data[at + i];
                int left = i >= step ? current[i - step] : 0;
                int above = prior[i];
                int corner = i >= step ? prior[i - step] : 0;

                current[i] = (byte)(filter switch
                {
                    0 => raw,
                    1 => raw + left,
                    2 => raw + above,
                    3 => raw + ((left + above) >> 1),
                    _ => raw + Paeth(left, above, corner),
                });
            }

            at += rowBytes;
            Buffer.BlockCopy(current, 0, samples, y * rowBytes, rowBytes);

            (prior, current) = (current, prior);
        }

        return true;
    }

    /// <summary>
    /// The filter PNG names after Paeth: whichever of the three neighbours the gradient predicts.
    ///
    /// It is a predictor rather than a filter - the byte is whichever neighbour is closest to `left + above -
    /// corner` - and getting it wrong smears every photographic PNG rather than failing outright.
    /// </summary>
    private static int Paeth(int left, int above, int corner)
    {
        int estimate = left + above - corner;
        int toLeft = Math.Abs(estimate - left);
        int toAbove = Math.Abs(estimate - above);
        int toCorner = Math.Abs(estimate - corner);

        return toLeft <= toAbove && toLeft <= toCorner ? left
            : toAbove <= toCorner ? above
            : corner;
    }

    /// <summary>
    /// Turns the unfiltered scanlines into the model's sample layout.
    ///
    /// Three of PNG's five colour types interleave coverage with colour, and the model keeps those apart (a soft
    /// mask beside the samples), so those rows are split here. The rest are already what the model wants: grey and
    /// indexed samples pack exactly the way <see cref="ImageItem"/> packs them.
    /// </summary>
    private static bool Separate(
        byte[] samples, int colorType, int bitDepth, int width, int height, byte[]? transparency,
        ImageItem image, out string? problem)
    {
        problem = null;

        switch (colorType)
        {
            case 0:
                image.ColorSpace = ImageColorSpace.Gray;
                image.Samples = samples;

                // A grey colour key is the one sample value that paints nothing. PNG writes it as a 16-bit value
                // whatever the depth is, which is why it is scaled by the range rather than by 255.
                if (transparency is { Length: >= 2 })
                {
                    double key = BinaryPrimitives.ReadUInt16BigEndian(transparency) / (double)((1 << bitDepth) - 1);
                    image.ColourKey = new[] { key, key };
                }

                return true;

            case 2:
                image.ColorSpace = ImageColorSpace.Rgb;
                image.Samples = samples;

                if (transparency is { Length: >= 6 })
                {
                    double max = (1 << bitDepth) - 1;
                    image.ColourKey = new[]
                    {
                        BinaryPrimitives.ReadUInt16BigEndian(transparency) / max,
                        BinaryPrimitives.ReadUInt16BigEndian(transparency.AsSpan(2)) / max,
                        BinaryPrimitives.ReadUInt16BigEndian(transparency.AsSpan(4)) / max,
                    };
                }

                return true;

            case 3:
                image.Samples = samples;

                // An indexed PNG carries per-entry opacity rather than one transparent colour. The samples stay
                // packed indices, so the entries are looked up through the same accessor a renderer will use.
                if (transparency is { Length: > 0 })
                {
                    image.Mask = IndexedMask(image, width, height, transparency);
                }

                return true;

            default:
            {
                // Grey+alpha and RGB+alpha: two components per pixel, interleaved.
                int components = colorType == 4 ? 1 : 3;
                int bytesPerSample = bitDepth / 8;
                int pixelBytes = (components + 1) * bytesPerSample;

                var colour = new byte[width * height * components * bytesPerSample];
                var mask = new byte[width * height];

                for (int i = 0; i < width * height; i++)
                {
                    int source = i * pixelBytes;
                    Buffer.BlockCopy(samples, source, colour, i * components * bytesPerSample,
                        components * bytesPerSample);

                    // Coverage is one byte per pixel in the model even when the file writes sixteen, so the first
                    // byte of the sample is the one kept - PNG is big-endian, so that is the high byte.
                    mask[i] = samples[source + (components * bytesPerSample)];
                }

                image.ColorSpace = components == 1 ? ImageColorSpace.Gray : ImageColorSpace.Rgb;
                image.Samples = colour;
                image.Mask = mask;
                return true;
            }
        }
    }

    /// <summary>Coverage for an indexed PNG's per-entry transparency.</summary>
    private static byte[] IndexedMask(ImageItem image, int width, int height, byte[] transparency)
    {
        var mask = new byte[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int index = image.RawSampleAt(x, y);
                mask[(y * width) + x] = index >= 0 && index < transparency.Length ? transparency[index] : (byte)255;
            }
        }

        return mask;
    }
}
