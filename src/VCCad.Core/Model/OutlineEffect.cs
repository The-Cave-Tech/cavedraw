using System.Collections;
using VCCad.Geometry;

namespace VCCad.Core.Model;

/// <summary>How an outline effect reshapes the outline it is applied to.</summary>
public enum OutlineEffectKind
{
    /// <summary>Move the whole outline outward (or inward) by a distance.</summary>
    OffsetPath,

    /// <summary>Push each vertex a little way in a random direction, so a straight line stops looking ruled.</summary>
    Roughen,

    /// <summary>Add a point to each segment, displaced alternately to either side.</summary>
    ZigZag,

    /// <summary>Draw the outline several times, each pass a little out of place.</summary>
    Scribble,
}

/// <summary>
/// How the corners of an offset outline are formed.
///
/// The same three answers a stroke's own join has, for the same reason: a mitre on a sharp turn runs away into a
/// long spike, and a person who can already choose a bevel or a round for the stroke has to be able to choose it
/// for the outline it grows into. <see cref="Miter"/> is the default because it is what an offset path was before
/// the choice existed, so an existing document renders as it always did.
/// </summary>
public enum OutlineJoin
{
    /// <summary>The two offset edges are extended until they cross, which is the sharpest corner.</summary>
    Miter,

    /// <summary>The offset edges stop at the corner and a straight edge closes the gap between them.</summary>
    Bevel,

    /// <summary>The gap is closed with an arc of the offset distance.</summary>
    Round,
}

/// <summary>
/// One outline effect: what it is, and the parameters that apply to it.
///
/// A single record with a kind rather than a type per effect, because the alternative is a discriminated union
/// that has to be hand-written for JSON, for the sidecar and for every reader. Which members apply is decided by
/// <see cref="Kind"/> and documented here; the others are carried and ignored, which is what lets a person change
/// an effect's kind without the panel having to rebuild the effect from scratch.
///
/// <see cref="Seed"/> is what makes a random-looking effect **deterministic**. The repo requires identical
/// documents to produce identical output, so an effect that drew fresh randomness on each render would make a
/// document look different every time the window was redrawn and export differently from the canvas - which is the
/// whole thing this subsystem exists to prevent.
/// </summary>
public sealed record OutlineEffectSpec(
    OutlineEffectKind Kind,
    double Size = 2.0,
    double Detail = 1.0,
    int Seed = 1)
{
    /// <summary>Roughen or scribble: how far a point may move. Zig-zag: how far to either side.</summary>
    public double Size { get; init; } = Size;

    /// <summary>
    /// How finely the effect is drawn. Roughen: how many pieces each segment is divided into before its points are
    /// displaced, so a roughen can be fine-grained rather than merely large. Scribble: how many passes to draw,
    /// rounded and clamped to at least one. Zig-zag and offset path ignore it.
    /// </summary>
    public double Detail { get; init; } = Detail;

    /// <summary>The seed every pseudo-random choice is drawn from, so the same effect always looks the same.</summary>
    public int Seed { get; init; } = Seed;

    /// <summary>
    /// Zig-zag: how many to-and-fro points each segment carries. One is the single midpoint it had before this was
    /// a choice, which is what keeps an existing document looking the same.
    /// </summary>
    public int Ridges { get; init; } = 1;

    /// <summary>Zig-zag: whether each ridge is rounded into a wave rather than a straight-sided point.</summary>
    public bool Smooth { get; init; }

    /// <summary>Offset path: how the corners are formed. Mitre, which is what an offset path always was.</summary>
    public OutlineJoin Join { get; init; } = OutlineJoin.Miter;

    /// <summary>
    /// Scribble: how many points each pass samples per segment of the path. One is the path's own points, which is
    /// what a pass was before this was a choice - and what keeps the default geometry unchanged.
    /// </summary>
    public double Density { get; init; } = 1.0;

    /// <summary>
    /// Scribble: how far each pass runs on past the point where the loop closes, as a fraction of one whole turn.
    /// </summary>
    public double Overlap { get; init; }

    /// <summary>Scribble: how far each sampled point wanders to either side of the path, alternating side to side.</summary>
    public double Width { get; init; }

    /// <summary>Scribble: how far each pass bows away from the path in one smooth sweep round the loop.</summary>
    public double Curviness { get; init; }

    /// <summary>Scribble: how far each sampled point is thrown in a random direction of its own.</summary>
    public double Scatter { get; init; }

    /// <summary>A sensible zig-zag, which is the effect most drawings reach for first.</summary>
    public static OutlineEffectSpec ZigZag(double size, int seed = 1)
        => new(OutlineEffectKind.ZigZag, size, 1.0, seed);

    /// <summary>A sensible roughen.</summary>
    public static OutlineEffectSpec Roughen(double size, int seed = 1)
        => new(OutlineEffectKind.Roughen, size, 1.0, seed);

    /// <summary>An offset outline. Positive grows, negative shrinks.</summary>
    public static OutlineEffectSpec OffsetPath(double distance)
        => new(OutlineEffectKind.OffsetPath, distance, 1.0, 1);

    /// <summary>A scribble of this many passes.</summary>
    public static OutlineEffectSpec Scribble(double size, int passes, int seed = 1)
        => new(OutlineEffectKind.Scribble, size, passes, seed);
}

