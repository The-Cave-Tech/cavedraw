using System.Text;
using VCCad.Core.Model;
using VCCad.Pdf;
using Xunit;

namespace VCCad.Pdf.Tests;

/// <summary>
/// CMYK ink values survive import and reach the export.
///
/// The model stores colours as RGB, but a viewer colour-manages DeviceCMYK rather than
/// applying the naive <c>(1-c)(1-k)</c> the model uses, so the conversion cannot be
/// undone: the sample's <c>k=0.3</c> grey renders as 189/255, not the 178 the formula
/// gives, and <c>k=1</c> renders as (35,31,32), not black. The ink values are therefore
/// kept as the file set them and written back out.
///
/// Both spellings matter. <c>k</c>/<c>K</c> are the direct form, but a content stream may
/// equally use <c>CS</c>/<c>SCN</c>, and a first version maintained the ink state only for
/// the direct operators — so a stale black from an earlier <c>K</c> was written onto every
/// line the file coloured the indirect way, and every line came out black.
/// </summary>
public class CmykImportTests
{
    private static byte[] Pdf(string content, string resources = "<< >>")
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources {resources} /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
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
        return Encoding.ASCII.GetBytes(builder.ToString());
    }

    private static IReadOnlyList<PathItem> Paths(byte[] pdf)
        => PdfImporter.Import(pdf).Artboards[0].Layers[0].Children.OfType<PathItem>().ToList();

    [Fact]
    public void DirectKOperatorsAreCaptured()
    {
        // A red fill and a black stroke, both in the direct spelling.
        byte[] pdf = Pdf("0.008 0.141 0.855 0 k 0 0 0 1 K 10 700 200 80 re B");

        PathItem path = Paths(pdf).Single();

        Assert.Equal(new[] { 0.008, 0.141, 0.855, 0.0 }, path.SourceFillCmyk);
        Assert.Equal(new[] { 0.0, 0.0, 0.0, 1.0 }, path.SourceStrokeCmyk);
    }

    [Fact]
    public void IndirectCsScnOperatorsAreCapturedToo()
    {
        // The same colours through CS/SCN, which names the space indirectly.
        byte[] pdf = Pdf(
            "/DeviceCMYK CS /DeviceCMYK cs 0.008 0.141 0.855 0 scn 0 0 0 1 SCN 10 700 200 80 re B");

        PathItem path = Paths(pdf).Single();

        Assert.Equal(new[] { 0.008, 0.141, 0.855, 0.0 }, path.SourceFillCmyk);
        Assert.Equal(new[] { 0.0, 0.0, 0.0, 1.0 }, path.SourceStrokeCmyk);
    }

    [Fact]
    public void AStaleDirectValueDoesNotSurviveAnIndirectColour()
    {
        // Black in the direct spelling, then a colour the indirect way. The stroke must be
        // the colour, not the black that was set earlier.
        byte[] pdf = Pdf(
            "0 0 0 1 K /DeviceCMYK CS 0.871 0.749 0 0 SCN 10 700 200 80 re S");

        Assert.Equal(new[] { 0.871, 0.749, 0.0, 0.0 }, Paths(pdf).Single().SourceStrokeCmyk);
    }

    [Fact]
    public void AnIndirectRgbColourCarriesNoInkValues()
    {
        // SCN under DeviceRGB is not DeviceCMYK. Recording it as ink values would write
        // four components where the file had three.
        byte[] pdf = Pdf("/DeviceRGB CS 0.2 0.4 0.6 SCN 10 700 200 80 re S");

        Assert.Null(Paths(pdf).Single().SourceStrokeCmyk);
    }

    [Fact]
    public void TheInkValuesReachTheExport()
    {
        byte[] pdf = Pdf("0 0 0 0.3 k 10 700 200 80 re f");
        CadDocument document = PdfImporter.Import(pdf);

        // The k=0.3 grey renders as 189 through a viewer, not the 178 the naive formula
        // gives, so the export has to carry the ink values rather than the RGB.
        string exported = Encoding.Latin1.GetString(PdfDocumentExporter.Export(document));

        Assert.Contains("0 0 0 0.3 k", Inflated(exported), StringComparison.Ordinal);
    }

    /// <summary>Every decompressed stream, concatenated.</summary>
    private static string Inflated(string latin)
    {
        var builder = new StringBuilder();
        byte[] bytes = Encoding.Latin1.GetBytes(latin);

        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(latin, @"(?<!end)stream\r?\n"))
        {
            int start = m.Index + m.Length;
            int end = latin.IndexOf("endstream", start, StringComparison.Ordinal);
            if (end < 0)
            {
                break;
            }

            try
            {
                using var input = new MemoryStream(bytes, start, end - start);
                using var zlib = new System.IO.Compression.ZLibStream(
                    input, System.IO.Compression.CompressionMode.Decompress);
                using var reader = new StreamReader(zlib, Encoding.UTF8);
                builder.Append(reader.ReadToEnd());
            }
            catch (Exception)
            {
                // Not a Flate stream.
            }
        }

        return builder.ToString();
    }
}
