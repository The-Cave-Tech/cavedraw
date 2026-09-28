using System.Text;
using VCCad.Core.Model;
using VCCad.Pdf.Parsing;

namespace VCCad.Pdf.Ai;

/// <summary>
/// Reads the Illustrator private-data payload out of a <c>.ai</c>/PDF file.
///
/// The payload lives at page → <c>/PieceInfo</c> → <c>/Illustrator</c> →
/// <c>/Private</c>, a dictionary holding an optional <c>/NumBlock</c> count and one
/// or more <c>/AIPrivateData&lt;n&gt;</c> stream entries. Two things make this
/// trickier than it looks, and both are handled here:
///
/// <list type="number">
/// <item>
/// <b>Blocks are read raw, not PDF-decoded.</b> Illustrator applies the block-level
/// zlib compression through the PDF <c>/FlateDecode</c> filter itself, so the raw
/// stream bytes already start with a zlib header — that is exactly the byte pattern
/// the container rules ("look for the zlib header") are written against. Feeding the
/// PDF-decoded bytes instead would silently drop the page-thumbnail block, which has
/// no filter and is not part of the payload. When the raw bytes are unusable the
/// extractor retries with the decoded bytes, which is what rescues files whose
/// payload is hidden behind an extra data filter (e.g. <c>ASCIIHexDecode</c>).
/// </item>
/// <item>
/// <b>Blocks are numbered, not ordered.</b> The dictionary entry
/// <c>/AIPrivateData10</c> sorts before <c>/AIPrivateData2</c> in string order, so
/// blocks are ordered by their numeric suffix. <c>/NumBlock</c> is reported but
/// never required: single-block files omit it entirely.
/// </item>
/// </list>
/// </summary>
public static class AiPrivateDataExtractor
{
    /// <summary>
    /// Extracts the decoded Illustrator payload from <paramref name="pdfBytes"/>,
    /// or returns <c>null</c> when the file carries none.
    ///
    /// A bare PostScript document (Illustrator 8 and earlier, or an already
    /// extracted payload) is accepted too: there the whole file <em>is</em> the
    /// private data, so it is returned verbatim as an AI8 payload.
    /// </summary>
    public static AiPrivateDataDocument? Extract(byte[] pdfBytes)
        => TryExtract(pdfBytes, out AiPrivateDataDocument? document) ? document : null;

    /// <summary>Non-throwing form of <see cref="Extract"/>.</summary>
    public static bool TryExtract(byte[] pdfBytes, out AiPrivateDataDocument? document)
    {
        document = null;
        if (pdfBytes is null || pdfBytes.Length == 0)
        {
            return false;
        }

        try
        {
            if (!IsPdfContainer(pdfBytes))
            {
                return TryExtractBarePostScript(pdfBytes, out document);
            }

            var file = new PdfFile(pdfBytes);
            List<Block> blocks = CollectBlocks(file, out int declaredBlocks);
            if (blocks.Count == 0)
            {
                return false;
            }

            // Pass 1: raw stream bytes (the rules' native input, see the class remarks).
            // Pass 2: PDF-filter-decoded bytes, for containers that hide the payload
            // behind an additional filter.
            if (!TryDecode(blocks.Select(b => b.Raw).ToList(),
                    out AiPrivateDataFormat format, out string text))
            {
                var decoded = new List<byte[]>(blocks.Count);
                foreach (Block block in blocks)
                {
                    decoded.Add(block.Stream is null ? block.Raw : DecodeBlock(file, block.Stream));
                }

                if (!TryDecode(decoded, out format, out text))
                {
                    return false;
                }
            }

            document = AiPrivateDataDocument.Parse(text, format, declaredBlocks);
            return true;
        }
        catch (Exception)
        {
            // Extraction is best-effort: a malformed/foreign file must never break an
            // import. Callers that care use the return value.
            document = null;
            return false;
        }
    }

    /// <summary>Extracts just the model-level payload, suitable for <see cref="CadDocument"/>.</summary>
    public static AiPrivateData? ExtractPrivateData(byte[] pdfBytes)
        => Extract(pdfBytes)?.PrivateData;

    /// <summary>One <c>/AIPrivateData&lt;n&gt;</c> entry: its numeric index and bytes.</summary>
    private sealed record Block(int PageIndex, int Index, byte[] Raw, PdfStream? Stream);

