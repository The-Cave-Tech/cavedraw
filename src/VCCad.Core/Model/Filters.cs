namespace VCCad.Core.Model;

/// <summary>The filter primitives a document can hold.</summary>
public enum FilterPrimitiveKind
{
    /// <summary>`feGaussianBlur` - the one every file uses.</summary>
    GaussianBlur,

    /// <summary>`feOffset` - displacement, usually as a shadow's first step.</summary>
    Offset,

    /// <summary>`feFlood` - a rectangle of one colour and opacity, the raw material of a shadow.</summary>
    Flood,

    /// <summary>`feComposite` - the two-input arithmetic, and the most used primitive in the corpus.</summary>
    Composite,

    /// <summary>`feBlend` - the colour blend modes.</summary>
    Blend,

    /// <summary>`feMorphology` - a shape grown or shrunk by a box, which is how real files thicken an edge.</summary>
    Morphology,

    /// <summary>`feColorMatrix` - a colour transform, most often a desaturation or a tint.</summary>
    ColorMatrix,

    /// <summary>`feDisplacementMap` - a picture pushed around by another picture's channels.</summary>
    DisplacementMap,

    /// <summary>`feTurbulence` (and its alias `fePerlinNoise`) - the seeded noise behind every paper texture.</summary>
    Turbulence,

    /// <summary>`feSpecularLighting` - a height field lit to a highlight, the glossy half of the bevel pair.</summary>
    SpecularLighting,

    /// <summary>`feDiffuseLighting` - a height field lit diffusely, the matte half of the bevel pair.</summary>
    DiffuseLighting,
}

