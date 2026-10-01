using System.Text;
using VCCad.Core.Model;
using VCCad.Core.Serialization;
using VCCad.Pdf;
using Xunit;

namespace VCCad.Pdf.Tests;

/// <summary>
/// A compressed image keeps its compressor.
///
/// A JPEG cannot be decoded by this reader, so its bytes are passed through exactly as they
/// arrived. They only mean anything alongside the filter that produced them: the
/// Transparency Guide's page 7 photograph is 185x174 RGB in 6,114 bytes, and an image that
/// size needs 96,570 bytes of samples. Written out as raw samples, the export said "here are
/// six kilobytes of RGB for a picture that needs ninety-six", and the two engines made
/// different things of it — one left the area blank and the other filled it black.
///
/// How much this is worth on the page is not settled: fixing it moved the Transparency
/// Guide's numbers by nothing measurable, so whatever is drawn wrongly there is something
/// else. What is settled is that the file now says what it means.
/// </summary>
public class CompressedImageTests
{
    /// <summary>A page with one 2x2 JPEG of a known, tiny shape, as raw DCTDecode bytes.</summary>
    private static byte[] JpegPdf(byte[] jpeg)
    {
        string content = "q 100 0 0 100 50 50 cm /Im0 Do Q";

        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                + "/Resources << /XObject << /Im0 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            $"<< /Type /XObject /Subtype /Image /Width 2 /Height 2 "
                + $"/BitsPerComponent 8 /ColorSpace /DeviceRGB /Filter /DCTDecode "
                + $"/Length {jpeg.Length} >>\nstream\n",
        };

        var builder = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();

        for (int i = 0; i < bodies.Count - 1; i++)
        {
            offsets.Add(builder.Length);
            builder.Append($"{i + 1} 0 obj\n{bodies[i]}\nendobj\n");
        }

        // The last object carries binary, so it is written as bytes rather than as text.
        offsets.Add(builder.Length);
        builder.Append($"{bodies.Count} 0 obj\n{bodies[^1]}");
        var head = Encoding.Latin1.GetBytes(builder.ToString());
        var tail = Encoding.Latin1.GetBytes("\nendstream\nendobj\n");

        int xref = head.Length + jpeg.Length + tail.Length;
        var rest = new StringBuilder();
        rest.Append($"xref\n0 {bodies.Count + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets)
        {
            rest.Append($"{offset:0000000000} 00000 n \n");
        }

        rest.Append($"trailer\n<< /Size {bodies.Count + 1} /Root 1 0 R >>\n");
        rest.Append($"startxref\n{xref}\n%%EOF\n");

        return head.Concat(jpeg).Concat(tail).Concat(Encoding.Latin1.GetBytes(rest.ToString()))
            .ToArray();
    }

    /// <summary>Enough of a JPEG for the reader to pass through, not for anyone to decode.</summary>
    private static byte[] JpegBytes()
        => new byte[]
        {
            0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01,
            0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0xFF, 0xD9,
        };

    [Fact]
    public void ACompressedImageIsImportedWithItsCompressor()
    {
        CadDocument document = PdfImporter.Import(JpegPdf(JpegBytes()));
        ImageItem image = Imported.OneOn<ImageItem>(document);

        Assert.Equal("DCTDecode", image.Filter);

        // And the bytes are the ones that arrived, not something decoded from them.
        Assert.Equal(JpegBytes(), image.Samples);
    }

    [Fact]
    public void TheCompressorIsWrittenBackOut()
    {
        CadDocument document = PdfImporter.Import(JpegPdf(JpegBytes()));
        string pdf = Encoding.Latin1.GetString(PdfDocumentExporter.Export(document));

        Assert.Contains("/DCTDecode", pdf, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCompressorSurvivesSaveAndReload()
    {
        CadDocument document = VccadDocumentSerializer.Deserialize(
            VccadDocumentSerializer.SerializeToBytes(PdfImporter.Import(JpegPdf(JpegBytes()))));

        ImageItem image = Imported.OneOn<ImageItem>(document);
        Assert.Equal("DCTDecode", image.Filter);
    }

    [Fact]
    public void AnUncompressedImageCarriesNoCompressor()
    {
        // A Flate image is opened on the way in, so its bytes really are samples and there is
        // nothing to say about a filter. Saying "FlateDecode" here would double-compress it.
        string content = "q 100 0 0 100 50 50 cm /Im0 Do Q";
        string pixels = "0 0 0 255 255 255 255 255 255 0 0 0";

        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                + "/Resources << /XObject << /Im0 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            $"<< /Type /XObject /Subtype /Image /Width 2 /Height 2 "
                + $"/BitsPerComponent 8 /ColorSpace /DeviceRGB /Length {pixels.Length} >>\n"
                + $"stream\n{pixels}\nendstream",
        };

        var builder = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(builder.Length);
            builder.Append($"{i + 1} 0 obj\n{bodies[i]}\nendobj\n");
        }

        int xref = builder.Length;
        builder.Append($"xref\n0 {bodies.Count + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets)
        {
            builder.Append($"{offset:0000000000} 00000 n \n");
        }

        builder.Append($"trailer\n<< /Size {bodies.Count + 1} /Root 1 0 R >>\n");
        builder.Append($"startxref\n{xref}\n%%EOF\n");

        CadDocument document = PdfImporter.Import(
            Encoding.Latin1.GetBytes(builder.ToString()));
        ImageItem image = Imported.OneOn<ImageItem>(document);

        Assert.Null(image.Filter);

        string pdf = Encoding.Latin1.GetString(PdfDocumentExporter.Export(document));
        Assert.DoesNotContain("/DCTDecode", pdf, StringComparison.Ordinal);
    }
}