    // ------------------------------------------------------------------
    // PDF container
    // ------------------------------------------------------------------

    private static bool IsPdfContainer(byte[] bytes)
    {
        // The header may be preceded by junk in the wild; %PDF- must appear early.
        int limit = Math.Min(bytes.Length, 1024);
        for (int i = 0; i + 5 <= limit; i++)
        {
            if (bytes[i] == (byte)'%' && bytes[i + 1] == (byte)'P' && bytes[i + 2] == (byte)'D'
                && bytes[i + 3] == (byte)'F' && bytes[i + 4] == (byte)'-')
            {
                return true;
            }
        }

        return false;
    }

    private static List<Block> CollectBlocks(PdfFile file, out int declaredBlocks)
    {
        declaredBlocks = 0;
        var blocks = new List<Block>();
        var seenObjects = new HashSet<int>();
        int pageIndex = 0;

        foreach (Dictionary<string, object?> page in EnumeratePages(file))
        {
            if (file.ResolveDict(page.GetValueOrDefault("PieceInfo")) is not { } pieceInfo ||
                file.ResolveDict(pieceInfo.GetValueOrDefault("Illustrator")) is not { } illustrator ||
                file.ResolveDict(illustrator.GetValueOrDefault("Private")) is not { } priv)
            {
                pageIndex++;
                continue;
            }

            if (declaredBlocks == 0 &&
                file.ResolveNumber(priv.GetValueOrDefault("NumBlock")) is double count)
            {
                declaredBlocks = (int)count;
            }

            foreach ((string key, object? value) in priv)
            {
                int index = ParseBlockIndex(key);
                if (index < 0)
                {
                    continue;
                }

                // Several pages can reference one shared stream object (that is what
                // VCCad's own exporter does); it must contribute to the payload once.
                if (value is PdfRef reference)
                {
                    if (!seenObjects.Add(reference.Number))
                    {
                        continue;
                    }
                }

                if (file.Resolve(value) is not PdfStream stream)
                {
                    continue;
                }

                blocks.Add(new Block(pageIndex, index, stream.Raw, stream));
            }

            pageIndex++;
        }

        // Document order first, numeric /AIPrivateData<n> order within a page.
        blocks.Sort((a, b) => a.PageIndex != b.PageIndex
            ? a.PageIndex.CompareTo(b.PageIndex)
            : a.Index.CompareTo(b.Index));
        return blocks;
    }

    /// <summary>
    /// Returns the numeric suffix of <c>/AIPrivateData&lt;n&gt;</c> (1 when the key
    /// has no digits), or −1 when the key is something else.
    /// </summary>
    private static int ParseBlockIndex(string key)
    {
        const string prefix = "AIPrivateData";
        if (!key.StartsWith(prefix, StringComparison.Ordinal))
        {
            return -1;
        }

        string digits = key[prefix.Length..];
        if (digits.Length == 0)
        {
            return 1;
        }

        return int.TryParse(digits, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out int index)
            ? index
            : -1;
    }

    /// <summary>
    /// Page objects in document order. Deliberately a local copy of the importer's
    /// walk (returning the dictionaries themselves rather than a projection) so the
    /// extraction path stays independent of the vector importer.
    /// </summary>
    private static IEnumerable<Dictionary<string, object?>> EnumeratePages(PdfFile file)
    {
        int? catalog = file.FindCatalog();
        var pages = new List<Dictionary<string, object?>>();

        if (catalog is not null && file.GetObject(catalog.Value) is Dictionary<string, object?> root)
        {
            Walk(file, root.GetValueOrDefault("Pages"), 0, pages);
        }

        if (pages.Count == 0)
        {
            // No usable page tree (or a linearized/oddly structured file): fall back
            // to any object that carries a /PieceInfo, which is all we need here.
            foreach (int number in file.ObjectNumbers)
            {
                if (file.GetObject(number) is Dictionary<string, object?> dict &&
                    dict.ContainsKey("PieceInfo"))
                {
                    pages.Add(dict);
                }
            }
        }

        return pages;
    }

