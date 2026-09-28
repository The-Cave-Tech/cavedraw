namespace VCCad.Pdf;

/// <summary>The family a standard PDF font belongs to.</summary>
public enum StandardFontKind
{
    /// <summary>Helvetica and friends.</summary>
    Sans,

    /// <summary>Times and friends.</summary>
    Serif,

    /// <summary>Courier and friends.</summary>
    Mono,

    /// <summary>Symbol.</summary>
    Symbol,

    /// <summary>ZapfDingbats.</summary>
    Dingbats,
}

/// <summary>One of the faces a standard PDF font can map to.</summary>
/// <param name="Kind">Sans, serif, mono, symbol or dingbats.</param>
/// <param name="Bold">Whether the bold face is wanted.</param>
/// <param name="Italic">Whether the italic/oblique face is wanted.</param>
public readonly record struct StandardFace(StandardFontKind Kind, bool Bold, bool Italic);

/// <summary>
/// The standard PDF fonts — the fixed set a file may use *without embedding them*.
///
/// PDF defines exactly fourteen (ISO 32000-1 §9.6.2.2): Courier, Helvetica and Times
/// in four styles each, plus Symbol and ZapfDingbats. A viewer is expected to supply
/// them, which is why a document can name "Helvetica" and embed nothing at all.
///
/// Neither Adobe's originals nor their URW clones can be redistributed inside an MIT
/// application, so this type only classifies a name. Finding an actual face is
/// <see cref="VCCad.App.Fonts.StandardFontCatalog"/>'s job — the same split every
/// viewer makes: recognise the name here, resolve it against the platform there.
///
/// Names are matched by their family word rather than an exhaustive alias list. That
/// is deliberate: the alias space is enormous ("ArialMT", "Helvetica-BoldOblique",
/// "TimesNewRomanPSMT", "CourierStd"…), but every one of them contains the word that
/// identifies its family, so one rule covers the whole set.
/// </summary>
public static class StandardFonts
{
    /// <summary>The fourteen names a PDF may use without embedding anything.</summary>
    public static readonly string[] Base14 =
    {
        "Courier",
        "Courier-Bold",
        "Courier-BoldOblique",
        "Courier-Oblique",
        "Helvetica",
        "Helvetica-Bold",
        "Helvetica-BoldOblique",
        "Helvetica-Oblique",
        "Times-Roman",
        "Times-Bold",
        "Times-BoldItalic",
        "Times-Italic",
        "Symbol",
        "ZapfDingbats",
    };

    /// <summary>
    /// Classifies a PDF font name. Returns false only for an empty name; the family
    /// word is always present in practice, and anything unrecognised is treated as
    /// sans rather than rejected, because a substitute is better than nothing.
    /// </summary>
    /// <param name="pdfFontName">The name from the file, e.g. <c>ABCDEF+Helvetica-Bold</c>.</param>
    /// <param name="bold">Bold flag already derived from the name.</param>
    /// <param name="italic">Italic flag already derived from the name.</param>
    /// <param name="face">The face to substitute.</param>
    public static bool TryResolve(string? pdfFontName, bool bold, bool italic, out StandardFace face)
    {
        face = default;
        if (string.IsNullOrWhiteSpace(pdfFontName))
        {
            return false;
        }

        string name = Normalise(pdfFontName);

        bool isBold = bold || name.Contains("bold", StringComparison.Ordinal) ||
                      name.Contains("demi", StringComparison.Ordinal) ||
                      name.Contains("black", StringComparison.Ordinal) ||
                      name.Contains("heavy", StringComparison.Ordinal);
        bool isItalic = italic || name.Contains("italic", StringComparison.Ordinal) ||
                        name.Contains("oblique", StringComparison.Ordinal);

        StandardFontKind kind;
        if (name.Contains("dingbat", StringComparison.Ordinal))
        {
            kind = StandardFontKind.Dingbats;
            isBold = false;
            isItalic = false;
        }
        else if (name.Contains("symbol", StringComparison.Ordinal))
        {
            kind = StandardFontKind.Symbol;
            isBold = false;
            isItalic = false;
        }
        else if (name.Contains("courier", StringComparison.Ordinal) ||
                 name.Contains("mono", StringComparison.Ordinal) ||
                 name.Contains("consol", StringComparison.Ordinal))
        {
            kind = StandardFontKind.Mono;
        }
        else if (name.Contains("gothic", StringComparison.Ordinal) ||
                 name.Contains("grotesk", StringComparison.Ordinal) ||
                 name.Contains("grotesque", StringComparison.Ordinal))
        {
            // Century Gothic is a geometric sans; "Century" on its own is ambiguous
            // (Century Schoolbook is a serif), so the family word decides.
            kind = StandardFontKind.Sans;
        }
        else if (name.Contains("times", StringComparison.Ordinal) ||
                 name.Contains("serif", StringComparison.Ordinal) ||
                 name.Contains("roman", StringComparison.Ordinal) ||
                 name.Contains("garamond", StringComparison.Ordinal) ||
                 name.Contains("schoolbook", StringComparison.Ordinal) ||
                 name.Contains("bookman", StringComparison.Ordinal) ||
                 name.Contains("palatino", StringComparison.Ordinal) ||
                 name.Contains("georgia", StringComparison.Ordinal))
        {
            kind = StandardFontKind.Serif;
        }
        else
        {
            kind = StandardFontKind.Sans;
        }

        face = new StandardFace(kind, isBold, isItalic);
        return true;
    }

