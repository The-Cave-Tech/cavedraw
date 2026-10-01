using VCCad.Core.Model;
using VCCad.Core.Raster;

namespace VCCad.Core.Model;

/// <summary>
/// Turns a stroke's **raster effects** into a filter graph, so they render.
///
/// A raster effect is a blur, a drop shadow or a glow, and each of those is a short graph of the primitives the
/// filter engine already evaluates - a blur over the source's alpha, an offset, a flood for the colour, a composite
/// to put the result behind or inside the artwork. Writing them as graphs rather than as new pixel code means the
/// effects are rendered by **the engine that is already tested at known pixels**, and it is the same engine the
/// canvas and the SVG filters use.
///
/// The stroke's own colour is passed in because a shadow with no tint is "the colour of the line it falls from" -
/// the model's documented meaning - and a graph cannot ask the caller what that was.
/// </summary>
public static class RasterEffectFilters
{
    /// <summary>Whether any effect here needs a filter at all - a no-op list renders as the artwork.</summary>
    public static bool Any(IEnumerable<RasterEffectSpec>? effects)
        => effects is not null && effects.Any();

    /// <summary>
    /// The graph for one effect, or null when it needs none.
    ///
    /// Each graph takes `SourceGraphic` and produces the artwork **with** the effect, so a list of them chains:
    /// each one's output is the next one's source. That is what makes the order the model keeps meaningful.
    /// </summary>
    public static FilterSpec? ToFilter(RasterEffectSpec effect, ColorRgb strokeColour)
    {
        ColorRgb colour = effect.Tint ?? strokeColour;
        string kind = effect.Kind.ToString();

        return effect.Kind switch
        {
            RasterEffectKind.Blur => new FilterSpec($"raster{kind}", new[]
            {
                FilterPrimitive.Blur(effect.Radius, input: "SourceGraphic"),
            })
            {
                X = -effect.Radius,
                Y = -effect.Radius,
                Width = 1,
                Height = 1,
                ObjectBoundingBox = false,
            },

            // A shadow is the artwork's alpha, blurred, moved, filled with the colour, and put **behind** the
            // artwork - which is `over`, the operator that draws the first input on top of the second.
            RasterEffectKind.DropShadow => new FilterSpec($"raster{kind}", new[]
            {
                FilterPrimitive.Blur(effect.Radius, input: "SourceAlpha", result: "soft"),
                FilterPrimitive.OffsetBy(effect.OffsetX, effect.OffsetY, input: "soft", result: "moved"),
                FilterPrimitive.Solid(colour, effect.Opacity, "ink"),
                FilterPrimitive.Combine("in", "ink", "moved", "shadow"),
                FilterPrimitive.Combine("over", "SourceGraphic", "shadow"),
            })
            {
                X = -effect.Radius,
                Y = -effect.Radius,
                Width = 1,
                Height = 1,
                ObjectBoundingBox = false,
            },

            // An outer glow is the same shape without the displacement; an inner glow keeps only the part of it
            // that falls inside the artwork, which is what `in` against the artwork's own alpha means.
            RasterEffectKind.OuterGlow => new FilterSpec($"raster{kind}", new[]
            {
                FilterPrimitive.Blur(effect.Radius, input: "SourceAlpha", result: "soft"),
                FilterPrimitive.Solid(colour, effect.Opacity, "ink"),
                FilterPrimitive.Combine("in", "ink", "soft", "glow"),
                FilterPrimitive.Combine("over", "SourceGraphic", "glow"),
            })
            {
                X = -effect.Radius,
                Y = -effect.Radius,
                Width = 1,
                Height = 1,
                ObjectBoundingBox = false,
            },

            RasterEffectKind.InnerGlow => new FilterSpec($"raster{kind}", new[]
            {
                FilterPrimitive.Blur(effect.Radius, input: "SourceAlpha", result: "soft"),
                FilterPrimitive.Solid(colour, effect.Opacity, "ink"),
                FilterPrimitive.Combine("in", "ink", "soft", "glow"),
                FilterPrimitive.Combine("in", "glow", "SourceAlpha", "inside"),
                FilterPrimitive.Combine("over", "SourceGraphic", "inside"),
            })
            {
                X = -effect.Radius,
                Y = -effect.Radius,
                Width = 1,
                Height = 1,
                ObjectBoundingBox = false,
            },

            _ => null,
        };
    }

    /// <summary>
    /// The artwork with every effect applied, in the order the stroke keeps them.
    ///
    /// Each effect is evaluated over the previous one's result, because that is what "the order is the picture"
    /// means - a blur then a shadow is not a shadow then a blur. The buffer is used as it is, since the caller has
    /// already placed the artwork in the region it wants rendered.
    /// </summary>
    public static FilterBuffer Apply(FilterBuffer source, IEnumerable<RasterEffectSpec> effects, ColorRgb strokeColour)
    {
        FilterBuffer current = source;

        foreach (RasterEffectSpec effect in effects)
        {
            if (ToFilter(effect, strokeColour) is { } filter)
            {
                current = new FilterEngine(filter).EvaluateInPlace(current);
            }
        }

        return current;
    }
}
