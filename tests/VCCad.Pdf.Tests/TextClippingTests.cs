using VCCad.Core.Model;
using VCCad.Pdf;
using Xunit;

namespace VCCad.Pdf.Tests;

/// <summary>
/// Text that a content stream clips.
///
/// A tiled pattern draws each label once per sheet it touches, translated, and lets the page edge
/// show only the part belonging to that sheet. The importer keeps the run **whole** and stores the
/// clip with it; the canvas clips to the artboard when it draws.
///
/// This class used to assert the other approach - trim the run to the visible characters, and drop
/// it entirely when the clip missed it. That was tried and it butchered the labels: removing
/// characters invalidates the embedded-font glyph mapping (a glyph id per character no longer lines
/// up with the string), and a per-character advance estimate cuts in the wrong place. AGENTS.md §9
/// records the decision and `SamplePatternTests` pins the invariant; these tests now pin it too,
/// rather than failing against behaviour that was deliberately removed.
/// </summary>
public class TextClippingTests
{
    /// <summary>
    /// A one-page PDF with a single clipped text run. <paramref name="clipWidth"/>
    /// limits the visible area; the text starts at <paramref name="textX"/>.
    /// </summary>
    private static byte[] BuildClippedTextPdf(string text, double textX, double clipWidth)
    {
        const int pageHeight = 400;
        var assembler = new PdfAssembler();
        int catalog = assembler.Allocate();
        int pages = assembler.Allocate();
        int page = assembler.Allocate();
        int content = assembler.Allocate();
        int font = assembler.Allocate();

        string stream =
            $"q 0 0 {clipWidth} {pageHeight} re W n " +
            $"BT /F1 20 Tf 1 0 0 1 {textX} 200 Tm ({text}) Tj ET Q";

        assembler.SetBody(font, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        assembler.SetBody(content, $"<< /Length {stream.Length} >>\nstream\n{stream}\nendstream");
        assembler.SetBody(page,
            $"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 400 {pageHeight}] " +
            $"/Resources << /Font << /F1 {font} 0 R >> >> /Contents {content} 0 R >>");
        assembler.SetBody(pages, $"<< /Type /Pages /Kids [{page} 0 R] /Count 1 >>");
        assembler.SetBody(catalog, $"<< /Type /Catalog /Pages {pages} 0 R >>");
        return assembler.Serialize(catalog);
    }

    private static TextItem? ImportSingleText(byte[] pdf)
    {
        CadDocument document = PdfImporter.Import(pdf);
        // Through the masking groups, which is where a clipped run now lives.
        return Imported.Everything(document.Artboards[0]).OfType<TextItem>().FirstOrDefault();
    }

    [Fact]
    public void TextWhollyInsideTheClipIsUntouched()
    {
        // The text starts at x=20 and the clip runs to x=350: everything is visible.
        TextItem? text = ImportSingleText(BuildClippedTextPdf("ABCDEFGHIJ", 20, 350));

        Assert.NotNull(text);
        Assert.Equal("ABCDEFGHIJ", text!.PlainText);
    }

    [Fact]
    public void TextRunningPastTheClipKeepsItsWholeStringAndItsPlace()
    {
        // Ten characters at 20pt reach x=270; the clip stops at 200. The run is kept as authored -
        // all ten characters, still starting at 150 - and the clip goes with it.
        TextItem? text = ImportSingleText(BuildClippedTextPdf("ABCDEFGHIJ", 150, 200));

        Assert.NotNull(text);
        Assert.Equal("ABCDEFGHIJ", text!.PlainText);
        Assert.Equal(150.0, text.Origin.X, 1);
        Assert.True(text.IsClipped, "the clip must be carried on the item");
    }

    [Fact]
    public void TextOverlappingTheClipEdgeKeepsItsWholeStringAndOrigin()
    {
        // Starting at x=-50, the first characters are off the left edge. Nothing is shifted onto
        // the first "visible" character: the run stays where the file put it and the page clips it.
        TextItem? text = ImportSingleText(BuildClippedTextPdf("ABCDEFGHIJ", -50, 300));

        Assert.NotNull(text);
        Assert.Equal("ABCDEFGHIJ", text!.PlainText);
        Assert.Equal(-50.0, text.Origin.X, 1);
        Assert.True(text.IsClipped);
    }

    [Fact]
    public void TextEntirelyOutsideTheClipIsStillImported()
    {
        // The clip misses it here, but the clip is a rendering concern: the other sheet of a tiled
        // pattern is exactly this run translated, and dropping it loses the label from the document.
        TextItem? text = ImportSingleText(BuildClippedTextPdf("ABCDEFGHIJ", 900, 300));

        Assert.NotNull(text);
        Assert.Equal("ABCDEFGHIJ", text!.PlainText);
        Assert.Equal(900.0, text.Origin.X, 1);
        Assert.True(text.IsClipped);
    }

    [Fact]
    public void AClippedRunStaysOneRunWithItsWholeString()
    {
        // The rejected approach rewrote the run: characters trimmed, or the run split into one
        // piece per visible span. Either leaves the string and its per-character codes out of step,
        // which is what breaks the exporter's pass-through of an embedded programme. This is the
        // shape that keeps them lined up.
        TextItem? text = ImportSingleText(BuildClippedTextPdf("ABCDEFGHIJ", 150, 200));

        Assert.NotNull(text);
        TextRun run = Assert.Single(text!.Runs);
        Assert.Equal("ABCDEFGHIJ", run.Text);
    }
}
