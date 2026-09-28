using VCCad.Pdf.Parsing;

namespace VCCad.Pdf;

/// <summary>
/// Character encodings for PDF simple fonts (Type1 and non-symbolic TrueType).
///
/// A content stream holds <em>codes</em>, not characters, and a font with no
/// <c>/ToUnicode</c> CMap must be decoded through its <c>/Encoding</c>. Treating the
/// bytes as Latin-1 — which is what this project used to do — is right for ASCII and
/// wrong for everything else: WinAnsi code 0x94 is a right double quotation mark (the
/// inches mark on a pattern), but Latin-1 makes it U+0094, a C1 control character
/// with no glyph anywhere. That is why labels came out as boxes or blank.
///
/// <c>/Differences</c> entries name individual glyphs and override the base encoding,
/// so a glyph-name table is needed alongside the code tables.
/// </summary>
public static class PdfTextEncoding
{
    /// <summary>
    /// Builds a code → text map for a font, or null when the font carries no
    /// encoding information (callers then fall back to the bytes as-is).
    /// </summary>
    /// <param name="file">The file, for resolving indirect objects.</param>
    /// <param name="fontDict">The font dictionary.</param>
    internal static Dictionary<int, string>? ForFont(PdfFile file, Dictionary<string, object?> fontDict)
    {
        object? encoding = file.Resolve(fontDict.GetValueOrDefault("Encoding")); // NOSONAR: PdfFile.Resolve takes object?
        string? baseName;
        object? differences = null;

        switch (encoding)
        {
            case PdfName name:
                baseName = name.Value;
                break;
            case Dictionary<string, object?> dict:
                baseName = (file.Resolve(dict.GetValueOrDefault("BaseEncoding")) as PdfName)?.Value;
                differences = file.Resolve(dict.GetValueOrDefault("Differences"));
                break;
            default:
                baseName = null;
                break;
        }

        // A TrueType font with no /Encoding uses its own cmap, which in practice aligns
        // with WinAnsi for every code that matters; a Type1 font defaults to
        // StandardEncoding. Both are ASCII-identical below 0x80.
        Dictionary<int, string> table = baseName switch
        {
            "MacRomanEncoding" => MacRoman(),
            "WinAnsiEncoding" or "PDFDocEncoding" => WinAnsi(),
            "StandardEncoding" => WinAnsi(),
            null => (fontDict.GetValueOrDefault("Subtype") as PdfName)?.Value == "TrueType"
                ? WinAnsi()
                : WinAnsi(),
            _ => WinAnsi(),
        };

        if (differences is List<object?> list)
        {
            ApplyDifferences(table, list);
        }

        return table;
    }

    private static void ApplyDifferences(Dictionary<int, string> table, List<object?> differences)
    {
        int code = 0;
        foreach (object? entry in differences)
        {
            if (entry is double number)
            {
                code = (int)number;
                continue;
            }

            if (entry is not PdfName name)
            {
                continue;
            }

            string? text = GlyphNameToUnicode(name.Value);
            if (text is not null)
            {
                table[code] = text;
            }

            code++;
        }
    }

    /// <summary>WinAnsiEncoding: Latin-1 with the 0x80–0x9F range filled with typography.</summary>
    private static Dictionary<int, string> WinAnsi()
    {
        var map = new Dictionary<int, string>();
        for (int code = 32; code < 127; code++)
        {
            map[code] = ((char)code).ToString();
        }

        for (int code = 0xA0; code <= 0xFF; code++)
        {
            map[code] = ((char)code).ToString();
        }

        void Put(int code, char ch) => map[code] = ch.ToString();

        Put(0x80, '\u20AC'); // euro
        Put(0x82, '\u201A'); // single low quote
        Put(0x83, '\u0192'); // florin
        Put(0x84, '\u201E'); // double low quote
        Put(0x85, '\u2026'); // ellipsis
        Put(0x86, '\u2020'); // dagger
        Put(0x87, '\u2021'); // double dagger
        Put(0x88, '\u02C6'); // circumflex
        Put(0x89, '\u2030'); // per mille
        Put(0x8A, '\u0160'); // S caron
        Put(0x8B, '\u2039'); // single left angle
        Put(0x8C, '\u0152'); // OE
        Put(0x8E, '\u017D'); // Z caron
        Put(0x91, '\u2018'); // left single quote
        Put(0x92, '\u2019'); // right single quote
        Put(0x93, '\u201C'); // left double quote
        Put(0x94, '\u201D'); // right double quote — the inches mark
        Put(0x95, '\u2022'); // bullet
        Put(0x96, '\u2013'); // en dash
        Put(0x97, '\u2014'); // em dash
        Put(0x98, '\u02DC'); // small tilde
        Put(0x99, '\u2122'); // trademark
        Put(0x9A, '\u0161'); // s caron
        Put(0x9B, '\u203A'); // single right angle
        Put(0x9C, '\u0153'); // oe
        Put(0x9E, '\u017E'); // z caron
        Put(0x9F, '\u0178'); // Y diaeresis
        return map;
    }

