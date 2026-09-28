using System.Text;
using VCCad.Core.Model;
using VCCad.Pdf;
using Xunit;

namespace VCCad.Pdf.Tests;

/// <summary>
/// Image masks — one-bit stencils painted in the current fill colour.
///
/// An <c>/ImageMask true</c> image has NO colour space: it says "paint where the bit says
/// to, in whatever colour is current". Treating it as a greyscale image gave a 1-bit RGB
/// image with three components per pixel where the file has one, which is not merely the
/// wrong colour — the exported page could not be rendered at all.
///
/// Stencils are how logos, stamps, signatures and scanned line art are usually carried,
/// so this is the difference between those drawing and not.
/// </summary>
public class ImageMaskTests
{
    /// <summary>A page drawing a stencil at the unit square mapped to 200x200.</summary>
    private static byte[] StencilPdf(byte[] packed, int width, int height,
        string colour = "0 0 0 1 k", string extra = "", string? decode = null)
    {
        string decodeEntry = decode is null ? string.Empty : $" /Decode [{decode}]";
        string content = $"q {colour} 200 0 0 200 150 500 cm /Im1 Do Q";

        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                + "/Resources << /XObject << /Im1 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
        };

        var image = new StringBuilder();
        image.Append($"<< /Type /XObject /Subtype /Image /Width {width} /Height {height} ")
            .Append($"/ImageMask true /BitsPerComponent 1{decodeEntry}{extra} ")
            .Append($"/Length {packed.Length} >>\nstream\n");
        objects.Add(image.ToString() + Encoding.Latin1.GetString(packed) + "\nendstream");

        var builder = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(builder.Length);
            builder.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        int xref = builder.Length;
        builder.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets)
        {
            builder.Append($"{offset:0000000000} 00000 n \n");
        }

        builder.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\n");
        builder.Append($"startxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(builder.ToString());
    }

    /// <summary>Packs one-bit rows, most significant bit first.</summary>
    private static byte[] Pack(params int[] bits)
    {
        var bytes = new byte[(bits.Length + 7) / 8];
        for (int i = 0; i < bits.Length; i++)
        {
            if (bits[i] != 0)
            {
                bytes[i >> 3] |= (byte)(1 << (7 - (i & 7)));
            }
        }

        return bytes;
    }

    [Fact]
    public void AStencilIsNotReadAsAnRgbImage()
    {
        // Four pixels: paint, don't, paint, don't.
        byte[] pdf = StencilPdf(Pack(0, 1, 0, 1), width: 4, height: 1);

        ImageItem image = PdfImporter.Import(pdf)
            .Artboards[0].Layers[0].Children.OfType<ImageItem>().Single();

        // The bug was a 1-bit RGB image: three components per pixel for data that has one.
        Assert.Equal(ImageColorSpace.Indexed, image.ColorSpace);
        Assert.Equal(1, image.BitsPerComponent);
        Assert.Equal(1, image.Components);
    }

    [Fact]
    public void TheStencilPaintsInTheCurrentColour()
    {
        // A dark red fill, in the CMYK the sample files use.
        byte[] pdf = StencilPdf(Pack(0, 1), width: 2, height: 1,
            colour: "0 0.85 0.85 0.1 k");

        ImageItem image = PdfImporter.Import(pdf)
            .Artboards[0].Layers[0].Children.OfType<ImageItem>().Single();

        ColorRgb painted = image.PixelAt(0, 0);

        // Red-dominant, and not black: the colour comes from the fill, not from the bits.
        Assert.True(painted.R > painted.G && painted.R > painted.B,
            $"expected a red-dominant stencil colour, got {painted.R},{painted.G},{painted.B}");
        Assert.True(painted.G < 0.2, $"green should be low, got {painted.G}");
    }

    [Fact]
    public void TheStencilIsOpaqueWhereItPaintsAndClearWhereItDoesNot()
    {
        // Pixels 0 and 2 paint; 1 and 3 do not.
        byte[] pdf = StencilPdf(Pack(0, 1, 0, 1), width: 4, height: 1);

        ImageItem image = PdfImporter.Import(pdf)
            .Artboards[0].Layers[0].Children.OfType<ImageItem>().Single();

        Assert.True(image.HasMask);
        Assert.Equal(1.0, image.CoverageAt(0, 0), 3);
        Assert.Equal(0.0, image.CoverageAt(1, 0), 3);
        Assert.Equal(1.0, image.CoverageAt(2, 0), 3);
        Assert.Equal(0.0, image.CoverageAt(3, 0), 3);
    }

    [Fact]
    public void AnInvertingDecodeFlipsWhichPixelsPaint()
    {
        byte[] pdf = StencilPdf(Pack(0, 1, 0, 1), width: 4, height: 1, decode: "1 0");

        ImageItem image = PdfImporter.Import(pdf)
            .Artboards[0].Layers[0].Children.OfType<ImageItem>().Single();

        Assert.Equal(0.0, image.CoverageAt(0, 0), 3);
        Assert.Equal(1.0, image.CoverageAt(1, 0), 3);
    }

    [Fact]
    public void TheExportIsRenderableRatherThanOneBitRgb()
    {
        byte[] pdf = StencilPdf(Pack(0, 1, 0, 1), width: 4, height: 1, colour: "0 0 0 1 k");
        CadDocument document = PdfImporter.Import(pdf);

        string exported = Encoding.Latin1.GetString(PdfDocumentExporter.Export(document));

        // A 1-bit image with a three-component colour space is not valid PDF, and poppler
        // refused the whole page. It has to be one component with an explicit palette.
        Assert.Contains("/BitsPerComponent 1", exported, StringComparison.Ordinal);
        Assert.Contains("/Indexed /DeviceRGB", exported, StringComparison.Ordinal);
        Assert.DoesNotContain("/BitsPerComponent 1 /ColorSpace /DeviceRGB", exported,
            StringComparison.Ordinal);
    }
}
