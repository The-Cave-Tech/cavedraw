using System.Text;
using VCCad.Core.Model;
using VCCad.Pdf;
using Xunit;

namespace VCCad.Pdf.Tests;

/// <summary>
/// Showing text moves the pen, and the next show operation continues from there.
///
/// A line is often drawn as several show operations without a new text matrix — the usual
/// shape when the font changes mid-line, for a trademark symbol or a product name. A viewer
/// carries the pen across them; an importer that reads each piece at the matrix alone puts
/// every piece back at the line's start.
///
/// The Transparency Guide lays its page-2 legal notice out that way, and reading the page
/// back gave two lines interleaved — word-ratio 0.807 against the original, char-ratio
/// 0.538. Advancing the matrix after each show operation takes it to 1.000 and 1.000, and
/// pages 3 and 7 improve with it.
/// </summary>
public class TextMatrixAdvanceTests
{
    /// <summary>A page whose one line changes font halfway through, with no new Tm.</summary>
    private static byte[] TwoFontsOneLinePdf()
    {
        string content =
            "BT /F1 10 Tf 100 700 Td (AB) Tj /F2 10 Tf (CD) Tj ET";

        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                + "/Resources << /Font << /F1 5 0 R /F2 6 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding "
                + "/FirstChar 65 /LastChar 70 /Widths [600 600 600 600 600 600] >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Courier /Encoding /WinAnsiEncoding "
                + "/FirstChar 65 /LastChar 70 /Widths [500 500 500 500 500 500] >>",
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
        return Encoding.Latin1.GetBytes(builder.ToString());
    }

    private static List<TextItem> Items()
        => PdfImporter.Import(TwoFontsOneLinePdf())
            .Artboards[0].Layers[0].Children.OfType<TextItem>()
            .OrderBy(t => t.Origin.X)
            .ToList();

    [Fact]
    public void TheSecondShowOperationContinuesTheLine()
    {
        List<TextItem> items = Items();

        // One block, not two: the second piece starts exactly where the first ended, which
        // is what continuing a line means, so the two merge into the line they are.
        TextItem item = Assert.Single(items);
        Assert.Equal(100.0, item.Origin.X, 2);
        Assert.Equal(2, item.Runs.Count);
        Assert.Equal(12.0, item.Runs[0].AdvanceWidth!.Value, 2);
    }

    [Fact]
    public void TheAdvanceUsesTheFontTheTextWasSetIn()
    {
        // The faces differ, so the pieces stay separate runs with their own metrics even
        // though they are one block.
        TextItem item = Assert.Single(Items());
        Assert.Equal("AB", item.Runs[0].Text);
        Assert.Equal("CD", item.Runs[1].Text);
    }

    [Fact]
    public void TheLineReadsInOrder()
    {
        // Same panel, so the piece drawn second is the one further along.
        List<TextItem> items = Items();
        string reading = string.Concat(items.Select(t => t.PlainText));
        Assert.Equal("ABCD", reading);
    }

    [Fact]
    public void AnExplicitMatrixStillWinsOverThePen()
    {
        // A new Tm is a new start, so the piece after it does not continue anything.
        string content = "BT /F1 10 Tf 100 700 Td (AB) Tj 10 0 Td (CD) Tj ET";
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                + "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding "
                + "/FirstChar 65 /LastChar 70 /Widths [600 600 600 600 600 600] >>",
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

        List<TextItem> items = PdfImporter.Import(Encoding.Latin1.GetBytes(builder.ToString()))
            .Artboards[0].Layers[0].Children.OfType<TextItem>()
            .OrderBy(t => t.Origin.X)
            .ToList();

        // Td moves relative to the *line* matrix, not the pen: 100 + 10.
        Assert.Equal(110.0, items[1].Origin.X, 2);
    }
}
