using System.Globalization;
using System.Text;
using VCCad.Core.Model;
using VCCad.Pdf.Fonts;

namespace VCCad.Pdf;

/// <summary>Identity of an embedded font: family + weight + slant.</summary>
internal readonly record struct FontKey(string Family, bool Bold, bool Italic);

/// <summary>
/// Embeds the bundled fonts used by a document into the PDF as Type0/CIDFontType2
/// fonts with a FontFile2 stream (full font, FlateDecode) and a ToUnicode CMap,
/// and exposes the resource names used by the content streams.
/// </summary>
internal sealed class PdfFontEmbedder
{
    private sealed record Entry(string Name, TrueTypeFont Font, int Type0Object);

    private readonly Dictionary<FontKey, Entry> _fonts = new();
    private readonly Dictionary<EmbeddedFont, string> _embedded = new(ReferenceEqualityComparer.Instance);
    private readonly PdfAssembler _assembler;

    public PdfFontEmbedder(PdfAssembler assembler, IReadOnlyDictionary<FontKey, HashSet<int>> usage,
        IReadOnlyList<EmbeddedFont>? embeddedFonts = null)
    {
        _assembler = assembler;

        int embeddedIndex = 0;
        if (embeddedFonts is not null)
        {
            foreach (EmbeddedFont font in embeddedFonts)
            {
                if (_embedded.ContainsKey(font))
                {
                    continue;
                }

                string embeddedName = $"/FE{++embeddedIndex}";
                _embedded[font] = embeddedName;
                BuildEmbeddedFont(font, embeddedName);
            }
        }

        int index = 1;
        foreach ((FontKey key, HashSet<int> codePoints) in usage.OrderBy(k => k.Key.Family)
                     .ThenBy(k => k.Key.Bold).ThenBy(k => k.Key.Italic))
        {
            // A font the document did not embed is supplied by the standard-font chain:
            // the URW Core 35 faces carry the Helvetica/Times/Courier metrics, and their
            // licence explicitly permits embedding them in a PDF. When this machine has
            // none, the run stays a plain standard-font reference — exactly what the
            // source document did — rather than inventing a substitute face.
            StandardFonts.TryResolve(key.Family, key.Bold, key.Italic, out StandardFace face);
            byte[]? program = StandardFontFiles.TryReadProgram(face);

            if (program is null)
            {
                continue;
            }

            TrueTypeFont font = new(program);
            string name = $"/F{index++}";

            int type0 = assembler.Allocate();
            int cidFont = assembler.Allocate();
            int descriptor = assembler.Allocate();
            int fontFile = assembler.Allocate();
            int toUnicode = assembler.Allocate();

            BuildFontFile(fontFile, font);
            BuildDescriptor(descriptor, font, fontFile);
            BuildCidFont(cidFont, font, descriptor, codePoints);
            BuildToUnicode(toUnicode, font, codePoints);
            assembler.SetBody(type0,
                $"<< /Type /Font /Subtype /Type0 /BaseFont /{font.PostScriptName} " +
                $"/Encoding /Identity-H /DescendantFonts [{cidFont} 0 R] /ToUnicode {toUnicode} 0 R >>");

            _fonts[key] = new Entry(name, font, type0);
        }
    }



    /// <summary>True when no text fonts were used.</summary>
    public bool IsEmpty => _fonts.Count == 0 && _embeddedObjectNames.Count == 0;

    /// <summary>Resource name (e.g. /F1) for a font key.</summary>
    public string NameFor(FontKey key) => _fonts[key].Name;

    /// <summary>The parsed font for metrics/glyph lookup.</summary>
    public TrueTypeFont FontFor(FontKey key) => _fonts[key].Font;

    /// <summary>The <c>/Font</c> resource dictionary entry (or empty).</summary>
    public string FontDict()
    {
        var sb = new StringBuilder();
        foreach ((string name, int objectNumber) in _embeddedObjectNames)
        {
            sb.Append(name).Append(' ').Append(objectNumber).Append(" 0 R ");
        }

        foreach (Entry entry in _fonts.Values)
        {
            sb.Append(entry.Name).Append(' ').Append(entry.Type0Object).Append(" 0 R ");
        }

        if (sb.Length == 0)
        {
            return string.Empty;
        }

        return "/Font << " + sb + ">> ";
    }

