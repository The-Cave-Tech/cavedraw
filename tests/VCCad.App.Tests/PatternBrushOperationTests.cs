using System.Text.Json;
using VCCad.App.Automation;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Core.Serialization;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// The pattern brush through the one operation registry: creating one of the new kind, assigning each of its five
/// tiles, setting the set's spacing and corner threshold, applying it, reading it back and reading where its tiles
/// go (issue #101).
///
/// The operations are the only place a person and a driver can act, so the assertions here are about **reach**: a
/// member that can be set through the registry, a report that says which kind a brush is and which slots it fills,
/// and a tile readout that turns "the tiles follow the path" into numbers a caller with no eyes can check. The
/// geometry itself is pinned in <c>PatternBrushAlongPathTests</c>; what is pinned here is that the registry reaches
/// it, including the corner case, which is the half a parameter round trip could not see.
/// </summary>
public class PatternBrushOperationTests
{
    private static JsonElement Params(object value) => JsonSerializer.SerializeToElement(value);

    /// <summary>A ten by ten square of artwork on the pasteboard, which is what a pattern brush's tile is.</summary>
    private static PathItem Tile(EditorViewModel vm, string name = "tile")
    {
        var tile = new PathItem { Name = name, Fill = FillSpec.None };
        SubPath sub = tile.AddSubPath(closed: true);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(10, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(10, 10)));
        sub.Nodes.Add(new PathNode(new Point2D(0, 10)));
        vm.Document.Orphans.AddItem(tile);
        return tile;
    }

    /// <summary>A 60pt line, with the selection on it - what every test below brushes.</summary>
    private static (AutomationContext Context, CadDocument Document, PathItem Path) Host()
    {
        var vm = new EditorViewModel();
        var path = new PathItem { Name = "line", Fill = FillSpec.None };
        SubPath sub = path.AddSubPath(closed: false);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(60, 0)));
        path.Stroke = new StrokeSpec(true, ColorRgb.Black, 4, StrokeCap.Butt, StrokeJoin.Miter, 4);
        vm.Document.Artboards[0].Layers[0].AddItem(path);
        vm.SelectObject(path);

        return (new AutomationContext { ViewModel = vm }, vm.Document, path);
    }

    private static JsonElement[] Tiles(AutomationContext context, string brush)
        => JsonSerializer.Deserialize<JsonElement[]>(
            JsonSerializer.Serialize(
                EditorOperations.Invoke(context, "brush.tiles", Params(new { name = brush }))))!;

    private static void Move(PathItem path, int index, Point2D to)
    {
        PathNode node = path.SubPaths[0].Nodes[index];
        node.Anchor = to;
        node.InHandle = to;
        node.OutHandle = to;
    }

    /// <summary>Turns the two-node line into an L: a leg to <paramref name="corner"/>, then a leg to the end.</summary>
    private static void Bend(PathItem path, Point2D corner, Point2D end)
    {
        Move(path, 1, end);
        path.SubPaths[0].Nodes.Insert(1, new PathNode(corner));
    }

    [Fact]
    public void APatternBrushCanBeCreatedListedAndApplied()
    {
        (AutomationContext context, _, PathItem path) = Host();
        PathItem tile = Tile(context.ViewModel);

        EditorOperations.Invoke(context, "brush.create",
            Params(new { name = "Rail", kind = "pattern", side = tile.Id, size = 20, spacing = 2.5, cornerThreshold = 15 }));

        string list = JsonSerializer.Serialize(EditorOperations.Invoke(context, "brush.list", default));
        Assert.Contains("\"kind\":\"pattern\"", list, StringComparison.Ordinal);
        Assert.Contains(tile.Id.ToString(), list, StringComparison.Ordinal);
        Assert.Contains("\"spacing\":2.5", list, StringComparison.Ordinal);
        Assert.Contains("\"cornerThreshold\":15", list, StringComparison.Ordinal);

        // Every slot is reported, filled or not, so "the file filled no corner slot" is visible rather than absent.
        JsonElement pattern = JsonSerializer.SerializeToElement(
            EditorOperations.Invoke(context, "brush.list", default))[0].GetProperty("pattern");
        Assert.Equal(tile.Id, pattern.GetProperty("side").GetProperty("asset").GetGuid());
        Assert.Equal(JsonValueKind.Null, pattern.GetProperty("start").ValueKind);
        Assert.Equal(JsonValueKind.Null, pattern.GetProperty("innerCorner").ValueKind);

        // Creating it does not apply it: an asset sits in the document until something uses it.
        Assert.False(path.Stroke.HasBrush);

        EditorOperations.Invoke(context, "brush.apply", Params(new { name = "Rail" }));

        Assert.True(path.Stroke.HasBrush);
        Assert.Equal(BrushKind.Pattern, path.Stroke.Brush!.Kind);
        Assert.True(path.Stroke.Brush.IsPattern);
        Assert.False(path.Stroke.Brush.IsNib);
        Assert.Equal(20.0, path.Stroke.Brush.Diameter, 6);
        Assert.Equal(2.5, path.Stroke.Brush.PatternSpacing, 6);
    }

    /// <summary>
    /// **The geometry is reachable.** A 60pt line with a 20pt side tile holds three of them, and the readout says
    /// where each one's centre is and which way it is turned, without anyone having to see the drawing.
    /// </summary>
    [Fact]
    public void TheTileReadoutSaysWhereEachTileGoes()
    {
        (AutomationContext context, _, _) = Host();
        PathItem tile = Tile(context.ViewModel);

        EditorOperations.Invoke(context, "brush.create",
            Params(new { name = "Rail", kind = "pattern", side = tile.Id, size = 20 }));
        EditorOperations.Invoke(context, "brush.apply", Params(new { name = "Rail" }));

        JsonElement[] read = Tiles(context, "Rail");
        JsonElement placements = read[0].GetProperty("tiles");

        Assert.Equal(3, placements.GetArrayLength());
        Assert.Equal(new[] { "side", "side", "side" },
            placements.EnumerateArray().Select(t => t.GetProperty("slot").GetString()).ToArray());
        Assert.Equal(new[] { 10.0, 30.0, 50.0 },
            placements.EnumerateArray().Select(t => t.GetProperty("position").GetDouble()).ToArray());
        Assert.All(placements.EnumerateArray(), t => Assert.Equal(20.0, t.GetProperty("length").GetDouble(), 6));
        Assert.Equal("tile", placements[0].GetProperty("assetName").GetString());
    }

    /// <summary>
    /// **The corner case through the registry.** A right-angled path puts a corner tile at the turn and the side
    /// tiles on either side of it - which is the assertion the issue asks for by name, made about the positions
    /// the registry reports.
    /// </summary>
    [Fact]
    public void ARightAngledPathUsesTheCornerTileOnceAndTheSideTilesBetween()
    {
        (AutomationContext context, _, PathItem path) = Host();
        PathItem side = Tile(context.ViewModel, "side");
        PathItem corner = Tile(context.ViewModel, "corner");

        // The path becomes an L: a 60pt leg to the right and a 40pt leg down, with the turn at (60, 0).
        Bend(path, new Point2D(60, 0), new Point2D(60, 40));

        EditorOperations.Invoke(context, "brush.create",
            Params(new
            {
                name = "Rail",
                kind = "pattern",
                side = side.Id,
                outerCorner = corner.Id,
                innerCorner = corner.Id,
                size = 20,
            }));
        EditorOperations.Invoke(context, "brush.apply", Params(new { name = "Rail" }));

        JsonElement[] tiles = Tiles(context, "Rail")[0].GetProperty("tiles").EnumerateArray().ToArray();

        JsonElement[] corners = tiles
            .Where(t => t.GetProperty("slot").GetString() != "side")
            .ToArray();

        JsonElement atTheTurn = Assert.Single(corners);
        Assert.Equal("outerCorner", atTheTurn.GetProperty("slot").GetString());
        Assert.Equal(corner.Id, atTheTurn.GetProperty("asset").GetGuid());

        // The turn is where the two legs meet, and the corner tile is turned to the bisector of them - 45 degrees,
        // seeing as the path arrives travelling right and leaves travelling down the screen.
        Assert.Equal(60.0, atTheTurn.GetProperty("x").GetDouble(), 4);
        Assert.Equal(0.0, atTheTurn.GetProperty("y").GetDouble(), 4);
        Assert.Equal(45.0, atTheTurn.GetProperty("tangentDegrees").GetDouble(), 4);

        // Side tiles on both sides of it, and not on top of it: the row is sorted along the path and the corner
        // sits between the two runs.
        Assert.True(tiles.Length > 3, $"the L should hold side tiles too, and it holds {tiles.Length}");
        int at = Array.FindIndex(tiles, t => t.GetProperty("slot").GetString() != "side");
        Assert.Equal("side", tiles[at - 1].GetProperty("slot").GetString());
        Assert.Equal("side", tiles[at + 1].GetProperty("slot").GetString());
    }

    /// <summary>
    /// **The documented fallback, through the registry**: a brush with no corner tile fills the corner slot with
    /// the side tile's artwork, and the readout says both things - the slot that was filled and what filled it.
    /// </summary>
    [Fact]
    public void ACornerSlotWithNoCornerTileFallsBackToTheSideTile()
    {
        (AutomationContext context, _, PathItem path) = Host();
        PathItem side = Tile(context.ViewModel, "side");

        Bend(path, new Point2D(60, 0), new Point2D(60, 40));

        EditorOperations.Invoke(context, "brush.create",
            Params(new { name = "Rail", kind = "pattern", side = side.Id, size = 20 }));
        EditorOperations.Invoke(context, "brush.apply", Params(new { name = "Rail" }));

        JsonElement[] tiles = Tiles(context, "Rail")[0].GetProperty("tiles").EnumerateArray().ToArray();
        JsonElement corner = Assert.Single(tiles, t => t.GetProperty("slot").GetString() is "outerCorner" or "innerCorner");

        Assert.Equal("outerCorner", corner.GetProperty("slot").GetString());
        Assert.Equal(side.Id, corner.GetProperty("asset").GetGuid());
    }

    /// <summary>
    /// **The tile readout follows the path.** Editing the path changes what the operation answers with, and the
    /// brush on the stroke is untouched by the edit, which is what makes the tiles a property of the stroke rather
    /// than artwork pasted along the line.
    /// </summary>
    [Fact]
    public void TheTileReadoutFollowsThePathWithoutTheBrushBeingReapplied()
    {
        (AutomationContext context, _, PathItem path) = Host();
        PathItem tile = Tile(context.ViewModel);

        EditorOperations.Invoke(context, "brush.create",
            Params(new { name = "Rail", kind = "pattern", side = tile.Id, size = 20 }));
        EditorOperations.Invoke(context, "brush.apply", Params(new { name = "Rail" }));

        JsonElement before = Tiles(context, "Rail")[0].GetProperty("tiles")[0];
        BrushSpec applied = path.Stroke.Brush!;

        // The person drags the far end of the line up and back, and nothing else happens.
        Move(path, 1, new Point2D(30, 40));

        JsonElement after = Tiles(context, "Rail")[0].GetProperty("tiles")[0];
        Assert.Equal(applied, path.Stroke.Brush);
        Assert.NotEqual(before.GetProperty("tangentDegrees").GetDouble(),
            after.GetProperty("tangentDegrees").GetDouble());
        Assert.Equal(Math.Atan2(40, 30) * 180.0 / Math.PI, after.GetProperty("tangentDegrees").GetDouble(), 4);
    }

    /// <summary>
    /// **A corner with no tile is not a corner this brush can draw, and the registry says so** rather than
    /// answering with tiles that do not exist. A nib and an art brush are refused by name too, and each is pointed
    /// at the operation that does answer for it.
    /// </summary>
    [Fact]
    public void TheTileReadoutRefusesABrushWithNoTileSet()
    {
        (AutomationContext context, _, _) = Host();
        PathItem tile = Tile(context.ViewModel);

        EditorOperations.Invoke(context, "brush.create", Params(new { name = "Chisel", roundness = 0.3 }));
        EditorOperationException nib = Assert.Throws<EditorOperationException>(
            () => EditorOperations.Invoke(context, "brush.tiles", Params(new { name = "Chisel" })));
        Assert.Contains("no tile set", nib.Message, StringComparison.OrdinalIgnoreCase);

        EditorOperations.Invoke(context, "brush.create",
            Params(new { name = "Vine", kind = "art", asset = tile.Id, size = 20 }));
        EditorOperationException art = Assert.Throws<EditorOperationException>(
            () => EditorOperations.Invoke(context, "brush.tiles", Params(new { name = "Vine" })));
        Assert.Contains("brush.placements", art.Message, StringComparison.Ordinal);

        // And the art readout points the other way, at the pattern operation.
        EditorOperations.Invoke(context, "brush.create",
            Params(new { name = "Rail", kind = "pattern", side = tile.Id, size = 20 }));
        EditorOperationException pattern = Assert.Throws<EditorOperationException>(
            () => EditorOperations.Invoke(context, "brush.placements", Params(new { name = "Rail" })));
        Assert.Contains("brush.tiles", pattern.Message, StringComparison.Ordinal);
    }

    /// <summary>A slot naming an item the document does not have is refused where it is named, not stored.</summary>
    [Fact]
    public void ASlotNamingAnItemTheDocumentDoesNotHaveIsRefused()
    {
        (AutomationContext context, CadDocument document, _) = Host();
        var missing = Guid.NewGuid();

        EditorOperationException create = Assert.Throws<EditorOperationException>(
            () => EditorOperations.Invoke(context, "brush.create",
                Params(new { name = "Rail", kind = "pattern", side = missing, size = 20 })));
        Assert.Contains(missing.ToString(), create.Message, StringComparison.Ordinal);
        Assert.Empty(document.Brushes);

        PathItem tile = Tile(context.ViewModel);
        EditorOperations.Invoke(context, "brush.create",
            Params(new { name = "Rail", kind = "pattern", side = tile.Id, size = 20 }));

        EditorOperationException set = Assert.Throws<EditorOperationException>(
            () => EditorOperations.Invoke(context, "brush.setTile",
                Params(new { name = "Rail", slot = "outerCorner", asset = missing })));
        Assert.Contains(missing.ToString(), set.Message, StringComparison.Ordinal);
        Assert.Null(document.FindBrush("Rail")!.PatternOuterTile);

        // A slot whose artwork is deleted afterwards is named by the readout rather than answered with nothing.
        EditorOperations.Invoke(context, "brush.setTile",
            Params(new { name = "Rail", slot = "outerCorner", asset = tile.Id }));
        document.Orphans.RemoveItem(tile);

        EditorOperationException gone = Assert.Throws<EditorOperationException>(
            () => EditorOperations.Invoke(context, "brush.tiles", Params(new { name = "Rail" })));
        Assert.Contains(tile.Id.ToString(), gone.Message, StringComparison.Ordinal);
    }

    /// <summary>Each of the five slots is assigned by name, and a slot nobody touched keeps what it had.</summary>
    [Fact]
    public void EachSlotIsAssignedOnItsOwnAndTheOthersAreLeftAlone()
    {
        (AutomationContext context, CadDocument document, PathItem path) = Host();
        PathItem side = Tile(context.ViewModel, "side");
        PathItem start = Tile(context.ViewModel, "start");
        PathItem end = Tile(context.ViewModel, "end");
        PathItem inner = Tile(context.ViewModel, "inner");
        PathItem outer = Tile(context.ViewModel, "outer");

        EditorOperations.Invoke(context, "brush.create",
            Params(new { name = "Rail", kind = "pattern", side = side.Id, size = 20 }));
        EditorOperations.Invoke(context, "brush.apply", Params(new { name = "Rail" }));

        EditorOperations.Invoke(context, "brush.setTile", Params(new { name = "Rail", slot = "start", asset = start.Id }));
        EditorOperations.Invoke(context, "brush.setTile", Params(new { name = "Rail", slot = "end", asset = end.Id }));
        EditorOperations.Invoke(context, "brush.setTile", Params(new { name = "Rail", slot = "innerCorner", asset = inner.Id }));
        EditorOperations.Invoke(context, "brush.setTile", Params(new { name = "Rail", slot = "outerCorner", asset = outer.Id }));

        BrushSpec brush = document.FindBrush("Rail")!;
        Assert.Equal(side.Id, brush.PatternSideTile!.Asset);
        Assert.Equal(start.Id, brush.PatternStartTile!.Asset);
        Assert.Equal(end.Id, brush.PatternEndTile!.Asset);
        Assert.Equal(inner.Id, brush.PatternInnerTile!.Asset);
        Assert.Equal(outer.Id, brush.PatternOuterTile!.Asset);

        // The strokes that use the brush were re-pointed with it: a brush is an asset, not a copy.
        Assert.Equal(outer.Id, path.Stroke.Brush!.PatternOuterTile!.Asset);

        // And a tile's own controls are set without disturbing the slot next door.
        EditorOperations.Invoke(context, "brush.setTile",
            Params(new { name = "Rail", slot = "start", flipAlong = true, rotation = 30, scale = 0.5 }));

        PatternTileSpec flipped = document.FindBrush("Rail")!.PatternStartTile!;
        Assert.Equal(start.Id, flipped.Asset);
        Assert.True(flipped.FlipAlong);
        Assert.False(flipped.FlipAcross);
        Assert.Equal(30.0, flipped.RotationDegrees, 6);
        Assert.Equal(0.5, flipped.Scale, 6);
        Assert.False(document.FindBrush("Rail")!.PatternEndTile!.FlipAlong);
    }

    /// <summary>
    /// A tile's own rotation turns the **artwork** and not its foot: the placement's transform is turned and the
    /// length of path it covers is unchanged - which is a claim only the geometry can settle.
    /// </summary>
    [Fact]
    public void ATilesOwnRotationReachesTheGeometryAndNotTheFoot()
    {
        (AutomationContext context, _, _) = Host();
        PathItem tile = Tile(context.ViewModel);

        EditorOperations.Invoke(context, "brush.create",
            Params(new { name = "Rail", kind = "pattern", side = tile.Id, size = 20 }));
        EditorOperations.Invoke(context, "brush.apply", Params(new { name = "Rail" }));

        JsonElement plain = Tiles(context, "Rail")[0].GetProperty("tiles")[0];
        EditorOperations.Invoke(context, "brush.setTile",
            Params(new { name = "Rail", slot = "side", rotation = 90 }));

        JsonElement turned = Tiles(context, "Rail")[0].GetProperty("tiles")[0];

        Assert.Equal(plain.GetProperty("length").GetDouble(), turned.GetProperty("length").GetDouble(), 6);
        Assert.Equal(plain.GetProperty("position").GetDouble(), turned.GetProperty("position").GetDouble(), 6);

        // The transform's first column is the tile's own +X axis where it lands, and its second is +Y. Across the
        // path and along it at no rotation; the other way round at a quarter turn - the art turning, the foot
        // staying put. The length is the scale the tile is drawn at, its own 10pt box taken to the brush's 20.
        JsonElement plainTransform = plain.GetProperty("transform");
        JsonElement turnedTransform = turned.GetProperty("transform");

        Assert.Equal(0.0, plainTransform[0].GetDouble(), 6);
        Assert.Equal(-2.0, plainTransform[1].GetDouble(), 6);
        Assert.Equal(2.0, plainTransform[2].GetDouble(), 6);
        Assert.Equal(0.0, plainTransform[3].GetDouble(), 6);

        Assert.Equal(2.0, turnedTransform[0].GetDouble(), 6);
        Assert.Equal(0.0, turnedTransform[1].GetDouble(), 6);
        Assert.Equal(0.0, turnedTransform[2].GetDouble(), 6);
        Assert.Equal(2.0, turnedTransform[3].GetDouble(), 6);
    }

    /// <summary>Clearing a slot empties it, and an empty corner slot goes back to the side tile's fallback.</summary>
    [Fact]
    public void ClearingASlotEmptiesIt()
    {
        (AutomationContext context, CadDocument document, _) = Host();
        PathItem side = Tile(context.ViewModel, "side");
        PathItem outer = Tile(context.ViewModel, "outer");

        EditorOperations.Invoke(context, "brush.create",
            Params(new { name = "Rail", kind = "pattern", side = side.Id, outerCorner = outer.Id, size = 20 }));

        JsonElement cleared = JsonSerializer.SerializeToElement(
            EditorOperations.Invoke(context, "brush.setTile",
                Params(new { name = "Rail", slot = "outerCorner", asset = (Guid?)null })));

        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("tile").ValueKind);
        Assert.Null(document.FindBrush("Rail")!.PatternOuterTile);
        Assert.Equal(side.Id, document.FindBrush("Rail")!.PatternSideTile!.Asset);

        // A slot that holds nothing and is asked to hold nothing is a mistake, not a silent no-op.
        EditorOperationException empty = Assert.Throws<EditorOperationException>(
            () => EditorOperations.Invoke(context, "brush.setTile",
                Params(new { name = "Rail", slot = "start", flipAcross = true })));
        Assert.Contains("holds nothing", empty.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A slot this build does not have is refused by name rather than quietly going somewhere else.</summary>
    [Fact]
    public void AnUnknownSlotIsRefusedByName()
    {
        (AutomationContext context, _, _) = Host();
        PathItem side = Tile(context.ViewModel);

        EditorOperations.Invoke(context, "brush.create",
            Params(new { name = "Rail", kind = "pattern", side = side.Id, size = 20 }));

        EditorOperationException error = Assert.Throws<EditorOperationException>(
            () => EditorOperations.Invoke(context, "brush.setTile",
                Params(new { name = "Rail", slot = "middle", asset = side.Id })));

        Assert.Contains("middle", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("side", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Editing the set's spacing and threshold reaches the brush and every stroke that named it.</summary>
    [Fact]
    public void TheSpacingAndCornerThresholdAreSettableThroughBrushSet()
    {
        (AutomationContext context, CadDocument document, PathItem path) = Host();
        PathItem side = Tile(context.ViewModel);

        EditorOperations.Invoke(context, "brush.create",
            Params(new { name = "Rail", kind = "pattern", side = side.Id, size = 20 }));
        EditorOperations.Invoke(context, "brush.apply", Params(new { name = "Rail" }));

        JsonElement[] before = Tiles(context, "Rail")[0].GetProperty("tiles").EnumerateArray().ToArray();
        Assert.Equal(3, before.Length);

        EditorOperations.Invoke(context, "brush.set",
            Params(new { name = "Rail", spacing = 10, cornerThreshold = 45 }));

        Assert.Equal(10.0, document.FindBrush("Rail")!.PatternSpacing, 6);
        Assert.Equal(45.0, document.FindBrush("Rail")!.PatternCornerThresholdDegrees, 6);
        Assert.Equal(10.0, path.Stroke.Brush!.PatternSpacing, 6);

        // A 30pt pitch on a 60pt line holds two tiles rather than three, which is the change the person asked for.
        JsonElement[] after = Tiles(context, "Rail")[0].GetProperty("tiles").EnumerateArray().ToArray();
        Assert.Equal(2, after.Length);

        // A member nobody gave keeps what it had.
        EditorOperations.Invoke(context, "brush.set", Params(new { name = "Rail", spacing = 5 }));
        Assert.Equal(45.0, document.FindBrush("Rail")!.PatternCornerThresholdDegrees, 6);
    }

    /// <summary>A pattern brush is a document asset like any other, so it survives a save and a reload.</summary>
    [Fact]
    public void ThePatternBrushSurvivesSaveAndReload()
    {
        (AutomationContext context, CadDocument document, PathItem path) = Host();
        PathItem side = Tile(context.ViewModel, "side");
        PathItem corner = Tile(context.ViewModel, "corner");

        EditorOperations.Invoke(context, "brush.create",
            Params(new
            {
                name = "Rail",
                kind = "pattern",
                side = side.Id,
                outerCorner = corner.Id,
                size = 20,
                spacing = 4,
                cornerThreshold = 20,
            }));
        EditorOperations.Invoke(context, "brush.apply", Params(new { name = "Rail" }));
        EditorOperations.Invoke(context, "brush.setTile",
            Params(new { name = "Rail", slot = "outerCorner", flipAcross = true, rotation = 90, scale = 2 }));

        CadDocument reloaded = VccadDocumentSerializer.Deserialize(
            VccadDocumentSerializer.SerializeToBytes(document));

        BrushSpec brush = Assert.Single(reloaded.Brushes);
        Assert.Equal(BrushKind.Pattern, brush.Kind);
        Assert.Equal(4.0, brush.PatternSpacing, 6);
        Assert.Equal(20.0, brush.PatternCornerThresholdDegrees, 6);
        Assert.Equal(side.Id, brush.PatternSideTile!.Asset);

        PatternTileSpec outerTile = brush.PatternOuterTile!;
        Assert.Equal(corner.Id, outerTile.Asset);
        Assert.True(outerTile.FlipAcross);
        Assert.Equal(90.0, outerTile.RotationDegrees, 6);
        Assert.Equal(2.0, outerTile.Scale, 6);

        PathItem restored = reloaded.Artboards[0].Layers[0].Children.OfType<PathItem>().Single();
        Assert.Equal("Rail", restored.Stroke.Brush!.Name);
        Assert.Empty(reloaded.MissingBrushAssets());
    }

    /// <summary>
    /// **#185's rule, for this kind.** A capability that exists in the model and cannot be reached through the
    /// registry is a defect: every one of the pattern brush's capabilities is an operation in the catalogue a
    /// person reads in the diagnostics overlay and a driver reads as its tool list.
    /// </summary>
    [Fact]
    public void EveryPatternBrushCapabilityIsInTheOperationCatalogue()
    {
        var names = EditorOperations.All.Select(o => o.Name).ToHashSet(StringComparer.Ordinal);
        string catalog = EditorOperations.Catalog();

        string[] required =
        {
            "brush.create",     // make one of the kind
            "brush.list",       // read its slots back
            "brush.set",        // and its spacing and threshold
            "brush.setTile",    // assign each of the five tiles and its own controls
            "brush.apply",      // put it on a stroke
            "brush.tiles",      // read where the tiles go
            "brush.rename",     // and the asset operations every brush has
            "brush.delete",
            "brush.missingAssets",
        };

        foreach (string name in required)
        {
            Assert.True(names.Contains(name), $"'{name}' is not in the operation registry");
            Assert.Contains(name, catalog, StringComparison.Ordinal);
        }

        // The kind is one a caller can ask for by name, and the refusal for the ones this build does not make
        // names all three it does.
        string create = EditorOperations.All.Single(o => o.Name == "brush.create").Parameters;
        Assert.Contains("pattern", create, StringComparison.OrdinalIgnoreCase);
    }
}
