using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using VCCad.Core.Model;

namespace VCCad.Core.Svg;

/// <summary>
/// Writes the model's raster form as a PNG.
///
/// **Why this lives here.** SVG states a picture as an *encoded* resource - a `data:` URI naming a PNG or a JPEG -
/// and <see cref="ImageItem"/> holds **decoded samples**: row-major bytes in whatever colour space the file used,
/// with a soft mask beside them. The reader has opened a PNG since <see cref="PngDecoder"/> and the writer wrote
/// none, so a document containing a raster exported to an SVG without it. This is the other half: the samples are
/// put back into the one format the reader can open.
///
/// **It states what the model holds, or it says why it cannot.** Where PNG has no type for what the model holds -
/// CMYK, a palette that indexes into anything but RGB, a bit depth a colour type does not allow, a soft mask that
/// is not one value per palette entry - the encoder returns null and a reason rather than converting the samples,
/// because a converted raster is a picture the document did not draw. Converting CMYK to RGB here would also be the
/// very conversion the model refuses on import ("what we did not have to change, we do not change").
///
/// **The packing is the same on both sides.** PDF and PNG both pack sub-byte samples most significant first and
/// both put a 16-bit sample big-endian, so a row that goes out is the row that comes back - which is what makes the
/// round trip an assertion on the sample bytes rather than on a colour read from one pixel. Every scanline is
/// written with filter 0 ("none"), so the decoder's unfiltering is exercised on the honest path and the bytes are
/// recovered exactly.
///
/// Determinism is load-bearing for this repository ("identical documents serialize to identical bytes"), and it
/// holds: the deflate stream is a pure function of the bytes written to it.
/// </summary>
internal static class PngEncoder
{
    private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

