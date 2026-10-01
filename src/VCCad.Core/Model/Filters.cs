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
    string Mode = "normal")
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
    /// These are the names a graph is allowed to read without a producer. The engine treats the last three as
    /// transparent because this model has no fill, stroke or background picture to hand them, which is a documented
    /// gap rather than a reason to refuse a file that uses them.
    /// </summary>
    public static IReadOnlyList<string> SourceInputs { get; } = new[]
    {
        "SourceGraphic", "SourceAlpha", "BackgroundImage", "FillPaint", "StrokePaint",
    };

    /// <summary>Whether a buffer name is one the renderer supplies.</summary>
    public static bool IsSourceInput(string name) => SourceInputs.Contains(name, StringComparer.Ordinal);

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
            if (Primitives[i] != other.Primitives[i])
            {
                return false;
            }
        }

        return X == other.X && Y == other.Y && Width == other.Width && Height == other.Height &&
               ObjectBoundingBox == other.ObjectBoundingBox && Output == other.Output;
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
        foreach (FilterPrimitive primitive in Primitives)
        {
            hash.Add(primitive);
        }

        return hash.ToHashCode();
    }
}
