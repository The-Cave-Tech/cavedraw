using System.Text;
using VCCad.Core.Model;
using VCCad.Pdf;
using Xunit;

namespace VCCad.Pdf.Tests;

/// <summary>
/// The text-state operators that change how wide a run is or where it sits.
///
/// <c>Tc</c> adds space after every glyph, <c>Tw</c> after every word space, <c>Tz</c>
/// scales the whole line horizontally, and <c>Ts</c> lifts it off the baseline. A run
/// measured without them is not the run the file drew — and because the neutral values are
/// 0, 0, 100 and 0, a file that uses them normally looks perfectly fine until one uses
/// something else. They are part of the graphics state, so <c>q</c>/<c>Q</c> carry them.
/// </summary>
public class TextStyleTests
{
    /// <summary>
    /// Every glyph 600/1000 em wide, so an advance is predictable: five glyphs at 10pt is
    /// 30 points before any spacing.
    /// </summary>
    private static byte[] Pdf(string content)
    {
        var widths = new StringBuilder();
        for (int code = 32; code <= 126; code++)
        {
            widths.Append(code == 32 ? "600" : "600").Append(' ');
        }

        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                + "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding "
                + $"/FirstChar 32 /LastChar 126 /Widths [{widths.ToString().Trim()}] >>",
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
            builder.Append($"{offset:0000000000} 00000 n \n");
        }

        builder.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\n");
        builder.Append($"startxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(builder.ToString());
    }

    private static TextItem Import(string textOps, string show = "(ABCDE) Tj")
    {
        CadDocument document = PdfImporter.Import(
            Pdf($"BT /F1 10 Tf {textOps} 100 700 Td {show} ET"));
        return Imported.OneOn<TextItem>(document);
    }

    [Fact]
    public void WithoutSpacingTheAdvanceIsJustTheGlyphs()
    {
        TextItem item = Import(string.Empty);

        // Five glyphs at 600/1000 em, 10pt: 5 * 0.6 * 10 = 30.
        Assert.Equal(30.0, item.Runs[0].AdvanceWidth!.Value, 3);
    }

    [Fact]
    public void CharacterSpacingWidensEveryGlyph()
    {
        TextItem item = Import("3 Tc");

        // 30 plus five glyphs at 3 points each.
        Assert.Equal(45.0, item.Runs[0].AdvanceWidth!.Value, 3);
    }

    [Fact]
    public void WordSpacingWidensOnlyTheSpaces()
    {
        // "A B C": five glyph widths plus two spaces.
        TextItem item = Import("7 Tw", show: "(A B C) Tj");

        // 5 * 0.6 * 10 = 30, plus 2 * 7 = 14.
        Assert.Equal(44.0, item.Runs[0].AdvanceWidth!.Value, 3);
    }

    [Fact]
    public void HorizontalScaleStretchesTheWholeLine()
    {
        TextItem item = Import("50 Tz");

        Assert.Equal(15.0, item.Runs[0].AdvanceWidth!.Value, 3);
    }

    [Fact]
    public void CharacterSpacingIsScaledToo()
    {
        TextItem item = Import("3 Tc 50 Tz");

        // Spacing is in unscaled text space, so the scale applies to it as well:
        // (30 + 15) * 0.5.
        Assert.Equal(22.5, item.Runs[0].AdvanceWidth!.Value, 3);
    }

    [Fact]
    public void RiseLiftsTheTextOffTheBaseline()
    {
        TextItem flat = Import(string.Empty);
        TextItem raised = Import("8 Ts");

        // The model stores the block's top-left, which sits one ascent above the
        // baseline; rise moves the baseline, so the top moves with it.
        Assert.True(raised.Origin.Y < flat.Origin.Y,
            $"rise should move the text up, but {raised.Origin.Y} is not above {flat.Origin.Y}");
        Assert.Equal(8.0, flat.Origin.Y - raised.Origin.Y, 3);
    }

    [Fact]
    public void SpacingAlsoAppliesThroughATjArray()
    {
        // TJ is the form real files use, and it has its own measurement path.
        TextItem item = Import("4 Tc", show: "[(ABCDE)] TJ");

        Assert.Equal(50.0, item.Runs[0].AdvanceWidth!.Value, 3);
    }

    [Fact]
    public void TheTextStateSurvivesAndIsRestoredByQandQ()
    {
        // Set spacing inside a q/Q pair: after the Q it must be back to nothing.
        CadDocument document = PdfImporter.Import(Pdf(
            "BT /F1 10 Tf 100 700 Td (ABCDE) Tj ET "
            + "q BT /F1 10 Tf 5 Tc 100 600 Td (ABCDE) Tj ET Q "
            + "BT /F1 10 Tf 100 500 Td (ABCDE) Tj ET"));

        List<TextItem> items = document.Artboards[0].Layers[0].Children.OfType<TextItem>().ToList();

        Assert.Equal(30.0, items[0].Runs[0].AdvanceWidth!.Value, 3);
        Assert.Equal(55.0, items[1].Runs[0].AdvanceWidth!.Value, 3);

        // The third line is outside the pair, so the spacing set inside it is gone.
        Assert.Equal(30.0, items[2].Runs[0].AdvanceWidth!.Value, 3);
    }
}
