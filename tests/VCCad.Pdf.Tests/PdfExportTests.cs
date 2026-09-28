using System.Text;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Pdf.Tests;

public class PdfExportTests
{
    private static CadDocument BuildFixture()
    {
        CadDocument doc = CadDocument.CreateDefault("Exported");
        Layer layer = doc.Artboards[0].Layers[0];

        PathItem rect = PathFactory.CreateRectangle("swatch", new Rect2D(20, 30, 120, 80));
        rect.Fill = FillSpec.Solid(ColorRgb.FromBytes(220, 30, 30));
        rect.Stroke = new StrokeSpec(true, ColorRgb.Black, 2.5, StrokeCap.Round, StrokeJoin.Bevel, 3.0);
        layer.AddItem(rect);

        PathItem curve = PathFactory.CreateLine("curve", new Point2D(10, 10), new Point2D(200, 200));
        curve.SubPaths[0].Nodes[0].OutHandle = new Point2D(80, -60);
        curve.SubPaths[0].Nodes[1].InHandle = new Point2D(150, 120);
        curve.Stroke = StrokeSpec.Hairline(ColorRgb.Blue);
        layer.AddItem(curve);

        PathItem open = PathFactory.CreatePolyline("open", new[]
        {
            new Point2D(300, 300),
            new Point2D(360, 300),
            new Point2D(360, 380),
        });
        open.Stroke = StrokeSpec.Hairline(ColorRgb.Green);
        layer.AddItem(open);

        doc.AddArtboard(PageSizes.A4Portrait, "Page 2");
        return doc;
    }

    [Fact]
    public void ExportedFileHasPdfHeaderAndEof()
    {
        if (!StandardFontFixture.Available) { return; }

        byte[] pdf = PdfDocumentExporter.Export(BuildFixture());
        string head = Encoding.ASCII.GetString(pdf, 0, 9);
        Assert.StartsWith("%PDF-1.7", head);
        string tail = Encoding.ASCII.GetString(pdf, pdf.Length - 32, 32);
        Assert.Contains("%%EOF", tail);
    }

    [Fact]
    public void EveryArtboardBecomesAPage()
    {
        if (!StandardFontFixture.Available) { return; }

        byte[] pdf = PdfDocumentExporter.Export(BuildFixture());
        string text = Encoding.ASCII.GetString(pdf);
        Assert.Contains("/Count 2", text);
        // Two artboards (A4 landscape + A4 portrait) → exactly two page objects.
        Assert.Equal(2, CountOf(text, "/Type /Page "));
    }

    [Fact]
    public void ContentStreamUsesNativeCubicOperators()
    {
        if (!StandardFontFixture.Available) { return; }

        byte[] pdf = PdfDocumentExporter.Export(BuildFixture());
        // The exporter must not flatten curves: straight lines are `l`, cubic
        // segments are `c`. Our fixture has both, plus closed/open painting ops.
        string[] tokens = DecompressAllContent(pdf).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("m", tokens);
        Assert.Contains("l", tokens);
        Assert.Contains("c", tokens);
        Assert.Contains("cm", tokens); // model→PDF y-flip matrix
        Assert.Contains("f", tokens);  // fill, closed contour
        Assert.Contains("S", tokens);  // stroke
    }

    [Fact]
    public void RoundTripThroughPdfPreservesTheDocument()
    {
        if (!StandardFontFixture.Available) { return; }

        CadDocument doc = BuildFixture();
        byte[] pdf = PdfDocumentExporter.Export(doc);
        CadDocument revived = PdfSidecarReader.ReadDocument(pdf);

        string original = VCCad.Core.Serialization.VccadDocumentSerializer.Serialize(doc);
        string after = VCCad.Core.Serialization.VccadDocumentSerializer.Serialize(revived);
        Assert.Equal(original, after);
    }

