using VCCad.Geometry;

namespace VCCad.Core.Model;

/// <summary>
/// One bristle of a bristle brush: where it starts along the path, how far it runs, how far it sits across the
/// path, how thick it is, the turn its own direction takes, the shade its colour strays to, and its own polyline.
/// </summary>
/// <param name="Start">Where the bristle begins, as arc length along its own subpath.</param>
/// <param name="Length">How much of the path the bristle runs along, in path-local units.</param>
/// <param name="Offset">
/// How far the bristle sits across the path, positive to the left of travel. It is the position in the bundle
/// that the pressure moves: every bristle of a fully pressed brush is further from the centreline than the same
/// bristle of a lightly pressed one.
/// </param>
/// <param name="Thickness">The bristle's own thickness, in path-local units - the width the loop it paints covers.</param>
/// <param name="TurnDegrees">
/// The turn the bristle's own direction takes, in degrees, on top of the direction the path runs in where it
/// starts. It is where the pen's tilt lands, and where a bristle's own stray from the sequence lands.
/// </param>
/// <param name="Shade">
/// How far the bristle's colour strays from the stroke's, in -1..1: negative towards black, positive towards
/// white. Zero - which is every bristle of a brush that states no colour jitter - paints the stroke's own colour.
/// </param>
/// <param name="Points">
/// The bristle's own centreline, in path-local coordinates. A stiff bristle follows the path's curve; a limp one
/// is a straight line from where it starts, which is what makes a brush splay through a bend.
/// </param>
public readonly record struct BristleStroke(
    double Start,
    double Length,
    double Offset,
    double Thickness,
    double TurnDegrees,
    double Shade,
    IReadOnlyList<Point2D> Points);

/// <summary>
/// What a bristle brush painted on one path: the bristles themselves, and whether the count asked for was more
/// than the engine would draw.
/// </summary>
/// <param name="Bristles">The bristles, in the order they are painted, along the path.</param>
/// <param name="Requested">How many bristles per subpath the brush asked for, before the bound.</param>
/// <param name="CountBoundHit">
/// Whether <see cref="Requested"/> was more than the engine draws. **Reported rather than hidden**: a brush that
/// asked for five thousand bristles and quietly got the bound is a drawing that looks like it lost detail, and the
/// whole point of the bound is that it is a stated limit rather than a silent one.
/// </param>
public sealed record BristleBundle(
    IReadOnlyList<BristleStroke> Bristles,
    int Requested,
    bool CountBoundHit)
{
    /// <summary>A path a bristle brush draws nothing on.</summary>
    public static BristleBundle None { get; } = new(Array.Empty<BristleStroke>(), 0, false);
}

/// <summary>
/// Where a bristle brush's bristles go along a path, and where each one's own stroke runs (issue #103).
///
/// **Why this is a seam of its own.** An art, pattern or scatter brush puts one piece of the document's artwork at
/// a place, and all three answer as an item under a transform (<see cref="PlacedArt"/>). A bristle brush puts no
/// artwork anywhere: its bristles are **strokes**, and what a renderer needs is the geometry of those strokes -
/// which is a different shape of answer. So this returns a list of bristles rather than a list of placements, and
/// <see cref="StrokeOutlineBuilder"/> turns each bristle into the loop it covers, which is what makes the outline
/// the union of the bristle strokes.
///
/// **Nothing here is held on the path.** The bristles are recomputed from the path every time they are asked for,
/// which is what makes a bristle brush a **stroke property**: edit the path and the bristles follow, with no brush
/// re-applied and nothing re-baked. A <see cref="StrokeRenderPlan"/> is widths and outlines and has no member that
/// says a bristle is drawn at a place, so the fact that this is a second step rather than part of the plan is
/// reported by <c>brush.bristles</c> rather than hidden.
///
/// **The sequence is reproducible.** A bristle's own stray, its start and its shade are pure functions of the path,
/// the parameters and the bristle's own index, through the same stable integer sequence
/// <see cref="ScatterBrushPath"/> uses - never a fresh <see cref="Random"/>. The repository's determinism rule is
/// load-bearing: identical documents must render identically, and a brush that drew from a per-frame generator
/// would make every frame a different picture and every exported file a different document.
///
/// **The work is bounded, and the bound is reported.** A long path with a large count is a large number of strokes,
/// so there are three limits: <see cref="MaxBristles"/> on the count, and <see cref="MaxSamples"/> samples per
/// bristle taken at <see cref="SampleStep"/> along the path. A count past the first limit comes back as
/// <see cref="BristleBundle.CountBoundHit"/> rather than as a silent shortage.
/// </summary>
public static class BristleBrushPath
{
    /// <summary>
    /// The most bristles one subpath may be painted with. A thousand bristles on a long path is a thousand
    /// strokes, so the count is capped rather than obeyed - and the cap is reported, because a brush that asked
    /// for more and got this is a drawing that looks like it lost detail.
    /// </summary>
    public const int MaxBristles = 1024;

