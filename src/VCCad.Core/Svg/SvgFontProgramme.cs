using System.Buffers.Binary;
using System.IO.Compression;
using System.Xml.Linq;
using VCCad.Core.Model;

namespace VCCad.Core.Svg;

/// <summary>
/// The glyph definitions an **SVG-in-OpenType** font carries: a programme whose `SVG ` table holds, for a range of
/// glyph ids, an SVG document that draws them.
///
/// This is the case `text-svg-glyph-custom.svg` in the corpus exists for. A document declares a face with
/// `@font-face { font-family: "…"; src: url("./svginotf/….otf"); }` and writes text in it; the programme is a real
/// OpenType font, but the picture the file means is the one in its `SVG ` table - which is why a renderer that only
/// reads the outlines draws a different shape, and one that reads nothing draws a substituted face.
///
/// **Read here rather than through `VCCad.Pdf.Fontext`.** The PDF side has a fuller sfnt reader, but the assembly
/// rule is Geometry ← Core ← {Pdf, Api, App}, so the SVG reader cannot reach it. What is needed here is small and
/// different: the table directory, the `SVG ` records, the cmap, and nothing about embedding.
///
/// The glyph documents are frequently gzipped **as records** (the table's own spec allows either), independently of
/// the file being gzipped, so each record is inflated on its own.
/// </summary>
public sealed class SvgFontProgramme : ISvgGlyphFont
{
    private const uint GzipMagic = 0x1F8B;

    private readonly Dictionary<int, string> _documents = new();
    private readonly Dictionary<int, int> _cmap = new();
    private int[] _advances = Array.Empty<int>();

    private SvgFontProgramme()
    {
    }

    /// <summary>The family the programme names, when its `name` table can be read: how a `@font-face` is matched.</summary>
    public string FamilyName { get; private set; } = string.Empty;

    /// <summary>
    /// The em square the glyph coordinates are in - a glyph's numbers are font units and divide by this to become
    /// ems. 1000 for most faces, including the corpus's test font; 2048 for the TrueType ones.
    /// </summary>
    public int UnitsPerEm { get; private set; }

    /// <summary>
    /// How far the pen moves after a glyph, in the same font units. Zero when the programme states none, which is
    /// the caller's cue to fall back to something it can measure rather than to place everything on one spot.
    /// </summary>
    public int AdvanceFor(int glyphId)
    {
        if (_advances.Length == 0 || glyphId < 0)
        {
            return 0;
        }

        return glyphId < _advances.Length ? _advances[glyphId] : _advances[^1];
    }

    /// <summary>Glyph ids the programme carries an SVG document for.</summary>
    public IReadOnlyCollection<int> GlyphIds => _documents.Keys;

    /// <summary>
    /// The programme in <paramref name="bytes"/>, or null when it is not an sfnt or carries no `SVG ` table - the
    /// ordinary case, and one every caller has to handle.
    /// </summary>
    public static SvgFontProgramme? Parse(byte[] bytes)
    {
        if (bytes.Length < 12)
        {
            return null;
        }

        // A TrueType/OpenType programme starts with a version tag or 'ttcf'; either way the table count is a
        // big-endian uint16 at offset 4 for the ordinary single-font case this reader handles.
        uint tag = BinaryPrimitives.ReadUInt32BigEndian(bytes);
        bool sfnt = tag is 0x00010000 or 0x4F54544F or 0x74727565; // 1.0, 'OTTO', 'true'
        if (!sfnt)
        {
            return null;
        }

        int tableCount = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(4));
        if (tableCount <= 0 || 12 + (tableCount * 16) > bytes.Length)
        {
            return null;
        }

