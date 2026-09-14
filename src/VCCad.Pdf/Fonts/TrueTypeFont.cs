using System.Buffers.Binary;

namespace VCCad.Pdf.Fonts;

/// <summary>
/// A minimal TrueType/OpenType (glyf) font reader: just enough to embed the font
/// in a PDF and lay out text — cmap (formats 4 and 12), head, hhea, hmtx, maxp
/// and the PostScript name. Not a general font engine.
/// </summary>
public sealed class TrueTypeFont
{
    private readonly byte[] _data;
    private readonly Dictionary<string, (int Offset, int Length)> _tables = new();
    private readonly Dictionary<int, int> _cmap = new();
    private readonly ushort[] _advances;
    private readonly ushort _numberOfHMetrics;
    private readonly ushort _numGlyphs;

    public int UnitsPerEm { get; }

    public string PostScriptName { get; }

    public double ItalicAngle { get; }

    public int XMin { get; }
    public int YMin { get; }
    public int XMax { get; }
    public int YMax { get; }
    public int Ascender { get; }
    public int Descender { get; }

    public byte[] Data => _data;

    public TrueTypeFont(byte[] data)
    {
        _data = data;

        ushort numTables = ReadU16(4);
        for (int i = 0; i < numTables; i++)
        {
            int rec = 12 + i * 16;
            string tag = System.Text.Encoding.ASCII.GetString(data, rec, 4);
            int offset = (int)ReadU32(rec + 8);
            int length = (int)ReadU32(rec + 12);
            _tables[tag] = (offset, length);
        }

        (int headOffset, _) = _tables["head"];
        UnitsPerEm = ReadU16(headOffset + 18);
        XMin = ReadI16(headOffset + 36);
        YMin = ReadI16(headOffset + 38);
        XMax = ReadI16(headOffset + 40);
        YMax = ReadI16(headOffset + 42);

        (int hheaOffset, _) = _tables["hhea"];
        Ascender = ReadI16(hheaOffset + 4);
        Descender = ReadI16(hheaOffset + 6);
        _numberOfHMetrics = ReadU16(hheaOffset + 34);

        (int maxpOffset, _) = _tables["maxp"];
        _numGlyphs = ReadU16(maxpOffset + 4);

        // hmtx: advance widths (last one repeats for the remaining glyphs).
        (int hmtxOffset, _) = _tables["hmtx"];
        _advances = new ushort[_numGlyphs];
        ushort last = 0;
        for (int i = 0; i < _numGlyphs; i++)
        {
            if (i < _numberOfHMetrics)
            {
                last = ReadU16(hmtxOffset + i * 4);
            }

            _advances[i] = last;
        }

        ParseCmap();
        PostScriptName = ParseName() ?? "EmbeddedFont";
        ItalicAngle = ParseItalicAngle();
    }

    /// <summary>Maps a Unicode code point to a glyph id (0 = .notdef).</summary>
    public int GlyphFor(int codePoint) => _cmap.TryGetValue(codePoint, out int gid) ? gid : 0;

    /// <summary>Advance width of a glyph in 1000-unit em space (PDF text space).</summary>
    public double Advance1000(int glyphId)
    {
        if (glyphId < 0 || glyphId >= _advances.Length || UnitsPerEm == 0)
        {
            return 500;
        }

        return _advances[glyphId] * 1000.0 / UnitsPerEm;
    }