/// <summary>
/// An ordered list of outline effects, compared **by value**.
///
/// A record or a bare array would compare by reference, so two strokes with the same effects would never be equal
/// to each other and every round-trip test built on equality would fail for a reason that has nothing to do with
/// the round trip. `WidthProfileSpec` and `DashPattern` both had to avoid the same trap.
///
/// The order is the order they are applied in, which is why this is a list rather than a set: a roughen inside an
/// offset does not look like an offset inside a roughen.
/// </summary>
public sealed class EffectStack : IReadOnlyList<OutlineEffectSpec>
{
    private readonly OutlineEffectSpec[] _effects;

    public EffectStack(IEnumerable<OutlineEffectSpec> effects) => _effects = effects.ToArray();

    /// <summary>No effects, which is what an ordinary stroke has.</summary>
    public static EffectStack Empty { get; } = new(Array.Empty<OutlineEffectSpec>());

    public OutlineEffectSpec this[int index] => _effects[index];

    public int Count => _effects.Length;

    public IEnumerator<OutlineEffectSpec> GetEnumerator()
        => ((IEnumerable<OutlineEffectSpec>)_effects).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _effects.GetEnumerator();

    public bool Equals(EffectStack? other)
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

    public override bool Equals(object? obj) => Equals(obj as EffectStack);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (OutlineEffectSpec effect in _effects)
        {
            hash.Add(effect);
        }

        return hash.ToHashCode();
    }
}

/// <summary>
/// Applies outline effects to a set of outlines.
///
/// Every effect here is **pure geometry**: outlines in, outlines out, nothing read from the document and nothing
/// written back. That is what lets the same code run in the canvas, in the PDF exporter and later in the SVG
/// exporter without any of them having to agree about anything beyond calling it.
/// </summary>
public static class OutlineEffects
{
    /// <summary>
    /// The most pieces a segment may be divided into, and the most passes a scribble may draw.
    ///
    /// A panel hands this a number a person typed, and a number with no ceiling is a request to hang the window:
    /// these caps are what keeps a typo - "100000" for "10" - a slower drawing rather than a frozen application.
    /// </summary>
    private const int MaxDivisions = 64;

    /// <summary>
    /// The point ceiling for one scribble pass. `density`, `overlap` and the path's own length multiply together,
    /// so a cap is needed on the product as well as on each factor.
    /// </summary>
    private const int MaxSamples = 4096;

    /// <summary>How many pieces a rounded zig-zag ridge is drawn in.</summary>
    private const int SmoothSamples = 4;