    /// <summary>
    /// The PNG for a raster, or null and the reason it cannot be stated as one.
    ///
    /// The caller handles a raster that is still compressed: its bytes are the picture and a JPEG goes out as
    /// itself rather than through here, because re-encoding somebody else's photograph is a lossy rewrite.
    /// </summary>
    public static byte[]? Encode(ImageItem image, out string? problem)
    {
        problem = null;

        if (image.PixelWidth <= 0 || image.PixelHeight <= 0)
        {
            problem = "the raster has no pixel grid to place, so there is no picture to write";
            return null;
        }

        if (image.Filter is { Length: > 0 } filter)
        {
            problem = $"the raster's samples are still in {filter}, which is not a picture an image element carries";
            return null;
        }

        int bits = image.BitsPerComponent;
        int width = image.PixelWidth;
        int height = image.PixelHeight;
        bool masked = image.Mask.Length > 0;

        if (masked && image.Mask.Length < width * height)
        {
            problem = "the soft mask is shorter than the pixel grid, so there is no coverage to write";
            return null;
        }

        if (masked && image.MaskFilter is { Length: > 0 } maskFilter)
        {
            problem = $"the soft mask's bytes are still in {maskFilter}, which a PNG alpha channel cannot carry";
            return null;
        }

        int colorType;
        byte[]? palette = null;
        byte[]? transparency = null;

        switch (image.ColorSpace)
        {
            case ImageColorSpace.Gray:
                if (bits is not (1 or 2 or 4 or 8 or 16))
                {
                    problem = $"a PNG states grey samples at 1, 2, 4, 8 or 16 bits, and this raster is {bits}";
                    return null;
                }

                if (masked && bits is not (8 or 16))
                {
                    // PNG allows an alpha channel only at 8 or 16 bits per sample for colour types 4 and 6, and
                    // there is no sub-byte spelling of one at all.
                    problem = $"the raster is {bits}-bit grey with a soft mask, and PNG states a masked grey " +
                              "sample at 8 or 16 bits";
                    return null;
                }

                colorType = masked ? 4 : 0;
                break;

            case ImageColorSpace.Rgb:
                if (bits is not (8 or 16))
                {
                    problem = $"a PNG states colour samples at 8 or 16 bits, and this raster is {bits}";
                    return null;
                }

                colorType = masked ? 6 : 2;
                break;

            case ImageColorSpace.Indexed:
                if (bits is not (1 or 2 or 4 or 8))
                {
                    problem = $"a PNG states palette indices at 1, 2, 4 or 8 bits, and this raster is {bits}";
                    return null;
                }

                if (image.PaletteBase != ImageColorSpace.Rgb)
                {
                    problem = $"PNG states an indexed palette in RGB, and this palette indexes into " +
                              $"{image.PaletteBase}, which would leave every entry a different colour";
                    return null;
                }

                if (image.Palette.Length == 0 || image.Palette.Length % 3 != 0)
                {
                    problem = "the indexed raster has no palette an image element can state";
                    return null;
                }

                if (image.Palette.Length / 3 > 256)
                {
                    problem = "a PNG palette holds at most 256 entries, and this one holds " +
                              (image.Palette.Length / 3);
                    return null;
                }

                colorType = 3;
                palette = image.Palette;

                // Coverage for an indexed raster is per **entry**, not per pixel: a PNG states it in `tRNS`, which
                // is one value per palette entry. A mask that varies within an entry is a coverage the format has
                // no place for, and quantising it would be inventing one.
                if (masked && !EntryMask(image, out transparency))
                {
                    problem = "the soft mask is not one value per palette entry, which is the only coverage a PNG " +
                              "`tRNS` states";
                    return null;
                }

                break;

            case ImageColorSpace.Cmyk:
                problem = "PNG has no CMYK colour type, and the writer does not convert the samples to state one";
                return null;

            default:
                problem = $"the writer has no PNG spelling for a {image.ColorSpace} raster";
                return null;
        }

        int rowBytes = image.RowBytes;
        if (image.Samples.Length < (long)rowBytes * height)
        {
            problem = "the sample grid is shorter than the raster's own size says";
            return null;
        }

        // **Only colour types 4 and 6 put coverage inside the picture.** An indexed raster states it in `tRNS`,
        // which is a chunk rather than a sample, so its rows are the model's rows and the mask never reaches them.
        bool alphaChannel = colorType is 4 or 6;
        byte[] raw = alphaChannel
            ? MaskedRows(image, colorType, bits, width, height)
            : PlainRows(image, rowBytes, height);

        var output = new MemoryStream();
        output.Write(Signature);

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0), width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = (byte)bits;
        header[9] = (byte)colorType;

        // Compression 0, filter 0 and interlace 0: the only values PNG defines, stated rather than left out.
        Chunk(output, "IHDR", header);

        if (palette is not null)
        {
            Chunk(output, "PLTE", palette);
        }

        if (transparency is not null)
        {
            Chunk(output, "tRNS", transparency);
        }

