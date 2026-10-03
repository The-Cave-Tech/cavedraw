using System.Globalization;
using System.Xml.Linq;
using VCCad.Core.Model;
using VCCad.Core.Text;

namespace VCCad.Core.Svg;

/// <summary>
/// The font and line properties a text element carries, inherited down the tree the way SVG says.
///
/// **Every property here has somewhere in the model to live**, and each is resolved to the value the model stores:
/// `font-stretch` and `font-variant` keep the file's own spelling because the model names one face per run and
/// cannot put a word back together from a number, and the two spacings become lengths so nothing downstream has to
/// know what percentage they were written as. `writing-mode`, `direction` and `glyph-orientation-vertical` are the
/// same kind of member since #127: the mode and the base direction name the block's own axes, and the orientation
/// says whether a glyph is turned in a vertical column, so all three reach the layout engine rather than being
/// reported. A property the model genuinely has no field for - `text-decoration` - is still
/// **reported** with the value the file wrote, because the rule this repository enforces is that a value the reader
/// cannot keep is said out loud, never quietly dropped.
///
/// What maps where:
/// <list type="bullet">
/// <item>`font-family` (and `-inkscape-font-specification` over it) to the face a run names.</item>
/// <item>`font-size` to the run's size, with `em`, `ex` and `%` resolved against the size in force - the context a
/// bare length elsewhere in the file does not have.</item>
/// <item>`font-weight` and `font-style` to the model's bold and italic flags.</item>
/// <item>`font-stretch` and `font-variant` to the run's own record of the face the file asked for.</item>
/// <item>`letter-spacing` and `word-spacing` to the run's tracking, resolved against its own size.</item>
/// <item>`writing-mode` and `direction` to the block, and `glyph-orientation-vertical` (or CSS's
/// `text-orientation`) to the run.</item>
/// <item>`baseline-shift` to the run, as a fraction of its own em - the layout raises its baseline by it, which is
/// what makes a superscript (#128).</item>
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
    double LineSpacing,
    double LetterSpacing,
    double WordSpacing,
    string? FontStretch,
    string? FontVariant,
    TextWritingMode WritingMode,
    TextDirection Direction,
    GlyphOrientation Orientation,
    double BaselineShift)
{
    /// <summary>
    /// SVG's initial values: a medium (16px) upright face, anchored at the start, collapsing white space, with no
    /// tracking and no width or variant asked for, laid out horizontally left to right.
    ///
    /// 16 is CSS's initial font size and so SVG's `medium`, and the family is the model's own default because the
    /// initial `font-family` is the user agent's choice - the file does not name one, so there is nothing of the
    /// file's to lose.
    /// </summary>
    public static SvgTextStyle Default { get; } = new(
        TextItem.DefaultFontFamily, 16.0, false, false, TextAlignment.Left, PreserveSpace: false, LineSpacing: 1.2,
        LetterSpacing: 0.0, WordSpacing: 0.0, FontStretch: null, FontVariant: null,
        TextWritingMode.HorizontalTb, TextDirection.LeftToRight, GlyphOrientation.Auto, BaselineShift: 0.0);

    /// <summary>Resolves the element's own text properties over the ones it inherits.</summary>
    /// <param name="faces">
    /// The faces the document supplies for itself, when they have been loaded. A face the file *carries* for a family
    /// it lists wins over `-inkscape-font-specification`: the specification names the face Inkscape chose for its own
    /// rendering, while an `@font-face` is the resource this file is handing the renderer for that family. Without
    /// that, a document's own font loses to a specification naming a generic word, which is exactly how
    /// `text-svg-glyph-custom.svg` reads (`font-family: 'SVGinOTF testfont1'` beside
    /// `-inkscape-font-specification:Serif`) and its supplied glyphs would never be used.
    /// </param>
    public static SvgTextStyle From(
        XElement element,
        SvgTextStyle inherited,
        IReadOnlyDictionary<string, (string Value, bool Important)>? sheet,
        Action<string>? warn,
        SvgFontFaces? faces = null)
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

        // A face the document carries is the one resource in this decision that is *in the file*, so it is taken
        // over the specification rather than under it.
        bool supplied = faces?.Find(ListedFamily ?? string.Empty) is not null;

        if (SpecificationFamily is { Length: > 0 } && !supplied)
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

        // The width the specification's own words asked for, when it named a width at all - "Nimbus Sans,
        // Semi-Condensed". A narrower face than the family's regular one is a design decision the author made, and
        // the run now has somewhere to keep it; the style words are the only place it is written.
        string? specWidth = null;
        if (StyleWords is { Length: > 0 })
        {
            (double specWeight, bool specItalic, string? specStretch) = ReadFaceWords(StyleWords, weight, warn);
            weight = specWeight;
            italic |= specItalic;
            specWidth = specStretch;
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

        // Both spacings are lengths the model keeps, so a relative one is resolved here, against the size in
        // force: `em` is the element's own em, `ex` half of it, and a percentage its own font size. What the run
        // stores is a length, so nothing after this has to know what the file wrote - which is also why a
        // percentage must not reach the model as "5%".
        double letterSpacing = ReadSpacing(Value("letter-spacing"), size, inherited.LetterSpacing, "letter-spacing", warn);
        double wordSpacing = ReadSpacing(Value("word-spacing"), size, inherited.WordSpacing, "word-spacing", warn);

        // Both are inherited CSS properties, so an element that states neither keeps what it inherited - which is
        // what makes a width written on a `text` element hold for every `tspan` inside it. The width a
        // specification's style words named stands in when the property itself says nothing, which is what makes
        // `-inkscape-font-specification:'Nimbus Sans, Semi-Condensed'` keep its width rather than only its slant.
        string? stretch = specWidth ?? inherited.FontStretch;
        if (ReadFaceRequest(Value("font-stretch"), "font-stretch", IsFontStretch, warn) is { } writtenStretch)
        {
            stretch = writtenStretch;
        }

        string? variant = inherited.FontVariant;
        if (ReadFaceRequest(Value("font-variant"), "font-variant", IsFontVariant, warn) is { } writtenVariant)
        {
            variant = writtenVariant;
        }

        // **The writing mode, the base direction and the glyph orientation as of #127.** All three are inherited
        // CSS properties, so an element that states none keeps what it inherited - which is what makes
        // `writing-mode` on the `text` element hold for every `tspan` inside it, and what makes a `tspan` that
        // turns its own Latin upright a run of its own.
        TextWritingMode mode = inherited.WritingMode;
        if (ReadWritingMode(Value("writing-mode"), warn) is { } writtenMode)
        {
            mode = writtenMode;
        }

        // **The direction is the file's, and only the file's.** `direction` is inherited and its initial value is
        // `ltr`, so an element that states none anywhere up the tree runs left to right. This reader used to infer
        // the base direction from the first strong character of the content (UAX #9 P2/P3) when the file stated
        // nothing, on the reasoning that a block full of Hebrew "obviously" runs right to left - and that is an
        // invention, not a reading. P2/P3 applies when no higher-level protocol states the paragraph level, and
        // SVG's own `direction` property is that statement; its initial value is part of the file's meaning. The
        // difference is not academic: the corpus file `test-rtl-vertical.svg` puts an untagged Arabic line at
        // `x="50"` and its own expected rendering - `expected_rendering/test-rtl-vertical.png`, Inkscape's, the
        // author's - draws that word with its ink at x 51..111, inside the page and starting at the file's own
        // coordinate. Inferred `rtl` puts the ink at x -36..50 instead: one line width to the left, half of it off
        // the artboard, for a file that draws it inside. A right-to-left *island* inside a left-to-right line is
        // still reordered - that is a different rule and the layout still applies it. See #127.
        TextDirection direction = inherited.Direction;
        if (ReadDirection(Value("direction"), warn) is { } writtenDirection)
        {
            direction = writtenDirection;
        }

        GlyphOrientation orientation = inherited.Orientation;
        if (ReadOrientation(Value("glyph-orientation-vertical") ?? Value("text-orientation"), warn)
            is { } writtenOrientation)
        {
            orientation = writtenOrientation;
        }

        // **The baseline shift, as of #128.** A length or a percentage of the line's height - and SVG 1.1 also names
        // CSS's `super` and `sub`, for which no specification gives a number. Resolved against the run's own size,
        // because that is the only length the reading has: see `ReadBaselineShift`.
        double baselineShift = inherited.BaselineShift;
        if (ReadBaselineShift(Value("baseline-shift"), size, warn) is { } writtenShift)
        {
            baselineShift = writtenShift;
        }

        // The embedding and override codes are not layout this reader acts on, and a file that puts one in its text
        // is a file whose visual order this reader is approximating. Named once per element that holds one, with
        // the character, because the count is what tells a person whether it matters.
        int embeddings = 0;
        foreach (char c in element.Value)
        {
            if (Bidi.IsEmbeddingCode(c))
            {
                embeddings++;
            }
        }

        if (embeddings > 0)
        {
            warn?.Invoke(
                $"the text holds {embeddings} bidirectional embedding or override character(s), which this reader " +
                "treats as neutral: the run's own order is resolved by the surrounding direction");
        }

        ReportUnkeptProperties(Value, warn);
        ReportUnselectedFace(stretch, variant, warn);

        return new SvgTextStyle(
            family, size, weight >= 600, italic, anchor, preserve, lineSpacing,
            letterSpacing, wordSpacing, stretch, variant, mode, direction, orientation, baselineShift);
    }

    /// <summary>
    /// A `writing-mode`, as the model's own member.
    ///
    /// The two values SVG 1.1 defines for text are the two vertical modes, and `horizontal-tb` is the initial value
    /// and is kept as such rather than reported. `lr` and `tb` are SVG 1.0's older spellings of the same two things
    /// and are read too. `vertical-rl` is what Inkscape and the CSS writing-modes draft call the CJK mode, and the
    /// model holds it as the block it is: the pen runs down the page and the columns stack sideways.
    /// </summary>
    private static TextWritingMode? ReadWritingMode(string? value, Action<string>? warn)
    {
        if (value is null || IsCssWideKeyword(value))
        {
            return null;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "horizontal-tb":
                return TextWritingMode.HorizontalTb;
            case "vertical-rl":
                return TextWritingMode.VerticalRl;

            // SVG 1.0's spelling of a right-to-left vertical column, and SVG 1.1's `vertical-lr`.
            case "tb" or "tb-rl" or "vertical-lr":
                return TextWritingMode.VerticalLr;
            default:
                warn?.Invoke($"writing-mode=\"{value}\" is not a writing mode this reader knows");
                return null;
        }
    }

    /// <summary>A `direction`, as the model's own member. `ltr` and `rtl` are the property's whole value space.</summary>
    private static TextDirection? ReadDirection(string? value, Action<string>? warn)
    {
        if (value is null || IsCssWideKeyword(value))
        {
            return null;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "ltr":
                return TextDirection.LeftToRight;
            case "rtl":
                return TextDirection.RightToLeft;
            default:
                warn?.Invoke($"direction=\"{value}\" is neither \"ltr\" nor \"rtl\"");
                return null;
        }
    }

    /// <summary>
    /// `glyph-orientation-vertical`, and the `text-orientation` that replaced it in CSS Writing Modes.
    ///
    /// SVG 1.1 spells it as an angle - `auto`, `0` or `90` - and CSS spells the same three as `mixed`, `upright`
    /// and `sideways`. Both are read, because a file may carry either and reading only one would turn a run the
    /// author set upright on its side.
    /// </summary>
    private static GlyphOrientation? ReadOrientation(string? value, Action<string>? warn)
    {
        if (value is null || IsCssWideKeyword(value))
        {
            return null;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "auto" or "mixed":
                return GlyphOrientation.Auto;
            case "0" or "upright":
                return GlyphOrientation.Upright;
            case "90" or "sideways" or "sideways-right":
                return GlyphOrientation.Rotate;
            default:
                warn?.Invoke($"glyph-orientation-vertical=\"{value}\" is not an orientation this reader knows");
                return null;
        }
    }

    /// <summary>
    /// One of the two spacings, as the length the model stores.
    ///
    /// `normal` is the initial value and adds nothing. `em`, `ex` and `%` are relative to the size in force - the
    /// run's own size, which is the context text has and a bare length elsewhere in the file does not. Anything
    /// that is not a length is reported, and the inherited value stands.
    /// </summary>
    private static double ReadSpacing(
        string? value, double size, double inherited, string property, Action<string>? warn)
    {
        if (value is null || IsCssWideKeyword(value))
        {
            return inherited;
        }

        string trimmed = value.Trim();
        if (trimmed.Equals("normal", StringComparison.OrdinalIgnoreCase))
        {
            return 0.0;
        }

        int end = trimmed.Length;
        while (end > 0 && (char.IsLetter(trimmed[end - 1]) || trimmed[end - 1] == '%'))
        {
            end--;
        }

        string unit = trimmed[end..].ToLowerInvariant();
        string number = trimmed[..end].Trim();
        if (double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
        {
            switch (unit)
            {
                case "" or "px":
                    return parsed;
                case "em":
                    return parsed * size;
                case "ex":
                    return parsed * size / 2.0;
                case "%":
                    return parsed / 100.0 * size;
            }
        }

        warn?.Invoke($"{property}=\"{value}\" is not a spacing this reader can resolve");
        return inherited;
    }

    /// <summary>
    /// SVG's `baseline-shift`, as the fraction of the run's em the model keeps - or null when the element says
    /// nothing, which is "keep what was inherited".
    ///
    /// **The property states a length or a percentage of the line's height, and the model keeps a fraction of the
    /// em.** A bare number is a length in the file's own user units, exactly as every other bare number in an SVG
    /// file is - and the writer emits this very property that way, so reading it as anything else loses a factor of
    /// the font size on the way in and then again on the way out. `em` is the run's own em and `ex` half of one,
    /// both resolved here because the run's size is the context text has and a bare length elsewhere in the file
    /// does not; a percentage is per cent of the line's height, which is the same fraction of the em for the small
    /// shifts a baseline is for. The layout then multiplies the fraction by the run's own size, so the shift moves
    /// with the size it belongs to.
    ///
    /// CSS 2.1 names its `super` and `sub` "the proper position" and gives no number at all, so the value this
    /// reader gives a file that names one is the one every layout engine gives it - half an em up or down (see
    /// <see cref="Super"/>/<see cref="Sub"/>) - and it is written down here because it is the reader's choice and
    /// not the specification's.
    ///
    /// `baseline` is the initial value and is kept as absence, so a file that says nothing about a baseline and one
    /// that says `baseline` hold the same run.
    /// </summary>
    private static double? ReadBaselineShift(string? value, double fontSize, Action<string>? warn)
    {
        if (value is null || IsCssWideKeyword(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        switch (trimmed.ToLowerInvariant())
        {
            case "baseline":
                return 0.0;
            case "super":
                return Super;
            case "sub":
                return Sub;
        }

        int end = trimmed.Length;
        while (end > 0 && (char.IsLetter(trimmed[end - 1]) || trimmed[end - 1] == '%'))
        {
            end--;
        }

        // The scale that turns a written number into an em fraction. A bare number is a length in the file's own
        // units, so it is divided by the size in force - the one length a text element gives a reader.
        string unit = trimmed[end..].ToLowerInvariant();
        double scale = unit switch
        {
            "" or "px" => fontSize > 0 ? 1.0 / fontSize : 0.0,
            "em" => 1.0,
            "ex" => 0.5,
            "%" => 0.01,
            _ => 0.0,
        };

        if (scale > 0 &&
            double.TryParse(trimmed[..end].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture,
                out double parsed))
        {
            return parsed * scale;
        }

        warn?.Invoke($"baseline-shift=\"{value}\" is not a baseline this reader can resolve");
        return null;
    }

    /// <summary>
    /// The fraction of an em this reader raises a run for SVG's `baseline-shift="super"`.
    ///
    /// CSS 2.1 defines both keywords as "the proper position" for a superscript or a subscript and gives no number,
    /// so every implementation chooses one; half an em is the value the CSS box-alignment model uses and the one
    /// the target renderers here (Chrome, Inkscape) apply. A superscript raises, so this is positive.
    /// </summary>
    private const double Super = 0.5;

    /// <summary>The fraction of an em this reader lowers a run for <c>baseline-shift="sub"</c>. See <see cref="Super"/>.</summary>
    private const double Sub = -0.5;

    /// <summary>
    /// One of the two face requests, as the file wrote it, or null when it says nothing.
    ///
    /// The value is kept in the file's own words because the model names one family per run and does not choose a
    /// face by width or variant: a normalised number could not be turned back into the word the author used, and
    /// inventing one would be data the file has not got. `normal` is the initial value, so it is stored as absence
    /// - a document written by Inkscape states `font-stretch:normal` on every text element it makes, and keeping
    /// it would add a member to every file in the world without changing a drawing.
    /// </summary>
    private static string? ReadFaceRequest(
        string? value, string property, Func<string, bool> isValid, Action<string>? warn)
    {
        if (value is null || IsCssWideKeyword(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        if (trimmed.Equals("normal", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!isValid(trimmed))
        {
            warn?.Invoke($"{property}=\"{value}\" is not a {property} this reader can resolve");
            return null;
        }

        return trimmed;
    }

    /// <summary>A `font-stretch`: one of CSS's width keywords, or a percentage of the face's own width.</summary>
    private static bool IsFontStretch(string value)
    {
        string key = value.Trim().ToLowerInvariant();
        if (key is
            "ultra-condensed" or "extra-condensed" or "condensed" or "semi-condensed" or "semi-expanded" or
            "expanded" or "extra-expanded" or "ultra-expanded")
        {
            return true;
        }

        // CSS 4's percentages, which Inkscape writes as `font-stretch:90%`.
        return key.EndsWith('%') &&
               double.TryParse(key[..^1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double pct) &&
               pct > 0;
    }

    /// <summary>
    /// A `font-variant`: the caps keywords, and the numeric and ligature ones CSS 2.1 and CSS Fonts define.
    ///
    /// Recognising the whole set is what makes "not reported" mean "the model holds this word", rather than "the
    /// reader did not look".
    /// </summary>
    private static bool IsFontVariant(string value)
        => value
            .Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .All(word => word.ToLowerInvariant() is
                "small-caps" or "all-small-caps" or "petite-caps" or "all-petite-caps" or "unicase" or
                "titling-caps" or "lining-nums" or "oldstyle-nums" or "proportional-nums" or "tabular-nums" or
                "diagonal-fractions" or "stacked-fractions" or "ordinal" or "slashed-zero" or
                "common-ligatures" or "no-common-ligatures" or "discretionary-ligatures" or
                "no-discretionary-ligatures" or "historical-ligatures" or "no-historical-ligatures" or
                "contextual" or "no-contextual");

    /// <summary>
    /// Reports a width or a variant the model holds and this build does not draw.
    ///
    /// A run names **one family**, and the face is chosen from it by weight and slant, so `semi-condensed` and
    /// `small-caps` are kept faithfully and then drawn in the family's own face. Saying so is the whole of what #161
    /// asks for here: a value the model holds, the sidecar round-trips and the painter ignores is the one gap a
    /// reader of the document cannot see - the document looks as though the width were in force and only the page
    /// disagrees.
    ///
    /// Selecting such a face is not something this build can do half-way. The canvas and the export have to agree
    /// about the same text - that is the invariant #159 and #162 exist for - and the export supplies a face from the
    /// standard-font chain, which has no width-selected programme to embed. So both draw the family's own face, and
    /// both are told. Choosing the values to keep is still the reader's job, which is why this is reported here and
    /// not inferred from a number somewhere downstream.
    ///
    /// Both are reported only when the file says something: `normal` is the initial value and is stored as absence,
    /// so a document Inkscape wrote states neither and is not buried in noise.
    /// </summary>
    private static void ReportUnselectedFace(string? stretch, string? variant, Action<string>? warn)
    {
        if (warn is null)
        {
            return;
        }

        if (stretch is { Length: > 0 })
        {
            warn(
                $"font-stretch=\"{stretch}\" is kept on the run, and no face is selected by width: " +
                "the face is chosen by family, weight and slant, so the run draws in the family's own face");
        }

        if (variant is { Length: > 0 })
        {
            warn(
                $"font-variant=\"{variant}\" is kept on the run, and no face is selected by variant: " +
                "the face is chosen by family, weight and slant, so the run draws in the family's own face");
        }
    }

    /// <summary>
    /// The properties that carry real layout and that the model has no field for.
    ///
    /// Each is reported **only when it says something** - `text-decoration:none` and `dominant-baseline:auto` are
    /// the initial values and change nothing, and warning about them would bury the one file that really is
    /// underlined. `font-stretch`, `font-variant`, `letter-spacing` and `word-spacing` used to be here and no
    /// longer are: the model holds all four (#147). `writing-mode` and `direction` used to be here and no longer
    /// are either: the model holds both and the layout acts on them (#127). `baseline-shift` used to be here and is
    /// not any more: the model holds it on the run and the layout raises the run's baseline by it (#128).
    /// `dominant-baseline` is still a real gap - it names which of a face's baselines the text hangs from, and the
    /// model knows only the alphabetic one.
    /// </summary>
    private static void ReportUnkeptProperties(Func<string, string?> value, Action<string>? warn)
    {
        if (warn is null)
        {
            return;
        }

        void Report(string property, string message)
        {
            if (value(property) is { Length: > 0 } written && !IsInitial(written))
            {
                warn(message.Replace("{value}", written, StringComparison.Ordinal));
            }
        }

        Report("text-decoration", "text-decoration=\"{value}\" is not kept: a run holds no decoration");
        Report("dominant-baseline", "dominant-baseline=\"{value}\" is a baseline the model does not hold");
    }

    /// <summary>Whether a written value is the property's own initial value, which says nothing.</summary>
    private static bool IsInitial(string written)
    {
        string value = written.Trim();
        return value.Equals("normal", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("none", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("baseline", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("auto", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("horizontal-tb", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("ltr", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("0", StringComparison.Ordinal);
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
    /// This is where a file records that it wanted the semi-bold cut of a family, or a narrower one, and a word
    /// this reader does not know is reported rather than ignored - the whole point of reading the specification is
    /// that the face it names is the one the author saw. A width word is handed back rather than reported,
    /// because the run now has a <see cref="TextRun.FontStretch"/> to keep it in.
    /// </summary>
    private static (double Weight, bool Italic, string? Stretch) ReadFaceWords(
        string words, double inheritedWeight, Action<string>? warn)
    {
        double weight = inheritedWeight;
        bool italic = false;
        string? stretch = null;

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
            else if (Stretch(key) is { } width)
            {
                stretch = width;
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

        return (weight, italic, stretch);
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

    /// <summary>
    /// A style word that names a width, as CSS's own keyword - "Semi-Condensed" is `semi-condensed` - or null when
    /// the word is not a width.
    ///
    /// The spellings a face name uses and the spellings CSS uses are not the same: a family is called
    /// "Nimbus Sans Narrow" and the property is called `condensed`. Translating is not inventing, because both
    /// name the same width axis; "narrow" and "wide" have no CSS keyword that means them exactly, so they keep
    /// their own word rather than being rounded to one that does not.
    /// </summary>
    private static string? Stretch(string word) => word switch
    {
        "ultracondensed" => "ultra-condensed",
        "extracondensed" => "extra-condensed",
        "semicondensed" => "semi-condensed",
        "condensed" => "condensed",
        "narrow" => "narrow",
        "semiexpanded" => "semi-expanded",
        "extraexpanded" => "extra-expanded",
        "ultraexpanded" => "ultra-expanded",
        "expanded" => "expanded",
        "wide" => "wide",
        "extended" => "expanded",
        _ => null,
    };

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