    /// <summary>How much turn one piece of a rounded offset corner covers.</summary>
    private const double RoundCornerStep = Math.PI / 12.0;

    /// <summary>Applies each effect in order, which is the order they were added to the appearance stack.</summary>
    public static IReadOnlyList<IReadOnlyList<Point2D>> Apply(
        IReadOnlyList<IReadOnlyList<Point2D>> outlines,
        IReadOnlyList<OutlineEffectSpec> effects)
    {
        IReadOnlyList<IReadOnlyList<Point2D>> current = outlines;

        foreach (OutlineEffectSpec effect in effects)
        {
            current = ApplyOne(current, effect);
        }

        return current;
    }

    /// <summary>Applies one effect to every loop.</summary>
    public static IReadOnlyList<IReadOnlyList<Point2D>> ApplyOne(
        IReadOnlyList<IReadOnlyList<Point2D>> outlines,
        OutlineEffectSpec effect)
    {
        if (effect.Size == 0 && effect.Kind != OutlineEffectKind.Scribble)
        {
            // Nothing to do, and worth returning the input unchanged rather than a rebuilt copy: an effect with
            // no size should not perturb the geometry at all.
            return outlines;
        }

        var result = new List<IReadOnlyList<Point2D>>();

        foreach (IReadOnlyList<Point2D> loop in outlines)
        {
            switch (effect.Kind)
            {
                case OutlineEffectKind.OffsetPath:
                    result.Add(Offset(loop, effect));
                    break;
                case OutlineEffectKind.Roughen:
                    result.Add(Roughen(loop, effect));
                    break;
                case OutlineEffectKind.ZigZag:
                    result.Add(ZigZag(loop, effect));
                    break;
                case OutlineEffectKind.Scribble:
                    result.AddRange(Scribble(loop, effect));
                    break;
                default:
                    result.Add(loop);
                    break;
            }
        }

        return result;
    }

    /// <summary>
    /// Moves every point along the outline's outward normal, joining the corners the way <see cref="OutlineEffectSpec.Join"/>
    /// asks for.
    ///
    /// The outward direction comes from the loop's **signed area**: a loop that runs one way has its outside on
    /// one side, and a loop that runs the other way has it on the other. Using the winding rather than a fixed
    /// direction is what makes a positive distance grow the shape whichever way round it was drawn.
    /// </summary>
    private static IReadOnlyList<Point2D> Offset(IReadOnlyList<Point2D> loop, OutlineEffectSpec effect)
    {
        int n = loop.Count;
        if (n < 2)
        {
            return loop;
        }

        double sign = SignedArea(loop) >= 0 ? 1.0 : -1.0;
        double distance = effect.Size * sign;
        var moved = new List<Point2D>(n);

        for (int i = 0; i < n; i++)
        {
            Point2D previous = loop[(i - 1 + n) % n];
            Point2D next = loop[(i + 1) % n];
            Vector2D incoming = Unit(loop[i] - previous);
            Vector2D outgoing = Unit(next - loop[i]);

            Vector2D normalOut = new(outgoing.Y, -outgoing.X);
            Vector2D normalIn = new(incoming.Y, -incoming.X);

            switch (effect.Join)
            {
                case OutlineJoin.Bevel:
                    // Each offset edge simply stops where it reaches the corner, and the straight edge between the
                    // two ends is the bevel.
                    moved.Add(loop[i] + (normalIn * distance));
                    moved.Add(loop[i] + (normalOut * distance));
                    break;

                case OutlineJoin.Round:
                    moved.AddRange(RoundCorner(loop[i], normalIn, normalOut, distance));
                    break;

                default:
                    moved.Add(MiterCorner(loop[i], normalIn, normalOut, distance));
                    break;
            }
        }

        return moved;
    }

