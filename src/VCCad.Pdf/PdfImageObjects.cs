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
            if (image.HasMask)
            {
                maskObject = assembler.Allocate();
                assembler.SetBody(maskObject, PdfDocumentExporter.MakeStreamObject(
                    PdfDocumentExporter.CompressBytes(image.Mask),
                    $" /Type /XObject /Subtype /Image /Width {image.PixelWidth}" +
                    $" /Height {image.PixelHeight} /BitsPerComponent 8 /ColorSpace /DeviceGray"));
            }

            string smask = maskObject != 0 ? $" /SMask {maskObject} 0 R" : string.Empty;

            int objectNumber = assembler.Allocate();
            assembler.SetBody(objectNumber, PdfDocumentExporter.MakeStreamObject(
                PdfDocumentExporter.CompressBytes(image.Samples),
                $" /Type /XObject /Subtype /Image /Width {image.PixelWidth}" +
                $" /Height {image.PixelHeight} /BitsPerComponent {image.BitsPerComponent}" +
                $" /ColorSpace {ColorSpaceOf(image)}{smask}"));

            _names[image] = name;
            _entries.Add((name, objectNumber));
        }
    }

    /// <summary>True once at least one image was written.</summary>
    public bool Any => _entries.Count > 0;

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
