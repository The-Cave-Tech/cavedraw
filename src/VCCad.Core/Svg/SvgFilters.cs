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
///
/// A region may be written as a **percentage**, and a percentage is a fraction of the reference box `filterUnits`
/// names - the object's own box, or the document's viewport under `userSpaceOnUse`. Both are resolved in one place
/// below, so a value cannot come out differently depending on which of the four read it, and one that has no
/// reference box to be a fraction of is **said** rather than replaced by the region's default: a plausible number
/// nobody stated is exactly the failure this reader exists to avoid.
/// </summary>
internal sealed class SvgFilters
{
    private readonly Dictionary<string, FilterSpec> _filters = new(StringComparer.Ordinal);

    /// <summary>The filters in the document, by id - which is the name an element refers to them by.</summary>
    public IReadOnlyDictionary<string, FilterSpec> All => _filters;

    public static SvgFilters Collect(XElement root, Action<string>? warn = null)
    {
        var filters = new SvgFilters();

        // The reference box a percentage in a **user-space** region is a fraction of. It is read once, because it
        // is a property of the document rather than of any one filter, and it is read here rather than handed in
        // because a filter is a document asset: it is collected before any element refers to it, and the element
        // that does may be anywhere - so the viewport a region is measured against can only be the document's own.
        SvgViewport? viewport = DocumentViewport(root);

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

            // A percentage in user-space units is a percentage **of the viewport**, which is a property of the
            // document rather than of the filter - so the region is resolved against it, and a document that
            // states no viewport is reported rather than given a region nobody wrote.
            var filter = new FilterSpec(id, primitives)
            {
                X = Region(element, "x", -10.0, SvgAxis.X, userSpace, viewport, id, warn),
                Y = Region(element, "y", -10.0, SvgAxis.Y, userSpace, viewport, id, warn),
                Width = Region(element, "width", 120.0, SvgAxis.X, userSpace, viewport, id, warn),
                Height = Region(element, "height", 120.0, SvgAxis.Y, userSpace, viewport, id, warn),
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
                ColorRgb colour = SvgColour.Parse(
                        element.Attribute("flood-color")?.Value ?? "black",
                        PresentationStyle.ColourInForce(element, null))
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
        ColorRgb colour = SvgColour.Parse(
                element.Attribute("lighting-color")?.Value ?? "white",
                PresentationStyle.ColourInForce(element, null))
            ?? ColorRgb.White;

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
    /// One number of the filter region, resolved against the reference box `filterUnits` decides.
    ///
    /// **`objectBoundingBox`** puts the region's numbers in the object's own box, which is what the model carries:
    /// a percentage is the fraction the engine multiplies that box by, so it is resolved without needing a shape. **`userSpaceOnUse`** puts them in user units, and a percentage there is a fraction
    /// of the **viewport** - resolved into the file's own units, which is the space a plain number in the same
    /// attribute is already written in. Both answers come from here, so `x` and `width` cannot disagree about what
    /// a percentage means.
    ///
    /// An attribute the file leaves out is resolved the same way, because SVG's initial values for all four are
    /// percentages rather than numbers: a region that is absent is not "the whole shape", and under
    /// `userSpaceOnUse` it is not `-0.1` of a user unit either.
    ///
    /// **A percentage that cannot be resolved is said, never substituted.** With no viewport stated there is no
    /// reference box for a user-space percentage to be a fraction of, and the region's default stands - which the
    /// caller is told, because the alternative is a region the file did not name looking exactly like one it did.
    /// </summary>
    /// <param name="initialPercent">SVG's own initial value for the attribute, which is a percentage.</param>
    private static double Region(
        XElement element,
        string name,
        double initialPercent,
        SvgAxis axis,
        bool userSpace,
        SvgViewport? viewport,
        string filterId,
        Action<string>? warn)
    {
        string? text = element.Attribute(name)?.Value?.Trim();
        double percent = initialPercent;

        if (text is { Length: > 0 })
        {
            if (SvgLength.ParseWithUnit(text, warn) is not { } parsed)
            {
                // The report above is the whole of what this reader can say about a value it cannot read; SVG's
                // initial value stands, which is what the file's own omission would have meant.
            }
            else if (!parsed.IsPercent)
            {
                // A plain number is already written in the reference box's units - a fraction of the object's box,
                // or a user unit - so it is the answer as it stands rather than something measured a second time.
                return parsed.Value;
            }
            else
            {
                percent = parsed.Value;
            }
        }

        if (!userSpace)
        {
            // Under the box's own units a percentage is already the fraction the model stores: the shape is
            // supplied where the filter is evaluated, so no box is needed to resolve it here.
            return percent / 100.0;
        }

        if (viewport is { } port)
        {
            // A horizontal coordinate is a fraction of the viewport's width and a vertical one of its height, so
            // a percentage is the length the file would have written for the same place.
            return percent / 100.0 * (axis == SvgAxis.Y ? port.Height : port.Width);
        }

        string stated = text is { Length: > 0 } ? $"{name}=\"{text}\"" : $"{name} (SVG's default)";
        warn?.Invoke(
            $"filter '{filterId}' resolves {stated} as a percentage of a userSpaceOnUse region, and the document " +
            "states no viewport to resolve it against; the region's default is used instead");
        return initialPercent / 100.0;
    }

    /// <summary>
    /// The document's viewport, in the units the file's own coordinates are written in, or null when the file
    /// never states one.
    ///
    /// This is the reference box a percentage in a `userSpaceOnUse` region is a fraction of, and it is a property
    /// of the root `svg` - the element a viewport and a `viewBox` are declared on.
    ///
    /// A dimension the root does not declare comes from the `viewBox`, which is the coordinate system the content
    /// is written in: a file that gives a box and one of the two has stated the other. Nothing is converted here,
    /// because a percentage of the viewport is measured in the same user units the geometry around it is.
    ///
    /// **A viewport the file never states is not one.** A root with no size still imports, because CSS gives a
    /// standalone replaced element a default size - but that is the viewer's assumption rather than the file's
    /// statement, and a region resolved against it would be a size nobody wrote. Null means exactly that, and the
    /// caller reports it. The root's own lengths are parsed without a warning channel here, because the page has
    /// already reported them on the way in, and saying the same thing twice is its own kind of noise.
    /// </summary>
    private static SvgViewport? DocumentViewport(XElement root)
    {
        double? width = SvgLength.Parse(root.Attribute("width")?.Value);
        double? height = SvgLength.Parse(root.Attribute("height")?.Value);

        if (SvgReader.Numbers(root.Attribute("viewBox")?.Value) is { Length: 4 } box && box[2] > 0 && box[3] > 0)
        {
            width ??= box[2];
            height ??= box[3];
        }

        return width is { } resolvedWidth && height is { } resolvedHeight
            ? new SvgViewport(resolvedWidth, resolvedHeight)
            : null;
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
