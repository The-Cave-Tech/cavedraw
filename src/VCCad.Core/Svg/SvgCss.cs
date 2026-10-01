using System.Xml.Linq;

namespace VCCad.Core.Svg;

/// <summary>One declaration that won, and what it beat.</summary>
internal readonly record struct CssValue(string Value, bool Important, int Ids, int Classes, int Types, int Order)
{
    /// <summary>
    /// Whether this beats another declaration of the same property.
    ///
    /// The order is CSS's: **importance first**, then specificity, then document order. Importance outranks
    /// specificity because that is what `!important` is for - a rule that says it means it beats one that does not,
    /// however specific that one is. Getting these two the wrong way round inverts three of Inkscape's own test
    /// files, which exist precisely to catch it.
    /// </summary>
    public bool Beats(CssValue other)
    {
        if (Important != other.Important)
        {
            return Important;
        }

        if (Ids != other.Ids)
        {
            return Ids > other.Ids;
        }

        if (Classes != other.Classes)
        {
            return Classes > other.Classes;
        }

        if (Types != other.Types)
        {
            return Types > other.Types;
        }

        return Order >= other.Order;
    }
}

/// <summary>
/// The CSS cascade over an SVG document.
///
/// **This is a cascade, not a parser detail.** The same property can arrive from a presentation attribute, from a
/// rule in a `style` element, from an imported sheet, and from an inline `style` - and which one wins is decided by
/// importance, specificity and order. Reading each in isolation, or letting the last one read win, gets
/// multi-stylesheet files subtly wrong in ways that look like a rendering bug.
///
/// The priority a presentation attribute has is **the lowest of all**, below every rule. It is easy to assume the
/// attribute written on the element is the most specific thing there is, and it is the opposite: it is a fallback,
/// and inverting that inverts the colours of the files that test it.
/// </summary>
internal sealed class SvgStylesheet
{
    private readonly List<Rule> _rules = new();

    private sealed record Rule(Selector Selector, string Property, string Value, bool Important, int Order);

    /// <summary>The rules parsed so far, in document order, which is the order that breaks a specificity tie.</summary>
    private int _order;

    /// <summary>
    /// Parses a stylesheet, following `@import` into the files it names.
    ///
    /// An import is relative to the sheet that names it, which is why the base directory travels with the call -
    /// a file that imports a sibling sheet is the normal case, and one that could only import absolute paths would
    /// be useless.
    /// </summary>
    public static SvgStylesheet Parse(string css, string? baseDirectory = null)
    {
        var sheet = new SvgStylesheet();
        sheet.Add(css, baseDirectory, importDepth: 0);
        return sheet;
    }

    private void Add(string css, string? baseDirectory, int importDepth)
    {
        if (importDepth > 8)
        {
            // A sheet that imports itself, directly or through a chain, stops here rather than recursing.
            return;
        }

        foreach ((string selectorText, List<(string Property, string Value, bool Important)> declarations)
                 in SplitRules(css))
        {
            foreach (Selector selector in Selector.ParseList(selectorText))
            {
                foreach ((string property, string value, bool important) in declarations)
                {
                    _rules.Add(new Rule(selector, property, value, important, _order++));
                }
            }
        }

        foreach (string reference in Imports(css))
        {
            string path = Resolve(reference, baseDirectory);
            if (path.Length > 0 && File.Exists(path))
            {
                try
                {
                    Add(File.ReadAllText(path), Path.GetDirectoryName(path), importDepth + 1);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // An unreadable import is a sheet the document does not get, not a file it cannot open.
                }
            }
        }
    }

    private static string Resolve(string reference, string? baseDirectory)
    {
        if (baseDirectory is null)
        {
            return string.Empty;
        }

        string trimmed = reference.Trim().Trim('"', '\'');

        // A URL import may carry a media query or a fragment; neither names a file.
        int space = trimmed.IndexOfAny(new[] { ' ', '\t' });
        if (space > 0)
        {
            trimmed = trimmed[..space];
        }

        int hash = trimmed.IndexOf('#');
        if (hash > 0)
        {
            trimmed = trimmed[..hash];
        }

        return trimmed.Length == 0 ? string.Empty : Path.Combine(baseDirectory, trimmed);
    }

