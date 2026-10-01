using VCCad.Geometry;

namespace VCCad.Core.Model;

/// <summary>
/// What a stroke should be drawn as: an ordinary stroked path at a width, or a filled outline.
///
/// The distinction is the whole point of the builder. A stroke of constant width is **stroked** - the renderer
/// works out the caps and joins, and the outline of a stroked path is not what gets drawn. A stroke that varies
/// in width, or that a brush has mapped, has no stroke to draw, so it becomes the outline that would be filled
/// instead. Both answers come out of here so that no two renderers have to decide for themselves.
/// </summary>
public sealed record StrokeRenderPlan(
    bool IsOutline,
    double Width,
    IReadOnlyList<IReadOnlyList<Point2D>> Outlines)
{
    /// <summary>An ordinary stroked path at this width.</summary>
    public static StrokeRenderPlan Stroked(double width)
        => new(false, width, Array.Empty<IReadOnlyList<Point2D>>());

    /// <summary>A filled outline covering this region.</summary>
    public static StrokeRenderPlan Filled(IReadOnlyList<IReadOnlyList<Point2D>> outlines)
        => new(true, 0.0, outlines);
}

/// <summary>
/// The one place a stroke becomes geometry: path plus width profile plus everything that comes later, resolved to
/// either a width to stroke at or the outlines to fill.
///
/// **Why this exists as a single named step.** The canvas and the PDF exporter each grew their own answer, and
/// they agreed while a stroke was one width with a colour: there was nothing to disagree about. The moment a
/// stroke can vary along its length that stops being true, because two implementations of offsetting a path
/// differ at exactly the corners a person is looking at when they compare the canvas with an export. So the
/// decision is made once, here, in model coordinates, and both renderers consume it.
///
/// The builder is **pure**: the same path and stroke produce the same plan, and nothing is accumulated between
/// calls. That is not an incidental property. An effect applied during planning that also mutates the model
/// would be applied again on the next render, and again on export - the double-application bug, which shows up as
/// an effect that gets stronger every time the window is redrawn.
///
/// Coordinates are **path-local**: the artboard offset is the renderer's business, because only the renderer
/// knows what space it is drawing in.
/// </summary>
public static class StrokeOutlineBuilder
{
    /// <summary>
    /// How much of a round cap's half turn one straight piece covers.
    ///
    /// The cap is sampled rather than emitted as an arc because everything downstream of here - the effects,
    /// the PDF writer - works on points; the same step the round corner of an offset path uses, so a cap and a
    /// corner of the same radius are faceted the same way.
    /// </summary>
    private const double CapStep = Math.PI / 12.0;

    /// <summary>
    /// Resolves a stroke to the geometry that draws it.
    ///
    /// <paramref name="scale"/> is the renderer's own scale factor - the group transform's scale, for the
    /// exporter. The profile's widths are in the same units as the stroke's own width, so both are scaled
    /// together; the canvas passes 1 because it paints inside the transform.
    /// </summary>
    public static StrokeRenderPlan Plan(PathItem path, StrokeSpec stroke, double scale = 1.0)
    {
        double width = Math.Max(0.0, stroke.Width * scale);

        if (!stroke.HasWidthProfile && !stroke.HasEffects)
        {
            // A constant-width stroke with nothing done to its outline stays a stroke. Turning it into an outline
            // would change what it looks like, because a stroked path's caps and joins are the renderer's, not a
            // filled region's.
            return StrokeRenderPlan.Stroked(width);
        }

        return StrokeRenderPlan.Filled(Outline(path, stroke, scale));
    }

    /// <summary>
    /// The outlines covering a stroked path, in path-local coordinates.
    ///
    /// Also the answer for a plain stroke, for callers that need the region rather than a plan - the flattening
    /// and offsetting are the same work either way.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<Point2D>> Outline(PathItem path, StrokeSpec stroke, double scale = 1.0)
    {
        WidthProfileSpec? profile = stroke.WidthProfile;
        if (profile is { IsEmpty: false } && scale != 1.0)
        {
            profile = new WidthProfileSpec(
                profile.Name,
                profile.Points.Select(point => new WidthPoint(
                    point.Position,
                    point.LeftWidth * scale,
                    point.RightWidth * scale,
                    point.Interpolation)));
        }

        double width = stroke.Width * scale;
        IReadOnlyList<FlattenedOutline> flattened = PathFlattener.FlattenForStroke(path);

        IReadOnlyList<IReadOnlyList<Point2D>> outlines = Dashing(stroke.Dash, scale)
            ? DashedOutline(flattened, profile, width, stroke, scale)
            : PathOffset.Outline(flattened, profile, width, stroke.MiterLimit);

        // The effects come **after** the outline exists, because that is what they reshape. Applying them to the
        // path instead would mean each effect having to know about widths and joins, and two renderers could then
        // apply them in different orders.
        return stroke.HasEffects ? OutlineEffects.Apply(outlines, stroke.AllEffects) : outlines;
    }