    /// <summary>
    /// Where the two offset edges cross.
    ///
    /// Each **edge** has to end up Size away from where it was, and the corner is where the two offset
    /// edges meet. Moving the corner Size along the bisector does not do that: at a right angle it leaves
    /// the edges only 3.5 of the 5 they were asked for, and every corner comes up short the same way - the
    /// same mistake the stroke outline makes and has to miter to avoid. So this is the intersection of the
    /// two offset lines, `(n1 + n2) / (1 + n1.n2)`, which at a right angle is the familiar sqrt(2).
    /// </summary>
    private static Point2D MiterCorner(Point2D corner, Vector2D normalIn, Vector2D normalOut, double distance)
    {
        double dot = (normalOut.X * normalIn.X) + (normalOut.Y * normalIn.Y);
        double denominator = 1.0 + dot;

        Vector2D bisector;
        if (denominator < 1e-3)
        {
            // The edges nearly double back, so the intersection runs away; bevel instead, as the outline does.
            bisector = Unit(new Vector2D(normalOut.X + normalIn.X, normalOut.Y + normalIn.Y));
        }
        else
        {
            bisector = new Vector2D(
                (normalOut.X + normalIn.X) / denominator,
                (normalOut.Y + normalIn.Y) / denominator);
        }

        return corner + (bisector * distance);
    }

    /// <summary>
    /// The arc that joins the two offset edges round the corner, from one to the other the short way.
    ///
    /// Both ends sit the same distance from the corner the edges came from, so the arc is centred there and the
    /// distance is its radius. The number of pieces follows the **turn**, not the size, so a corner of the same
    /// sharpness is drawn with the same points whatever distance it was offset by.
    /// </summary>
    private static IEnumerable<Point2D> RoundCorner(
        Point2D corner, Vector2D normalIn, Vector2D normalOut, double distance)
    {
        Vector2D from = normalIn * distance;
        Vector2D to = normalOut * distance;

        double first = Math.Atan2(from.Y, from.X);
        double delta = Math.Atan2(to.Y, to.X) - first;
        while (delta > Math.PI)
        {
            delta -= Math.PI * 2.0;
        }

        while (delta < -Math.PI)
        {
            delta += Math.PI * 2.0;
        }

        int steps = Math.Clamp((int)Math.Ceiling(Math.Abs(delta) / RoundCornerStep), 2, 24);
        double radius = Math.Abs(distance);

        for (int k = 0; k <= steps; k++)
        {
            double angle = first + (delta * k / steps);
            yield return new Point2D(
                corner.X + (Math.Cos(angle) * radius),
                corner.Y + (Math.Sin(angle) * radius));
        }
    }

    /// <summary>
    /// Pushes each point a little way in a direction drawn from the seed.
    ///
    /// `detail` divides each segment first: one keeps the path's own points, and each step above that puts another
    /// displaced point between them. Size and detail are different questions - how far a point moves and how many
    /// points there are to move - and a person asking for a fine grain wants the second one without also asking
    /// for a wilder outline.
    /// </summary>
    private static IReadOnlyList<Point2D> Roughen(IReadOnlyList<Point2D> loop, OutlineEffectSpec effect)
    {
        var random = new DeterministicRandom(effect.Seed);
        int between = Division(effect.Detail) - 1;
        var moved = new List<Point2D>(loop.Count * (between + 1));

        for (int i = 0; i < loop.Count; i++)
        {
            moved.Add(Displace(random, loop[i], effect.Size));

            if (between <= 0 || loop.Count < 2)
            {
                continue;
            }

            Point2D to = loop[(i + 1) % loop.Count];
            for (int k = 1; k <= between; k++)
            {
                double t = (double)k / (between + 1);
                moved.Add(Displace(
                    random,
                    new Point2D(
                        loop[i].X + ((to.X - loop[i].X) * t),
                        loop[i].Y + ((to.Y - loop[i].Y) * t)),
                    effect.Size));
            }
        }

        return moved;
    }

