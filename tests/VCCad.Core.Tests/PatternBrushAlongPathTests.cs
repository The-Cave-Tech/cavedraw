using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// A pattern brush lays a **tile set** along the path - a side tile repeated between the turns, a corner tile at
/// each turn, and one start and one end tile at the two ends (issue #101).
///
/// The assertions are on the geometry, not on the parameters: a placement says where the tile's own frame lands,
/// which way it is turned and how much of the path it covers, so what is measured here is where the tile actually
/// sits. Every tile is a document item's box in its own frame - the same relationship the art brush has with its
/// asset - so a test can place a tile's box and measure the result.
///
/// Two things are pinned that a parameter round trip could not see:
///
/// - **Corner detection reads the path's own nodes, not the flattened polyline.** A node whose incoming and
///   outgoing directions differ by more than the threshold is a corner; a smooth node's handles are collinear, so
///   a curve has no corner however tightly it bends. A test that only used a right-angled polyline could not tell
///   a node-based rule from a sample-based one, so a smooth node that doubles the path back is asserted to have
///   no corner at all.
/// - **A corner tile replaces the side tiles over its own extent**, so the run of side tiles is measured between
///   corner tiles rather than across them. That is what "the side tile fills between the corners" means, and the
///   counts below are what make it visible.
/// </summary>
public class PatternBrushAlongPathTests
{
    /// <summary>A ten by ten tile whose box starts at the origin, which is what an item's own bounds are.</summary>
    private static readonly Rect2D TenByTen = new(0, 0, 10, 10);

    /// <summary>A wide, short corner tile, so its foot along the path is visibly different from a side tile's.</summary>
    private static readonly Rect2D CornerTile = new(0, 0, 20, 10);

    private static PathItem Open(params Point2D[] points)
    {
        var path = new PathItem { Name = "line", Fill = FillSpec.None };
        SubPath sub = path.AddSubPath(closed: false);
        foreach (Point2D point in points)
        {
            sub.Nodes.Add(new PathNode(point));
        }

        path.Stroke = new StrokeSpec(true, ColorRgb.Black, 4, StrokeCap.Butt, StrokeJoin.Miter, 4);
        return path;
    }

    /// <summary>An L: a 100pt leg to the right, then a 60pt leg downwards. One right-angled turn at arc length 100.</summary>
    private static PathItem L()
        => Open(new Point2D(0, 0), new Point2D(100, 0), new Point2D(100, 60));

    // Each tile is a named asset, so a test can say **which** artwork a slot was filled with - which is the only
    // way to see the documented fallback: a corner with no corner tile is drawn with the side tile's artwork.
    private static readonly Guid Side = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Corner = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Start = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid End = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private static Rect2D? Bounds(Guid id) => id switch
    {
        var g when g == Side => TenByTen,
        var g when g == Corner => CornerTile,
        var g when g == Start => TenByTen,
        var g when g == End => TenByTen,
        _ => null,
    };

    private static IReadOnlyList<PatternTilePlacement> Place(PathItem path, BrushSpec brush, double scale = 1.0)
        => PatternBrushPath.Placements(path, brush, Bounds, scale);

    /// <summary>A side tile and nothing else: size 20 across a ten-wide tile is a scale of 2, so 20 long.</summary>
    private static BrushSpec Rail(double spacing = 0.0, double threshold = 30.0, PatternTileSpec? side = null)
        => BrushSpec.Pattern("Rail", 20, side: side ?? new PatternTileSpec(Side), spacing: spacing,
            cornerThresholdDegrees: threshold);

    /// <summary>The tile's box as the placement puts it on the path, so a test measures the tile and not the numbers.</summary>
    private static Rect2D Placed(PatternTilePlacement placement, Rect2D? box = null)
        => (box ?? TenByTen) is var b
            ? Rect2D.FromPoints(
                placement.Transform.Transform(new Point2D(b.X, b.Y)),
                placement.Transform.Transform(new Point2D(b.Right, b.Bottom)))
            : Rect2D.Empty;