    /// <summary>
    /// The declarations that apply to an element, with the cascade already resolved so each property appears once.
    ///
    /// <paramref name="ancestors"/> is the chain from the element's parent up to the root, which is what a
    /// descendant selector matches against - `#groupA use` means "a use with a groupA ancestor", not a child.
    /// </summary>
    public Dictionary<string, (string Value, bool Important)> DeclarationsFor(
        XElement element, IReadOnlyList<XElement> ancestors)
    {
        var winners = new Dictionary<string, CssValue>(StringComparer.OrdinalIgnoreCase);

        foreach (Rule rule in _rules)
        {
            if (!rule.Selector.Matches(element, ancestors))
            {
                continue;
            }

            var value = new CssValue(
                rule.Value, rule.Important, rule.Selector.Ids, rule.Selector.Classes, rule.Selector.Types, rule.Order);

            if (!winners.TryGetValue(rule.Property, out CssValue current) || value.Beats(current))
            {
                winners[rule.Property] = value;
            }
        }

        var declarations = new Dictionary<string, (string, bool)>(StringComparer.OrdinalIgnoreCase);
        foreach ((string property, CssValue value) in winners)
        {
            declarations[property] = (value.Value, value.Important);
        }

        return declarations;
    }

    /// <summary>
    /// Splits a stylesheet into rules.
    ///
    /// Comments are stripped first, and **empty declarations are skipped** rather than stored: a stylesheet may
    /// legitimately write `{ ; fill: green; }` and an inline style `;fill:blue`, and a parser that treated the
    /// leading semicolon as a property with no value would either throw or shadow the real declaration. One of
    /// Inkscape's test files exists for exactly that.
    /// </summary>
    private static IEnumerable<(string Selector, List<(string, string, bool)>)> SplitRules(string css)
    {
        string text = StripComments(css);
        int i = 0;

        while (i < text.Length)
        {
            int open = text.IndexOf('{', i);
            if (open < 0)
            {
                yield break;
            }

            int close = text.IndexOf('}', open);
            if (close < 0)
            {
                yield break;
            }

            // An at-rule with a block - a media query, a font face - is not a selector, and its contents are not
            // rules this reader can apply. They are skipped rather than read as a selector named "@media".
            string selectorText = text[i..open].Trim();
            i = close + 1;

            if (selectorText.Length == 0 || selectorText.StartsWith('@'))
            {
                continue;
            }

            var declarations = new List<(string, string, bool)>();
            foreach (string piece in text[(open + 1)..close].Split(';'))
            {
                (string property, string value, bool important) = SplitDeclaration(piece);
                if (property.Length > 0)
                {
                    declarations.Add((property, value, important));
                }
            }

            if (declarations.Count > 0)
            {
                yield return (selectorText, declarations);
            }
        }
    }

    /// <summary>One `property: value`, with the `!important` flag taken off the value.</summary>
    internal static (string Property, string Value, bool Important) SplitDeclaration(string piece)
    {
        int colon = piece.IndexOf(':');
        if (colon <= 0)
        {
            return (string.Empty, string.Empty, false);
        }

        string property = piece[..colon].Trim();
        string value = piece[(colon + 1)..].Trim();
        bool important = false;

        int bang = value.LastIndexOf('!');
        if (bang >= 0 && value[(bang + 1)..].Trim().Equals("important", StringComparison.OrdinalIgnoreCase))
        {
            important = true;
            value = value[..bang].Trim();
        }

        return (property, value, important);
    }

    private static string StripComments(string css)
    {
        var builder = new System.Text.StringBuilder(css.Length);
        int i = 0;

        while (i < css.Length)
        {
            if (i + 1 < css.Length && css[i] == '/' && css[i + 1] == '*')
            {
                int end = css.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? css.Length : end + 2;
                continue;
            }

            builder.Append(css[i]);
            i++;
        }

        return builder.ToString();
    }

    /// <summary>The sheets an `@import` names, in the order they appear.</summary>
    private static IEnumerable<string> Imports(string css)
    {
        string text = StripComments(css);
        int i = 0;

        while (true)
        {
            int at = text.IndexOf("@import", i, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
            {
                yield break;
            }

            int open = text.IndexOf('(', at);
            string reference;
            if (open >= 0 && (text.IndexOf(';', at) < 0 || open < text.IndexOf(';', at)))
            {
                int close = text.IndexOf(')', open);
                if (close < 0)
                {
                    yield break;
                }

                reference = text[(open + 1)..close];
                i = close + 1;
            }
            else
            {
                int quote = text.IndexOfAny(new[] { '"', '\'' }, at);
                if (quote < 0)
                {
                    yield break;
                }

                int end = text.IndexOf(text[quote], quote + 1);
                if (end < 0)
                {
                    yield break;
                }

                reference = text[(quote + 1)..end];
                i = end + 1;
            }

            yield return reference;
        }
    }
}

/// <summary>
/// A selector: compound selectors separated by whitespace, each matching by type, class and id.
///
/// Descendant rather than child, because that is what a space means - and the difference is not academic: `#groupA
/// use` matches a `use` at any depth inside groupA, which is how Inkscape's own test files are written.
/// </summary>
internal sealed class Selector
{
    private readonly Compound[] _compounds;

