using System.Text;
using VCCad.Core.Model;

namespace VCCad.Pdf;

/// <summary>
/// Writes embedded raster images into the PDF as XObjects.
///
/// Samples are FlateDecode-compressed and written back in the colour space the document
/// stores, so an image that came in as DeviceCMYK leaves as DeviceCMYK. A soft mask
/// becomes a second greyscale image referenced from /SMask, which is how a transparency
/// channel is carried in a PDF image.
/// </summary>
internal sealed class PdfImageObjects
{
    private readonly Dictionary<ImageItem, string> _names = new();
    private readonly List<(string Name, int Object)> _entries = new();

    public PdfImageObjects(PdfAssembler assembler, IEnumerable<ImageItem> images)
    {
        int index = 0;

        foreach (ImageItem image in images)
        {
            if (image.PixelWidth <= 0 || image.PixelHeight <= 0 ||
                image.Samples.Length == 0 || _names.ContainsKey(image))
            {
                continue;
            }

            string name = $"Im{++index}";

            int maskObject = 0;

            // A colour key is resolved into coverage here rather than at import, so the
            // exact range the file specified survives in the model and a page that keys
            // out white still keys out white after a save and reload.
            byte[]? mask = image.HasMask
                ? image.Mask
                : image.ColourKey is not null ? ResolveColourKey(image) : null;

            if (mask is not null)
            {
                maskObject = assembler.Allocate();

                // The mask's own compressor, for the same reason as the image's: a JPEG mask
                // is not grey samples, and writing it as though it were leaves the viewer to
                // guess. Flate is what this exporter applies to samples it holds.
                bool maskPassThrough = image.MaskFilter is { Length: > 0 };
                assembler.SetBody(maskObject, PdfDocumentExporter.MakeStreamObject(
                    maskPassThrough ? mask : PdfDocumentExporter.CompressBytes(mask),
                    $" /Type /XObject /Subtype /Image /Width {image.PixelWidth}" +
                    $" /Height {image.PixelHeight} /BitsPerComponent 8 /ColorSpace /DeviceGray"
                    + (maskPassThrough ? $" /Filter /{image.MaskFilter}" : string.Empty)));
            }

            string smask = maskObject != 0 ? $" /SMask {maskObject} 0 R" : string.Empty;

            // A JPEG's bytes are not samples, they are a picture in a compressor. Writing
            // them out without saying which one turns a 6 kB photograph into 6 kB of raw RGB
            // for an image that needs 96 kB, and a viewer draws whatever it likes with the
            // difference - one leaves the area blank, another fills it black.
            if (image.Filter is { Length: > 0 } compressor)
            {
                int passThroughNumber = assembler.Allocate();
                assembler.SetBody(passThroughNumber, PdfDocumentExporter.MakeStreamObject(
                    image.Samples,
                    $" /Type /XObject /Subtype /Image /Width {image.PixelWidth}" +
                    $" /Height {image.PixelHeight} /BitsPerComponent {image.BitsPerComponent}" +
                    $" /ColorSpace {ColorSpaceOf(image)}{smask} /Filter /{compressor}"));

                _names[image] = name;
                _entries.Add((name, passThroughNumber));
                continue;
            }

            // Both of these change what the picture looks like, and the samples are
            // written exactly as they arrived, so leaving either out would export a
            // different image from the one that was read.
            string decode = image.Decode is { Length: >= 2 } d
                ? $" /Decode [{string.Join(' ', d.Select(v => v.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture)))}]"
                : string.Empty;

            int objectNumber = assembler.Allocate();
            assembler.SetBody(objectNumber, PdfDocumentExporter.MakeStreamObject(
                PdfDocumentExporter.CompressBytes(image.Samples),
                $" /Type /XObject /Subtype /Image /Width {image.PixelWidth}" +
                $" /Height {image.PixelHeight} /BitsPerComponent {image.BitsPerComponent}" +
                $" /ColorSpace {ColorSpaceOf(image)}{smask}{decode}"));

            _names[image] = name;
            _entries.Add((name, objectNumber));
        }
    }

    /// <summary>True once at least one image was written.</summary>
    public bool Any => _entries.Count > 0;

    /// <summary>
    /// The colour key as per-pixel coverage, one byte each: 0 where the pixel matches the
    /// key and so does not paint, 255 where it does.
    /// </summary>
    private static byte[] ResolveColourKey(ImageItem image)
    {
        var coverage = new byte[image.PixelWidth * image.PixelHeight];
        for (int y = 0; y < image.PixelHeight; y++)
        {
            for (int x = 0; x < image.PixelWidth; x++)
            {
                coverage[(y * image.PixelWidth) + x] =
                    image.IsColourKeyed(x, y) ? (byte)0 : (byte)255;
            }
        }

        return coverage;
    }

    /// <summary>The <c>/XObject</c> entry for the page resource dictionary.</summary>
    public string Dict()
    {
        if (_entries.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(" /XObject << ");
        foreach ((string name, int number) in _entries)
        {
            builder.Append('/').Append(name).Append(' ').Append(number).Append(" 0 R ");
        }

        return builder.Append(">>").ToString();
    }

    public bool TryName(ImageItem image, out string name) => _names.TryGetValue(image, out name!);

    /// <summary>
    /// The image's colour space as the PDF name it was imported as.
    ///
    /// An indexed image carries its palette inline as a hex string, because the palette
    /// is part of the colour space rather than of the pixel data.
    /// </summary>
    private static string ColorSpaceOf(ImageItem image) => image.ColorSpace switch
    {
        ImageColorSpace.Gray => "/DeviceGray",
        ImageColorSpace.Cmyk => "/DeviceCMYK",
        ImageColorSpace.Indexed when image.Palette.Length >= 3 =>
            $"[/Indexed /DeviceRGB {(image.Palette.Length / 3) - 1} <{Convert.ToHexString(image.Palette)}>]",
        ImageColorSpace.Indexed => "/DeviceGray",
        _ => "/DeviceRGB",
    };
}
