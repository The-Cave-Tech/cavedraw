using VCCad.Geometry;

namespace VCCad.Core.Model;

/// <summary>
/// One tile of a pattern brush as it sits on the path: which slot it fills, the artwork it is drawn with, where
/// its centre is, which way it is turned and how much of the path it covers.
/// </summary>
/// <param name="Slot">
/// The slot the tile fills - side, start, end, inner corner or outer corner. It is the **slot** rather than the
/// artwork, and deliberately: the documented fallback is "a corner with no corner tile of its own is drawn with
/// the side tile", so a reader needs to be able to tell "a turn was recognised and filled" from "the artwork that
/// filled it", which is <paramref name="Asset"/>.
/// </param>
/// <param name="Asset">The document item whose artwork this tile is.</param>
/// <param name="Position">The arc length, along its own subpath, of the tile's **centre**.</param>
/// <param name="Point">The path point at <paramref name="Position"/>, in path-local coordinates.</param>
/// <param name="TangentRadians">
/// The direction the tile is turned to, measured from +X towards +Y: the direction of travel for a side tile, and
/// the bisector of the two legs for a corner tile.
/// </param>
/// <param name="Length">The tile's own foot - how much of the path it covers.</param>
/// <param name="Transform">
/// The affine map from the **tile's own coordinates** into the path's. The tile's own centre lands on
/// <paramref name="Point"/>, its +Y axis runs along <paramref name="TangentRadians"/> and its +X axis across the
/// path to the left of travel - the same frame <see cref="ArtBrushPath"/> gives an art brush's asset, so a
/// renderer draws a tile exactly as it draws placed art.
/// </param>
public readonly record struct PatternTilePlacement(
    PatternTileKind Slot,
    Guid Asset,
    double Position,
    Point2D Point,
    double TangentRadians,
    double Length,
    AffineTransform Transform);

/// <summary>
/// Where a pattern brush's tile set goes along a path: the arithmetic that lays tiles beside the path, turns them
/// at its corners and caps its ends (issue #101).
///
/// **Why this is not <see cref="ArtBrushPath"/>.** Both place a document item's artwork and neither is a width, so
/// they share a frame and a way of measuring. What they do not share is the question: an art brush maps **one**
/// asset along the whole path - stretched, scaled or repeated - and its answer is a run of identical pieces. A
/// pattern brush has up to **five different** tiles and has to decide which goes where, which means reading the
/// path's own corners and cutting the path into runs between them. A single placement list could not say which
/// piece is a corner, so the two seams stay separate and neither answers the other's question.
///
/// **A corner is a node, not a sample.** The detection reads the path's **own nodes**: a node is a corner when the
/// direction arriving at it differs from the direction leaving it by more than
/// <see cref="BrushSpec.PatternCornerThresholdDegrees"/>. That is the honest reading, and it is why a smooth curve
/// has no corners however tightly it bends: a smooth node's two handles are collinear, so the direction of travel
/// through it does not change and the turning happens across the segments rather than at the node. Flattening the
/// path and measuring the turn between consecutive polyline samples would report a corner wherever the flattening
/// tolerance put one, which is a fact about the tolerance rather than about the drawing.
///
/// **Which turn takes which corner tile.** The signed turn is measured from the incoming direction to the outgoing
/// one, so a turn towards the path's right - clockwise in this Y-down space - is positive. A positive turn is the
/// **convex** corner of a shape drawn that way round and takes the **outer** tile; a negative turn is the concave
/// one and takes the **inner** tile. That is the convention this build states, and
/// <c>PatternBrushAlongPathTests</c> pins it rather than leaving it to be guessed from the artwork.
///
/// **The side tiles fill between the corner tiles.** A corner tile reserves its own foot, centred on the turn, and
/// the side tiles run from the start of the path to the first foot, from each foot to the next, and from the last
/// foot to the end. A run is filled at a pitch of the side tile's foot plus the brush's spacing, and the last tile
/// of a run **overruns** rather than being squashed into what is left of it - the choice
/// <see cref="ArtBrushPath"/> already documents, because a squeezed tile is a different picture from a repeated
/// one. An open path's ends are reserved before the corners are placed, so a corner's foot never lands on a start
/// or end tile.
///
/// **Nothing here is held on the path.** The tiles are recomputed from the path every time they are asked for,
/// which is what makes a pattern brush a **stroke property**: edit the path and the tiles follow, with no brush
/// re-applied and nothing re-baked. The model holds no member that says a tile is drawn at a place - a
/// <see cref="StrokeRenderPlan"/> is widths and outlines - so a renderer draws each tile's item at its placement,
/// and the fact that this is a second step rather than part of the plan is reported rather than hidden.
/// </summary>
public static class PatternBrushPath
{
    /// <summary>
    /// A ceiling on how many tiles may be placed, for the reason <see cref="ArtBrushPath"/> gives: a path can
    /// always be long enough, or a tile small enough, that the count runs away.
    /// </summary>
    private const int MaxTiles = 100_000;

