using VCCad.Geometry;

namespace VCCad.Core.Model;

/// <summary>
/// One copy a scatter brush places: where it sits on the path, how big it is drawn, which way it is turned, how far
/// it is offset across the path, how opaque it is, and the transform that carries the asset's own frame onto the path.
/// </summary>
/// <param name="Position">The copy's **centre**, as arc length along its own subpath.</param>
/// <param name="Point">The path point at <paramref name="Position"/>, in path-local coordinates, before the offset.</param>
/// <param name="TangentRadians">The direction of travel where the copy sits, measured from +X towards +Y.</param>
/// <param name="Length">
/// How much of the path the copy covers along it, which is its own height at the scale it is drawn at. It is the
/// copy's foot and not the stroke's width: a scatter copy is laid **along** the path rather than across it.
/// </param>
/// <param name="Scale">
/// The copy's own size as a multiple of the brush's size - its draw from the range, multiplied by whatever the pen's
/// pressure asked for. One is the brush's size, which is what a copy with no scale stated is drawn at.
/// </param>
/// <param name="RotationDegrees">The extra turn the copy's own range gave it, on top of the path's tangent.</param>
/// <param name="Offset">How far the copy's centre sits across the path, positive to the left of travel.</param>
/// <param name="Opacity">
/// How opaque the copy is painted, in 0..1: its draw from the opacity range multiplied by the pen's pressure. It is
/// carried per copy rather than on the item, because a scatter's copies differ from one another and the item does not.
/// </param>
/// <param name="Transform">
/// The affine map from the **copy's own coordinates** into the path's. The copy's own centre lands on the offset
/// point, its +X axis lies across the path turned by <paramref name="RotationDegrees"/>, and its +Y axis runs along
/// the path - the same frame <see cref="ArtBrushPath"/> gives an art brush's asset and <see cref="PatternBrushPath"/>
/// gives a tile, so a renderer draws a scattered copy exactly as it draws either of those.
/// </param>
public readonly record struct ScatterBrushPlacement(
    double Position,
    Point2D Point,
    double TangentRadians,
    double Length,
    double Scale,
    double RotationDegrees,
    double Offset,
    double Opacity,
    AffineTransform Transform);

/// <summary>
/// Where a scatter brush's copies go along a path: the arithmetic that repeats artwork along a curve with a range
/// around each of its controls, so the run looks hand-placed rather than machined (issue #102).
///
/// **Why this is not <see cref="ArtBrushPath"/>.** Both repeat one asset along a path and neither is a width, so
/// they share a frame and a way of measuring. What they do not share is the question: an art brush draws the asset
/// at its own proportions, stretched or scaled to a stated size, and every piece is the same. A scatter brush
/// draws every copy with a **different** turn, size and offset, drawn from a range - so its answer is a list of
/// placements that differ from one another, which the art brush's seam cannot express. The pattern brush is a third
/// question again - which of five tiles goes where - which is why the three seams stay separate and none answers
/// another's question.
///
/// **The randomness is reproducible.** A copy's draw is a pure function of the path, the parameters and the copy's
/// own index, through a stable integer sequence in this file - never a fresh <see cref="Random"/>. The repository's
/// determinism rule is load-bearing: identical documents must render identically, and a scatter that drew from a
/// per-frame generator would make every render a different picture and every exported PDF a different file. The
/// sequence is arithmetic on 32-bit integers, so it is identical on every machine, for the reason
/// <see cref="DynamicsCurve.Evaluate"/>'s bisection is.
///
/// **A range of zero is the stated value exactly.** No draw is made when there is nothing to draw from, so a caller
/// who wants a fixed run of copies gets the numbers they typed to the last bit rather than a plus-or-minus nothing.
///
/// **Nothing here is held on the path.** The copies are recomputed from the path every time they are asked for,
/// which is what makes a scatter brush a **stroke property**: edit the path and the copies follow, with no brush
/// re-applied and nothing re-baked. The model holds no member that says a copy is drawn at a place - a
/// <see cref="StrokeRenderPlan"/> is widths and outlines - so a renderer draws the asset at each placement, and the
/// fact that this is a second step rather than part of the plan is reported rather than hidden.
///
/// **Pressure is a table, not a tint.** The seam takes the pen's pressure and reads the brush's own
/// <see cref="DynamicsSpec"/> for <see cref="DynamicsTarget.ScatterScale"/> and <see cref="DynamicsTarget.Opacity"/>.
/// A target that is switched off contributes a factor of one rather than the pressure itself, which is the rule the
/// width dynamics already follow: with no dynamics recorded a light sample must draw a full copy, not a faint one.
/// </summary>
public static class ScatterBrushPath
{
    /// <summary>
    /// A ceiling on how many copies may be placed, for the reason <see cref="ArtBrushPath"/> gives: a path can always
    /// be long enough, or a pitch small enough, that the count runs away.
    /// </summary>
    private const int MaxCopies = 100_000;

