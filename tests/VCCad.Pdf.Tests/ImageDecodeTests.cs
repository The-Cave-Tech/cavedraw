using System.Text;
using VCCad.Core.Model;
using VCCad.Pdf;
using Xunit;

namespace VCCad.Pdf.Tests;

/// <summary>
/// Two image dictionary entries that change what a picture looks like, and neither of
/// which is optional.
///
/// <c>/Decode</c> maps each component's stored value onto the range it stands for, and is
/// how an image is inverted. A colour-key <c>/Mask [min max ...]</c> makes one colour
/// transparent, which is how a logo drawn on a white card is placed over coloured artwork.
/// Ignoring either paints something the file never asked for — and the samples are stored
/// exactly as they arrived, so an export that drops them produces a different picture.
/// </summary>
public class ImageDecodeTests
{
    private static byte[] Pdf(string imageExtras, byte[] samples, int width, int height,
        string resources, string content)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources {resources} "
                + $"/Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
        };

        var image = new StringBuilder();
        image.Append($"<< /Type /XObject /Subtype /Image /Width {width} /Height {height} ")
            .Append($"/BitsPerComponent 8 {imageExtras} ")
            .Append($"/Length {samples.Length} >>\nstream\n");
        objects.Add(image.ToString() + Encoding.Latin1.GetString(samples) + "\nendstream");

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

    private static ImageItem Import(string extras, byte[] samples, int width = 2, int height = 1)
    {
        string resources = "<< /XObject << /Im1 5 0 R >> >>";
        byte[] pdf = Pdf(extras, samples, width, height, resources,
            "q 200 0 0 200 150 500 cm /Im1 Do Q");
        return PdfImporter.Import(pdf).Artboards[0].Layers[0].Children.OfType<ImageItem>().Single();
    }

    [Fact]
    public void InvertingDecodeTurnsTheSamplesAround()
    {
        // Two grey pixels: dark then light.
        var image = Import("/ColorSpace /DeviceGray /Decode [1 0]",
            new byte[] { 0x00, 0xFF });

        Assert.Equal(1.0, image.SampleAt(0, 0), 6);
        Assert.Equal(0.0, image.SampleAt(1, 0), 6);

        // The stored bytes are untouched, so the file can be written back as it arrived.
        Assert.Equal(0x00, image.RawSampleAt(0, 0));
        Assert.Equal(0xFF, image.RawSampleAt(1, 0));
    }

    [Fact]
    public void ADecodeThatNarrowsTheRangeIsHonoured()
    {
        // 0..1 mapped onto 0.25..0.75: mid grey stays put, black becomes a quarter tone.
        var image = Import("/ColorSpace /DeviceGray /Decode [0.25 0.75]",
            new byte[] { 0x00, 0x80 });

        Assert.Equal(0.25, image.SampleAt(0, 0), 6);

        // 0x80 is 128/255, so the decoded value is 0.25 + (128/255 * 0.5) = 0.50098.
        Assert.Equal(0.501, image.SampleAt(1, 0), 3);
    }

    [Fact]
    public void NoDecodeLeavesTheSamplesAlone()
    {
        var image = Import("/ColorSpace /DeviceGray", new byte[] { 0x00, 0xFF });

        Assert.Null(image.Decode);
        Assert.Equal(0.0, image.SampleAt(0, 0), 6);
        Assert.Equal(1.0, image.SampleAt(1, 0), 6);
    }

    [Fact]
    public void AColourKeyMakesThatColourTransparent()
    {
        // Two RGB pixels: white, then red. White is the key.
        var image = Import(
            "/ColorSpace /DeviceRGB /Mask [1 1 1 1 1 1]",
            new byte[] { 255, 255, 255, 220, 30, 30 });

        Assert.NotNull(image.ColourKey);
        Assert.False(image.HasMask);

        // Coverage, not colour, is what the key changes.
        Assert.Equal(0.0, image.CoverageAt(0, 0), 6);
        Assert.Equal(1.0, image.CoverageAt(1, 0), 6);
    }

    [Fact]
    public void AKeyWrittenInByteValuesStillKeys()
    {
        // Files in the wild write 0..255 where the specification says 0..1. Read at face
        // value, a key of 255 against a component of 1 matches nothing and the whole
        // picture stays opaque — the transparent background simply never appears.
        var image = Import(
            "/ColorSpace /DeviceRGB /Mask [255 255 255 255 255 255]",
            new byte[] { 255, 255, 255, 220, 30, 30 });

        Assert.Equal(0.0, image.CoverageAt(0, 0), 6);
        Assert.Equal(1.0, image.CoverageAt(1, 0), 6);
    }

    [Fact]
    public void OnlyPixelsInsideEveryRangeAreKeyed()
    {
        // A key covering near-white: 250..255 on all three components.
        var image = Import(
            "/ColorSpace /DeviceRGB /Mask [250 255 250 255 250 255]",
            new byte[] { 253, 253, 253, 253, 200, 253 });

        Assert.Equal(0.0, image.CoverageAt(0, 0), 6);

        // One component outside its range keeps the pixel, which is what "and" means here.
        Assert.Equal(1.0, image.CoverageAt(1, 0), 6);
    }

    [Fact]
    public void BothSurviveAReExport()
    {
        CadDocument document = PdfImporter.Import(
            Pdf("/ColorSpace /DeviceRGB /Decode [1 0 1 0 1 0] /Mask [1 1 1 1 1 1]",
                new byte[] { 255, 255, 255, 10, 10, 10 }, 2, 1,
                "<< /XObject << /Im1 5 0 R >> >>", "q 200 0 0 200 150 500 cm /Im1 Do Q"));

        string exported = Encoding.Latin1.GetString(PdfDocumentExporter.Export(document));

        // The samples are written exactly as they arrived, so dropping either of these
        // would export a different picture from the one that was read.
        Assert.Contains("/Decode [1 0 1 0 1 0]", exported, StringComparison.Ordinal);

        // The key is resolved into coverage on the way out - a soft mask is the same
        // transparency, and the exact range stays in the model rather than as 8-bit alpha.
        Assert.Contains("/SMask", exported, StringComparison.Ordinal);
    }
}
