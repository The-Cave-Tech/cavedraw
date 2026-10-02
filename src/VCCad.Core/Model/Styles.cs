using VCCad.Geometry;

namespace VCCad.Core.Model;

/// <summary>
/// A colour described by additive RGB channels.
///
/// Channels are normalised to [0,1] (1.0 = full intensity). Float-normalised RGB
/// maps directly onto PDF's <c>rg</c>/<c>RG</c> operators and Skia colour floats,
/// which is why the model avoids 0..255 integers.
///
/// CMYK is intentionally absent from the model: it is a *device* colour space and
/// would corrupt lossless round-tripping when documents travel between displays.
/// The PDF exporter may convert RGB → CMYK only when the user asks for it.
/// </summary>
public readonly record struct ColorRgb(double R, double G, double B, double A = 1.0)
{
    /// <summary>Builds a colour from 0..255 byte channels (the UI "picker" representation).</summary>
    public static ColorRgb FromBytes(byte r, byte g, byte b, byte a = 255)
        => new(r / 255.0, g / 255.0, b / 255.0, a / 255.0);

    /// <summary>Pure black.</summary>
    public static ColorRgb Black { get; } = new(0.0, 0.0, 0.0);

    /// <summary>Pure white.</summary>
    public static ColorRgb White { get; } = new(1.0, 1.0, 1.0);

    /// <summary>Mid grey.</summary>
    public static ColorRgb Gray { get; } = new(0.5, 0.5, 0.5);

    /// <summary>Full-intensity red.</summary>
    public static ColorRgb Red { get; } = new(1.0, 0.0, 0.0);

    /// <summary>Full-intensity green.</summary>
    public static ColorRgb Green { get; } = new(0.0, 1.0, 0.0);

    /// <summary>Full-intensity blue.</summary>
    public static ColorRgb Blue { get; } = new(0.0, 0.0, 1.0);

    /// <summary>Channel values, each clamped defensively into [0,1].</summary>
    public ColorRgb Clamped()
        => new(MathUtils.Clamp(R, 0.0, 1.0), MathUtils.Clamp(G, 0.0, 1.0), MathUtils.Clamp(B, 0.0, 1.0),
            MathUtils.Clamp(A, 0.0, 1.0));

    /// <summary>A copy with a different alpha channel.</summary>
    public ColorRgb WithAlpha(double alpha) => this with { A = MathUtils.Clamp(alpha, 0.0, 1.0) };
}

/// <summary>
/// Winding rule used when filling a path. Governs how self-intersections and
/// nested subpaths (e.g. the hole of a donut) decide whether a point is inside.
/// </summary>
public enum FillRule
{
    /// <summary>Point is inside when the winding number is non-zero (default, matches most tools).</summary>
    NonZero,

    /// <summary>Point is inside when the winding number is odd.</summary>
    EvenOdd,
}

/// <summary>
/// Shape applied to the two open ends of a stroked path (PDF /linecap).
/// Closed paths never expose caps because they have no free end.
/// </summary>
public enum StrokeCap
{
    /// <summary>The stroke ends flush with the path end (PDF 0).</summary>
    Butt,

    /// <summary>The stroke ends in a semicircle of radius width/2 (PDF 1).</summary>
    Round,

    /// <summary>The stroke ends in a square overhang of width/2 (PDF 2).</summary>
    Square,
}

/// <summary>
/// Shape of the outside corner where two stroked segments meet (PDF /linejoin).
/// </summary>
public enum StrokeJoin
{
    /// <summary>Corner extended until the outer edges meet; clipped by the miter limit (PDF 0).</summary>
    Miter,

    /// <summary>Corner is a semicircle of radius width/2 (PDF 1).</summary>
    Round,

    /// <summary>Corner is cut off by a straight line between the outer edges (PDF 2).</summary>
    Bevel,
}

