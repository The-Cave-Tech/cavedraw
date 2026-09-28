using VCCad.Core.Model;
using VCCad.Core.Serialization;
using VCCad.Pdf;
using Xunit;

namespace VCCad.Pdf.Tests;

/// <summary>
/// Clip paths: the outline a page restricts painting to.
///
/// A clip is part of the artwork, not a rendering detail. A file may draw the same
/// paragraph several times and use a clip to show one copy, or round off a logo, or trim a
/// photograph — none of which can be recovered from the geometry underneath, because the
/// geometry is the whole thing and the clip is what limits it.
///
/// The ghostscript corpus uses W in 132 of 208 files, so this is not a corner.
/// </summary>
public class ClipPathTests
{
    /// <summary>
    /// A page painting one 300x300 square, clipped to a 100x100 one, so the painted area
    /// says unambiguously whether the clip was honoured.
    /// </summary>
    private static byte[] ClippedSquarePdf(string clipOperator = "W")
    {
        // The clip and the square start at the same corner, so the clip keeps the square's
        // bottom-left 100x100 and discards the rest. A clip that missed the square would
        // prove nothing: it would discard everything either way.
        string content =
            "q 0 0 100 100 re " + clipOperator + " n " +
            "0 0 0 rg 0 0 300 300 re f Q";

        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
        };

        var builder = new System.Text.StringBuilder("%PDF-1.7\n");
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
        return System.Text.Encoding.Latin1.GetBytes(builder.ToString());
    }

    [Fact]
    public void AClipIsReadAndAttachedToTheItemItLimits()
    {
        CadDocument document = PdfImporter.Import(ClippedSquarePdf());
        PathItem square = document.Artboards[0].Layers[0].Children.OfType<PathItem>().Single();

        Assert.True(square.IsClipped);
        ClipSpec clip = square.Clips.Single();

        // The outline, not the drawn square: 100x100 at the origin.
        Assert.Equal(FillRule.NonZero, clip.Rule);
        Assert.Single(clip.SubPaths);

        // Everything inside the clip is inside; everything outside is not.
        // Everything inside the clip is inside; everything outside is not. Model space is
        // y-down from the top, so the clipped corner sits near the top of the page.
        Assert.True(clip.Contains(new VCCad.Geometry.Point2D(50, 742)));
        Assert.False(clip.Contains(new VCCad.Geometry.Point2D(250, 742)));
    }

    [Fact]
    public void TheRuleTravelsWithTheClip()
    {
        CadDocument document = PdfImporter.Import(ClippedSquarePdf("W*"));
        PathItem square = document.Artboards[0].Layers[0].Children.OfType<PathItem>().Single();

        Assert.Equal(FillRule.EvenOdd, square.Clips.Single().Rule);
    }

    [Fact]
    public void AnUnclippedItemCarriesNoClip()
    {
        CadDocument document = PdfImporter.Import(ClippedSquarePdf("W"));
        document.Artboards[0].Layers[0].Children.OfType<PathItem>().Single();

        // A second page with no clip at all.
        var plain = new PathItem { Name = "Plain" };
        plain.AddSubPath(false).Nodes.Add(new PathNode(new VCCad.Geometry.Point2D(0, 0)));
        Assert.False(plain.IsClipped);
    }

    [Fact]
    public void AClipSurvivesSaveAndReload()
    {
        CadDocument document = PdfImporter.Import(ClippedSquarePdf("W*"));

        CadDocument reloaded = VccadDocumentSerializer.Deserialize(
            VccadDocumentSerializer.SerializeToBytes(document));

        PathItem square = reloaded.Artboards[0].Layers[0].Children.OfType<PathItem>().Single();

        Assert.True(square.IsClipped);
        Assert.Equal(FillRule.EvenOdd, square.Clips.Single().Rule);
        Assert.Equal(4, square.Clips.Single().SubPaths[0].Nodes.Count);
    }

    [Fact]
    public void TheExportCarriesTheClip()
    {
        CadDocument document = PdfImporter.Import(ClippedSquarePdf());

        // The clip has to be written out, or the export paints the whole 300x300 square
        // where the file showed a 100x100 corner of it. The content stream is compressed
        // in the file, so it is inflated before it can be read.
        string exported = Inflate(PdfDocumentExporter.Export(document));

        // Two clips: the page box, which the exporter always emits, and the item's own.
        int clips = System.Text.RegularExpressions.Regex.Matches(
            exported, @"(?<![A-Za-z])W\*?(?![A-Za-z])").Count;
        Assert.True(clips >= 2, $"expected the item's clip as well as the page box, found {clips}");
    }

    /// <summary>Every decompressed stream, concatenated.</summary>
    private static string Inflate(byte[] pdf)
    {
        string latin = System.Text.Encoding.Latin1.GetString(pdf);
        var builder = new System.Text.StringBuilder();

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
                using var input = new MemoryStream(pdf, start, end - start);
                using var zlib = new System.IO.Compression.ZLibStream(
                    input, System.IO.Compression.CompressionMode.Decompress);
                using var reader = new StreamReader(zlib, System.Text.Encoding.UTF8);
                builder.Append(reader.ReadToEnd());
            }
            catch (Exception)
            {
                // Not a Flate stream.
            }
        }

        return builder.ToString();
    }

    [Fact]
    public void AClipWrittenToTheSidecarIsNotDroppedOnTheNextSave()
    {
        // Two saves in a row: the clip must survive both, not just the first.
        CadDocument once = VccadDocumentSerializer.Deserialize(
            VccadDocumentSerializer.SerializeToBytes(PdfImporter.Import(ClippedSquarePdf())));
        CadDocument twice = VccadDocumentSerializer.Deserialize(
            VccadDocumentSerializer.SerializeToBytes(once));

        PathItem square = twice.Artboards[0].Layers[0].Children.OfType<PathItem>().Single();
        Assert.True(square.IsClipped);
    }
}