        var tables = new Dictionary<string, (int Offset, int Length)>(StringComparer.Ordinal);
        for (int i = 0; i < tableCount; i++)
        {
            int at = 12 + (i * 16);
            string name = System.Text.Encoding.ASCII.GetString(bytes, at, 4);
            int offset = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at + 8));
            int length = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at + 12));
            if (offset >= 0 && length >= 0 && offset + length <= bytes.Length)
            {
                tables[name] = (offset, length);
            }
        }

        if (!tables.TryGetValue("SVG ", out (int svgOffset, int svgLength) svg))
        {
            return null;
        }

        var programme = new SvgFontProgramme();
        programme.ReadFamily(bytes, tables);
        programme.ReadUnitsPerEm(bytes, tables);
        programme.ReadAdvances(bytes, tables);
        programme.ReadCmap(bytes, tables);
        programme.ReadGlyphDocuments(bytes, svg.svgOffset, svg.svgLength);
        return programme._documents.Count > 0 ? programme : null;
    }

    /// <summary>The em square, from the `head` table. 1000 when it is absent, which is the common value.</summary>
    private void ReadUnitsPerEm(byte[] bytes, Dictionary<string, (int Offset, int Length)> tables)
    {
        UnitsPerEm = tables.TryGetValue("head", out (int Offset, int Length) head) && head.Length >= 20
            ? BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(head.Offset + 18))
            : 1000;

        if (UnitsPerEm <= 0)
        {
            UnitsPerEm = 1000;
        }
    }

    /// <summary>
    /// The advances, from `hhea` and `hmtx`: how many long metrics there are, then one advance and one
    /// left-side-bearing each. A glyph past the last long metric repeats the last advance, which is what the format
    /// means by the short form.
    /// </summary>
    private void ReadAdvances(byte[] bytes, Dictionary<string, (int Offset, int Length)> tables)
    {
        if (!tables.TryGetValue("hhea", out (int Offset, int Length) hhea) || hhea.Length < 36 ||
            !tables.TryGetValue("hmtx", out (int Offset, int Length) hmtx))
        {
            return;
        }

        int count = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(hhea.Offset + 34));
        if (count <= 0 || hmtx.Length < count * 4)
        {
            return;
        }

        int glyphCount = tables.TryGetValue("maxp", out (int Offset, int Length) maxp) && maxp.Length >= 6
            ? BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(maxp.Offset + 4))
            : count;

        var advances = new int[Math.Max(glyphCount, count)];
        for (int i = 0; i < advances.Length; i++)
        {
            int at = hmtx.Offset + (Math.Min(i, count - 1) * 4);
            advances[i] = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(at));
        }

        _advances = advances;
    }

    /// <summary>The SVG document that draws the glyph, or null when the programme has none for it.</summary>
    public string? DocumentFor(int glyphId) => _documents.TryGetValue(glyphId, out string? document) ? document : null;

    /// <summary>
    /// The shapes the glyph's document draws, in font units.
    ///
    /// A shape kind this reader does not draw is **reported** rather than skipped in silence: a glyph drawn with a
    /// rect or a circle is a picture this reader cannot make, and saying so is the difference between a gap and a
    /// guess. That is the same rule the SVG reader applies to a whole element it cannot draw.
    /// </summary>
    IReadOnlyList<GlyphShape> ISvgGlyphFont.Glyphs(int glyphId, Action<string> warn)
    {
        if (DocumentFor(glyphId) is not { Length: > 0 } document)
        {
            return Array.Empty<GlyphShape>();
        }

        XDocument xml;
        try
        {
            xml = XDocument.Parse(document);
        }
        catch (System.Xml.XmlException exception)
        {
            warn($"a glyph drawing is not well-formed XML and was not drawn: {exception.Message}");
            return Array.Empty<GlyphShape>();
        }

        var shapes = new List<GlyphShape>();
        foreach (XElement element in xml.Descendants())
        {
            if (element.Name.LocalName != "path")
            {
                if (element.Name.LocalName is "rect" or "circle" or "ellipse" or "polygon" or "polyline" or "use")
                {
                    warn($"a glyph drawing uses a <{element.Name.LocalName}>, which this reader does not draw as a glyph");
                }

                continue;
            }

            string data = element.Attribute("d")?.Value ?? string.Empty;
            if (data.Length == 0)
            {
                continue;
            }

            (FillSpec fill, StrokeSpec stroke) = SvgGlyphPaint.Read(element, warn);
            shapes.Add(new GlyphShape(data, fill, stroke));
        }

        return shapes;
    }

    /// <summary>The glyph id a code point maps to, or 0 for "no glyph" as the cmap means it.</summary>
    public int GlyphFor(int codePoint) => _cmap.TryGetValue(codePoint, out int gid) ? gid : 0;

    /// <summary>
    /// An OpenType programme's ligatures live in its GSUB table, which this reader does not parse - so it
    /// answers 0 for every sequence, which is the honest answer rather than a guess at the first character's glyph.
    /// </summary>
    public int GlyphForSequence(string sequence) => 0;

    /// <summary>
    /// The `SVG ` table: a header, then one record per glyph range naming an offset and length into the same table.
    /// A record's document may be gzipped on its own, which is why the inflation is per record rather than per file.
    /// </summary>
    private void ReadGlyphDocuments(byte[] bytes, int offset, int length)
    {
        if (length < 10)
        {
            return;
        }

        int version = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset));
        if (version != 0 || length < 12)
        {
            return;
        }

        // The header is version, the offset of the document index from the table's start, and a reserved word.
        int documentOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset + 2));
        int index = offset + documentOffset;
        if (documentOffset <= 0 || index + 2 > offset + length)
        {
            return;
        }

        // The index itself is a count followed by that many records.
        int recordCount = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(index));
        if (recordCount <= 0)
        {
            return;
        }

        for (int i = 0; i < recordCount; i++)
        {
            int at = index + 2 + (i * 12);
            if (at + 12 > offset + length)
            {
                return;
            }

            int startGlyph = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(at));
            int endGlyph = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(at + 2));
            int docOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at + 4));
            int docLength = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at + 8));

            // **The document offsets are measured from the start of the index, not from the table.** Reading them
            // from the table's start lands inside the index itself - the bytes there are the index's own records,
            // and a parser that inflated from that point would find one of them prefixed to the document.
            int document = index + docOffset;
            if (docLength <= 0 || document + docLength > offset + length)
            {
                continue;
            }

            string? text = Decode(bytes, document, docLength);
            if (text is null)
            {
                continue;
            }

            // A record may cover a range of glyphs; the same document is the answer for each of them, which is how
            // a font says "these glyphs share one drawing".
            for (int gid = startGlyph; gid <= endGlyph; gid++)
            {
                _documents[gid] = text;
            }
        }
    }

    /// <summary>One record's document, inflating it when the record carries gzip magic.</summary>
    private static string? Decode(byte[] bytes, int offset, int length)
    {
        if (length >= 2 && BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset)) == GzipMagic)
        {
            try
            {
                using var input = new MemoryStream(bytes, offset, length);
                using var gzip = new GZipStream(input, CompressionMode.Decompress);
                using var reader = new StreamReader(gzip, System.Text.Encoding.UTF8);
                return reader.ReadToEnd();
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException)
            {
                return null;
            }
        }

        return System.Text.Encoding.UTF8.GetString(bytes, offset, length);
    }

    /// <summary>The family name, from the `name` table's first family record (name id 1).</summary>
    private void ReadFamily(byte[] bytes, Dictionary<string, (int Offset, int Length)> tables)
    {
        if (!tables.TryGetValue("name", out (int Offset, int Length) table) || table.Length < 6)
        {
            return;
        }

        int at = table.Offset;
        int count = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(at + 2));
        int storage = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(at + 4));
        for (int i = 0; i < count; i++)
        {
            int record = at + 6 + (i * 12);
            if (record + 12 > at + table.Length)
            {
                return;
            }

            int nameId = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(record + 6));
            int length = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(record + 8));
            int offset = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(record + 10));
            if (nameId != 1 || offset + length > table.Length)
            {
                continue;
            }

            // Windows records are UTF-16BE; Macintosh ones are single-byte. Only the family is wanted here.
            int platform = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(record));
            int valueAt = at + storage + offset;
            if (valueAt + length > at + table.Length)
            {
                return;
            }

            FamilyName = platform == 3
                ? System.Text.Encoding.BigEndianUnicode.GetString(bytes, valueAt, length)
                : System.Text.Encoding.ASCII.GetString(bytes, valueAt, length);
            return;
        }
    }

    /// <summary>
    /// The cmap, in the two formats a font that carries SVG glyphs actually uses: 4 for the basic multilingual
    /// plane and 12 for everything above it. Format 0 and the rest are skipped: this reader is looking up a
    /// character in a test or display face, not reimplementing a font engine.
    /// </summary>
    private void ReadCmap(byte[] bytes, Dictionary<string, (int Offset, int Length)> tables)
    {
        if (!tables.TryGetValue("cmap", out (int Offset, int Length) table) || table.Length < 4)
        {
            return;
        }

        int count = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(table.Offset + 2));
        for (int i = 0; i < count; i++)
        {
            int record = table.Offset + 4 + (i * 8);
            if (record + 8 > table.Offset + table.Length)
            {
                return;
            }

            int subtable = table.Offset + (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(record + 4));
            if (subtable + 2 > bytes.Length)
            {
                continue;
            }

            int format = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(subtable));
            if (format == 4)
            {
                ReadCmapFormat4(bytes, subtable);
            }
            else if (format == 12)
            {
                ReadCmapFormat12(bytes, subtable);
            }
        }
    }

    private void ReadCmapFormat4(byte[] bytes, int at)
    {
        int segCountX2 = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(at + 6));
        int segCount = segCountX2 / 2;
        if (segCount <= 0)
        {
            return;
        }

        int endCodes = at + 14;
        int startCodes = endCodes + segCountX2 + 2;
        int deltas = startCodes + segCountX2;
        int rangeOffsets = deltas + segCountX2;

        for (int segment = 0; segment < segCount; segment++)
        {
            int end = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(endCodes + (segment * 2)));
            int start = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(startCodes + (segment * 2)));
            if (start > end || end == 0xFFFF)
            {
                continue;
            }

            int delta = BinaryPrimitives.ReadInt16BigEndian(bytes.AsSpan(deltas + (segment * 2)));
            int rangeOffset = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(rangeOffsets + (segment * 2)));
            for (int code = start; code <= end; code++)
            {
                int glyph;
                if (rangeOffset == 0)
                {
                    glyph = (code + delta) & 0xFFFF;
                }
                else
                {
                    int glyphAt = rangeOffsets + (segment * 2) + rangeOffset + ((code - start) * 2);
                    if (glyphAt + 2 > bytes.Length)
                    {
                        continue;
                    }

                    glyph = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(glyphAt));
                    if (glyph != 0)
                    {
                        glyph = (glyph + delta) & 0xFFFF;
                    }
                }

                if (glyph != 0)
                {
                    _cmap[code] = glyph;
                }
            }
        }
    }

    private void ReadCmapFormat12(byte[] bytes, int at)
    {
        int groups = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at + 12));
        for (int i = 0; i < groups; i++)
        {
            int group = at + 16 + (i * 12);
            if (group + 12 > bytes.Length)
            {
                return;
            }

            int start = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(group));
            int end = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(group + 4));
            int glyph = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(group + 8));
            for (int code = start; code <= end && code - start < 65536; code++)
            {
                _cmap[code] = glyph + (code - start);
            }
        }
    }
}