    private const double Epsilon = 1e-9;

    /// <summary>
    /// The tile set along the path, in path-local coordinates and in the order the tiles are drawn - along the
    /// path, with a corner tile after the side tile that runs into it, so the corner's artwork covers the overrun
    /// rather than lying under it.
    ///
    /// <paramref name="assetBounds"/> resolves a tile's item to its box **in its own frame** - what
    /// <see cref="ItemBounds.Of"/> answers - because a placement has to move the tile's own centre onto the path
    /// and only the caller knows which item a slot names. It answers null for an item the document does not have,
    /// and a slot whose artwork cannot be resolved places no tile: a brush naming a deleted item draws nothing for
    /// it rather than a tile of invented size.
    ///
    /// <paramref name="scale"/> is the renderer's own scale factor, the same one <see cref="StrokeOutlineBuilder"/>
    /// is handed: the brush's size and spacing are in the stroke's units, so a path inside a scaled group draws
    /// its tiles at that scale.
    ///
    /// A brush that is not a pattern brush, a brush with no size, a brush holding no tiles at all and a path with
    /// no segment all answer with no tiles: there is nothing to lay, and inventing one would draw artwork the
    /// model never described.
    /// </summary>
    public static IReadOnlyList<PatternTilePlacement> Placements(
        PathItem path, BrushSpec brush, Func<Guid, Rect2D?> assetBounds, double scale = 1.0)
    {
        if (!brush.IsPattern || HasNoTiles(brush))
        {
            return Array.Empty<PatternTilePlacement>();
        }

        double size = Math.Max(0.0, brush.Diameter) * scale;
        if (size <= 0.0)
        {
            return Array.Empty<PatternTilePlacement>();
        }

        var tiles = new List<PatternTilePlacement>();

        // The flattened polylines stand one-to-one, in order, for the subpaths that have at least two nodes -
        // which is the same filter `FlattenForStroke` applies, so a subpath that produces no polyline is a subpath
        // with no geometry to lay tiles along either.
        IReadOnlyList<FlattenedOutline> outlines = PathFlattener.FlattenForStroke(path);
        SubPath[] subpaths = path.SubPaths.Where(sub => sub.Nodes.Count >= 2).ToArray();

        for (int i = 0; i < outlines.Count && i < subpaths.Length; i++)
        {
            Lay(outlines[i], subpaths[i], brush, assetBounds, size, scale, tiles);
            if (tiles.Count >= MaxTiles)
            {
                break;
            }
        }

        // **The order the tiles are drawn.** Along the path, and where two coincide - a corner tile over the side
        // tile that overran into it - the corner wins, so the artwork that wraps the turn is what is seen there.
        tiles.Sort(static (a, b) =>
        {
            int byPosition = a.Position.CompareTo(b.Position);
            return byPosition != 0 ? byPosition : Rank(a.Slot).CompareTo(Rank(b.Slot));
        });

        return tiles;
    }

    /// <summary>A tile resolved to the artwork it draws, the foot that artwork covers and the box it is drawn in.</summary>
    private readonly record struct Resolved(PatternTileSpec Spec, Guid Asset, double Length, Rect2D Bounds);

    /// <summary>One turn of the path: the node it sits on, its arc length, and the signed size of the turn.</summary>
    private readonly record struct Corner(int Node, double At, double Turn);

    /// <summary>Where one tile goes: the band of the path it covers, and which way it is turned.</summary>
    private readonly record struct Band(
        double Start, double End, double Centre, PatternTileKind Slot, Resolved Tile, double Tangent);

