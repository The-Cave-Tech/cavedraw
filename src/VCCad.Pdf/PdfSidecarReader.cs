using System.Text;
using VCCad.Core.Model;
using VCCad.Core.Serialization;

namespace VCCad.Pdf;

/// <summary>
/// Reader for the PDF files VCCad produces. Scope is deliberately narrow and
/// documented: this is <em>not</em> a general PDF parser. It extracts the lossless
/// sidecar attachment back out of a VCCad-generated file (round-trip support) and
/// reports basic page layout. Full inbound vector parsing (arbitrary third-party
/// PDF → model) is a separate task scheduled in project plan M4.
///
/// The reader assumes the file was written by <see cref="PdfAssembler"/>: a
/// classic xref table followed by <c>trailer/startxref/%%EOF</c>. It walks the xref
/// to locate object bodies, then resolves the catalog → Names → EmbeddedFiles
/// chain to find the sidecar.
/// </summary>
public static class PdfSidecarReader
{
    /// <summary>
    /// Reads a VCCad PDF and returns the original document. Throws
    /// <see cref="InvalidDataException"/> when the sidecar is missing or corrupt.
    /// </summary>
    public static CadDocument ReadDocument(byte[] pdfBytes)
    {
        byte[] sidecar = ExtractSidecar(pdfBytes);
        return VccadDocumentSerializer.Deserialize(sidecar);
    }

    /// <summary>
    /// Extracts the raw sidecar JSON bytes (zlib inflated) from a VCCad PDF.
    /// </summary>
    public static byte[] ExtractSidecar(byte[] pdfBytes)
    {
        long[] offsets = ReadXref(pdfBytes);
        if (offsets.Length == 0)
        {
            throw new InvalidDataException("No xref entries found — not a VCCad PDF?");
        }

        // Object 1 is the catalog by construction of our exporter.
        string catalogBody = ReadObject(pdfBytes, offsets, 1);
        int namesRef = FindReference(catalogBody, "/Names");
        if (namesRef < 0)
        {
            throw new InvalidDataException("Catalog has no /Names entry.");
        }

        string namesBody = ReadObject(pdfBytes, offsets, namesRef);

        // The names object nests: << /EmbeddedFiles << /Names [(name) specRef 0 R] >> >>.
        // Find the Filespec object reference inside the embedded-files /Names array
        // (our writer emits a single-element array, which is all a minimal reader
        // needs to support; parsing general name trees is out of scope for M4 seed).
        int embeddedFiles = namesBody.IndexOf("/EmbeddedFiles", StringComparison.Ordinal);
        if (embeddedFiles < 0)
        {
            throw new InvalidDataException("Names tree has no /EmbeddedFiles entry.");
        }

        int namesArray = namesBody.IndexOf("/Names", embeddedFiles, StringComparison.Ordinal);
        if (namesArray < 0)
        {
            throw new InvalidDataException("EmbeddedFiles has no /Names array.");
        }

        int specRef = FindIndirectAfter(namesBody, namesArray);
        if (specRef < 0)
        {
            throw new InvalidDataException("EmbeddedFiles /Names array has no Filespec reference.");
        }

        string specBody = ReadObject(pdfBytes, offsets, specRef);
        int streamRef = FindReference(specBody, "/EF");
        if (streamRef < 0)
        {
            throw new InvalidDataException("Filespec has no /EF (embedded file).");
        }

        return ExtractStream(pdfBytes, offsets, streamRef);
    }

    /// <summary>
    /// Parses the xref table to produce the byte offset of every object, indexed by
    /// object number. Supports the classic subsection layout our writer emits.
    /// </summary>
    private static long[] ReadXref(byte[] bytes)
    {
        // Walk back from the end to locate startxref.
        string text = Encoding.ASCII.GetString(bytes);
        int startxref = text.LastIndexOf("startxref", StringComparison.Ordinal);
        if (startxref < 0)
        {
            return Array.Empty<long>();
        }

        // The value after startxref is the file offset of the xref section.
        int valueStart = startxref + "startxref".Length;
        int valueEnd = text.IndexOf("%%EOF", valueStart, StringComparison.Ordinal);
        string offsetText = text[valueStart..valueEnd].Trim();
        long xrefOffset = long.Parse(offsetText, System.Globalization.CultureInfo.InvariantCulture);

        // Our writer emits a single subsection "xref\n0 N\n" followed by N entries,
        // each fixed width (20 bytes). Parse generically enough to skip the free
        // head-of-list entry for object 0.
        var span = bytes.AsSpan((int)xrefOffset);
        int line = 0;
        var reader = new AsciiLineReader(span);
        reader.NextLine(ref line); // "xref"
        string? subsection = reader.NextLine(ref line);
        if (subsection is null)
        {
            return Array.Empty<long>();
        }

        string[] parts = subsection.Split(' ');
        int first = int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
        int count = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);

        var offsets = new long[first + count];
        for (int i = 0; i < count; i++)
        {
            string? entry = reader.NextLine(ref line);
            if (entry is null || entry.Length < 18)
            {
                break;
            }

            // Each entry: "nnnnnnnnnn ggggg n \n" → offset is the first ten digits.
            long entryOffset = long.Parse(entry.AsSpan(0, 10),
                System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture);
            bool inUse = entry.Length > 17 && entry[17] == 'n';
            offsets[first + i] = inUse ? entryOffset : -1;
        }

