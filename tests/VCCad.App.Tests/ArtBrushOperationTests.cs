using System.Text.Json;
using VCCad.App.Automation;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Core.Serialization;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// The art brush through the one operation registry: creating one of the new kind, listing it, applying it,
/// reading it back and reading where its art goes (issue #100).
///
/// The operations are the only place a person and a driver can act, so the assertions here are about **reach**:
/// a member that can be set through the registry, a report that says which kind a brush is, and a placement
/// readout that turns "the art follows the path" into numbers a caller with no eyes can check. The geometry
/// itself is pinned in <c>ArtBrushAlongPathTests</c>; what is pinned here is that the registry reaches it.
/// </summary>
public class ArtBrushOperationTests
{
    private static JsonElement Params(object value) => JsonSerializer.SerializeToElement(value);

    /// <summary>A ten by ten square of artwork on the pasteboard, which is what an art brush maps.</summary>
    private static PathItem Asset(EditorViewModel vm)
    {
        var asset = new PathItem { Name = "tile", Fill = FillSpec.None };
        SubPath sub = asset.AddSubPath(closed: true);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(10, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(10, 10)));
        sub.Nodes.Add(new PathNode(new Point2D(0, 10)));
        vm.Document.Orphans.AddItem(asset);
        return asset;
    }

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

    private static void Move(PathItem path, int index, Point2D to)
    {
        PathNode node = path.SubPaths[0].Nodes[index];
        node.Anchor = to;
        node.InHandle = to;
        node.OutHandle = to;
    }

    private static JsonElement[] Placements(AutomationContext context, string brush)
        => JsonSerializer.Deserialize<JsonElement[]>(
            JsonSerializer.Serialize(
                EditorOperations.Invoke(context, "brush.placements", Params(new { name = brush }))))!;

    [Fact]
    public void AnArtBrushCanBeCreatedListedAndApplied()
    {
        (AutomationContext context, _, PathItem path) = Host();
        PathItem asset = Asset(context.ViewModel);

        EditorOperations.Invoke(context, "brush.create",
            Params(new { name = "Vine", kind = "art", asset = asset.Id, size = 20, stretch = "repeat" }));

        string list = JsonSerializer.Serialize(EditorOperations.Invoke(context, "brush.list", default));
        Assert.Contains("\"kind\":\"art\"", list, StringComparison.Ordinal);
        Assert.Contains(asset.Id.ToString(), list, StringComparison.Ordinal);
        Assert.Contains("\"stretch\":\"repeat\"", list, StringComparison.Ordinal);

        // Creating it does not apply it: an asset sits in the document until something uses it.
        Assert.False(path.Stroke.HasBrush);

        EditorOperations.Invoke(context, "brush.apply", Params(new { name = "Vine" }));

        Assert.True(path.Stroke.HasBrush);
        Assert.Equal(BrushKind.Art, path.Stroke.Brush!.Kind);
        Assert.Equal(asset.Id, path.Stroke.Brush.ArtAsset);
        Assert.Equal(20.0, path.Stroke.Brush.Diameter, 6);
        Assert.True(path.Stroke.Brush.IsArt);
    }

    /// <summary>
    /// **An asset the document does not have is refused where it is named**, not stored as a brush that maps
    /// nothing. The brush would hold an id no item answers to, and every render after that would be a picture
    /// nobody asked for with no sign of why.
    /// </summary>
    [Fact]
    public void AnArtBrushNamingAnItemTheDocumentDoesNotHaveIsRefused()
    {
        (AutomationContext context, CadDocument document, _) = Host();
        var missing = Guid.NewGuid();

        EditorOperationException error = Assert.Throws<EditorOperationException>(
            () => EditorOperations.Invoke(context, "brush.create",
                Params(new { name = "Vine", kind = "art", asset = missing, size = 20 })));

        Assert.Contains(missing.ToString(), error.Message, StringComparison.Ordinal);
        Assert.Empty(document.Brushes);
    }

    /// <summary>
    /// A kind this build does not make is refused by name rather than quietly made a nib.
    ///
    /// **Turned over twice now, never deleted.** This test used to name 'scatter' as the example of a kind that did
    /// not exist; when #102 landed, that became a kind this build does make, and the refusal was pinned with
    /// 'bristle' instead. #103 has now landed too, so 'bristle' joins 'scatter' on the accepted side and the
    /// refusal is pinned with a name no brush engine here has - which is the only thing left that genuinely stands
    /// for "not made". Leaving the old name in place would have made the test a lie; deleting it would have thrown
    /// away the gap it recorded. This is the failing-on-improvement rule working as intended.
    /// </summary>
    [Fact]
    public void AKindThisBuildDoesNotMakeIsRefusedByName()
    {
        (AutomationContext context, CadDocument document, _) = Host();

        EditorOperationException error = Assert.Throws<EditorOperationException>(
            () => EditorOperations.Invoke(context, "brush.create",
                Params(new { name = "Chalk", kind = "chalk", diameter = 4 })));

        Assert.Contains("chalk", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(document.Brushes);

        // The refusal names every kind this build does make, including the two that used to be the examples.
        Assert.Contains("scatter", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("bristle", error.Message, StringComparison.OrdinalIgnoreCase);

        // And the kinds that used to be refused are now made, each with its own kind on the brush.
        EditorOperations.Invoke(context, "brush.create",
            Params(new { name = "Spray", kind = "scatter", diameter = 4, spacing = 10 }));
        EditorOperations.Invoke(context, "brush.create",
            Params(new { name = "Bristle", kind = "bristle", diameter = 4, count = 9 }));

        Assert.Equal(new[] { BrushKind.Scatter, BrushKind.Bristle }, document.Brushes.Select(b => b.Kind).ToArray());
        Assert.True(document.FindBrush("Spray")!.IsScatter);
        Assert.True(document.FindBrush("Bristle")!.IsBristle);
        Assert.Equal(9, document.FindBrush("Bristle")!.BristleSpec!.Count);
    }

    /// <summary>**Reading a stroke back says what kind of brush it carries**, art members and all.</summary>
    [Fact]
    public void ReadingAStrokeReportsTheArtBrushAndItsMembers()
    {
        (AutomationContext context, _, PathItem path) = Host();
        PathItem asset = Asset(context.ViewModel);
        EditorOperations.Invoke(context, "brush.create",
            Params(new
            {
                name = "Vine",
                kind = "art",
                asset = asset.Id,
                size = 24,
                stretch = "stretchToFit",
                flipAlong = true,
                colourisation = "tintAndShade",
                shadeColour = new[] { 10, 20, 30 },
            }));
        EditorOperations.Invoke(context, "brush.apply", Params(new { name = "Vine" }));

        JsonElement strokes = JsonSerializer.SerializeToElement(
            EditorOperations.Invoke(context, "style.strokes", default));

        JsonElement brush = strokes[0].GetProperty("strokes")[0].GetProperty("brush");
        Assert.Equal("art", brush.GetProperty("kind").GetString());
        Assert.Equal(24.0, brush.GetProperty("size").GetDouble(), 6);

        JsonElement art = brush.GetProperty("art");
        Assert.Equal(asset.Id.ToString(), art.GetProperty("asset").GetString());
        Assert.Equal("stretchtofit", art.GetProperty("stretch").GetString().ToLowerInvariant());
        Assert.True(art.GetProperty("flipAlong").GetBoolean());
        Assert.False(art.GetProperty("flipAcross").GetBoolean());
        Assert.Equal("tintandshade", art.GetProperty("colourisation").GetString());

        // The mode is held and the effect is not: a caller must not read "tint" as "it has been tinted".
        Assert.False(art.GetProperty("colourisationApplied").GetBoolean());

        // And a nib reports no art at all, so the member says which kind the brush is rather than being noise.
        path.Stroke = path.Stroke with { Brush = BrushSpec.Calligraphic("Chisel", 35, 0.2, 24) };
        JsonElement nibbed = JsonSerializer.SerializeToElement(
            EditorOperations.Invoke(context, "style.strokes", default));
        Assert.Equal(JsonValueKind.Null,
            nibbed[0].GetProperty("strokes")[0].GetProperty("brush").GetProperty("art").ValueKind);
    }

    /// <summary>
    /// **The placement readout follows the path.** Editing the path changes what the operation answers with, and
    /// the brush on the stroke is untouched by the edit - which is what makes the art a property of the stroke
    /// rather than artwork pasted along the line.
    /// </summary>
    [Fact]
    public void ThePlacementReadoutFollowsThePathWithoutTheBrushBeingReapplied()
    {
        (AutomationContext context, _, PathItem path) = Host();
        PathItem asset = Asset(context.ViewModel);
        EditorOperations.Invoke(context, "brush.create",
            Params(new { name = "Vine", kind = "art", asset = asset.Id, size = 20, stretch = "repeat" }));
        EditorOperations.Invoke(context, "brush.apply", Params(new { name = "Vine" }));

        JsonElement[] before = Placements(context, "Vine");
        JsonElement placements = before[0].GetProperty("placements");
        Assert.Equal(3, placements.GetArrayLength());
        Assert.Equal(0.0, placements[0].GetProperty("tangentDegrees").GetDouble(), 6);
        Assert.Equal(10.0, before[0].GetProperty("assetHeight").GetDouble(), 6);

        BrushSpec applied = path.Stroke.Brush!;
        Move(path, 1, new Point2D(30, 40));

        JsonElement[] after = Placements(context, "Vine");
        Assert.Equal(applied, path.Stroke.Brush);
        Assert.NotEqual(
            placements[0].GetProperty("transform")[0].GetDouble(),
            after[0].GetProperty("placements")[0].GetProperty("transform")[0].GetDouble());

        double tangent = after[0].GetProperty("placements")[0].GetProperty("tangentDegrees").GetDouble();
        Assert.Equal(Math.Atan2(40, 30) * 180.0 / Math.PI, tangent, 4);
    }

    /// <summary>A nib places no art, and a brush whose asset is gone is refused by name rather than answered empty.</summary>
    [Fact]
    public void ThePlacementReadoutRefusesANibAndAMissingAsset()
    {
        (AutomationContext context, CadDocument document, _) = Host();
        PathItem asset = Asset(context.ViewModel);

        EditorOperations.Invoke(context, "brush.create", Params(new { name = "Chisel", roundness = 0.3 }));
        EditorOperationException nib = Assert.Throws<EditorOperationException>(
            () => EditorOperations.Invoke(context, "brush.placements", Params(new { name = "Chisel" })));
        Assert.Contains("no artwork", nib.Message, StringComparison.OrdinalIgnoreCase);

        EditorOperations.Invoke(context, "brush.create",
            Params(new { name = "Vine", kind = "art", asset = asset.Id, size = 20 }));

        // The artwork is deleted out from under the brush, which is what a person deleting the definition does.
        document.Orphans.RemoveItem(asset);

        JsonElement[] missing = JsonSerializer.Deserialize<JsonElement[]>(
            JsonSerializer.Serialize(
                EditorOperations.Invoke(context, "brush.missingAssets", default)))!;
        JsonElement report = Assert.Single(missing);
        Assert.Equal("Vine", report.GetProperty("brush").GetString());
        Assert.Equal(asset.Id, report.GetProperty("assetId").GetGuid());

        EditorOperationException gone = Assert.Throws<EditorOperationException>(
            () => EditorOperations.Invoke(context, "brush.placements", Params(new { name = "Vine" })));
        Assert.Contains(asset.Id.ToString(), gone.Message, StringComparison.Ordinal);
    }

    /// <summary>Editing the asset reaches the strokes that named it, the same way editing a nib does.</summary>
    [Fact]
    public void EditingAnArtBrushReachesTheStrokesThatUseIt()
    {
        (AutomationContext context, CadDocument document, PathItem path) = Host();
        PathItem asset = Asset(context.ViewModel);
        EditorOperations.Invoke(context, "brush.create",
            Params(new { name = "Vine", kind = "art", asset = asset.Id, size = 20, stretch = "repeat" }));
        EditorOperations.Invoke(context, "brush.apply", Params(new { name = "Vine" }));

        EditorOperations.Invoke(context, "brush.set",
            Params(new { name = "Vine", stretch = "scaleProportionally", flipAcross = true, size = 8 }));

        Assert.Equal(ArtStretch.ScaleProportionally, document.Brushes.Single().Stretch);
        Assert.Equal(ArtStretch.ScaleProportionally, path.Stroke.Brush!.Stretch);
        Assert.True(path.Stroke.Brush.FlipAcross);
        Assert.Equal(8.0, path.Stroke.Brush.Diameter, 6);

        // A member that was not given keeps what it had rather than being reset to this operation's default.
        Assert.Equal(asset.Id, path.Stroke.Brush.ArtAsset);
        Assert.False(path.Stroke.Brush.FlipAlong);
    }

    /// <summary>
    /// **A raster asset is an item too**, so an art brush can map an embedded image: nothing here is vector-only.
    /// The asset's box is the image's placement, which is what the brush measures and scales against - the picture
    /// itself is never copied into the brush.
    ///
    /// What the model cannot state is the **turn**: an <see cref="ImageItem"/> holds an axis-aligned placement
    /// plus two mirrors and no rotation, so the placement readout is what carries the tangent. That is the one
    /// part of an art brush a raster asset cannot be held as, and it is named rather than papered over.
    /// </summary>
    [Fact]
    public void AnEmbeddedImageCanBeTheArtABrushMaps()
    {
        (AutomationContext context, _, _) = Host();
        var image = new ImageItem
        {
            Name = "scan",
            Placement = new Rect2D(0, 0, 20, 40),
            PixelWidth = 2,
            PixelHeight = 2,
        };
        context.ViewModel.Document.Orphans.AddItem(image);

        EditorOperations.Invoke(context, "brush.create",
            Params(new { name = "Scan", kind = "art", asset = image.Id, size = 15, stretch = "repeat" }));

        JsonElement[] read = Placements(context, "Scan");
        Assert.Equal(20.0, read[0].GetProperty("assetWidth").GetDouble(), 6);
        Assert.Equal(40.0, read[0].GetProperty("assetHeight").GetDouble(), 6);

        // 15 across a 20-wide image scales it 0.75, so a 40-tall image is 30 long and a 60pt line holds two.
        JsonElement pieces = read[0].GetProperty("placements");
        Assert.Equal(2, pieces.GetArrayLength());
        Assert.Equal(30.0, pieces[0].GetProperty("length").GetDouble(), 6);
    }

    /// <summary>An art brush is a document asset like any other, so it survives a save and a reload.</summary>
    [Fact]
    public void TheArtBrushSurvivesSaveAndReload()
    {
        (AutomationContext context, CadDocument document, PathItem path) = Host();
        PathItem asset = Asset(context.ViewModel);
        EditorOperations.Invoke(context, "brush.create",
            Params(new { name = "Vine", kind = "art", asset = asset.Id, size = 20, stretch = "stretchToFit" }));
        EditorOperations.Invoke(context, "brush.apply", Params(new { name = "Vine" }));

        CadDocument reloaded = VccadDocumentSerializer.Deserialize(
            VccadDocumentSerializer.SerializeToBytes(document));

        BrushSpec brush = Assert.Single(reloaded.Brushes);
        Assert.Equal(BrushKind.Art, brush.Kind);
        Assert.Equal(ArtStretch.StretchToFit, brush.Stretch);

        PathItem restored = reloaded.Artboards[0].Layers[0].Children.OfType<PathItem>().Single();
        Assert.Equal("Vine", restored.Stroke.Brush!.Name);
        Assert.Equal(asset.Id, restored.Stroke.Brush.ArtAsset);
        Assert.Empty(reloaded.MissingBrushAssets());
    }
}
