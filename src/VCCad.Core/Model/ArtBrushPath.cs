using VCCad.Geometry;

namespace VCCad.Core.Model;

/// <summary>
/// One piece of an art brush's asset as it sits on the path: where it starts, which way it points, how long it is
/// and the transform that carries the asset's own frame onto the path.
/// </summary>
/// <param name="Position">Where the piece begins, as arc length along its subpath.</param>
/// <param name="Point">The path point that beginning sits on, in path-local coordinates.</param>
/// <param name="TangentRadians">The direction the path is travelling there, measured from +X towards +Y.</param>
/// <param name="Length">How much of the path this piece covers - its extent along the tangent.</param>
/// <param name="Transform">
/// The affine map from the **asset's own coordinates** into the path's. Composing it with the caller's own frame
/// (a group transform, the artboard offset) is what draws the art where the path is.
/// </param>
public readonly record struct ArtBrushPlacement(
    double Position,
    Point2D Point,
    double TangentRadians,
    double Length,
    AffineTransform Transform);

/// <summary>
/// Where an art brush's asset goes along a path: the arithmetic that maps artwork along a curve rather than
/// stroking a line (issue #100).
///
/// **Why this is not in <see cref="PathOffset"/>.** That seam answers one question - how far the stroke's edge is
/// from the centreline at a vertex, given the direction of travel - and every brush that has a width answers it.
/// An art brush has no width: it has artwork, of a stated size, that is *placed* at a point and *turned* to the
/// tangent there, repeated or stretched along the path. Those are different shapes of answer, so they are
/// different seams, and keeping the art out of the width seam is what stops a diameter being read as a half-width
/// and drawn as a line where the file has a picture.
///
/// **Where the art is placed, and what the model does not hold.** The asset's own frame is placed so that its
/// **+X axis runs across the path** - to the left of travel - and its **+Y axis runs along it**, in the direction
/// the path is travelling. The asset's width therefore becomes the brush's size
/// (<see cref="BrushSpec.Diameter"/>, scaled by the renderer's own scale) and its height becomes the piece's
/// extent along the path, which is what the stretch mode decides. The whole thing is the same composition SVG
/// places a marker with (<see cref="Svg.SvgMarkers"/>): translate to the point, turn to the tangent, scale, and
/// move the asset's own origin onto the point.
///
/// Nothing here is stored on the path. The placements are recomputed from the path every time they are asked
/// for - which is what makes an art brush a **stroke property**: edit the path and the art follows, with no brush
/// re-applied and nothing re-baked. The model holds no member that says "art is drawn here" - a
/// <see cref="StrokeRenderPlan"/> is widths and outlines - so a renderer draws the asset at each placement, and
/// the fact that this is a second step rather than part of the plan is reported rather than hidden.
///
/// **Corners are pieces, not bends.** A piece takes the tangent of the flattened segment its start falls on, so
/// where the path turns a corner the art is drawn as separate pieces at each side of it rather than being bent
/// around it. Illustrator offers both; separate pieces is the one that needs no per-asset bending and no
/// assumption that the asset can be bent, so it is the one this build states.
/// </summary>
public static class ArtBrushPath
{
    /// <summary>
    /// A ceiling on how many pieces a repeat may place.
    ///
    /// A path can always be long enough, or a brush small enough, that the count runs away; a caller asking for
    /// the placements of a hairline art brush along a page-sized curve has asked for something no renderer will
    /// draw anyway. The cap is high enough that no drawing reaches it and finite so that none can hang.
    /// </summary>
    private const int MaxPlacements = 100_000;

    /// <summary>
    /// The asset's pieces along the path, in path-local coordinates and in the order they are drawn.
    ///
    /// <paramref name="assetBounds"/> is the asset **in its own frame** - what <see cref="ItemBounds.Of"/> answers
    /// for the item a brush names - because the placement has to move the asset's own origin onto the path, and
    /// only the caller knows which item that is.
    ///
    /// <paramref name="scale"/> is the renderer's own scale factor, the same one <see cref="StrokeOutlineBuilder"/>
    /// is handed: the brush's size is in the stroke's units, so a path inside a scaled group draws the art at that
    /// scale. Nothing else about the path is scaled - the placements are returned in the path's own coordinates,
    /// exactly as the outline is.
    ///
    /// A brush that is not an art brush, an asset with no extent, a brush with no size and a path with no segment
    /// all answer with no placements: there is nothing to place, and inventing one would draw art the model never
    /// described.
    /// </summary>
    public static IReadOnlyList<ArtBrushPlacement> Placements(
        PathItem path, BrushSpec brush, Rect2D assetBounds, double scale = 1.0)
    {
        if (!brush.IsArt || assetBounds.Width <= 0 || assetBounds.Height <= 0)
        {
            return Array.Empty<ArtBrushPlacement>();
        }

        double size = Math.Max(0.0, brush.Diameter) * scale;
        if (size <= 0.0)
        {
            return Array.Empty<ArtBrushPlacement>();
        }

        // The asset's width is the brush's size, so this one number scales every art brush: what the stretch mode
        // changes is the **along**-path extent, never how wide the art lies across the stroke.
        double across = size / assetBounds.Width;

        var placements = new List<ArtBrushPlacement>();

        foreach (FlattenedOutline outline in PathFlattener.FlattenForStroke(path))
        {
            IReadOnlyList<Point2D> points = outline.Points;
            if (points.Count < 2)
            {
                continue;
            }

            double[] lengths = SegmentLengths(points, outline.IsClosed);
            double total = lengths.Sum();
            if (total <= 0.0)
            {
                continue;
            }

            (double along, double pieceLength, IReadOnlyList<double> starts) =
                Layout(brush.Stretch, across, total, assetBounds.Height);

            foreach (double start in starts)
            {
                (Point2D point, double tangent) = At(points, lengths, start, total);
                placements.Add(new ArtBrushPlacement(
                    start,
                    point,
                    tangent,
                    pieceLength,
                    Placement(point, tangent, across, along, assetBounds, brush.FlipAcross, brush.FlipAlong)));

                if (placements.Count >= MaxPlacements)
                {
                    return placements;
                }
            }
        }

        return placements;
    }

