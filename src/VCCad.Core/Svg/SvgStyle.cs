using System.Globalization;
using VCCad.Core.Model;
using VCCad.Geometry;

namespace VCCad.Core.Svg;

/// <summary>Raised when a file cannot be read as SVG. Carries what is wrong with it, not just that something is.</summary>
public sealed class SvgImportException : Exception
{
    public SvgImportException(string message)
        : base(message)
    {
    }

    public SvgImportException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// How a shape is painted, inherited down the tree.
///
/// **Inherited, because that is what SVG says.** A `g` carrying `stroke="#f00"` paints every child that does not
/// name a stroke of its own, and a reader that treated each element's attributes in isolation would draw a file
/// with one styled group as a file of unstroked shapes - which looks like a much worse bug than a missing colour.
///
/// The presentation attributes and the equivalent entries in the `style` attribute are read together, with `style`
/// winning, which is the order the specification gives them.
/// </summary>
internal sealed record PresentationStyle(
    FillSpec Fill,
    StrokeSpec Stroke,
    string? FillGradientId = null,
    BlendMode Blend = BlendMode.Normal)
{
    /// <summary>SVG's initial values: black fill, no stroke.</summary>
    public static PresentationStyle Default { get; } = new(
        FillSpec.Solid(ColorRgb.Black),
        StrokeSpec.None);

    /// <summary>Reads an element's own paint, with the cascade of attributes, stylesheet and inline style resolved.</summary>
    public static PresentationStyle From(
        System.Xml.Linq.XElement element,
        PresentationStyle inherited,
        IReadOnlyDictionary<string, (string Value, bool Important)>? sheet = null)
    {
        Dictionary<string, string> inline = ReadStyleAttribute(element);
        Dictionary<string, bool> inlineImportant = ReadStyleImportance(element);

        // The cascade, in the order CSS puts it. A presentation attribute is the **lowest** of the four, not the
        // highest - it is a fallback for when nothing else says anything - and an important rule beats a
        // non-important one wherever it came from, including over an inline style.
        string? Value(string name)
        {
            (string Value, bool Important) fromSheet = sheet is not null && sheet.TryGetValue(name, out var s)
                ? s
                : (string.Empty, false);
            bool hasSheet = sheet is not null && sheet.ContainsKey(name);
            bool hasInline = inline.TryGetValue(name, out string? fromInline);
            bool inlineIsImportant = inlineImportant.TryGetValue(name, out bool flag) && flag;
            string? fromAttribute = element.Attribute(name)?.Value;

            if (inlineIsImportant && hasInline)
            {
                return fromInline;
            }

            if (hasSheet && fromSheet.Important)
            {
                return fromSheet.Value;
            }

            if (hasInline)
            {
                return fromInline;
            }

            return hasSheet ? fromSheet.Value : fromAttribute;
        }

        FillSpec fill = inherited.Fill;
        StrokeSpec stroke = inherited.Stroke;
        string? gradientId = inherited.FillGradientId;

        string? fillValue = Value("fill");
        if (fillValue is not null)
        {
            // `url(#id)` is a paint server rather than a colour, and it cannot be resolved here: the shape's box is
            // what a gradient is normalised against, and the shape has not been built yet. The id is carried and
            // resolved once the geometry exists.
            (FillSpec parsedFill, string? parsedId) = ParseFill(fillValue, Value("fill-opacity"));
            fill = parsedFill;
            gradientId = parsedId;
        }

        string? fillRule = Value("fill-rule");
        if (fillRule is not null)
        {
            fill = fill with { Rule = fillRule.Trim().Equals("evenodd", StringComparison.OrdinalIgnoreCase)
                ? FillRule.EvenOdd
                : FillRule.NonZero };
        }

        // Paint is only replaced when the element names one; the other stroke members refine whichever stroke is in
        // force, whether it was inherited or named **here**.
        //
        // This used to refine only the inherited case, so an element that said `stroke="#000"
        // stroke-linecap="round"` lost the cap, the join, the miter limit and the dash - the paint was read and
        // everything describing how it was laid down was not. Nothing in the corpus happens to write both together,
        // so no corpus test could see it. The SVG exporter's round trip did, on its first run.
        string? strokeValue = Value("stroke");
        bool namedStroke = strokeValue is not null;
        if (strokeValue is not null)
        {
            stroke = ParseStroke(strokeValue, Value("stroke-opacity"), Value("stroke-width"))
                ?? StrokeSpec.None;
        }

        if (stroke.HasVisibleOutline)
        {
            stroke = stroke with
            {
                Width = Length(Value("stroke-width")) ?? stroke.Width,
                Cap = ParseCap(Value("stroke-linecap")) ?? stroke.Cap,
                Join = ParseJoin(Value("stroke-linejoin")) ?? stroke.Join,
                MiterLimit = Number(Value("stroke-miterlimit")) ?? stroke.MiterLimit,
                Dash = ParseDash(Value("stroke-dasharray"), Value("stroke-dashoffset")) ?? stroke.Dash,
            };

            // Only when nothing named a paint: naming one already applied the opacity, and applying it twice is how
            // a half-transparent stroke becomes a quarter-transparent one.
            if (!namedStroke)
            {
                stroke = stroke with { Color = ApplyOpacity(stroke.Color, Value("stroke-opacity")) };
            }
        }

        string? opacity = Value("opacity");

        // Object opacity multiplies the paint's own, because that is what it is: how much of the finished object
        // shows through, applied after its fill and stroke have been composited.
        if (opacity is not null && double.TryParse(
                opacity.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double alpha))
        {
            fill = fill with { Color = ApplyOpacity(fill.Color, opacity) };
            stroke = stroke with { Color = ApplyOpacity(stroke.Color, opacity) };
            _ = alpha;
        }

        // Read from **this** element only, never from `inherited`: CSS's `mix-blend-mode` does not inherit, and a
        // group's blend leaking onto its children would composite each of them against a backdrop the file never
        // asked for.
        return new PresentationStyle(
            fill,
            stroke,
            gradientId,
            BlendModes.Parse(Value("mix-blend-mode")) ?? BlendMode.Normal);
    }

    /// <summary>The declarations inside a `style` attribute, which is a small inline stylesheet.</summary>
    private static Dictionary<string, string> ReadStyleAttribute(System.Xml.Linq.XElement element)
    {
        var declarations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? style = element.Attribute("style")?.Value;
        if (string.IsNullOrWhiteSpace(style))
        {
            return declarations;
        }

        foreach (string part in style.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = part.IndexOf(':');
            if (colon > 0)
            {
                declarations[part[..colon].Trim()] = part[(colon + 1)..].Trim();
            }
        }

        return declarations;
    }

    /// <summary>Which of an inline style's declarations say `!important`.</summary>
    private static Dictionary<string, bool> ReadStyleImportance(System.Xml.Linq.XElement element)
    {
        var importance = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        string? style = element.Attribute("style")?.Value;
        if (string.IsNullOrWhiteSpace(style))
        {
            return importance;
        }

        foreach (string part in style.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            (string property, _, bool important) = SvgStylesheet.SplitDeclaration(part);
            if (property.Length > 0)
            {
                importance[property] = important;
            }
        }

        return importance;
    }

    /// <summary>
    /// A fill value: a colour, `none`, or a reference to a paint server.
    ///
    /// The reference comes back as an id rather than resolved, because a gradient is normalised against the shape
    /// it paints and that shape does not exist yet. A solid colour comes back with a null id, so a caller can tell
    /// "paint this with this colour" from "paint it with whatever `#g` turns out to be".
    /// </summary>
    private static (FillSpec Fill, string? GradientId) ParseFill(string value, string? opacity)
    {
        string trimmed = value.Trim();
        if (trimmed.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return (FillSpec.None, null);
        }

        if (trimmed.StartsWith("url(", StringComparison.OrdinalIgnoreCase))
        {
            int open = trimmed.IndexOf('(');
            int close = trimmed.IndexOf(')');
            if (close > open)
            {
                string reference = trimmed[(open + 1)..close].Trim().Trim('"', '\'');
                string id = reference.StartsWith('#') ? reference[1..] : string.Empty;

                // Visible and fully transparent until the gradient is resolved: the shape is filled, and what
                // fills it is not known yet.
                return (FillSpec.Solid(ColorRgb.Black with { A = 0.0 }), id.Length > 0 ? id : null);
            }
        }

        ColorRgb colour = SvgColour.Parse(trimmed) ?? ColorRgb.Black;
        return (FillSpec.Solid(ApplyOpacity(colour, opacity)), null);
    }

    private static StrokeSpec? ParseStroke(string value, string? opacity, string? width)
    {
        string trimmed = value.Trim();
        if (trimmed.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        ColorRgb colour = SvgColour.Parse(trimmed) ?? ColorRgb.Black;
        return new StrokeSpec(
            true,
            ApplyOpacity(colour, opacity),
            Length(width) ?? 1.0,
            StrokeCap.Butt,
            StrokeJoin.Miter,
            4.0);
    }

    private static ColorRgb ApplyOpacity(ColorRgb colour, string? opacity)
        => opacity is not null &&
           double.TryParse(opacity.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double alpha)
            ? colour with { A = colour.A * Math.Clamp(alpha, 0.0, 1.0) }
            : colour;

    private static StrokeCap? ParseCap(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "round" => StrokeCap.Round,
        "square" => StrokeCap.Square,
        "butt" => StrokeCap.Butt,
        _ => null,
    };

    private static StrokeJoin? ParseJoin(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "round" => StrokeJoin.Round,
        "bevel" => StrokeJoin.Bevel,
        "miter" => StrokeJoin.Miter,
        _ => null,
    };

    /// <summary>
    /// A dash list.
    ///
    /// An odd number of entries is **doubled**, because SVG says a list of five dashes means ten - the pattern is
    /// repeated to make it even. Reading it as-is would dash the line with a pattern twice as long as the file
    /// meant.
    /// </summary>
    private static DashPattern? ParseDash(string? value, string? offset = null)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        double[]? numbers = SvgReader.Numbers(value);
        if (numbers is not { Length: > 0 })
        {
            return null;
        }

        // The phase comes with the pattern: `stroke-dashoffset` decides where in it the line starts, and a dashed
        // line that begins at the beginning instead of where it was drawn is a different picture.
        double phase = Length(offset) ?? 0.0;

        return numbers.Length % 2 == 0
            ? new DashPattern(numbers, phase)
            : new DashPattern(numbers.Concat(numbers).ToArray(), phase);
    }

    private static double? Length(string? value) => SvgReader.Length(value);

    /// <summary>A plain number, which is what `stroke-miterlimit` and `stroke-opacity` are.</summary>
    private static double? Number(string? value)
        => double.TryParse(value?.Trim(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double parsed)
            ? parsed
            : null;
}