    /// <summary>Whether every one of the five slots holds nothing, which is a brush with no tile set at all.</summary>
    private static bool HasNoTiles(BrushSpec brush)
        => brush.PatternSideTile is null
           && brush.PatternStartTile is null
           && brush.PatternEndTile is null
           && brush.PatternInnerTile is null
           && brush.PatternOuterTile is null;

    /// <summary>
    /// The drawing order of two tiles that land on the same arc length. A start tile is under the side tiles that
    /// follow it, the corner tiles are over the side tiles that meet at a turn, and an end tile is over the side
    /// tiles that run into it.
    /// </summary>
    private static int Rank(PatternTileKind slot) => slot switch
    {
        PatternTileKind.Start => 0,
        PatternTileKind.Side => 1,
        PatternTileKind.InnerCorner or PatternTileKind.OuterCorner => 2,
        PatternTileKind.End => 3,
        _ => 1,
    };

    /// <summary>One subpath's tiles: its corners found, its runs cut between them, and each run filled by its slot.</summary>
    private static void Lay(
        FlattenedOutline outline,
        SubPath sub,
        BrushSpec brush,
        Func<Guid, Rect2D?> assetBounds,
        double size,
        double scale,
        List<PatternTilePlacement> tiles)
    {
        IReadOnlyList<Point2D> points = outline.Points;
        int count = points.Count;
        int segments = outline.IsClosed ? count : count - 1;
        if (segments < 1)
        {
            return;
        }

        var lengths = new double[segments];
        var at = new double[count];
        double total = 0.0;
        for (int i = 0; i < segments; i++)
        {
            lengths[i] = Distance(points[i], points[(i + 1) % count]);
            at[i] = total;
            total += lengths[i];
        }

        at[^1] = total;

        if (total <= 0.0)
        {
            return;
        }

        // The tile in a slot, resolved. Null when the slot holds nothing, names an item the document does not
        // have, or the item has no extent to draw - none of which is a tile.
        Resolved? Resolve(PatternTileKind slot)
            => ResolveTile(brush, slot, assetBounds, size);

        // **The fallback.** A corner slot with no tile of its own is filled with the side tile, which is why the
        // slot travels on the placement and the artwork travels beside it.
        Resolved? Corner(PatternTileKind slot) => Resolve(slot) ?? Resolve(PatternTileKind.Side);

        double threshold = Math.Clamp(brush.PatternCornerThresholdDegrees, 0.0, 180.0) * Math.PI / 180.0;
        var corners = new List<Corner>();
        int searchFrom = 0;

        for (int node = 0; node < sub.Nodes.Count; node++)
        {
            // An open path's two ends have no direction on one side, so there is nothing to turn through.
            if (!outline.IsClosed && (node == 0 || node == sub.Nodes.Count - 1))
            {
                continue;
            }

            if (!TurnAt(sub, node, out double turn) || Math.Abs(turn) < threshold - Epsilon)
            {
                continue;
            }

            double arc = ArcLengthOf(sub.Nodes[node].Anchor, points, at, ref searchFrom);
            corners.Add(new Corner(node, arc, turn));
        }

        // The two ends are reserved first, so a corner's foot never lands on a start or end tile.
        double runStart = 0.0;
        double runEnd = total;

        if (!outline.IsClosed && Resolve(PatternTileKind.Start) is { } start)
        {
            Place(tiles, points, lengths, total, PatternTileKind.Start, start, start.Length / 2.0, null, size);
            runStart = start.Length;
        }

        if (!outline.IsClosed && Resolve(PatternTileKind.End) is { } end)
        {
            Place(tiles, points, lengths, total, PatternTileKind.End, end, total - (end.Length / 2.0), null, size);
            runEnd = total - end.Length;
        }

        // Each corner's foot, centred on the turn and kept inside the run so a corner near an end does not push
        // its tile off the path. A corner slot nothing can fill places no tile and leaves the run unbroken.
        var bands = new List<Band>();
        foreach (Corner corner in corners.OrderBy(c => c.At))
        {
            PatternTileKind slot =
                corner.Turn > 0.0 ? PatternTileKind.OuterCorner : PatternTileKind.InnerCorner;
            if (Corner(slot) is not { } tile)
            {
                continue;
            }

            double centre = corner.At;
            if (runEnd - runStart >= tile.Length)
            {
                centre = Math.Clamp(corner.At, runStart + (tile.Length / 2.0), runEnd - (tile.Length / 2.0));
            }

            bands.Add(new Band(
                centre - (tile.Length / 2.0), centre + (tile.Length / 2.0), centre, slot, tile,
                Bisector(sub, corner.Node)));
        }

        Resolved? side = Resolve(PatternTileKind.Side);
        double spacing = Math.Max(0.0, brush.PatternSpacing) * scale;

        void SideRun(double from, double to)
        {
            if (side is not { } tile || tile.Length <= 0.0)
            {
                return;
            }

            double pitch = tile.Length + spacing;
            if (pitch <= 0.0)
            {
                return;
            }

            for (double start = from; start < to - Epsilon && tiles.Count < MaxTiles; start += pitch)
            {
                Place(tiles, points, lengths, total, PatternTileKind.Side, tile, start + (tile.Length / 2.0),
                    null, size);
            }
        }

        double cursor = runStart;
        foreach (Band band in bands.OrderBy(b => b.Start))
        {
            SideRun(cursor, band.Start);
            Place(tiles, points, lengths, total, band.Slot, band.Tile, band.Centre, band.Tangent, size);
            cursor = Math.Max(cursor, band.End);
        }

        SideRun(cursor, runEnd);
    }

