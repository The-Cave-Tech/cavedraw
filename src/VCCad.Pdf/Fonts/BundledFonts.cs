using System.Reflection;

namespace VCCad.Pdf.Fonts;

/// <summary>
/// The fonts bundled with VCCad (DejaVu, redistributable) and embedded into every
/// exported PDF. A requested family/weight/italic is resolved to one of these;
/// unknown families fall back to the default sans font.
/// </summary>
public static class BundledFonts
{
    public const string DefaultFamily = "DejaVu Sans";

    private static readonly Dictionary<string, TrueTypeFont> Cache = new();

    private static readonly Dictionary<(string Family, bool Bold, bool Italic), string> Resources = new()
    {
        [("DejaVu Sans", false, false)] = "VCCad.Pdf.Fonts.DejaVuSans.ttf",
        [("DejaVu Sans", true, false)] = "VCCad.Pdf.Fonts.DejaVuSans-Bold.ttf",
        [("DejaVu Sans", false, true)] = "VCCad.Pdf.Fonts.DejaVuSans-Oblique.ttf",
        [("DejaVu Sans", true, true)] = "VCCad.Pdf.Fonts.DejaVuSans-Bold.ttf",
        [("DejaVu Serif", false, false)] = "VCCad.Pdf.Fonts.DejaVuSerif.ttf",
        [("DejaVu Serif", true, false)] = "VCCad.Pdf.Fonts.DejaVuSerif.ttf",
        [("DejaVu Serif", false, true)] = "VCCad.Pdf.Fonts.DejaVuSerif.ttf",
        [("DejaVu Serif", true, true)] = "VCCad.Pdf.Fonts.DejaVuSerif.ttf",
        [("DejaVu Sans Mono", false, false)] = "VCCad.Pdf.Fonts.DejaVuSansMono.ttf",
        [("DejaVu Sans Mono", true, false)] = "VCCad.Pdf.Fonts.DejaVuSansMono.ttf",
        [("DejaVu Sans Mono", false, true)] = "VCCad.Pdf.Fonts.DejaVuSansMono.ttf",
        [("DejaVu Sans Mono", true, true)] = "VCCad.Pdf.Fonts.DejaVuSansMono.ttf",
    };

    /// <summary>Available family names for the UI.</summary>
    public static IReadOnlyList<string> Families { get; } = new[]
    {
        "DejaVu Sans", "DejaVu Serif", "DejaVu Sans Mono",
    };

    /// <summary>Resolves a family/weight/italic to a parsed font (cached).</summary>
    public static TrueTypeFont Resolve(string family, bool bold, bool italic)
    {
        (string fam, bool b, bool i) = Normalize(family, bold, italic);
        string resource = Resources[(fam, b, i)];
        lock (Cache)
        {
            if (Cache.TryGetValue(resource, out TrueTypeFont? font))
            {
                return font;
            }

            using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"Bundled font '{resource}' is missing.");
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            font = new TrueTypeFont(buffer.ToArray());
            Cache[resource] = font;
            return font;
        }
    }

    private static (string Family, bool Bold, bool Italic) Normalize(string family, bool bold, bool italic)
    {
        string fam = Families.FirstOrDefault(f => string.Equals(f, family, StringComparison.OrdinalIgnoreCase))
                     ?? DefaultFamily;
        return (fam, bold, italic);
    }
}
