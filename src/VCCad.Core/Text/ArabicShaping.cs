using System.Text;

namespace VCCad.Core.Text;

/// <summary>
/// Arabic's contextual forms, as the **presentation-form code points** Unicode defines for them (issue #197).
///
/// **Why code points rather than glyph ids.** The exporter picks a glyph per character, and the shaper's glyph ids
/// are only meaningful against the exact programme it shaped with - which the export chooses for itself, from the
/// text. The presentation forms are a way round that: they are ordinary characters in the font's own cmap, so a run
/// written in them flows through the usage map, the covering-face lookup, the widths and the `ToUnicode` CMap
/// unchanged, and the page draws the joined shapes. Nothing new is bundled and no face has to be agreed on.
///
/// **What it is not.** A general shaper: it does not do mark positioning, kerning, or the ligatures beyond
/// lam-alef, and it knows nothing of the scripts that reorder (Indic). It handles the joining the issue names for
/// Arabic, and reverses Hebrew, whose letters do not join but whose order must run right to left. Anything else it
/// leaves alone, and the export declares the difference.
/// </summary>
public static class ArabicShaping
{
    /// <summary>True when the text is in a script this can place: Arabic (joining) or Hebrew (reordering).</summary>
    public static bool Applies(string text) => text.Any(IsArabicLetter) || text.Any(IsHebrewLetter);

    /// <summary>
    /// The text as the shapes its context asks for, in **visual** order - which is what a pen that advances
    /// left-to-right has to write for a script that runs right to left.
    /// </summary>
    public static string Shape(string text)
    {
        if (!Applies(text))
        {
            return text;
        }

        var shaped = new StringBuilder();
        for (int i = 0; i < text.Length; i++)
        {
            char current = text[i];

            if (IsHebrewLetter(current))
            {
                // Hebrew's letters keep their shapes and reverse. Collecting the whole run and reversing at the end
                // would reorder marks with them; Hebrew here is consonants, which is what the model carries.
                shaped.Append(current);
                continue;
            }

            if (!IsArabicLetter(current))
            {
                shaped.Append(current);
                continue;
            }

            char? next = i + 1 < text.Length && IsArabicLetter(text[i + 1]) ? text[i + 1] : null;
            char? previous = i > 0 && IsArabicLetter(text[i - 1]) ? text[i - 1] : null;

            // Lam followed by alef is the one mandatory ligature, and the pair is a single glyph in every Arabic
            // face - so it has to be formed before the two letters take their own medial and final shapes.
            if (current == '\u0644' && next is { } after && IsAlef(after))
            {
                shaped.Append(LamAlef(after));
                i++;
                continue;
            }

            bool joinsPrevious = previous is { } before && JoinsForward(before) && JoinsBackward(current);
            bool joinsNext = next is { } following && JoinsForward(current) && JoinsBackward(following);

            shaped.Append(Form(current, joinsPrevious, joinsNext));
        }

        string result = shaped.ToString();
        return ContainsArabic(text) ? Reverse(result) : result;
    }

    /// <summary>
    /// One letter in the shape its context asks for: isolated when it joins neither way, initial, medial or final
    /// otherwise. A letter that does not join forward can only ever be isolated or final.
    /// </summary>
    /// <summary>One letter's form for a context, for a caller checking the table against a face.</summary>
    public static char FormFor(char letter, bool joinsPrevious, bool joinsNext) => Form(letter, joinsPrevious, joinsNext);

    private static char Form(char letter, bool joinsPrevious, bool joinsNext)
        => (joinsPrevious, joinsNext) switch
        {
            (false, false) => Forms(letter)[0],
            (false, true) => Forms(letter)[1],
            (true, true) => Forms(letter)[2],
            (true, false) => Forms(letter)[3],
        };

    /// <summary>
    /// The four presentation forms of a letter, in the order isolated, initial, medial, final, or the letter itself
    /// four times when Unicode gives it no separate form (it joins on neither side).
    /// </summary>
    private static char[] Forms(char letter) => _forms.TryGetValue(letter, out char[]? forms)
        ? forms
        : new[] { letter, letter, letter, letter };

    /// <summary>The letters this shaper has forms for. U+063B to U+063F are not among them: left isolated.</summary>
    public static IReadOnlyCollection<char> Letters() => _forms.Keys;

    private static bool IsArabicLetter(char c) => c >= '\u0621' && c <= '\u064A';

    private static bool IsHebrewLetter(char c) => c >= '\u05D0' && c <= '\u05EA';

    private static bool ContainsArabic(string text) => text.Any(IsArabicLetter);

    private static bool IsAlef(char c) => c is '\u0622' or '\u0623' or '\u0625' or '\u0627';

    /// <summary>The lam-alef ligature for each alef it can follow: the pair is one code point.</summary>
    private static char LamAlef(char alef) => alef switch
    {
        '\u0622' => '\uFEF5',
        '\u0623' => '\uFEF7',
        '\u0625' => '\uFEF9',
        _ => '\uFEFB',
    };

    /// <summary>Whether a letter takes a form that joins to the letter **after** it.</summary>
    /// <summary>Whether a letter takes a form that joins to the letter after it.</summary>
    public static bool JoinsForward(char c) => c switch
    {
        // The letters that connect only on their right: alef, dal, thal, reh, zain, waw, and their variants. Every
        // other Arabic letter connects both ways.
        '\u0622' or '\u0623' or '\u0625' or '\u0627' or '\u062F' or '\u0630' or '\u0631' or '\u0632'
            or '\u0648' or '\u0621' => false,
        _ => true,
    };