    /// <summary>Resource name for an imported embedded font.</summary>
    public string NameForEmbedded(EmbeddedFont font) => _embedded[font];

    /// <summary>Emits an imported programme verbatim so the re-export uses the
    /// exact original face (no substitution).</summary>
    private void BuildEmbeddedFont(EmbeddedFont font, string name)
    {
        if (font.Composite)
        {
            BuildCompositeEmbeddedFont(font, name);
            return;
        }

        int fontObj = _assembler.Allocate();
        int descriptor = _assembler.Allocate();
        int fontFile = _assembler.Allocate();
        int toUnicode = font.ToUnicode is { Length: > 0 } ? _assembler.Allocate() : 0;

        string fileKey = font.Format switch
        {
            EmbeddedFontFormat.Type1 => "FontFile",
            EmbeddedFontFormat.TrueType => "FontFile2",
            _ => "FontFile3",
        };

        string fileExtra = font.Format switch
        {
            EmbeddedFontFormat.Type1C => " /Subtype /Type1C",
            EmbeddedFontFormat.OpenType => " /Subtype /OpenType",
            _ => $"/Length1 {font.Program.Length}",
        };
        fileExtra = fileExtra.StartsWith("/Length1") ? " " + fileExtra : fileExtra;
        _assembler.SetBody(fontFile,
            PdfDocumentExporter.MakeStreamObject(PdfDocumentExporter.Compress(font.Program), fileExtra));

        string bbox = $"[{Num(font.FontBBox[0])} {Num(font.FontBBox[1])} {Num(font.FontBBox[2])} {Num(font.FontBBox[3])}]";
        _assembler.SetBody(descriptor,
            $"<< /Type /FontDescriptor /FontName /{Sanitise(font.BaseFont)} /Flags {font.Flags} " +
            $"/FontBBox {bbox} /ItalicAngle {Num(font.ItalicAngle)} /Ascent {Num(font.Ascent)} " +
            $"/Descent {Num(font.Descent)} /CapHeight {Num(font.CapHeight)} /StemV {Num(font.StemV)} " +
            $"/MissingWidth {Num(font.MissingWidth)} /{fileKey} {fontFile} 0 R >>");

        if (toUnicode != 0)
        {
            _assembler.SetBody(toUnicode,
                PdfDocumentExporter.MakeStreamObject(PdfDocumentExporter.Compress(font.ToUnicode!)));
        }

        string subtype = font.Format is EmbeddedFontFormat.TrueType or EmbeddedFontFormat.OpenType
            ? "TrueType"
            : "Type1";
        string encoding = BuildEncoding(font);
        int lastChar = font.FirstChar + Math.Max(0, font.Widths.Length) - 1;
        string widths = "[" + string.Join(' ', font.Widths.Select(w => Num(w))) + "]";
        string toUniRef = toUnicode != 0 ? $" /ToUnicode {toUnicode} 0 R" : string.Empty;

        _assembler.SetBody(fontObj,
            $"<< /Type /Font /Subtype /{subtype} /BaseFont /{Sanitise(font.BaseFont)} " +
            $"/FirstChar {font.FirstChar} /LastChar {lastChar} /Widths {widths}{encoding} " +
            $"/FontDescriptor {descriptor} 0 R{toUniRef} >>");

        // Map the name to the object number for FontDict().
        _embeddedObjectNames[name] = fontObj;
    }

