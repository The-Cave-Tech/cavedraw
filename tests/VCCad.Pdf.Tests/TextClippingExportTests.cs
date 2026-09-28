using System.Text;
using VCCad.Core.Model;
using VCCad.Pdf;
using Xunit;

namespace VCCad.Pdf.Tests;

/// <summary>
/// Text is clipped like anything else.
///
/// It was not. Paths and images are painted through PaintItem, which emits the item's clips,
/// and text has its own loop in the page writer — so a clipped label was exported unclipped,
/// showing text the file had hidden.
///
/// Neither sample in the corpus can show this: the Transparency Guide clips all 1,648 of its
/// text items, but to the page box, so nothing is actually hidden and the render is
/// identical either way. The fix is visible only in the operator counts, 7,093 W to 9,399,
/// which is why the behaviour is pinned here with a clip that bites.
/// </summary>
public class TextClippingExportTests
{
    /// <summary>
    /// The page's content streams, concatenated.
    ///
    /// Font programmes are excluded deliberately. They are decompressed streams too, and
    /// their bytes contain the letter W often enough that counting operators across every
    /// stream gave 199 for a page with one text object — which is how a test can look like
    /// it measures something and measure decompressed font data instead.
    /// </summary>
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
                string stream = reader.ReadToEnd();

                if (stream.Contains(" Tf", StringComparison.Ordinal) &&
                    stream.Contains("Tj", StringComparison.Ordinal))
                {
                    text.Append(stream);
                }
            }
            catch (Exception)
            {
                // Not a Flate stream.
            }
        }

        return text.ToString();
    }

    /// <summary>A text block limited to a small square far from the page corner.</summary>
    private static CadDocument ClippedText()
    {
        CadDocument document = CadDocument.CreateDefault("ClippedText");
        var item = new TextItem
        {
            Name = "Labelled",
            Origin = new VCCad.Geometry.Point2D(100, 200),
            Color = new ColorRgb(0, 0, 0),
        };

        item.Runs.Add(new TextRun { Text = "AB", FontFamily = "Nimbus Sans", FontSize = 12 });

        // Deliberately not at the page corner. Every page already carries a page-box clip
        // whose corners are the page size, so a clip at 0,0 is indistinguishable from it —
        // a first version of this test asserted "the scope contains W and n", passed with
        // the clip disabled, and was worth nothing. These edges appear nowhere else.
        var clip = new ClipSpec();
        var sub = new SubPath { IsClosed = true };
        sub.Nodes.Add(new PathNode(new VCCad.Geometry.Point2D(312, 444)));
        sub.Nodes.Add(new PathNode(new VCCad.Geometry.Point2D(362, 444)));
        sub.Nodes.Add(new PathNode(new VCCad.Geometry.Point2D(362, 494)));
        sub.Nodes.Add(new PathNode(new VCCad.Geometry.Point2D(312, 494)));
        clip.SubPaths.Add(sub);
        item.Clips.Add(clip);

        document.Artboards[0].Layers[0].AddItem(item);
        return document;
    }

    /// <summary>
    /// The operators between the nearest <c>q</c> and the text object, which is the scope
    /// the text is painted inside.
    /// </summary>
    private static IReadOnlyList<string> ScopeBeforeText(string content)
    {
        string[] lines = content.Replace("\r\n", "\n").Split('\n')
            .Select(l => l.Trim()).ToArray();

        int bt = Array.IndexOf(lines, "BT");
        Assert.True(bt >= 0, "expected a text object in the export");

        var scope = new List<string>();
        for (int i = bt - 1; i >= 0; i--)
        {
            if (lines[i] == "q")
            {
                break;
            }

            scope.Insert(0, lines[i]);
        }

        return scope;
    }

    /// <summary>
    /// The numbers on the lines between the nearest <c>q</c> and the text object — the scope
    /// the text is painted inside.
    /// </summary>
    private static List<double> ScopeNumbersBeforeText(string content)
    {
        string[] lines = content.Replace("\r\n", "\n").Split('\n')
            .Select(l => l.Trim()).ToArray();

        int bt = Array.IndexOf(lines, "BT");
        Assert.True(bt >= 0, "expected a text object in the export");

        var scope = new List<string>();
        for (int i = bt - 1; i >= 0; i--)
        {
            if (lines[i] == "q")
            {
                break;
            }

            scope.Insert(0, lines[i]);
        }

        // Parsed as numbers, not searched for as substrings: "444" inside "1444.5" is not the
        // edge of anything, and two earlier versions of this test were fooled by exactly that.
        return System.Text.RegularExpressions.Regex
            .Matches(string.Join(' ', scope), @"-?\d+(?:\.\d+)?")
            .Select(m => double.Parse(m.Value, System.Globalization.CultureInfo.InvariantCulture))
            .ToList();
    }

    [Fact]
    public void AClipOnTextIsWrittenOut()
    {
        List<double> scope = ScopeNumbersBeforeText(Content(ClippedText()));

        // The clip is a 50pt square from (312, 444), written in model coordinates: the page's
        // flip is a separate cm at the top of the stream, so the outline is emitted as it is
        // stored rather than pre-flipped here.
        Assert.Contains(scope, n => Math.Abs(n - 312) < 0.01);
        Assert.Contains(scope, n => Math.Abs(n - 362) < 0.01);
        Assert.Contains(scope, n => Math.Abs(n - 444) < 0.01);
        Assert.Contains(scope, n => Math.Abs(n - 494) < 0.01);
    }

    [Fact]
    public void UnclippedTextIsNotWrappedInAClip()
    {
        CadDocument document = CadDocument.CreateDefault("Plain");
        var item = new TextItem
        {
            Name = "Plain",
            Origin = new VCCad.Geometry.Point2D(100, 200),
            Color = new ColorRgb(0, 0, 0),
        };
        item.Runs.Add(new TextRun { Text = "AB", FontFamily = "Nimbus Sans", FontSize = 12 });
        document.Artboards[0].Layers[0].AddItem(item);

        Assert.DoesNotContain("W", ScopeBeforeText(Content(document)));
    }

    [Fact]
    public void TheClipIsClosedAgainAfterTheText()
    {
        string content = Content(ClippedText());
        string[] lines = content.Replace("\r\n", "\n").Split('\n')
            .Select(l => l.Trim()).ToArray();

        int q = Array.IndexOf(lines, "q");
        Assert.True(q >= 0, "expected the clip to open a scope");

        // q and Q must balance, or every following item inherits the clip.
        int opens = lines.Count(l => l == "q");
        int closes = lines.Count(l => l == "Q");
        Assert.Equal(opens, closes);
    }
}
