using System.Buffers.Binary;
using System.Text;

namespace VCCad.Pdf.Fonts;

/// <summary>
/// Wraps a bare Compact Font Format programme (CFF's own container: header
/// <c>01 00 04 02</c>, name/top-dict/string/charstring INDEXes) into a minimal
/// OpenType <c>sfnt</c> with a <c>CFF&nbsp;</c> table.
///
/// PDF embeds bare CFF directly (<c>/FontFile3 /Subtype /Type1C</c> or
/// <c>/CIDFontType0C</c>), but platform font managers — Skia and DirectWrite
/// alike — only load <em>sfnt</em> containers. Handed a bare programme they
/// refuse it, and a caller that does not notice ends up drawing the programme's
/// glyph ids through an unrelated fallback face. Wrapping restores the original
/// outlines, which is what keeps on-canvas text identical to the PDF.
///
/// Only the tables a rasterizer needs to build a typeface and advance glyphs are
/// emitted: <c>head</c>, <c>hhea</c>, <c>maxp</c>, <c>hmtx</c>, <c>cmap</c>,
/// <c>OS/2</c>, <c>name</c>, <c>post</c> and <c>CFF&nbsp;</c>. Per-glyph advances
/// are recovered from the CFF charstrings so spacing matches the original face.
/// </summary>
public static class CffSfnt
{
    /// <summary>sfnt version tag for a CFF-flavoured OpenType font (<c>OTTO</c>).</summary>
    private const uint SfntVersionCff = 0x4F54544Fu;

    /// <summary>
    /// True when <paramref name="program"/> is a bare CFF programme rather than an
    /// sfnt/OpenType container. CFF's header is major=1, minor=0, hdrSize, offSize.
    /// </summary>
    public static bool IsBareCff(ReadOnlySpan<byte> program)
    {
        if (program.Length < 4 || program[0] != 1)
        {
            return false;
        }

        // Reject the sfnt signatures that also start with a plausible byte.
        uint tag = BinaryPrimitives.ReadUInt32BigEndian(program[..4]);
        if (tag is 0x00010000u or 0x4F54544Fu or 0x74727565u or 0x74746366u)
        {
            return false;
        }

        int hdrSize = program[2];
        int offSize = program[3];
        return hdrSize >= 4 && hdrSize <= 16 && offSize is >= 1 and <= 4;
    }

