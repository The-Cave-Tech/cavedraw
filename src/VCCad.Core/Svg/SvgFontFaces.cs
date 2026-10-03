namespace VCCad.Core.Svg;

/// <summary>
/// The faces a document supplies for itself, loaded from the `@font-face` rules in its own stylesheets.
///
/// A file that carries a webfont is not asking this machine for a face - it is telling the renderer where the face
/// is, and for SVG-in-OpenType the glyph definitions are inside the programme. The registry at
/// <see cref="SvgStylesheet"/> deliberately does not read at-rules as rules, which is right for `@media` and wrong
/// for this one: the rule is not a style, it is a resource, and it is read here instead.
///
/// Only a programme that actually carries `SVG ` glyph documents is kept. A face the file names but the reader
/// cannot load is *reported* by the caller rather than substituted silently, which is the whole difference this
/// class exists to make: a font that can be loaded is drawn with, and one that cannot is named.
/// </summary>
public sealed class SvgFontFaces
{
    private readonly Dictionary<string, SvgFontProgramme> _faces = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True once at least one face was loaded.</summary>
    public bool Any => _faces.Count > 0;

    /// <summary>The families this registry holds, for a caller that wants to say what it did.</summary>
    public IEnumerable<string> Families => _faces.Keys;

    /// <summary>
    /// Reads every `@font-face` rule in <paramref name="css"/> whose `src` names a file that exists beside the
    /// document and carries glyph definitions.
    /// </summary>
    public static SvgFontFaces Load(string css, string? baseDirectory)
    {
        var faces = new SvgFontFaces();
        if (string.IsNullOrWhiteSpace(css) || string.IsNullOrWhiteSpace(baseDirectory))
        {
            return faces;
        }

        foreach (string rule in AtRules(css, "@font-face"))
        {
            string? family = Declared(rule, "font-family");
            string? source = Source(rule);
            if (family is null || source is null)
            {
                continue;
            }

            string path = Resolve(baseDirectory!, source);
            byte[] bytes;
            try
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                bytes = File.ReadAllBytes(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (SvgFontProgramme.Parse(bytes) is { } programme)
            {
                faces._faces[family] = programme;
            }
        }

        return faces;
    }

    /// <summary>The programme a run's family names, or null when the document supplies no such face.</summary>
    public SvgFontProgramme? Find(string family)
        => family.Length > 0 && _faces.TryGetValue(family, out SvgFontProgramme? programme) ? programme : null;

    /// <summary>Each `@font-face` rule's body, with nested braces balanced.</summary>
    private static IEnumerable<string> AtRules(string css, string keyword)
    {
        int at = 0;
        while (true)
        {
            int start = css.IndexOf(keyword, at, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                yield break;
            }

            int open = css.IndexOf('{', start);
            if (open < 0)
            {
                yield break;
            }

            int depth = 0;
            int close = open;
            for (; close < css.Length; close++)
            {
                if (css[close] == '{')
                {
                    depth++;
                }
                else if (css[close] == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        break;
                    }
                }
            }

            if (close >= css.Length)
            {
                yield break;
            }

            yield return css[(open + 1)..close];
            at = close + 1;
        }
    }

    /// <summary>One declaration's value, unquoted, case-insensitively.</summary>
    private static string? Declared(string rule, string property)
    {
        foreach (string declaration in rule.Split(';'))
        {
            int colon = declaration.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            if (declaration[..colon].Trim().Equals(property, StringComparison.OrdinalIgnoreCase))
            {
                return declaration[(colon + 1)..].Trim().Trim('"', '\'');
            }
        }

        return null;
    }

    /// <summary>
    /// The first `url(...)` a `src` names. A `src` may list several candidates, and taking the first is what a
    /// renderer does with them too - the difference being that this reader stops there rather than trying the next
    /// when the first has no glyph definitions.
    /// </summary>
    private static string? Source(string rule)
    {
        string? src = Declared(rule, "src");
        if (src is null)
        {
            return null;
        }

        int at = src.IndexOf("url(", StringComparison.OrdinalIgnoreCase);
        if (at < 0)
        {
            return null;
        }

        int open = src.IndexOf('(', at);
        int close = src.IndexOf(')', open + 1);
        if (close < 0)
        {
            return null;
        }

        return src[(open + 1)..close].Trim().Trim('"', '\'');
    }

    /// <summary>A `src` URL as a path on this machine, with a `file:` URL treated as a path.</summary>
    private static string Resolve(string baseDirectory, string source)
    {
        if (source.StartsWith("file:", StringComparison.OrdinalIgnoreCase) &&
            Uri.TryCreate(source, UriKind.Absolute, out Uri? uri))
        {
            return uri.LocalPath;
        }

        return Path.IsPathRooted(source) ? source : Path.GetFullPath(Path.Combine(baseDirectory, source));
    }
}
