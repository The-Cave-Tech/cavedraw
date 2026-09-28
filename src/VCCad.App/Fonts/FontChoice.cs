using Avalonia.Media;

namespace VCCad.App.Fonts;

/// <summary>
/// One entry in the font chooser's list.
///
/// The row has to carry three things at once: what to print, which face to print it in, and
/// the family's real name for applying. They are not the same string - a family with four
/// faces reads "Adobe Arabic (4)" but is applied as "Adobe Arabic" - and losing that
/// distinction is how a label ends up in the document.
/// </summary>
/// <param name="Name">The family name, as the document records it.</param>
/// <param name="Label">What the row prints, with the face count when there is more than one.</param>
/// <param name="Face">The family to draw the row in, or null when nothing can draw it.</param>
/// <param name="FaceCount">How many faces the family really has.</param>
/// <param name="IsStandard">Whether it is one of the names a PDF may use without embedding.</param>
public sealed record FontChoice(
    string Name,
    string Label,
    FontFamily? Face,
    int FaceCount,
    bool IsStandard)
{
    /// <summary>
    /// Whether this machine can draw the row in its own face.
    ///
    /// False is shown rather than hidden: a row silently drawn in the default looks exactly
    /// like a row drawn correctly, so the list would claim to offer a font it cannot.
    /// </summary>
    public bool Drawable => Face is not null;
}

/// <summary>Builds the chooser's entries from the catalogue.</summary>
public static class FontChoices
{
    /// <summary>Every entry for a family list, in the order given.</summary>
    public static IReadOnlyList<FontChoice> For(IEnumerable<FontFamilyEntry> families)
        => families.Select(For).ToList();

    /// <summary>One entry for a family.</summary>
    public static FontChoice For(FontFamilyEntry family)
    {
        FontFace? face = FontChooser.RowFace(family);

        return new FontChoice(
            family.Name,
            face is null ? $"{family.Label} \u00b7 unavailable" : family.Label,
            face is null ? null : new FontFamily(face.Family),
            family.FaceCount,
            family.IsStandard);
    }
}