    /// <summary>
    /// Builds a minimal OpenType file around <paramref name="cff"/>. Returns
    /// <c>null</c> when the programme is not parseable CFF.
    /// </summary>
    /// <param name="cff">The bare CFF programme.</param>
    /// <param name="familyName">Name recorded in the <c>name</c> table.</param>
    /// <param name="ascent">Typographic ascender in font units (usually /1000 em).</param>
    /// <param name="descent">Typographic descender in font units (negative).</param>
    /// <param name="bbox">FontBBox as <c>[xMin yMin xMax yMax]</c>, if known.</param>
    /// <param name="unitsPerEm">Font units per em (CFF is normally 1000).</param>
    public static byte[]? Wrap(
        byte[] cff,
        string familyName,
        double ascent = 800,
        double descent = -200,
        double[]? bbox = null,
        int unitsPerEm = 1000)
    {
        if (cff is null || !IsBareCff(cff) || !TryReadCff(cff, out CffInfo info))
        {
            return null;
        }

        int glyphs = info.GlyphCount;
        ushort[] advances = info.Advances;

        short xMin = 0, yMin = 0, xMax = 0, yMax = 0;
        if (bbox is { Length: >= 4 })
        {
            xMin = Clamp16(bbox[0]);
            yMin = Clamp16(bbox[1]);
            xMax = Clamp16(bbox[2]);
            yMax = Clamp16(bbox[3]);
        }

        ushort upem = (ushort)Math.Clamp(unitsPerEm, 16, 16384);
        short asc = Clamp16(ascent * upem / 1000.0);
        short desc = Clamp16(descent * upem / 1000.0);
        ushort advanceMax = 0;
        foreach (ushort a in advances)
        {
            if (a > advanceMax)
            {
                advanceMax = a;
            }
        }

        if (advanceMax == 0)
        {
            advanceMax = upem;
        }

        byte[] head = BuildHead(upem, xMin, yMin, xMax, yMax);
        byte[] hhea = BuildHhea(asc, desc, advanceMax, xMax, glyphs);
        byte[] maxp = BuildMaxp(glyphs);
        byte[] hmtx = BuildHmtx(advances, glyphs, upem);
        byte[] cmap = BuildCmap();
        byte[] os2 = BuildOs2(upem, asc, desc, advanceMax, xMin, yMin, xMax, yMax);
        byte[] name = BuildName(familyName);
        byte[] post = BuildPost();

        var tables = new List<(uint Tag, byte[] Data)>
        {
            (0x43464620u, cff),   // 'CFF '
            (0x4F532F32u, os2),   // 'OS/2'
            (0x636D6170u, cmap),  // 'cmap'
            (0x68656164u, head),  // 'head'
            (0x68686561u, hhea),  // 'hhea'
            (0x686D7478u, hmtx),  // 'hmtx'
            (0x6D617870u, maxp),  // 'maxp'
            (0x6E616D65u, name),  // 'name'
            (0x706F7374u, post),  // 'post'
        };
        tables.Sort((a, b) => a.Tag.CompareTo(b.Tag));

        return Assemble(tables);
    }

    // ------------------------------------------------------------------
    // sfnt assembly
    // ------------------------------------------------------------------

