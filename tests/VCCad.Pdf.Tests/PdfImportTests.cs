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

        // Pages are laid out in reading order without overlapping. The axis is not
        // fixed: the layout picks the grid shape closest to a working area, so two
        // very differently-shaped pages stack (600x640) rather than form a 940x400
        // strip. Two equal A4 pages do sit side by side — see PageLayoutTests.
        Assert.False(doc.Artboards[0].Bounds.Intersects(doc.Artboards[1].Bounds));
        Assert.Equal(new Point2D(0, 0), new Point2D(doc.Artboards[0].X, doc.Artboards[0].Y));
        Assert.True(doc.Artboards[1].X > 0 || doc.Artboards[1].Y > 0);
    }

    [Fact]
    public void PageSizeScannerIgnoresPageTreeNodes()
    {
        IReadOnlyList<Size2D> sizes = PdfImporter.ReadPageSizes(BuildTwoPagePdf());
        Assert.Equal(2, sizes.Count);
    }

    [Fact]
    public void QRestoresFullGraphicsStateNotJustTheCtm()
    {
        // A white stroke set inside q...Q must not leak past the Q: the second
        // path is stroked with the black set before the q.
        byte[] pdf = BuildSinglePagePdf(string.Join("\n", new[]
        {
            "0 0 0 RG",
            "q",
            "1 1 1 RG",
            "0 0 m 10 0 l S",
            "Q",
            "0 0 m 20 0 l S",
        }));

        CadDocument doc = PdfImporter.Import(pdf);
        List<PathItem> paths = doc.Artboards[0].Layers
            .SelectMany(l => l.Children).OfType<PathItem>().ToList();

        Assert.Equal(2, paths.Count);
        Assert.True(paths[0].Stroke.Color.R > 0.9, "first path should be white");
        Assert.True(paths[1].Stroke.Color.R < 0.1, "graphics state should be restored to black");
    }

    private static byte[] BuildSinglePagePdf(string content)
    {
        var assembler = new PdfAssembler();
        int catalog = assembler.Allocate();
        int pages = assembler.Allocate();
        int page = assembler.Allocate();
        int contentObj = assembler.Allocate();

        byte[] bytes = Encoding.ASCII.GetBytes(content);
        assembler.SetBody(contentObj, $"<< /Length {bytes.Length} >>\nstream\n{content}\nendstream");
        assembler.SetBody(page, $"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 300 400] /Contents {contentObj} 0 R /Resources << >> >>");
        assembler.SetBody(pages, $"<< /Type /Pages /Kids [{page} 0 R] /Count 1 >>");
        assembler.SetBody(catalog, $"<< /Type /Catalog /Pages {pages} 0 R >>");
        return assembler.Serialize(catalog);
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