    /// <summary>True when the name is literally one of the fourteen.</summary>
    public static bool IsBase14(string? pdfFontName)
    {
        if (string.IsNullOrWhiteSpace(pdfFontName))
        {
            return false;
        }

        string name = pdfFontName.Trim();
        int plus = name.IndexOf('+');
        if (plus >= 0)
        {
            name = name[(plus + 1)..];
        }

        return Base14.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The URW Core 35 family for a face. These are the fonts Ghostscript and
    /// Inkscape use, and they carry the original metrics.
    /// </summary>
    public static string UrwFamily(StandardFace face) => face.Kind switch
    {
        StandardFontKind.Serif => "Nimbus Roman",
        StandardFontKind.Mono => "Nimbus Mono PS",
        StandardFontKind.Symbol => "Standard Symbols PS",
        StandardFontKind.Dingbats => "D050000L",
        _ => "Nimbus Sans",
    };

    /// <summary>The URW file name (without directory) for a face.</summary>
    public static string UrwFileName(StandardFace face)
    {
        string stem = face.Kind switch
        {
            StandardFontKind.Serif => "NimbusRoman",
            StandardFontKind.Mono => "NimbusMonoPS",
            StandardFontKind.Symbol => "StandardSymbolsPS",
            StandardFontKind.Dingbats => "D050000L",
            _ => "NimbusSans",
        };

        return face.Kind is StandardFontKind.Symbol or StandardFontKind.Dingbats
            ? stem + ".otf"
            : $"{stem}-{Style(face)}.otf";
    }

    /// <summary>
    /// The family a platform clone normally has. Arial is metrically identical to
    /// Helvetica, Times New Roman to Times, Courier New to Courier — which is why
    /// every viewer, Chrome included, substitutes these when the file embeds nothing.
    /// </summary>
    public static string? CloneFamily(StandardFace face) => face.Kind switch
    {
        StandardFontKind.Sans => "Arial",
        StandardFontKind.Serif => "Times New Roman",
        StandardFontKind.Mono => "Courier New",
        _ => null,
    };

    /// <summary>Strips a subset prefix (<c>ABCDEF+</c>) and normalises separators.</summary>
    private static string Normalise(string name)
    {
        string value = name.Trim();
        int plus = value.IndexOf('+');
        if (plus >= 0 && plus <= 6)
        {
            value = value[(plus + 1)..];
        }

        return value.Replace(" ", string.Empty)
            .Replace("-", string.Empty)
            .Replace(",", string.Empty)
            .ToLowerInvariant();
    }

    private static string Style(StandardFace face) => (face.Bold, face.Italic) switch
    {
        (true, true) => "BoldItalic",
        (true, false) => "Bold",
        (false, true) => "Italic",
        _ => "Regular",
    };
}
