using System.Buffers.Binary;

namespace VCCad.Pdf;

/// <summary>
/// Minimal Compact Font Format reader: parses the charset so a Unicode character
/// can be mapped to a glyph index via its standard Adobe glyph name. This lets
/// bare CFF programmes (which carry no Unicode cmap) render by glyph id. Only the
/// charset is read — outlines are left to the renderer.
/// </summary>
internal static class CffGlyphMap
{
    /// <summary>SID→GID for the font, or null if the programme is not parseable CFF.</summary>
    public static Dictionary<int, ushort>? SidToGid(byte[] program)
    {
        try
        {
            if (program.Length < 4)
            {
                return null;
            }

            int pos = 4; // skip header
            if (!ReadIndex(program, ref pos, out _, out _)) return null;             // Name INDEX
            if (!ReadIndex(program, ref pos, out int topStart, out int topEnd)) return null; // Top DICT INDEX
            ReadIndex(program, ref pos, out _, out _);                                // String INDEX

            int? charStrings = null;
            int? charset = null;
            ParseTopDict(program, topStart, topEnd, ref charStrings, ref charset);
            if (charStrings is null)
            {
                return null;
            }

            int csPos = charStrings.Value;
            if (csPos < 0 || csPos >= program.Length ||
                !ReadIndex(program, ref csPos, out _, out int glyphCount) || glyphCount <= 0)
            {
                return null;
            }

            var gidToSid = new int[glyphCount];
            if (charset is null || charset.Value == 0)
            {
                for (int g = 1; g < glyphCount; g++)
                {
                    gidToSid[g] = g; // ISOAdobe charset: GID == SID
                }
            }
            else if (!ReadCharset(program, charset.Value, gidToSid))
            {
                return null;
            }

            var sidToGid = new Dictionary<int, ushort>();
            for (int g = 0; g < glyphCount; g++)
            {
                sidToGid.TryAdd(gidToSid[g], (ushort)g);
            }

            return sidToGid;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>ASCII (32..126) maps to standard SID = codepoint - 31.</summary>
    public static int AsciiSid(char ch) => ch is >= (char)32 and <= (char)126 ? ch - 31 : -1;

    private static void ParseTopDict(byte[] d, int start, int end, ref int? charStrings, ref int? charset)
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
                if (op == 15 && operands.Count > 0) charset = (int)operands[^1];
                operands.Clear();
                continue;
            }

            if (b == 30) // real number: consume until a 0x0F nibble
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

            int len;
            double value;
            if (b is >= 32 and <= 246) { len = 1; value = b - 139; }
            else if (b is >= 247 and <= 250) { len = 2; value = ((b - 247) * 256) + d[pos + 1] + 108; }
            else if (b is >= 251 and <= 254) { len = 2; value = (-(b - 251) * 256) - d[pos + 1] - 108; }
            else { len = 5; value = BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(pos + 1, 4)); }

            operands.Add(value);
            pos += len;
        }
    }

    private static bool ReadCharset(byte[] d, int offset, int[] gidToSid)
    {
        if (offset < 0 || offset >= d.Length)
        {
            return false;
        }

        int pos = offset;
        int format = d[pos++];
        int gid = 1;

        if (format == 0)
        {
            while (gid < gidToSid.Length && pos + 1 < d.Length)
            {
                gidToSid[gid++] = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(pos, 2));
                pos += 2;
            }

            return true;
        }

        while (gid < gidToSid.Length && pos + 2 < d.Length)
        {
            int first = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(pos, 2));
            pos += 2;
            int nLeft;
            if (format == 1)
            {
                nLeft = d[pos++];
            }
            else
            {
                nLeft = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(pos, 2));
                pos += 2;
            }

            for (int i = 0; i <= nLeft && gid < gidToSid.Length; i++)
            {
                gidToSid[gid++] = first + i;
            }
        }

        return true;
    }

    private static bool ReadIndex(byte[] d, ref int pos, out int bodyStart, out int bodyEnd)
    {
        bodyStart = bodyEnd = pos;
        if (pos + 2 > d.Length)
        {
            return false;
        }

        int count = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(pos, 2));
        pos += 2;
        if (count == 0)
        {
            return true;
        }

        int offSize = d[pos++];
        int offsBase = pos;
        int dataStart = offsBase + ((count + 1) * offSize); // 0-based index of data
        int first = ReadOffset(d, offsBase, offSize);
        int last = ReadOffset(d, offsBase + (count * offSize), offSize);

        bodyStart = dataStart + first - 1;
        bodyEnd = dataStart + last - 1;
        pos = bodyEnd;
        return true;
    }

    private static int ReadOffset(byte[] d, int at, int size)
    {
        int v = 0;
        for (int i = 0; i < size; i++)
        {
            v = (v << 8) | d[at + i];
        }

        return v;
    }
}