    private const double Epsilon = 1e-9;

    // The axes the sequence is asked for, one seed offset each. They are named rather than numbered at the call
    // sites so that a reader can see which draw is which, and their values are arbitrary but fixed - changing one
    // would rescat every existing drawing, which is why they are written down once.
    private const int SpacingAxis = 0;
    private const int RotationAxis = 1;
    private const int ScaleAxis = 2;
    private const int OffsetAxis = 3;
    private const int OpacityAxis = 4;

    /// <summary>
    /// The copies along the path, in path-local coordinates and in the order they are drawn - along the path from
    /// its beginning, and from the beginning of each subpath after it.
    ///
    /// <paramref name="assetBounds"/> resolves the brush's asset to its box **in its own frame** - what
    /// <see cref="ItemBounds.Of"/> answers - because a placement has to move the asset's own centre onto the path and
    /// only the caller knows which item that is. It answers null for an item the document does not have, and a brush
    /// naming a deleted item places nothing: there is no box to draw, and inventing one would be artwork nobody wrote.
    ///
    /// <paramref name="scale"/> is the renderer's own scale factor, the same one <see cref="StrokeOutlineBuilder"/> is
    /// handed: the brush's size, its pitch and its ranges are in the stroke's units, so a path inside a scaled group
    /// scatters at that scale.
    ///
    /// <paramref name="pressure"/> is the pen's pressure in 0..1. It is 1 - a fully pressed pen, which is what a
    /// static document is drawn as - when nothing recorded how the path was drawn.
    ///
    /// A brush that is not a scatter brush, one that names no asset, an asset with no extent, a brush with no size
    /// and a path with no segment all answer with nothing: there is nothing to place, and inventing one would draw
    /// artwork the model never described.
    /// </summary>
    public static IReadOnlyList<ScatterBrushPlacement> Placements(
        PathItem path,
        BrushSpec brush,
        Func<Guid, Rect2D?> assetBounds,
        double scale = 1.0,
        double pressure = 1.0)
    {
        if (!brush.IsScatter || brush.ScatterSpec is not { } spec || spec.Asset is not { } asset)
        {
            return Array.Empty<ScatterBrushPlacement>();
        }

        double size = Math.Max(0.0, brush.Diameter) * scale;
        if (size <= 0.0)
        {
            return Array.Empty<ScatterBrushPlacement>();
        }

        if (assetBounds(asset) is not { } bounds || bounds.Width <= 0.0 || bounds.Height <= 0.0)
        {
            return Array.Empty<ScatterBrushPlacement>();
        }

        // The pen's two responses, read once: a target that is off contributes one rather than the pressure itself,
        // which is what stops a light sample erasing copies when no dynamics were recorded.
        DynamicsSpec dynamics = brush.Dynamics ?? DynamicsSpec.None;
        double fromPressure = dynamics.For(DynamicsTarget.ScatterScale).Enabled
            ? dynamics.Apply(DynamicsTarget.ScatterScale, pressure)
            : 1.0;
        double opacityFromPressure = dynamics.For(DynamicsTarget.Opacity).Enabled
            ? dynamics.Apply(DynamicsTarget.Opacity, pressure)
            : 1.0;

        var copies = new List<ScatterBrushPlacement>();

        // The flattened polylines stand one-to-one, in order, for the subpaths that have at least two nodes - the
        // same filter `FlattenForStroke` applies, so a subpath that produces no polyline has nothing to scatter
        // along either.
        IReadOnlyList<FlattenedOutline> outlines = PathFlattener.FlattenForStroke(path);
        for (int subpath = 0; subpath < outlines.Count; subpath++)
        {
            Lay(outlines[subpath], subpath, spec, bounds, size, scale, fromPressure, opacityFromPressure, copies);
            if (copies.Count >= MaxCopies)
            {
                break;
            }
        }

        return copies;
    }

