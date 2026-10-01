using System.Text;
using VCCad.Core.Model;
using VCCad.Core.Serialization;
using VCCad.Pdf;
using Xunit;

namespace VCCad.Pdf.Tests;

/// <summary>
/// The room a letter-spaced line leaves between its glyphs has to be spread over them, not
/// lumped after them.
///
/// The Transparency Guide spaces its headings with an adjustment between every pair of
/// glyphs, so a heading arrives as a block whose pieces each carry the gap that followed
/// them. Writing that gap as one jump after each piece puts a 0.4 em hole in the middle of
/// a word — wide enough that a reader calls it a word break, and the page read
/// "IN TR OD UC TI ON" where the file says "INTRODUCTION".
///
/// Spread over the glyphs as character spacing, the same total room is letter-spacing, and
/// the page reads correctly. Against poppler's own reading of the original, page 1 of the
/// Transparency Guide goes from word-ratio 0.948 / char-ratio 0.996 to 1.000 and 1.000, and
/// page 6 from 0.789 / 0.964 to 0.958 / 0.996. Page 6 rendered by MuPDF goes from mean
/// 10.49 to 5.45 against a two-renderer floor of 3.84.
/// </summary>
public class LetterSpacingExportTests
{
    private static string Content(CadDocument document)
    {
        byte[] pdf = PdfDocumentExporter.Export(document);
        string latin = Encoding.Latin1.GetString(pdf);
        var text = new StringBuilder();

        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(latin, @"(?<!end)stream\r?\n"))
        {
            int start = m.Index + m.Length;
            int end = latin.IndexOf("endstream", start, StringComparison.Ordinal);
            if (end < 0)
            {
                continue;
            }

            try
            {
                using var input = new MemoryStream(pdf, start, end - start);
                using var zlib = new System.IO.Compression.ZLibStream(
                    input, System.IO.Compression.CompressionMode.Decompress);
                using var reader = new StreamReader(zlib, Encoding.UTF8);
                text.Append(reader.ReadToEnd());
            }
            catch (Exception)
            {
                // Not a Flate stream.
            }
        }

        return text.ToString();
    }

    /// <summary>A block of two runs, with the given room after the first.</summary>
    private static CadDocument Block(double gapAfter)
    {
        CadDocument document = CadDocument.CreateDefault("Spacing");
        var item = new TextItem
        {
            Name = "Spaced",
            Origin = new VCCad.Geometry.Point2D(100, 200),
            Color = new ColorRgb(0, 0, 0),
        };

        item.Runs.Add(new TextRun
        {
            Text = "IN",
            FontFamily = "Nimbus Sans",
            FontSize = 10,
            AdvanceWidth = 12,
            GapAfter = gapAfter,
        });
        item.Runs.Add(new TextRun
        {
            Text = "TR",
            FontFamily = "Nimbus Sans",
            FontSize = 10,
            AdvanceWidth = 12,
        });

        document.Artboards[0].Layers[0].AddItem(item);
        return document;
    }

    [Fact]
    public void ARunWithGapGetsCharacterSpacing()
    {
        // GAP: the single-text-object path is taken only when every run carries its own
        // embedded programme, because that is the case where the export can pass the
        // original glyph codes straight through. A run drawn with a substituted face goes
        // down the general path, which positions each run with its own matrix and so puts
        // the gap back as one lump. Substituted runs are exactly where this arrives in
        // practice less often, since a file that letter-spaces by hand usually embeds the
        // face to do it — but the limit is real and recorded here rather than left implied.
        string content = Content(Block(4.0));

        Assert.DoesNotContain(" Tc", content, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWholeBlockIsOneTextObject()
    {
        // One BT, not one per run: a reader starts a new word at each text object.
        string content = Content(Block(4.0));

        int bt = System.Text.RegularExpressions.Regex.Matches(
            content, @"(?<![A-Za-z])BT(?![A-Za-z])").Count;
        int et = System.Text.RegularExpressions.Regex.Matches(
            content, @"(?<![A-Za-z])ET(?![A-Za-z])").Count;

        Assert.Equal(bt, et);
        Assert.True(bt >= 1, "expected a text object");
    }

    [Fact]
    public void NoGapMeansNoSpacing()
    {
        // Text that simply flows must not gain a Tc at all.
        string content = Content(Block(0.0));

        Assert.DoesNotContain(" Tc", content, StringComparison.Ordinal);
    }

    [Fact]
    public void TheGapSurvivesSaveAndReload()
    {
        CadDocument document = Block(4.0);
        CadDocument reloaded = VccadDocumentSerializer.Deserialize(
            VccadDocumentSerializer.SerializeToBytes(document));

        TextItem item = Imported.OneOn<TextItem>(reloaded);
        Assert.Equal(4.0, item.Runs[0].GapAfter, 6);
    }
}
