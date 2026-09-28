using VCCad.Core.Model;
using VCCad.Pdf;
using Xunit;

namespace VCCad.Pdf.Tests;

/// <summary>
/// Text that a content stream clips.
///
/// A tiled pattern draws each label once per sheet it touches, translated, and lets
/// the clip show only the part belonging to that sheet. Keeping the run whole makes
/// the label spill past the artboard edge — which is what "TEMI BOW BUSTIER" did on
/// the A4 pattern — so the run keeps only the characters visible on this sheet.
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
        return document.Artboards
            .SelectMany(a => a.Layers)
            .SelectMany(l => l.Children)
            .OfType<TextItem>()
            .FirstOrDefault();
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
    public void TextRunningPastTheClipKeepsOnlyTheVisibleCharacters()
    {
        // 20pt Helvetica: the model advances 12pt per character (0.6 x size), so ten
        // characters starting at x=150 reach x=270. The clip ends at x=200, which
        // covers characters 0..3 (150-198); character 4 starts at 198 and is only 2pt
        // visible, so it belongs to the neighbouring sheet.
        TextItem? text = ImportSingleText(BuildClippedTextPdf("ABCDEFGHIJ", 150, 200));

        Assert.NotNull(text);
        Assert.Equal("ABCD", text!.PlainText);

        // The surviving characters did not move: the first one was already visible.
        Assert.Equal(150.0, text.Origin.X, 1);
    }

    [Fact]
    public void TextOverlappingTheClipEdgeIsShiftedOntoItsFirstVisibleCharacter()
    {
        // Starting at x=-50, characters 0..3 are off the left edge. Character 4 begins
        // at -50 + 4*12 = -2 with only 10pt of its 12pt inside, so digit 4 is the first
        // substantially visible one and the origin moves onto it.
        TextItem? text = ImportSingleText(BuildClippedTextPdf("ABCDEFGHIJ", -50, 300));

        Assert.NotNull(text);
        Assert.DoesNotContain("ABCD", text!.PlainText);
        Assert.True(text.Origin.X > -12, $"origin should have advanced, was {text.Origin.X}");
        Assert.True(text.Origin.X <= 10, $"origin should still start at the edge, was {text.Origin.X}");
    }

    [Fact]
    public void TextEntirelyOutsideTheClipIsRemoved()
    {
        TextItem? text = ImportSingleText(BuildClippedTextPdf("ABCDEFGHIJ", 900, 300));

        Assert.Null(text);
    }

    [Fact]
    public void TrimmedRunKeepsItsAdvanceInProportion()
    {
        // A trimmed run must not stretch: the advance follows the kept characters.
        TextItem? text = ImportSingleText(BuildClippedTextPdf("ABCDEFGHIJ", 150, 200));

        Assert.NotNull(text);
        TextRun run = Assert.Single(text!.Runs);
        Assert.NotNull(run.AdvanceWidth);
        Assert.Equal(12.0 * run.Text.Length, run.AdvanceWidth!.Value, 1);
    }
}
