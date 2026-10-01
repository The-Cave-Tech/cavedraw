using VCCad.Core.Model;
using VCCad.Geometry;

namespace VCCad.Core.Raster;

/// <summary>
/// Evaluates a filter graph onto a raster buffer.
///
/// **A filter is a directed graph, and this walks it as one.** Each primitive names the buffers it reads (`in`,
/// `in2`) and the buffer it produces (`result`), so a result can feed two consumers and an intermediate step can be
/// the filter's answer. Each named buffer is evaluated **once** and cached, which is both what makes the sharing
/// cheap and what makes a cycle impossible to run away with - a graph that refers back to itself is refused rather
/// than recursed into.
///
/// **The region is the canvas.** `x`, `y`, `width` and `height` decide the rectangle the filter is evaluated over,
/// in pixels, and nothing exists outside it: a blur near the edge of the region spreads into it and is then cut off
/// by it. That is why a shadow either reaches into the margin or is sliced - and it is why the region is applied
/// here rather than by the caller, because a filter that ignored it would produce different pictures at different
/// zooms.
///
/// Coordinates are **model units**, and the buffer is at `scale` pixels per unit, so a filter rendered at a zoom
/// blurs and offsets proportionally. The source buffer the caller supplies must cover exactly
/// <c>sourceBounds</c>; the engine places it in the region itself.
/// </summary>
public sealed class FilterEngine
{
    private readonly FilterSpec _filter;
    private readonly double _scale;
    private readonly Dictionary<string, FilterBuffer> _results = new(StringComparer.Ordinal);
    private readonly HashSet<string> _running = new(StringComparer.Ordinal);
    private FilterBuffer? _sourceAlpha;

    /// <summary>Creates an engine for one filter.</summary>
    /// <param name="filter">The filter to evaluate.</param>
    /// <param name="scale">Buffer pixels per model unit. A filter evaluated at 2x blurs twice as many pixels.</param>
    public FilterEngine(FilterSpec filter, double scale = 1.0)
    {
        if (scale <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(scale), "scale must be positive");
        }