    private void ParseCmap()
    {
        (int cmapOffset, _) = _tables["cmap"];
        int subTables = ReadU16(cmapOffset + 2);
        int best = -1;
        int bestScore = -1;

        for (int i = 0; i < subTables; i++)
        {
            int rec = cmapOffset + 4 + i * 8;
            ushort platform = ReadU16(rec);
            ushort encoding = ReadU16(rec + 2);
            int sub = cmapOffset + (int)ReadU32(rec + 4);
            ushort format = ReadU16(sub);

            // Prefer Windows UCS-4 (format 12) then Windows BMP (format 4).
            int score = format switch
            {
                12 when platform == 3 && encoding == 10 => 3,
                4 when platform == 3 && encoding == 1 => 2,
                4 when platform == 0 => 1,
                _ => 0,
            };

            if (score > bestScore)
            {
                bestScore = score;
                best = sub;
            }
        }

        if (best < 0)
        {
            return;
        }

        ushort chosen = ReadU16(best);
        if (chosen == 4)
        {
            ParseCmap4(best);
        }
        else if (chosen == 12)
        {
            ParseCmap12(best);
        }
    }

    private void ParseCmap4(int sub)
    {
        int segCount = ReadU16(sub + 6) / 2;
        int endBase = sub + 14;
        int startBase = endBase + segCount * 2 + 2;
        int deltaBase = startBase + segCount * 2;
        int rangeBase = deltaBase + segCount * 2;

        for (int s = 0; s < segCount; s++)
        {
            int end = ReadU16(endBase + s * 2);
            int start = ReadU16(startBase + s * 2);
            short delta = ReadI16(deltaBase + s * 2);
            int rangeOffset = ReadU16(rangeBase + s * 2);

            for (int c = start; c <= end && c != 0xFFFF; c++)
            {
                int gid;
                if (rangeOffset == 0)
                {
                    gid = (c + delta) & 0xFFFF;
                }
                else
                {
                    int gi = rangeBase + s * 2 + rangeOffset + (c - start) * 2;
                    gid = ReadU16(gi);
                    if (gid != 0)
                    {
                        gid = (gid + delta) & 0xFFFF;
                    }
                }

                if (gid != 0)
                {
                    _cmap[c] = gid;
                }
            }
        }
    }

    private void ParseCmap12(int sub)
    {
        int groups = (int)ReadU32(sub + 12);
        int rec = sub + 16;
        for (int g = 0; g < groups; g++)
        {
            int start = (int)ReadU32(rec);
            int end = (int)ReadU32(rec + 4);
            int startGlyph = (int)ReadU32(rec + 8);
            for (int c = start; c <= end; c++)
            {
                _cmap[c] = startGlyph + (c - start);
            }

            rec += 12;
        }
    }

    private string? ParseName()
    {
        if (!_tables.TryGetValue("name", out (int Offset, int Length) table))
        {
            return null;
        }

        int count = ReadU16(table.Offset + 2);
        int stringOffset = table.Offset + ReadU16(table.Offset + 4);

        for (int i = 0; i < count; i++)
        {
            int rec = table.Offset + 6 + i * 12;
            ushort platform = ReadU16(rec);
            ushort nameId = ReadU16(rec + 6);
            int length = ReadU16(rec + 8);
            int offset = ReadU16(rec + 10);
            if (nameId != 6)
            {
                continue; // 6 = PostScript name
            }

            int start = stringOffset + offset;
            if (platform == 3 || platform == 0)
            {
                // UTF-16BE
                var sb = new System.Text.StringBuilder();
                for (int k = 0; k + 1 < length; k += 2)
                {
                    sb.Append((char)ReadU16(start + k));
                }

                return sb.ToString();
            }

            return System.Text.Encoding.ASCII.GetString(_data, start, length);
        }

        return null;
    }

    private double ParseItalicAngle()
    {
        if (!_tables.TryGetValue("post", out (int Offset, int Length) post) || post.Length < 8)
        {
            return 0;
        }

        // post table v2 has italicAngle as Fixed at offset 4.
        int fixedValue = (int)ReadU32(post.Offset + 4);
        return fixedValue / 65536.0;
    }

    private ushort ReadU16(int offset) => BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(offset));
    private short ReadI16(int offset) => BinaryPrimitives.ReadInt16BigEndian(_data.AsSpan(offset));
    private uint ReadU32(int offset) => BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(offset));
}