    /// <summary>
    /// Adds points along each segment, displaced to one side and then the other.
    ///
    /// Alternating by **point index** rather than randomly is deliberate: a zig-zag that chose its side at
    /// random would look like a roughen, and the effect exists to give a line a regular to-and-fro. `ridges` says
    /// how many of those to-and-fro points a segment carries, and `smooth` rounds each one into a wave: a ridge is
    /// the half of the to-and-fro between two crossings, so a rounded one rises and falls across that half rather
    /// than going out to the full size and straight back.
    /// </summary>
    private static IReadOnlyList<Point2D> ZigZag(IReadOnlyList<Point2D> loop, OutlineEffectSpec effect)
    {
        int n = loop.Count;
        if (n < 2)
        {
            return loop;
        }

        int ridges = Division(effect.Ridges);
        int perRidge = effect.Smooth ? SmoothSamples : 1;
        var jagged = new List<Point2D>(n * (1 + (ridges * perRidge)));
        int ridge = 0;

        for (int i = 0; i < n; i++)
        {
            Point2D from = loop[i];
            Point2D to = loop[(i + 1) % n];
            jagged.Add(from);

            Vector2D direction = Unit(to - from);
            Vector2D normal = new(direction.Y, -direction.X);

            for (int r = 0; r < ridges; r++)
            {
                double side = ridge % 2 == 0 ? 1.0 : -1.0;

                for (int s = 0; s < perRidge; s++)
                {
                    double within = perRidge == 1 ? 0.5 : (s + 0.5) / perRidge;
                    double along = (r + within) / ridges;
                    var on = new Point2D(
                        from.X + ((to.X - from.X) * along),
                        from.Y + ((to.Y - from.Y) * along));

                    double reach = perRidge == 1 ? 1.0 : Math.Sin(Math.PI * within);
                    jagged.Add(on + (normal * (effect.Size * side * reach)));
                }

                ridge++;
            }
        }

        return jagged;
    }

    /// <summary>
    /// Draws the outline several times, each pass nudged from the last.
    ///
    /// One pass returns the loop unchanged rather than a copy of it displaced, because a scribble of one pass is
    /// not a scribble - and an effect that quietly moved the artwork would be the wrong answer to "make this look
    /// hand-drawn" on a shape someone was happy with.
    ///
    /// Every pass is nudged as a whole by up to `size`, which is what a scribble was before the rest of these
    /// parameters existed; the others say **how** a pass wanders rather than how far the pass is displaced.
    /// </summary>
    private static IEnumerable<IReadOnlyList<Point2D>> Scribble(IReadOnlyList<Point2D> loop, OutlineEffectSpec effect)
    {
        int passes = Division(effect.Detail);
        if (passes <= 1)
        {
            yield return loop;
            yield break;
        }

        int perSegment = Division(effect.Density);
        int samples = SampleCount(loop.Count, perSegment, effect.Overlap);
        double width = Math.Max(0.0, effect.Width);
        double curviness = Math.Max(0.0, effect.Curviness);
        double scatter = Math.Max(0.0, effect.Scatter);

        for (int pass = 0; pass < passes; pass++)
        {
            var random = new DeterministicRandom(effect.Seed + (pass * 7919));
            double dx = (random.NextDouble() - 0.5) * 2.0 * effect.Size;
            double dy = (random.NextDouble() - 0.5) * 2.0 * effect.Size;

            var strand = new List<Point2D>(samples);

            for (int m = 0; m < samples; m++)
            {
                // Sampled in index space rather than by true arc length, so a density of one lands exactly on the
                // path's own points - which is what makes the default the geometry it was before.
                double along = m * (double)loop.Count / samples;
                Point2D on = Sample(loop, along);
                Vector2D normal = NormalAt(loop, along);

                double sideways = (width * (m % 2 == 0 ? 1.0 : -1.0)) +
                                  (curviness * Math.Sin(Math.PI * 2.0 * m / samples));

                double thrownX = 0.0;
                double thrownY = 0.0;
                if (scatter > 0.0)
                {
                    double angle = random.NextDouble() * Math.PI * 2.0;
                    double distance = random.NextDouble() * scatter;
                    thrownX = Math.Cos(angle) * distance;
                    thrownY = Math.Sin(angle) * distance;
                }

                strand.Add(new Point2D(
                    on.X + dx + (normal.X * sideways) + thrownX,
                    on.Y + dy + (normal.Y * sideways) + thrownY));
            }

            yield return strand;
        }
    }