        return offsets;
    }

    /// <summary>Returns the text of an object body (between "N 0 obj" and "endobj").</summary>
    private static string ReadObject(byte[] bytes, long[] offsets, int objectNumber)
    {
        if (objectNumber <= 0 || objectNumber >= offsets.Length)
        {
            throw new InvalidDataException($"Object {objectNumber} out of xref range.");
        }

        long start = offsets[objectNumber];
        if (start < 0)
        {
            throw new InvalidDataException($"Object {objectNumber} is a free (deleted) xref entry.");
        }

        // Find "endobj" after the object start; object bodies we write never
        // contain the token otherwise.
        int from = (int)start;
        // Latin1 (not ASCII) so any binary stream payload inside the object is
        // preserved byte-for-byte when we slice it back out for inflation.
        string tail = Encoding.Latin1.GetString(bytes, from, bytes.Length - from);
        int endobj = tail.IndexOf("endobj", StringComparison.Ordinal);
        if (endobj < 0)
        {
            throw new InvalidDataException($"Object {objectNumber} has no endobj marker.");
        }

        // Strip the leading "N 0 obj\n".
        int bodyStart = tail.IndexOf('\n') + 1;
        return tail[bodyStart..endobj].Trim();
    }

    /// <summary>
    /// Reads the raw (decompressed) bytes of a stream object given its number.
    /// Returns the inflated content when the stream carries /Filter /FlateDecode,
    /// otherwise the raw bytes.
    /// </summary>
    private static byte[] ExtractStream(byte[] bytes, long[] offsets, int objectNumber)
    {
        string body = ReadObject(bytes, offsets, objectNumber);
        int streamIndex = body.IndexOf("stream", StringComparison.Ordinal);
        if (streamIndex < 0)
        {
            throw new InvalidDataException($"Object {objectNumber} is not a stream.");
        }

        // Data begins right after "stream" plus its EOL (either \r\n or \n).
        int dataStart = streamIndex + "stream".Length;
        if (body[dataStart] == '\r')
        {
            dataStart += 2;
        }
        else if (body[dataStart] == '\n')
        {
            dataStart += 1;
        }

        int dataEnd = body.IndexOf("endstream", StringComparison.Ordinal);
        if (dataEnd < 0)
        {
            throw new InvalidDataException($"Stream {objectNumber} is unterminated.");
        }

        // The dictionary precedes the data; our slice contains dictionary + data,
        // so pull out just the bytes between dataStart and endstream (trim EOL).
        while (dataEnd > dataStart && (body[dataEnd - 1] == '\n' || body[dataEnd - 1] == '\r'))
        {
            dataEnd--;
        }

        byte[] raw = Encoding.Latin1.GetBytes(body[dataStart..dataEnd]);
        bool flate = body.Contains("/FlateDecode", StringComparison.Ordinal);
        return flate ? PdfDocumentExporter.Decompress(raw) : raw;
    }

    /// <summary>Finds the indirect reference "N 0 R" following a dictionary key.</summary>
    private static int FindReference(string body, string key)
    {
        int index = body.IndexOf(key, StringComparison.Ordinal);
        return index < 0 ? -1 : FindIndirectAfter(body, index + key.Length);
    }

    /// <summary>
    /// Finds the next "<c>digits 0 R</c>" (indirect reference) at or after
    /// <paramref name="startIndex"/> and returns the referenced object number.
    /// Returns −1 when none exists. The token layout is "<c>N 0 R</c>" — number,
    /// space, 0, space, R — so the digit run sits one or more spaces to the left
    /// of the '0' we search for.
    /// </summary>
    private static int FindIndirectAfter(string body, int startIndex)
    {
        int search = startIndex;
        while (true)
        {
            int refIndex = body.IndexOf("0 R", search, StringComparison.Ordinal);
            if (refIndex < 0)
            {
                return -1;
            }

            // Move left across any spaces separating the digit run from the '0'.
            int onePastDigits = refIndex;
            while (onePastDigits > 0 && body[onePastDigits - 1] == ' ')
            {
                onePastDigits--;
            }

            // Then walk across the digits themselves.
            int firstDigit = onePastDigits;
            while (firstDigit > 0 && char.IsDigit(body[firstDigit - 1]))
            {
                firstDigit--;
            }

            if (firstDigit < onePastDigits)
            {
                return int.Parse(body[firstDigit..onePastDigits],
                    System.Globalization.CultureInfo.InvariantCulture);
            }

            // The "0 R" we found was not an indirect reference (no number before
            // it); keep scanning for the next candidate.
            search = refIndex + 3;
        }
    }

    /// <summary>Minimal line reader over an ASCII span (xref tables are ASCII).</summary>
    private ref struct AsciiLineReader
    {
        private readonly ReadOnlySpan<byte> _span;
        private int _position;

        public AsciiLineReader(ReadOnlySpan<byte> span)
        {
            _span = span;
            _position = 0;
        }

        public string? NextLine(ref int line)
        {
            if (_position >= _span.Length)
            {
                return null;
            }

            int start = _position;
            while (_position < _span.Length && _span[_position] != (byte)'\n')
            {
                _position++;
            }

            int end = _position;
            _position++; // consume the '\n'

            // Trim a trailing '\r' left by CRLF line endings.
            if (end > start && _span[end - 1] == (byte)'\r')
            {
                end--;
            }

            line++;
            return Encoding.ASCII.GetString(_span[start..end]);
        }
    }
}
