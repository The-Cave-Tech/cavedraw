using System.Text;
using VCCad.Core.Model;
using VCCad.Pdf;
using Xunit;

namespace VCCad.Pdf.Tests;

/// <summary>
/// Positioning inside a TJ array.
///
/// A TJ array interleaves strings with adjustments in thousandths of an em. They are not
/// decoration — the page-number table on a sewing pattern is drawn as
/// <c>[(1)-1130(2)-1118(3)]TJ</c>, where -1130 means "move 1.13 em before the next
/// digit". Concatenating the strings and dropping the numbers collapses that to "123"
/// bunched at the origin: the digits land in the wrong cell, and an extractor reads one
/// word where the file has three.
/// </summary>
public class TjPositioningTests
{
    /// <summary>
    /// A one-page PDF whose only content is the given TJ array, written the way
    /// Illustrator writes it: <c>1 Tf</c> with the real size carried by the text matrix.
    /// </summary>
    private static byte[] Pdf(string tj)
    {
        string content = $"BT /F1 1 Tf 26.4559 0 0 26.4559 72.4771 400 Tm {tj} ET";

        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
            "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /FirstChar 32 /LastChar 122 " +
            "/Widths [" + string.Join(" ", Enumerable.Repeat("556", 91)) + "] >>",
        };

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
            builder.Append($"{offset:D10} 00000 n \n");
        }

        builder.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(builder.ToString());
    }

    private static List<TextItem> TextItems(CadDocument document)
    {
        var items = new List<TextItem>();
        foreach (Artboard artboard in document.Artboards)
        {
            foreach (Layer layer in artboard.Layers)
            {
                items.AddRange(layer.Children.OfType<TextItem>());
            }
        }

        return items;
    }

    [Fact]
    public void AwidelySpacedTjArrayKeepsItsCellsApart()
    {
        CadDocument document = PdfImporter.Import(Pdf("[(1)-1130(2)-1118(3)]TJ"));
        List<TextItem> items = TextItems(document);

        // One block of three pieces rather than three blocks. The pieces sit a whole cell
        // apart, and that distance is carried on each piece's advance — so the block lays
        // out where the file put it, and an editor still sees one row of a table. Standing
        // them up as three separate objects said the same thing in a shape that made the
        // row impossible to select, move or restyle as a unit.
        TextItem item = Assert.Single(items);
        Assert.Equal(new[] { "1", "2", "3" }, item.Runs.Select(r => r.Text).ToArray());

        // The digits must be a whole cell apart, not bunched: the file advances ~1.13 em
        // plus the glyph's own width between them, so each piece is over 30pt wide.
        Assert.Equal(3, item.Runs.Count);
        Assert.True(item.Runs[0].AdvanceWidth > 30,
            $"expected a wide advance, got {item.Runs[0].AdvanceWidth:F1}pt");
        Assert.True(item.Runs[1].AdvanceWidth > 30,
            $"expected a wide advance, got {item.Runs[1].AdvanceWidth:F1}pt");

        double first = item.Runs[0].AdvanceWidth!.Value;
        double second = item.Runs[1].AdvanceWidth!.Value;
        Assert.Equal(first, second, 0.5);
    }

    [Fact]
    public void TightKerningInATjArrayStaysOneRun()
    {
        // Small adjustments are kerning. Splitting on them would turn a letter-spaced
        // line into one object per glyph, so they are deliberately folded away.
        CadDocument document = PdfImporter.Import(Pdf("[(AV)-40(AV)-35(AV)]TJ"));
        List<TextItem> items = TextItems(document);

        Assert.Single(items);
        Assert.Equal("AVAVAV", items[0].PlainText);
    }

    [Fact]
    public void AdjacentStringsWithNoAdjustmentStayTogether()
    {
        CadDocument document = PdfImporter.Import(Pdf("[(Hel)()(lo)]TJ"));
        List<TextItem> items = TextItems(document);

        Assert.Single(items);
        Assert.Equal("Hello", items[0].PlainText);
    }
}