    /// <summary>Where one point of the tile's own frame ends up, which is how a direction is measured.</summary>
    private static Point2D On(PatternTilePlacement placement, double x, double y)
        => placement.Transform.Transform(new Point2D(x, y));

    /// <summary>A smooth node: the two handles sit on one line through the anchor, on opposite sides of it.</summary>
    private static PathItem Smooth(params (Point2D Anchor, Point2D In, Point2D Out)[] nodes)
    {
        var path = new PathItem { Name = "curve", Fill = FillSpec.None };
        SubPath sub = path.AddSubPath(closed: false);
        foreach ((Point2D anchor, Point2D incoming, Point2D outgoing) in nodes)
        {
            sub.Nodes.Add(new PathNode(anchor, incoming, outgoing));
        }

        path.Stroke = new StrokeSpec(true, ColorRgb.Black, 4, StrokeCap.Butt, StrokeJoin.Miter, 4);
        return path;
    }

    // ---------------------------------------------------------------------------------------------------------
    // 1. The side tile along a straight path.
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void ASideTileRepeatsAlongAStraightPath()
    {
        IReadOnlyList<PatternTilePlacement> tiles = Place(Open(new Point2D(0, 0), new Point2D(100, 0)), Rail());

        // A ten-tall tile scaled to 20 across is 20 along, so a 100pt path holds five of them end to end.
        Assert.Equal(5, tiles.Count);
        Assert.Equal(new[] { 10.0, 30.0, 50.0, 70.0, 90.0 }, tiles.Select(t => t.Position).ToArray());
        Assert.All(tiles, t => Assert.Equal(20.0, t.Length, 6));
        Assert.All(tiles, t => Assert.Equal(PatternTileKind.Side, t.Slot));
        Assert.All(tiles, t => Assert.Equal(Side, t.Asset));
        Assert.All(tiles, t => Assert.Equal(0.0, t.TangentRadians, 9));

        // The band the tile covers: across the path it is the brush's own 20 wide, centred on the line, and along
        // it runs from 0 to 20 for the first tile - the tile's foot, not the stroke's 4pt width.
        Rect2D first = Placed(tiles[0]);
        Assert.Equal(0.0, first.X, 6);
        Assert.Equal(20.0, first.Width, 6);
        Assert.Equal(-10.0, first.Y, 6);
        Assert.Equal(10.0, first.Bottom, 6);

        // The last tile ends exactly where the path does.
        Assert.Equal(100.0, Placed(tiles[4]).Right, 6);
    }

    // ---------------------------------------------------------------------------------------------------------
    // 2. The corner tile at the turn, and the side tile between the turns.
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void ACornerTileSitsAtTheTurnAndTheSideTilesFillBetweenThem()
    {
        BrushSpec brush = Rail() with { PatternOuterTile = new PatternTileSpec(Corner) };

        IReadOnlyList<PatternTilePlacement> tiles = Place(L(), brush);

        // The corner tile is 20 wide and 10 tall, so at a brush size of 20 it is drawn at its own size and covers
        // ten points of the path, centred on the turn: 95 to 105. The side runs are therefore [0, 95) and
        // [105, 160), and a side tile starts wherever it still falls inside its run - 0, 20, 40, 60, 80 on the
        // first leg and 105, 125, 145 on the second - overrunning the run's end rather than being squashed into
        // it.
        Assert.Equal(9, tiles.Count);
        Assert.Equal(
            new[]
            {
                PatternTileKind.Side, PatternTileKind.Side, PatternTileKind.Side, PatternTileKind.Side,
                PatternTileKind.Side, PatternTileKind.OuterCorner,
                PatternTileKind.Side, PatternTileKind.Side, PatternTileKind.Side,
            },
            tiles.Select(t => t.Slot).ToArray());

        Assert.Equal(new[] { 10.0, 30.0, 50.0, 70.0, 90.0, 100.0, 115.0, 135.0, 155.0 },
            tiles.Select(t => t.Position).ToArray());

        // **The corner tile is exactly once, and it is at the turn.** The turn is a right-angled one at (100, 0).
        PatternTilePlacement corner = tiles.Single(t => t.Slot == PatternTileKind.OuterCorner);
        Assert.Equal(Corner, corner.Asset);
        Assert.Equal(new Point2D(100, 0), corner.Point);
        Assert.Equal(10.0, corner.Length, 6);

        // It is turned to the bisector of the two legs - halfway between travelling right and travelling down -
        // so it faces neither leg, which is what lets one tile wrap the turn.
        Assert.Equal(Math.PI / 4.0, corner.TangentRadians, 9);

        // Its own centre lands on the turn, it is drawn 20 across the path there and 10 along it, and it is turned
        // to the bisector rather than to either leg.
        Assert.Equal(new Point2D(100, 0), On(corner, 10, 5));
        Assert.Equal(20.0, (On(corner, 20, 5) - On(corner, 0, 5)).Length, 6);
        Assert.Equal(10.0, (On(corner, 10, 10) - On(corner, 10, 0)).Length, 6);

        // The artwork's own +Y runs along the bisector, and its +X across the path to the left of travel.
        Vector2D along = On(corner, 10, 10) - On(corner, 10, 0);
        Assert.Equal(Math.Cos(Math.PI / 4.0), along.X / 10.0, 6);
        Assert.Equal(Math.Sin(Math.PI / 4.0), along.Y / 10.0, 6);

        // And the second leg's side tiles are turned down the second leg, not along the first.
        Assert.All(tiles.Skip(6), t => Assert.Equal(Math.PI / 2.0, t.TangentRadians, 9));
    }