    /// <summary>
    /// Whether the dash pattern puts any gaps in the stroke.
    ///
    /// A pattern whose lengths sum to nothing is not a dash: PDF and SVG both draw such a stroke solid, and a
    /// pattern of all zeros is the shape that arrives as. Filtering it here rather than dashing with it keeps a
    /// pattern that says "no gaps" from taking a different route through the geometry than a plain stroke.
    ///
    /// Public because every caller that has to **decide** whether a stroke reaches the pen or the outline is
    /// asking this question, and a second reading of the rule drifts from this one: an all-zero pattern read as
    /// a dash takes the outline route, where the ends are squared, instead of the pen's own caps.
    /// </summary>
    public static bool Dashing(DashPattern dash, double scale = 1.0)
    {
        if (dash.IsEmpty)
        {
            return false;
        }

        double total = 0.0;
        foreach (double segment in dash.Segments)
        {
            total += Math.Max(0.0, segment) * scale;
        }

        return total > 0.0;
    }

    /// <summary>
    /// The region a **dashed** stroke covers, as the outline of each dash.
    ///
    /// The dash belongs to the stroke, so it is applied to the path **before** the path becomes geometry: the ink
    /// is only on the on-intervals, and each interval is an open run of the path stroked with its own two ends.
    /// Dashing the finished outline instead - cutting its boundary into lengths - draws a different picture that
    /// merely looks similar in a thumbnail: the dashes would run along the edges of the band rather than across
    /// it, and the gaps would be gaps in the boundary rather than gaps in the ink.
    ///
    /// A dash that runs off one end of a **closed** subpath and on at the other is one run, not two: the two
    /// pieces are joined across the seam, so the loop does not grow a pair of caps in the middle of a dash.
    /// </summary>
    private static IReadOnlyList<IReadOnlyList<Point2D>> DashedOutline(
        IReadOnlyList<FlattenedOutline> outlines,
        WidthProfileSpec? profile,
        double width,
        StrokeSpec stroke,
        double scale)
    {
        var result = new List<IReadOnlyList<Point2D>>();

        foreach (FlattenedOutline outline in outlines)
        {
            foreach (DashRun run in DashRuns(outline, stroke.Dash, scale))
            {
                // The profile is measured along the whole path, so a run reads it through the part of the path
                // it covers. Without that remapping every dash would restart the taper, and a stroke that tapers
                // away to nothing would be at its fat end at the start of every dash.
                WidthProfileSpec? runProfile = run.Capped ? RunProfile(profile, run) : profile;

                IReadOnlyList<IReadOnlyList<Point2D>> loops =
                    PathOffset.Outline(new[] { run.Shape }, runProfile, width, stroke.MiterLimit);

                if (loops.Count == 0)
                {
                    continue;
                }

                result.Add(run.Capped ? Capped(loops[0], run, runProfile, width, stroke.Cap) : loops[0]);
            }
        }

        return result;
    }

    /// <summary>One on-interval of a dashed subpath, as the open shape the stroke is drawn along.</summary>
    /// <param name="Shape">The run's polyline, open, in the subpath's own coordinates.</param>
    /// <param name="Start">Where the run begins, as arc length along the subpath.</param>
    /// <param name="Length">The run's own arc length.</param>
    /// <param name="PathLength">The whole subpath's arc length, which is what a profile position is relative to.</param>
    /// <param name="Capped">False only for a whole closed loop, which has no ends to cap.</param>
    private sealed record DashRun(
        FlattenedOutline Shape,
        double Start,
        double Length,
        double PathLength,
        bool Capped);