    [Fact]
    public void TextIsExportedWithEmbeddedFont()
    {
        if (!StandardFontFixture.Available) { return; }

        CadDocument doc = CadDocument.CreateDefault("text");
        var text = new TextItem { Name = "t", Origin = new Point2D(50, 60) };
        text.Runs.Add(new TextRun { Text = "VCCad", FontFamily = "Helvetica", FontSize = 24, Bold = true });
        doc.Artboards[0].Layers[0].AddItem(text);

        byte[] pdf = PdfDocumentExporter.Export(doc);
        string ascii = System.Text.Encoding.ASCII.GetString(pdf);

        Assert.Contains("/FontFile2", ascii);
        Assert.Contains("/Identity-H", ascii);
        Assert.Contains("/ToUnicode", ascii);

        // Content streams are compressed; check the operators after inflating.
        string content = DecompressAllContent(pdf);
        Assert.Contains("Tf", content);
        Assert.Contains("Tj", content);

        // The lossless sidecar still round-trips the text object.
        CadDocument revived = PdfSidecarReader.ReadDocument(pdf);
        Assert.IsType<TextItem>(revived.Artboards[0].Layers[0].Children[0]);
    }

    [Fact]
    public void MissingSidecarRaisesInvalidData()
    {
        if (!StandardFontFixture.Available) { return; }

        // Hand-built PDF with a catalog but no Names/EmbeddedFiles chain.
        var assembler = new PdfAssemblerInternal();
        byte[] pdf = assembler.BuildWithoutSidecar();
        Assert.Throws<InvalidDataException>(() => PdfSidecarReader.ExtractSidecar(pdf));
    }

    private static string DecompressAllContent(byte[] pdf)
    {
        // Oracle: locate every stream via its exact dictionary→stream marker (the
        // byte payload may coincidentally contain stream-like substrings, so we
        // match the full "/FlateDecode >>\nstream\n" prefix and skip EOL after it).
        string text = Encoding.Latin1.GetString(pdf);
        const string marker = "/FlateDecode >>\nstream";
        var sb = new StringBuilder();
        int scan = 0;
        while (true)
        {
            int dictStart = text.IndexOf(marker, scan, StringComparison.Ordinal);
            if (dictStart < 0)
            {
                break;
            }

            int dataStart = dictStart + marker.Length;
            if (dataStart < text.Length && text[dataStart] == '\n')
            {
                dataStart++;
            }

            int dataEnd = text.IndexOf("endstream", dataStart, StringComparison.Ordinal);
            while (dataEnd > dataStart && (text[dataEnd - 1] == '\n' || text[dataEnd - 1] == '\r'))
            {
                dataEnd--;
            }

            byte[] deflated = Encoding.Latin1.GetBytes(text[dataStart..dataEnd]);
            byte[] raw = PdfDocumentExporter.Decompress(deflated);
            sb.Append(Encoding.UTF8.GetString(raw));

            scan = dataEnd + 1;
        }

        return sb.ToString();
    }

    private static int CountOf(string haystack, string needle)
    {
        int count = 0;
        int index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    /// <summary>Exposes the internal assembler for constructing a sidecar-less PDF.</summary>
    private sealed class PdfAssemblerInternal
    {
        public byte[] BuildWithoutSidecar()
        {
            var assembler = new PdfAssembler();
            int catalog = assembler.Allocate();
            int pages = assembler.Allocate();
            int page = assembler.Allocate();
            int content = assembler.Allocate();

            assembler.SetBody(content, "<< /Length 0 >>\nstream\n\nendstream");
            assembler.SetBody(page, $"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 100 100] " +
                                     $"/Contents {content} 0 R /Resources << >> >>");
            assembler.SetBody(pages, $"<< /Type /Pages /Kids [{page} 0 R] /Count 1 >>");
            assembler.SetBody(catalog, $"<< /Type /Catalog /Pages {pages} 0 R >>");
            return assembler.Serialize(catalog);
        }
    }
}