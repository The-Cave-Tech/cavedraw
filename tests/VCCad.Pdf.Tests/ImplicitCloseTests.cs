using System.Text;
using VCCad.Core.Model;
using VCCad.Pdf;
using Xunit;

namespace VCCad.Pdf.Tests;

/// <summary>
/// A fill implicitly closes every open subpath (ISO 32000-1 §8.5.3.3).
///
/// This is not a detail of the model, it is whether the letter is drawn at all. Outline
/// exporters routinely write a glyph as <c>m ... c c c f</c> with no <c>h</c>; an open
/// figure does not fill, so the glyph disappears. On a real pattern that cost the round
/// glyphs — O, S, C, 0, c — from every label, while glyphs whose contours happened to
/// carry an <c>h</c> survived, which is what made it look like "some letters".
/// </summary>
public class ImplicitCloseTests
{
    private static byte[] Pdf(string path)
    {
        string content = "1 0 0 1 100 700 cm 0 0 0 rg " + path + " f";

        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>",
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
            builder.Append($"{offset:D10} 00000 n \n");
        }

        builder.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(builder.ToString());
    }

    private static PathItem OnlyPath(CadDocument document)
        => Imported.PathOn(document);

    [Fact]
    public void AnOpenContourDrawnByAFillIsClosed()
    {
        // A "C" shape with no "h" - exactly how the failing glyphs were written.
        CadDocument document = PdfImporter.Import(
            Pdf("0 0 m 10 10 l 20 10 l 20 20 l 10 20 l 0 0 l"));

        PathItem path = OnlyPath(document);
        Assert.True(path.Fill.IsVisible);
        Assert.All(path.SubPaths, sub => Assert.True(sub.IsClosed,
            "a fill must close the subpath or nothing is painted"));
    }

    [Fact]
    public void AnOpenContourDrawnByAStrokeIsLeftOpen()
    {
        // "S" strokes an open path; closing it would add a line the file never drew.
        CadDocument document = PdfImporter.Import(
            Pdf("0 0 m 10 10 l 20 10 l 20 20 l 10 20 l 0 0 l"));
        _ = document;

        CadDocument stroked = PdfImporter.Import(
            Encoding.ASCII.GetBytes(
                Encoding.ASCII.GetString(Pdf("0 0 m 10 10 l 20 10 l 20 20 l 10 20 l 0 0 l"))
                    .Replace(" l f", " l S")));

        PathItem path = OnlyPath(stroked);
        Assert.True(path.Stroke.IsVisible);
        Assert.All(path.SubPaths, sub => Assert.False(sub.IsClosed));
    }
}