    /// <summary>How far apart a bristle's own points are sampled along the path, in path units.</summary>
    public const double SampleStep = 1.0;

    /// <summary>The most points one bristle's centreline is sampled at, so a very long run cannot run away.</summary>
    public const int MaxSamples = 256;

    private const double Epsilon = 1e-9;

    // The axes the sequence is asked for, one seed offset each, named so a reader can see which draw is which.
    // Their values are arbitrary but fixed: changing one would re-place every existing bristle.
    private const int OffsetAxis = 0;
    private const int StartAxis = 1;
    private const int TurnAxis = 2;
    private const int ShadeAxis = 3;

    /// <summary>
    /// The bristles a brush paints on a path, in path-local coordinates and in the order they are painted - along
    /// the path, and per subpath in the order the path states them.
    ///
    /// <paramref name="scale"/> is the renderer's own scale factor, the same one <see cref="StrokeOutlineBuilder"/>
    /// is handed: the brush's size, its bristles' length and thickness are in the stroke's units, so a path inside
    /// a scaled group paints at that scale.
    ///
    /// <paramref name="pressure"/> is the pen's pressure in 0..1, which widens the bundle through
    /// <see cref="BristleBrushSpec.PressureSpread"/>. It is 1 - a fully pressed pen, which is what a static
    /// document is drawn as - when nothing recorded how the path was drawn.
    ///
    /// <paramref name="tiltDegrees"/> is how far the pen is laid over, in degrees, which turns every bristle
    /// through <see cref="BristleBrushSpec.TiltTurn"/>. It is 0 for the same reason pressure is 1: a stored
    /// document holds no pen.
    ///
    /// A brush that is not a bristle brush, one with no size, one whose bristles have no thickness and a path with
    /// no segment all answer with nothing: there is nothing to paint, and inventing a bristle would draw a stroke
    /// the model never described. **The count is per subpath**, for the reason a nib is swept along each subpath:
    /// every subpath of a compound path is brushed in its own right rather than sharing one bundle.
    /// </summary>
    public static BristleBundle Strokes(
        PathItem path,
        BrushSpec brush,
        double scale = 1.0,
        double pressure = 1.0,
        double tiltDegrees = 0.0)
    {
        if (!brush.IsBristle || brush.BristleSpec is not { } spec)
        {
            return BristleBundle.None;
        }

        int requested = Math.Max(0, spec.Count);
        int count = Math.Min(requested, MaxBristles);
        bool bound = requested > MaxBristles;

        double size = Math.Max(0.0, brush.Diameter) * scale;
        double thickness = Math.Max(0.0, spec.Thickness) * scale;
        if (count == 0 || size <= 0.0 || thickness <= 0.0)
        {
            return new BristleBundle(Array.Empty<BristleStroke>(), requested, bound);
        }

        // The pen's two responses, read once. Pressure widens the bundle and tilt turns it; a brush that states no
        // amount for either ignores the pen entirely, which is what a document drawn without one looks like.
        double pressed = double.IsNaN(pressure) ? 1.0 : Math.Clamp(pressure, 0.0, 1.0);
        double spread = Math.Max(0.0, spec.Spread)
            * (1.0 - (Math.Clamp(spec.PressureSpread, 0.0, 1.0) * (1.0 - pressed)));
        double half = size / 2.0 * spread;

        double randomness = Math.Clamp(spec.Randomness, 0.0, 1.0);
        double stiffness = Math.Clamp(spec.Stiffness, 0.0, 1.0);
        double fromTilt = (double.IsNaN(tiltDegrees) ? 0.0 : tiltDegrees) * spec.TiltTurn;

        var bristles = new List<BristleStroke>();
        IReadOnlyList<FlattenedOutline> outlines = PathFlattener.FlattenForStroke(path);

        for (int subpath = 0; subpath < outlines.Count; subpath++)
        {
            FlattenedOutline outline = outlines[subpath];
            if (!TryMeasure(outline, out IReadOnlyList<Point2D> points, out double[] lengths, out double total))
            {
                continue;
            }

            uint baseSeed = Seed(spec, size, subpath, total);

            // A length of zero is the model's own default and means "the whole path"; a stated length longer than
            // the subpath is the subpath, because a bristle cannot run past the stroke that carries it.
            double run = spec.Length > 0.0 ? Math.Min(spec.Length * scale, total) : total;
            double room = Math.Max(0.0, total - run);

            for (int i = 0; i < count; i++)
            {
                uint seed = Mix(baseSeed + ((uint)i * 0x9E3779B9u) + ((uint)subpath * 0x85EBCA6Bu));

                // Where the bristle sits across the bundle: an even fan from one edge to the other, moved by its
                // own stray. A bundle of one is on the centreline, which is the only place it can be.
                double fan = count == 1 ? 0.0 : ((2.0 * i) / (count - 1)) - 1.0;
                double offset = (fan * half) + (Noise(seed, OffsetAxis) * randomness * half);

                // Where along the path it starts. A bristle that runs the whole path starts at the beginning; a
                // shorter one is spread evenly over the room it has, with its own stray, so a bundle of dashes
                // covers the stroke rather than piling up at one end.
                double start = room <= Epsilon
                    ? 0.0
                    : (((count == 1 ? 0.5 : i / (double)(count - 1)) * room)
                        + (Noise(seed, StartAxis) * randomness * room / Math.Max(1, count)));

                double turn = fromTilt + (Noise(seed, TurnAxis) * randomness * 90.0);
                double shade = Math.Clamp(Noise(seed, ShadeAxis) * spec.ColourJitter, -1.0, 1.0);

                bristles.Add(new BristleStroke(
                    start,
                    run,
                    offset,
                    thickness,
                    turn,
                    shade,
                    Draw(points, lengths, total, start, run, offset, turn, stiffness)));
            }
        }

        return new BristleBundle(bristles, requested, bound);
    }