    /// <summary>
    /// How a stretch mode divides the path: the scale along it, the length one piece covers, and where the pieces
    /// start.
    ///
    /// **Stretch to fit** gives the asset the whole path once, so its along-scale is the path length over its own
    /// height - which does not keep its aspect, and is exactly what "to fit" means. **Scale proportionally** gives
    /// it the same scale in both directions, so it stays the shape it was drawn and leaves the rest of the path
    /// bare. **Repeat** gives it that same proportional scale and then starts another piece every time the
    /// previous one ends, so the last piece overruns the end of the path rather than being squashed into what is
    /// left of it: a squeezed final tile is a different picture from a repeated one.
    /// </summary>
    private static (double Along, double PieceLength, IReadOnlyList<double> Starts) Layout(
        ArtStretch stretch,
        double across,
        double pathLength,
        double assetHeight)
    {
        if (stretch == ArtStretch.StretchToFit)
        {
            return (pathLength / assetHeight, pathLength, new[] { 0.0 });
        }

        double piece = assetHeight * across;
        if (stretch == ArtStretch.ScaleProportionally || piece <= 0.0)
        {
            return (across, piece, new[] { 0.0 });
        }

        var starts = new List<double>();
        for (double at = 0.0; at < pathLength - 1e-9 && starts.Count < MaxPlacements; at += piece)
        {
            starts.Add(at);
        }

        if (starts.Count == 0)
        {
            starts.Add(0.0);
        }

        return (across, piece, starts);
    }

    /// <summary>
    /// The transform carrying the asset's own frame onto the path at one point, turned to the tangent there.
    ///
    /// Read outermost first, as <see cref="Svg.SvgMarkers"/> states it: move the asset's own origin to the origin,
    /// scale it to the brush's size and the stretch's extent - mirrored when either flip is on - turn its axes so
    /// that +X lies across the path and +Y along it, and finally put that on the path point.
    ///
    /// The origin moved is the asset's own **left-centre** - its box's left edge at half its height - so the art
    /// is centred across the stroke and begins at the piece's start along it. Placing its top-left corner instead
    /// would hang every piece half its width off one side of the path.
    /// </summary>
    private static AffineTransform Placement(
        Point2D point,
        double tangent,
        double across,
        double along,
        Rect2D assetBounds,
        bool flipAcross,
        bool flipAlong)
    {
        // +X across the path means the turn from the asset's +X to the path's left normal, which is the tangent
        // angle less a quarter turn: travelling along +X, that normal points along -Y in this Y-down space.
        double turn = tangent - (Math.PI / 2.0);

        return AffineTransform.CreateTranslation(point.X, point.Y)
            .Compose(AffineTransform.CreateRotation(turn))
            .Compose(AffineTransform.CreateScale(
                flipAcross ? -across : across,
                flipAlong ? -along : along))
            .Compose(AffineTransform.CreateTranslation(
                -(assetBounds.X + (assetBounds.Width / 2.0)),
                -assetBounds.Y));
    }

    /// <summary>The length of each segment of a polyline, the closing segment included when the outline is closed.</summary>
    private static double[] SegmentLengths(IReadOnlyList<Point2D> points, bool closed)
    {
        int segments = closed ? points.Count : points.Count - 1;
        var lengths = new double[segments];

        for (int i = 0; i < segments; i++)
        {
            lengths[i] = Distance(points[i], points[(i + 1) % points.Count]);
        }

        return lengths;
    }

    /// <summary>
    /// The point and the direction of travel at an arc length along the polyline.
    ///
    /// The direction is the **segment's**, not a blended average of the two meeting at a vertex: the art is drawn
    /// as pieces and each piece belongs to one segment, so a piece starting on the far side of a corner points
    /// along that side rather than along a bisector neither segment runs in.
    /// </summary>
    private static (Point2D Point, double Tangent) At(
        IReadOnlyList<Point2D> points, double[] lengths, double position, double total)
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