/// <summary>
/// One step of a filter: what it does, what it reads, and what it calls the answer.
///
/// **The wiring is the point.** `<c>in</c>`, `<c>in2</c>` and `<c>result</c>` name buffers rather than positions in
/// a list, because a filter is a **directed graph**: an element can feed two consumers, a consumer can take two
/// inputs, and an intermediate result can be the filter's output. A design that models this as a pipeline is
/// rewritten the first time a file uses one named result twice, and `feComposite`'s 155 uses in Inkscape's corpus
/// guarantee that happens.
///
/// The parameters are carried on one record with a kind rather than a type per primitive, for the same reason the
/// outline effects are: the alternative is a discriminated union hand-written for JSON, for the sidecar and for
/// every reader.
///
/// **The members are a superset, and each kind reads the ones it declares.** A blur reads `Radius`, a colour matrix
/// reads `Matrix`, a lighting primitive reads `SurfaceScale`; nothing reads a member its kind does not declare, and
/// <see cref="FilterPrimitiveRegistry"/> is where "what this kind reads" is written down rather than being inferred
/// from which fields happen to be set.
/// </summary>
public sealed record FilterPrimitive(
    FilterPrimitiveKind Kind,
    string? Input = null,
    string? Input2 = null,
    string Result = "",
    double Radius = 0.0,
    double Dx = 0.0,
    double Dy = 0.0,
    ColorRgb? FloodColor = null,
    double FloodOpacity = 1.0,
    string Operator = "over",
    string Mode = "normal",
    double Scale = 0.0,
    string XChannel = "A",
    string YChannel = "A",
    string Type = "turbulence",
    double BaseFrequency = 0.0,
    int Octaves = 1,
    int Seed = 0,
    double[]? Matrix = null,
    double SurfaceScale = 1.0,
    double SpecularConstant = 1.0,
    double SpecularExponent = 1.0,
    double DiffuseConstant = 1.0,
    ColorRgb? LightingColor = null,
    double Azimuth = 0.0,
    double Elevation = 0.0)
{
    /// <summary>`feGaussianBlur`, which reads one buffer and blurs it.</summary>
    public static FilterPrimitive Blur(double radius, string? input = null, string result = "")
        => new(FilterPrimitiveKind.GaussianBlur, input, null, result, Radius: radius);

    /// <summary>`feOffset`, which moves a buffer.</summary>
    public static FilterPrimitive OffsetBy(double dx, double dy, string? input = null, string result = "")
        => new(FilterPrimitiveKind.Offset, input, null, result, Dx: dx, Dy: dy);

    /// <summary>`feFlood`, which fills the filter region with a colour.</summary>
    public static FilterPrimitive Solid(ColorRgb colour, double opacity = 1.0, string result = "")
        => new(FilterPrimitiveKind.Flood, null, null, result, FloodColor: colour, FloodOpacity: opacity);

    /// <summary>`feComposite`, which combines two buffers by an operator.</summary>
    public static FilterPrimitive Combine(string op, string input, string input2, string result = "")
        => new(FilterPrimitiveKind.Composite, input, input2, result, Operator: op);

    /// <summary>`feBlend`, which combines two buffers by a blend mode.</summary>
    public static FilterPrimitive Blended(string mode, string input, string input2, string result = "")
        => new(FilterPrimitiveKind.Blend, input, input2, result, Mode: mode);

    /// <summary>`feMorphology`, which grows (`dilate`) or shrinks (`erode`) the picture by a box of this radius.</summary>
    public static FilterPrimitive Morph(string op, double radius, string? input = null, string result = "")
        => new(FilterPrimitiveKind.Morphology, input, null, result, Radius: radius, Operator: op);

    /// <summary>
    /// `feColorMatrix`, whose values are the twenty numbers of the matrix in row-major order - including the fifth
    /// column, which is the constant added to each channel.
    ///
    /// The shorthand forms (`saturate`, `hueRotate`, `luminanceToAlpha`) arrive here already expanded into the
    /// matrix they are defined as, so there is one evaluation and one thing to compare: a filter that used the
    /// shorthand and one that spelled the matrix out draw the same pixels, which is exactly what SVG says they are.
    /// </summary>
    public static FilterPrimitive ColourMatrix(
        double[] values, string type = "matrix", string? input = null, string result = "")
        => new(
            FilterPrimitiveKind.ColorMatrix, input, null, result,
            Matrix: (double[])values.Clone(), Type: type);

    /// <summary>`feDisplacementMap`, which displaces the first input by the second input's channel.</summary>
    public static FilterPrimitive Displace(
        double scale, string xChannel, string yChannel, string input, string input2, string result = "")
        => new(
            FilterPrimitiveKind.DisplacementMap, input, input2, result,
            Scale: scale, XChannel: xChannel, YChannel: yChannel);

    /// <summary>`feTurbulence`, whose noise is fixed by its seed rather than by the clock.</summary>
    public static FilterPrimitive Noise(
        string type = "turbulence",
        double baseFrequency = 0.05,
        int octaves = 1,
        int seed = 0,
        string? input = null,
        string result = "")
        => new(
            FilterPrimitiveKind.Turbulence, input, null, result,
            Type: type, BaseFrequency: baseFrequency, Octaves: octaves, Seed: seed);

    /// <summary>`feSpecularLighting`, lit by a distant light.</summary>
    public static FilterPrimitive Specular(
        double surfaceScale,
        double specularConstant,
        double specularExponent,
        ColorRgb lightingColor,
        double azimuth = 0.0,
        double elevation = 0.0,
        string? input = null,
        string result = "")
        => new(
            FilterPrimitiveKind.SpecularLighting, input, null, result,
            SurfaceScale: surfaceScale, SpecularConstant: specularConstant,
            SpecularExponent: specularExponent, LightingColor: lightingColor,
            Azimuth: azimuth, Elevation: elevation);

    /// <summary>`feDiffuseLighting`, lit by a distant light.</summary>
    public static FilterPrimitive Diffuse(
        double surfaceScale,
        double diffuseConstant,
        ColorRgb lightingColor,
        double azimuth = 0.0,
        double elevation = 0.0,
        string? input = null,
        string result = "")
        => new(
            FilterPrimitiveKind.DiffuseLighting, input, null, result,
            SurfaceScale: surfaceScale, DiffuseConstant: diffuseConstant,
            LightingColor: lightingColor, Azimuth: azimuth, Elevation: elevation);

    /// <summary>
    /// The identity colour matrix: the twenty numbers that change nothing.
    ///
    /// Here rather than in the engine because it is a fact about the **format** - a colour matrix with no `values`
    /// leaves its input alone - and both the reader (which needs a matrix when a file gives none) and the writer
    /// (which needs one when a model holds none) have to agree on it.
    /// </summary>
    public static double[] IdentityMatrix { get; } = new double[]
    {
        1, 0, 0, 0, 0,
        0, 1, 0, 0, 0,
        0, 0, 1, 0, 0,
        0, 0, 0, 1, 0,
    };
}