    /// <summary>
    /// Cuts one subpath into the runs a dash pattern leaves inked.
    ///
    /// The walk is by **arc length**, which is what a dash is measured in, so the pattern does not speed up
    /// through a curve the way it would if it stepped a fixed number of vertices at a time. The phase is the
    /// stroke's own offset into the pattern, and each subpath starts at it - which is how every renderer dashes a
    /// compound path, so the export and the canvas are dashing from the same place.
    /// </summary>
    private static IReadOnlyList<DashRun> DashRuns(FlattenedOutline outline, DashPattern dash, double scale)
    {
        IReadOnlyList<Point2D> points = outline.Points;
        int count = points.Count;
        if (count < 2)
        {
            return Array.Empty<DashRun>();
        }

        double[] pattern = dash.Segments.Select(s => Math.Max(0.0, s) * scale).ToArray();
        double total = pattern.Sum();

        int segments = outline.IsClosed ? count : count - 1;
        var lengths = new double[segments];
        double pathLength = 0.0;
        for (int i = 0; i < segments; i++)
        {
            lengths[i] = Distance(points[i], points[(i + 1) % count]);
            pathLength += lengths[i];
        }

        if (pathLength <= 0.0 || total <= 0.0)
        {
            return Array.Empty<DashRun>();
        }

        // The phase, brought into the first turn of the pattern: an offset of one whole pattern is no offset,
        // and a negative one is the same dash shifted the other way.
        double phase = dash.Offset * scale;
        double into = phase - (Math.Floor(phase / total) * total);
        int index = 0;
        while (into >= pattern[index])
        {
            into -= pattern[index];
            index = (index + 1) % pattern.Length;
        }

        double remaining = pattern[index] - into;
        bool on = index % 2 == 0;
        bool onAtStart = on;

        var runs = new List<DashRun>();
        var run = new List<Point2D>();
        double runStart = 0.0;
        double walked = 0.0;

        void Advance()
        {
            for (int guard = 0; guard < pattern.Length; guard++)
            {
                index = (index + 1) % pattern.Length;
                if (pattern[index] > 0.0)
                {
                    remaining = pattern[index];
                    on = index % 2 == 0;
                    return;
                }
            }

            // Unreachable while the pattern has a positive length; from here on nothing more is dashed.
            remaining = double.PositiveInfinity;
            on = true;
        }

        DashRun Finish()
        {
            var finished = new DashRun(
                new FlattenedOutline(run, isClosed: false), runStart, RunLength(run), pathLength, Capped: true);
            run = new List<Point2D>();
            runStart = 0.0;
            return finished;
        }

        for (int s = 0; s < segments; s++)
        {
            Point2D from = points[s];
            Point2D to = points[(s + 1) % count];
            double length = lengths[s];
            if (length <= 0.0)
            {
                continue;
            }

            double along = 0.0;
            while (along < length - 1e-12)
            {
                if (remaining <= 1e-12)
                {
                    Advance();
                    continue;
                }

                double step = Math.Min(remaining, length - along);
                if (on)
                {
                    if (run.Count == 0)
                    {
                        runStart = walked + along;
                        run.Add(Lerp(from, to, along / length));
                    }

                    run.Add(Lerp(from, to, (along + step) / length));
                }
                else if (run.Count >= 2)
                {
                    runs.Add(Finish());
                }
                else
                {
                    run.Clear();
                    runStart = 0.0;
                }

                along += step;
                remaining -= step;
                if (remaining <= 1e-12)
                {
                    Advance();
                }
            }

            walked += length;
        }

        // Whether the ink actually reaches the seam. It is not enough for the pattern to be on there: the interval
        // the walk ends inside may have started at the seam itself, with no length to draw.
        bool reachesTheEnd = run.Count >= 2;
        if (reachesTheEnd)
        {
            runs.Add(Finish());
        }

        // On a closed subpath the walk starts and ends at the same point, so a run that reaches both is one dash
        // spanning the seam. Left as two it would be drawn with a cap at each side of the seam - a bulge in the
        // middle of a dash - and the count would be wrong by one.
        if (outline.IsClosed && runs.Count > 0 && onAtStart && reachesTheEnd)
        {
            if (runs.Count == 1)
            {
                // Nothing in the pattern fell off the loop, so the whole outline is inked and there are no ends.
                return new[] { new DashRun(outline, 0.0, pathLength, pathLength, Capped: false) };
            }

            DashRun first = runs[0];
            DashRun last = runs[^1];
            var wrapped = new FlattenedOutline(
                last.Shape.Points.Concat(first.Shape.Points.Skip(1)).ToList(), isClosed: false);

            runs[0] = new DashRun(wrapped, last.Start, last.Length + first.Length, pathLength, Capped: true);
            runs.RemoveAt(runs.Count - 1);
        }

        return runs;
    }

