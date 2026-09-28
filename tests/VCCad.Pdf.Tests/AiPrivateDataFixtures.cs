using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace VCCad.Pdf.Tests;

/// <summary>
/// Synthetic Illustrator fixtures and a miniature PDF writer, so the private-data
/// tests never depend on the (GPL/AGPL) corpus being downloaded.
///
/// Two sources of fixtures:
/// <list type="bullet">
/// <item>
/// Hand-authored <c>.aifix</c> files under <c>tests/VCCad.Pdf.Tests/Fixtures/ai</c>.
/// They are text files (base64 payload + base64 container) because the payload and
/// the container are binary; the format is documented in
/// <see cref="LoadFixtures"/>.
/// </item>
/// <item>
/// PDFs built in memory by <see cref="BuildPdf"/>, used for the edge cases that are
/// easier to construct than to store (marker offset, per-block filters, truncation).
/// </item>
/// </list>
/// </summary>
internal static class AiPrivateDataFixtures
{
    /// <summary>One block of a synthetic PDF: its <c>/AIPrivateData&lt;n&gt;</c> index and bytes.</summary>
    internal sealed record PdfBlock(int Index, byte[] Data, bool PdfFlateDecode);

    /// <summary>A loaded <c>.aifix</c> fixture.</summary>
    internal sealed record Fixture(string Name, string FormatName, byte[] Payload, byte[]? Container);

    /// <summary>Locates <c>tests/VCCad.Pdf.Tests/Fixtures/ai</c> by walking up from the test binary.</summary>
    internal static string? FixtureDirectory()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "tests", "VCCad.Pdf.Tests", "Fixtures", "ai");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }

    /// <summary>
    /// Loads every <c>.aifix</c> fixture in deterministic (ordinal) name order.
    ///
    /// File format (one key per line, <c>#</c> starts a comment):
    /// <code>
    /// format: ZstdAi24                 # expected AiPrivateDataFormat
    /// payload-b64: &lt;base64&gt;       # decoded payload bytes
    /// pdf-b64: &lt;base64&gt;           # container bytes; omitted for a bare .ai (AI8)
    /// </code>
    /// </summary>
    internal static List<Fixture> LoadFixtures()
    {
        var fixtures = new List<Fixture>();
        string? directory = FixtureDirectory();
        if (directory is null)
        {
            return fixtures;
        }

        foreach (string path in Directory.EnumerateFiles(directory, "*.aifix")
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            string format = string.Empty;
            byte[] payload = Array.Empty<byte>();
            byte[]? container = null;

            foreach (string rawLine in File.ReadAllLines(path))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                {
                    continue;
                }

                int colon = line.IndexOf(':');
                if (colon < 0)
                {
                    continue;
                }

                string key = line[..colon].Trim();
                string value = line[(colon + 1)..].Trim();
                switch (key)
                {
                    case "format":
                        format = value;
                        break;
                    case "payload-b64":
                        payload = Convert.FromBase64String(value);
                        break;
                    case "pdf-b64":
                        container = Convert.FromBase64String(value);
                        break;
                }
            }

            fixtures.Add(new Fixture(Path.GetFileNameWithoutExtension(path), format, payload, container));
        }

        return fixtures;
    }

    /// <summary>zlib (RFC 1950) compression — the framing PDF's /FlateDecode uses.</summary>
    internal static byte[] Zlib(byte[] raw)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(raw, 0, raw.Length);
        }

        return output.ToArray();
    }

    /// <summary>
    /// Builds a minimal, structurally valid single-page PDF whose page carries
    /// <c>/PieceInfo → /Illustrator → /Private</c> with the supplied blocks.
    ///
    /// <paramref name="numBlock"/> is written as <c>/NumBlock</c> when supplied (the
    /// real corpus shows files both with and without it).
    /// </summary>
    internal static byte[] BuildPdf(IReadOnlyList<PdfBlock> blocks, int? numBlock = null)
    {
        var offsets = new List<long>();
        using var output = new MemoryStream();
        void Write(string text)
        {
            byte[] bytes = Encoding.Latin1.GetBytes(text);
            output.Write(bytes, 0, bytes.Length);
        }

        void BeginObject(int number)
        {
            offsets.Add(output.Length);
            Write($"{number} 0 obj\n");
        }

        Write("%PDF-1.7\n%\xE2\xE3\xCF\xD3\n");

        BeginObject(1);
        Write("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        BeginObject(2);
        Write("<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        var privateDict = new StringBuilder("<< ");
        if (numBlock is not null)
        {
            privateDict.Append("/NumBlock ").Append(numBlock.Value.ToString(CultureInfo.InvariantCulture)).Append(' ');
        }

        for (int i = 0; i < blocks.Count; i++)
        {
            privateDict.Append("/AIPrivateData").Append(blocks[i].Index.ToString(CultureInfo.InvariantCulture))
                .Append(' ').Append(4 + i).Append(" 0 R ");
        }

        privateDict.Append(">>");

        BeginObject(3);
        Write("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] " +
              "/PieceInfo << /Illustrator << /Subtype /Artwork " +
              "/CreatorInfo << /Creator (Synthetic) /Subtype /Artwork >> " +
              $"/Private {privateDict} >> >> >>\nendobj\n");

        for (int i = 0; i < blocks.Count; i++)
        {
            PdfBlock block = blocks[i];
            byte[] data = block.PdfFlateDecode ? Zlib(block.Data) : block.Data;
            BeginObject(4 + i);
            Write($"<< /Length {data.Length.ToString(CultureInfo.InvariantCulture)}" +
                  (block.PdfFlateDecode ? " /Filter /FlateDecode" : string.Empty) + " >>\nstream\n");
            output.Write(data, 0, data.Length);
            Write("\nendstream\nendobj\n");
        }

        long xrefOffset = output.Length;
        var xref = new StringBuilder();
        xref.Append("xref\n0 ").Append(offsets.Count + 1).Append('\n');
        xref.Append("0000000000 65535 f \n");
        foreach (long offset in offsets)
        {
            xref.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        }

        Write(xref.ToString());
        Write($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF\n");

        return output.ToArray();
    }
}