    /// <summary>One point moved in a direction drawn from the effect's own sequence.</summary>
    private static Point2D Displace(DeterministicRandom random, Point2D point, double size)
    {
        double angle = random.NextDouble() * Math.PI * 2.0;
        double distance = random.NextDouble() * size;
        return new Point2D(
            point.X + (Math.Cos(angle) * distance),
            point.Y + (Math.Sin(angle) * distance));
    }

    /// <summary>How many points one scribble pass draws: `density` per segment, plus `overlap` more of a turn.</summary>
    private static int SampleCount(int points, int perSegment, double overlap)
    {
        double turns = 1.0 + Math.Max(0.0, Math.Min(overlap, MaxDivisions));
        return Math.Clamp((int)Math.Ceiling(points * perSegment * turns), 1, MaxSamples);
    }

    /// <summary>The point `t` vertices along the loop, interpolating and wrapping round the end.</summary>
    private static Point2D Sample(IReadOnlyList<Point2D> loop, double t)
    {
        int n = loop.Count;
        int index = (int)Math.Floor(t);
        double fraction = t - index;
        Point2D from = loop[((index % n) + n) % n];
        Point2D to = loop[(((index + 1) % n) + n) % n];

        return new Point2D(
            from.X + ((to.X - from.X) * fraction),
            from.Y + ((to.Y - from.Y) * fraction));
    }

    /// <summary>The unit normal at `t`, from the segment the point sits on - so a wander is always across the path.</summary>
    private static Vector2D NormalAt(IReadOnlyList<Point2D> loop, double t)
    {
        int n = loop.Count;
        int index = (int)Math.Floor(t);
        Point2D from = loop[((index % n) + n) % n];
        Point2D to = loop[(((index + 1) % n) + n) % n];
        Vector2D direction = Unit(to - from);

        return new Vector2D(direction.Y, -direction.X);
    }

    /// <summary>A count of pieces, whole and capped, from a number a panel may have been handed.</summary>
    private static int Division(double value) => Math.Clamp((int)Math.Round(value), 1, MaxDivisions);

    /// <summary>Twice the signed area; the sign is which way the loop runs.</summary>
    private static double SignedArea(IReadOnlyList<Point2D> loop)
    {
        double sum = 0.0;
        for (int i = 0; i < loop.Count; i++)
        {
            Point2D a = loop[i];
            Point2D b = loop[(i + 1) % loop.Count];
            sum += (a.X * b.Y) - (b.X * a.Y);
        }

        return sum / 2.0;
    }

    private static Vector2D Unit(Vector2D v)
    {
        double length = Math.Sqrt((v.X * v.X) + (v.Y * v.Y));
        return length < 1e-12 ? new Vector2D(0, 0) : new Vector2D(v.X / length, v.Y / length);
    }

    /// <summary>
    /// A tiny linear congruential generator.
    ///
    /// Written out rather than using <see cref="Random"/> with a seed, because the framework's seeded sequence is
    /// documented as not being stable across versions: a document that rendered one way yesterday would render
    /// another way after a runtime upgrade, and the export would stop matching the canvas for no reason anyone
    /// could see. These constants are the classic ones, and the sequence they produce is fixed forever.
    /// </summary>
    private sealed class DeterministicRandom
    {
        private uint _state;

        public DeterministicRandom(int seed) => _state = (uint)seed == 0 ? 0x9E3779B9u : (uint)seed;

        public double NextDouble()
        {
            _state = (1664525u * _state) + 1013904223u;
            return _state / 4294967296.0;
        }
    }
}
