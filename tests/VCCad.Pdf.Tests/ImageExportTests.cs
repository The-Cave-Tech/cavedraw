using System.Text;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Pdf.Tests;

/// <summary>
/// Embedded images must be written back into the exported PDF, in the colour space the
/// document stores. A document that imports an image and then exports nothing has lost
/// it, which is the fidelity rule this project exists to keep.
/// </summary>
public class ImageExportTests
{
    private static ImageItem Sample()
    {
        var image = new ImageItem
        {
            Name = "X",
            PixelWidth = 4,
            PixelHeight = 3,
            BitsPerComponent = 8,
            ColorSpace = ImageColorSpace.Cmyk,
            Placement = new Rect2D(20, 30, 100, 80),
            Samples = new byte[4 * 3 * 4],
            Mask = new byte[4 * 3],
        };

        for (int i = 0; i < image.Samples.Length; i++)
        {
            image.Samples[i] = (byte)(i * 7 % 256);
        }

        for (int i = 0; i < image.Mask.Length; i++)
        {
            image.Mask[i] = (byte)(255 - (i * 9));
        }

        return image;
    }

    private static CadDocument WithImage()
    {
        CadDocument doc = CadDocument.CreateDefault("Images");
        doc.Artboards[0].Layers[0].AddItem(Sample());
        return doc;
    }

    private static string Latin(byte[] pdf) => new(Encoding.Latin1.GetChars(pdf));

    /// <summary>
    /// The decompressed content streams. The placement operators live in a FlateDecode
    /// stream, so searching the raw file for them finds nothing.
    /// </summary>
    private static string Content(byte[] pdf)
    {
        string latin = Latin(pdf);
        var builder = new StringBuilder();

        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(latin, @"(?<!end)stream\r?\n"))
        {
            int start = m.Index + m.Length;
            int end = latin.IndexOf("endstream", start, StringComparison.Ordinal);
            if (end < 0)
            {
                break;
            }

            try
            {
                using var input = new MemoryStream(pdf, start, end - start);
                using var zlib = new System.IO.Compression.ZLibStream(
                    input, System.IO.Compression.CompressionMode.Decompress);
                using var reader = new StreamReader(zlib, Encoding.Latin1);
                builder.Append(reader.ReadToEnd());
            }
            catch (Exception)
            {
                // Not a content stream.
            }
        }

        return builder.ToString();
    }

    [Fact]
    public void AnImageIsWrittenAsAnXObject()
    {
        string pdf = Latin(PdfDocumentExporter.Export(WithImage()));

        Assert.Contains("/Subtype /Image", pdf, StringComparison.Ordinal);
        Assert.Contains("/Width 4", pdf, StringComparison.Ordinal);
        Assert.Contains("/Height 3", pdf, StringComparison.Ordinal);
    }

    [Fact]
    public void TheColourSpaceIsPreserved()
    {
        string pdf = Latin(PdfDocumentExporter.Export(WithImage()));

        // A CMYK image that came in CMYK must go out CMYK.
        Assert.Contains("/DeviceCMYK", pdf, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSoftMaskIsWrittenAndReferenced()
    {
        string pdf = Latin(PdfDocumentExporter.Export(WithImage()));

        Assert.Contains("/SMask", pdf, StringComparison.Ordinal);
        Assert.Contains("/DeviceGray", pdf, StringComparison.Ordinal);
    }

    [Fact]
    public void TheImageIsPlacedWithADoOperator()
    {
        byte[] bytes = PdfDocumentExporter.Export(WithImage());

        // The placement is in a compressed content stream.
        Assert.Contains("/Im1 Do", Content(bytes), StringComparison.Ordinal);

        // And the resource dictionary names it.
        Assert.Contains("/XObject <<", Latin(bytes), StringComparison.Ordinal);
        Assert.Contains("/Im1 ", Latin(bytes), StringComparison.Ordinal);
    }

    [Fact]
    public void AnImageWithoutAMaskWritesNoSmask()
    {
        CadDocument doc = CadDocument.CreateDefault("NoMask");
        ImageItem image = Sample();
        image.Mask = Array.Empty<byte>();
        doc.Artboards[0].Layers[0].AddItem(image);

        string pdf = Latin(PdfDocumentExporter.Export(doc));

        Assert.Contains("/Subtype /Image", pdf, StringComparison.Ordinal);
        Assert.DoesNotContain("/SMask", pdf, StringComparison.Ordinal);
    }

    [Fact]
    public void AnIndexedImageKeepsItsPalette()
    {
        CadDocument doc = CadDocument.CreateDefault("Indexed");
        var image = new ImageItem
        {
            Name = "pal",
            PixelWidth = 2,
            PixelHeight = 2,
            BitsPerComponent = 8,
            ColorSpace = ImageColorSpace.Indexed,
            Samples = new byte[] { 0, 1, 1, 0 },
            Palette = new byte[] { 0, 0, 0, 255, 0, 0 },
            Placement = new Rect2D(0, 0, 10, 10),
        };
        doc.Artboards[0].Layers[0].AddItem(image);

        string pdf = Latin(PdfDocumentExporter.Export(doc));

        Assert.Contains("[/Indexed /DeviceRGB 1 <000000FF0000>]", pdf, StringComparison.Ordinal);
    }

    [Fact]
    public void AnImageHiddenOnThePageIsStillWrittenButNotPlaced()
    {
        CadDocument doc = CadDocument.CreateDefault("Hidden");
        ImageItem image = Sample();
        image.IsVisible = false;
        doc.Artboards[0].Layers[0].AddItem(image);

        byte[] bytes = PdfDocumentExporter.Export(doc);

        // The XObject is written — it is part of the document — but nothing draws it.
        Assert.Contains("/Subtype /Image", Latin(bytes), StringComparison.Ordinal);
        Assert.DoesNotContain("/Im1 Do", Content(bytes), StringComparison.Ordinal);
    }
}
