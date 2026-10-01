namespace VCCad.Core.Model;

/// <summary>What sort of value an effect parameter takes.</summary>
public enum EffectParameterKind
{
    /// <summary>A length or a distance, in points.</summary>
    Number,

    /// <summary>A whole number - a pass count, a seed.</summary>
    Integer,

    /// <summary>A colour, or null meaning "the stroke's own".</summary>
    Color,
}

/// <summary>
/// One parameter an effect accepts: its name, its sort, and what it means.
///
/// The name is the one the operation uses, so a caller reading this declaration can call the operation without a
/// second source of truth. The meaning is here rather than in a tooltip written by hand, because a parameter whose
/// description lives somewhere else is a parameter that falls out of step with what it does.
/// </summary>
public sealed record EffectParameter(
    string Name,
    EffectParameterKind Kind,
    string Meaning,
    double Default = 0.0,
    double Minimum = double.NegativeInfinity,
    double Maximum = double.PositiveInfinity);

/// <summary>
/// An effect a stroke can carry: its kind name, whether it is a raster effect, and the parameters it takes.
///
/// **This is the declaration the panel and the operations both read.** The issue that asks for an effects panel
/// names the trap exactly: a hand-written switch in the panel falls out of step with the engine, and a person sees
/// a control that does nothing or, worse, a parameter they cannot reach. A new effect then appears in the panel by
/// **existing** - declaring itself here - rather than by being added in three places.
///
/// The declarations are per **kind**, not per family: a blur has a radius and nothing else, a drop shadow has a
/// radius and a displacement and a tint. That is the whole reason a list of names would not do.
/// </summary>
public sealed record EffectDefinition(
    string Kind,
    bool Raster,
    string Meaning,
    IReadOnlyList<EffectParameter> Parameters,
    OutlineEffectKind? OutlineKind = null,
    RasterEffectKind? RasterKind = null,
    string[]? Aliases = null)
{
    /// <summary>What the model holds for this effect, which is what an operation sets.</summary>
    public object? ModelKind => Raster ? RasterKind : OutlineKind;

    /// <summary>
    /// Whether a caller's word for this effect names it - the canonical name or an alias, ignoring case.
    ///
    /// The aliases are here rather than in the operations because they are the same kind of knowledge as the name:
    /// `drop_shadow` and `dropShadow` are the same effect, and having each operation keep its own list is how the
    /// two drift apart and one accepts a spelling the other refuses.
    /// </summary>
    public bool AnswersTo(string name)
        => Kind.Equals(name, StringComparison.OrdinalIgnoreCase) ||
           (Aliases ?? Array.Empty<string>()).Any(alias => alias.Equals(name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Every effect this build has, with what each one takes.</summary>
public static class EffectRegistry
{
    private static readonly EffectParameter Seed = new(
        "seed", EffectParameterKind.Integer,
        "What makes a random-looking effect the same every time it is drawn: the same document must render and " +
        "export identically.",
        Default: 1);

    private static readonly EffectParameter Size = new(
        "size", EffectParameterKind.Number,
        "How far a point may move - or, for an offset path, how far the edges move. Positive grows, negative shrinks.",
        Default: 2, Minimum: double.NegativeInfinity);

    private static readonly EffectParameter Detail = new(
        "detail", EffectParameterKind.Number,
        "Scribble: how many passes to draw. Roughen and zig-zag ignore it.",
        Default: 1, Minimum: 1);

    private static readonly EffectParameter Radius = new(
        "radius", EffectParameterKind.Number,
        "How far the effect spreads.", Default: 4, Minimum: 0);

    private static readonly EffectParameter OffsetX = new(
        "offsetX", EffectParameterKind.Number,
        "Drop shadow: how far the shadow is displaced.", Default: 0);

    private static readonly EffectParameter OffsetY = new(
        "offsetY", EffectParameterKind.Number,
        "Drop shadow: how far the shadow is displaced.", Default: 0);

    private static readonly EffectParameter Opacity = new(
        "opacity", EffectParameterKind.Number,
        "The effect's own opacity, which is what makes a glow subtle.", Default: 1, Minimum: 0, Maximum: 1);

    private static readonly EffectParameter Tint = new(
        "tint", EffectParameterKind.Color,
        "The effect's colour. Omitted means the stroke's own, which is the usual answer for a glow.");

    /// <summary>Every effect, outline first and then the raster ones - the order a panel offers them in.</summary>
    public static IReadOnlyList<EffectDefinition> All { get; } = new[]
    {
        new EffectDefinition("zigZag", false, "The outline wobbles to either side of the line.",
            new[] { Size, Seed }, OutlineEffectKind.ZigZag, null, new[] { "zig_zag" }),
        new EffectDefinition("roughen", false, "The outline is displaced point by point.",
            new[] { Size, Seed }, OutlineEffectKind.Roughen),
        new EffectDefinition("offsetPath", false, "The outline moves outward or inward as a whole.",
            new[] { Size }, OutlineEffectKind.OffsetPath, null, new[] { "offset_path", "offset" }),
        new EffectDefinition("scribble", false, "The stroke is drawn several times, hand-drawn style.",
            new[] { Size, Detail, Seed }, OutlineEffectKind.Scribble),

        new EffectDefinition("blur", true, "The stroke is softened.",
            new[] { Radius }, null, RasterEffectKind.Blur, new[] { "gaussianBlur", "gaussian_blur" }),
        new EffectDefinition("dropShadow", true, "A displaced copy is drawn behind the stroke.",
            new[] { Radius, OffsetX, OffsetY, Opacity, Tint }, null, RasterEffectKind.DropShadow,
            new[] { "drop_shadow", "shadow" }),
        new EffectDefinition("innerGlow", true, "A glow inside the stroke.",
            new[] { Radius, Opacity, Tint }, null, RasterEffectKind.InnerGlow, new[] { "inner_glow" }),
        new EffectDefinition("outerGlow", true, "A glow around the stroke.",
            new[] { Radius, Opacity, Tint }, null, RasterEffectKind.OuterGlow, new[] { "outer_glow" }),
    };

    /// <summary>The declaration for a kind, or null when this build has no such effect.</summary>
    public static EffectDefinition? Find(string kind)
        => All.FirstOrDefault(definition => definition.AnswersTo(kind ?? string.Empty));

    /// <summary>Whether a kind is one this build has - which is what an operation validates against.</summary>
    public static bool IsKnown(string kind) => Find(kind) is not null;
}
