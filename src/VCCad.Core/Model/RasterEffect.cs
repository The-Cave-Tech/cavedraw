namespace VCCad.Core.Model;

/// <summary>What a raster effect does to the pixels a stroke was drawn as.</summary>
public enum RasterEffectKind
{
    /// <summary>Blur the stroke.</summary>
    Blur,

    /// <summary>Blur a copy of the stroke, tint it, and draw it behind and offset from the original.</summary>
    DropShadow,

    /// <summary>Blur the stroke's colour inward from its own edge.</summary>
    InnerGlow,

    /// <summary>Draw a blurred, tinted copy behind the stroke, spreading outward from its edge.</summary>
    OuterGlow,
}

/// <summary>
/// One raster effect: what it is, and the parameters that apply to it.
///
/// These are the effects that **cannot** be geometry, which is why they are a separate list from
/// <see cref="OutlineEffectSpec"/> rather than more kinds in the same one. A roughen moves a point; a blur has no
/// points to move - it changes which pixels the stroke covers, and every consequence follows from that: it does
/// not scale with zoom, it cannot be exported as vector geometry, and a renderer needs a pixel buffer to do it.
///
/// Same shape as the outline effects, and for the same reason: a single record with a kind rather than a type per
/// effect, because the alternative is a discriminated union hand-written for JSON, for the sidecar and for every
/// reader.
/// </summary>
public sealed record RasterEffectSpec(
    RasterEffectKind Kind,
    double Radius = 4.0,
    double OffsetX = 0.0,
    double OffsetY = 0.0,
    double Opacity = 1.0,
    ColorRgb? Tint = null)
{
    /// <summary>How far the effect spreads, in points.</summary>
    public double Radius { get; init; } = Radius;

    /// <summary>Drop shadow only: how far the shadow is displaced.</summary>
    public double OffsetX { get; init; } = OffsetX;

    /// <summary>Drop shadow only: how far the shadow is displaced.</summary>
    public double OffsetY { get; init; } = OffsetY;

    /// <summary>The effect's own opacity, which is what makes a glow subtle.</summary>
    public double Opacity { get; init; } = Opacity;

    /// <summary>
    /// The effect's colour, or null to use the stroke's own.
    ///
    /// Null by default rather than black, because "a shadow the colour of the line it falls from" is the common
    /// case and black would be a silent choice - the same distinction the fill's gradient fallback makes.
    /// </summary>
    public ColorRgb? Tint { get; init; } = Tint;

    public static RasterEffectSpec Blur(double radius)
        => new(RasterEffectKind.Blur, radius);

    public static RasterEffectSpec DropShadow(double radius, double offsetX, double offsetY, ColorRgb? tint = null)
        => new(RasterEffectKind.DropShadow, radius, offsetX, offsetY, 1.0, tint);

    public static RasterEffectSpec Glow(RasterEffectKind kind, double radius, ColorRgb tint)
        => new(kind, radius, 0, 0, 1.0, tint);
}

/// <summary>
/// An ordered list of raster effects, compared **by value** - the same reasoning as <see cref="EffectStack"/>: a
/// record or a bare array would compare by reference and two identical strokes would never be equal.
/// </summary>
public sealed class RasterEffectStack : IReadOnlyList<RasterEffectSpec>
{
    private readonly RasterEffectSpec[] _effects;

    public RasterEffectStack(IEnumerable<RasterEffectSpec> effects) => _effects = effects.ToArray();

    public static RasterEffectStack Empty { get; } = new(Array.Empty<RasterEffectSpec>());

    public RasterEffectSpec this[int index] => _effects[index];

    public int Count => _effects.Length;

    public IEnumerator<RasterEffectSpec> GetEnumerator()
        => ((IEnumerable<RasterEffectSpec>)_effects).GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => _effects.GetEnumerator();

    public bool Equals(RasterEffectStack? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (_effects.Length != other._effects.Length)
        {
            return false;
        }

        for (int i = 0; i < _effects.Length; i++)
        {
            if (_effects[i] != other._effects[i])
            {
                return false;
            }
        }

        return true;
    }

    public override bool Equals(object? obj) => Equals(obj as RasterEffectStack);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (RasterEffectSpec effect in _effects)
        {
            hash.Add(effect);
        }

        return hash.ToHashCode();
    }
}
