using System.Text;

namespace VCCad.Pdf;

/// <summary>
/// Minimal low-level PDF file assembler.
///
/// Responsibilities are deliberately narrow: keep an ordered set of numbered PDF
/// objects, then lay them out into a syntactically valid PDF 1.7 byte stream with
/// a correct cross-reference table and trailer. Higher layers (the exporter) own
/// what goes into each object — this class never interprets content.
///
/// Object numbers are allocated contiguously starting at 1. Callers assign bodies
/// by number afterwards, which allows forward references (a parent pointing at
/// children allocated later) without any two-pass rewriting.
/// </summary>
internal sealed class PdfAssembler
{
    private readonly List<byte[]> _objects = new();

    /// <summary>Allocates the next object number and reserves a slot for its body.</summary>
    public int Allocate()
    {
        _objects.Add(Array.Empty<byte>());
        return _objects.Count;
    }

    /// <summary>Sets the body (exact PDF object text) for an allocated number.</summary>
    public void SetBody(int objectNumber, string body)
        => SetBody(objectNumber, Encoding.UTF8.GetBytes(body));

    /// <summary>Sets the body bytes for an allocated number (binary-safe).</summary>
    public void SetBody(int objectNumber, byte[] body)
        => _objects[objectNumber - 1] = body;

    /// <summary>
    /// Serializes the file: header + binary comment line, then every object with
    /// its byte offset recorded, then the xref table and trailer. Returns the
    /// complete file bytes. See <see cref="PdfSidecarReader"/> for the reader that
    /// parses this layout back.
    /// </summary>
    public byte[] Serialize(int rootObjectNumber, string trailerExtra = "")
    {
        using var output = new MemoryStream();

        // The %PDF header must be followed within a few bytes by a line whose first
        // character is a high-bit byte — scanners use it to detect binary files.
        // Latin1 so the ≥128 binary marker survives (ASCII would turn it into "?").
        byte[] header = Encoding.Latin1.GetBytes("%PDF-1.7\n%\xE2\xE3\xCF\xD3\n");
        output.Write(header, 0, header.Length);

        var offsets = new long[_objects.Count];
        for (int i = 0; i < _objects.Count; i++)
        {
            offsets[i] = output.Length; // record where object i+1 starts
            byte[] body = _objects[i];
            byte[] obj = Encoding.ASCII.GetBytes($"{i + 1} 0 obj\n");
            output.Write(obj, 0, obj.Length);
            output.Write(body, 0, body.Length);
            byte[] eol = Encoding.ASCII.GetBytes("\nendobj\n");
            output.Write(eol, 0, eol.Length);
        }

        long xrefOffset = output.Length;

        // xref section. The free-entry for object 0 (head of the free list) is
        // conventional; every real object is an in-use entry. Each entry is exactly
        // 20 bytes including the two-byte end-of-line, as the spec requires.
        var xref = new StringBuilder();
        xref.Append($"xref\n0 {_objects.Count + 1}\n");
        xref.Append("0000000000 65535 f \n");
        foreach (long off in offsets)
        {
            xref.Append(off.ToString("D10")).Append(" 00000 n \n");
        }

        byte[] xrefBytes = Encoding.ASCII.GetBytes(xref.ToString());
        output.Write(xrefBytes, 0, xrefBytes.Length);

        byte[] trailer = Encoding.ASCII.GetBytes(
            $"trailer\n<< /Size {_objects.Count + 1} /Root {rootObjectNumber} 0 R{trailerExtra} >>\n" +
            $"startxref\n{xrefOffset}\n%%EOF\n");
        output.Write(trailer, 0, trailer.Length);
        return output.ToArray();
    }
}
