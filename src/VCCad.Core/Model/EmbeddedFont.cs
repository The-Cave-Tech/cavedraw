namespace VCCad.Core.Model;

/// <summary>Container format of an embedded PDF font programme.</summary>
public enum EmbeddedFontFormat
{
    /// <summary>FontFile: Adobe Type 1 (PostScript).</summary>
    Type1,

    /// <summary>FontFile3 /Subtype /Type1C or /CIDFontType0C: Compact Font Format.</summary>
    Type1C,

    /// <summary>FontFile2: TrueType (glyf).</summary>
    TrueType,

    /// <summary>FontFile3 /Subtype /OpenType.</summary>
    OpenType,
}

/// <summary>
/// An embedded font programme extracted from an imported PDF. Keeping the
/// original programme lets us render (and re-export) text with the exact face the
/// document used instead of substituting a bundled font, which is what makes
/// imported text match other compliant renderers.
/// </summary>
public sealed class EmbeddedFont
{
    public EmbeddedFontFormat Format { get; init; } = EmbeddedFontFormat.Type1C;

    /// <summary>Raw (unfiltered) font programme bytes.</summary>
    public byte[] Program { get; init; } = Array.Empty<byte>();

    /// <summary>Composite (Type0) fonts encode multi-byte glyph codes.</summary>
    public bool Composite { get; init; }

    public string BaseFont { get; init; } = "Embedded";

    /// <summary>Simple-font /FirstChar (codes below are relative to it).</summary>
    public int FirstChar { get; init; }

    /// <summary>Simple-font per-code advances in 1/1000 em (may be empty).</summary>
    public double[] Widths { get; init; } = Array.Empty<double>();

    public double MissingWidth { get; init; }

    /// <summary>Original /ToUnicode CMap stream bytes, if any (pass-through).</summary>
    public byte[]? ToUnicode { get; init; }

    /// <summary>Simple-font /Encoding name (WinAnsiEncoding, MacRomanEncoding, ...).</summary>
    public string? EncodingName { get; init; }

    /// <summary>/Encoding /BaseEncoding when the encoding is a dictionary.</summary>
    public string? BaseEncoding { get; init; }

    /// <summary>/Encoding /Differences pairs (code, glyph name), when present.</summary>
    public IReadOnlyList<(int Code, string Name)> Differences { get; init; } = Array.Empty<(int, string)>();

    public int Flags { get; init; } = 4;
    public double[] FontBBox { get; init; } = { 0, 0, 0, 0 };
    public double ItalicAngle { get; init; }
    public double Ascent { get; init; } = 800;
    public double Descent { get; init; } = -200;
    public double CapHeight { get; init; } = 700;
    public double StemV { get; init; } = 80;
}
