using System.Text;
using VCCad.Core.Model;

namespace VCCad.Core.Svg;

/// <summary>
/// The raster an SVG <c>image</c> refers to.
///
/// **A reference is resolved or it is reported; it is never fetched.** An SVG may point at a file beside it, at a
/// `data:` URI carried inside the document, or at a URL on the network - and the last of those is the one this
/// reader refuses. The repository's rule is that an importer reflects the file: turning a document into a request
/// for somebody else's server is not reading it, and an import that is allowed to reach out is an import whose
/// result depends on the weather.
///
/// A reference that cannot be resolved - a missing file, a URL, a format this reader does not open - comes back as
/// a **reason** rather than as an empty picture, so the caller can put it in the list of things the document asks
/// for and did not get.
/// </summary>
internal static class SvgImages
{
    /// <summary>The image an `<image>` element names, or null with the reason it could not be read.</summary>
    public static ImageItem? Load(string? href, string? baseDirectory, string name, out string? problem)
    {
        problem = null;

        if (string.IsNullOrWhiteSpace(href))
        {
            problem = "image has no href";
            return null;
        }

        string reference = href.Trim();

        if (reference.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return FromDataUri(reference, name, out problem);
        }

        if (reference.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            reference.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            reference.StartsWith("//", StringComparison.Ordinal))
        {
            // Deliberately not fetched. Reported as unresolved, because that is what it is.
            problem = $"image '{reference}' is a network reference, which is never fetched";
            return null;
        }

        if (baseDirectory is null || baseDirectory.Length == 0)
        {
            problem = $"image '{reference}' is a file reference with no directory to resolve it against";
            return null;
        }

        string path;
        try
        {
            path = Path.Combine(baseDirectory, Uri.UnescapeDataString(reference));
        }
        catch (UriFormatException)
        {
            problem = $"image '{reference}' is not a path this reader can resolve";
            return null;
        }

        if (!File.Exists(path))
        {
            problem = $"image '{reference}' is not there";
            return null;
        }

        try
        {
            return Decode(File.ReadAllBytes(path), name, out problem);
        }
        catch (IOException exception)
        {
            problem = $"image '{reference}' could not be read: {exception.Message}";
            return null;
        }
        catch (UnauthorizedAccessException exception)
        {
            problem = $"image '{reference}' could not be read: {exception.Message}";
            return null;
        }
    }

    /// <summary>
    /// The payload of a `data:` URI.
    ///
    /// The media type is advisory: what the bytes are is decided by looking at them, because a PNG announced as a
    /// JPEG is a mistake files make and a reader that trusted the label would decode neither.
    /// </summary>
    private static ImageItem? FromDataUri(string reference, string name, out string? problem)
    {
        problem = null;

        int comma = reference.IndexOf(',');
        if (comma < 0)
        {
            problem = "image has a data URI with no data in it";
            return null;
        }

        string header = reference[5..comma];
        string payload = reference[(comma + 1)..];
        byte[] bytes;

        try
        {
            bytes = header.EndsWith(";base64", StringComparison.OrdinalIgnoreCase)
                ? Convert.FromBase64String(payload.Trim())
                : Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload));
        }
        catch (FormatException exception)
        {
            problem = $"image has a data URI that is not readable base64: {exception.Message}";
            return null;
        }

        return Decode(bytes, name, out problem);
    }

    /// <summary>Reads the bytes as whatever raster format they turn out to be.</summary>
    private static ImageItem? Decode(byte[] bytes, string name, out string? problem)
    {
        problem = null;
        if (bytes.Length == 0)
        {
            problem = "the image's data is empty";
            return null;
        }

        if (IsJpeg(bytes))
        {
            // A JPEG is kept exactly as it arrived. The model has no way to open one, and re-encoding it would be
            // a lossy rewrite of somebody else's picture; naming the compressor is what keeps it a picture, since
            // the bytes only mean anything alongside it.
            if (!JpegSize(bytes, out int width, out int height, out int components))
            {
                problem = "the JPEG's size could not be read";
                return null;
            }

            return new ImageItem
            {
                Name = name,
                PixelWidth = width,
                PixelHeight = height,
                BitsPerComponent = 8,
                ColorSpace = components switch
                {
                    1 => ImageColorSpace.Gray,
                    4 => ImageColorSpace.Cmyk,
                    _ => ImageColorSpace.Rgb,
                },
                Samples = bytes,
                Filter = "DCTDecode",
            };
        }

        ImageItem? png = PngDecoder.Decode(bytes, name, out problem);
        if (png is not null)
        {
            return png;
        }

        problem = problem is null
            ? "the image is in a format this reader does not open"
            : $"the image is in a format this reader does not open ({problem})";
        return null;
    }

    private static bool IsJpeg(byte[] bytes)
        => bytes.Length > 3 && bytes[0] == 0xFF && bytes[1] == 0xD8;

    /// <summary>
    /// A JPEG's size and component count, read from its start-of-frame marker.
    ///
    /// Walking the segments is the only way to find it: the header has no fixed offset, because every application
    /// marker before it can be any length. A JPEG whose frame header is not found is reported rather than assumed
    /// to be some default size.
    /// </summary>
    private static bool JpegSize(byte[] jpeg, out int width, out int height, out int components)
    {
        width = 0;
        height = 0;
        components = 0;

        int at = 2;
        while (at + 4 <= jpeg.Length)
        {
            if (jpeg[at] != 0xFF)
            {
                // A marker is only a marker where 0xFF is; anything else is padding between segments.
                at++;
                continue;
            }

            byte marker = jpeg[at + 1];
            if (marker == 0xFF)
            {
                at++;
                continue;
            }

            // Standalone markers carry no length.
            if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
            {
                at += 2;
                continue;
            }

            int length = (jpeg[at + 2] << 8) | jpeg[at + 3];
            if (length < 2)
            {
                return false;
            }

            // SOF0..SOF15, less the three whose numbering is used for other segments: the Huffman table (C4), the
            // JPEG extension (C8) and the arithmetic coding table (CC).
            bool frame = marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;
            if (frame)
            {
                if (at + 9 >= jpeg.Length)
                {
                    return false;
                }

                components = jpeg[at + 9];
                height = (jpeg[at + 5] << 8) | jpeg[at + 6];
                width = (jpeg[at + 7] << 8) | jpeg[at + 8];
                return width > 0 && height > 0 && components > 0;
            }

            at += 2 + length;
        }

        return false;
    }
}
