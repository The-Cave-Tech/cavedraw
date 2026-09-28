using Avalonia.Media;
using SkiaSharp;

namespace VCCad.App.Fonts;

/// <summary>One face of a family: a family name plus the style within it.</summary>
/// <param name="Family">The family this face belongs to, as the platform names it.</param>
/// <param name="Style">The style name shown beside the family, for example "Bold Italic".</param>
/// <param name="Bold">Whether the face is bold.</param>
/// <param name="Italic">Whether the face is italic.</param>
public sealed record FontFace(
    string Family,
    string Style,
    bool Bold,
    bool Italic)
{
    /// <summary>The typeface to render this face with.</summary>
    public Typeface Typeface => new(
        new FontFamily(Family),
        Italic ? FontStyle.Italic : FontStyle.Normal,
        Bold ? FontWeight.Bold : FontWeight.Normal);
}

/// <summary>
/// A family and the faces it really has.
/// </summary>
/// <param name="Name">The family name, as the platform names it.</param>
/// <param name="Faces">Every face the family really has, regular first.</param>
/// <param name="IsStandard">
/// Whether this is one of the fourteen names a PDF may use without embedding, which are
/// offered first and are the ones the font report has to account for separately.
/// </param>
public sealed record FontFamilyEntry(
    string Name,
    IReadOnlyList<FontFace> Faces,
    bool IsStandard)
{
    /// <summary>How many faces the family has. At least one, or it would not be listed.</summary>
    public int FaceCount => Faces.Count;

    /// <summary>
    /// The row's label: the family, and how many faces it carries when there is more than
    /// one. "Adobe Arabic (4)" is a family worth opening; "Agency FB" is not.
    /// </summary>
    public string Label => FaceCount > 1 ? $"{Name} ({FaceCount})" : Name;

    /// <summary>The regular face, or the first one if the family has no regular.</summary>
    public FontFace RegularFace =>
        Faces.FirstOrDefault(f => !f.Bold && !f.Italic) ?? Faces[0];
}

/// <summary>
/// The fonts this machine can actually draw with, grouped into families with their faces.
///
/// The picker used to be a list of family names, which cannot show what a font looks like and
/// cannot say whether a family has a bold. This is the list behind a chooser that can: every
/// row is a family with a real, resolvable face to draw it in.
///
/// Faces are read from the font manager's own index rather than by asking Avalonia for each
/// style in turn. Asking for a style a family does not have does not fail — it makes the
/// engine build a synthetic one — and on this machine that crashes natively for at least one
/// installed font: SimSun-ExtG, a CJK font with tens of thousands of glyphs, takes the whole
/// process down with an AccessViolationException when its bold italic is requested. Nothing
/// catches that, so the question must not be asked. The index already knows which faces exist.
/// </summary>
public static class FontCatalog
{
    /// <summary>
    /// The faces a family really has, regular first, or empty when the machine has no such
    /// family.
    /// </summary>
    public static IReadOnlyList<FontFace> FacesOf(string family)
    {
        if (string.IsNullOrWhiteSpace(family))
        {
            return Array.Empty<FontFace>();
        }

        var faces = new List<FontFace>();
        var seen = new HashSet<(bool Bold, bool Italic)>();

        try
        {
            using SKFontStyleSet? set = SKFontManager.Default.GetFontStyles(family);
            if (set is null || set.Count == 0)
            {
                return Array.Empty<FontFace>();
            }

            for (int i = 0; i < set.Count; i++)
            {
                using SKTypeface? typeface = set.CreateTypeface(i);
                if (typeface is null ||
                    !string.Equals(typeface.FamilyName, family, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                SKFontStyle style = typeface.FontStyle;
                bool bold = style.Weight >= (int)SKFontStyleWeight.SemiBold;
                bool italic = style.Slant != SKFontStyleSlant.Upright;

                if (seen.Add((bold, italic)))
                {
                    faces.Add(new FontFace(family, StyleName(bold, italic), bold, italic));
                }
            }
        }
        catch (Exception)
        {
            return Array.Empty<FontFace>();
        }

        // Regular first, then bold, italic, bold italic, however the index listed them: the
        // regular is the one a family's row is drawn in.
        faces.Sort((a, b) => Rank(a).CompareTo(Rank(b)));
        return faces;
    }

    private static int Rank(FontFace face) => (face.Bold, face.Italic) switch
    {
        (false, false) => 0,
        (true, false) => 1,
        (false, true) => 2,
        _ => 3,
    };

    private static string StyleName(bool bold, bool italic) => (bold, italic) switch
    {
        (false, false) => "Regular",
        (true, false) => "Bold",
        (false, true) => "Italic",
        _ => "Bold Italic",
    };

    /// <summary>
    /// Every family this machine can draw with, standard faces first, then the machine's own,
    /// each with the faces it really has.
    ///
    /// A family with no face at all is left out rather than listed: a name that cannot be
    /// drawn is not a choice, and putting it in the list is how a picker ends up offering
    /// fonts that silently render as something else.
    /// </summary>
    public static IReadOnlyList<FontFamilyEntry> Families()
    {
        var entries = new List<FontFamilyEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string name in StandardFontResolver.StandardFamilyNames())
        {
            if (seen.Add(name) && FacesOf(name) is { Count: > 0 } faces)
            {
                entries.Add(new FontFamilyEntry(name, faces, IsStandard: true));
            }
        }

        foreach (string name in MachineFamilies())
        {
            if (seen.Add(name) && FacesOf(name) is { Count: > 0 } faces)
            {
                entries.Add(new FontFamilyEntry(name, faces, IsStandard: false));
            }
        }

        return entries;
    }

    /// <summary>The family names the platform reports as installed, sorted.</summary>
    private static IEnumerable<string> MachineFamilies()
    {
        var names = new List<string>();

        try
        {
            names.AddRange(SKFontManager.Default.GetFontFamilies());
        }
        catch (Exception)
        {
        }

        if (names.Count == 0)
        {
            // A platform whose font manager will not enumerate still gets Avalonia's list.
            try
            {
                names.AddRange(FontManager.Current.SystemFonts
                    .Select(f => f.Name)
                    .Where(n => !string.IsNullOrWhiteSpace(n)));
            }
            catch (Exception)
            {
            }
        }

        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    /// <summary>A family by name, or null when this machine cannot draw with it.</summary>
    public static FontFamilyEntry? Find(string family)
        => Families().FirstOrDefault(
            f => string.Equals(f.Name, family, StringComparison.OrdinalIgnoreCase));
}