/// <summary>
/// A filter: a named asset holding an ordered list of primitives, held by the document and referred to by the
/// elements it applies to.
///
/// **The order is a starting point, not a pipeline.** It is the order the primitives are evaluated in for the ones
/// that read "the previous result", and the wiring decides what each one actually reads. Keeping both is what lets
/// a file that names its results - which is most of them - be read without rewriting it into a linear chain.
///
/// The **region** is part of the filter rather than decoration: it decides what a blur near an edge does. A blur
/// that grows past the region is clipped, and a region larger than the shape is why a shadow reaches into the
/// margin. Reading it wrong is the first thing that looks wrong in a rendering.
/// </summary>
public sealed record FilterSpec
{
    public FilterSpec(string name, IEnumerable<FilterPrimitive> primitives)
    {
        Name = name;
        Primitives = primitives.ToArray();
    }

    /// <summary>The name this asset is known by, which is what a person picks it by.</summary>
    public string Name { get; init; }

    /// <summary>The primitives, in evaluation order.</summary>
    public IReadOnlyList<FilterPrimitive> Primitives { get; init; }

    /// <summary>
    /// The region the filter is evaluated over, as percentages of the shape's box.
    ///
    /// The defaults are SVG's: ten per cent of margin all round, which is why a blur that spreads further than that
    /// is cut off. Negative values are allowed and mean the region starts inside the shape, which is how a filter
    /// clips its own effect.
    /// </summary>
    public double X { get; init; } = -0.1;

    /// <summary>See <see cref="X"/>.</summary>
    public double Y { get; init; } = -0.1;

    /// <summary>See <see cref="X"/>.</summary>
    public double Width { get; init; } = 1.2;

    /// <summary>See <see cref="X"/>.</summary>
    public double Height { get; init; } = 1.2;

    /// <summary>Whether the region's numbers are fractions of the shape's box, which is SVG's default.</summary>
    public bool ObjectBoundingBox { get; init; } = true;

    /// <summary>
    /// Whether a **primitive's own lengths** are fractions of the shape's box rather than user units.
    ///
    /// This is SVG's `primitiveUnits`, and it is a different question from <see cref="ObjectBoundingBox"/>, which is
    /// about the region the filter is evaluated over: a file may give a user-space region with bounding-box
    /// primitive lengths, or the other way round, and the two are read and written independently for that reason. A
    /// blur of 0.1 means "a tenth of the shape" under one and "0.1 of a user unit" under the other, and the two are
    /// different pictures at every size - which is what makes it worth carrying rather than dropping.
    ///
    /// The default is SVG's: `userSpaceOnUse`.
    /// </summary>
    public bool PrimitiveUnitsObjectBoundingBox { get; init; }

    /// <summary>
    /// The resolution the filter is evaluated at, in pixels across the region, or null when the file gives none.
    ///
    /// This is SVG's `filterRes`. A filter is a raster operation, so the resolution it is sampled at is part of the
    /// picture rather than an implementation detail: evaluated at a coarse resolution the blur is computed on a
    /// handful of pixels, and the result stretched over the region is visibly coarser than the same filter
    /// evaluated finely. Absent, the caller's own scale decides.
    /// </summary>
    public int? FilterResolutionX { get; init; }

