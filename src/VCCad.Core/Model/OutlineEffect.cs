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
    /// Zig-zag: ignored. Roughen: unused for now. Scribble: how many passes to draw, rounded and clamped to at
    /// least one.
    /// </summary>
    public double Detail { get; init; } = Detail;

    /// <summary>The seed every pseudo-random choice is drawn from, so the same effect always looks the same.</summary>
    public int Seed { get; init; } = Seed;

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
    /// Moves every point along the outline's outward normal.
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
        var moved = new List<Point2D>(n);

        for (int i = 0; i < n; i++)
        {
            Point2D previous = loop[(i - 1 + n) % n];
            Point2D next = loop[(i + 1) % n];
            Vector2D incoming = Unit(loop[i] - previous);
            Vector2D outgoing = Unit(next - loop[i]);

            // Each **edge** has to end up Size away from where it was, and the corner is where the two offset
            // edges meet. Moving the corner Size along the bisector does not do that: at a right angle it leaves
            // the edges only 3.5 of the 5 they were asked for, and every corner comes up short the same way - the
            // same mistake the stroke outline makes and has to miter to avoid. So this is the intersection of the
            // two offset lines, `(n1 + n2) / (1 + n1.n2)`, which at a right angle is the familiar sqrt(2).
            Vector2D normalOut = new(outgoing.Y, -outgoing.X);
            Vector2D normalIn = new(incoming.Y, -incoming.X);
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

            moved.Add(loop[i] + (bisector * (effect.Size * sign)));
        }

        return moved;
    }

    /// <summary>Pushes each point a little way in a direction drawn from the seed.</summary>
    private static IReadOnlyList<Point2D> Roughen(IReadOnlyList<Point2D> loop, OutlineEffectSpec effect)
    {
        var random = new DeterministicRandom(effect.Seed);
        var moved = new List<Point2D>(loop.Count);

        foreach (Point2D point in loop)
        {
            double angle = random.NextDouble() * Math.PI * 2.0;
            double distance = random.NextDouble() * effect.Size;
            moved.Add(new Point2D(
                point.X + (Math.Cos(angle) * distance),
                point.Y + (Math.Sin(angle) * distance)));
        }

        return moved;
    }

    /// <summary>
    /// Adds a point at the middle of each segment, displaced to one side and then the other.
    ///
    /// Alternating by **segment index** rather than randomly is deliberate: a zig-zag that chose its side at
    /// random would look like a roughen, and the effect exists to give a line a regular to-and-fro.
    /// </summary>
    private static IReadOnlyList<Point2D> ZigZag(IReadOnlyList<Point2D> loop, OutlineEffectSpec effect)
    {
        int n = loop.Count;
        if (n < 2)
        {
            return loop;
        }

        var jagged = new List<Point2D>(n * 2);

        for (int i = 0; i < n; i++)
        {
            Point2D from = loop[i];
            Point2D to = loop[(i + 1) % n];
            jagged.Add(from);

            var mid = new Point2D((from.X + to.X) / 2.0, (from.Y + to.Y) / 2.0);
            Vector2D direction = Unit(to - from);
            Vector2D normal = new(direction.Y, -direction.X);
            double side = i % 2 == 0 ? 1.0 : -1.0;

            jagged.Add(mid + (normal * (effect.Size * side)));
        }

        return jagged;
    }

    /// <summary>
    /// Draws the outline several times, each pass nudged from the last.
    ///
    /// One pass returns the loop unchanged rather than a copy of it displaced, because a scribble of one pass is
    /// not a scribble - and an effect that quietly moved the artwork would be the wrong answer to "make this look
    /// hand-drawn" on a shape someone was happy with.
    /// </summary>
    private static IEnumerable<IReadOnlyList<Point2D>> Scribble(IReadOnlyList<Point2D> loop, OutlineEffectSpec effect)
    {
        int passes = Math.Max(1, (int)Math.Round(effect.Detail));
        if (passes <= 1)
        {
            yield return loop;
            yield break;
        }

        for (int pass = 0; pass < passes; pass++)
        {
            var random = new DeterministicRandom(effect.Seed + (pass * 7919));
            double dx = (random.NextDouble() - 0.5) * 2.0 * effect.Size;
            double dy = (random.NextDouble() - 0.5) * 2.0 * effect.Size;

            yield return loop.Select(p => new Point2D(p.X + dx, p.Y + dy)).ToList();
        }
    }

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