    private Selector(Compound[] compounds) => _compounds = compounds;

    public int Ids { get; private init; }

    public int Classes { get; private init; }

    public int Types { get; private init; }

    private sealed record Compound(string? Type, List<string> Ids, List<string> Classes);

    /// <summary>Every selector in a comma-separated list, which is what a grouped selector is.</summary>
    public static IEnumerable<Selector> ParseList(string text)
    {
        foreach (string piece in text.Split(','))
        {
            string trimmed = piece.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            var compounds = new List<Compound>();
            foreach (string part in trimmed.Split(new[] { ' ', '\t', '\n', '\r' },
                         StringSplitOptions.RemoveEmptyEntries))
            {
                // A combinator character is not part of a compound selector: `>`, `+` and `~` are read as
                // descendant, which over-matches rather than under-matches. A child-only selector that matched
                // too widely is a styling difference; one that matched nothing is a blank drawing.
                string cleaned = part.Trim('>', '+', '~');
                if (cleaned.Length == 0 || cleaned == "*")
                {
                    compounds.Add(new Compound(null, new List<string>(), new List<string>()));
                    continue;
                }

                string? type = null;
                var ids = new List<string>();
                var classes = new List<string>();
                int i = 0;

                while (i < cleaned.Length)
                {
                    char marker = cleaned[i];
                    if (marker is '#' or '.')
                    {
                        int start = ++i;
                        while (i < cleaned.Length && cleaned[i] is not ('#' or '.'))
                        {
                            i++;
                        }

                        string name = cleaned[start..i];
                        if (marker == '#')
                        {
                            ids.Add(name);
                        }
                        else
                        {
                            classes.Add(name);
                        }
                    }
                    else
                    {
                        int start = i;
                        while (i < cleaned.Length && cleaned[i] is not ('#' or '.'))
                        {
                            i++;
                        }

                        type = cleaned[start..i];
                    }
                }

                compounds.Add(new Compound(type, ids, classes));
            }

            if (compounds.Count == 0)
            {
                continue;
            }

            var selector = new Selector(compounds.ToArray())
            {
                Ids = compounds.Sum(c => c.Ids.Count),
                Classes = compounds.Sum(c => c.Classes.Count),
                Types = compounds.Sum(c => c.Type is null ? 0 : 1),
            };

            yield return selector;
        }
    }

    /// <summary>
    /// Whether this selector matches.
    ///
    /// The compounds are consumed **right to left**, so the last one has to match the element itself and the
    /// earlier ones have to match its ancestors in order - which is what makes a descendant selector a chain
    /// rather than a set.
    /// </summary>
    public bool Matches(XElement element, IReadOnlyList<XElement> ancestors)
    {
        if (!MatchesCompound(_compounds[^1], element))
        {
            return false;
        }

        int index = ancestors.Count - 1;
        for (int c = _compounds.Length - 2; c >= 0; c--)
        {
            bool found = false;
            while (index >= 0)
            {
                if (MatchesCompound(_compounds[c], ancestors[index]))
                {
                    found = true;
                    index--;
                    break;
                }

                index--;
            }

            if (!found)
            {
                return false;
            }
        }

        return true;
    }

    private static bool MatchesCompound(Compound compound, XElement element)
    {
        if (compound.Type is { Length: > 0 } type &&
            !element.Name.LocalName.Equals(type, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (compound.Ids.Count > 0)
        {
            string? id = element.Attribute("id")?.Value;
            if (id is null || !compound.Ids.Contains(id, StringComparer.Ordinal))
            {
                return false;
            }
        }

        if (compound.Classes.Count > 0)
        {
            string? value = element.Attribute("class")?.Value;
            if (value is null)
            {
                return false;
            }

            string[] classes = value.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            if (!compound.Classes.All(c => classes.Contains(c, StringComparer.Ordinal)))
            {
                return false;
            }
        }

        return true;
    }
}