    /// <summary>MacRomanEncoding differs from WinAnsi across the whole upper range.</summary>
    private static Dictionary<int, string> MacRoman()
    {
        const string upper =
            "ÄÅÇÉÑÖÜáàâäãåçéèêëíìîïñóòôöõúùûü†°¢£§•¶ß®©™´¨≠ÆØ∞±≤≥¥µ∂∑∏π∫ªºΩæø" +
            "¿¡¬√ƒ≈∆«»…\u00A0ÀÃÕŒœ–—“”‘’÷◊ÿŸ⁄€‹›ﬁﬂ‡·‚„‰ÂÊÁËÈÍÎÏÌÓÔ\uF8FFÒÚÛÙıˆ˜¯˘˙˚¸˝˛ˇ";

        var map = new Dictionary<int, string>();
        for (int code = 32; code < 127; code++)
        {
            map[code] = ((char)code).ToString();
        }

        for (int i = 0; i < upper.Length && 0x80 + i <= 0xFF; i++)
        {
            map[0x80 + i] = upper[i].ToString();
        }

        return map;
    }

    /// <summary>
    /// Adobe glyph names used by <c>/Differences</c>. Covers the punctuation and
    /// accents that appear in real documents; the full list is thousands of entries
    /// and the <c>uniXXXX</c> convention picks up the rest.
    /// </summary>
    private static string? GlyphNameToUnicode(string name)
    {
        if (name.Length == 7 && name.StartsWith("uni", StringComparison.Ordinal) &&
            int.TryParse(name.AsSpan(3), System.Globalization.NumberStyles.HexNumber, null, out int uni))
        {
            return char.ConvertFromUtf32(uni);
        }

        if (name.Length == 5 && name.StartsWith('u') &&
            int.TryParse(name.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out int u4))
        {
            return char.ConvertFromUtf32(u4);
        }

        return Names.TryGetValue(name, out string? text) ? text : null;
    }

    private static readonly Dictionary<string, string> Names = new(StringComparer.Ordinal)
    {
        ["space"] = " ",
        ["quotedbl"] = "\"",
        ["quotesingle"] = "'",
        ["quoteright"] = "\u2019",
        ["quoteleft"] = "\u2018",
        ["quotedblright"] = "\u201D",
        ["quotedblleft"] = "\u201C",
        ["quotedblbase"] = "\u201E",
        ["quotesinglbase"] = "\u201A",
        ["grave"] = "`",
        ["acute"] = "\u00B4",
        ["circumflex"] = "\u02C6",
        ["tilde"] = "\u02DC",
        ["macron"] = "\u00AF",
        ["breve"] = "\u02D8",
        ["dotaccent"] = "\u02D9",
        ["dieresis"] = "\u00A8",
        ["ring"] = "\u02DA",
        ["cedilla"] = "\u00B8",
        ["hungarumlaut"] = "\u02DD",
        ["caron"] = "\u02C7",
        ["endash"] = "\u2013",
        ["emdash"] = "\u2014",
        ["hyphen"] = "-",
        ["periodcentered"] = "\u00B7",
        ["bullet"] = "\u2022",
        ["ellipsis"] = "\u2026",
        ["dagger"] = "\u2020",
        ["daggerdbl"] = "\u2021",
        ["perthousand"] = "\u2030",
        ["Euro"] = "\u20AC",
        ["florin"] = "\u0192",
        ["trademark"] = "\u2122",
        ["copyright"] = "\u00A9",
        ["registered"] = "\u00AE",
        ["degree"] = "\u00B0",
        ["plusminus"] = "\u00B1",
        ["multiply"] = "\u00D7",
        ["divide"] = "\u00F7",
        ["onehalf"] = "\u00BD",
        ["onequarter"] = "\u00BC",
        ["threequarters"] = "\u00BE",
        ["onesuperior"] = "\u00B9",
        ["twosuperior"] = "\u00B2",
        ["threesuperior"] = "\u00B3",
        ["fraction"] = "\u2044",
        ["sterling"] = "\u00A3",
        ["yen"] = "\u00A5",
        ["cent"] = "\u00A2",
        ["section"] = "\u00A7",
        ["paragraph"] = "\u00B6",
        ["germandbls"] = "\u00DF",
        ["AE"] = "\u00C6",
        ["ae"] = "\u00E6",
        ["OE"] = "\u0152",
        ["oe"] = "\u0153",
        ["Oslash"] = "\u00D8",
        ["oslash"] = "\u00F8",
        ["Lslash"] = "\u0141",
        ["lslash"] = "\u0142",
        ["minus"] = "\u2212",
        ["notequal"] = "\u2260",
        ["infinity"] = "\u221E",
        ["lessequal"] = "\u2264",
        ["greaterequal"] = "\u2265",
        ["partialdiff"] = "\u2202",
        ["summation"] = "\u2211",
        ["product"] = "\u220F",
        ["pi"] = "\u03C0",
        ["Omega"] = "\u03A9",
        ["Delta"] = "\u2206",
        ["radical"] = "\u221A",
        ["approxequal"] = "\u2248",
        ["Delta"] = "\u2206",
        ["lozenge"] = "\u25CA",
        ["fi"] = "\uFB01",
        ["fl"] = "\uFB02",
        ["guilsinglleft"] = "\u2039",
        ["guilsinglright"] = "\u203A",
        ["guillemotleft"] = "\u00AB",
        ["guillemotright"] = "\u00BB",
    };
}