    /// <summary>
    /// The tile a slot holds, resolved - or null when the slot holds nothing, the item is not there, or the item
    /// has no extent or no size after the tile's own scale.
    ///
    /// The tile's width across the path is the brush's size, so its height runs along the path at the same scale:
    /// the aspect is kept, because a stretched tile is a different picture from a scaled one.
    /// </summary>
    private static Resolved? ResolveTile(
        BrushSpec brush, PatternTileKind slot, Func<Guid, Rect2D?> assetBounds, double size)
    {
        if (brush.Tile(slot) is not { Asset: { } asset } spec)
        {
            return null;
        }

        double tileSize = size * spec.Scale;
        if (tileSize <= 0.0)
        {
            return null;
        }

        if (assetBounds(asset) is not { } bounds || bounds.Width <= 0.0 || bounds.Height <= 0.0)
        {
            return null;
        }

        return new Resolved(spec, asset, bounds.Height * (tileSize / bounds.Width), bounds);
    }

    /// <summary>
    /// Adds one tile to the list: the path point its centre sits on, the direction it is turned to and the affine
    /// map carrying its own frame onto the path.
    ///
    /// <paramref name="tangent"/> is null for a side, start or end tile, which is turned to the direction of travel
    /// where it sits; a corner tile carries the bisector of its two legs, because it faces neither of them.
    /// </summary>
    private static void Place(
        List<PatternTilePlacement> tiles,
        IReadOnlyList<Point2D> points,
        double[] lengths,
        double total,
        PatternTileKind slot,
        Resolved tile,
        double centre,
        double? tangent,
        double size)
    {
        if (tiles.Count >= MaxTiles)
        {
            return;
        }

        // A tile may overrun the end of its run, so its centre can fall past the end of the path; the point is
        // the end of the path there, which is where a tile that hangs off it is anchored.
        double position = Math.Clamp(centre, 0.0, total);
        (Point2D point, double direction) = At(points, lengths, position);
        if (tangent is { } given)
        {
            direction = given;
        }

        double across = size * tile.Spec.Scale / tile.Bounds.Width;
        double turn = direction - (Math.PI / 2.0) + (tile.Spec.RotationDegrees * Math.PI / 180.0);

        // Read outermost first, as ArtBrushPath states it: move the tile's own centre to the origin, scale it -
        // mirrored when either flip is on - turn its axes so +X lies across the path and +Y along it, and put the
        // tile's centre on the path point.
        AffineTransform transform = AffineTransform.CreateTranslation(point.X, point.Y)
            .Compose(AffineTransform.CreateRotation(turn))
            .Compose(AffineTransform.CreateScale(
                tile.Spec.FlipAcross ? -across : across,
                tile.Spec.FlipAlong ? -across : across))
            .Compose(AffineTransform.CreateTranslation(
                -(tile.Bounds.X + (tile.Bounds.Width / 2.0)),
                -(tile.Bounds.Y + (tile.Bounds.Height / 2.0))));

        tiles.Add(new PatternTilePlacement(slot, tile.Asset, position, point, direction, tile.Length, transform));
    }