    private void BuildCompositeEmbeddedFont(EmbeddedFont font, string name)
    {
        int fontObj = _assembler.Allocate();
        int cidFont = _assembler.Allocate();
        int descriptor = _assembler.Allocate();
        int fontFile = _assembler.Allocate();
        int toUnicode = font.ToUnicode is { Length: > 0 } ? _assembler.Allocate() : 0;
        int encodingStream = font.Type0EncodingStream is { Length: > 0 } ? _assembler.Allocate() : 0;

        bool cidType0 = font.DescendantSubtype == "CIDFontType0";
        string fileKey = cidType0 ? "FontFile3" : "FontFile2";
        string fileExtra = cidType0
            ? font.Format == EmbeddedFontFormat.OpenType ? " /Subtype /OpenType" : " /Subtype /CIDFontType0C"
            : $"/Length1 {font.Program.Length}";
        _assembler.SetBody(fontFile,
            PdfDocumentExporter.MakeStreamObject(PdfDocumentExporter.Compress(font.Program),
                fileExtra.StartsWith("/Length1") ? " " + fileExtra : fileExtra));

        string bbox = $"[{Num(font.FontBBox[0])} {Num(font.FontBBox[1])} {Num(font.FontBBox[2])} {Num(font.FontBBox[3])}]";
        _assembler.SetBody(descriptor,
            $"<< /Type /FontDescriptor /FontName /{Sanitise(font.DescendantBaseFont)} /Flags {font.Flags} " +
            $"/FontBBox {bbox} /ItalicAngle {Num(font.ItalicAngle)} /Ascent {Num(font.Ascent)} " +
            $"/Descent {Num(font.Descent)} /CapHeight {Num(font.CapHeight)} /StemV {Num(font.StemV)} " +
            $"/MissingWidth {Num(font.MissingWidth)} /{fileKey} {fontFile} 0 R >>");

        if (toUnicode != 0)
        {
            _assembler.SetBody(toUnicode,
                PdfDocumentExporter.MakeStreamObject(PdfDocumentExporter.Compress(font.ToUnicode!)));
        }

        string cidToGid = font.CidToGidMapStream is { Length: > 0 }
            ? _assembler.Allocate().ToString()
            : "/" + (font.CidToGidMapName ?? "Identity");
        if (font.CidToGidMapStream is { Length: > 0 } mapBytes)
        {
            int mapObj = int.Parse(cidToGid, System.Globalization.CultureInfo.InvariantCulture);
            _assembler.SetBody(mapObj, PdfDocumentExporter.MakeStreamObject(PdfDocumentExporter.Compress(mapBytes)));
            cidToGid = mapObj + " 0 R";
        }

        _assembler.SetBody(cidFont,
            $"<< /Type /Font /Subtype /{font.DescendantSubtype} /BaseFont /{Sanitise(font.DescendantBaseFont)} " +
            $"/CIDSystemInfo << {font.CidSystemInfo} >> /FontDescriptor {descriptor} 0 R " +
            $"/DW {Num(font.DefaultWidth)} /W {font.WidthsSpec} " +
            $"/CIDToGIDMap {(cidToGid.StartsWith('/') ? cidToGid : cidToGid)} >>");

        string encoding;
        if (encodingStream != 0)
        {
            _assembler.SetBody(encodingStream,
                PdfDocumentExporter.MakeStreamObject(PdfDocumentExporter.Compress(font.Type0EncodingStream!)));
            encoding = encodingStream + " 0 R";
        }
        else
        {
            encoding = "/" + (font.Type0Encoding ?? "Identity-H");
        }

        string toUniRef = toUnicode != 0 ? $" /ToUnicode {toUnicode} 0 R" : string.Empty;
        _assembler.SetBody(fontObj,
            $"<< /Type /Font /Subtype /Type0 /BaseFont /{Sanitise(font.BaseFont)} " +
            $"/Encoding {encoding} /DescendantFonts [{cidFont} 0 R]{toUniRef} >>");

        _embeddedObjectNames[name] = fontObj;
    }

    private readonly Dictionary<string, int> _embeddedObjectNames = new();

    private static string BuildEncoding(EmbeddedFont font)
    {
        if (font.EncodingName is { Length: > 0 } name)
        {
            return $" /Encoding /{Sanitise(name)}";
        }

        if (font.Differences.Count == 0 && font.BaseEncoding is null)
        {
            return string.Empty;
        }

        var sb = new StringBuilder(" /Encoding << ");
        if (font.BaseEncoding is { Length: > 0 } baseName)
        {
            sb.Append("/BaseEncoding /").Append(Sanitise(baseName)).Append(' ');
        }

        if (font.Differences.Count > 0)
        {
            sb.Append("/Differences [");
            foreach ((int code, string glyph) in font.Differences)
            {
                sb.Append(code).Append(" /").Append(Sanitise(glyph)).Append(' ');
            }

            sb.Append("] ");
        }

        return sb.Append(">>").ToString();
    }