    /// <summary>
    /// One bristle's centreline: the path's own run at the bristle's offset, turned by its own angle, and
    /// straightened by how stiff the brush is.
    ///
    /// Read in three steps, which is the order a person sees them: the **run** is the piece of path the bristle
    /// covers, sampled at <see cref="SampleStep"/> and pushed across the path so the bristle lies beside the line
    /// rather than on it; the **turn** rotates that whole streak about its own start, which is where the pen's tilt
    /// and the bristle's stray land; and the **stiffness** blends the turned streak towards the straight line it
    /// would be if it did not follow the path at all - one keeps every curve of the path, zero draws a rigid
    /// bristle. On a straight path the two are the same line, which is why a test can pin the turn exactly there.
    /// </summary>
    private static IReadOnlyList<Point2D> Draw(
        IReadOnlyList<Point2D> points,
        double[] lengths,
        double total,
        double start,
        double run,
        double offset,
        double turnDegrees,
        double stiffness)
    {
        IReadOnlyList<Point2D> followed = Sample(points, lengths, total, start, run, offset);
        if (followed.Count < 2)
        {
            return followed;
        }

        double turn = turnDegrees * Math.PI / 180.0;
        Point2D origin = followed[0];
        var turned = new List<Point2D>(followed.Count);
        foreach (Point2D point in followed)
        {
            turned.Add(Rotate(point, origin, turn));
        }

        if (stiffness >= 1.0)
        {
            return turned;
        }

        // The straight bristle: from the same start, along the path's own direction there turned by the same
        // angle, for the same arc length - so a limp bristle is the same length as a stiff one and only its shape
        // differs.
        double along = Math.Atan2(
            points[1].Y - points[0].Y, points[1].X - points[0].X);
        if (run > Epsilon && total > Epsilon)
        {
            (_, double tangent) = At(points, lengths, start);
            along = tangent;
        }

        double direction = along + turn;
        double straightLength = run <= Epsilon ? TotalLength(turned) : run;
        var straight = new List<Point2D>(turned.Count);
        int steps = turned.Count - 1;
        for (int i = 0; i < turned.Count; i++)
        {
            double walked = straightLength * i / steps;
            straight.Add(new Point2D(
                origin.X + (Math.Cos(direction) * walked),
                origin.Y + (Math.Sin(direction) * walked)));
        }

        var blended = new List<Point2D>(turned.Count);
        for (int i = 0; i < turned.Count; i++)
        {
            blended.Add(Lerp(turned[i], straight[i], 1.0 - stiffness));
        }

        return blended;
    }

