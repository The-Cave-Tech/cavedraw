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
/// dash pattern and alignment (centre/inside/outside).
/// </summary>
public sealed record StrokeSpec(
    bool IsVisible,
    ColorRgb Color,
    double Width,
    StrokeCap Cap,
    StrokeJoin Join,
    double MiterLimit,
    StrokeAlignment Alignment = StrokeAlignment.Center,
    DashPattern Dash = default)
{
    /// <summary>Convenience: no visible stroke.</summary>
    public static StrokeSpec None { get; } =
        new(false, ColorRgb.Black, 1.0, StrokeCap.Butt, StrokeJoin.Miter, 4.0);

    /// <summary>A 1pt solid stroke, the classic vector-editor default.</summary>
    public static StrokeSpec Hairline(ColorRgb color)
        => new(true, color, 1.0, StrokeCap.Butt, StrokeJoin.Miter, 4.0);

    /// <summary>True when a stroke is visible and has positive width.</summary>
    public bool HasVisibleOutline => IsVisible && Width > 0.0;
}
