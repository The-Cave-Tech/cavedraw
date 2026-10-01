using System.Text;
using VCCad.Core.Model;
using VCCad.Pdf;
using Xunit;

namespace VCCad.Pdf.Tests;

/// <summary>
/// A colour change inside one PDF text object is a **run** boundary, not a block boundary.
///
/// A content stream states a colour with a separate operator between two show operations
/// (<c>1 0 0 rg (red) Tj 0 0 1 rg (blue) Tj</c>), so the text object the file wrote is still one object.
/// The importer used to start a new <see cref="TextItem"/> whenever the colour differed from the previous
/// run's and never set <see cref="TextRun.Color"/>, so that object arrived as one block per colour - the same
/// split the SVG reader made before #161, where a <c>tspan fill=...</c> was a block rather than a run.
/// Once <see cref="TextItem.ColourOf"/> is read by the canvas and the exporter, the split costs structure for
/// nothing: a two-colour line can no longer be selected, moved or restyled as the one object the file says it is.
///
/// This is the PDF half of #161, so the assertions are the ones that test makes on the model: one block, one
/// run per piece the file shows, and the colour the file gave each one answered through
/// <see cref="TextItem.ColourOf"/>.
///
/// Built the way the other content-importer tests build a file (see <see cref="TjPositioningTests"/>): a small
/// hand-written one-page PDF, so the construct under test is the whole document.
/// </summary>
public class TextRunColourImportTests
{
    private static readonly ColorRgb Red = new(1.0, 0.0, 0.0);

    private static readonly ColorRgb Blue = new(0.0, 0.0, 1.0);

    /// <summary>A one-page PDF whose only content is the given stream.</summary>
    private static byte[] Pdf(string content)
    {
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

    /// <summary>The text blocks of a page, wherever the importer's grouping put them.</summary>
    private static List<TextItem> TextItems(CadDocument document)
        => Imported.Page(document).OfType<TextItem>().ToList();

    /// <summary>
    /// **The block the file wrote stays one block, and every run carries the colour the file gave it.**
    ///
    /// Two <c>Tj</c> operations under one <c>BT</c>/<c>ET</c>, the second in another colour. Against the
    /// importer before the fix this is two <see cref="TextItem"/>s of one run each - so <see cref="Assert.Single"/>
    /// fails with "2", which is the shape of the defect rather than a missing colour.
    ///
    /// The block's own colour stays the first run's, and a run that differs carries its own, so
    /// <see cref="TextItem.ColourOf"/> answers with the file's paint for both. A colour change is still a new
    /// run and never merged into one, because the two pieces are one string in no file: the file shows them
    /// separately and the model keeps that.
    /// </summary>
    [Fact]
    public void AColourChangeMidLineStaysOneBlockWithTwoRunColours()
    {
        CadDocument document = PdfImporter.Import(Pdf(
            "BT /F1 12 Tf 1 0 0 rg 72 400 Td (red) Tj 0 0 1 rg (blue) Tj ET"));

        TextItem item = Assert.Single(TextItems(document));
        Assert.Equal(2, item.Runs.Count);
        Assert.Equal("red", item.Runs[0].Text);
        Assert.Equal("blue", item.Runs[1].Text);

        Assert.Equal(Red, item.Color);
        Assert.Equal(Red, item.ColourOf(item.Runs[0]));
        Assert.Equal(Blue, item.ColourOf(item.Runs[1]));
    }

    /// <summary>
    /// **The colour change is a run boundary across a split <c>TJ</c> array too, and the pieces are not merged.**
    ///
    /// A <c>TJ</c> array with a wide adjustment is deliberately broken into separately-placed pieces (see
    /// <see cref="TjPositioningTests"/>), and a colour change between two such arrays must not undo that or
    /// silently join the runs. The four pieces keep their own texts and the operator's colour is in force for
    /// every piece of the array it precedes, which is the only colour a <c>TJ</c> array can have: the format's
    /// grammar puts strings and numbers inside the array, so no colour operator can occur within one.
    /// </summary>
    [Fact]
    public void AColourChangeBetweenTjOperatorsKeepsThePiecesAndTheirColours()
    {
        CadDocument document = PdfImporter.Import(Pdf(
            "BT /F1 1 Tf 26.4559 0 0 26.4559 72.4771 400 Tm " +
            "1 0 0 rg [(1)-1130(2)]TJ 0 0 1 rg [(3)-1118(4)]TJ ET"));

        TextItem item = Assert.Single(TextItems(document));

        // Four runs, not one per colour and not one per array: the pieces the file placed stay separate.
        Assert.Equal(new[] { "1", "2", "3", "4" }, item.Runs.Select(r => r.Text).ToArray());

        Assert.Equal(Red, item.ColourOf(item.Runs[0]));
        Assert.Equal(Red, item.ColourOf(item.Runs[1]));
        Assert.Equal(Blue, item.ColourOf(item.Runs[2]));
        Assert.Equal(Blue, item.ColourOf(item.Runs[3]));
    }

    /// <summary>
    /// **A line that keeps one colour is unchanged by the fix.** One block, and no run grows a colour member it
    /// does not need - every run falls back to the block's own.
    /// </summary>
    [Fact]
    public void AOneColourLineCarriesNoPerRunColour()
    {
        CadDocument document = PdfImporter.Import(Pdf(
            "BT /F1 12 Tf 1 0 0 rg 72 400 Td (red) Tj (also red) Tj ET"));

        TextItem item = Assert.Single(TextItems(document));
        Assert.Equal(2, item.Runs.Count);
        Assert.All(item.Runs, run => Assert.Null(run.Color));
        Assert.All(item.Runs, run => Assert.Equal(Red, item.ColourOf(run)));
    }
}
