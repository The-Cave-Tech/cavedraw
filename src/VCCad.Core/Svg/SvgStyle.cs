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
/// The three marker properties in force on an element, as the ids they name - null meaning `none`, or "nothing
/// stated" when nothing in the cascade named one.
///
/// They travel together because SVG's `marker` is a shorthand for exactly this triple and because they inherit
/// together, so one record carries the cascade rather than three loose parameters.
/// </summary>
internal readonly record struct MarkerReferences(string? Start, string? Mid, string? End)
{
    /// <summary>True when no vertex of a path carrying these references draws an arrowhead.</summary>
    public bool IsEmpty => Start is null && Mid is null && End is null;
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
    BlendMode Blend = BlendMode.Normal,
    MarkerReferences Markers = default,
    double StrokeWidth = 1.0)
{
    /// <summary>SVG's initial values: black fill, no stroke.</summary>
    public static PresentationStyle Default { get; } = new(
        FillSpec.Solid(ColorRgb.Black),
        StrokeSpec.None);

    /// <summary>Reads an element's own paint, with the cascade of attributes, stylesheet and inline style resolved.</summary>
    /// <param name="viewport">
    /// What a percentage among the stroke properties is a percentage of. Null when the document establishes no
    /// viewport, in which case such a percentage is reported rather than replaced by the property's default.
    /// </param>
    /// <param name="warn">Where a length the reader cannot resolve is reported.</param>
    /// <param name="readObjectOpacity">
    /// Whether the element's `opacity` is part of this read. It is for every element whose paint is being
    /// established, because an object's opacity multiplies the paint's own. It is **not** when the question is what
    /// a `use` site states, because the instance's own `opacity` is the group's opacity and is carried on the group
    /// rather than in its presentation - reading it as paint too would apply it twice.
    /// </param>
    public static PresentationStyle From(
        System.Xml.Linq.XElement element,
        PresentationStyle inherited,
        IReadOnlyDictionary<string, (string Value, bool Important)>? sheet = null,
        SvgViewport? viewport = null,
        Action<string>? warn = null,
        bool readObjectOpacity = true)
    {
        Dictionary<string, string> inline = ReadStyleAttribute(element);
        Dictionary<string, bool> inlineImportant = ReadStyleImportance(element);

        // The cascade, in the order CSS puts it. A presentation attribute is the **lowest** of the four, not the
        // highest - it is a fallback for when nothing else says anything - and an important rule beats a
        // non-important one wherever it came from, including over an inline style. The order itself lives in
        // SvgProperties, because the text properties come from the same cascade and must not answer it differently.
        string? Value(string name) => SvgProperties.Value(element, sheet, inline, inlineImportant, name);

        FillSpec fill = inherited.Fill;
        StrokeSpec stroke = inherited.Stroke;
        string? gradientId = inherited.FillGradientId;

        // The stroke's width is read whatever the paint is, because a marker's size is stated in stroke widths
        // (`markerUnits="strokeWidth"`) and a file may draw no stroke on the path that carries an arrowhead. It
        // used to be read only when a visible stroke was named, which is right for drawing the stroke and wrong
        // for measuring a marker - so it is one value, read once, and applied where each of them needs it.
        double strokeWidth = inherited.StrokeWidth;
        if (Value("stroke-width") is { } statedWidth && Length(statedWidth, viewport, warn) is { } resolvedWidth)
        {
            strokeWidth = resolvedWidth;
        }

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
            stroke = ParseStroke(strokeValue, Value("stroke-opacity"), Value("stroke-width"), viewport, warn)
                ?? StrokeSpec.None;
        }

        if (stroke.HasVisibleOutline)
        {
            stroke = stroke with
            {
                Width = strokeWidth,
                Cap = ParseCap(Value("stroke-linecap")) ?? stroke.Cap,
                Join = ParseJoin(Value("stroke-linejoin")) ?? stroke.Join,
                MiterLimit = Number(Value("stroke-miterlimit")) ?? stroke.MiterLimit,
                Dash = ParseDash(Value("stroke-dasharray"), Value("stroke-dashoffset"), viewport, warn) ?? stroke.Dash,
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
        //
        // **Only a paint that is drawn has an opacity to multiply.** Applying it to a `fill:none` or a stroke with
        // no outline writes an alpha into a member nothing reads - and the writer, which states only a stroke it
        // can see, cannot put that alpha back. The round trip then differed on a value with no picture behind it,
        // which is the kind of difference that gets waved away as noise until it hides a real one.
        if (readObjectOpacity && opacity is not null && double.TryParse(
                opacity.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double alpha))
        {
            if (fill.IsVisible)
            {
                fill = fill with { Color = ApplyOpacity(fill.Color, opacity) };
            }

            if (stroke.HasVisibleOutline)
            {
                stroke = stroke with { Color = ApplyOpacity(stroke.Color, opacity) };
            }

            _ = alpha;
        }

        // `marker` is the shorthand for all three, and the three longhands refine whichever marker is in force -
        // the same shape the fill and stroke properties take above. They inherit, because SVG says so: a `g` that
        // carries `marker-end` puts that arrowhead on every path under it.
        MarkerReferences markers = inherited.Markers;
        if (Value("marker") is { } shorthand)
        {
            string? only = MarkerId(shorthand, warn);
            markers = new MarkerReferences(only, only, only);
        }

        if (Value("marker-start") is { } markerStart)
        {
            markers = markers with { Start = MarkerId(markerStart, warn) };
        }

        if (Value("marker-mid") is { } markerMid)
        {
            markers = markers with { Mid = MarkerId(markerMid, warn) };
        }

        if (Value("marker-end") is { } markerEnd)
        {
            markers = markers with { End = MarkerId(markerEnd, warn) };
        }

        // Read from **this** element only, never from `inherited`: CSS's `mix-blend-mode` does not inherit, and a
        // group's blend leaking onto its children would composite each of them against a backdrop the file never
        // asked for.
        return new PresentationStyle(
            fill,
            stroke,
            gradientId,
            BlendModes.Parse(Value("mix-blend-mode")) ?? BlendMode.Normal,
            markers,
            strokeWidth);
    }

    /// <summary>
    /// The marker id a `marker`/`marker-start`/`marker-mid`/`marker-end` value names, or null for `none` and for a
    /// value this reader cannot honour - which is reported rather than read as an arrowhead that is not there.
    ///
    /// A dangling id is **not** reported here: whether the document defines the marker is a question about the
    /// document, answered where the path is read, and this method only reads the property.
    /// </summary>
    private static string? MarkerId(string value, Action<string>? warn)
    {
        string trimmed = value.Trim();
        if (trimmed.Length == 0 || trimmed.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (trimmed.StartsWith("url(", StringComparison.OrdinalIgnoreCase))
        {
            int open = trimmed.IndexOf('(');
            int close = trimmed.IndexOf(')');
            if (close > open)
            {
                string reference = trimmed[(open + 1)..close].Trim().Trim('"', '\'');
                if (reference.Length > 1 && reference[0] == '#')
                {
                    return reference[1..];
                }
            }
        }

        warn?.Invoke(
            $"a marker property states \"{value}\", which is neither 'none' nor a reference to a <marker>");
        return null;
    }

    /// <summary>The declarations inside a `style` attribute, which is a small inline stylesheet.</summary>
    internal static Dictionary<string, string> ReadStyleAttribute(System.Xml.Linq.XElement element)
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
    internal static Dictionary<string, bool> ReadStyleImportance(System.Xml.Linq.XElement element)
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

    private static StrokeSpec? ParseStroke(
        string value, string? opacity, string? width, SvgViewport? viewport, Action<string>? warn)
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
            Length(width, viewport, warn) ?? 1.0,
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
    private static DashPattern? ParseDash(
        string? value, string? offset = null, SvgViewport? viewport = null, Action<string>? warn = null)
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
        double phase = Length(offset, viewport, warn) ?? 0.0;

        return numbers.Length % 2 == 0
            ? new DashPattern(numbers, phase)
            : new DashPattern(numbers.Concat(numbers).ToArray(), phase);
    }

    /// <summary>
    /// A stroke property that is a length, with its unit converted and a percentage resolved.
    ///
    /// A percentage among the stroke properties is a fraction of the viewport's **diagonal**, which is SVG's own
    /// rule and the reason a wide stroke keeps its weight in a viewport that is not square. With no viewport to
    /// measure against there is no answer to give, and the property's default is not it - so that is reported.
    /// </summary>
    private static double? Length(string? value, SvgViewport? viewport = null, Action<string>? warn = null)
    {
        (double Value, bool IsPercent)? parsed = SvgLength.ParseWithUnit(value, warn);
        if (parsed is null)
        {
            return null;
        }

        if (!parsed.Value.IsPercent)
        {
            return parsed.Value.Value;
        }

        if (viewport is not { } port)
        {
            warn?.Invoke($"'{value}' is a percentage with no viewport to resolve it against");
            return null;
        }

        return parsed.Value.Value / 100.0 * port.Diagonal;
    }

    /// <summary>A plain number, which is what `stroke-miterlimit` and `stroke-opacity` are.</summary>
    private static double? Number(string? value)
        => double.TryParse(value?.Trim(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double parsed)
            ? parsed
            : null;
}
