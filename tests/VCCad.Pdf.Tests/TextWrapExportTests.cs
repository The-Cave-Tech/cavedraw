using System.Text;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Pdf.Tests;

/// <summary>
/// A text block wrapped into a frame must be exported wrapped.
///
/// Export used to honour only explicit newlines. A paragraph the editor wrapped over
/// several lines therefore came out on the page as one long line — the exported document
/// was not the document that was on screen, which is the fidelity rule this project
/// exists to keep.
/// </summary>
public class TextWrapExportTests
{
    [Fact]
    public void AFramedTextBlockIsDrawnAsSeveralLines()
    {
        if (!StandardFontFixture.Available) { return; }

        CadDocument doc = CadDocument.CreateDefault("Wrapped");
        var text = new TextItem { Origin = new Point2D(60, 120), FrameWidth = 100 };
        text.Runs.Add(new TextRun
        {
            Text = "The quick brown fox jumps over the lazy dog",
            FontSize = 12,
            FontFamily = "Helvetica",
        });
        doc.Artboards[0].Layers[0].AddItem(text);

        byte[] pdf = PdfDocumentExporter.Export(doc);

        int drawn = DrawnTextRuns(pdf);
        Assert.True(drawn >= 3, $"expected several drawn lines, found {drawn}");
    }

    [Fact]
    public void AnUnframedTextBlockIsStillDrawnAsOneLine()
    {
        if (!StandardFontFixture.Available) { return; }

        CadDocument doc = CadDocument.CreateDefault("Unwrapped");
        var text = new TextItem { Origin = new Point2D(60, 120) };
        text.Runs.Add(new TextRun
        {
            Text = "The quick brown fox",
            FontSize = 12,
            FontFamily = "Helvetica",
        });
        doc.Artboards[0].Layers[0].AddItem(text);

        byte[] pdf = PdfDocumentExporter.Export(doc);

        // No frame means no wrapping: the block is one line however long it is.
        Assert.Equal(1, DrawnTextRuns(pdf));
    }

    [Fact]
    public void RoundTrippingAWrappedBlockKeepsEveryWord()
    {
        if (!StandardFontFixture.Available) { return; }

        CadDocument doc = CadDocument.CreateDefault("RoundTrip");
        var text = new TextItem { Origin = new Point2D(60, 120), FrameWidth = 100 };
        text.Runs.Add(new TextRun
        {
            Text = "The quick brown fox jumps over the lazy dog",
            FontSize = 12,
            FontFamily = "Helvetica",
        });
        doc.Artboards[0].Layers[0].AddItem(text);

        CadDocument again = PdfImporter.Import(PdfDocumentExporter.Export(doc));
        string recovered = AllText(again);

        foreach (string word in new[] { "quick", "brown", "jumps", "lazy", "dog" })
        {
            Assert.Contains(word, recovered, StringComparison.Ordinal);
        }
    }

    private static string AllText(CadDocument document)
    {
        var builder = new StringBuilder();

        void Collect(IReadOnlyList<LayerItem> items)
        {
            foreach (LayerItem item in items)
            {
                switch (item)
                {
                    case TextItem text:
                        builder.Append(text.PlainText).Append(' ');
                        break;
                    case ArtGroup group:
                        Collect(group.Children);
                        break;
                }
            }
        }

        foreach (Artboard artboard in document.Artboards)
        {
            foreach (Layer layer in artboard.Layers)
            {
                Collect(layer.Children);
            }
        }

        Collect(document.Orphans.Children);
        return builder.ToString();
    }

    /// <summary>How many text-showing operators the content streams contain.</summary>
    private static int DrawnTextRuns(byte[] pdf)
    {
        var latin = new string(Encoding.Latin1.GetChars(pdf));
        int count = 0;

        // Match the keyword and its line ending. Searching for a bare "stream" also
        // matches inside "endstream", which walks the scan off the rails.
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
            count += System.Text.RegularExpressions.Regex.Matches(content, @"\bTj\b").Count;
        }

        return count;
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
