using System.Globalization;
using System.Xml.Linq;
using VCCad.Core.Model;

namespace VCCad.Core.Svg;

/// <summary>
/// The font and line properties a text element carries, inherited down the tree the way SVG says.
///
/// **Only what the model can hold is resolved to a value here.** A property the model has no field for - `font-stretch`,
/// `font-variant`, `letter-spacing`, `word-spacing` - is **reported** with the value the file wrote, because the rule
/// this repository enforces is that a value the reader cannot keep is said out loud, never quietly dropped. Reporting
/// it is the difference between a gap somebody can act on and a heading that quietly lost its tracking.
///
/// What maps where:
/// <list type="bullet">
/// <item>`font-family` (and `-inkscape-font-specification` over it) to the face a run names.</item>
/// <item>`font-size` to the run's size, with `em`, `ex` and `%` resolved against the size in force - the context a
/// bare length elsewhere in the file does not have.</item>
/// <item>`font-weight` and `font-style` to the model's bold and italic flags.</item>
/// <item>`text-anchor` to the block's alignment.</item>
/// <item>`line-height` to the block's line spacing.</item>
/// <item>`white-space` and `xml:space` to whether white space is collapsed or kept.</item>
/// </list>
/// </summary>
internal sealed record SvgTextStyle(
    string FontFamily,
    double FontSize,
    bool Bold,
    bool Italic,
    TextAlignment Anchor,
    bool PreserveSpace,
    double LineSpacing)
{
    /// <summary>
    /// SVG's initial values: a medium (16px) upright face, anchored at the start, collapsing white space.
    ///
    /// 16 is CSS's initial font size and so SVG's `medium`, and the family is the model's own default because the
    /// initial `font-family` is the user agent's choice - the file does not name one, so there is nothing of the
    /// file's to lose.
    /// </summary>
    public static SvgTextStyle Default { get; } = new(
        TextItem.DefaultFontFamily, 16.0, false, false, TextAlignment.Left, PreserveSpace: false, LineSpacing: 1.2);

    /// <summary>Resolves the element's own text properties over the ones it inherits.</summary>
    public static SvgTextStyle From(
        XElement element,
        SvgTextStyle inherited,
        IReadOnlyDictionary<string, (string Value, bool Important)>? sheet,
        Action<string>? warn)
    {
        Dictionary<string, string> inline = PresentationStyle.ReadStyleAttribute(element);
        Dictionary<string, bool> inlineImportant = PresentationStyle.ReadStyleImportance(element);

        string? Value(string name) => SvgProperties.Value(element, sheet, inline, inlineImportant, name);

        string? familyValue = Value("font-family");
        string? specification = Value("-inkscape-font-specification");

        (string? ListedFamily, bool IsList, bool Generic) = ParseFamilyList(familyValue);

        // `-inkscape-font-specification` is the face Inkscape actually chose, written as "family, Style". It is
        // honoured **over** the family list, which is why it is read second: a file whose `font-family` is a generic
        // word and whose specification names the real face draws in the real face, which is what the file's author
        // saw.
        (string? SpecificationFamily, string? StyleWords) = SplitSpecification(specification);

        string family = inherited.FontFamily;
        if (ListedFamily is { Length: > 0 })
        {
            family = ListedFamily;
        }

        if (SpecificationFamily is { Length: > 0 })
        {
            family = SpecificationFamily;
        }

        if (IsGenericFamily(family))
        {
            warn?.Invoke(
                $"font-family=\"{family}\" is a generic family rather than a face name, and the model holds " +
                "the face a run is drawn with");
        }
        else if (IsList && SpecificationFamily is null)
        {
            // The alternates are the file's fallback chain, and the model has one family per run. Said out loud
            // because it is the difference between drawing the second choice and drawing a face nobody listed.
            warn?.Invoke(
                $"font-family=\"{familyValue}\" is a list, and a run holds one family: \"{family}\" is used");
        }

        double size = inherited.FontSize;
        if (Value("font-size") is { } sizeValue && !IsCssWideKeyword(sizeValue))
        {
            if (ReadFontSize(sizeValue, inherited.FontSize, warn) is { } resolved && resolved > 0)
            {
                size = resolved;
            }
        }

        // The weight the element ends up at, which `bolder` and `lighter` need and which the model can only
        // remember as regular or bold - hence the round trip through a number rather than straight to the flag.
        double weight = inherited.Bold ? 700 : 400;
        if (Value("font-weight") is { } weightValue && !IsCssWideKeyword(weightValue))
        {
            if (ReadWeight(weightValue, weight, warn) is { } resolved)
            {
                weight = resolved;
            }
        }

        bool italic = inherited.Italic;
        if (Value("font-style") is { } styleValue && !IsCssWideKeyword(styleValue))
        {
            italic = ReadSlant(styleValue, warn) ?? italic;
        }

        if (StyleWords is { Length: > 0 })
        {
            (double specWeight, bool specItalic) = ReadFaceWords(StyleWords, weight, warn);
            weight = specWeight;
            italic |= specItalic;
        }

        TextAlignment anchor = inherited.Anchor;
        if (Value("text-anchor") is { } anchorValue && !IsCssWideKeyword(anchorValue))
        {
            anchor = ReadAnchor(anchorValue, anchor, warn);
        }

        double lineSpacing = inherited.LineSpacing;
        if (Value("line-height") is { } lineValue && !IsCssWideKeyword(lineValue))
        {
            lineSpacing = ReadLineHeight(lineValue, size, lineSpacing, warn);
        }

        bool preserve = inherited.PreserveSpace;
        string? whiteSpace = Value("white-space");
        if (whiteSpace is not null && !IsCssWideKeyword(whiteSpace))
        {
            preserve = ReadWhiteSpace(whiteSpace.Trim(), preserve, warn);
        }
        else if (element.Attribute(XNamespace.Xml + "space")?.Value is { Length: > 0 } xmlSpace)
        {
            // SVG 1.1's spelling of the same thing, and still what Inkscape writes on the root element.
            string value = xmlSpace.Trim();
            if (value.Equals("preserve", StringComparison.OrdinalIgnoreCase))
            {
                preserve = true;
            }
            else if (value.Equals("default", StringComparison.OrdinalIgnoreCase))
            {
                preserve = false;
            }
            else
            {
                warn?.Invoke($"xml:space=\"{xmlSpace}\" is neither \"default\" nor \"preserve\"");
            }
        }

        ReportUnkeptProperties(Value, warn);

        return new SvgTextStyle(family, size, weight >= 600, italic, anchor, preserve, lineSpacing);
    }

    /// <summary>
    /// The properties that carry real layout and that the model has no field for.
    ///
    /// Each is reported **only when it says something** - `font-stretch:normal` and `letter-spacing:0` are the
    /// initial values and change nothing, and warning about them would bury the one file that really is condensed.
    /// </summary>
    private static void ReportUnkeptProperties(Func<string, string?> value, Action<string>? warn)
    {
        if (warn is null)
        {
            return;
        }

        void Report(string property, string message)
        {
            if (value(property) is { Length: > 0 } written && !IsInitial(property, written))
            {
                warn(message.Replace("{value}", written, StringComparison.Ordinal));
            }
        }

        Report("font-stretch", "font-stretch=\"{value}\" needs a width axis, and a run holds a face and not a width");
        Report("font-variant", "font-variant=\"{value}\" is not kept: a run holds no variant");
        Report("font-variant-caps", "font-variant-caps=\"{value}\" is not kept: a run holds no variant");
        Report("letter-spacing", "letter-spacing=\"{value}\" is not kept: a run holds no letter spacing");
        Report("word-spacing", "word-spacing=\"{value}\" is not kept: a run holds no word spacing");
        Report("text-decoration", "text-decoration=\"{value}\" is not kept: a run holds no decoration");
        Report("baseline-shift", "baseline-shift=\"{value}\" is a baseline the model does not hold");
        Report("dominant-baseline", "dominant-baseline=\"{value}\" is a baseline the model does not hold");
        Report("writing-mode", "writing-mode=\"{value}\" is not kept: the model sets text horizontally");
        Report("direction", "direction=\"{value}\" is not kept: the model lays text left to right");
    }

    /// <summary>Whether a written value is the property's own initial value, which says nothing.</summary>
    private static bool IsInitial(string property, string written)
    {
        string value = written.Trim();
        return property switch
        {
            "letter-spacing" or "word-spacing" => value is "normal" or "0" or "0px" or "0em" or "0%",
            _ => value.Equals("normal", StringComparison.OrdinalIgnoreCase) ||
                 value.Equals("none", StringComparison.OrdinalIgnoreCase) ||
                 value.Equals("baseline", StringComparison.OrdinalIgnoreCase) ||
                 value.Equals("auto", StringComparison.OrdinalIgnoreCase) ||
                 value.Equals("horizontal-tb", StringComparison.OrdinalIgnoreCase) ||
                 value.Equals("ltr", StringComparison.OrdinalIgnoreCase) ||
                 value.Equals("0", StringComparison.Ordinal),
        };
    }

    /// <summary>
    /// A `font-family` list: the first family, whether there is more than one, and whether that first one is a CSS
    /// generic keyword.
    /// </summary>
    private static (string? Family, bool IsList, bool IsGeneric) ParseFamilyList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || IsCssWideKeyword(value))
        {
            return (null, false, false);
        }

        string[] families = value
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Trim().Trim('\'', '"').Trim())
            .Where(part => part.Length > 0)
            .ToArray();

        return families.Length == 0
            ? (null, false, false)
            : (families[0], families.Length > 1, IsGenericFamily(families[0]));
    }

    /// <summary>
    /// `-inkscape-font-specification`, as "family, Style".
    ///
    /// The style is after the **last** comma, because a family may contain one - `'Foo, Bar, Bold'` is the family
    /// `Foo, Bar` set bold. Splitting on the first would take half a family for a style word.
    /// </summary>
    private static (string? Family, string? Style) SplitSpecification(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return (null, null);
        }

        string trimmed = value.Trim().Trim('\'', '"').Trim();
        int comma = trimmed.LastIndexOf(',');
        return comma < 0
            ? (trimmed, null)
            : (trimmed[..comma].Trim().Trim('\'', '"').Trim(), trimmed[(comma + 1)..].Trim());
    }

    /// <summary>
    /// The style words of a font specification: "Bold", "Oblique", "Book", "Semi-Condensed".
    ///
    /// This is where a file records that it wanted the semi-bold cut of a family, and a word this reader does not
    /// know is reported rather than ignored - the whole point of reading the specification is that the face it
    /// names is the one the author saw.
    /// </summary>
    private static (double Weight, bool Italic) ReadFaceWords(string words, double inheritedWeight, Action<string>? warn)
    {
        double weight = inheritedWeight;
        bool italic = false;

        foreach (string word in words.Split(new[] { ' ', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string key = word.Replace("-", string.Empty).ToLowerInvariant();
            if (WeightOf(key) is { } named)
            {
                weight = named;
            }
            else if (key is "italic" or "oblique" or "kursiv")
            {
                italic = true;
            }
            else if (StretchOf(key))
            {
                warn?.Invoke(
                    $"-inkscape-font-specification style word \"{word}\" is a width, and a run holds no width axis");
            }
            else
            {
                warn?.Invoke($"-inkscape-font-specification style word \"{word}\" is not a weight, slant or width");
            }
        }

        if (weight is not (400 or 700))
        {
            warn?.Invoke(
                $"-inkscape-font-specification names a weight of {weight}, which is neither 400 nor 700, " +
                "and a run holds a regular or a bold face");
        }

        return (weight, italic);
    }

    /// <summary>A CSS weight name as the number it stands for, or null when the word is not a weight.</summary>
    private static double? WeightOf(string word) => word switch
    {
        "thin" or "hairline" => 100,
        "extralight" or "ultralight" => 200,
        "light" => 300,
        "book" or "regular" or "normal" => 400,
        "medium" => 500,
        "demibold" or "semibold" or "demi" => 600,
        "bold" => 700,
        "extrabold" or "ultrabold" => 800,
        "black" or "heavy" => 900,
        _ => null,
    };

    /// <summary>Whether a style word names a width rather than a weight or a slant.</summary>
    private static bool StretchOf(string word) => word is
        "condensed" or "semicondensed" or "extracondensed" or "ultracondensed" or
        "expanded" or "semiexpanded" or "extraexpanded" or "ultraexpanded" or
        "narrow" or "wide" or "extended";

    /// <summary>
    /// A font size, with the three units that are relative to the size in force resolved against it.
    ///
    /// `em`, `ex` and `%` on `font-size` are relative to the **inherited** size - which this reader has, unlike the
    /// shapes reader, where a bare length has no text context at all. Resolving them through the generic length
    /// table would substitute CSS's initial 16px and report an assumption that is not being made.
    /// </summary>
    private static double? ReadFontSize(string value, double inherited, Action<string>? warn)
    {
        string trimmed = value.Trim();
        int end = trimmed.Length;
        while (end > 0 && (char.IsLetter(trimmed[end - 1]) || trimmed[end - 1] == '%'))
        {
            end--;
        }

        string unit = trimmed[end..].ToLowerInvariant();
        string number = trimmed[..end].Trim();

        if (unit is "em" or "ex" or "%" &&
            double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double relative))
        {
            return unit switch
            {
                "em" => relative * inherited,
                "ex" => relative * inherited / 2.0,
                _ => relative / 100.0 * inherited,
            };
        }

        if (unit == "rem")
        {
            warn?.Invoke($"font-size=\"{trimmed}\" is relative to the root font size, which this reader does not track");
            return null;
        }

        return SvgLength.Parse(trimmed, warn);
    }

    /// <summary>
    /// A `font-weight`, as the number it resolves to.
    ///
    /// The model keeps a run as regular or bold and nothing in between, so a weight that is neither 400 nor 700 is
    /// **reported**: 600 draws with the bold cut of a family that has one, and with a synthetic lean of one that
    /// does not, and neither is what a file naming a semi-bold face asked for.
    /// </summary>
    private static double? ReadWeight(string value, double inherited, Action<string>? warn)
    {
        string key = value.Trim().ToLowerInvariant();
        double? weight = key switch
        {
            "normal" => 400,
            "bold" => 700,

            // CSS's relative weights, against what the run would otherwise be. `bolder` from a normal weight is
            // bold, and from a bold one there is nothing bolder to reach for, so it stays where it is.
            "bolder" => inherited >= 600 ? inherited : 700,
            "lighter" => inherited >= 600 ? 400 : inherited,
            _ => double.TryParse(key, NumberStyles.Float, CultureInfo.InvariantCulture, out double numeric)
                ? Math.Clamp(numeric, 1.0, 1000.0)
                : null,
        };

        if (weight is null)
        {
            warn?.Invoke($"font-weight=\"{value}\" is not a weight");
            return null;
        }

        if (weight is not (400 or 700))
        {
            warn?.Invoke(
                $"font-weight=\"{value}\" is neither 400 nor 700, and a run holds a regular or a bold face");
        }

        return weight;
    }

    /// <summary>
    /// A `font-style`, as the italic flag.
    ///
    /// `oblique` is a slanted face that is not a drawn italic and the model holds one flag, so it is reported -
    /// a file that names both an italic and an oblique face of the same family gets one of them.
    /// </summary>
    private static bool? ReadSlant(string value, Action<string>? warn)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "normal":
                return false;
            case "italic":
                return true;
            case "oblique":
                warn?.Invoke("font-style=\"oblique\" is a slant the model holds as upright or italic");
                return true;
            default:
                warn?.Invoke($"font-style=\"{value}\" is not a slant");
                return null;
        }
    }

    private static TextAlignment ReadAnchor(string value, TextAlignment inherited, Action<string>? warn)
        => value.Trim().ToLowerInvariant() switch
        {
            "start" => TextAlignment.Left,
            "middle" => TextAlignment.Center,
            "end" => TextAlignment.Right,
            _ => WarnAnchor(value, inherited, warn),
        };

    private static TextAlignment WarnAnchor(string value, TextAlignment inherited, Action<string>? warn)
    {
        warn?.Invoke($"text-anchor=\"{value}\" is not \"start\", \"middle\" or \"end\"");
        return inherited;
    }

    /// <summary>
    /// A `line-height`, as the multiple of the font size the model stores.
    ///
    /// A bare number and a percentage are already multiples; an absolute length is divided by the size in force,
    /// which is the same thing. `normal` is the font's own leading and the model's default stands in for it.
    /// </summary>
    private static double ReadLineHeight(string value, double size, double inherited, Action<string>? warn)
    {
        string trimmed = value.Trim();
        if (trimmed.Equals("normal", StringComparison.OrdinalIgnoreCase))
        {
            return inherited;
        }

        string withoutUnit = trimmed.EndsWith('%') ? trimmed[..^1].Trim() : trimmed;
        if (double.TryParse(withoutUnit, NumberStyles.Float, CultureInfo.InvariantCulture, out double bare))
        {
            double multiple = trimmed.EndsWith('%') ? bare / 100.0 : bare;
            return multiple > 0 ? multiple : WarnLineHeight(value, inherited, warn);
        }

        if (SvgLength.Parse(trimmed, warn) is { } length && size > 0 && length > 0)
        {
            return length / size;
        }

        return WarnLineHeight(value, inherited, warn);
    }

    private static double WarnLineHeight(string value, double inherited, Action<string>? warn)
    {
        warn?.Invoke($"line-height=\"{value}\" is not a line height this reader can resolve");
        return inherited;
    }

    /// <summary>Whether white space is kept, per CSS's `white-space` keywords.</summary>
    private static bool ReadWhiteSpace(string value, bool inherited, Action<string>? warn) => value switch
    {
        "normal" or "nowrap" => false,
        "pre" or "pre-wrap" or "break-spaces" => true,

        // `pre-line` collapses runs of spaces but keeps the line breaks, and the model holds one flag: keeping the
        // breaks is the half that is visible on the page, and the collapsing is reported rather than lost.
        "pre-line" => WarnPreLine(warn),
        _ => WarnWhiteSpace(value, inherited, warn),
    };

    private static bool WarnPreLine(Action<string>? warn)
    {
        warn?.Invoke("white-space=\"pre-line\" collapses spaces and keeps line breaks, and the model keeps both");
        return true;
    }

    private static bool WarnWhiteSpace(string value, bool inherited, Action<string>? warn)
    {
        warn?.Invoke($"white-space=\"{value}\" is not a white-space value this reader knows");
        return inherited;
    }

    /// <summary>The CSS-wide keywords, which mean "whatever is inherited" and so say nothing of their own.</summary>
    private static bool IsCssWideKeyword(string value) => value.Trim().ToLowerInvariant() is
        "inherit" or "initial" or "unset" or "revert" or "revert-layer";

    /// <summary>The families CSS defines as keywords rather than face names.</summary>
    private static bool IsGenericFamily(string family) => family.ToLowerInvariant() is
        "serif" or "sans-serif" or "monospace" or "cursive" or "fantasy" or
        "system-ui" or "ui-serif" or "ui-sans-serif" or "ui-monospace" or "ui-rounded" or
        "math" or "emoji" or "fangsong";
}
