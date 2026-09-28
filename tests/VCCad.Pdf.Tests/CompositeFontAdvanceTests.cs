using System.Text;
using VCCad.Core.Model;
using VCCad.Pdf;
using Xunit;

namespace VCCad.Pdf.Tests;

/// <summary>
/// Advance widths for composite (Type0) fonts.
///
/// A composite font has no <c>/Widths</c>. Its widths live in the descendant CIDFont as
/// <c>/W</c> with a <c>/DW</c> default, and the importer returned null for them — so every
/// run in a document of CID fonts had no advance at all. Without a width the model cannot
/// lay text out, and the Transparency Guide's page 6, whose fourteen fonts are all Type0
/// and which draws one glyph per Tj, came out as a scatter of separately positioned
/// fragments: word-ratio 0.402 and char-ratio 0.244 against the original.
///
/// With the widths read, that page measures 0.783 and 0.963.
/// </summary>
public class CompositeFontAdvanceTests
{
    /// <summary>
    /// A one-page PDF whose font is a Type0 with the given <c>/W</c> array and
    /// <c>/DW</c> default, drawing the given hex string with it.
    /// </summary>
    private static byte[] CompositePdf(string wArray, string dw, string hex)
    {
        string content = $"BT /F1 10 Tf 100 700 Td <{hex}> Tj ET";

        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                + "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type0 /BaseFont /Test /Encoding /Identity-H "
                + "/DescendantFonts [6 0 R] >>",
            "<< /Type /Font /Subtype /CIDFontType2 /BaseFont /Test "
                + "/CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> "
                + $"/DW {dw} /W [{wArray}] >>",
        };

        return Assemble(bodies);
    }

    /// <summary>Assembles numbered objects into a PDF with a correct cross-reference.</summary>
    private static byte[] Assemble(List<string> bodies)
    {
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

    private static double? Advance(string wArray, string dw, string hex)
    {
        CadDocument document = PdfImporter.Import(CompositePdf(wArray, dw, hex));
        TextItem item = document.Artboards[0].Layers[0].Children.OfType<TextItem>().Single();
        return item.Runs[0].AdvanceWidth;
    }

    [Fact]
    public void WidthsFromTheRangeFormAreUsed()
    {
        // Codes 1..3 are 500/1000 em each, so three of them at 10pt is 15.
        Assert.Equal(15.0, Advance("1 3 500", "1000", "000100020003")!.Value, 3);
    }

    [Fact]
    public void WidthsFromTheListFormAreUsed()
    {
        // Code 1 is 200, code 2 is 700, code 3 is 300: 1.2 em at 10pt.
        Assert.Equal(12.0, Advance("1 [200 700 300]", "1000", "000100020003")!.Value, 3);
    }

    [Fact]
    public void CodesOutsideTheTableFallBackToTheDefault()
    {
        // Only code 1 is listed at 200; codes 2 and 3 take /DW, which is 400 here.
        Assert.Equal(10.0, Advance("1 [200]", "400", "000100020003")!.Value, 3);
    }

    [Fact]
    public void TwoBytesToACodeNotOnePerCharacter()
    {
        // One code, 0001, at 1000/1000 em: 10 points. Read as two characters it would be
        // two codes and come out at 20.
        Assert.Equal(10.0, Advance("1 1 1000", "1000", "0001")!.Value, 3);
    }

    [Fact]
    public void ASimpleFontIsStillMeasuredFromItsWidthsArray()
    {
        // The composite path must not swallow the simple case: a Type1 font with /Widths
        // is measured from that array, not from a descendant it does not have.
        string content = "BT /F1 10 Tf 100 700 Td (AB) Tj ET";
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                + "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding "
                + "/FirstChar 65 /LastChar 66 /Widths [600 400] >>",
        };

        CadDocument document = PdfImporter.Import(Assemble(bodies));
        TextItem item = document.Artboards[0].Layers[0].Children.OfType<TextItem>().Single();

        // A (600) plus B (400) at 10pt is 10 points.
        Assert.Equal(10.0, item.Runs[0].AdvanceWidth!.Value, 3);
    }
}
