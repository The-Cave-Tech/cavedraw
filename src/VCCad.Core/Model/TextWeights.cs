using VCCad.Core.Model;

namespace VCCad.Core.Model;

/// <summary>
/// **The weight a file asked for, read from the name it asked with** (issue #257).
///
/// A PDF names its faces in the font's base name - `Helvetica-Bold`, `NPFRLV+CenturyGothic-Bold`, `Arial-BoldItalicMT` - and
/// a subset carries a six-letter prefix before a `+`. The weight and the slant are in the words of that name, which is where
/// the importer reads them from.
///
/// But a run also has `Bold` and `Italic` flags, and **nothing re-derived them when a run was split by typing into the middle
/// of it**. So a run could carry `SourceFont = "Helvetica-Bold"` with `Bold = false`, and a character typed into it was drawn
/// with the family's regular face while the text around it stayed bold - which is the reported symptom, and it explains why
/// the family, the resolved family and the run count all read the same either side of the keystroke: the only thing that
/// disagreed was the flag that decides which face is drawn.
///
/// The rule this enforces is the one the issue states: **a run's flags must agree with the source name it carries**, however
/// the run came to be - imported, split by typing, or merged.
/// </summary>
public static partial class TextWeights
{
    /// <summary>The words that mean a heavier face than regular.</summary>
    private static readonly string[] BoldWords =
        ["bold", "semibold", "demibold", "extrabold", "ultrabold", "black", "heavy"];

    /// <summary>The words that mean a slanted face.</summary>
    private static readonly string[] ItalicWords = ["italic", "oblique"];

    /// <summary>Whether a font base name says the face is heavier than regular.</summary>
    public static bool NamesBold(string? sourceFont)
        => NamesAny(sourceFont, BoldWords);

    /// <summary>Whether a font base name says the face is slanted.</summary>
    public static bool NamesItalic(string? sourceFont)
        => NamesAny(sourceFont, ItalicWords);

    /// <summary>
    /// **Whether the name's words say `word`**, ignoring the subset prefix, the separator, case and everything after the first
    /// `+`. Split on the punctuation a name uses - `-`, `+`, `,`, space and underscore - so `Arial-BoldItalicMT` yields
    /// "Arial", "BoldItalicMT" and matches both `bold` and `italic` within that word.
    /// </summary>
    private static bool NamesAny(string? sourceFont, string[] words)
    {
        if (string.IsNullOrWhiteSpace(sourceFont))
        {
            return false;
        }

        // A subset prefix is six capitals and a plus; the separator is the same either way.
        string name = sourceFont;
        int plus = name.IndexOf('+');
        if (plus >= 0)
        {
            name = name[(plus + 1)..];
        }

        string[] parts = name.Split(['-', '+', ',', ' ', '_'], StringSplitOptions.RemoveEmptyEntries);

        foreach (string part in parts)
        {
            foreach (string word in words)
            {
                if (part.Contains(word, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// **Makes a block's flags agree with the names its runs carry** (issue #257).
    ///
    /// Called on the paths that create a run by splitting another - typing into the middle of a block - because that is where a
    /// run can be born with the file's name but not the file's weight. A run that already agrees is left untouched, so this is
    /// safe to call wherever text is edited.
    /// </summary>
    public static void NormaliseSourceWeights(TextItem text)
    {
        foreach (TextRun run in text.Runs)
        {
            if (NamesBold(run.SourceFont))
            {
                run.Bold = true;
            }

            if (NamesItalic(run.SourceFont))
            {
                run.Italic = true;
            }
        }
    }
}
