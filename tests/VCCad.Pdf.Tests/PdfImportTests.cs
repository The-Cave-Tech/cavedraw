using System.Text;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Pdf.Tests;

public class PdfImportTests
{
    /// <summary>Builds a minimal two-page PDF with deliberately different page
    /// sizes, without a VCCad sidecar (foreign-document case).</summary>
    private static byte[] BuildTwoPagePdf()
    {
        var assembler = new PdfAssembler();
        int catalog = assembler.Allocate();
        int pages = assembler.Allocate();
        int pageA = assembler.Allocate();
        int contentA = assembler.Allocate();
        int pageB = assembler.Allocate();
        int contentB = assembler.Allocate();

        assembler.SetBody(contentA, "<< /Length 0 >>\nstream\n\nendstream");
        assembler.SetBody(contentB, "<< /Length 0 >>\nstream\n\nendstream");
        assembler.SetBody(pageA, $"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 300 400] /Contents {contentA} 0 R /Resources << >> >>");
        assembler.SetBody(pageB, $"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 600 200] /Contents {contentB} 0 R /Resources << >> >>");
        assembler.SetBody(pages, $"<< /Type /Pages /Kids [{pageA} 0 R {pageB} 0 R] /Count 2 >>");
        assembler.SetBody(catalog, $"<< /Type /Catalog /Pages {pages} 0 R >>");
        return assembler.Serialize(catalog);
    }

    [Fact]
    public void ForeignPdfImportsOneArtboardPerPageWithExactSizes()
    {
        byte[] pdf = BuildTwoPagePdf();

        CadDocument doc = PdfImporter.Import(pdf);

        Assert.Equal(2, doc.Artboards.Count);
        Assert.Equal(300.0, doc.Artboards[0].Width, 6);
        Assert.Equal(400.0, doc.Artboards[0].Height, 6);
        Assert.Equal(600.0, doc.Artboards[1].Width, 6);
        Assert.Equal(200.0, doc.Artboards[1].Height, 6);
        // Pages are laid out left-to-right, not overlapping.
        Assert.True(doc.Artboards[1].X > doc.Artboards[0].X);
    }

    [Fact]
    public void PageSizeScannerIgnoresPageTreeNodes()
    {
        IReadOnlyList<Size2D> sizes = PdfImporter.ReadPageSizes(BuildTwoPagePdf());
        Assert.Equal(2, sizes.Count);
    }

    [Fact]
    public void VccadPdfRoundTripsLosslesslyThroughImport()
    {
        // Our own export carries the sidecar, so import must be the full model.
        CadDocument original = CadDocument.CreateDefault("roundtrip");
        Layer layer = original.Artboards[0].Layers[0];
        layer.AddItem(PathFactory.CreateEllipse("orb", new Point2D(100, 100), 40, 25));
        original.AddArtboard(PageSizes.A4Portrait, "Portrait", new Point2D(1200, 0));

        byte[] pdf = PdfDocumentExporter.Export(original);
        CadDocument imported = PdfImporter.Import(pdf);

        Assert.Equal(
            VCCad.Core.Serialization.VccadDocumentSerializer.Serialize(original),
            VCCad.Core.Serialization.VccadDocumentSerializer.Serialize(imported));
    }
}
