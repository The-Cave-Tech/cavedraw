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
/// <param name="Paints">
/// The colour of each loop in <paramref name="Outlines"/>, or null when every loop takes the stroke's own colour.
/// It is parallel to the outlines and present only for a **bristle brush** whose bristles differ in colour: a
/// bristle brush paints each bristle as its own stroke, and a colour jitter that reached the model and not the
/// pixels would be exactly the "stored, round-tripped and never honoured" defect. Null is the ordinary case, and
/// it is what keeps every other kind's rendering a single fill.
/// </param>
public sealed record StrokeRenderPlan(
    bool IsOutline,
    double Width,
    IReadOnlyList<IReadOnlyList<Point2D>> Outlines,
    IReadOnlyList<ColorRgb>? Paints = null)
{
    /// <summary>An ordinary stroked path at this width.</summary>
    public static StrokeRenderPlan Stroked(double width)
        => new(false, width, Array.Empty<IReadOnlyList<Point2D>>());

    /// <summary>A filled outline covering this region, optionally painted loop by loop.</summary>
    public static StrokeRenderPlan Filled(
        IReadOnlyList<IReadOnlyList<Point2D>> outlines, IReadOnlyList<ColorRgb>? paints = null)
        => new(true, 0.0, outlines, paints);
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
    /// Resolves a stroke to the geometry that draws it.
    ///
    /// <paramref name="scale"/> is the renderer's own scale factor - the group transform's scale, for the
    /// exporter. The profile's widths, and the brush's diameter, are in the same units as the stroke's own
    /// width, so all of them are scaled together; the canvas passes 1 because it paints inside the transform.
    ///
    /// **An art brush contributes nothing here, deliberately.** A plan is a width or a set of outlines, and an art
    /// brush is neither: it maps an asset along the path at a stated size, which <see cref="ArtBrushPath"/>
    /// answers. So a stroke carrying one keeps the width it has - the outline below is the stroke's own, not the
    /// art's - and the art is placed by a second step that this type has no member to describe. That gap is real
    /// and is reported rather than patched over: a renderer that reads only the plan draws the stroke and no art.
    ///
    /// **A bristle brush, by contrast, *is* the outline.** Its answer is a set of strokes rather than a width, and
    /// filling their union is exactly what the brush paints - so <see cref="BristleBrushPath"/> becomes the loops
    /// here, and the canvas, the PDF writer and the SVG writer each draw a bristle brush with no drawing route of
    /// their own. When the brush jitters its bristles' colours the loops carry one paint each
    /// (<see cref="StrokeRenderPlan.Paints"/>), which is the one thing a single fill cannot say.
    /// </summary>
    public static StrokeRenderPlan Plan(PathItem path, StrokeSpec stroke, double scale = 1.0)
    {
        double width = Math.Max(0.0, stroke.Width * scale);

        if (!stroke.HasWidthProfile && !stroke.HasEffects && !stroke.HasBrush)
        {
            // A constant-width stroke with nothing done to its outline stays a stroke. Turning it into an outline
            // would change what it looks like, because a stroked path's caps and joins are the renderer's, not a
            // filled region's.
            return StrokeRenderPlan.Stroked(width);
        }

        (IReadOnlyList<IReadOnlyList<Point2D>> loops, IReadOnlyList<ColorRgb>? paints) =
            PaintedGeometry(path, stroke, scale);
        return StrokeRenderPlan.Filled(loops, paints);
    }

    /// <summary>
    /// The outlines covering a stroked path, in path-local coordinates.
    ///
    /// Also the answer for a plain stroke, for callers that need the region rather than a plan - the flattening
    /// and offsetting are the same work either way.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<Point2D>> Outline(PathItem path, StrokeSpec stroke, double scale = 1.0)
        => PaintedGeometry(path, stroke, scale).Loops;

    /// <summary>
    /// The outlines covering a stroked path **and the colour each one is painted**, in path-local coordinates.
    ///
    /// The two travel together rather than as two calls because they are one answer: a bristle brush's loops are
    /// one loop per bristle in the order the bristles are painted, and a caller that asked for the loops and then
    /// for the colours would be reading two computations of one geometry that are only equal because the sequence
    /// is deterministic. <see cref="Outline"/> is this answer without the colours, which is what every caller that
    /// paints the whole stroke one colour wants.
    /// </summary>
    public static (IReadOnlyList<IReadOnlyList<Point2D>> Loops, IReadOnlyList<ColorRgb>? Paints) PaintedGeometry(
        PathItem path, StrokeSpec stroke, double scale = 1.0)
    {
        // **A bristle brush is the one kind whose answer is a set of strokes rather than one region.** Its loops
        // are the outline of each bristle, so filling them is filling the union the issue describes - and every
        // renderer already fills the plan's loops, so the canvas, the PDF writer and the SVG writer each draw a
        // bristle brush without learning a fifth drawing route.
        if (stroke.Brush is { IsBristle: true } bristle)
        {
            return BristleOutline(path, stroke, bristle, scale);
        }

        return (OutlineCore(path, stroke, scale), null);
    }

    /// <summary>
    /// The outline a bristle brush covers, one loop per bristle, with each bristle's own colour when the brush
    /// jitters it.
    ///
    /// **The effects are applied to each bristle on its own**, rather than to the concatenated outline, and that is
    /// what keeps the colours aligned: a scribble effect turns one loop into several, so applying it after the
    /// loops were concatenated would leave a flat list whose entries no longer say which bristle they came from.
    /// Applied per bristle, a bristle's effects produce that bristle's loops and nothing else, in the same order
    /// the concatenated form would have had them.
    ///
    /// A bristle is a hair, so its own stroke is capped and joined round: the loop is what a pen of the bristle's
    /// thickness would have painted along the bristle's centreline.
    /// </summary>
    private static (IReadOnlyList<IReadOnlyList<Point2D>> Loops, IReadOnlyList<ColorRgb>? Paints) BristleOutline(
        PathItem path, StrokeSpec stroke, BrushSpec brush, double scale)
    {
        BristleBrushSpec spec = brush.BristleSpec ?? BristleBrushSpec.Default;

        // **What the pen was doing travels with the stroke** (issue #107). It used to be left out here - the seam was
        // called for a fully pressed, untilted pen because a stored document had no member recording one - so a
        // bundle drawn under a light pen painted the width a heavy one would, and the readings the seam already
        // honoured were unreachable at render time. `stroke.PenAt` answers a full, upright pen when nothing was
        // recorded, which is exactly what a mouse-drawn line is drawn as.
        BristleBundle bundle = BristleBrushPath.Strokes(path, brush, scale, stroke.Pen);
        if (bundle.Bristles.Count == 0)
        {
            return (Array.Empty<IReadOnlyList<Point2D>>(), null);
        }

        bool jittered = spec.ColourJitter > 0.0;
        var loops = new List<IReadOnlyList<Point2D>>(bundle.Bristles.Count);
        List<ColorRgb>? paints = jittered ? new List<ColorRgb>(bundle.Bristles.Count) : null;

        foreach (BristleStroke bristle in bundle.Bristles)
        {
            IReadOnlyList<IReadOnlyList<Point2D>> one = PathOffset.Outline(
                new[] { new FlattenedOutline(bristle.Points, isClosed: false) },
                null,
                bristle.Thickness,
                stroke.MiterLimit,
                StrokeCap.Round,
                StrokeJoin.Round);

            if (stroke.HasEffects)
            {
                one = OutlineEffects.Apply(one, stroke.AllEffects);
            }

            ColorRgb paint = Shaded(stroke.Color, bristle.Shade);
            foreach (IReadOnlyList<Point2D> loop in one)
            {
                loops.Add(loop);
                paints?.Add(paint);
            }
        }

        return (loops, paints);
    }

    /// <summary>
    /// A colour moved towards black or white by a bristle's own shade, which is what a colour jitter means.
    ///
    /// The alpha is the stroke's, not the bristle's: a jitter is a difference in **hue and lightness** between
    /// hairs, and a translucent stroke whose hairs were also translucent would be a different document rather than
    /// a broader one.
    /// </summary>
    private static ColorRgb Shaded(ColorRgb colour, double shade)
    {
        if (shade == 0.0)
        {
            return colour;
        }

        double t = Math.Clamp(Math.Abs(shade), 0.0, 1.0);
        double to = shade < 0.0 ? 0.0 : 1.0;
        return new ColorRgb(
            colour.R + ((to - colour.R) * t),
            colour.G + ((to - colour.G) * t),
            colour.B + ((to - colour.B) * t),
            colour.A);
    }

    /// <summary>The outlines covering a stroked path, without asking what colour each one is painted.</summary>
    private static IReadOnlyList<IReadOnlyList<Point2D>> OutlineCore(
        PathItem path, StrokeSpec stroke, double scale)
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

        // The nib is scaled with the stroke's own widths, so a brush on a path inside a scaled group draws the
        // line the canvas shows at that scale rather than the line it would draw at scale 1.
        BrushSpec? brush = stroke.Brush;
        if (brush is not null && scale != 1.0)
        {
            brush = brush.Scaled(scale);
        }

        // Only a **nib** answers this seam. An art brush maps an asset along the path instead of sweeping a nib
        // along it, so it has no half-width to give - reading its size as one would draw a line where the file has
        // artwork. Its geometry is ArtBrushPath, which is a different shape of answer and a different caller.
        PathOffset.DirectionalHalfWidths? directional =
            brush is { IsNib: true } ? (_, direction) => brush.Halves(direction) : null;

        double width = stroke.Width * scale;
        IReadOnlyList<FlattenedOutline> flattened = PathFlattener.FlattenForStroke(path);

        // The stroke's own cap and join travel with the outline, because nothing downstream can supply them:
        // the canvas, the PDF writer and the SVG writer all fill these loops and no other geometry, so a filled
        // region without its stroke's ends and corners is a different picture in all three at once - and this is
        // the one place that can put them back for all three.
        IReadOnlyList<IReadOnlyList<Point2D>> outlines = Dashing(stroke.Dash, scale)
            ? DashedOutline(flattened, profile, width, stroke, scale, directional)
            : PathOffset.Outline(
                flattened, profile, width, stroke.MiterLimit, stroke.Cap, stroke.Join, directional);

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
    /// a dash leaves no on-interval at all, so the stroke the pen would have drawn solid inks nothing.
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
        double scale,
        PathOffset.DirectionalHalfWidths? directional)
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
                    PathOffset.Outline(
                        new[] { run.Shape }, runProfile, width, stroke.MiterLimit, stroke.Cap, stroke.Join,
                        directional);

                if (loops.Count == 0)
                {
                    continue;
                }

                // A dash has two ends where the path had one, so the cap is the same answer the undashed
                // outline takes from `PathOffset` - a dash is a run of the stroke, and its ends are the stroke's.
                // A run that spans a whole closed loop has no ends at all, which `run.Shape` being closed says.
                result.Add(loops[0]);
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