    /// <summary>See <see cref="FilterResolutionX"/>.</summary>
    public int? FilterResolutionY { get; init; }

    /// <summary>
    /// The largest resolution this build will evaluate at, for either axis.
    ///
    /// A `filterRes` is a request to allocate the region at that size, so a file asking for a million pixels across
    /// is asking for terabytes. The reader **says** so and leaves the resolution unset rather than storing a number
    /// nothing will honour; the engine refuses a model that carries one anyway, because nothing that came through
    /// the reader can.
    /// </summary>
    public const int MaximumFilterResolution = 8192;

    /// <summary>Whether the file asked for a resolution and it is one an engine can allocate.</summary>
    public bool HasFilterResolution
        => FilterResolutionX is >= 1 and <= MaximumFilterResolution &&
           FilterResolutionY is >= 1 and <= MaximumFilterResolution;

    /// <summary>
    /// Whether a resolution a model carries is one this build can evaluate at - what the reader checks before
    /// storing one and the engine checks before allocating.
    /// </summary>
    public static bool AcceptsFilterResolution(int width, int height)
        => width is >= 1 and <= MaximumFilterResolution && height is >= 1 and <= MaximumFilterResolution;

    /// <summary>
    /// The buffer the filter's output is taken from, or empty for the last primitive's result.
    ///
    /// A file can name an intermediate result as the filter's answer, which is the other half of "a graph rather
    /// than a pipeline": the last primitive is not necessarily the last thing computed.
    /// </summary>
    public string Output { get; init; } = string.Empty;

    /// <summary>Whether the filter has anything in it - a filter with no primitives paints nothing.</summary>
    public bool IsEmpty => Primitives.Count == 0;

    /// <summary>
    /// The buffers a renderer supplies rather than a primitive producing them.
    ///
    /// These are the names a graph is allowed to read without a producer. `SourceGraphic` and `SourceAlpha` come
    /// from the shape being filtered; the other three have to be handed to the engine, because only the renderer
    /// knows what is behind the object or what its fill and stroke were painted with - and an engine that invented
    /// them would be drawing a picture the file did not ask for.
    /// </summary>
    public static IReadOnlyList<string> SourceInputs { get; } = new[]
    {
        "SourceGraphic", "SourceAlpha", "BackgroundImage", "FillPaint", "StrokePaint",
    };

    /// <summary>Whether a buffer name is one the renderer supplies.</summary>
    public static bool IsSourceInput(string name) => SourceInputs.Contains(name, StringComparer.Ordinal);

    /// <summary>
    /// The renderer-supplied buffers this graph reads, in the order the primitives name them.
    ///
    /// A caller that cannot supply one - a canvas with no picture behind the object - can say which ones it is
    /// about to paint as transparent, which is the difference between a gap that is reported and one that is
    /// discovered from the picture.
    /// </summary>
    public IEnumerable<string> SourceInputsRead
        => Inputs.Where(IsSourceInput);

    /// <summary>
    /// The primitive that produces a named buffer, or null.
    ///
    /// Used by a renderer to walk the graph from the output backwards, which is the only way to evaluate what is
    /// actually needed rather than everything in the list.
    /// </summary>
    public FilterPrimitive? ProducerOf(string name)
        => Primitives.LastOrDefault(p => p.Result == name);

    /// <summary>
    /// The primitives that **read** a named buffer - the other end of <see cref="ProducerOf"/>.
    ///
    /// One result feeding several consumers is the shape that tells a graph from a pipeline, so this is what an
    /// edit asks before removing a step or renaming its result: a consumer left reading a name nothing produces
    /// silently receives a transparent buffer, and the picture goes wrong somewhere else entirely.
    /// </summary>
    public IEnumerable<FilterPrimitive> ConsumersOf(string name)
        => Primitives.Where(p => p.Input == name || p.Input2 == name);