    /// <summary>
    /// A run of the path as the polyline a bristle covers, pushed across the path by <paramref name="offset"/>
    /// along the left normal of travel at every sample.
    ///
    /// The offset is applied **along the path** rather than to a straight line, so a bristle beside a bend bends
    /// with it: the bundle's outer bristles stay outside the curve instead of cutting its corner. The samples are
    /// taken by arc length at <see cref="SampleStep"/>, capped at <see cref="MaxSamples"/> - which is the second
    /// half of the work bound, because a straight run of ten thousand points is ten thousand points on every
    /// bristle otherwise.
    /// </summary>
    private static IReadOnlyList<Point2D> Sample(
        IReadOnlyList<Point2D> points,
        double[] lengths,
        double total,
        double start,
        double run,
        double offset)
    {
        int samples = Math.Clamp(
            (int)Math.Ceiling(run / SampleStep), 1, MaxSamples);

        var result = new List<Point2D>(samples + 1);
        int segment = 0;
        double walked = 0.0;
        double end = Math.Min(start + run, total);

        for (int i = 0; i <= samples; i++)
        {
            double position = start + ((end - start) * i / samples);

            // One forward walk for the whole run rather than a search per sample, so a long path costs one pass
            // over its segments instead of one per bristle point.
            while (segment < lengths.Length - 1 && walked + lengths[segment] <= position + Epsilon)
            {
                walked += lengths[segment];
                segment++;
            }

            Point2D from = points[segment];
            Point2D to = points[(segment + 1) % points.Count];
            double length = lengths[segment];
            double t = length <= Epsilon ? 0.0 : Math.Clamp((position - walked) / length, 0.0, 1.0);
            Point2D point = Lerp(from, to, t);

            double tangent = length <= Epsilon ? 0.0 : Math.Atan2(to.Y - from.Y, to.X - from.X);

            // The left normal of travel in a Y-down space, which is the same normal PathOffset offsets by.
            result.Add(new Point2D(
                point.X + (Math.Sin(tangent) * offset),
                point.Y - (Math.Cos(tangent) * offset)));
        }

        return result;
    }

    /// <summary>Whether an outline has a direction to give, and its segment lengths and total when it has.</summary>
    private static bool TryMeasure(
        FlattenedOutline outline,
        out IReadOnlyList<Point2D> points,
        out double[] lengths,
        out double total)
    {
        points = outline.Points;
        lengths = Array.Empty<double>();
        total = 0.0;

        int count = points.Count;
        if (count < 2)
        {
            return false;
        }

        // A closed outline's last point joins back to the first, so that segment counts; an open one stops at its
        // last point - the same rule every other seam in the model follows.
        int segments = outline.IsClosed ? count : count - 1;
        lengths = new double[segments];
        for (int i = 0; i < segments; i++)
        {
            lengths[i] = Distance(points[i], points[(i + 1) % count]);
            total += lengths[i];
        }

        return total > Epsilon;
    }

