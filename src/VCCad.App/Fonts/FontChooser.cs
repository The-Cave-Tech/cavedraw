using VCCad.Core.Model;
using VCCad.Pdf;

namespace VCCad.App.Fonts;

/// <summary>The ways the chooser narrows the list.</summary>
public enum FontCategory
{
    All,
    Recent,
    Used,
    Favourites,
}

/// <summary>
/// What the font chooser shows.
///
/// The list, the filtering and the categories live here rather than in the control, for the
/// same reason the Layers panel's traversal does: an operation that reports what the chooser
/// "would" show is worth nothing if the chooser shows something else, and the only way to be
/// sure is for there to be one answer. fonts.families and the popup both come through here.
/// </summary>
public static class FontChooser
{
    /// <summary>The line every row and the preview are set in.</summary>
    public const string PreviewText = "The quick brown fox jumps over the lazy dog";

    /// <summary>Parses a category name, defaulting to All.</summary>
    public static FontCategory Parse(string? name) => (name ?? "all").ToLowerInvariant() switch
    {
        "recent" => FontCategory.Recent,
        "used" => FontCategory.Used,
        "favourites" or "favorites" => FontCategory.Favourites,
        _ => FontCategory.All,
    };

    /// <summary>The name of a category, as the API and the buttons spell it.</summary>
    public static string NameOf(FontCategory category) => category switch
    {
        FontCategory.Recent => "recent",
        FontCategory.Used => "used",
        FontCategory.Favourites => "favourites",
        _ => "all",
    };

    /// <summary>
    /// The families to show, in the order to show them.
    ///
    /// <paramref name="document"/> is only read for <see cref="FontCategory.Used"/>, because
    /// that is the one category whose answer depends on what is open. Everything else is
    /// about the machine and the person's own lists.
    /// </summary>
    public static IReadOnlyList<FontFamilyEntry> Select(
        CadDocument? document, FontCategory category, string? search = null)
    {
        IReadOnlyList<FontFamilyEntry> every = FontCatalog.Families();
        List<FontFamilyEntry> list;

        switch (category)
        {
            case FontCategory.Favourites:
                list = every.Where(f => FontFavourites.Shared.IsFavourite(f.Name)).ToList();
                break;

            case FontCategory.Recent:
                // Ordered by use, not by name: that is what "recent" means. A family that has
                // since been uninstalled simply drops out rather than appearing as a blank.
                list = FontFavourites.Shared.Recents.All
                    .Select(name => every.FirstOrDefault(f =>
                        string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)))
                    .OfType<FontFamilyEntry>()
                    .ToList();
                break;

            case FontCategory.Used:
                list = document is null
                    ? new List<FontFamilyEntry>()
                    : FontUsage.Detail(document)
                        .Select(d => d.Font.BaseFont)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Select(name => every.FirstOrDefault(f =>
                            string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)))
                        .OfType<FontFamilyEntry>()
                        .ToList();
                break;

            default:
                list = every.ToList();
                break;
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            list = list
                .Where(f => f.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        return list;
    }

    /// <summary>
    /// The face a family's row should be drawn in, or null when the machine cannot draw it.
    ///
    /// A row drawn in the default face when its own is missing is a lie the eye cannot catch:
    /// every row would look the same and the list would stop being a specimen sheet. So a
    /// family that cannot be resolved is reported as such rather than drawn in whatever the
    /// renderer falls back to.
    /// </summary>
    public static FontFace? RowFace(FontFamilyEntry family)
        => family.FaceCount > 0 ? family.RegularFace : null;
}
