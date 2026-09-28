using System.Text;
using VCCad.Core.Model;
using VCCad.Geometry;
using VCCad.Pdf;
using Xunit;

namespace VCCad.Pdf.Tests;

/// <summary>
/// How a multi-page document is laid out in model space after import.
///
/// This is what makes a paginated pattern usable: a real A4 sewing pattern is eight
/// portrait pages, and placing them in one long row produced a 4837 x 814 pt strip
/// that fitted to the window at ~13% zoom — every page an unreadable sliver.
/// </summary>
public class PageLayoutTests
{
    /// <summary>
    /// A minimal multi-page PDF written by hand (ASCII, small pages, no VCCad
    /// sidecar). A PDF exported by this project carries the lossless document in a
    /// sidecar, which the importer prefers — that path would test the round-trip
    /// rather than the page layout.
    /// </summary>
    private static byte[] BuildMultiPagePdf(int pages, double width = 595, double height = 842)
    {
        var body = new StringBuilder();
        var offsets = new Dictionary<int, int>();
        int highest = 0;

        void Add(int number, string content)
        {
            offsets[number] = body.Length;
            body.Append(number).Append(" 0 obj\n").Append(content).Append("\nendobj\n");
            highest = Math.Max(highest, number);
        }

        body.Append("%PDF-1.7\n");
        Add(1, "<< /Type /Catalog /Pages 2 0 R >>");

        var kids = new List<string>();
        int number = 3;
        for (int i = 0; i < pages; i++)
        {
            int pageObject = number++;
            int contentObject = number++;
            kids.Add($"{pageObject} 0 R");

            string content = "0.8 0.8 0.8 rg 50 50 200 300 re f";
            Add(pageObject,
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {width:0.###} {height:0.###}] " +
                $"/Contents {contentObject} 0 R >>");
            Add(contentObject, $"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        }

        Add(2, $"<< /Type /Pages /Kids [{string.Join(' ', kids)}] /Count {pages} >>");

        int xref = body.Length;
        body.Append("xref\n0 ").Append(highest + 1).Append('\n');
        body.Append("0000000000 65535 f \n");
        for (int i = 1; i <= highest; i++)
        {
            body.Append(offsets[i].ToString("D10")).Append(" 00000 n \n");
        }

        body.Append("trailer\n<< /Size ").Append(highest + 1).Append(" /Root 1 0 R >>\n");
        body.Append("startxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(body.ToString());
    }

    private static CadDocument Import(int pages)
    {
        CadDocument document = PdfImporter.Import(BuildMultiPagePdf(pages));

        // Guard: if the hand-written fixture ever stops importing, the layout
        // assertions below would pass vacuously.
        Assert.Equal(pages, document.Artboards.Count);
        return document;
    }

    [Fact]
    public void EightPortraitPagesAreNotLaidOutInASingleRow()
    {
        CadDocument document = Import(8);

        double sheetWidth = document.Artboards.Max(a => a.X + a.Width);
        double sheetHeight = document.Artboards.Max(a => a.Y + a.Height);

        // One row would be ~4837 pt wide and 814 pt tall (5.9:1). A grid must be
        // markedly less extreme, otherwise fitting the view makes it unreadable.
        Assert.True(sheetWidth / sheetHeight < 3.0,
            $"the sheet is still a strip: {sheetWidth:F0}x{sheetHeight:F0}");

        Assert.True(document.Artboards.Select(a => a.Y).Distinct().Count() > 1,
            "all pages share one row");
    }

    [Fact]
    public void PagesNeverOverlap()
    {
        CadDocument document = Import(8);

        for (int i = 0; i < document.Artboards.Count; i++)
        {
            for (int j = i + 1; j < document.Artboards.Count; j++)
            {
                Rect2D a = document.Artboards[i].Bounds;
                Rect2D b = document.Artboards[j].Bounds;

                bool separated = a.Right <= b.Left || b.Right <= a.Left ||
                                 a.Bottom <= b.Top || b.Bottom <= a.Top;
                Assert.True(separated,
                    $"'{document.Artboards[i].Name}' and '{document.Artboards[j].Name}' overlap");
            }
        }
    }

    [Fact]
    public void TwoPagesStillSitSideBySide()
    {
        // The common case must not regress: a two-page document is a spread.
        CadDocument document = Import(2);

        Assert.Equal(document.Artboards[0].Y, document.Artboards[1].Y);
        Assert.True(document.Artboards[1].X >= document.Artboards[0].X + document.Artboards[0].Width);
    }

    [Fact]
    public void GridOriginsHandleMixedPageSizes()
    {
        // A landscape sheet inside a portrait document must not be overlapped by
        // its neighbours: columns and rows size to the largest member.
        var sizes = new List<Size2D>
        {
            new(595, 842),
            new(842, 595),
            new(595, 842),
            new(595, 842),
        };

        IReadOnlyList<Point2D> origins = PdfImporter.GridOrigins(sizes);

        Assert.Equal(4, origins.Count);
        Assert.Equal(new Point2D(0, 0), origins[0]);

        // Every page clears the ones before it on its own row, and starts a new row
        // rather than sitting on top of page 0.
        for (int i = 1; i < origins.Count; i++)
        {
            Assert.True(origins[i].X > 0 || origins[i].Y > 0, $"page {i} sits on top of page 0");
            if (origins[i].Y == origins[0].Y)
            {
                Assert.True(origins[i].X >= origins[i - 1].X + sizes[i - 1].Width,
                    $"page {i} overlaps page {i - 1}");
            }
        }

        // A wide page in the first column must widen that column for the next one.
        IReadOnlyList<Point2D> wideFirst = PdfImporter.GridOrigins(new List<Size2D>
        {
            new(1200, 500),
            new(595, 842),
            new(595, 842),
        });

        Assert.True(wideFirst[1].Y > wideFirst[0].Y || wideFirst[1].X >= 1200,
            "a 1200pt-wide page must push its neighbour clear of it");
    }

    [Fact]
    public void SinglePageIsAtTheOrigin()
    {
        IReadOnlyList<Point2D> origins = PdfImporter.GridOrigins(new List<Size2D> { PageSizes.A4Portrait });

        Assert.Single(origins);
        Assert.Equal(new Point2D(0, 0), origins[0]);
    }
}