    /// <summary>The point and the direction of travel at an arc length along the polyline.</summary>
    private static (Point2D Point, double Tangent) At(
        IReadOnlyList<Point2D> points, double[] lengths, double position)
    {
        double walked = 0.0;
        for (int i = 0; i < lengths.Length; i++)
        {
            double length = lengths[i];
            if (position <= walked + length || i == lengths.Length - 1)
            {
                Point2D from = points[i];
                Point2D to = points[(i + 1) % points.Count];
                double t = length <= Epsilon ? 0.0 : Math.Clamp((position - walked) / length, 0.0, 1.0);
                return (Lerp(from, to, t), Math.Atan2(to.Y - from.Y, to.X - from.X));
            }

            walked += length;
        }

        return (points[^1], 0.0);
    }

    /// <summary>One point turned about another, in the sense the angles in this file are measured in.</summary>
    private static Point2D Rotate(Point2D point, Point2D about, double radians)
    {
        double cos = Math.Cos(radians);
        double sin = Math.Sin(radians);
        double dx = point.X - about.X;
        double dy = point.Y - about.Y;
        return new Point2D(
            about.X + (dx * cos) - (dy * sin),
            about.Y + (dx * sin) + (dy * cos));
    }

    /// <summary>A draw in -1..1 from one axis of one bristle's seed.</summary>
    private static double Noise(uint seed, int axis)
        => (Mix(seed + ((uint)axis * 0x27D4EB2Fu)) / 4294967296.0 * 2.0) - 1.0;

    /// <summary>
    /// The seed a subpath's sequence starts from: the bundle's own parameters, which subpath this is, and how long
    /// it is - and nothing else, for the reason <see cref="ScatterBrushPath"/> gives: the arrangement depends on
    /// what was drawn and not on an identifier nobody drew, so two renders and two exports agree.
    /// </summary>
    private static uint Seed(BristleBrushSpec spec, double size, int subpath, double totalLength)
    {
        uint seed = 2166136261u;
        seed = Fold(seed, subpath);
        seed = Fold(seed, (int)Math.Round(totalLength * 1000.0, MidpointRounding.AwayFromZero));
        seed = Fold(seed, (int)Math.Round(size * 1000.0, MidpointRounding.AwayFromZero));
        seed = Fold(seed, spec.Count);
        seed = Fold(seed, (int)Math.Round(spec.Length * 1000.0, MidpointRounding.AwayFromZero));
        seed = Fold(seed, (int)Math.Round(spec.Stiffness * 1000.0, MidpointRounding.AwayFromZero));
        seed = Fold(seed, (int)Math.Round(spec.Thickness * 1000.0, MidpointRounding.AwayFromZero));
        seed = Fold(seed, (int)Math.Round(spec.Spread * 1000.0, MidpointRounding.AwayFromZero));
        seed = Fold(seed, (int)Math.Round(spec.Randomness * 1000.0, MidpointRounding.AwayFromZero));
        return Mix(seed);
    }

    /// <summary>One integer mixed into a seed, FNV-1a's step: the same input always gives the same output.</summary>
    private static uint Fold(uint seed, int value) => (seed ^ (uint)value) * 16777619u;

    /// <summary>
    /// A 32-bit avalanche, so that neighbouring seeds give unrelated draws rather than similar ones - arithmetic on
    /// unsigned integers only, which is what makes it identical on every machine and every run.
    /// </summary>
    private static uint Mix(uint x)
    {
        x ^= x >> 16;
        x *= 0x7FEB352Du;
        x ^= x >> 15;
        x *= 0x846CA68Bu;
        x ^= x >> 16;
        return x;
    }

    private static Point2D Lerp(Point2D from, Point2D to, double t)
        => new(from.X + ((to.X - from.X) * t), from.Y + ((to.Y - from.Y) * t));

    private static double Distance(Point2D a, Point2D b)
        => Math.Sqrt(((b.X - a.X) * (b.X - a.X)) + ((b.Y - a.Y) * (b.Y - a.Y)));

    private static double TotalLength(IReadOnlyList<Point2D> points)
    {
        double total = 0.0;
        for (int i = 1; i < points.Count; i++)
        {
            total += Distance(points[i - 1], points[i]);
        }

        return total;
    }
}
