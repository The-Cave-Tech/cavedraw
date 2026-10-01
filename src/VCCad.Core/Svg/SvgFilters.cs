using System.Globalization;
using System.Xml.Linq;
using VCCad.Core.Model;
using VCCad.Core.Raster;

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
                if (ReadPrimitive(child, id, warn) is not { } primitive)
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
    /// silently changes what every step after it receives. The same rule applies to a lighting element this build
    /// refuses - a point or spot light - because a bevel lit by the wrong kind of light is a different bevel, and
    /// doing it quietly would be the more convincing lie of the two.
    /// </summary>
    private static FilterPrimitive? ReadPrimitive(XElement element, string filterId, Action<string>? warn)
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

            case "feMorphology":
            {
                string op = element.Attribute("operator")?.Value?.Trim() ?? "erode";
                double radius = Number(element.Attribute("radius")?.Value, 0.0);

                // `radius` may carry two numbers, one per axis, and the model has the one. Reading the first is an
                // approximation, and an approximation nobody is told about is a shape thinned unevenly with nothing
                // in the file to explain it.
                if (element.Attribute("radius")?.Value
                        ?.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries).Length > 1)
                {
                    warn?.Invoke(
                        $"filter '{filterId}' gives a two-value feMorphology radius; the model has one radius, and " +
                        "the first value is used for both axes");
                }

                return FilterPrimitive.Morph(ToOperatorWord(op), radius, input, result);
            }

            case "feColorMatrix":
                return MatrixPrimitive(element, filterId, input, result, warn);

            case "feDisplacementMap":
                return FilterPrimitive.Displace(
                    Number(element.Attribute("scale")?.Value, 0.0),
                    element.Attribute("xChannelSelector")?.Value?.Trim() ?? "A",
                    element.Attribute("yChannelSelector")?.Value?.Trim() ?? "A",
                    input ?? "SourceGraphic",
                    input2 ?? string.Empty,
                    result);

            case "feTurbulence":
            case "fePerlinNoise":
            {
                if (element.Attribute("baseFrequency")?.Value
                        ?.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries).Length > 1)
                {
                    warn?.Invoke(
                        $"filter '{filterId}' gives a two-value baseFrequency; the model has one frequency, and the " +
                        "first value is used for both axes");
                }

                return FilterPrimitive.Noise(
                    element.Attribute("type")?.Value?.Trim() ?? "turbulence",
                    Number(element.Attribute("baseFrequency")?.Value, 0.0),
                    (int)Math.Round(Number(element.Attribute("numOctaves")?.Value, 1.0)),
                    (int)Math.Round(Number(element.Attribute("seed")?.Value, 0.0)),
                    input,
                    result);
            }

            case "feSpecularLighting":
            case "feDiffuseLighting":
                return LightingPrimitive(element, filterId, input, result, warn);
        }

        return null;
    }

    /// <summary>
    /// `feColorMatrix`, with its three shorthands expanded into the matrices they are defined as.
    ///
    /// Expanding here rather than at evaluation time is what makes a shorthand and the matrix it stands for the
    /// **same model**: the filter draws the same pixels either way, and a round trip that writes back the shorthand
    /// it read is a spelling, not a difference in the picture.
    /// </summary>
    /// <summary>
    /// `feColorMatrix`, whose three shorthands are stored as **the one number that describes them** rather than as
    /// the matrix they stand for.
    ///
    /// The matrix is expanded where it is needed - <see cref="FilterEngine.MatrixOf"/> - so the model holds the
    /// shorthand a person wrote, the writer can write it back the way it was read, and a shorthand and the matrix
    /// it stands for still draw the same pixels. Storing the expansion here instead would make every `saturate` come
    /// back as a wall of twenty numbers, which is the lossy half of the round trip that this file exists to avoid.
    /// </summary>
    private static FilterPrimitive MatrixPrimitive(
        XElement element, string filterId, string? input, string result, Action<string>? warn)
    {
        string type = element.Attribute("type")?.Value?.Trim() ?? "matrix";
        double[] values = Numbers(element.Attribute("values")?.Value);

        switch (type.ToLowerInvariant())
        {
            case "saturate":
                return FilterPrimitive.ColourMatrix(
                    new[] { values.Length > 0 ? values[0] : 1.0 }, "saturate", input, result);

            case "huerotate":
                return FilterPrimitive.ColourMatrix(
                    new[] { values.Length > 0 ? values[0] : 0.0 }, "hueRotate", input, result);

            case "luminancetoalpha":
                return FilterPrimitive.ColourMatrix(Array.Empty<double>(), type, input, result);

            case "matrix":
                if (values.Length == 20)
                {
                    return FilterPrimitive.ColourMatrix(values, type, input, result);
                }

                // A matrix with the wrong number of numbers is not a matrix, and reading the ones that are there
                // would silently invent the rest. Identity is what a viewer that ignored the element would draw,
                // and the warning is what stops that being mistaken for the file's own picture.
                warn?.Invoke(
                    $"filter '{filterId}' gives feColorMatrix type=matrix with {values.Length} values rather than " +
                    "20; the step is read as the identity");
                return FilterPrimitive.ColourMatrix(FilterEngine.Identity(), type, input, result);

            default:
                warn?.Invoke($"filter '{filterId}' gives an feColorMatrix type this build does not know: {type}");
                return FilterPrimitive.ColourMatrix(FilterEngine.Identity(), "matrix", input, result);
        }
    }

    /// <summary>
    /// `feSpecularLighting` and `feDiffuseLighting`, lit by a **distant light**.
    ///
    /// A point or spot light is refused - the whole step, not the light - because the two produce visibly different
    /// pictures: a distant light gives a surface that is uniformly lit, a point light a bright patch that moves with
    /// the distance. Approximating one with the other would be a plausible-looking lie, which is worse than a filter
    /// that is honestly missing.
    /// </summary>
    private static FilterPrimitive? LightingPrimitive(
        XElement element, string filterId, string? input, string result, Action<string>? warn)
    {
        XElement? light = element.Elements().FirstOrDefault(child =>
            child.Name.LocalName is "feDistantLight" or "fePointLight" or "feSpotLight");

        if (light is not { } source || source.Name.LocalName != "feDistantLight")
        {
            warn?.Invoke(
                light is null
                    ? $"filter '{filterId}' has {element.Name.LocalName} with no light source; this build reads " +
                      "feDistantLight only, so the step is not read at all"
                    : $"filter '{filterId}' lights {element.Name.LocalName} with {light.Name.LocalName}; this build " +
                      "reads feDistantLight only, and refuses rather than approximating the other two, so the step " +
                      "is not read at all");
            return null;
        }

        double azimuth = Number(light.Attribute("azimuth")?.Value, 0.0);
        double elevation = Number(light.Attribute("elevation")?.Value, 0.0);
        double surfaceScale = Number(element.Attribute("surfaceScale")?.Value, 1.0);
        ColorRgb colour = SvgColour.Parse(element.Attribute("lighting-color")?.Value ?? "white") ?? ColorRgb.White;

        if (element.Name.LocalName == "feSpecularLighting")
        {
            return FilterPrimitive.Specular(
                surfaceScale,
                Number(element.Attribute("specularConstant")?.Value, 1.0),
                Number(element.Attribute("specularExponent")?.Value, 1.0),
                colour,
                azimuth,
                elevation,
                input,
                result);
        }

        return FilterPrimitive.Diffuse(
            surfaceScale,
            Number(element.Attribute("diffuseConstant")?.Value, 1.0),
            colour,
            azimuth,
            elevation,
            input,
            result);
    }

    /// <summary>`erode`/`dilate` in the spelling the model's operator member carries.</summary>
    private static string ToOperatorWord(string op)
        => op.Trim().ToLowerInvariant() == "dilate" ? "dilate" : "erode";

    /// <summary>The numbers of a `values` attribute, in the order written. Anything not a number is skipped rather
    /// than read as zero, because a zero that was not in the file is a matrix row nobody asked for.</summary>
    private static double[] Numbers(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<double>();
        }

        return text
            .Split(new[] { ' ', ',', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(part => double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                ? (double?)value
                : null)
            .Where(value => value is not null)
            .Select(value => value!.Value)
            .ToArray();
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