    /// <summary>One subpath's copies: the walk from its beginning, one draw per axis per copy.</summary>
    private static void Lay(
        FlattenedOutline outline,
        int subpath,
        ScatterBrushSpec spec,
        Rect2D bounds,
        double size,
        double rendererScale,
        double fromPressure,
        double opacityFromPressure,
        List<ScatterBrushPlacement> copies)
    {
        IReadOnlyList<Point2D> points = outline.Points;
        int count = points.Count;
        int segments = outline.IsClosed ? count : count - 1;
        if (segments < 1)
        {
            return;
        }

        var lengths = new double[segments];
        double total = 0.0;
        for (int i = 0; i < segments; i++)
        {
            lengths[i] = Distance(points[i], points[(i + 1) % count]);
            total += lengths[i];
        }

        if (total <= Epsilon)
        {
            return;
        }

        // Across the path the copy is the brush's size, so this one number is the scale of the asset's own frame;
        // its length along the path follows from the same scale, because a stretched copy is a different picture
        // from a scaled one - the rule the pattern brush's tiles already follow.
        double across = size / bounds.Width;
        double extent = bounds.Height * across;

        uint baseSeed = Seed(spec, subpath, total);
        double spacing = Math.Max(0.0, spec.Spacing.Value) * rendererScale;
        double spacingRange = Math.Abs(spec.Spacing.Randomness) * rendererScale;

        for (double position = 0.0; position < total - Epsilon && copies.Count < MaxCopies;)
        {
            uint seed = Mix(baseSeed + ((uint)copies.Count * 0x9E3779B9u) + ((uint)subpath * 0x85EBCA6Bu));

            // The copy's own turn, size and offset, each drawn from its own axis of the sequence. The offset is
            // held out of the range assertion's way by being clamped nowhere: a copy across the path is a real
            // placement wherever it lands, and the brush's own range is what says how far.
            double rotation = Draw(spec.Rotation, RotationAxis, seed, rendererScale, length: false);
            double copyScale = Math.Max(0.0, Draw(spec.Scale, ScaleAxis, seed, rendererScale, length: false) * fromPressure);
            double offset = Draw(spec.Offset, OffsetAxis, seed, rendererScale, length: true);
            double opacity = Math.Clamp(
                Draw(spec.Opacity, OpacityAxis, seed, rendererScale, length: false) * opacityFromPressure, 0.0, 1.0);

            (Point2D point, double tangent) = At(points, lengths, position);

            // The pitch: the stated spacing, or - when the brush states none, which is the model's default - the
            // copy's own extent along the path, which is what lays the copies end to end. The copy's own scale is
            // folded in so a run of larger copies is not also a denser one.
            double pitch = (spacing > Epsilon ? spacing : extent * copyScale)
                + (Noise(seed, SpacingAxis) * spacingRange);

                copies.Add(new ScatterBrushPlacement(
                    position,
                    point,
                    tangent,
                    extent * copyScale,
                    copyScale,
                    rotation,
                    offset,
                    opacity,
                    Place(point, tangent, across * copyScale, rotation, offset, bounds)));

                // A pitch of nothing or less would place every remaining copy at the same point, so the walk ends
                // instead: a caller who stated a range that cancels the pitch gets the copies that landed, not a hang.
                if (pitch <= Epsilon)
                {
                    break;
                }

                position += pitch;
            }
        }

    /// <summary>
    /// The transform carrying the asset's own frame onto the path at one copy.
    ///
    /// Read outermost first, as <see cref="PatternBrushPath"/> states it: move the asset's own **centre** to the
    /// origin, scale it uniformly to the copy's size - the aspect is kept, because a copy is scaled and not stretched
    /// - turn its axes so +X lies across the path (turned further by the copy's own rotation) and +Y along it, then
    /// put that on the point the copy's offset names.
    ///
    /// The offset is applied in **path** space, not in the copy's own: a copy is moved across the path it travels
    /// along, and the copy's own rotation must not swing the offset round with it. That is also what makes an offset
    /// readable as "five points to the left of the line" however the copy is turned.
    /// </summary>
    private static AffineTransform Place(
        Point2D point, double tangent, double across, double rotationDegrees, double offset, Rect2D bounds)
    {
        // The left normal of travel, which is the tangent angle less a quarter turn - the frame's own +X, so an
        // offset travels the same way the copy's own +X axis does.
        double leftX = Math.Sin(tangent);
        double leftY = -Math.Cos(tangent);
        var centre = new Point2D(point.X + (leftX * offset), point.Y + (leftY * offset));

        double turn = tangent - (Math.PI / 2.0) + (rotationDegrees * Math.PI / 180.0);

        return AffineTransform.CreateTranslation(centre.X, centre.Y)
            .Compose(AffineTransform.CreateRotation(turn))
            .Compose(AffineTransform.CreateScale(across, across))
            .Compose(AffineTransform.CreateTranslation(
                -(bounds.X + (bounds.Width / 2.0)),
                -(bounds.Y + (bounds.Height / 2.0))));
    }

    /// <summary>
    /// One copy's draw from a range: the stated value, moved by the sequence within plus-or-minus its randomness.
    ///
    /// A zero range is the stated value **exactly** - not a draw that happens to be zero, but no draw at all - which
    /// is what lets a caller pin a scatter down to a fixed run of copies and assert it bit for bit.
    ///
    /// **Only the lengths are scaled by the renderer's own scale.** A pitch and an offset are in the stroke's units,
    /// so a path inside a scaled group scatters at that scale; an angle, a size **multiple** and an opacity are not
    /// lengths, and scaling them would scramble the meaning of the number a caller stated.
    /// </summary>
    private static double Draw(
        ScatterParameter parameter, int axis, uint seed, double rendererScale, bool length)
    {
        double factor = length ? rendererScale : 1.0;
        double value = parameter.Value * factor;
        double range = Math.Abs(parameter.Randomness) * factor;
        return range <= 0.0 ? value : value + (Noise(seed, axis) * range);
    }