    /// <summary>
    /// The whole-path profile read through one run.
    ///
    /// The widths are sampled at the run's two ends and every profile point that falls inside it is carried over,
    /// remapped to the run's own 0..1. The remapping is affine and the path is walked by arc length in both, so
    /// the width at any point of the run is the width it had before the run was cut out.
    ///
    /// A run that spans a closed subpath's seam wraps past the end of the profile and starts again at its
    /// beginning, so a breakpoint is offered at its own position **and a turn later**: that is what keeps the
    /// width either side of the seam the width the whole path had there.
    ///
    /// The easing travels with the point that ends a piece, which is where <see cref="WidthProfileSpec.HalvesAt"/>
    /// reads it from, so a cubic taper stays cubic across a dash boundary rather than quietly straightening.
    /// </summary>
    private static WidthProfileSpec? RunProfile(WidthProfileSpec? profile, DashRun run)
    {
        // Positions are fractions of the whole path, so a comparison against a run's ends has to be loose enough
        // that a breakpoint the run starts or stops exactly on is not carried twice.
        const double Epsilon = 1e-9;

        if (profile is null || profile.IsEmpty || run.PathLength <= 0.0 || run.Length <= 0.0)
        {
            return profile;
        }

        double from = run.Start / run.PathLength;
        double span = run.Length / run.PathLength;
        if (span <= 0.0)
        {
            return profile;
        }

        // Only a run that crosses the seam goes past the end of the profile: a run that ends exactly where the
        // path does is at position 1, and treating that as "a whole turn further on" would read the widths from
        // the profile's beginning.
        double endPosition = from + span;
        double start = from - Math.Floor(from);
        double end = endPosition > 1.0 + Epsilon ? endPosition - 1.0 : Math.Min(endPosition, 1.0);

        var points = new List<WidthPoint>();
        (double left, double right) = profile.HalvesAt(start) ?? (0.0, 0.0);
        points.Add(new WidthPoint(0.0, left * 2.0, right * 2.0, EasingAt(profile, start)));

        foreach (WidthPoint point in profile.Points)
        {
            for (double candidate = point.Position; candidate < endPosition; candidate += 1.0)
            {
                if (candidate <= from + Epsilon || candidate >= endPosition - Epsilon)
                {
                    continue;
                }

                points.Add(new WidthPoint(
                    (candidate - from) / span, point.LeftWidth, point.RightWidth, point.Interpolation));
            }
        }

        (left, right) = profile.HalvesAt(end) ?? (0.0, 0.0);
        points.Add(new WidthPoint(1.0, left * 2.0, right * 2.0, EasingAt(profile, end)));

        return new WidthProfileSpec(profile.Name, points);
    }

    /// <summary>The interpolation that governs the profile segment around a position.</summary>
    private static WidthInterpolation EasingAt(WidthProfileSpec profile, double position)
    {
        foreach (WidthPoint point in profile.Points)
        {
            if (point.Position >= position)
            {
                return point.Interpolation;
            }
        }

        return profile.Points[^1].Interpolation;
    }

