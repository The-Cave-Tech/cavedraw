using System.Text;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Pdf.Tests;

/// <summary>
/// **A per-character across offset reaches the page, one show per character.**
///
/// `TextRun.PositionOffsets` is the file's own statement - SVG's `y`/`dy` list - of where each character sits across
/// the line. The layout and the canvas honour it; the exporter could not, because a horizontal block goes through
/// the one-text-object path, which writes a single `Tm` and a single show for the whole block and has no way to say
/// that one character sits off the baseline. So a document that drew correctly on screen lost the offsets in the
/// product's own storage format, which is the fidelity rule this project exists to keep.
///
/// The assertion is the bytes: one show per character, and the baselines the file asked for rather than the one the
/// block was placed on.
/// </summary>
public class TextOffsetExportTests
{
    private static byte[] ExportWith(double[]? offsets)
    {
        CadDocument doc = CadDocument.CreateDefault("Offsets");
        var text = new TextItem { Origin = new Point2D(60, 120) };
        text.Runs.Add(new TextRun
        {
            Text = "abc",
            FontSize = 12,
            FontFamily = "Helvetica",
            PositionOffsets = offsets,
        });
        doc.Artboards[0].Layers[0].AddItem(text);
        return PdfDocumentExporter.Export(doc);
    }

    /// <summary>
    /// **The offsets are the page's, not just the screen's.** Three characters with `dy="0 5 10"` are drawn by three
    /// separate shows whose baselines are 5 apart in turn - and a block with no offsets still goes through the
    /// one-object path, because the skip is a condition and not a replacement.
    /// </summary>
    [Fact]
    public void APerCharacterOffsetIsDrawnPerCharacterAndOnItsOwnBaseline()
    {
        if (!StandardFontFixture.Available) { return; }

        string content = ContentOf(ExportWith(new[] { 0.0, 5.0, 10.0 }));

        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(content, @"\bTj\b").Count);

        List<double> baselines = Baselines(content);
        Assert.Equal(3, baselines.Count);
        Assert.Equal(5.0, Math.Abs(baselines[1] - baselines[0]), 6);
        Assert.Equal(5.0, Math.Abs(baselines[2] - baselines[1]), 6);

        // The same block with no list is still one show: the per-character route is taken for a reason, not always.
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(ContentOf(ExportWith(null)), @"\bTj\b").Count);
    }

    /// <summary>The text matrix's `y` for every show, in the order the content stream writes them.</summary>
    private static List<double> Baselines(string content)
    {
        var found = new List<double>();
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                     content, @"([-\d.]+) ([-\d.]+) ([-\d.]+) ([-\d.]+) ([-\d.]+) ([-\d.]+) Tm"))
        {
            found.Add(double.Parse(m.Groups[6].Value, System.Globalization.CultureInfo.InvariantCulture));
        }

        return found;
    }

    /// <summary>The inflated page content stream.</summary>
    private static string ContentOf(byte[] pdf)
    {
        var latin = new string(Encoding.Latin1.GetChars(pdf));
        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(latin, @"(?<!end)stream\r?\n"))
        {
            int start = m.Index + m.Length;
            int end = latin.IndexOf("endstream", start, StringComparison.Ordinal);
            if (end < 0)
            {
                break;
            }

            string content = Inflate(pdf, start, end - start);
            if (content.Contains("BT", StringComparison.Ordinal))
            {
                return content;
            }
        }

        return string.Empty;
    }

    private static string Inflate(byte[] pdf, int offset, int length)
    {
        try
        {
            using var input = new MemoryStream(pdf, offset, length);
            using var zlib = new System.IO.Compression.ZLibStream(
                input, System.IO.Compression.CompressionMode.Decompress);
            using var reader = new StreamReader(zlib, Encoding.Latin1);
            return reader.ReadToEnd();
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }
}
