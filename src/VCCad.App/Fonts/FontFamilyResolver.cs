using Avalonia.Media;
using VCCad.Core.Model;

namespace VCCad.App.Fonts;

/// <summary>
/// One place that turns a run's font family into a <see cref="FontFamily"/>, and never from a blank name.
///
/// **A blank name is not a rendering problem, it is a process-ending one** (issue #213). Avalonia's
/// <c>FontFamily(Uri, string)</c> throws <c>ArgumentNullException</c> for a null name, and both places that build
/// one here are reached while drawing: the paint pass and the text metrics. The crash that was reported came from
/// the paint path and was fixed there - and the metrics path kept the same unguarded construction one call away,
/// which is why this is one function rather than a guard copied to each site.
///
/// **The fallback has to be a real face, not an engine object's name.** Re-deriving a family from
/// <c>FontFamily.Default.Name</c> - a string taken out of a valid object and parsed again - gives a name that need
/// not resolve as a family, and a typeface that yields no glyphs: text laid out, positioned, selectable, black in
/// the model, and invisible on the canvas (issue #221). The blank case therefore asks the same chain that supplies
/// every other run for the face it gives a document that names nothing, and falls back to the clone that chain
/// itself uses.
///
/// **Drawing code has to be total.** A null family is bad data - worth a fallback, worth a note to the person -
/// but it is one run of text in one frame, and the worst acceptable outcome is that it is drawn in another face.
/// </summary>
public static class FontFamilyResolver
{
    /// <summary>The family name to draw a run in, never null or blank.</summary>
    public static string NameFor(TextRun run)
    {
        string? family = StandardFontResolver.FamilyFor(run);
        if (!string.IsNullOrWhiteSpace(family))
        {
            return family;
        }

        string? substitute = StandardFontResolver.FamilyFor(
            new TextRun { Text = string.Empty, FontFamily = "Helvetica" });
        return string.IsNullOrWhiteSpace(substitute) ? "Arial" : substitute;
    }

    /// <summary>The face for a run. Total: it answers for a null family rather than throwing while drawing.</summary>
    public static FontFamily For(TextRun run) => new(NameFor(run));
}
