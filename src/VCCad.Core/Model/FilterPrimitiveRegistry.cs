namespace VCCad.Core.Model;

/// <summary>What sort of value a filter primitive's parameter takes.</summary>
public enum FilterParameterKind
{
    /// <summary>A length or a distance in the primitive's own units - a blur radius, a displacement.</summary>
    Number,

    /// <summary>A colour, given the way the other operations take one: components 0-255.</summary>
    Color,

    /// <summary>One of a fixed set of words - a composite operator, a blend mode.</summary>
    Choice,

    /// <summary>
    /// A buffer name: `SourceGraphic`, `SourceAlpha`, or the `result` of another step. This is the **wiring**, not a
    /// value - an absent input is the previous step's result, which is why none of these is required and why naming
    /// one is <c>filter.connectPrimitive</c>'s job rather than a parameter edit.
    /// </summary>
    Buffer,
}

/// <summary>
/// One parameter a filter primitive takes: its name, its sort, what it means, and what it may hold.
///
/// The name is the one the operations use, so a caller reading this declaration can call
/// <c>filter.addPrimitive</c> or <c>filter.setPrimitiveParameter</c> without a second source of truth. The meaning
/// lives here rather than in a tooltip written by hand, for the same reason it does in
/// <see cref="EffectRegistry"/>: a description kept somewhere else falls out of step with what the parameter does.
/// </summary>
public sealed record FilterParameter(
    string Name,
    FilterParameterKind Kind,
    string Meaning,
    bool Required = false,
    string? Default = null,
    double Minimum = double.NegativeInfinity,
    double Maximum = double.PositiveInfinity,
    string[]? Choices = null);