    /// <summary>Whether a letter takes a form that joins to the letter **before** it.</summary>
    private static bool JoinsBackward(char c) => c != '\u0621';

    /// <summary>Reverses a run, leaving its combining marks after the letter they belong to.</summary>
    private static string Reverse(string text)
    {
        var reversed = new StringBuilder(text.Length);
        for (int i = text.Length - 1; i >= 0; i--)
        {
            reversed.Append(text[i]);
        }

        return reversed.ToString();
    }

    /// <summary>
    /// The four forms per letter, from Unicode's Arabic Presentation Forms-B: isolated, final, initial, medial as the
    /// block lists them, reordered here to isolated, initial, medial, final so the context switch reads plainly.
    /// </summary>
    private static readonly Dictionary<char, char[]> _forms = new()
    {
        ['\u0621'] = new[] { '\uFE80', '\uFE80', '\uFE80', '\uFE80' },  // hamza, joins neither way
        ['\u0622'] = new[] { '\uFE81', '\uFE81', '\uFE81', '\uFE82' },
        ['\u0623'] = new[] { '\uFE83', '\uFE83', '\uFE83', '\uFE84' },
        ['\u0624'] = new[] { '\uFE85', '\uFE85', '\uFE85', '\uFE86' },
        ['\u0625'] = new[] { '\uFE87', '\uFE87', '\uFE87', '\uFE88' },
        ['\u0626'] = new[] { '\uFE89', '\uFE8B', '\uFE8C', '\uFE8A' },
        ['\u0627'] = new[] { '\uFE8D', '\uFE8D', '\uFE8D', '\uFE8E' },
        ['\u0628'] = new[] { '\uFE8F', '\uFE91', '\uFE92', '\uFE90' },
        ['\u0629'] = new[] { '\uFE93', '\uFE93', '\uFE93', '\uFE94' },
        ['\u062A'] = new[] { '\uFE95', '\uFE97', '\uFE98', '\uFE96' },
        ['\u062B'] = new[] { '\uFE99', '\uFE9B', '\uFE9C', '\uFE9A' },
        ['\u062C'] = new[] { '\uFE9D', '\uFE9F', '\uFEA0', '\uFE9E' },
        ['\u062D'] = new[] { '\uFEA1', '\uFEA3', '\uFEA4', '\uFEA2' },
        ['\u062E'] = new[] { '\uFEA5', '\uFEA7', '\uFEA8', '\uFEA6' },
        ['\u062F'] = new[] { '\uFEA9', '\uFEA9', '\uFEA9', '\uFEAA' },
        ['\u0630'] = new[] { '\uFEAB', '\uFEAB', '\uFEAB', '\uFEAC' },
        ['\u0631'] = new[] { '\uFEAD', '\uFEAD', '\uFEAD', '\uFEAE' },
        ['\u0632'] = new[] { '\uFEAF', '\uFEAF', '\uFEAF', '\uFEB0' },
        ['\u0633'] = new[] { '\uFEB1', '\uFEB3', '\uFEB4', '\uFEB2' },
        ['\u0634'] = new[] { '\uFEB5', '\uFEB7', '\uFEB8', '\uFEB6' },
        ['\u0635'] = new[] { '\uFEB9', '\uFEBB', '\uFEBC', '\uFEBA' },
        ['\u0636'] = new[] { '\uFEBD', '\uFEBF', '\uFEC0', '\uFEBE' },
        ['\u0637'] = new[] { '\uFEC1', '\uFEC3', '\uFEC4', '\uFEC2' },
        ['\u0638'] = new[] { '\uFEC5', '\uFEC7', '\uFEC8', '\uFEC6' },
        ['\u0639'] = new[] { '\uFEC9', '\uFECB', '\uFECC', '\uFECA' },
        ['\u063A'] = new[] { '\uFECD', '\uFECF', '\uFED0', '\uFECE' },
        ['\u0640'] = new[] { '\u0640', '\u0640', '\u0640', '\u0640' },  // tatweel joins both ways, one shape
        ['\u0641'] = new[] { '\uFED1', '\uFED3', '\uFED4', '\uFED2' },
        ['\u0642'] = new[] { '\uFED5', '\uFED7', '\uFED8', '\uFED6' },
        ['\u0643'] = new[] { '\uFED9', '\uFEDB', '\uFEDC', '\uFEDA' },
        ['\u0644'] = new[] { '\uFEDD', '\uFEDF', '\uFEE0', '\uFEDE' },
        ['\u0645'] = new[] { '\uFEE1', '\uFEE3', '\uFEE4', '\uFEE2' },
        ['\u0646'] = new[] { '\uFEE5', '\uFEE7', '\uFEE8', '\uFEE6' },
        ['\u0647'] = new[] { '\uFEE9', '\uFEEB', '\uFEEC', '\uFEEA' },
        ['\u0648'] = new[] { '\uFEED', '\uFEED', '\uFEED', '\uFEEE' },
        ['\u0649'] = new[] { '\uFEEF', '\uFBE8', '\uFBE9', '\uFEF0' },
        ['\u064A'] = new[] { '\uFEF1', '\uFEF3', '\uFEF4', '\uFEF2' },
    };
}