/// <summary>
/// Immutable fill specification for a path. A path is filled only when it is
/// closed (per the brief: "Closed paths should be filled"), but the model keeps
/// the fill spec available regardless so an open path can be filled after it is
/// closed without losing the user's chosen colour.
///
/// <para>
/// <see cref="Color"/> is always meaningful, even for a gradient: it is the colour to paint with
/// when the gradient cannot be, which is how a renderer without shading support, a flattened
/// export, or a swatch preview shows the fill. A gradient therefore never leaves a fill with no
/// colour at all.
/// </para>
///
/// <para>
/// <see cref="Gradient"/> is null for a solid fill. It is the last parameter and optional so that
/// every existing construction - and every deserialized document that predates gradients - keeps
/// its meaning without change.
/// </para>
/// </summary>
public sealed record FillSpec(
    bool IsVisible,
    ColorRgb Color,
    FillRule Rule,
    GradientSpec? Gradient = null,
    HatchSpec? Hatch = null)
{
    /// <summary>
    /// A hatch fill. The colour is the fallback used wherever a hatch cannot be drawn - a solid swatch, a
    /// thumbnail, a format with no way to express one - and the lines themselves are drawn in the object's
    /// **stroke** colour, which is what makes a hatch read as hatching rather than as a filled shape.
    /// </summary>
    public static FillSpec WithHatch(
        HatchSpec hatch,
        FillRule rule = FillRule.NonZero,
        ColorRgb? flattened = null)
        => new(true, flattened ?? ColorRgb.Black, rule, null, hatch);

    /// <summary>Convenience: an invisible fill ("none").</summary>
    public static FillSpec None { get; } = new(false, ColorRgb.White, FillRule.NonZero);

    /// <summary>Convenience: a solid visible fill.</summary>
    public static FillSpec Solid(ColorRgb color, FillRule rule = FillRule.NonZero)
        => new(true, color, rule);

    /// <summary>
    /// A gradient fill. <paramref name="flattened"/> is the colour used wherever the gradient
    /// cannot be painted - a thumbnail, a flattened export, a viewer with no shading support -
    /// and defaults to the gradient's own midpoint so that a gradient with no explicit fallback
    /// still previews as something recognisable rather than white.
    /// </summary>
    public static FillSpec WithGradient(
        GradientSpec gradient,
        FillRule rule = FillRule.NonZero,
        ColorRgb? flattened = null)
        => new(true, flattened ?? gradient.Sample(0.5).Color, rule, gradient);

    /// <summary>True when this fill carries a gradient.</summary>
    public bool HasGradient => Gradient is not null;
}

/// <summary>Which side of the path outline the stroke is drawn on (Illustrator's
/// Align Stroke). PDF has no native notion of this, so the exporter/renderer
/// realise Inside/Outside by clipping.</summary>
public enum StrokeAlignment
{
    /// <summary>Stroke straddles the outline (default).</summary>
    Center,

    /// <summary>Stroke lies inside the filled region.</summary>
    Inside,

    /// <summary>Stroke lies outside the filled region.</summary>
    Outside,
}

/// <summary>
/// Immutable dash pattern: alternating on/off lengths (in the same units as the
/// stroke width) plus a phase offset. An empty pattern means a solid line. Value
/// equality is sequence-based so style comparisons behave as expected.
/// </summary>
public readonly struct DashPattern : IEquatable<DashPattern>
{
    private readonly double[]? _segments;

    public DashPattern(IReadOnlyList<double> segments, double offset = 0.0)
    {
        _segments = segments is { Count: > 0 } ? segments.ToArray() : null;
        Offset = offset;
    }

    /// <summary>Alternating on/off lengths, or empty for a solid line.</summary>
    public IReadOnlyList<double> Segments => _segments ?? Array.Empty<double>();

    /// <summary>Distance into the pattern at which the stroke starts.</summary>
    public double Offset { get; }

    /// <summary>True when this is a solid (non-dashed) line.</summary>
    public bool IsEmpty => _segments is null || _segments.Length == 0;

    /// <summary>Convenience: a solid line.</summary>
    public static DashPattern None => default;

    public bool Equals(DashPattern other)
    {
        if (!Offset.Equals(other.Offset))
        {
            return false;
        }

        IReadOnlyList<double> a = Segments;
        IReadOnlyList<double> b = other.Segments;
        if (a.Count != b.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Count; i++)
        {
            if (!a[i].Equals(b[i]))
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj) => obj is DashPattern other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Offset);
        foreach (double s in Segments)
        {
            hash.Add(s);
        }

        return hash.ToHashCode();
    }
}