    /// <summary>
    /// Adds the stroke's caps to the two ends of one dash.
    ///
    /// A dash has two ends and a stroke ends in its cap, so a dashed round-capped line drawn without this has
    /// every dash squared off - the dash is right and the stroke it came from is not. Only a dash's runs are
    /// capped here: the ends of a path that is not dashed are the plain outline's business, and that is not a
    /// behaviour this change is entitled to alter.
    /// </summary>
    private static IReadOnlyList<Point2D> Capped(
        IReadOnlyList<Point2D> loop, DashRun run, WidthProfileSpec? profile, double width, StrokeCap cap)
    {
        if (cap == StrokeCap.Butt)
        {
            return loop;
        }

        IReadOnlyList<Point2D> points = run.Shape.Points;
        int n = points.Count;
        if (n < 2 || loop.Count != n * 2)
        {
            return loop;
        }

        // The loop is the left side out and the right side back, so the end cap sits between left(end) and
        // right(end) and the start cap between right(start) and left(start) - which wraps, and is appended.
        (double startLeft, double startRight) = PathOffset.WidthsAt(profile, width, 0.0);
        (double endLeft, double endRight) = PathOffset.WidthsAt(profile, width, 1.0);

        IReadOnlyList<Point2D> end = CapPoints(
            loop[n - 1], loop[n], Unit(points[^1] - points[^2]), (endLeft + endRight) / 2.0, cap);
        IReadOnlyList<Point2D> start = CapPoints(
            loop[^1], loop[0], Unit(points[0] - points[1]), (startLeft + startRight) / 2.0, cap);

        var capped = new List<Point2D>(loop);
        capped.InsertRange(n, end);
        capped.AddRange(start);
        return capped;
    }

    /// <summary>
    /// The points a cap adds between the two sides of a dash's end, in the order the contour travels.
    ///
    /// Butt adds nothing, because the two sides already meet the end squarely. Square projects them by half the
    /// width, so the end is a rectangle past the line. Round is the half turn about the end's midpoint which
    /// bulges along the direction of travel, sampled into points - the other half turn would carve a bite out of
    /// the dash instead, which is the mistake the sweep test below exists to prevent.
    ///
    /// A cap is symmetric, so where a profile is wider on one side of the path than the other the two halves are
    /// averaged: there is no cap that is half a round end and half a square one, and an average keeps the end
    /// where the person drew it rather than pushing it to one side of the line.
    /// </summary>
    private static IReadOnlyList<Point2D> CapPoints(
        Point2D from, Point2D to, Vector2D forward, double half, StrokeCap cap)
    {
        if (half <= 0.0)
        {
            return Array.Empty<Point2D>();
        }

        if (cap == StrokeCap.Square)
        {
            return new[] { from + (forward * half), to + (forward * half) };
        }

        var centre = new Point2D((from.X + to.X) / 2.0, (from.Y + to.Y) / 2.0);
        double radius = Distance(from, to) / 2.0;
        if (radius <= 0.0)
        {
            return Array.Empty<Point2D>();
        }

        double first = Math.Atan2(from.Y - centre.Y, from.X - centre.X);
        double sweep = Math.Atan2(to.Y - centre.Y, to.X - centre.X) - first;
        while (sweep > Math.PI)
        {
            sweep -= Math.PI * 2.0;
        }

        while (sweep <= -Math.PI)
        {
            sweep += Math.PI * 2.0;
        }

        double middle = first + (sweep / 2.0);
        if ((Math.Cos(middle) * forward.X) + (Math.Sin(middle) * forward.Y) < 0.0)
        {
            sweep += sweep > 0.0 ? -Math.PI * 2.0 : Math.PI * 2.0;
        }

        int steps = Math.Max(2, (int)Math.Ceiling(Math.Abs(sweep) / CapStep));
        var arc = new List<Point2D>(steps - 1);
        for (int k = 1; k < steps; k++)
        {
            double angle = first + (sweep * k / steps);
            arc.Add(new Point2D(
                centre.X + (Math.Cos(angle) * radius), centre.Y + (Math.Sin(angle) * radius)));
        }

        return arc;
    }

    /// <summary>The length of a polyline, which is what a run's profile positions are relative to.</summary>
    private static double RunLength(IReadOnlyList<Point2D> points)
    {
        double length = 0.0;
        for (int i = 1; i < points.Count; i++)
        {
            length += Distance(points[i - 1], points[i]);
        }

        return length;
    }

    private static Point2D Lerp(Point2D from, Point2D to, double t)
        => new(from.X + ((to.X - from.X) * t), from.Y + ((to.Y - from.Y) * t));

    private static double Distance(Point2D a, Point2D b)
        => Math.Sqrt(((b.X - a.X) * (b.X - a.X)) + ((b.Y - a.Y) * (b.Y - a.Y)));

    private static Vector2D Unit(Vector2D v)
    {
        double length = Math.Sqrt((v.X * v.X) + (v.Y * v.Y));
        return length < 1e-12 ? new Vector2D(0, 0) : new Vector2D(v.X / length, v.Y / length);
    }
}