    /// <summary>A draw in -1..1 from one axis of one copy's seed.</summary>
    private static double Noise(uint seed, int axis)
        => (Mix(seed + ((uint)axis * 0x27D4EB2Fu)) / 4294967296.0 * 2.0) - 1.0;

    /// <summary>
    /// The seed a subpath's sequence starts from: the brush's own parameters, the asset it maps, which subpath this
    /// is, and how long it is.
    ///
    /// **Derived from the path and the parameters**, which is what the issue asks for and what makes a scatter
    /// reproducible: the same document gives the same numbers, so two renders agree and two exports are identical
    /// files. Nothing here is a clock, a thread id or a fresh generator. The asset's id is folded by its bytes rather
    /// than by <see cref="Guid.GetHashCode"/>, because a hash is not promised to be the same number on a different
    /// run and a seed that moved would rescatter every drawing.
    /// </summary>
    private static uint Seed(ScatterBrushSpec spec, int subpath, double totalLength)
    {
        uint seed = 2166136261u;
        seed = Fold(seed, subpath);
        seed = Fold(seed, (int)Math.Round(totalLength * 1000.0, MidpointRounding.AwayFromZero));
        seed = FoldGuid(seed, spec.Asset);
        seed = FoldParameter(seed, spec.Spacing);
        seed = FoldParameter(seed, spec.Rotation);
        seed = FoldParameter(seed, spec.Scale);
        seed = FoldParameter(seed, spec.Offset);
        seed = FoldParameter(seed, spec.Opacity);
        return Mix(seed);
    }

    /// <summary>One parameter's value and range folded into a seed, in thousandths so a stated number is not lost to rounding.</summary>
    private static uint FoldParameter(uint seed, ScatterParameter parameter)
    {
        seed = Fold(seed, (int)Math.Round(parameter.Value * 1000.0, MidpointRounding.AwayFromZero));
        return Fold(seed, (int)Math.Round(Math.Abs(parameter.Randomness) * 1000.0, MidpointRounding.AwayFromZero));
    }

    /// <summary>The asset's own bytes folded in, so two brushes that map different artwork do not scatter alike.</summary>
    private static uint FoldGuid(uint seed, Guid? id)
    {
        if (id is not { } guid)
        {
            return Fold(seed, 0);
        }

        Span<byte> bytes = stackalloc byte[16];
        guid.TryWriteBytes(bytes);
        for (int i = 0; i < 16; i += 4)
        {
            int word = bytes[i] | (bytes[i + 1] << 8) | (bytes[i + 2] << 16) | (bytes[i + 3] << 24);
            seed = Fold(seed, word);
        }

        return seed;
    }

    /// <summary>One integer mixed into a seed, FNV-1a's step: the same input always gives the same output.</summary>
    private static uint Fold(uint seed, int value) => (seed ^ (uint)value) * 16777619u;

    /// <summary>
    /// A 32-bit avalanche, so that neighbouring seeds give unrelated draws rather than similar ones. This is the
    /// finalizer of the well-known splitmix sequence; it is arithmetic on unsigned integers only, which is what makes
    /// it identical on every machine and every run.
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

    /// <summary>
    /// The point and the direction of travel at an arc length along the polyline.
    ///
    /// The direction is the **segment's**, not a blended average of the two meeting at a vertex: a copy belongs to one
    /// segment, so a copy starting on the far side of a corner points along that side rather than along a bisector
    /// neither segment runs in - the same choice <see cref="ArtBrushPath"/> and <see cref="PatternBrushPath"/> make.
    /// </summary>
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
                double t = length <= 0.0 ? 0.0 : Math.Clamp((position - walked) / length, 0.0, 1.0);
                return (Lerp(from, to, t), Math.Atan2(to.Y - from.Y, to.X - from.X));
            }

            walked += length;
        }

        // Unreachable while the polyline has a segment; a path of one repeated point has no direction to give.
        return (points[^1], 0.0);
    }

    private static Point2D Lerp(Point2D from, Point2D to, double t)
        => new(from.X + ((to.X - from.X) * t), from.Y + ((to.Y - from.Y) * t));

    private static double Distance(Point2D a, Point2D b)
        => Math.Sqrt(((b.X - a.X) * (b.X - a.X)) + ((b.Y - a.Y) * (b.Y - a.Y)));
}