    /// <summary>
    /// **A turn to the path's right is a convex corner and takes the outer tile; a turn to the left is the
    /// concave one and takes the inner tile.** The S below turns right at the first corner and left at the
    /// second, so one of each is placed and the order says which is which.
    /// </summary>
    [Fact]
    public void AConvexCornerTakesTheOuterTileAndAConcaveOneTheInnerTile()
    {
        PathItem path = Open(
            new Point2D(0, 0), new Point2D(100, 0), new Point2D(100, 60), new Point2D(200, 60));

        BrushSpec brush = Rail() with
        {
            PatternOuterTile = new PatternTileSpec(Corner),
            PatternInnerTile = new PatternTileSpec(Corner),
        };

        PatternTilePlacement[] corners = Place(path, brush)
            .Where(t => t.Slot is PatternTileKind.OuterCorner or PatternTileKind.InnerCorner)
            .ToArray();

        Assert.Equal(2, corners.Length);
        Assert.Equal(PatternTileKind.OuterCorner, corners[0].Slot);
        Assert.Equal(new Point2D(100, 0), corners[0].Point);
        Assert.Equal(PatternTileKind.InnerCorner, corners[1].Slot);
        Assert.Equal(new Point2D(100, 60), corners[1].Point);
    }

    // ---------------------------------------------------------------------------------------------------------
    // 3. Start and end tiles.
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheStartAndEndTilesAppearOnceAtTheRightEnds()
    {
        BrushSpec brush = Rail() with
        {
            PatternStartTile = new PatternTileSpec(Start),
            PatternEndTile = new PatternTileSpec(End),
        };

        IReadOnlyList<PatternTilePlacement> tiles = Place(Open(new Point2D(0, 0), new Point2D(100, 0)), brush);

        Assert.Equal(5, tiles.Count);
        Assert.Equal(
            new[]
            {
                PatternTileKind.Start, PatternTileKind.Side, PatternTileKind.Side, PatternTileKind.Side,
                PatternTileKind.End,
            },
            tiles.Select(t => t.Slot).ToArray());

        // One each, at the two ends of the path, and the side tiles fill what is left between them.
        PatternTilePlacement start = tiles[0];
        Assert.Equal(Start, start.Asset);
        Assert.Equal(new Point2D(10, 0), start.Point);
        Assert.Equal(0.0, Placed(start).X, 6);
        Assert.Equal(20.0, Placed(start).Right, 6);

        PatternTilePlacement end = tiles[^1];
        Assert.Equal(End, end.Asset);
        Assert.Equal(new Point2D(90, 0), end.Point);
        Assert.Equal(80.0, Placed(end).X, 6);
        Assert.Equal(100.0, Placed(end).Right, 6);
    }

