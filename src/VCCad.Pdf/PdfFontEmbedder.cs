using System.Globalization;
using System.Text;
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
    private readonly PdfAssembler _assembler;

    public PdfFontEmbedder(PdfAssembler assembler, IReadOnlyDictionary<FontKey, HashSet<int>> usage)
    {
        _assembler = assembler;

        int index = 1;
        foreach ((FontKey key, HashSet<int> codePoints) in usage.OrderBy(k => k.Key.Family)
                     .ThenBy(k => k.Key.Bold).ThenBy(k => k.Key.Italic))
        {
            TrueTypeFont font = BundledFonts.Resolve(key.Family, key.Bold, key.Italic);
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
    public bool IsEmpty => _fonts.Count == 0;

    /// <summary>Resource name (e.g. /F1) for a font key.</summary>
    public string NameFor(FontKey key) => _fonts[key].Name;

    /// <summary>The parsed font for metrics/glyph lookup.</summary>
    public TrueTypeFont FontFor(FontKey key) => _fonts[key].Font;

    /// <summary>Page /Resources dictionary exposing every embedded font.</summary>
    public string ResourcesDict()
    {
        if (_fonts.Count == 0)
        {
            return "/Resources << >>";
        }

        var sb = new StringBuilder("/Resources << /Font << ");
        foreach (Entry entry in _fonts.Values)
        {
            sb.Append(entry.Name).Append(' ').Append(entry.Type0Object).Append(" 0 R ");
        }

        sb.Append(">> >>");
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