    /// <summary>
    /// Whether the wiring runs in a circle - a step that reads, through any chain of consumers, the buffer it
    /// produces.
    ///
    /// A cycle has no answer to evaluate: the engine deliberately refuses to recurse into one and hands back a
    /// transparent buffer rather than hanging, so a graph like this draws nothing at all. This is what lets an
    /// operation refuse to build one in the first place, where the reason can be said.
    /// </summary>
    public bool HasCycle
    {
        get
        {
            // Two primitives that agree on every field are equal as records, so the walk is over positions rather
            // than over primitives: a graph is allowed to hold the same step twice.
            var producers = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < Primitives.Count; i++)
            {
                if (Primitives[i].Result.Length > 0)
                {
                    // The last producer wins, which is the same answer ProducerOf gives.
                    producers[Primitives[i].Result] = i;
                }
            }

            var settled = new bool[Primitives.Count];
            var onPath = new bool[Primitives.Count];

            bool Walk(int index)
            {
                if (onPath[index])
                {
                    return true;
                }

                if (settled[index])
                {
                    return false;
                }

                onPath[index] = true;
                foreach (string? name in new[] { Primitives[index].Input, Primitives[index].Input2 })
                {
                    if (name is { Length: > 0 } && producers.TryGetValue(name, out int producer) && Walk(producer))
                    {
                        return true;
                    }
                }

                onPath[index] = false;
                settled[index] = true;
                return false;
            }

            for (int i = 0; i < Primitives.Count; i++)
            {
                if (Walk(i))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>The buffers every primitive reads, which is how a renderer finds its roots.</summary>
    public IEnumerable<string> Inputs
        => Primitives
            .SelectMany(p => new[] { p.Input, p.Input2 })
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .Distinct(StringComparer.Ordinal);

    public bool Equals(FilterSpec? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (Name != other.Name || Primitives.Count != other.Primitives.Count)
        {
            return false;
        }

        for (int i = 0; i < Primitives.Count; i++)
        {
            if (!PrimitiveEquals(Primitives[i], other.Primitives[i]))
            {
                return false;
            }
        }

        return X == other.X && Y == other.Y && Width == other.Width && Height == other.Height &&
               ObjectBoundingBox == other.ObjectBoundingBox && Output == other.Output &&
               PrimitiveUnitsObjectBoundingBox == other.PrimitiveUnitsObjectBoundingBox &&
               FilterResolutionX == other.FilterResolutionX &&
               FilterResolutionY == other.FilterResolutionY;
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Name);
        hash.Add(X);
        hash.Add(Y);
        hash.Add(Width);
        hash.Add(Height);
        hash.Add(Output);
        hash.Add(PrimitiveUnitsObjectBoundingBox);
        hash.Add(FilterResolutionX);
        hash.Add(FilterResolutionY);
        foreach (FilterPrimitive primitive in Primitives)
        {
            hash.Add(primitive);
        }

        return hash.ToHashCode();
    }

    /// <summary>
    /// Whether two primitives are the same step.
    ///
    /// The compiler's own record equality compares <see cref="FilterPrimitive.Matrix"/> **by reference**, and a
    /// twenty-number array read from a file is never the same reference as the one it is compared with - so a
    /// round trip that preserved every number would still report two different filters. Comparing the numbers is
    /// what makes "the graph that went out is the graph that came back" a claim a test can make, and it is also what
    /// the cycle walk and the JSON round trip need: two steps that look alike must compare alike everywhere.
    /// </summary>
    internal static bool PrimitiveEquals(FilterPrimitive a, FilterPrimitive b)
    {
        if (a == b)
        {
            return true;
        }

        if (a.Kind != b.Kind)
        {
            return false;
        }

        // Everything but the matrix is a value or a string, and the compiler's equality is right for those. The
        // matrix is the only array on the record, so it is the only member that has to be compared by content.
        return a with { Matrix = null } == b with { Matrix = null } && SequenceEquals(a.Matrix, b.Matrix);
    }

    private static bool SequenceEquals(double[]? a, double[]? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        if (a is null || b is null || a.Length != b.Length)
        {
            return false;
        }

        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i])
            {
                return false;
            }
        }

        return true;
    }
}