    private static byte[] Assemble(List<(uint Tag, byte[] Data)> tables)
    {
        int count = tables.Count;
        int offsetTable = 12 + (count * 16);
        int total = offsetTable;
        foreach ((uint _, byte[] data) in tables)
        {
            total += (data.Length + 3) & ~3;
        }

        byte[] font = new byte[total];
        int entrySelector = 0;
        while ((1 << (entrySelector + 1)) <= count)
        {
            entrySelector++;
        }

        int searchRange = 16 * (1 << entrySelector);
        BinaryPrimitives.WriteUInt32BigEndian(font.AsSpan(0), SfntVersionCff);
        BinaryPrimitives.WriteUInt16BigEndian(font.AsSpan(4), (ushort)count);
        BinaryPrimitives.WriteUInt16BigEndian(font.AsSpan(6), (ushort)searchRange);
        BinaryPrimitives.WriteUInt16BigEndian(font.AsSpan(8), (ushort)entrySelector);
        BinaryPrimitives.WriteUInt16BigEndian(font.AsSpan(10), (ushort)((count * 16) - searchRange));

        int pos = offsetTable;
        int record = 12;
        foreach ((uint tag, byte[] data) in tables)
        {
            BinaryPrimitives.WriteUInt32BigEndian(font.AsSpan(record), tag);
            BinaryPrimitives.WriteUInt32BigEndian(font.AsSpan(record + 4), TableChecksum(data));
            BinaryPrimitives.WriteUInt32BigEndian(font.AsSpan(record + 8), (uint)pos);
            BinaryPrimitives.WriteUInt32BigEndian(font.AsSpan(record + 12), (uint)data.Length);
            data.CopyTo(font.AsSpan(pos));
            pos += (data.Length + 3) & ~3;
            record += 16;
        }

        // head.checkSumAdjustment: 0xB1B0AFBA minus the sum of the whole file. The
        // head table is located through the record we just wrote.
        int headOffset = -1;
        for (int i = 0; i < count; i++)
        {
            if (BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(12 + (i * 16))) == 0x68656164u)
            {
                headOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(12 + (i * 16) + 8));
                break;
            }
        }

        if (headOffset >= 0)
        {
            uint adjustment = 0xB1B0AFBAu - TableChecksum(font);
            BinaryPrimitives.WriteUInt32BigEndian(font.AsSpan(headOffset + 8), adjustment);
        }

        return font;
    }

    private static uint TableChecksum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        int i = 0;
        for (; i + 4 <= data.Length; i += 4)
        {
            sum += BinaryPrimitives.ReadUInt32BigEndian(data[i..]);
        }

        if (i < data.Length)
        {
            Span<byte> tail = stackalloc byte[4];
            data[i..].CopyTo(tail);
            sum += BinaryPrimitives.ReadUInt32BigEndian(tail);
        }

        return sum;
    }

    // ------------------------------------------------------------------
    // required tables
    // ------------------------------------------------------------------

    private static byte[] BuildHead(ushort unitsPerEm, short xMin, short yMin, short xMax, short yMax)
    {
        byte[] t = new byte[54];
        BinaryPrimitives.WriteUInt32BigEndian(t.AsSpan(0), 0x00010000u);
        BinaryPrimitives.WriteUInt32BigEndian(t.AsSpan(4), 0x00010000u);
        BinaryPrimitives.WriteUInt32BigEndian(t.AsSpan(8), 0); // checkSumAdjustment
        BinaryPrimitives.WriteUInt32BigEndian(t.AsSpan(12), 0x5F0F3CF5u);
        BinaryPrimitives.WriteUInt16BigEndian(t.AsSpan(16), 0x0003);
        BinaryPrimitives.WriteUInt16BigEndian(t.AsSpan(18), unitsPerEm);
        // created/modified left at 0: CFF carries no reliable timestamp for us.
        BinaryPrimitives.WriteInt16BigEndian(t.AsSpan(36), xMin);
        BinaryPrimitives.WriteInt16BigEndian(t.AsSpan(38), yMin);
        BinaryPrimitives.WriteInt16BigEndian(t.AsSpan(40), xMax);
        BinaryPrimitives.WriteInt16BigEndian(t.AsSpan(42), yMax);
        BinaryPrimitives.WriteUInt16BigEndian(t.AsSpan(44), 0); // macStyle
        BinaryPrimitives.WriteUInt16BigEndian(t.AsSpan(46), 8); // lowestRecPPEM
        BinaryPrimitives.WriteInt16BigEndian(t.AsSpan(48), 2);  // fontDirectionHint
        BinaryPrimitives.WriteInt16BigEndian(t.AsSpan(50), 0);  // indexToLocFormat
        BinaryPrimitives.WriteInt16BigEndian(t.AsSpan(52), 0);  // glyphDataFormat
        return t;
    }

    private static byte[] BuildHhea(short ascender, short descender, ushort advanceMax, short xMaxExtent, int glyphCount)
    {
        byte[] t = new byte[36];
        BinaryPrimitives.WriteUInt32BigEndian(t.AsSpan(0), 0x00010000u);
        BinaryPrimitives.WriteInt16BigEndian(t.AsSpan(4), ascender);
        BinaryPrimitives.WriteInt16BigEndian(t.AsSpan(6), descender);
        BinaryPrimitives.WriteInt16BigEndian(t.AsSpan(8), 0); // lineGap
        BinaryPrimitives.WriteUInt16BigEndian(t.AsSpan(10), advanceMax);
        BinaryPrimitives.WriteInt16BigEndian(t.AsSpan(12), 0);
        BinaryPrimitives.WriteInt16BigEndian(t.AsSpan(14), 0);
        BinaryPrimitives.WriteInt16BigEndian(t.AsSpan(16), xMaxExtent);
        BinaryPrimitives.WriteInt16BigEndian(t.AsSpan(18), 1); // caretSlopeRise
        BinaryPrimitives.WriteInt16BigEndian(t.AsSpan(20), 0); // caretSlopeRun
        BinaryPrimitives.WriteInt16BigEndian(t.AsSpan(22), 0); // caretOffset
        BinaryPrimitives.WriteInt16BigEndian(t.AsSpan(32), 0); // metricDataFormat
        BinaryPrimitives.WriteUInt16BigEndian(t.AsSpan(34), (ushort)Math.Min(glyphCount, ushort.MaxValue));
        return t;
    }

    private static byte[] BuildMaxp(int glyphCount)
    {
        byte[] t = new byte[6];
        BinaryPrimitives.WriteUInt32BigEndian(t.AsSpan(0), 0x00005000u); // version 0.5 (CFF)
        BinaryPrimitives.WriteUInt16BigEndian(t.AsSpan(4), (ushort)Math.Min(glyphCount, ushort.MaxValue));
        return t;
    }

    private static byte[] BuildHmtx(ushort[] advances, int glyphCount, int unitsPerEm)
    {
        byte[] t = new byte[glyphCount * 4];
        for (int g = 0; g < glyphCount; g++)
        {
            ushort advance = g < advances.Length && advances[g] != 0 ? advances[g] : (ushort)unitsPerEm;
            BinaryPrimitives.WriteUInt16BigEndian(t.AsSpan(g * 4), advance);
            BinaryPrimitives.WriteInt16BigEndian(t.AsSpan((g * 4) + 2), 0);
        }

        return t;
    }

    /// <summary>
    /// A format-4 cmap with a single terminating segment. We draw by glyph id, so
    /// the table only has to exist and be well formed for rasterizers that insist
    /// on one.
    /// </summary>
    private static byte[] BuildCmap()
    {
        byte[] t = new byte[12 + 24];
        BinaryPrimitives.WriteUInt16BigEndian(t.AsSpan(0), 0);   // version
        BinaryPrimitives.WriteUInt16BigEndian(t.AsSpan(2), 1);   // numTables
        BinaryPrimitives.WriteUInt16BigEndian(t.AsSpan(4), 3);   // platformID: Windows
        BinaryPrimitives.WriteUInt16BigEndian(t.AsSpan(6), 1);   // encodingID: Unicode BMP
        BinaryPrimitives.WriteUInt32BigEndian(t.AsSpan(8), 12);  // offset

        int s = 12;
        BinaryPrimitives.WriteUInt16BigEndian(t.AsSpan(s), 4);      // format
        BinaryPrimitives.WriteUInt16BigEndian(t.AsSpan(s + 2), 24); // length
        BinaryPrimitives.WriteUInt16BigEndian(t.AsSpan(s + 4), 0);  // language
        BinaryPrimitives.WriteUInt16BigEndian(t.AsSpan(s + 6), 2);  // segCountX2
        BinaryPrimitives.WriteUInt16BigEndian(t.AsSpan(s + 8), 2);  // searchRange
        BinaryPrimitives.WriteUInt16BigEndian(t.AsSpan(s + 10), 0); // entrySelector
        BinaryPrimitives.WriteUInt16BigEndian(t.AsSpan(s + 12), 0); // rangeShift
        BinaryPrimitives.WriteUInt16BigEndian(t.AsSpan(s + 14), 0xFFFF); // endCode
        BinaryPrimitives.WriteUInt16BigEndian(t.AsSpan(s + 16), 0);      // reservedPad
        BinaryPrimitives.WriteUInt16BigEndian(t.AsSpan(s + 18), 0xFFFF); // startCode
        BinaryPrimitives.WriteInt16BigEndian(t.AsSpan(s + 20), 1);       // idDelta
        BinaryPrimitives.WriteUInt16BigEndian(t.AsSpan(s + 22), 0);      // idRangeOffset
        return t;
    }

    /// <summary>
    /// A version-4 <c>OS/2</c> table (96 bytes). Rasterizers use its metrics for
    /// line height and fallback sizing, so the ascender/descender are carried
    /// through rather than left at zero.
    /// </summary>
    private static byte[] BuildOs2(
        ushort unitsPerEm, short ascender, short descender, ushort advanceMax,
        short xMin, short yMin, short xMax, short yMax)
    {
        byte[] t = new byte[96];
        BinaryPrimitives.WriteUInt16BigEndian(t.AsSpan(0), 4);                 // version
        BinaryPrimitives.WriteInt16BigEndian(t.AsSpan(2), (short)Math.Clamp((int)advanceMax, 0, short.MaxValue)); // xAvgCharWidth
        BinaryPrimitives.WriteUInt16BigEndian(t.AsSpan(4), 400);               // usWeightClass
        BinaryPrimitives.WriteUInt16BigEndian(t.AsSpan(6), 5);                 // usWidthClass
        BinaryPrimitives.WriteUInt16BigEndian(t.AsSpan(8), 0);                 // fsType
        BinaryPrimitives.WriteInt16BigEndian(t.AsSpan(30), 0);                 // sFamilyClass
        t[32] = 2;                                                             // panose: Latin text
        BinaryPrimitives.WriteUInt32BigEndian(t.AsSpan(42), 0xFFFFFFFFu);      // ulUnicodeRange1
        Encoding.ASCII.GetBytes("VCCD").CopyTo(t, 58);                         // achVendID
        BinaryPrimitives.WriteUInt16BigEndian(t.AsSpan(62), 0x0040);           // fsSelection: regular
        BinaryPrimitives.WriteUInt16BigEndian(t.AsSpan(64), 0x0020);           // usFirstCharIndex
        BinaryPrimitives.WriteUInt16BigEndian(t.AsSpan(66), 0xFFFF);           // usLastCharIndex
        BinaryPrimitives.WriteInt16BigEndian(t.AsSpan(68), ascender);          // sTypoAscender
        BinaryPrimitives.WriteInt16BigEndian(t.AsSpan(70), descender);         // sTypoDescender
        BinaryPrimitives.WriteInt16BigEndian(t.AsSpan(72), 0);                 // sTypoLineGap
        BinaryPrimitives.WriteUInt16BigEndian(t.AsSpan(74), (ushort)Math.Clamp((int)ascender, 0, short.MaxValue)); // usWinAscent
        BinaryPrimitives.WriteUInt16BigEndian(t.AsSpan(76), (ushort)Math.Clamp(-(int)descender, 0, short.MaxValue)); // usWinDescent
        BinaryPrimitives.WriteUInt32BigEndian(t.AsSpan(78), 1);                // ulCodePageRange1: Latin 1
        BinaryPrimitives.WriteInt16BigEndian(t.AsSpan(86), 0);                 // sxHeight
        BinaryPrimitives.WriteInt16BigEndian(t.AsSpan(88), 0);                 // sCapHeight
        BinaryPrimitives.WriteUInt16BigEndian(t.AsSpan(90), 0x0020);           // usDefaultChar
        BinaryPrimitives.WriteUInt16BigEndian(t.AsSpan(92), 0x0020);           // usBreakChar
        BinaryPrimitives.WriteUInt16BigEndian(t.AsSpan(94), 0);                // usMaxContext
        _ = unitsPerEm;
        _ = xMin;
        _ = yMin;
        _ = xMax;
        _ = yMax;
        return t;
    }

    private static byte[] BuildPost()
    {
        byte[] t = new byte[32];
        BinaryPrimitives.WriteUInt32BigEndian(t.AsSpan(0), 0x00030000u); // version 3.0
        return t;
    }

    private static byte[] BuildName(string familyName)
    {
        // Windows/Unicode-BMP records for nameID 1 (family) and 6 (PostScript name).
        byte[] family = Encoding.BigEndianUnicode.GetBytes(familyName);
        string ps = new string(familyName.Where(char.IsLetterOrDigit).ToArray());
        if (ps.Length == 0)
        {
            ps = "VCCadEmbedded";
        }

        byte[] postScript = Encoding.BigEndianUnicode.GetBytes(ps);

        const int recordCount = 2;
        int stringOffset = 6 + (recordCount * 12);
        byte[] t = new byte[stringOffset + family.Length + postScript.Length];

        BinaryPrimitives.WriteUInt16BigEndian(t.AsSpan(0), 0); // format
        BinaryPrimitives.WriteUInt16BigEndian(t.AsSpan(2), recordCount);
        BinaryPrimitives.WriteUInt16BigEndian(t.AsSpan(4), (ushort)stringOffset);

        int r = 6;
        WriteNameRecord(t, r, 1, family.Length, 0);
        WriteNameRecord(t, r + 12, 6, postScript.Length, family.Length);

        family.CopyTo(t.AsSpan(stringOffset));
        postScript.CopyTo(t.AsSpan(stringOffset + family.Length));
        return t;
    }

    private static void WriteNameRecord(byte[] t, int at, ushort nameId, int length, int offset)
    {
        BinaryPrimitives.WriteUInt16BigEndian(t.AsSpan(at), 3);      // platformID: Windows
        BinaryPrimitives.WriteUInt16BigEndian(t.AsSpan(at + 2), 1);  // encodingID: Unicode BMP
        BinaryPrimitives.WriteUInt16BigEndian(t.AsSpan(at + 4), 0x409); // en-US
        BinaryPrimitives.WriteUInt16BigEndian(t.AsSpan(at + 6), nameId);
        BinaryPrimitives.WriteUInt16BigEndian(t.AsSpan(at + 8), (ushort)length);
        BinaryPrimitives.WriteUInt16BigEndian(t.AsSpan(at + 10), (ushort)offset);
    }

    private static short Clamp16(double value)
        => (short)Math.Clamp(double.IsFinite(value) ? (int)Math.Round(value) : 0, short.MinValue, short.MaxValue);

    // ------------------------------------------------------------------
    // CFF internals: glyph count and per-glyph advances
    // ------------------------------------------------------------------

    private sealed class CffInfo
    {
        public int GlyphCount { get; init; }

        public ushort[] Advances { get; init; } = Array.Empty<ushort>();
    }

    /// <summary>
    /// Reads the glyph count from the CharStrings INDEX and each glyph's advance
    /// from its Type 2 charstring width operand. Wrong advances mis-space a
    /// <c>GlyphRun</c>, so they are worth recovering rather than guessing.
    /// </summary>
    private static bool TryReadCff(byte[] cff, out CffInfo info)
    {
        info = null!;
        try
        {
            int pos = cff[2]; // hdrSize
            if (!ReadIndex(cff, ref pos, out _, out _, out _)) return false;                 // Name INDEX
            if (!ReadIndex(cff, ref pos, out int topStart, out int topEnd, out _)) return false; // Top DICT
            if (!ReadIndex(cff, ref pos, out _, out _, out _)) return false;                 // String INDEX

            int? charStrings = null;
            int? privateSize = null;
            int? privateOffset = null;
            ReadTopDict(cff, topStart, topEnd, ref charStrings, ref privateSize, ref privateOffset);
            if (charStrings is null)
            {
                return false;
            }

            int csPos = charStrings.Value;
            if (!ReadIndex(cff, ref csPos, out int csDataStart, out _, out int[] csOffsets) || csOffsets.Length < 2)
            {
                return false;
            }

            int glyphCount = csOffsets.Length - 1;
            int defaultWidthX = 0;
            int nominalWidthX = 0;
            if (privateSize is > 0 && privateOffset is >= 0 &&
                privateOffset.Value + privateSize.Value <= cff.Length)
            {
                ReadPrivateDict(cff, privateOffset.Value, privateOffset.Value + privateSize.Value,
                    ref defaultWidthX, ref nominalWidthX);
            }

            ushort[] advances = new ushort[glyphCount];
            for (int g = 0; g < glyphCount; g++)
            {
                int start = csDataStart + csOffsets[g] - 1;
                int end = csDataStart + csOffsets[g + 1] - 1;
                if (start < 0 || end > cff.Length || end <= start)
                {
                    advances[g] = (ushort)Math.Clamp(defaultWidthX, 0, ushort.MaxValue);
                    continue;
                }

                int width = ReadCharstringWidth(cff, start, end, defaultWidthX, nominalWidthX);
                advances[g] = (ushort)Math.Clamp(width, 0, ushort.MaxValue);
            }

            info = new CffInfo { GlyphCount = glyphCount, Advances = advances };
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void ReadTopDict(byte[] d, int start, int end, ref int? charStrings,
        ref int? privateSize, ref int? privateOffset)
    {
        var operands = new List<double>();
        int pos = start;
        while (pos < end)
        {
            int b = d[pos];
            if (b <= 21)
            {
                pos++;
                int op = b == 12 && pos < end ? 1200 + d[pos++] : b;
                if (op == 17 && operands.Count > 0) charStrings = (int)operands[^1];
                if (op == 18 && operands.Count >= 2)
                {
                    privateSize = (int)operands[^2];
                    privateOffset = (int)operands[^1];
                }

                operands.Clear();
                continue;
            }

            if (b == 30)
            {
                pos++;
                while (pos < end && (d[pos] & 0x0F) != 0x0F && (d[pos] >> 4) != 0x0F)
                {
                    pos++;
                }

                pos++;
                operands.Add(0);
                continue;
            }

            operands.Add(ReadCffNumber(d, ref pos));
        }
    }

    private static void ReadPrivateDict(byte[] d, int start, int end, ref int defaultWidthX, ref int nominalWidthX)
    {
        var operands = new List<double>();
        int pos = start;
        while (pos < end)
        {
            int b = d[pos];
            if (b <= 21)
            {
                pos++;
                int op = b == 12 && pos < end ? 1200 + d[pos++] : b;
                if (op == 20 && operands.Count > 0) defaultWidthX = (int)operands[^1];
                if (op == 21 && operands.Count > 0) nominalWidthX = (int)operands[^1];
                operands.Clear();
                continue;
            }

            if (b == 30)
            {
                pos++;
                while (pos < end && (d[pos] & 0x0F) != 0x0F && (d[pos] >> 4) != 0x0F)
                {
                    pos++;
                }

                pos++;
                operands.Add(0);
                continue;
            }

            operands.Add(ReadCffNumber(d, ref pos));
        }
    }

    /// <summary>Decodes one CFF DICT operand, advancing <paramref name="pos"/>.</summary>
    private static double ReadCffNumber(byte[] d, ref int pos)
    {
        int b = d[pos];
        if (b == 28)
        {
            short v = BinaryPrimitives.ReadInt16BigEndian(d.AsSpan(pos + 1, 2));
            pos += 3;
            return v;
        }

        if (b == 29)
        {
            int v = BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(pos + 1, 4));
            pos += 5;
            return v;
        }

        if (b == 30)
        {
            // Real number: packed BCD nibbles terminated by 0xF.
            pos++;
            var sb = new StringBuilder();
            while (pos < d.Length)
            {
                int value = d[pos++];
                foreach (int nibble in new[] { value >> 4, value & 0x0F })
                {
                    if (nibble == 0x0F)
                    {
                        return double.TryParse(sb.ToString(), System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double r) ? r : 0;
                    }

                    sb.Append(nibble switch
                    {
                        0x0A => '.',
                        0x0B => 'E',
                        0x0C => 'E',
                        0x0D => '-',
                        0x0E => '-',
                        _ => (char)('0' + nibble),
                    });
                }
            }

            return 0;
        }

        if (b is >= 32 and <= 246) { pos += 1; return b - 139; }
        if (b is >= 247 and <= 250) { pos += 2; return ((b - 247) * 256) + d[pos - 1] + 108; }
        if (b is >= 251 and <= 254) { pos += 2; return (-(b - 251) * 256) - d[pos - 1] - 108; }

        pos += 1;
        return 0;
    }

    /// <summary>
    /// Recovers a Type 2 glyph's advance: the optional leading width operand of the
    /// first stack-clearing operator. When absent the Private DICT's
    /// <c>defaultWidthX</c> applies; otherwise it is <c>nominalWidthX + delta</c>.
    /// </summary>
    private static int ReadCharstringWidth(byte[] d, int start, int end, int defaultWidthX, int nominalWidthX)
    {
        int pos = start;
        int operands = 0;
        double first = 0;
        bool haveFirst = false;

        while (pos < end)
        {
            int b = d[pos];
            if (b == 28)
            {
                Push(BinaryPrimitives.ReadInt16BigEndian(d.AsSpan(pos + 1, 2)));
                pos += 3;
                continue;
            }

            if (b == 255)
            {
                Push(BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(pos + 1, 4)) / 65536.0);
                pos += 5;
                continue;
            }

            if (b >= 32)
            {
                Push(ReadCharstringOperand(d, ref pos));
                continue;
            }

            // Operator: the width, if present, is the first operand of the first
            // stack-clearing operator, so the first one decides.
            pos++;
            int op = b;
            if (op == 12 && pos < end)
            {
                op = 1200 + d[pos++];
            }

            bool widthConsumed = op switch
            {
                1 or 3 or 18 or 23 => operands % 2 == 1,        // h/vstem(h)m
                19 or 20 => operands % 2 == 1,                  // hintmask / cntrmask
                21 => operands == 3,                            // rmoveto
                4 or 22 => operands == 2,                       // vmoveto / hmoveto
                14 => operands is 1 or 5,                       // endchar
                _ => false,
            };

            return widthConsumed
                ? nominalWidthX + (int)Math.Round(first)
                : defaultWidthX;

            void Push(double value)
            {
                if (!haveFirst)
                {
                    first = value;
                    haveFirst = true;
                }

                operands++;
            }
        }

        return defaultWidthX;
    }

    private static double ReadCharstringOperand(byte[] d, ref int pos)
    {
        int b = d[pos];
        if (b is >= 32 and <= 246) { pos += 1; return b - 139; }
        if (b is >= 247 and <= 250) { pos += 2; return ((b - 247) * 256) + d[pos - 1] + 108; }
        if (b is >= 251 and <= 254) { pos += 2; return (-(b - 251) * 256) - d[pos - 1] - 108; }
        pos += 1;
        return 0;
    }

    /// <summary>
    /// Reads a CFF INDEX, returning the element offsets (one more entry than the
    /// element count) so callers can slice individual elements.
    /// </summary>
    private static bool ReadIndex(byte[] d, ref int pos, out int bodyStart, out int bodyEnd, out int[] offsets)
    {
        bodyStart = bodyEnd = pos;
        offsets = Array.Empty<int>();
        if (pos + 2 > d.Length)
        {
            return false;
        }

        int count = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(pos, 2));
        pos += 2;
        if (count == 0)
        {
            offsets = new[] { 1 };
            return true;
        }

        int offSize = d[pos++];
        if (offSize is < 1 or > 4)
        {
            return false;
        }

        int offsBase = pos;
        int dataStart = offsBase + ((count + 1) * offSize);
        if (dataStart > d.Length)
        {
            return false;
        }

        offsets = new int[count + 1];
        for (int i = 0; i <= count; i++)
        {
            int v = 0;
            for (int b = 0; b < offSize; b++)
            {
                v = (v << 8) | d[offsBase + (i * offSize) + b];
            }

            offsets[i] = v;
        }

        bodyStart = dataStart + offsets[0] - 1;
        bodyEnd = dataStart + offsets[^1] - 1;
        pos = bodyEnd;
        return true;
    }
}
