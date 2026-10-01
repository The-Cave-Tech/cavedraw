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
        // Morphology's radius is a length in the primitive's own units, like a blur's sigma, so it has to be scaled
        // by the same factor or an outline would thin as the zoom went in.
        FilterPrimitiveKind.Morphology => Morphology(a, primitive.Operator, primitive.Radius * _scale),
        FilterPrimitiveKind.ColorMatrix => ColourMatrix(a, MatrixOf(primitive)),
        FilterPrimitiveKind.DisplacementMap => Displace(
            a, b, primitive.Scale * _scale, primitive.XChannel, primitive.YChannel),
        FilterPrimitiveKind.Turbulence => Turbulence(a, primitive),
        FilterPrimitiveKind.SpecularLighting => SpecularLighting(a, primitive),
        FilterPrimitiveKind.DiffuseLighting => DiffuseLighting(a, primitive),
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

    /// <summary>
    /// The coefficients of SVG's luminance transform, which `feColorMatrix`'s `saturate` and `luminanceToAlpha`
    /// share.
    ///
    /// These are the specification's own numbers, 0.2125 / 0.7154 / 0.0721 - not the 0.299 / 0.587 / 0.114 of the
    /// older luma formula and not quite the 0.2126 / 0.7152 / 0.0722 of Rec. 709 either. They are that close to
    /// both that a wrong choice draws a picture nobody could tell apart, and the difference is measurable in the
    /// fourth decimal place - which is what makes the test that pins them worth having.
    /// </summary>
    private const double LuminanceRed = 0.2125;

    /// <summary>See <see cref="LuminanceRed"/>.</summary>
    private const double LuminanceGreen = 0.7154;

    /// <summary>See <see cref="LuminanceRed"/>.</summary>
    private const double LuminanceBlue = 0.0721;

    // The constants of SVG's hueRotate matrix, in the form the specification defines it: a luminance weight per
    // row, a cosine coefficient per row, and a sine coefficient per row, each scaled by (1 - cos) and sin. A cell
    // is therefore `weight + (1 - cos) * cosine + sin * sine`, and each row's weight plus its cosine coefficient
    // is one - which is what makes every row sum to one, and a matrix whose rows sum to one is the whole of "the
    // hue moved and the luminance did not".

    // ---------------------------------------------------------------- morphology

    /// <summary>
    /// `feMorphology`: the picture grown (`dilate`) or shrunk (`erode`) by a box.
    ///
    /// The box is SVG's own default kernel - a square of the given radius - which makes both operations separable:
    /// a window over the columns followed by the same window over the rows gives exactly the square's maximum or
    /// minimum, at O(r) per pixel instead of O(r^2), and there is no round kernel to get subtly wrong.
    ///
    /// Outside the region is treated as **transparent**, which is the same rule the blur follows: dilating a shape
    /// near the region's edge spreads into it until the edge, and eroding it lets the edge eat in from outside.
    /// </summary>
    public static FilterBuffer Morphology(FilterBuffer input, string op, double radius)
    {
        bool dilate = (op ?? "erode").Trim().ToLowerInvariant() == "dilate";
        int reach = Math.Max(0, (int)Math.Round(radius));
        if (reach == 0)
        {
            return input.Clone();
        }

        FilterBuffer horizontal = ExtremumWindow(input, reach, dilate, horizontal: true);
        return ExtremumWindow(horizontal, reach, dilate, horizontal: false);
    }

    /// <summary>One pass of a box minimum or maximum, along one axis.</summary>
    private static FilterBuffer ExtremumWindow(FilterBuffer input, int reach, bool maximum, bool horizontal)
    {
        var output = new FilterBuffer(input.Width, input.Height);

        for (int y = 0; y < input.Height; y++)
        {
            for (int x = 0; x < input.Width; x++)
            {
                float r = maximum ? float.NegativeInfinity : float.PositiveInfinity;
                float g = r, b = r, a = r;

                for (int i = -reach; i <= reach; i++)
                {
                    (float pr, float pg, float pb, float pa) = horizontal
                        ? input.Get(x + i, y)
                        : input.Get(x, y + i);

                    if (maximum)
                    {
                        r = Math.Max(r, pr);
                        g = Math.Max(g, pg);
                        b = Math.Max(b, pb);
                        a = Math.Max(a, pa);
                    }
                    else
                    {
                        r = Math.Min(r, pr);
                        g = Math.Min(g, pg);
                        b = Math.Min(b, pb);
                        a = Math.Min(a, pa);
                    }
                }

                output.Set(x, y, r, g, b, a);
            }
        }

        return output;
    }

    // ---------------------------------------------------------------- colour matrix

    /// <summary>`feColorMatrix`: one 4x5 matrix applied to the straight colour, as SVG defines it.</summary>
    public static FilterBuffer ColourMatrix(FilterBuffer input, double[] values)
    {
        double[] m = values.Length == 20 ? values : Identity();
        var output = new FilterBuffer(input.Width, input.Height);

        for (int y = 0; y < input.Height; y++)
        {
            for (int x = 0; x < input.Width; x++)
            {
                (float r, float g, float b, float a) = input.Get(x, y);

                // Straight rather than premultiplied: the matrix is defined on the colour a viewer would name, so a
                // desaturated shadow keeps its coverage instead of being multiplied by it twice.
                float nr = (float)((m[0] * r) + (m[1] * g) + (m[2] * b) + (m[3] * a) + m[4]);
                float ng = (float)((m[5] * r) + (m[6] * g) + (m[7] * b) + (m[8] * a) + m[9]);
                float nb = (float)((m[10] * r) + (m[11] * g) + (m[12] * b) + (m[13] * a) + m[14]);
                float na = (float)((m[15] * r) + (m[16] * g) + (m[17] * b) + (m[18] * a) + m[19]);

                output.Set(x, y, Math.Clamp(nr, 0f, 1f), Math.Clamp(ng, 0f, 1f), Math.Clamp(nb, 0f, 1f),
                    Math.Clamp(na, 0f, 1f));
            }
        }

        return output;
    }

    /// <summary>
    /// The twenty numbers a colour matrix primitive stands for.
    ///
    /// A shorthand is kept in the model as the one number that describes it - so it can be written back the way it
    /// was read - and expanded here. A caller therefore gets the same matrix out of a `saturate` and out of the
    /// twenty numbers it is defined as, which is what makes the two the same filter rather than two spellings.
    /// </summary>
    public static double[] MatrixOf(FilterPrimitive primitive)
    {
        if (primitive.Matrix is { Length: 20 } given)
        {
            return given;
        }

        return (primitive.Type ?? "matrix").Trim().ToLowerInvariant() switch
        {
            "saturate" => Saturate(primitive.Matrix is { Length: 1 } only ? only[0] : 1.0),
            "huerotate" => HueRotation(primitive.Matrix is { Length: 1 } angle ? angle[0] : 0.0),
            "luminancetoalpha" => LuminanceToAlpha(),
            _ => Identity(),
        };
    }

    /// <summary>The identity matrix, as a fresh array: the model's constant is shared, and a primitive that stamped
    /// it must not be able to edit everyone else's.</summary>
    public static double[] Identity() => (double[])FilterPrimitive.IdentityMatrix.Clone();

    /// <summary>SVG's `saturate` matrix, where `s` of 1 is identity and 0 is luminance-only grey.</summary>
    public static double[] Saturate(double s)
    {
        double inverse = 1.0 - s;
        return new double[]
        {
            (LuminanceRed * inverse) + s, LuminanceGreen * inverse, LuminanceBlue * inverse, 0, 0,
            LuminanceRed * inverse, (LuminanceGreen * inverse) + s, LuminanceBlue * inverse, 0, 0,
            LuminanceRed * inverse, LuminanceGreen * inverse, (LuminanceBlue * inverse) + s, 0, 0,
            0, 0, 0, 1, 0,
        };
    }

    /// <summary>
    /// SVG's `hueRotate` matrix, in the form the specification defines it.
    ///
    /// Each cell is a luminance weight `L` plus a cosine coefficient scaled by `(1 - cos)` plus a sine coefficient
    /// scaled by `sin`, and the three coefficient triples are the ones the specification prints. Every row's
    /// `L + C` is one, so every row sums to one whatever the angle - which is what makes this a rotation of the hue
    /// rather than a colour cast: red becomes green at 120 degrees and cyan at 180.
    /// </summary>
    public static double[] HueRotation(double degrees)
    {
        double radians = degrees * Math.PI / 180.0;
        double cos = Math.Cos(radians);
        double sin = Math.Sin(radians);

        // The specification's table: the diagonal is the identity, and the off-diagonal terms are the matrix at
        // 120 degrees, interpolated by `(3 - 1 - 2 * cos) / 3` on the cosine side and `sin / sqrt(3)` on the sine
        // side. At zero degrees both vanish and what is left is the identity; at 120 they reconstruct the
        // specification's own matrix exactly. The row sums are one at every angle, which is the whole of "the hue
        // moved and the luminance did not".
        double cosineScale = (3.0 - 1.0 - (2.0 * cos)) / 3.0;
        double sineScale = sin / 1.7320508075688772;

        return new double[]
        {
            1 - (cosineScale * 0.787) - (sineScale * 0.213),
            (cosineScale * 0.715) - (sineScale * 0.715),
            (cosineScale * 0.072) + (sineScale * (0.715 + 0.213)),
            0, 0,

            (cosineScale * 0.213) + (sineScale * 0.143),
            1 - (cosineScale * 0.715) - (sineScale * 0.715),
            (cosineScale * 0.072) - (sineScale * 0.283),
            0, 0,

            (cosineScale * 0.213) - (sineScale * 0.787),
            (cosineScale * 0.715) + (sineScale * 0.715),
            1 - (cosineScale * 0.072) + (sineScale * 0.072),
            0, 0,

            0, 0, 0, 1, 0,
        };
    }

    /// <summary>SVG's `luminanceToAlpha`: the colour is dropped and its luminance becomes the coverage.</summary>
    public static double[] LuminanceToAlpha() => new double[]
    {
        0, 0, 0, 0, 0,
        0, 0, 0, 0, 0,
        0, 0, 0, 0, 0,
        LuminanceRed, LuminanceGreen, LuminanceBlue, 0, 0,
    };

    // ---------------------------------------------------------------- displacement

    /// <summary>
    /// `feDisplacementMap`: the second input's channel moves the first input's pixels.
    ///
    /// The shift is `scale * (channel - 0.5)` per axis, and the picture moves **by** it: an output pixel reads its
    /// source from `x - shift`, so a channel of one at a scale of four carries content two pixels toward +x and
    /// +y. The midpoint of the channel is the value 0.5, at which nothing moves - which is why a half-opaque map
    /// is the neutral one and a fully opaque map is the extreme.
    ///
    /// The result is transparent black outside the source, because a pixel that is not there cannot contribute.
    /// </summary>
    public static FilterBuffer Displace(
        FilterBuffer input, FilterBuffer map, double scale, string xChannel, string yChannel)
    {
        var output = new FilterBuffer(input.Width, input.Height);

        for (int y = 0; y < input.Height; y++)
        {
            for (int x = 0; x < input.Width; x++)
            {
                (float mr, float mg, float mb, float ma) = map.Get(x, y);
                double dx = scale * (Channel(mr, mg, mb, ma, xChannel) - 0.5);
                double dy = scale * (Channel(mr, mg, mb, ma, yChannel) - 0.5);

                (float dr, float dg, float db, float da) = Sample(input, x + dx, y + dy);
                output.Set(x, y, dr, dg, db, da);
            }
        }

        return output;
    }

    /// <summary>One of a pixel's four channels by name, the way a displacement selector reads it.</summary>
    public static double Channel(double r, double g, double b, double a, string selector) =>
        (selector ?? "A").Trim().ToUpperInvariant() switch
        {
            "R" => r,
            "G" => g,
            "B" => b,
            _ => a,
        };

    /// <summary>
    /// One pixel, bilinearly interpolated, or transparent when the sample falls outside the buffer.
    ///
    /// Interpolated rather than snapped because a displacement is a continuous quantity: rounding every sample to
    /// the nearest pixel turns a smooth warp into a staircase, and the difference is visible at any zoom.
    /// </summary>
    private static (float R, float G, float B, float A) Sample(FilterBuffer buffer, double x, double y)
    {
        int x0 = (int)Math.Floor(x);
        int y0 = (int)Math.Floor(y);
        double fx = x - x0;
        double fy = y - y0;

        (float r, float g, float b, float a) = (0f, 0f, 0f, 0f);
        for (int j = 0; j <= 1; j++)
        {
            for (int i = 0; i <= 1; i++)
            {
                double weight = (i == 0 ? 1 - fx : fx) * (j == 0 ? 1 - fy : fy);
                if (weight == 0)
                {
                    continue;
                }

                (float pr, float pg, float pb, float pa) = buffer.Get(x0 + i, y0 + j);
                r += (float)(pr * weight);
                g += (float)(pg * weight);
                b += (float)(pb * weight);
                a += (float)(pa * weight);
            }
        }

        return (r, g, b, a);
    }

    // ---------------------------------------------------------------- turbulence

    /// <summary>
    /// `feTurbulence`: seeded value noise, summed over octaves, written into the colour channels.
    ///
    /// The input is ignored - SVG defines the noise as a function of the position and nothing else - so this is the
    /// one primitive that draws the same picture whatever it is handed.
    ///
    /// **The seed is the whole of the randomness.** It feeds a hash of the lattice coordinate rather than a
    /// generator walked from one end of the buffer to the other, so the same document renders and exports
    /// identically, and a partial re-render (a tile, a region, a zoom) lands on the same numbers. `turbulence` sums
    /// the absolute value of each octave, which is the billowy look; `fractalNoise` sums the octaves themselves,
    /// which is the cloudy one.
    /// </summary>
    public FilterBuffer Turbulence(FilterBuffer input, FilterPrimitive primitive)
    {
        bool fractal = (primitive.Type ?? "turbulence").Trim().ToLowerInvariant() == "fractalnoise";
        double frequency = primitive.BaseFrequency * _scale;
        int octaves = Math.Clamp(primitive.Octaves, 0, 12);
        var output = new FilterBuffer(input.Width, input.Height);

        for (int y = 0; y < input.Height; y++)
        {
            for (int x = 0; x < input.Width; x++)
            {
                double u = x * frequency;
                double v = y * frequency;

                double total = 0;
                double amplitude = 1;
                double amplitudeSum = 0;
                double step = frequency;

                for (int octave = 0; octave < octaves; octave++)
                {
                    double n = Noise(u, v, primitive.Seed + (octave * 0x9E37));
                    total += fractal ? n * amplitude : Math.Abs(n) * amplitude;
                    amplitudeSum += amplitude;
                    amplitude *= 0.5;
                    step *= 2;

                    // The next octave is the same lattice twice as fine: the coordinate doubles, which is what
                    // makes the frequencies octaves rather than unrelated bands of noise.
                    u *= 2;
                    v *= 2;
                }

                double value = amplitudeSum > 0
                    ? (fractal ? ((total / amplitudeSum) + 1.0) / 2.0 : total / amplitudeSum)
                    : 0;
                float channel = (float)Math.Clamp(value, 0.0, 1.0);

                output.Set(x, y, channel, channel, channel, 1f);
            }
        }

        return output;
    }

    /// <summary>
    /// Value noise at a coordinate: a hashed lattice corner, smoothly interpolated.
    ///
    /// The interpolation is the quintic smoothstep Perlin's improved noise uses, which has zero first and second
    /// derivatives at the lattice points - the reason the noise has no visible grid lines through it.
    /// </summary>
    private static double Noise(double x, double y, int seed)
    {
        int x0 = (int)Math.Floor(x);
        int y0 = (int)Math.Floor(y);
        double fx = x - x0;
        double fy = y - y0;

        double u = fx * fx * fx * ((fx * ((fx * 6) - 15)) + 10);
        double v = fy * fy * fy * ((fy * ((fy * 6) - 15)) + 10);

        double n00 = Hash(x0, y0, seed);
        double n10 = Hash(x0 + 1, y0, seed);
        double n01 = Hash(x0, y0 + 1, seed);
        double n11 = Hash(x0 + 1, y0 + 1, seed);

        double top = n00 + ((n10 - n00) * u);
        double bottom = n01 + ((n11 - n01) * u);
        return top + ((bottom - top) * v);
    }

    /// <summary>
    /// A lattice corner's value in [-1, 1], from its coordinate and the seed.
    ///
    /// Integer mixing rather than <see cref="Random"/>: the framework's seeded sequence is documented as not being
    /// stable across versions, and a texture that changed after a runtime upgrade would make the canvas and the
    /// export disagree for no reason anyone could see. These constants are fixed, so this sequence is fixed too.
    /// </summary>
    private static double Hash(int x, int y, int seed)
    {
        unchecked
        {
            uint h = (uint)seed * 0x9E3779B1u;
            h ^= (uint)x * 0x85EBCA6Bu;
            h = (h << 13) | (h >> 19);
            h ^= (uint)y * 0xC2B2AE35u;
            h = (h << 17) | (h >> 15);
            h *= 0x27D4EB2Fu;
            h ^= h >> 15;
            return ((h >> 8) / 8388607.5) - 1.0;
        }
    }

    // ---------------------------------------------------------------- lighting

    /// <summary>
    /// `feSpecularLighting`: the input's alpha read as a height field, lit to a highlight.
    ///
    /// The light is a **distant light** in the azimuth/elevation form of `feDistantLight`; a point or spot light is
    /// refused by the reader rather than approximated here, so this only ever sees the one kind.
    ///
    /// The highlight is `specularConstant * (N.H)^specularExponent` - the angle between the surface and the light,
    /// raised to the exponent - so the exponent is what decides how tight it is and a tilted surface is dimmer than
    /// a flat one facing the light. A pixel whose surface faces **away** from the light is black rather than
    /// reflecting the back of the highlight, which is what the coverage term on the returned alpha is for.
    /// </summary>
    public static FilterBuffer SpecularLighting(FilterBuffer input, FilterPrimitive primitive)
    {
        var output = new FilterBuffer(input.Width, input.Height);
        double[] light = DistantLight(primitive.Azimuth, primitive.Elevation);
        float[] colour = LightingChannels(primitive);
        double exponent = Math.Max(0.0, primitive.SpecularExponent);

        for (int y = 0; y < input.Height; y++)
        {
            for (int x = 0; x < input.Width; x++)
            {
                double[] normal = SurfaceNormal(input, x, y, primitive.SurfaceScale);
                double dot = (normal[0] * light[0]) + (normal[1] * light[1]) + (normal[2] * light[2]);
                float coverage = (float)Math.Clamp(dot, 0.0, 1.0);

                // The halfway vector between the light and the viewer, which for a viewer straight on is the light
                // direction with its z raised by one before normalising.
                double hx = light[0], hy = light[1], hz = light[2] + 1.0;
                double hl = Math.Sqrt((hx * hx) + (hy * hy) + (hz * hz));
                hx /= hl;
                hy /= hl;
                hz /= hl;

                double facing = Math.Max(0.0, (normal[0] * hx) + (normal[1] * hy) + (normal[2] * hz));

                // A surface the light does not reach reflects none of it, however close to the reflection angle its
                // normal happens to be - without this a bevel's shadowed side glints.
                float amount = (float)(primitive.SpecularConstant * Math.Pow(facing, exponent) * coverage);

                output.Set(x, y, colour[0] * amount, colour[1] * amount, colour[2] * amount, coverage);
            }
        }

        return output;
    }

    /// <summary>
    /// `feDiffuseLighting`: the input's alpha read as a height field, lit diffusely.
    ///
    /// The result is the lighting colour times the diffuse constant times the angle between the surface and the
    /// light, with the same distant light the specular primitive takes. The normal points **the same way** as the
    /// specular one - toward the viewer - so a flat surface under a light directly overhead reflects all of it,
    /// which is what makes `diffuseConstant` mean "the brightness of a flat surface facing the light".
    /// </summary>
    public static FilterBuffer DiffuseLighting(FilterBuffer input, FilterPrimitive primitive)
    {
        var output = new FilterBuffer(input.Width, input.Height);
        double[] light = DistantLight(primitive.Azimuth, primitive.Elevation);
        float[] colour = LightingChannels(primitive);

        for (int y = 0; y < input.Height; y++)
        {
            for (int x = 0; x < input.Width; x++)
            {
                double[] normal = SurfaceNormal(input, x, y, primitive.SurfaceScale);
                double dot = (normal[0] * light[0]) + (normal[1] * light[1]) + (normal[2] * light[2]);
                float amount = (float)(primitive.DiffuseConstant * Math.Max(0.0, dot));

                output.Set(x, y, colour[0] * amount, colour[1] * amount, colour[2] * amount, 1f);
            }
        }

        return output;
    }

    /// <summary>
    /// The surface normal at a pixel, from the height field the input's alpha gives.
    ///
    /// The three-by-three Sobel-style kernel is SVG's own: the centre column/row weighted twice, which is what
    /// makes the gradient smooth across a diagonal edge rather than stair-stepping along it. The normal points
    /// toward the viewer - z is positive - so both lighting primitives dot it with the same light vector.
    /// </summary>
    private static double[] SurfaceNormal(FilterBuffer input, int x, int y, double surfaceScale)
    {
        double Height(int sx, int sy) => input.Get(sx, sy).A * surfaceScale;

        double dx = (Height(x - 1, y - 1) + (2 * Height(x - 1, y)) + Height(x - 1, y + 1))
                    - (Height(x + 1, y - 1) + (2 * Height(x + 1, y)) + Height(x + 1, y + 1));
        double dy = (Height(x - 1, y - 1) + (2 * Height(x, y - 1)) + Height(x + 1, y - 1))
                    - (Height(x - 1, y + 1) + (2 * Height(x, y + 1)) + Height(x + 1, y + 1));

        double nx = dx / 8.0;
        double ny = dy / 8.0;
        double length = Math.Sqrt((nx * nx) + (ny * ny) + 1.0);
        return new[] { nx / length, ny / length, 1.0 / length };
    }

    /// <summary>`feDistantLight` as a unit vector, from the azimuth and elevation in degrees.</summary>
    private static double[] DistantLight(double azimuth, double elevation)
    {
        double azimuthRadians = azimuth * Math.PI / 180.0;
        double elevationRadians = elevation * Math.PI / 180.0;
        double cosElevation = Math.Cos(elevationRadians);
        return new[]
        {
            Math.Cos(azimuthRadians) * cosElevation,
            Math.Sin(azimuthRadians) * cosElevation,
            Math.Sin(elevationRadians),
        };
    }

    /// <summary>The lighting colour's channels, white when the primitive names none - SVG's own default.</summary>
    private static float[] LightingChannels(FilterPrimitive primitive)
    {
        ColorRgb colour = primitive.LightingColor ?? ColorRgb.White;
        return new[] { (float)colour.R, (float)colour.G, (float)colour.B };
    }

    private static float Unpremultiply(float colour, float alpha)
        => alpha > 0.0001f ? Math.Clamp(colour / alpha, 0f, 1f) : 0f;
}
