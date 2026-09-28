using System.Text;
using VCCad.Core.Model;
using VCCad.Pdf;
using Xunit;

namespace VCCad.Pdf.Tests;

/// <summary>
/// A form XObject's /BBox clips everything it draws.
///
/// It is the parent object's rectangle: the file says "this artwork belongs inside this box",
/// and a viewer that ignores it lets the artwork spill across the page. The importer used the
/// form's /Matrix and its content and never its box.
///
/// Neither sample can show it. The LILLIE file's boxes are a page and one line of header
/// text, both of which its content already fits inside, so the render is identical either
/// way. The behaviour is pinned here with a box the content overflows.
/// </summary>
public class FormBBoxTests
{
    /// <summary>
    /// A page whose form draws a wide path inside a box a tenth its width, so whether the
    /// box was applied is unmistakable.
    /// </summary>
    private static byte[] FormWithBox(string bbox)
    {
        // The form's content: a 200-wide bar. The /BBox is passed in.
        string formContent = "0 0 0 rg 0 0 200 40 re f";
        string pageContent = "q /Fm0 Do Q";

        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                + "/Resources << /XObject << /Fm0 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {pageContent.Length} >>\nstream\n{pageContent}\nendstream",
            $"<< /Type /XObject /Subtype /Form /BBox [{bbox}] "
                + $"/Resources << >> /Length {formContent.Length} >>\n"
                + $"stream\n{formContent}\nendstream",
        };

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

    private static string Inflated(byte[] pdf)
    {
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

                if (stream.Contains(" W", StringComparison.Ordinal) ||
                    stream.Contains("\nW\n", StringComparison.Ordinal) ||
                    stream.StartsWith("W ", StringComparison.Ordinal) ||
                    stream.Contains("BT", StringComparison.Ordinal))
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

    [Fact]
    public void AFormIsClippedToItsBox()
    {
        // The form draws 200 points wide; its box is 20.
        CadDocument document = PdfImporter.Import(FormWithBox("0 0 20 40"));
        string content = Inflated(PdfDocumentExporter.Export(document));

        Assert.Contains("W", content, StringComparison.Ordinal);

        // The box's own edge, as a number rather than as a substring: "20" inside "200" is
        // the bar's width and not the box's.
        List<double> numbers = System.Text.RegularExpressions.Regex
            .Matches(content, @"(?<![\d.])(\d+(?:\.\d+)?)(?![\d.])")
            .Select(m => double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))
            .ToList();

        Assert.Contains(numbers, n => Math.Abs(n - 20) < 0.01);
    }

    [Fact]
    public void TheBoxBecomesAClipOnTheItemsItBounds()
    {
        CadDocument document = PdfImporter.Import(FormWithBox("0 0 20 40"));
        PathItem bar = document.Artboards[0].Layers[0].Children.OfType<PathItem>().Single();

        Assert.True(bar.IsClipped, "the form's box should be a clip on what it draws");

        // The clip is the box: 20 wide, 40 tall, from the origin.
        ClipSpec clip = bar.Clips[0];
        double width = clip.SubPaths[0].Nodes.Max(n => n.Anchor.X);
        Assert.Equal(20.0, width, 2);
    }

    [Fact]
    public void ABoxThatAlreadyFitsItsContentChangesNothingVisible()
    {
        // The page-sized case, which is what most of the corpus looks like.
        CadDocument document = PdfImporter.Import(FormWithBox("0 0 612 792"));
        PathItem bar = document.Artboards[0].Layers[0].Children.OfType<PathItem>().Single();

        Assert.True(bar.IsClipped);
        double right = bar.Clips[0].SubPaths[0].Nodes.Max(n => n.Anchor.X);
        Assert.Equal(612.0, right, 2);
    }
}
