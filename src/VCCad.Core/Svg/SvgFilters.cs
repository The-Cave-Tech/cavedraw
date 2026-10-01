using System.Globalization;
using System.Xml.Linq;
using VCCad.Core.Model;

namespace VCCad.Core.Svg;

/// <summary>
/// Reads SVG filters.
///
/// A filter is a **directed graph**, not a list of effects: `in`, `in2` and `result` name buffers, an element can
/// feed two consumers, a consumer can take two inputs, and an intermediate result can be the filter's output. The
/// corpus guarantees that shape is used - `feComposite` appears 155 times across Inkscape's test files, more than
/// any other element - so this reads the wiring rather than flattening it into a chain.
///
/// The **region** comes with it. `x`, `y`, `width` and `height` decide where the filter is evaluated, which is why
/// a blur near an edge either grows into the margin or is clipped off; it is not decoration, and it is the first
/// thing that looks wrong when it is read incorrectly.
/// </summary>
internal sealed class SvgFilters
{
    private readonly Dictionary<string, FilterSpec> _filters = new(StringComparer.Ordinal);

    /// <summary>The filters in the document, by id - which is the name an element refers to them by.</summary>
    public IReadOnlyDictionary<string, FilterSpec> All => _filters;

    public static SvgFilters Collect(XElement root, Action<string>? warn = null)
    {
        var filters = new SvgFilters();

        foreach (XElement element in root.DescendantsAndSelf())
        {
            if (element.Name.LocalName != "filter")
            {
                continue;
            }

            string? id = element.Attribute("id")?.Value;
            if (string.IsNullOrEmpty(id))
            {
                continue;
            }

            var primitives = new List<FilterPrimitive>();
            foreach (XElement child in element.Elements())
            {
                if (ReadPrimitive(child) is not { } primitive)
                {
                    // A primitive this build does not read is **said**, not skipped quietly: a filter is a graph,
                    // so a step that does nothing silently changes what every step after it receives - and the
                    // shape comes out looking as though nobody had asked for a filter at all.
                    warn?.Invoke($"filter '{id}' uses a primitive this build does not read: {child.Name.LocalName}");
                    continue;
                }

                primitives.Add(primitive);

                // `stdDeviation` may carry two numbers, one per axis, and the model has a single radius. Using the
                // first is an approximation, and an approximation nobody is told about is a blur that is wrong in
                // one direction with nothing to explain it.
                if (primitive.Kind == FilterPrimitiveKind.GaussianBlur &&
                    child.Attribute("stdDeviation")?.Value
                        .Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries).Length > 1)
                {
                    warn?.Invoke(
                        $"filter '{id}' gives a two-value stdDeviation; the model has one radius, and the first " +
                        "value is used for both axes");
                }
            }

            if (primitives.Count == 0)
            {
                // A filter with no primitives paints nothing at all, which is not a state worth storing: an element
                // referring to it would vanish rather than be filtered.
                continue;
            }

            bool userSpace = string.Equals(
                element.Attribute("filterUnits")?.Value?.Trim(), "userSpaceOnUse", StringComparison.OrdinalIgnoreCase);

            // A percentage in user-space units is a percentage **of the viewport**, and the reader has no viewport
            // here - it is a property of the document, not of the filter. Reading `-10%` as a tenth of a user unit
            // is wrong, so it is **said** rather than done quietly: the region is read as a fraction, and the
            // caller is told that is an approximation it should not treat as exact.
            if (userSpace &&
                new[] { "x", "y", "width", "height" }.Any(name =>
                    element.Attribute(name)?.Value?.Trim().EndsWith('%') == true))
            {
                warn?.Invoke(
                    $"filter '{id}' gives a percentage region with userSpaceOnUse units, which resolve against the " +
                    "viewport; read as a fraction of the object instead");
            }

            filters._filters[id] = new FilterSpec(id, primitives)
            {
                X = Fraction(element, "x", -0.1),
                Y = Fraction(element, "y", -0.1),
                Width = Fraction(element, "width", 1.2),
                Height = Fraction(element, "height", 1.2),
                ObjectBoundingBox = !userSpace,
                Output = element.Attribute("result")?.Value ?? string.Empty,
            };
        }

        return filters;
    }

    /// <summary>
    /// One primitive, or null when the element is not one this reader knows.
    ///
    /// An unknown primitive is skipped rather than approximated: a filter is a graph, and a step that does nothing
    /// silently changes what every step after it receives.
    /// </summary>
    private static FilterPrimitive? ReadPrimitive(XElement element)
    {
        string? input = element.Attribute("in")?.Value;
        string? input2 = element.Attribute("in2")?.Value;
        string result = element.Attribute("result")?.Value ?? string.Empty;

        switch (element.Name.LocalName)
        {
            case "feGaussianBlur":
            {
                double radius = Number(element.Attribute("stdDeviation")?.Value, 0.0);
                return FilterPrimitive.Blur(radius, input, result);
            }

            case "feOffset":
                return FilterPrimitive.OffsetBy(
                    Number(element.Attribute("dx")?.Value, 0.0),
                    Number(element.Attribute("dy")?.Value, 0.0),
                    input,
                    result);

            case "feFlood":
            {
                ColorRgb colour = SvgColour.Parse(element.Attribute("flood-color")?.Value ?? "black")
                    ?? ColorRgb.Black;
                double opacity = Number(element.Attribute("flood-opacity")?.Value, 1.0);
                return FilterPrimitive.Solid(colour, opacity, result);
            }

            case "feComposite":
                return FilterPrimitive.Combine(
                    element.Attribute("operator")?.Value?.Trim() ?? "over",
                    input ?? "SourceGraphic",
                    input2 ?? string.Empty,
                    result);

            case "feBlend":
                return FilterPrimitive.Blended(
                    element.Attribute("mode")?.Value?.Trim() ?? "normal",
                    input ?? "SourceGraphic",
                    input2 ?? string.Empty,
                    result);
        }

        return null;
    }

    /// <summary>
    /// A region number, which may be a fraction or a percentage.
    ///
    /// The defaults are SVG's own, including the ten per cent of margin - which is why a blur larger than that is
    /// clipped, and why reading this as "the whole shape" makes every blurred edge grow instead of being cut.
    /// </summary>
    private static double Fraction(XElement element, string name, double fallback)
    {
        string? text = element.Attribute(name)?.Value;
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        string trimmed = text.Trim();
        bool percent = trimmed.EndsWith('%');
        double? value = SvgReader.Length(trimmed);
        if (value is null)
        {
            return fallback;
        }

        return percent ? value.Value / 100.0 : value.Value;
    }

    private static double Number(string? text, double fallback)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        // `stdDeviation` may carry two numbers - one per axis - and the model has one radius, so the first is read
        // and the second is noted as a gap rather than averaged into a blur that is wrong in both directions.
        string first = text.Trim().Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
            ?? string.Empty;

        return double.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : fallback;
    }
}
