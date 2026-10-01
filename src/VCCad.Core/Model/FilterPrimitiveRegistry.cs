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
    };

    /// <summary>The declaration for a kind, or null when this build has no such primitive.</summary>
    public static FilterPrimitiveDefinition? Find(string kind)
        => All.FirstOrDefault(definition => definition.AnswersTo(kind ?? string.Empty));

    /// <summary>Whether a kind is one this build has - which is what an operation validates against.</summary>
    public static bool IsKnown(string kind) => Find(kind) is not null;
}