/// <summary>
/// Immutable stroke specification for a path: visibility, colour, geometric width
/// (in points, unscaled by any group transform), end caps, joins, miter limit,
/// dash pattern, alignment (centre/inside/outside) and an optional width profile or brush.
///
/// A [WidthProfile] makes the stroke's width vary along the path, and can vary it differently on each side.
/// It is added rather than replacing [Width]: a stroke with a profile still has a width, which the profile
/// **replaces where it says something**, so a profile is a modulation of an ordinary stroke rather than a
/// separate kind of thing. That is what makes the two compositional - clearing a profile leaves a stroke
/// exactly as it was, and setting one on a stroke that has no width still draws nothing.
///
/// A [Brush] is the **nib the stroke is swept with**, which is a different question from how wide it is:
/// the width a nib lays down depends on the direction of travel, which a width profile cannot express. A
/// brush therefore replaces the width rather than modulating it, and is the last member so that a stroke
/// written before brushes existed is byte-identical to one written now.
///
/// [Opacity] and [Blend] are the **paint** members, and they are per stroke rather than per path: they are what
/// lets one path be a thin black line under a broad translucent highlight. Both are nullable and last, so a stroke
/// that states neither - which is every stroke written before this - serialises to exactly the bytes it did then.
/// </summary>
public sealed record StrokeSpec(
    bool IsVisible,
    ColorRgb Color,
    double Width,
    StrokeCap Cap,
    StrokeJoin Join,
    double MiterLimit,
    StrokeAlignment Alignment = StrokeAlignment.Center,
    DashPattern Dash = default,
    WidthProfileSpec? WidthProfile = null,
    EffectStack? Effects = null,
    RasterEffectStack? RasterEffects = null,
    DynamicsSpec? Dynamics = null,
    BrushSpec? Brush = null,
    double? Opacity = null,
    BlendMode? Blend = null)
{
    /// <summary>Convenience: no visible stroke.</summary>
    public static StrokeSpec None { get; } =
        new(false, ColorRgb.Black, 1.0, StrokeCap.Butt, StrokeJoin.Miter, 4.0);

    /// <summary>A 1pt solid stroke, the classic vector-editor default.</summary>
    public static StrokeSpec Hairline(ColorRgb color)
        => new(true, color, 1.0, StrokeCap.Butt, StrokeJoin.Miter, 4.0);

    /// <summary>
    /// Whether the stroke varies in width along the path.
    ///
    /// A profile with no points is not one: it says nothing about width, so the stroke is an ordinary one and
    /// the renderers take the plain path. Without that, an empty profile would send every stroke down the
    /// outline route to draw exactly what it drew before.
    /// </summary>
    public bool HasWidthProfile => WidthProfile is { IsEmpty: false };

    /// <summary>Whether any outline effect reshapes this stroke.</summary>
    public bool HasEffects => Effects is { Count: > 0 };

    /// <summary>The effects in order, or an empty list when there are none.</summary>
    public IReadOnlyList<OutlineEffectSpec> AllEffects => Effects ?? EffectStack.Empty;

    /// <summary>Whether any raster effect changes the pixels this stroke is drawn as.</summary>
    public bool HasRasterEffects => RasterEffects is { Count: > 0 };

    /// <summary>The raster effects in order, or an empty list when there are none.</summary>
    public IReadOnlyList<RasterEffectSpec> AllRasterEffects => RasterEffects ?? RasterEffectStack.Empty;

    /// <summary>
    /// Whether this stroke responds to the pen.
    ///
    /// This is what was **recorded** about how the stroke was drawn, not what it looks like: what it looks like is
    /// the width profile the pressure produced, which is stored beside it. Keeping both is what lets a drawing made
    /// with a tablet be exported as geometry and still say how it was made.
    /// </summary>
    public bool HasDynamics => Dynamics is { IsEmpty: false };

    /// <summary>
    /// Whether the stroke is swept with a brush.
    ///
    /// A brush that names no kind of nib still has one - the calligraphic nib is a real answer with a
    /// diameter and a roundness - so unlike an empty width profile there is no "says nothing" state to
    /// filter out here. The absence of a brush is the null member, which is the default.
    /// </summary>
    public bool HasBrush => Brush is not null;

    /// <summary>
    /// How much of this stroke is drawn, or **null when the stroke states no opacity at all**.
    ///
    /// Null rather than 1.0, and the difference is not pedantry: the model already distinguishes "stores nothing"
    /// from "stores a decision" for tablet dynamics, and this is the same distinction one level down. A stroke
    /// that states nothing is written without the member - so an ordinary document is byte-identical to what this
    /// build wrote before per-stroke opacity existed - while a stroke whose opacity is stated as 1.0 is a decision
    /// somebody made on that stroke of the stack, and it is written and read back as one.
    ///
    /// It is separate from the **alpha of the colour**, which is the paint's own transparency. Both apply, and they
    /// multiply: a stroke whose colour is half-transparent and whose opacity is 0.5 is drawn at a quarter.
    /// </summary>
    public double? Opacity { get; init; } = Opacity;

    /// <summary>
    /// How this stroke's colour combines with what is already drawn beneath it, or **null when it states none**,
    /// for the reason <see cref="Opacity"/> gives.
    ///
    /// Per **stroke** rather than per path, which is what the appearance stack needs: the highlight is multiplied
    /// over the line beneath it while the line itself is drawn normally, and a blend mode on the path cannot say
    /// that. Null is not the same as <see cref="BlendMode.Normal"/> written explicitly.
    /// </summary>
    public BlendMode? Blend { get; init; } = Blend;

    /// <summary>
    /// The opacity this stroke is actually drawn at: the member when it states one, and fully opaque when it does
    /// not. Callers that paint or export should use this rather than reading <see cref="Opacity"/> and deciding
    /// for themselves what the absence means.
    /// </summary>
    public double EffectiveOpacity => Opacity ?? 1.0;

    /// <summary>Whether this stroke states an opacity, as opposed to leaving it unstated.</summary>
    public bool HasOpacity => Opacity is not null;

    /// <summary>Whether this stroke states a blend mode, as opposed to leaving it unstated.</summary>
    public bool HasBlend => Blend is not null;

    /// <summary>True when a stroke is visible and has positive width.</summary>
    public bool HasVisibleOutline => IsVisible && Width > 0.0;
}