        Chunk(output, "IDAT", Deflate(raw));
        Chunk(output, "IEND", Array.Empty<byte>());
        return output.ToArray();
    }

    /// <summary>
    /// Rows for a raster whose samples are already laid out the way PNG wants them.
    ///
    /// Grey, RGB and indexed samples pack identically in both formats - a sub-byte component is most significant
    /// first and a 16-bit one is big-endian - so the only thing a row gains is the filter byte in front of it.
    /// </summary>
    private static byte[] PlainRows(ImageItem image, int rowBytes, int height)
    {
        var raw = new byte[(rowBytes + 1) * height];
        for (int y = 0; y < height; y++)
        {
            // Filter 0: the row is written as it is. The decoder's unfiltering is exercised on the honest path
            // rather than on a prediction the encoder chose, so a mistake in either half shows up as a byte.
            raw[y * (rowBytes + 1)] = 0;
            Buffer.BlockCopy(image.Samples, y * rowBytes, raw, (y * (rowBytes + 1)) + 1, rowBytes);
        }

        return raw;
    }

    /// <summary>
    /// Rows for a raster whose coverage travels **inside** the picture.
    ///
    /// PNG interleaves an alpha sample with the colour samples of each pixel, and the model keeps coverage in a
    /// separate one-byte plane, so the two are zipped here. A 16-bit alpha is written as the mask byte twice - the
    /// decoder keeps the first byte of a sixteen-bit coverage sample, because the model's mask is one byte per
    /// pixel, so repeating it is the same coverage and a shorter spelling of it.
    /// </summary>
    private static byte[] MaskedRows(ImageItem image, int colorType, int bits, int width, int height)
    {
        int components = colorType == 6 ? 3 : 1;
        int sampleBytes = bits / 8;
        int pixelBytes = (components + 1) * sampleBytes;
        int rowBytes = width * pixelBytes;

        var raw = new byte[(rowBytes + 1) * height];
        for (int y = 0; y < height; y++)
        {
            int at = (y * (rowBytes + 1)) + 1;
            for (int x = 0; x < width; x++)
            {
                int source = ((y * width) + x) * components * sampleBytes;
                Buffer.BlockCopy(image.Samples, source, raw, at, components * sampleBytes);
                at += components * sampleBytes;

                byte coverage = image.Mask[(y * width) + x];
                for (int b = 0; b < sampleBytes; b++)
                {
                    raw[at++] = coverage;
                }
            }
        }

        return raw;
    }

    /// <summary>
    /// The coverage of each palette entry, or false when one entry is used at more than one opacity.
    ///
    /// Every entry is written, including the ones no pixel uses, because a `tRNS` that stops short is a coverage
    /// the decoder reads as opaque - so an image whose mask is entirely opaque would come back with no mask at all,
    /// which is a model that changed rather than a file that is smaller.
    /// </summary>
    private static bool EntryMask(ImageItem image, out byte[]? transparency)
    {
        transparency = null;
        int entries = image.Palette.Length / 3;
        var values = new int[entries];
        Array.Fill(values, -1);

        for (int y = 0; y < image.PixelHeight; y++)
        {
            for (int x = 0; x < image.PixelWidth; x++)
            {
                int index = image.RawSampleAt(x, y);
                if (index < 0 || index >= entries)
                {
                    return false;
                }

                int coverage = image.Mask[(y * image.PixelWidth) + x];
                if (values[index] < 0)
                {
                    values[index] = coverage;
                }
                else if (values[index] != coverage)
                {
                    return false;
                }
            }
        }

        var mask = new byte[entries];
        for (int i = 0; i < entries; i++)
        {
            mask[i] = values[i] < 0 ? (byte)255 : (byte)values[i];
        }

        transparency = mask;
        return true;
    }

    /// <summary>The zlib stream PNG carries its image data in.</summary>
    private static byte[] Deflate(byte[] raw)
    {
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        return compressed.ToArray();
    }

    /// <summary>One PNG chunk: its length, its name, its data, and the CRC over the name and the data.</summary>
    private static void Chunk(Stream output, string kind, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);

        byte[] name = Encoding.ASCII.GetBytes(kind);
        output.Write(name);
        output.Write(data);

        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(name, data));
        output.Write(crc);
    }

    /// <summary>
    /// The CRC PNG names a chunk with - the ordinary CRC-32, reflected, with the polynomial's low bit first.
    ///
    /// Written out rather than reached for: the framework's is in a package this project does not reference, and a
    /// chunk with a wrong CRC is a file whose first chunk a viewer rejects.
    /// </summary>
    private static uint Crc32(byte[] name, byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in name)
        {
            crc = Step(crc, b);
        }

        foreach (byte b in data)
        {
            crc = Step(crc, b);
        }

        return crc ^ 0xFFFFFFFF;
    }

    private static uint Step(uint crc, byte value)
    {
        crc ^= value;
        for (int bit = 0; bit < 8; bit++)
        {
            crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        }

        return crc;
    }
}