    private static string Sanitise(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            sb.Append(char.IsLetterOrDigit(c) || c is '+' or '-' or '_' ? c : '_');
        }

        return sb.ToString();
    }

    private void BuildFontFile(int number, TrueTypeFont font)
    {
        byte[] compressed = PdfDocumentExporter.Compress(font.Data);
        // FontFile2 stream: Length1 is the uncompressed font size.
        byte[] body = PdfDocumentExporter.MakeStreamObject(compressed, $"/Length1 {font.Data.Length}");
        _assembler.SetBody(number, body);
    }

    private void BuildDescriptor(int number, TrueTypeFont font, int fontFile)
    {
        double scale = font.UnitsPerEm > 0 ? 1000.0 / font.UnitsPerEm : 1.0;
        string bbox = $"[{Math.Round(font.XMin * scale)} {Math.Round(font.YMin * scale)} " +
                      $"{Math.Round(font.XMax * scale)} {Math.Round(font.YMax * scale)}]";
        _assembler.SetBody(number,
            $"<< /Type /FontDescriptor /FontName /{font.PostScriptName} /Flags 32 " +
            $"/FontBBox {bbox} /ItalicAngle {Num(font.ItalicAngle)} " +
            $"/Ascent {Math.Round(font.Ascender * scale)} /Descent {Math.Round(font.Descender * scale)} " +
            $"/CapHeight {Math.Round(font.Ascender * scale)} /StemV 80 /FontFile2 {fontFile} 0 R >>");
    }

    private void BuildCidFont(int number, TrueTypeFont font, int descriptor, HashSet<int> codePoints)
    {
        var widths = new StringBuilder("[ ");
        foreach (int codePoint in codePoints)
        {
            int gid = font.GlyphFor(codePoint);
            if (gid == 0)
            {
                continue;
            }

            widths.Append(gid).Append(" [").Append(Num(font.Advance1000(gid))).Append("] ");
        }

        widths.Append(']');
        _assembler.SetBody(number,
            $"<< /Type /Font /Subtype /CIDFontType2 /BaseFont /{font.PostScriptName} " +
            $"/CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> " +
            $"/FontDescriptor {descriptor} 0 R /DW 1000 /W {widths} /CIDToGIDMap /Identity >>");
    }

    private void BuildToUnicode(int number, TrueTypeFont font, HashSet<int> codePoints)
    {
        var sb = new StringBuilder();
        sb.Append("/CIDInit /ProcSet findresource begin 12 dict begin begincmap ");
        sb.Append("/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def ");
        sb.Append("/CMapName /Adobe-Identity-UCS def /CMapType 2 def ");
        sb.Append("1 begincodespacerange <0000> <FFFF> endcodespacerange ");

        // Map each used glyph id back to its Unicode code point (BMP only).
        var entries = new List<(int Gid, int Code)>();
        foreach (int codePoint in codePoints)
        {
            if (codePoint is > 0 and <= 0xFFFF)
            {
                int gid = font.GlyphFor(codePoint);
                if (gid != 0)
                {
                    entries.Add((gid, codePoint));
                }
            }
        }

        sb.Append(entries.Count).Append(" beginbfchar ");
        foreach ((int gid, int code) in entries)
        {
            sb.Append('<').Append(gid.ToString("X4", CultureInfo.InvariantCulture)).Append("> ")
              .Append('<').Append(code.ToString("X4", CultureInfo.InvariantCulture)).Append("> ");
        }

        sb.Append("endbfchar endcmap CMapName currentdict /CMap defineresource pop end end");

        byte[] compressed = PdfDocumentExporter.Compress(Encoding.ASCII.GetBytes(sb.ToString()));
        _assembler.SetBody(number, PdfDocumentExporter.MakeStreamObject(compressed));
    }

    private static string Num(double value) => PdfDocumentExporter.Num(value);
}