    private static void Walk(PdfFile file, object? node, int depth, List<Dictionary<string, object?>> pages)
    {
        if (depth > 32 || file.ResolveDict(node) is not { } dict)
        {
            return;
        }

        string type = dict.GetValueOrDefault("Type") is PdfName name ? name.Value : string.Empty;
        if (type == "Pages" && file.Resolve(dict.GetValueOrDefault("Kids")) is List<object?> kids)
        {
            foreach (object? kid in kids)
            {
                Walk(file, kid, depth + 1, pages);
            }
        }
        else if (type == "Page")
        {
            pages.Add(dict);
        }
    }

    // ------------------------------------------------------------------
    // Decoding rules
    // ------------------------------------------------------------------

    /// <summary>
    /// Applies the container rules to one set of block bytes and reports which
    /// format matched. Order matters: the marker-based formats are unambiguous and
    /// are tried before the per-block heuristic, and plain text is the last resort.
    /// </summary>
    internal static bool TryDecode(IReadOnlyList<byte[]> blocks,
        out AiPrivateDataFormat format, out string text)
    {
        format = AiPrivateDataFormat.Unknown;
        text = string.Empty;

        byte[] concatenated = Concatenate(blocks);
        if (concatenated.Length == 0)
        {
            return false;
        }

        // AI 2020+ — one zstd stream after the %AI24_ZStandard_Data marker. The marker
        // sits wherever the plaintext prolog ends, never at a fixed offset.
        int zstd = AiPrivateDataCodec.IndexOf(concatenated, Encoding.ASCII.GetBytes(AiPrivateDataCodec.Ai24Marker));
        if (zstd >= 0)
        {
            int start = zstd + AiPrivateDataCodec.Ai24Marker.Length;
            if (AiPrivateDataCodec.TryDecompressZstd(concatenated.AsSpan(start), out byte[] inflated) &&
                LooksLikePostScriptPayload(inflated))
            {
                format = AiPrivateDataFormat.ZstdAi24;
                text = Encoding.Latin1.GetString(inflated);
                return true;
            }
        }

        // CS2 – CC Legacy — one zlib stream after the %AI12_CompressedData marker.
        int zlib = AiPrivateDataCodec.IndexOf(concatenated, Encoding.ASCII.GetBytes(AiPrivateDataCodec.Ai12Marker));
        if (zlib >= 0)
        {
            int start = zlib + AiPrivateDataCodec.Ai12Marker.Length;
            if (AiPrivateDataCodec.TryInflate(concatenated.AsSpan(start), out byte[] inflated) &&
                LooksLikePostScriptPayload(inflated))
            {
                format = AiPrivateDataFormat.ZlibAi12Cc;
                text = Encoding.Latin1.GetString(inflated);
                return true;
            }
        }

        // AI 9 – CS — every block is its own zlib stream; blocks without a zlib header
        // (the page thumbnail) are not payload.
        var parts = new List<byte[]>(blocks.Count);
        foreach (byte[] block in blocks)
        {
            if (AiPrivateDataCodec.LooksLikeZlib(block) &&
                AiPrivateDataCodec.TryInflate(block, out byte[] inflated))
            {
                parts.Add(inflated);
            }
        }

        if (parts.Count > 0)
        {
            byte[] joined = Concatenate(parts);
            if (LooksLikePostScriptPayload(joined))
            {
                format = AiPrivateDataFormat.ZlibAi9Cs;
                text = Encoding.Latin1.GetString(joined);
                return true;
            }
        }

        // Already-decoded payload (e.g. an extracted text blob, or a file whose
        // private data Illustrator stored uncompressed).
        if (LooksLikePostScriptPayload(concatenated))
        {
            format = AiPrivateDataFormat.PostScriptAi8;
            text = Encoding.Latin1.GetString(concatenated);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Applies a stream's PDF filter chain.
    ///
    /// <see cref="PdfFile.GetStreamData"/> covers the overwhelmingly common case
    /// (FlateDecode, with predictors), so it is used whenever the chain is empty or
    /// all-Flate. Anything else is walked here: Corel/Illustrator exports in the
    /// corpus use <c>[/ASCIIHexDecode /FlateDecode]</c>, and a reader that ignores
    /// the ASCII-hex step sees hex text instead of the payload. Filters are applied
    /// in array order, which is the order PDF defines for decoding.
    /// </summary>
    private static byte[] DecodeBlock(PdfFile file, PdfStream stream)
    {
        List<string> filters = FilterNames(file, stream);
        if (filters.Count == 0 || filters.TrueForAll(IsFlate))
        {
            return file.GetStreamData(stream);
        }

        byte[] data = stream.Raw;
        foreach (string filter in filters)
        {
            data = filter switch
            {
                "ASCIIHexDecode" or "AHx" => AsciiHexDecode(data),
                "FlateDecode" or "Fl" => AiPrivateDataCodec.Inflate(data),
                _ => data,
            };
        }

        return data;
    }

    private static bool IsFlate(string filter) => filter is "FlateDecode" or "Fl";

    private static List<string> FilterNames(PdfFile file, PdfStream stream)
    {
        var names = new List<string>();
        switch (file.Resolve(stream.Dict.GetValueOrDefault("Filter")))
        {
            case PdfName name:
                names.Add(name.Value);
                break;
            case List<object?> list:
                foreach (object? entry in list)
                {
                    if (file.Resolve(entry) is PdfName resolved)
                    {
                        names.Add(resolved.Value);
                    }
                }

                break;
        }

        return names;
    }

    /// <summary>
    /// Decodes an <c>/ASCIIHexDecode</c> payload: hex digit pairs, whitespace
    /// ignored, terminated by <c>&gt;</c> or the end of the data. A trailing odd
    /// digit is padded with a zero nibble, as the specification requires.
    /// </summary>
    private static byte[] AsciiHexDecode(byte[] data)
    {
        var bytes = new List<byte>(data.Length / 2);
        int high = -1;
        foreach (byte b in data)
        {
            if (b == (byte)'>')
            {
                break;
            }

            int value = HexValue((char)b);
            if (value < 0)
            {
                continue;
            }

            if (high < 0)
            {
                high = value;
            }
            else
            {
                bytes.Add((byte)((high << 4) | value));
                high = -1;
            }
        }

        if (high >= 0)
        {
            bytes.Add((byte)(high << 4));
        }

        return bytes.ToArray();
    }

    private static int HexValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };

    private static bool TryExtractBarePostScript(byte[] bytes, out AiPrivateDataDocument? document)
    {
        document = null;

        // A bare file is only Illustrator private data when it actually carries
        // Illustrator directives; a generic PostScript file must not be classified
        // as artwork just because it starts with %!PS.
        if (!LooksLikePostScriptPayload(bytes) || !ContainsIllustratorDirective(bytes))
        {
            return false;
        }

        document = AiPrivateDataDocument.Parse(
            Encoding.Latin1.GetString(bytes), AiPrivateDataFormat.PostScriptAi8);
        return true;
    }

    /// <summary>
    /// True when the head of the data contains an Illustrator directive
    /// (<c>%AI…</c>) — the marker that distinguishes an <c>.ai</c> programme from an
    /// arbitrary PostScript file.
    /// </summary>
    internal static bool ContainsIllustratorDirective(ReadOnlySpan<byte> data)
    {
        ReadOnlySpan<byte> head = data[..Math.Min(data.Length, 8192)];
        return head.IndexOf(Encoding.ASCII.GetBytes("%AI")) >= 0;
    }

    /// <summary>
    /// Cheap sanity gate for "this is a PostScript/Illustrator payload" rather than
    /// decompression garbage: the DSC header, the AI thumbnail prolog, or any
    /// <c>%AI&lt;n&gt;_</c> directive must appear near the start.
    /// </summary>
    internal static bool LooksLikePostScriptPayload(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return false;
        }

        ReadOnlySpan<byte> head = data[..Math.Min(data.Length, 8192)];
        foreach (string marker in new[] { "%!PS", "%%BoundingBox", "%AI5_", "%AI3_", "%AI12_", "%AI24_", "%AI17_" })
        {
            if (head.IndexOf(Encoding.ASCII.GetBytes(marker)) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private static byte[] Concatenate(IReadOnlyList<byte[]> blocks)
    {
        int total = 0;
        foreach (byte[] block in blocks)
        {
            total += block.Length;
        }

        var result = new byte[total];
        int offset = 0;
        foreach (byte[] block in blocks)
        {
            Buffer.BlockCopy(block, 0, result, offset, block.Length);
            offset += block.Length;
        }

        return result;
    }
}