        _filter = filter;
        _scale = scale;
    }

    /// <summary>The region in pixels that <see cref="Evaluate"/> would allocate for a source of this size.</summary>
    public static (int X, int Y, int Width, int Height) RegionPixels(
        FilterSpec filter, Rect2D sourceBounds, double scale)
    {
        Rect2D region = filter.ObjectBoundingBox
            ? new Rect2D(
                sourceBounds.X + (filter.X * sourceBounds.Width),
                sourceBounds.Y + (filter.Y * sourceBounds.Height),
                filter.Width * sourceBounds.Width,
                filter.Height * sourceBounds.Height)
            : new Rect2D(filter.X, filter.Y, filter.Width, filter.Height);

        int x = (int)Math.Floor(region.X * scale);
        int y = (int)Math.Floor(region.Y * scale);
        int right = (int)Math.Ceiling((region.X + region.Width) * scale);
        int bottom = (int)Math.Ceiling((region.Y + region.Height) * scale);

        // A degenerate region still has to be a buffer, or every filter on a zero-width shape would throw.
        return (x, y, Math.Max(1, right - x), Math.Max(1, bottom - y));
    }

    /// <summary>
    /// Runs the filter over a source buffer and returns the region it produced.
    ///
    /// The source is the *SourceGraphic* input: the shape as it would be drawn, already rasterised over
    /// <paramref name="sourceBounds"/>. Everything else - the region, the offsets between the two coordinate
    /// spaces, and the primitive order - happens here.
    /// </summary>
    public FilterBuffer Evaluate(FilterBuffer source, Rect2D sourceBounds)
    {
        (int regionX, int regionY, int width, int height) = RegionPixels(_filter, sourceBounds, _scale);
        var region = new FilterBuffer(width, height);

        // Where the source lands inside the region. The region may start before the shape (the default ten per cent
        // of margin) or inside it, and the difference is what decides whether a blur has anywhere to spread.
        int offsetX = (int)Math.Round((sourceBounds.X * _scale) - regionX);
        int offsetY = (int)Math.Round((sourceBounds.Y * _scale) - regionY);
        region.Blit(source, offsetX, offsetY);

        _results.Clear();
        _running.Clear();

        FilterBuffer sourceGraphic = region;
        FilterBuffer previous = region;

        foreach (FilterPrimitive primitive in _filter.Primitives)
        {
            FilterBuffer a = Resolve(primitive.Input, sourceGraphic, previous);
            FilterBuffer b = Resolve(primitive.Input2, sourceGraphic, previous);
            FilterBuffer output = Apply(primitive, a, b);

            if (primitive.Result.Length > 0)
            {
                _results[primitive.Result] = output;
            }

            previous = output;
        }

        // The filter's answer is a named result when it says so, and the last primitive otherwise - which is the
        // other half of "a graph rather than a pipeline": the last step computed need not be the one that is drawn.
        if (_filter.Output.Length > 0 && _results.TryGetValue(_filter.Output, out FilterBuffer? named))
        {
            return named;
        }

        return previous;
    }

    /// <summary>
    /// Runs the filter over a buffer that is **already** the region, with the source placed in it.
    ///
    /// The caller that has rendered the source into a region-sized buffer - which is what a canvas does, because it
    /// has to rasterise the shape somewhere - uses this rather than <see cref="Evaluate"/>, which would apply the
    /// region a second time and blur a picture that had already been cropped.
    /// </summary>
    public FilterBuffer EvaluateInPlace(FilterBuffer region)
    {
        _results.Clear();
        _running.Clear();
        _sourceAlpha = null;

        FilterBuffer previous = region;

        foreach (FilterPrimitive primitive in _filter.Primitives)
        {
            FilterBuffer a = Resolve(primitive.Input, region, previous);
            FilterBuffer b = Resolve(primitive.Input2, region, previous);
            FilterBuffer output = Apply(primitive, a, b);

            if (primitive.Result.Length > 0)
            {
                _results[primitive.Result] = output;
            }

            previous = output;
        }

        if (_filter.Output.Length > 0 && _results.TryGetValue(_filter.Output, out FilterBuffer? named))
        {
            return named;
        }

        return previous;
    }

    /// <summary>
    /// The buffer a name refers to.
    ///
    /// An **absent** `in` means the previous primitive's result, which is SVG's rule and the reason a file that
    /// names nothing still works. `SourceGraphic` and `SourceAlpha` are the two inputs every filter starts from;
    /// the other SVG inputs (`BackgroundImage`, `FillPaint`, `StrokePaint`) have no meaning in this model and read
    /// as transparent rather than as an error, so a file that uses them is not refused.
    /// </summary>
    private FilterBuffer Resolve(string? name, FilterBuffer source, FilterBuffer previous)
    {
        if (string.IsNullOrEmpty(name))
        {
            return previous;
        }

        if (name.Equals("SourceGraphic", StringComparison.Ordinal))
        {
            return source;
        }

        if (name.Equals("SourceAlpha", StringComparison.Ordinal))
        {
            return _sourceAlpha ??= source.ToAlpha();
        }

        if (name is "BackgroundImage" or "FillPaint" or "StrokePaint")
        {
            return new FilterBuffer(source.Width, source.Height);
        }

        if (_results.TryGetValue(name, out FilterBuffer? result))
        {
            return result;
        }

        // A result this walk has not reached yet. Primitives are evaluated in order, so this is only a **forward**
        // reference: a step reading a buffer a later step defines. It is evaluated here, with the same implicit
        // input SVG gives a primitive that names none, rather than being refused - a graph is allowed to be written
        // in any order, and the ordering in the file is a starting point rather than a dependency list.
        FilterPrimitive? producer = _filter.ProducerOf(name);
        if (producer is null || !_running.Add(name))
        {
            // A name nothing produces, or a graph that refers back to itself. Either way there is no buffer to hand
            // back, and recursing would not end.
            return new FilterBuffer(source.Width, source.Height);
        }

        try
        {
            FilterBuffer input = Resolve(producer.Input, source, previous);
            FilterBuffer input2 = Resolve(producer.Input2, source, previous);
            return _results[name] = Apply(producer, input, input2);
        }
        finally
        {
            _running.Remove(name);
        }
    }

    /// <summary>One primitive, applied to the buffers it reads.</summary>
    private FilterBuffer Apply(FilterPrimitive primitive, FilterBuffer a, FilterBuffer b) => primitive.Kind switch
    {
        FilterPrimitiveKind.GaussianBlur => Blur(a, primitive.Radius * _scale),
        FilterPrimitiveKind.Offset => OffsetBy(a, primitive.Dx * _scale, primitive.Dy * _scale),
        FilterPrimitiveKind.Flood => Flood(a, primitive),
        FilterPrimitiveKind.Composite => Composite(a, b, primitive.Operator),
        FilterPrimitiveKind.Blend => Blend(a, b, primitive.Mode),
        _ => a.Clone(),
    };

    /// <summary>
    /// A Gaussian blur, by two one-dimensional passes.
    ///
    /// Separable, because a two-dimensional Gaussian is the outer product of two one-dimensional ones - which turns
    /// an O(r^2) kernel per pixel into O(r), and is why a blur this size is affordable at all.
    ///
    /// Pixels outside the region are treated as **transparent**, not as edge-clamped: the region is the filter's
    /// canvas, so a shape blurred near its edge fades out rather than smearing its edge colour sideways.
    /// </summary>
    public static FilterBuffer Blur(FilterBuffer input, double sigma)
    {
        if (sigma <= 0.0001)
        {
            return input.Clone();
        }

        int radius = Math.Max(1, (int)Math.Ceiling(sigma * 3));
        var weights = new double[(radius * 2) + 1];
        double total = 0;
        for (int i = -radius; i <= radius; i++)
        {
            double weight = Math.Exp(-(i * i) / (2 * sigma * sigma));
            weights[i + radius] = weight;
            total += weight;
        }

        for (int i = 0; i < weights.Length; i++)
        {
            weights[i] /= total;
        }

        FilterBuffer horizontal = Convolve(input, weights, radius, horizontal: true);
        return Convolve(horizontal, weights, radius, horizontal: false);
    }

    private static FilterBuffer Convolve(FilterBuffer input, double[] weights, int radius, bool horizontal)
    {
        var output = new FilterBuffer(input.Width, input.Height);

        for (int y = 0; y < input.Height; y++)
        {
            for (int x = 0; x < input.Width; x++)
            {
                float r = 0, g = 0, b = 0, a = 0;
                for (int i = -radius; i <= radius; i++)
                {
                    (float pr, float pg, float pb, float pa) = horizontal
                        ? input.Get(x + i, y)
                        : input.Get(x, y + i);

                    double weight = weights[i + radius];
                    r += (float)(pr * weight);
                    g += (float)(pg * weight);
                    b += (float)(pb * weight);
                    a += (float)(pa * weight);
                }

                output.Set(x, y, r, g, b, a);
            }
        }

        return output;
    }

    /// <summary>`feOffset`: the same picture, moved. Pixels pushed off the region are gone, which is what the region
    /// means.</summary>
    public static FilterBuffer OffsetBy(FilterBuffer input, double dx, double dy)
    {
        var output = new FilterBuffer(input.Width, input.Height);
        output.Blit(input, (int)Math.Round(dx), (int)Math.Round(dy));
        return output;
    }

    /// <summary>`feFlood`: the whole region in one colour and opacity - the raw material of a shadow.</summary>
    public static FilterBuffer Flood(FilterBuffer input, FilterPrimitive primitive)
    {
        var output = new FilterBuffer(input.Width, input.Height);
        output.Fill(primitive.FloodColor ?? ColorRgb.Black, (float)Math.Clamp(primitive.FloodOpacity, 0.0, 1.0));
        return output;
    }

    /// <summary>
    /// `feComposite`: the Porter-Duff operators and the arithmetic one, each per the SVG specification.
    ///
    /// The arithmetic is done **premultiplied**, which is the form the compositing formulas are defined in, and the
    /// result is converted back - so a caller always reads a colour it could draw. Getting this wrong is subtle:
    /// compositing straight colours gives a plausible picture with the wrong coverage at partially transparent
    /// edges, which is exactly where a shadow lives.
    /// </summary>
    public static FilterBuffer Composite(FilterBuffer a, FilterBuffer b, string op)
    {
        var output = new FilterBuffer(a.Width, a.Height);
        string operation = (op ?? "over").Trim().ToLowerInvariant();

        for (int y = 0; y < output.Height; y++)
        {
            for (int x = 0; x < output.Width; x++)
            {
                (float ar, float ag, float ab, float aa) = a.Get(x, y);
                (float br, float bg, float bb, float ba) = b.Get(x, y);

                // Premultiplied, because the formulas below are defined on coverage-weighted colour.
                float car = ar * aa, cag = ag * aa, cab = ab * aa;
                float cbr = br * ba, cbg = bg * ba, cbb = bb * ba;

                float or_, og, ob, oa;
                switch (operation)
                {
                    case "in":
                        or_ = car * ba; og = cag * ba; ob = cab * ba; oa = aa * ba;
                        break;
                    case "out":
                        or_ = car * (1 - ba); og = cag * (1 - ba); ob = cab * (1 - ba); oa = aa * (1 - ba);
                        break;
                    case "atop":
                        or_ = (car * ba) + (cbr * (1 - aa));
                        og = (cag * ba) + (cbg * (1 - aa));
                        ob = (cab * ba) + (cbb * (1 - aa));
                        oa = ba;
                        break;
                    case "xor":
                        or_ = (car * (1 - ba)) + (cbr * (1 - aa));
                        og = (cag * (1 - ba)) + (cbg * (1 - aa));
                        ob = (cab * (1 - ba)) + (cbb * (1 - aa));
                        oa = (aa * (1 - ba)) + (ba * (1 - aa));
                        break;
                    case "arithmetic":
                        // k1 = 1 and k2 = k3 = k4 = 0, which is the "multiply" form. The model does not carry the
                        // four coefficients, so the other three are a documented gap rather than an invented value.
                        or_ = car * cbr; og = cag * cbg; ob = cab * cbb; oa = aa * ba;
                        break;
                    default:
                        or_ = car + (cbr * (1 - aa));
                        og = cag + (cbg * (1 - aa));
                        ob = cab + (cbb * (1 - aa));
                        oa = aa + (ba * (1 - aa));
                        break;
                }

                output.Set(x, y, Unpremultiply(or_, oa), Unpremultiply(og, oa), Unpremultiply(ob, oa),
                    Math.Clamp(oa, 0f, 1f));
            }
        }

        return output;
    }

    /// <summary>
    /// `feBlend`: one of the separable blend modes, then composited source-over.
    ///
    /// `in` is the **source** being blended and `in2` is the **backdrop** it is blended onto - SVG's convention, and
    /// the opposite way round to `feComposite`'s "A over B", which is why the two are spelled out rather than
    /// sharing a helper. The blend function acts on **straight** colour - multiply of two opaque colours is their
    /// product - and the compositing around it is the PDF/SVG formula, which keeps a blend over a transparent
    /// backdrop equal to the source rather than darkening it toward black.
    /// </summary>
    public static FilterBuffer Blend(FilterBuffer source, FilterBuffer backdrop, string mode)
    {
        var output = new FilterBuffer(source.Width, source.Height);
        string blend = (mode ?? "normal").Trim().ToLowerInvariant();

        for (int y = 0; y < output.Height; y++)
        {
            for (int x = 0; x < output.Width; x++)
            {
                (float sr, float sg, float sb, float sa) = source.Get(x, y);
                (float br, float bg, float bb, float ba) = backdrop.Get(x, y);

                float csr = sa > 0 ? sr : 0, csg = sa > 0 ? sg : 0, csb = sa > 0 ? sb : 0;
                float cbr = ba > 0 ? br : 0, cbg = ba > 0 ? bg : 0, cbb = ba > 0 ? bb : 0;

                float dr = BlendChannel(cbr, csr, blend);
                float dg = BlendChannel(cbg, csg, blend);
                float db = BlendChannel(cbb, csb, blend);

                float ao = sa + (ba * (1 - sa));
                float cr = ((1 - ba) * csr * sa) + ((1 - sa) * cbr * ba) + (sa * ba * dr);
                float cg = ((1 - ba) * csg * sa) + ((1 - sa) * cbg * ba) + (sa * ba * dg);
                float cb2 = ((1 - ba) * csb * sa) + ((1 - sa) * cbb * ba) + (sa * ba * db);

                output.Set(x, y, Unpremultiply(cr, ao), Unpremultiply(cg, ao), Unpremultiply(cb2, ao),
                    Math.Clamp(ao, 0f, 1f));
            }
        }

        return output;
    }

    /// <summary>`B(Cb, Cs)` - the separable blend functions SVG 1.1 defines, on straight colour.</summary>
    private static float BlendChannel(float backdrop, float source, string mode) => mode switch
    {
        "multiply" => backdrop * source,
        "screen" => backdrop + source - (backdrop * source),
        "darken" => Math.Min(backdrop, source),
        "lighten" => Math.Max(backdrop, source),
        _ => source,
    };

    private static float Unpremultiply(float colour, float alpha)
        => alpha > 0.0001f ? Math.Clamp(colour / alpha, 0f, 1f) : 0f;
}