/// <summary>
/// One filter primitive this build has: the `<c>fe*</c>` element it is written as, the model kind it becomes, and
/// the parameters it takes.
///
/// **This is the declaration the operations and the panel both read.** A panel that switches on the kind by hand
/// falls out of step with the engine, and a person then sees a control that does nothing or a parameter they cannot
/// reach; a new primitive appears in the panel by **existing** - declaring itself here - rather than by being added
/// in three places.
///
/// The declarations are per **kind**, not per family: a blur has a radius and nothing else, a composite has two
/// inputs and an operator. That is why a list of names would not do.
/// </summary>
public sealed record FilterPrimitiveDefinition(
    string Kind,
    string Element,
    FilterPrimitiveKind ModelKind,
    string Meaning,
    IReadOnlyList<FilterParameter> Parameters,
    string[]? Aliases = null)
{
    /// <summary>
    /// Whether a caller's word for this primitive names it - the canonical name or an alias, ignoring case.
    ///
    /// The aliases are here rather than in the operations because they are the same kind of knowledge as the name:
    /// `feGaussianBlur` and `gaussianBlur` are the same step, and a list kept per operation is how one accepts a
    /// spelling the other refuses.
    /// </summary>
    public bool AnswersTo(string name)
        => Kind.Equals(name, StringComparison.OrdinalIgnoreCase) ||
           Element.Equals(name, StringComparison.OrdinalIgnoreCase) ||
           (Aliases ?? Array.Empty<string>()).Any(alias => alias.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The declaration for one of this kind's parameters, or null when the kind has no such parameter.</summary>
    public FilterParameter? Parameter(string name)
        => Parameters.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The parameters without which this kind cannot be evaluated, in the order they are declared.</summary>
    public IEnumerable<FilterParameter> Required => Parameters.Where(p => p.Required);
}

/// <summary>Every filter primitive this build has, with what each one takes.</summary>
public static class FilterPrimitiveRegistry
{
    private static readonly FilterParameter In = new(
        "in", FilterParameterKind.Buffer,
        "The buffer this step reads. Omitted means the previous step's result - SVG's own rule - so a graph written " +
        "as a chain needs no names at all.");

    private static readonly FilterParameter In2 = new(
        "in2", FilterParameterKind.Buffer,
        "The second buffer, for the steps that take two: the backdrop a blend is blended onto, or the B in a " +
        "composite's 'A over B'.");

    private static readonly FilterParameter Result = new(
        "result", FilterParameterKind.Buffer,
        "What this step calls its answer. Named here, the buffer can feed any number of later steps, which is what " +
        "makes the filter a graph rather than a list.");

    private static readonly FilterParameter Radius = new(
        "radius", FilterParameterKind.Number,
        "`stdDeviation`: how far the blur spreads, in the primitive's own units. The model has one radius, so a " +
        "two-value stdDeviation in a file is read as its first value.",
        Required: true, Minimum: 0);

    private static readonly FilterParameter Dx = new(
        "dx", FilterParameterKind.Number,
        "How far the picture moves along x. Positive is to the right, in the primitive's own units.",
        Default: "0");

    private static readonly FilterParameter Dy = new(
        "dy", FilterParameterKind.Number,
        "How far the picture moves along y. Positive is down, because the model's y grows downward.",
        Default: "0");

    private static readonly FilterParameter FloodColor = new(
        "floodColor", FilterParameterKind.Color,
        "The colour the region is filled with, as [r,g,b] with components 0-255. Omitted means black.",
        Default: "black");

    private static readonly FilterParameter FloodOpacity = new(
        "floodOpacity", FilterParameterKind.Number,
        "How opaque that fill is. A shadow is a flood kept inside the shape, so this is what makes it soft.",
        Default: "1", Minimum: 0, Maximum: 1);

    private static readonly FilterParameter Operator = new(
        "operator", FilterParameterKind.Choice,
        "Which Porter-Duff operator combines the two buffers, or `arithmetic` for the product of them.",
        Default: "over", Choices: new[] { "over", "in", "out", "atop", "xor", "arithmetic" });

    private static readonly FilterParameter Mode = new(
        "mode", FilterParameterKind.Choice,
        "How the source is blended into the backdrop.",
        Default: "normal", Choices: new[] { "normal", "multiply", "screen", "darken", "lighten" });

    /// <summary>
    /// `radius` again, but for morphology rather than a blur, and therefore a different declaration: the two are
    /// both "how far the effect reaches" and they are not the same parameter, so they are not the same record.
    /// </summary>
    private static readonly FilterParameter MorphologyRadius = new(
        "radius", FilterParameterKind.Number,
        "How far the box grows (dilate) or shrinks (erode) the picture, in the primitive's own units. The box is " +
        "square - SVG's own default - so the effect is the same in every direction.",
        Required: true, Default: "0", Minimum: 0);

    private static readonly FilterParameter MorphologyOperator = new(
        "operator", FilterParameterKind.Choice,
        "`dilate` grows the picture by the box; `erode` shrinks it. An eroded shape eventually disappears, which is " +
        "what makes erode the harder of the two to reason about.",
        Default: "erode", Choices: new[] { "erode", "dilate" });

    private static readonly FilterParameter MatrixType = new(
        "type", FilterParameterKind.Choice,
        "How `values` is read: twenty numbers, or one of the shorthands SVG defines for the three common " +
        "transforms. `saturate` and `hueRotate` are expanded to their matrices as they are read, so a filter that " +
        "used a shorthand and one that spelled the matrix out draw the same pixels.",
        Default: "matrix", Choices: new[] { "matrix", "saturate", "hueRotate", "luminanceToAlpha" });

    private static readonly FilterParameter MatrixValues = new(
        "values", FilterParameterKind.Number,
        "The twenty numbers of the 4x5 matrix in row-major order - four colour rows of five, then the alpha row - " +
        "or the single number a shorthand takes. The fifth column of each row is the constant added to that " +
        "channel, which is why a matrix is twenty numbers and not sixteen.",
        Required: true);

    private static readonly FilterParameter MapScale = new(
        "scale", FilterParameterKind.Number,
        "How far a full channel swing (from 0 to 1) displaces the first input, in the primitive's own units. A " +
        "displacement reads a channel of the second input, subtracts a half, and moves the picture by that much " +
        "times this.",
        Required: true, Default: "0");

    private static readonly FilterParameter XChannel = new(
        "xChannel", FilterParameterKind.Choice,
        "Which channel of the second input moves the picture along x: `A`, `R`, `G` or `B`. `A` is the usual " +
        "choice, because the other picture is usually a shape rather than a colour.",
        Default: "A", Choices: new[] { "A", "R", "G", "B" });

    private static readonly FilterParameter YChannel = new(
        "yChannel", FilterParameterKind.Choice,
        "Which channel of the second input moves the picture along y. See `xChannel`.",
        Default: "A", Choices: new[] { "A", "R", "G", "B" });

    private static readonly FilterParameter NoiseType = new(
        "type", FilterParameterKind.Choice,
        "`turbulence` sums the absolute value of each octave, which is the billowy look; `fractalNoise` sums the " +
        "octaves themselves, which is the cloudy one. `fePerlinNoise` is the same element under its old name.",
        Default: "turbulence", Choices: new[] { "turbulence", "fractalNoise" });

    private static readonly FilterParameter BaseFrequency = new(
        "baseFrequency", FilterParameterKind.Number,
        "How many noise cycles fit in one unit, so a larger number is finer grain. A file may give two values, one " +
        "per axis, and the model has one: the first is read and the second is reported rather than averaged in.",
        Required: true, Default: "0", Minimum: 0);

    private static readonly FilterParameter Octaves = new(
        "numOctaves", FilterParameterKind.Number,
        "How many noise frequencies are stacked, each twice the last and half as loud. One octave is smooth; the " +
        "grain is what the later ones add.",
        Default: "1", Minimum: 0, Maximum: 12);

    private static readonly FilterParameter NoiseSeed = new(
        "seed", FilterParameterKind.Number,
        "The number the noise is drawn from, so the same document always renders the same texture. It is a whole " +
        "number, and it is **not** the clock: a filter that looked different on every render would make the canvas " +
        "and the export disagree for no visible reason.",
        Default: "0", Minimum: 0);

    private static readonly FilterParameter SurfaceScale = new(
        "surfaceScale",
        FilterParameterKind.Number,
        "How tall the input's alpha is taken to be, in the primitive's own units. A flat surface lit head-on gives " +
        "one value everywhere; the scale is what turns a soft edge into a rounded one.",
        Default: "1");

    private static readonly FilterParameter DiffuseConstant = new(
        "diffuseConstant", FilterParameterKind.Number,
        "How much light the surface reflects. The diffuse result is this times the angle between the surface and " +
        "the light, so a head-on flat surface gives exactly this.",
        Default: "1", Minimum: 0);

    private static readonly FilterParameter SpecularConstant = new(
        "specularConstant", FilterParameterKind.Number,
        "How bright the highlight is. The specular result is this times the highlight term raised to the " +
        "exponent, so a head-on flat surface gives exactly this.",
        Default: "1", Minimum: 0);

    private static readonly FilterParameter SpecularExponent = new(
        "specularExponent", FilterParameterKind.Number,
        "How tight the highlight is. One is broad and soft; large values give the small hard glint a bevel has.",
        Default: "1", Minimum: 0, Maximum: 128);

    private static readonly FilterParameter LightingColor = new(
        "lightingColor", FilterParameterKind.Color,
        "The colour of the light, as [r,g,b] with components 0-255 - and the colour of the result, because the " +
        "surface contributes a height rather than a colour of its own.",
        Default: "white");

    private static readonly FilterParameter Azimuth = new(
        "azimuth", FilterParameterKind.Number,
        "Which way round a distant light sits, in degrees: zero is along +x and the angle grows toward +y, which " +
        "in this model is downward.",
        Default: "0");

    private static readonly FilterParameter Elevation = new(
        "elevation", FilterParameterKind.Number,
        "How high a distant light sits, in degrees above the surface: ninety is directly overhead, and zero is on " +
        "the horizon.",
        Default: "0");

    /// <summary>Every primitive, in the order a panel offers them - the one every file uses first.</summary>
    public static IReadOnlyList<FilterPrimitiveDefinition> All { get; } = new[]
    {
        new FilterPrimitiveDefinition(
            "gaussianBlur", "feGaussianBlur", FilterPrimitiveKind.GaussianBlur,
            "The buffer is softened. The one primitive every real file uses.",
            new[] { In, Radius, Result },
            new[] { "blur" }),

        new FilterPrimitiveDefinition(
            "offset", "feOffset", FilterPrimitiveKind.Offset,
            "The buffer is moved, usually as a shadow's first step. Pixels pushed off the region are gone, which is " +
            "what the region means.",
            new[] { In, Dx, Dy, Result }),

        new FilterPrimitiveDefinition(
            "flood", "feFlood", FilterPrimitiveKind.Flood,
            "The whole region becomes one colour and opacity - the raw material of a shadow, kept to the shape by " +
            "compositing it with `SourceAlpha`.",
            new[] { FloodColor, FloodOpacity, Result }),

        new FilterPrimitiveDefinition(
            "composite", "feComposite", FilterPrimitiveKind.Composite,
            "Two buffers are combined by one operator. The most used primitive in the corpus, and the one that makes " +
            "the graph a graph: it reads two names rather than the two steps before it.",
            new[] { In, In2, Operator, Result }),

        new FilterPrimitiveDefinition(
            "blend", "feBlend", FilterPrimitiveKind.Blend,
            "The source is blended into the backdrop by one of the separable blend modes, then composited over it.",
            new[] { In, In2, Mode, Result }),

        new FilterPrimitiveDefinition(
            "morphology", "feMorphology", FilterPrimitiveKind.Morphology,
            "The picture is grown or shrunk by a box. `dilate` thickens an edge and is how a soft outline is built " +
            "up; `erode` thins it, and eroding far enough leaves nothing at all.",
            new[] { In, MorphologyOperator, MorphologyRadius, Result },
            new[] { "morph" }),

        new FilterPrimitiveDefinition(
            "colorMatrix", "feColorMatrix", FilterPrimitiveKind.ColorMatrix,
            "The colours are transformed by a 4x5 matrix. The shorthands - desaturate, rotate the hue, take " +
            "luminance as alpha - are the same matrices under names, and are expanded as they are read.",
            new[] { In, MatrixType, MatrixValues, Result },
            new[] { "colourMatrix" }),

        new FilterPrimitiveDefinition(
            "displacementMap", "feDisplacementMap", FilterPrimitiveKind.DisplacementMap,
            "One buffer pushes another around: a channel of `in2` displaces `in` by up to half the scale in each " +
            "direction. The two inputs are the picture and the thing that distorts it.",
            new[] { In, In2, MapScale, XChannel, YChannel, Result },
            new[] { "displacement" }),

        new FilterPrimitiveDefinition(
            "turbulence", "feTurbulence", FilterPrimitiveKind.Turbulence,
            "Seeded noise - the paper grain, the cloud, the water. The seed fixes it, so the same document renders " +
            "and exports identically; a file that asked for another seed gets another texture.",
            new[] { In, NoiseType, BaseFrequency, Octaves, NoiseSeed, Result },
            new[] { "perlinNoise", "fePerlinNoise" }),

        new FilterPrimitiveDefinition(
            "specularLighting", "feSpecularLighting", FilterPrimitiveKind.SpecularLighting,
            "The input's alpha is treated as a height field and lit to a highlight. The light is a " +
            "`feDistantLight`: a point or spot light is refused rather than approximated, because a bevel lit by " +
            "the wrong kind of light is a different bevel.",
            new[] { In, SurfaceScale, SpecularConstant, SpecularExponent, LightingColor, Azimuth, Elevation, Result },
            new[] { "specular" }),

        new FilterPrimitiveDefinition(
            "diffuseLighting", "feDiffuseLighting", FilterPrimitiveKind.DiffuseLighting,
            "The input's alpha is treated as a height field and lit diffusely - the matte half of the bevel pair. " +
            "The light is a `feDistantLight`, as for the specular one.",
            new[] { In, SurfaceScale, DiffuseConstant, LightingColor, Azimuth, Elevation, Result },
            new[] { "diffuse" }),
    };

    /// <summary>The declaration for a kind, or null when this build has no such primitive.</summary>
    public static FilterPrimitiveDefinition? Find(string kind)
        => All.FirstOrDefault(definition => definition.AnswersTo(kind ?? string.Empty));

    /// <summary>Whether a kind is one this build has - which is what an operation validates against.</summary>
    public static bool IsKnown(string kind) => Find(kind) is not null;
}