    /// <summary>A closed path has no ends, so neither an end tile nor a start tile is placed on one.</summary>
    [Fact]
    public void AClosedPathHasNoEndsToPutStartAndEndTilesOn()
    {
        var path = new PathItem { Name = "box", Fill = FillSpec.None };
        SubPath sub = path.AddSubPath(closed: true);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(100, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(100, 100)));
        sub.Nodes.Add(new PathNode(new Point2D(0, 100)));
        path.Stroke = new StrokeSpec(true, ColorRgb.Black, 4, StrokeCap.Butt, StrokeJoin.Miter, 4);

        BrushSpec brush = Rail() with
        {
            PatternStartTile = new PatternTileSpec(Start),
            PatternEndTile = new PatternTileSpec(End),
        };

        IReadOnlyList<PatternTilePlacement> tiles = Place(path, brush);

        Assert.NotEmpty(tiles);
        Assert.DoesNotContain(tiles, t => t.Slot is PatternTileKind.Start or PatternTileKind.End);
    }

    // ---------------------------------------------------------------------------------------------------------
    // 4. Corner detection, and what it does with a curve.
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// **The honest half: a smooth curve has no corner.** The middle node's handles are collinear, so the path
    /// passes through it without changing direction - it is stated in the path's own geometry, not guessed at
    /// from the flattening. The same path is asserted with the handles left straight and with a bend, and both
    /// answer with side tiles only.
    /// </summary>
    [Fact]
    public void ASmoothCurveHasNoCornerHoweverTightlyItBends()
    {
        // A gentle arc: the middle node's handles run along the +X axis.
        PathItem gentle = Smooth(
            (new Point2D(0, 0), new Point2D(0, 0), new Point2D(0, 0)),
            (new Point2D(50, 20), new Point2D(40, 20), new Point2D(60, 20)),
            (new Point2D(100, 0), new Point2D(100, 0), new Point2D(100, 0)));

        Assert.DoesNotContain(Place(gentle, Rail()), t => t.Slot is not PatternTileKind.Side);

        // A hairpin: the path runs right, doubles back through the node and returns. It is as sharp as a path
        // gets, and it still has no corner, because the node's handles are collinear - the turn happens across
        // the segments rather than at the node.
        PathItem hairpin = Smooth(
            (new Point2D(0, 0), new Point2D(0, 0), new Point2D(0, 0)),
            (new Point2D(100, 0), new Point2D(90, 0), new Point2D(110, 0)),
            (new Point2D(0, 20), new Point2D(0, 20), new Point2D(0, 20)));

        Assert.DoesNotContain(Place(hairpin, Rail()), t => t.Slot is not PatternTileKind.Side);
    }

    /// <summary>
    /// **The other half of the same rule: the node's own handles decide, not the samples.** Broken handles at the
    /// same node are a corner, and the corner tile is placed there.
    /// </summary>
    [Fact]
    public void BrokenHandlesAtANodeAreACorner()
    {
        PathItem broken = Smooth(
            (new Point2D(0, 0), new Point2D(0, 0), new Point2D(0, 0)),
            (new Point2D(50, 20), new Point2D(40, 20), new Point2D(50, 30)),
            (new Point2D(100, 0), new Point2D(100, 0), new Point2D(100, 0)));

        BrushSpec brush = Rail() with
        {
            PatternOuterTile = new PatternTileSpec(Corner),
            PatternInnerTile = new PatternTileSpec(Corner),
        };

        PatternTilePlacement[] corners = Place(broken, brush)
            .Where(t => t.Slot is PatternTileKind.OuterCorner or PatternTileKind.InnerCorner)
            .ToArray();

        // Incoming is +X and outgoing is +Y, so it is a right turn: one corner, of the outer kind, at the node.
        // The arc length of the turn is where the node was flattened, not the node's x - the segment arriving at it
        // is a curve, so it is longer than the straight line between its ends.
        PatternTilePlacement corner = Assert.Single(corners);
        Assert.Equal(PatternTileKind.OuterCorner, corner.Slot);
        Assert.Equal(new Point2D(50, 20), corner.Point);
        Assert.True(corner.Position > 50.0, $"the turn is past the straight-line distance: {corner.Position}");
    }

    /// <summary>
    /// The threshold is what decides: the same 20-degree turn is not a corner at the default 30 and is one at 10.
    /// A detection that ignored the threshold could not answer both.
    /// </summary>
    [Fact]
    public void TheCornerThresholdDecidesWhatCountsAsACorner()
    {
        double radians = 20.0 * Math.PI / 180.0;
        PathItem path = Open(
            new Point2D(0, 0),
            new Point2D(100, 0),
            new Point2D(100 + (50 * Math.Cos(radians)), 50 * Math.Sin(radians)));

        Assert.DoesNotContain(Place(path, Rail(threshold: 30.0)), t => t.Slot is not PatternTileKind.Side);

        PatternTilePlacement[] corners = Place(path, Rail(threshold: 10.0))
            .Where(t => t.Slot is not PatternTileKind.Side)
            .ToArray();

        PatternTilePlacement corner = Assert.Single(corners);
        Assert.Equal(PatternTileKind.OuterCorner, corner.Slot);
        Assert.Equal(100.0, corner.Position, 6);
    }

    // ---------------------------------------------------------------------------------------------------------
    // 5. Falling back, spacing, scale, flip and rotation.
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// **The documented fallback: no corner tile means the side tile is used at the corner.** The slot is still
    /// the corner's, so a reader can see that a turn was recognised, and the artwork is named - the side tile's -
    /// so a reader can see what filled it.
    /// </summary>
    [Fact]
    public void NoCornerTileFallsBackToTheSideTile()
    {
        IReadOnlyList<PatternTilePlacement> tiles = Place(L(), Rail());

        PatternTilePlacement corner = Assert.Single(tiles, t => t.Slot == PatternTileKind.OuterCorner);
        Assert.Equal(Side, corner.Asset);
        Assert.Equal(new Point2D(100, 0), corner.Point);
        Assert.Equal(20.0, corner.Length, 6);

        // With no corner tile of its own the slot is filled by the side tile's own 20pt foot, so the side runs
        // are [0, 90) and [110, 160) - which is a different layout from the 10pt corner tile above, and the
        // difference is exactly the point of the fallback.
        Assert.Equal(new[] { 10.0, 30.0, 50.0, 70.0, 90.0, 100.0, 120.0, 140.0, 160.0 },
            tiles.Select(t => t.Position).ToArray());
    }

    /// <summary>The spacing is the gap between side tiles, so changing it changes how many fit.</summary>
    [Fact]
    public void ChangingTheSpacingChangesTheSideTileCount()
    {
        PathItem path = Open(new Point2D(0, 0), new Point2D(100, 0));

        Assert.Equal(5, Place(path, Rail(spacing: 0.0)).Count);
        Assert.Equal(4, Place(path, Rail(spacing: 5.0)).Count);

        Assert.Equal(new[] { 10.0, 35.0, 60.0, 85.0 }, Place(path, Rail(spacing: 5.0))
            .Select(t => t.Position).ToArray());
    }

    /// <summary>A tile's own scale multiplies the brush's size for that tile and no other.</summary>
    [Fact]
    public void ATilesOwnScaleResizesThatTileOnly()
    {
        PathItem path = Open(new Point2D(0, 0), new Point2D(100, 0));

        BrushSpec brush = Rail() with { PatternStartTile = new PatternTileSpec(Start, Scale: 0.5) };

        IReadOnlyList<PatternTilePlacement> tiles = Place(path, brush);

        // The start tile is half the size, so its foot is 10 rather than 20, and the side tiles begin after it.
        Assert.Equal(10.0, tiles[0].Length, 6);
        Assert.Equal(5.0, tiles[0].Position, 6);
        Assert.Equal(20.0, tiles[1].Length, 6);
        Assert.Equal(20.0, tiles[1].Position, 6);
    }

    /// <summary>The two flips and the rotation are the tile's own, and they turn its artwork and not the path's.</summary>
    [Fact]
    public void ATilesFlipAndRotationReachItsPlacement()
    {
        PathItem path = Open(new Point2D(0, 0), new Point2D(100, 0));

        PatternTilePlacement plain = Place(path, Rail())[0];
        PatternTilePlacement across = Place(path, Rail(side: new PatternTileSpec(Side, FlipAcross: true)))[0];
        PatternTilePlacement turned =
            Place(path, Rail(side: new PatternTileSpec(Side, RotationDegrees: 90.0)))[0];

        // The tile's own left-centre sits on one side of the path, and the flip puts it on the other.
        Assert.Equal(10.0, On(plain, 0, 5).Y, 6);
        Assert.Equal(-10.0, On(across, 0, 5).Y, 6);

        // A quarter turn takes the tile's along-path axis and lays it across the path: the art is turned, but the
        // band it occupies is the same 20 points of the path, because the band is the tile's foot and not the art.
        Point2D plainBottom = On(plain, 5, 10);
        Assert.Equal(20.0, plainBottom.X, 9);
        Assert.Equal(0.0, plainBottom.Y, 9);

        Point2D turnedBottom = On(turned, 5, 10);
        Assert.Equal(10.0, turnedBottom.X, 9);
        Assert.Equal(10.0, turnedBottom.Y, 9);
        Assert.Equal(20.0, turned.Length, 6);
        Assert.Equal(10.0, turned.Position, 6);
    }

    // ---------------------------------------------------------------------------------------------------------
    // 6. Nothing to place, and the other kinds.
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Nothing to place, nothing placed: a brush with no tiles, a slot naming an item the document does not
    /// have, a brush with no size and a path with one point each answer with no tiles rather than invented ones.
    /// </summary>
    [Fact]
    public void NothingToPlacePlacesNothing()
    {
        PathItem path = Open(new Point2D(0, 0), new Point2D(100, 0));

        Assert.Empty(Place(path, BrushSpec.Pattern("Bare", 20)));
        Assert.Empty(Place(path, Rail() with { PatternSideTile = new PatternTileSpec(Guid.NewGuid()) }));
        Assert.Empty(Place(path, BrushSpec.Pattern("Tiny", 0, side: new PatternTileSpec(Side))));
        Assert.Empty(Place(Open(new Point2D(5, 5)), Rail()));
    }

    /// <summary>The other two kinds place no pattern tiles: this seam answers for a pattern brush only.</summary>
    [Fact]
    public void ACalligraphicOrArtBrushPlacesNoPatternTiles()
    {
        PathItem path = Open(new Point2D(0, 0), new Point2D(100, 0));

        Assert.Empty(Place(path, BrushSpec.Calligraphic("Chisel", 35, 0.2, 24)));
        Assert.Empty(Place(path, BrushSpec.Art("Vine", Side, 20)));
    }

    /// <summary>The renderer's own scale reaches the tiles the way it reaches a nib.</summary>
    [Fact]
    public void TheRenderersScaleReachesTheTiles()
    {
        PathItem path = Open(new Point2D(0, 0), new Point2D(100, 0));

        IReadOnlyList<PatternTilePlacement> plain = Place(path, Rail());
        IReadOnlyList<PatternTilePlacement> doubled = Place(path, Rail(), scale: 2.0);

        Assert.Equal(20.0, plain[0].Length, 6);
        Assert.Equal(40.0, doubled[0].Length, 6);
        Assert.Equal(5, plain.Count);

        // At the doubled size a tile's foot is 40, so a 100pt path holds three of them - the third overrunning the
        // end, exactly as the first does at the smaller size.
        Assert.Equal(3, doubled.Count);
        Assert.Equal(new[] { 20.0, 60.0, 100.0 }, doubled.Select(t => t.Position).ToArray());
        Assert.Equal(40.0, Placed(doubled[0]).Height, 6);
    }
}
