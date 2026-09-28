using System.Text;
using VCCad.Core.Model;
using Xunit;

namespace VCCad.Pdf.Tests;

/// <summary>
/// The command queue travels with the file as our own private data.
///
/// A document that carries how it was made, not just what it looks like, can be restored
/// with its history intact. It lives in a catalog stream beside the model sidecar, so a
/// reader that does not care about it ignores both.
/// </summary>
public class PdfHistoryTests
{
    private static string Latin(byte[] pdf) => new(Encoding.Latin1.GetChars(pdf));

    [Fact]
    public void TheQueueIsWrittenWhenThereIsOne()
    {
        CadDocument doc = CadDocument.CreateDefault("History");
        string[] history = { "{\"op\":\"document.new\"}", "{\"op\":\"object.create\"}" };

        string pdf = Latin(PdfDocumentExporter.Export(doc, history));

        Assert.Contains("/VCCadHistory", pdf, StringComparison.Ordinal);
        Assert.Contains("object.create", Inflate(pdf), StringComparison.Ordinal);
    }

    [Fact]
    public void NoQueueMeansNoStream()
    {
        CadDocument doc = CadDocument.CreateDefault("Empty");

        // Nothing to record, so nothing is added: an export of a freshly opened file
        // stays the size it was.
        Assert.DoesNotContain("/VCCadHistory", Latin(PdfDocumentExporter.Export(doc)),
            StringComparison.Ordinal);

        Assert.DoesNotContain("/VCCadHistory",
            Latin(PdfDocumentExporter.Export(doc, Array.Empty<string>())), StringComparison.Ordinal);
    }

    [Fact]
    public void TheModelSidecarIsStillThereAlongsideIt()
    {
        CadDocument doc = CadDocument.CreateDefault("Both");

        string pdf = Latin(PdfDocumentExporter.Export(doc, new[] { "{\"op\":\"x\"}" }));

        // The model and the history are separate streams and neither displaces the other.
        Assert.Contains("/VCCadDocument", pdf, StringComparison.Ordinal);
        Assert.Contains("/VCCadHistory", pdf, StringComparison.Ordinal);
    }

    /// <summary>Every decompressed stream, concatenated.</summary>
    private static string Inflate(string latin)
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