    /// <summary>
    /// Whether the path turns at a node, and by how much: the direction of travel arriving there against the
    /// direction leaving it, as a signed angle in (-pi, pi]. Positive is a turn to the path's right.
    /// </summary>
    private static bool TurnAt(SubPath sub, int node, out double turn)
    {
        turn = 0.0;
        if (!DirectionOf(Incoming(sub, node), out Vector2D incoming) ||
            !DirectionOf(Outgoing(sub, node), out Vector2D outgoing))
        {
            return false;
        }

        turn = Math.Atan2(Cross(incoming, outgoing), incoming.Dot(outgoing));
        return true;
    }

    /// <summary>
    /// The direction the path is travelling as it **arrives** at a node: the incoming segment's last control point
    /// to its end, which is the node itself. A straight incoming segment has its control point on the anchor, so
    /// the segment's own chord is the answer there.
    /// </summary>
    private static Vector2D Incoming(SubPath sub, int node)
    {
        int previous = node - 1;
        if (previous < 0)
        {
            previous += sub.Nodes.Count;
        }

        CubicBezier curve = sub.GetSegment(previous % sub.SegmentCount);
        Vector2D direction = curve.P3 - curve.P2;
        return IsZero(direction) ? curve.P3 - curve.P0 : direction;
    }

    /// <summary>The direction the path is travelling as it **leaves** a node, the outgoing half of the above.</summary>
    private static Vector2D Outgoing(SubPath sub, int node)
    {
        CubicBezier curve = sub.GetSegment(node % sub.SegmentCount);
        Vector2D direction = curve.P1 - curve.P0;
        return IsZero(direction) ? curve.P3 - curve.P0 : direction;
    }

    /// <summary>
    /// The direction a corner tile is turned to: the bisector of the two legs, so it faces neither of them.
    ///
    /// A turn of a whole half-circle has no bisector - the two unit directions cancel - and the outgoing direction
    /// is the answer there, because a tile that doubled back on itself would otherwise have no direction at all.
    /// </summary>
    private static double Bisector(SubPath sub, int node)
    {
        if (!DirectionOf(Incoming(sub, node), out Vector2D incoming) ||
            !DirectionOf(Outgoing(sub, node), out Vector2D outgoing))
        {
            return 0.0;
        }

        Vector2D sum = incoming + outgoing;
        if (IsZero(sum))
        {
            sum = outgoing;
        }

        return Math.Atan2(sum.Y, sum.X);
    }

    private static bool DirectionOf(Vector2D v, out Vector2D unit)
    {
        unit = IsZero(v) ? new Vector2D(0, 0) : v / v.Length;
        return !IsZero(unit);
    }

    private static bool IsZero(Vector2D v) => v.Length < Epsilon;

    private static double Cross(Vector2D a, Vector2D b) => (a.X * b.Y) - (a.Y * b.X);

    /// <summary>
    /// The arc length at which a node's anchor sits, found in the polyline the node was flattened into.
    ///
    /// Every node's anchor **is** one of the polyline's points - the flattening adds a curve's two ends before it
    /// subdivides - so this is a lookup rather than an estimate, and the search only ever moves forward because
    /// the nodes are visited in order. A polyline that has lost the point (a node repeated at the same place, so
    /// the closing duplicate was dropped) falls back to the nearest point, which is the same place by definition.
    /// </summary>
    private static double ArcLengthOf(Point2D anchor, IReadOnlyList<Point2D> points, double[] at, ref int searchFrom)
    {
        for (int i = searchFrom; i < points.Count; i++)
        {
            if (points[i].NearlyEquals(anchor, 1e-6))
            {
                searchFrom = i;
                return at[i];
            }
        }

        int nearest = 0;
        double best = double.PositiveInfinity;
        for (int i = 0; i < points.Count; i++)
        {
            double d = Distance(points[i], anchor);
            if (d < best)
            {
                best = d;
                nearest = i;
            }
        }

        searchFrom = nearest;
        return at[nearest];
    }

    /// <summary>
    /// The point and the direction of travel at an arc length along the polyline.
    ///
    /// The direction is the **segment's**, not a blended average of the two meeting at a vertex: a tile belongs to
    /// one segment, so a tile starting on the far side of a corner points along that side rather than along a
    /// bisector neither segment runs in.
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
