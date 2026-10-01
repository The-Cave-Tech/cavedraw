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

            // Both unit declarations are read before the primitives, because they are what the primitives' own
            // numbers mean: `filterUnits` where the region is, `primitiveUnits` what a step's lengths are. A file
            // may set either without the other, and the two are different questions.
            bool userSpace = string.Equals(
                element.Attribute("filterUnits")?.Value?.Trim(), "userSpaceOnUse", StringComparison.OrdinalIgnoreCase);
            bool primitiveUserSpace = ReadPrimitiveUnits(element, id, warn);
            (int X, int Y)? resolution = ReadFilterResolution(element, id, warn);

            var primitives = new List<FilterPrimitive>();
            foreach (XElement child in element.Elements())
            {
                WarnOnPrimitiveLengths(child, id, primitiveUserSpace, warn);

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

            var filter = new FilterSpec(id, primitives)
            {
                X = Fraction(element, "x", -0.1),
                Y = Fraction(element, "y", -0.1),
                Width = Fraction(element, "width", 1.2),
                Height = Fraction(element, "height", 1.2),
                ObjectBoundingBox = !userSpace,
                PrimitiveUnitsObjectBoundingBox = !primitiveUserSpace,
                FilterResolutionX = resolution?.X,
                FilterResolutionY = resolution?.Y,
                Output = element.Attribute("result")?.Value ?? string.Empty,
            };

            ReportUnsuppliedSources(filter, warn);
            filters._filters[id] = filter;
        }

        return filters;
    }

    /// <summary>
    /// Whether a step's own lengths are in user units (SVG's default) rather than fractions of the shape's box.
    ///
    /// A word this build does not know is **said** and read as the default: an unrecognised `primitiveUnits` that
    /// silently became one of the two would size every blur and every offset in the filter by the wrong unit.
    /// </summary>
    private static bool ReadPrimitiveUnits(XElement element, string filterId, Action<string>? warn)
    {
        string? text = element.Attribute("primitiveUnits")?.Value?.Trim();
        if (string.IsNullOrEmpty(text) || string.Equals(text, "userSpaceOnUse", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(text, "objectBoundingBox", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        warn?.Invoke(
            $"filter '{filterId}' gives a primitiveUnits this build does not know: {text}; a primitive's lengths " +
            "are read as user units, which is SVG's default");
        return true;
    }

    /// <summary>
    /// `filterRes`: the resolution the filter is evaluated at, or null when the file gives none.
    ///
    /// A resolution is an **allocation**, so a number this build will not allocate is said and left unset rather
    /// than stored: a model carrying a resolution nothing honours is worse than one that never had it, because the
    /// picture looks evaluated when it is not. One number means both axes, which is the format's own shorthand.
    /// </summary>
    private static (int X, int Y)? ReadFilterResolution(XElement element, string filterId, Action<string>? warn)
    {
        string? text = element.Attribute("filterRes")?.Value;
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string[] parts = text.Split(new[] { ' ', ',', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        // `filterRes` is one number or two, and SVG 1.1 deprecated it in favour of the region's own size. A third
        // number is not a resolution, and reading the first two would invent a filter the file did not ask for.
        if (parts.Length is 0 or > 2 ||
            !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int width) ||
            width < 1)
        {
            warn?.Invoke(
                $"filter '{filterId}' gives a filterRes this build cannot read: {text}; the caller's own scale " +
                "decides the resolution instead");
            return null;
        }

        int height = width;
        if (parts.Length == 2 &&
            (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out height) || height < 1))
        {
            warn?.Invoke(
                $"filter '{filterId}' gives a filterRes this build cannot read: {text}; the caller's own scale " +
                "decides the resolution instead");
            return null;
        }

        if (!FilterSpec.AcceptsFilterResolution(width, height))
        {
            warn?.Invoke(
                $"filter '{filterId}' asks to be evaluated at {width} by {height} pixels, and this build evaluates " +
                $"at between 1 and {FilterSpec.MaximumFilterResolution} across; the caller's own scale decides the " +
                "resolution instead");
            return null;
        }

        return (width, height);
    }

    /// <summary>
    /// The length attributes a primitive may carry, which are the ones `primitiveUnits` decides the meaning of.
    ///
    /// `surfaceScale` is deliberately not here: it is a height in the alpha's own range rather than a distance
    /// along the surface, so it is not measured in the primitive's unit - and a file that asked for bounding-box
    /// units expecting it to be is told so below.
    /// </summary>
    private static readonly string[] PrimitiveLengthAttributes =
    {
        "stdDeviation", "dx", "dy", "radius", "scale", "baseFrequency",
    };

    /// <summary>
    /// Says what a primitive's own numbers will not mean, so a length that lands as zero is not mistaken for one
    /// the file asked for.
    ///
    /// A percentage needs a viewport or a box to resolve against, and the model carries neither for a step's own
    /// lengths - reading `5%` as zero is a blur that vanished with nothing in the file to explain it. And
    /// `surfaceScale` is a height rather than a distance, so bounding-box primitive units do not scale it.
    /// </summary>
    private static void WarnOnPrimitiveLengths(
        XElement element, string filterId, bool primitiveUserSpace, Action<string>? warn)
    {
        if (warn is null)
        {
            return;
        }

        if (!primitiveUserSpace &&
            element.Attribute("surfaceScale")?.Value is { Length: > 0 } surfaceScale &&
            element.Name.LocalName is "feSpecularLighting" or "feDiffuseLighting")
        {
            warn?.Invoke(
                $"filter '{filterId}' sets surfaceScale on {element.Name.LocalName} with bounding-box primitive " +
                $"units; surfaceScale is a height in the alpha's own range rather than a distance, so {surfaceScale} " +
                "is read as a plain number rather than as a fraction of the box");
        }

        foreach (string name in PrimitiveLengthAttributes)
        {
            string? text = element.Attribute(name)?.Value?.Trim();
            if (text is { Length: > 0 } && text.EndsWith('%'))
            {
                warn?.Invoke(
                    $"filter '{filterId}' gives {element.Name.LocalName} a percentage {name} ({text}); this model " +
                    "has no viewport or box to resolve it against, so the length is read as zero");
            }
        }
    }

    /// <summary>
    /// Says which renderer-supplied inputs a graph reads that this build's renderer does not hand it.
    ///
    /// `SourceGraphic` and `SourceAlpha` come from the shape itself. `FillPaint`, `StrokePaint` and
    /// `BackgroundImage` have to be given to the engine, and the canvas has no picture of a shape's fill alone, no
    /// picture of its stroke alone, and nothing at all behind it - so a step that reads one paints transparent,
    /// which is the "artwork quietly went missing" failure this repository names.
    /// </summary>
    private static void ReportUnsuppliedSources(FilterSpec filter, Action<string>? warn)
    {
        if (warn is null)
        {
            return;
        }

        string[] unsupplied = filter.SourceInputsRead
            .Where(name => name is "FillPaint" or "StrokePaint" or "BackgroundImage")
            .ToArray();

        if (unsupplied.Length == 0)
        {
            return;
        }

        warn?.Invoke(
            $"filter '{filter.Name}' reads {string.Join(", ", unsupplied)}; the canvas hands the engine no fill, " +
            "stroke or backdrop picture, so a step reading one paints transparent");
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
