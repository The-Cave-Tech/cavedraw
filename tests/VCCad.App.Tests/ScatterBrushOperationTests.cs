using System.Text.Json;
using VCCad.App.Automation;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Core.Serialization;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// The scatter brush through the one operation registry: creating one of the kind, setting each of its five ranged
/// controls, applying it, reading it back, reading where its copies go, and switching the pen's response on
/// (issue #102).
///
/// The operations are the only place a person and a driver can act, so the assertions here are about **reach**: a
/// control that can be set through the registry, a report that says which kind a brush is and what each range is,
/// and a readout that turns "the copies follow the path" into numbers a caller with no eyes can check. The geometry
/// itself is pinned in <c>ScatterBrushAlongPathTests</c> and the drawing in <c>ScatterBrushCanvasTests</c> and
/// <c>ScatterBrushExportTests</c>; what is pinned here is that the registry reaches the same seam those do -
/// including the count at a stated spacing and the pressure response, which are the halves a parameter round trip
/// could not see.
/// </summary>
public class ScatterBrushOperationTests
{
    private static JsonElement Params(object value) => JsonSerializer.SerializeToElement(value);

    /// <summary>A ten by ten square of artwork on the pasteboard, which is what a scatter brush repeats.</summary>
    private static PathItem Art(EditorViewModel vm, string name = "copy")
    {
        var art = new PathItem { Name = name, Fill = FillSpec.Solid(ColorRgb.Black) };
        SubPath sub = art.AddSubPath(closed: true);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(10, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(10, 10)));
        sub.Nodes.Add(new PathNode(new Point2D(0, 10)));
        vm.Document.Orphans.AddItem(art);
        return art;
    }

    /// <summary>A 200pt line, with the selection on it - what every test below brushes.</summary>
    private static (AutomationContext Context, CadDocument Document, PathItem Path) Host()
    {
        var vm = new EditorViewModel();
        var path = new PathItem { Name = "line", Fill = FillSpec.None };
        SubPath sub = path.AddSubPath(closed: false);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(200, 0)));
        path.Stroke = new StrokeSpec(true, ColorRgb.Black, 4, StrokeCap.Butt, StrokeJoin.Miter, 4);
        vm.Document.Artboards[0].Layers[0].AddItem(path);
        vm.SelectObject(path);

        return (new AutomationContext { ViewModel = vm }, vm.Document, path);
    }

    /// <summary>The copy readout as raw JSON, which is what a driver is handed.</summary>
    private static JsonElement[] Copies(AutomationContext context, string brush, double? pressure = null)
        => JsonSerializer.Deserialize<JsonElement[]>(
            JsonSerializer.Serialize(EditorOperations.Invoke(
                context,
                "brush.scatter",
                pressure is { } p ? Params(new { name = brush, pressure = p }) : Params(new { name = brush }))))!;

    [Fact]
    public void AScatterBrushCanBeCreatedListedAndApplied()
    {
        (AutomationContext context, _, PathItem path) = Host();
        PathItem art = Art(context.ViewModel);

        EditorOperations.Invoke(context, "brush.create",
            Params(new
            {
                name = "Spray",
                kind = "scatter",
                asset = art.Id,
                size = 20,
                spacing = 40,
                spacingRandomness = 5,
                rotation = 25,
                rotationRandomness = 10,
                scale = 1.5,
                scaleRandomness = 0.25,
                offset = 3,
                offsetRandomness = 4,
                opacity = 0.8,
                opacityRandomness = 0.2,
            }));

        string list = JsonSerializer.Serialize(EditorOperations.Invoke(context, "brush.list", default));
        Assert.Contains("\"kind\":\"scatter\"", list, StringComparison.Ordinal);
        Assert.Contains(art.Id.ToString(), list, StringComparison.Ordinal);

        // Each control is reported as its value **and** its range, with the two ends of the range beside them, so a
        // caller does not have to know that a range is a plus-or-minus to see what a copy may be drawn at.
        JsonElement scatter = JsonSerializer.SerializeToElement(
            EditorOperations.Invoke(context, "brush.list", default))[0].GetProperty("scatter");
        Assert.Equal(art.Id, scatter.GetProperty("asset").GetGuid());
        Assert.Equal(40.0, scatter.GetProperty("spacing").GetProperty("value").GetDouble(), 6);
        Assert.Equal(5.0, scatter.GetProperty("spacing").GetProperty("randomness").GetDouble(), 6);
        Assert.Equal(35.0, scatter.GetProperty("spacing").GetProperty("min").GetDouble(), 6);
        Assert.Equal(45.0, scatter.GetProperty("spacing").GetProperty("max").GetDouble(), 6);
        Assert.Equal(1.5, scatter.GetProperty("scale").GetProperty("value").GetDouble(), 6);
        Assert.Equal(0.8, scatter.GetProperty("opacity").GetProperty("value").GetDouble(), 6);

        // Creating it does not apply it: an asset sits in the document until something uses it.
        Assert.False(path.Stroke.HasBrush);

        EditorOperations.Invoke(context, "brush.apply", Params(new { name = "Spray" }));

        Assert.True(path.Stroke.HasBrush);
        Assert.Equal(BrushKind.Scatter, path.Stroke.Brush!.Kind);
        Assert.True(path.Stroke.Brush.IsScatter);
        Assert.False(path.Stroke.Brush.IsNib);
        Assert.False(path.Stroke.Brush.IsArt);
        Assert.Equal(20.0, path.Stroke.Brush.Diameter, 6);
        Assert.Equal(40.0, path.Stroke.Brush.ScatterSpec!.Spacing.Value, 6);
        Assert.Equal(5.0, path.Stroke.Brush.ScatterSpec.Spacing.Randomness, 6);
    }

    /// <summary>
    /// **The geometry is reachable.** A 200pt line at a 40pt pitch holds five copies, and the readout says where
    /// each one's centre is, how big it is, which way it is turned and how far it is offset - without anyone having
    /// to see the drawing.
    /// </summary>
    [Fact]
    public void TheCopyReadoutSaysWhereEachCopyGoes()
    {
        (AutomationContext context, _, _) = Host();
        PathItem art = Art(context.ViewModel);

        EditorOperations.Invoke(context, "brush.create",
            Params(new { name = "Spray", kind = "scatter", asset = art.Id, size = 20, spacing = 40 }));

        JsonElement[] paths = Copies(context, "Spray");
        JsonElement[] copies = paths[0].GetProperty("copies").EnumerateArray().ToArray();

        Assert.Equal(5, copies.Length);
        Assert.Equal(new[] { 0.0, 40.0, 80.0, 120.0, 160.0 },
            copies.Select(c => c.GetProperty("position").GetDouble()).ToArray());
        Assert.All(copies, c => Assert.Equal(1.0, c.GetProperty("scale").GetDouble(), 6));
        Assert.All(copies, c => Assert.Equal(0.0, c.GetProperty("tangentDegrees").GetDouble(), 6));

        // The transform is reported as the six numbers a renderer applies, not as a note that a copy exists.
        Assert.Equal(6, copies[0].GetProperty("transform").GetArrayLength());
        Assert.Equal(20.0, copies[0].GetProperty("length").GetDouble(), 6);
    }

    /// <summary>
    /// **Every control and its range is settable after the fact**, and the readout follows: setting the pitch moves
    /// the copies, and setting a range scatters them without the document being re-brushed.
    /// </summary>
    [Fact]
    public void EveryControlAndItsRangeCanBeSet()
    {
        (AutomationContext context, _, PathItem path) = Host();
        PathItem art = Art(context.ViewModel);

        EditorOperations.Invoke(context, "brush.create",
            Params(new { name = "Spray", kind = "scatter", asset = art.Id, size = 20, spacing = 40 }));
        EditorOperations.Invoke(context, "brush.apply", Params(new { name = "Spray" }));

        EditorOperations.Invoke(context, "brush.set",
            Params(new { name = "Spray", spacing = 100, rotation = 30, rotationRandomness = 15, offset = 6, opacity = 0.5 }));

        // The edit reached the library and the stroke that named it, which is what makes a brush an asset.
        Assert.Equal(100.0, context.Document.FindBrush("Spray")!.ScatterSpec!.Spacing.Value, 6);
        Assert.Equal(30.0, path.Stroke.Brush!.ScatterSpec!.Rotation.Value, 6);
        Assert.Equal(15.0, path.Stroke.Brush.ScatterSpec.Rotation.Randomness, 6);
        Assert.Equal(6.0, path.Stroke.Brush.ScatterSpec.Offset.Value, 6);
        Assert.Equal(0.5, path.Stroke.Brush.ScatterSpec.Opacity.Value, 6);

        JsonElement[] copies = Copies(context, "Spray")[0].GetProperty("copies").EnumerateArray().ToArray();

        // A 200pt line at a 100pt pitch is two copies, and no range was given on the pitch so they are where the
        // pitch says - while the rotation range really did move them off the value it was set to.
        Assert.Equal(2, copies.Length);
        Assert.Equal(new[] { 0.0, 100.0 }, copies.Select(c => c.GetProperty("position").GetDouble()).ToArray());
        Assert.All(copies, c => Assert.InRange(c.GetProperty("rotationDegrees").GetDouble(), 15.0, 45.0));
        Assert.True(copies.Select(c => Math.Round(c.GetProperty("rotationDegrees").GetDouble(), 6)).Distinct().Count() > 1,
            "the rotation range has to move the copies apart from one another");
        Assert.All(copies, c => Assert.Equal(6.0, c.GetProperty("offset").GetDouble(), 6));
        Assert.All(copies, c => Assert.Equal(0.5, c.GetProperty("opacity").GetDouble(), 6));
    }

    /// <summary>
    /// **The pen's response is reachable too.** A brush whose scatter-scale target is on draws bigger copies at
    /// more pressure, through the same `brush.setDynamics` a person reaches from the diagnostics overlay - and with
    /// no dynamics recorded the same pressure changes nothing, which is what a static document is drawn as.
    /// </summary>
    [Fact]
    public void ThePressureResponseIsReachableAndChangesTheCopies()
    {
        (AutomationContext context, _, _) = Host();
        PathItem art = Art(context.ViewModel);

        EditorOperations.Invoke(context, "brush.create",
            Params(new { name = "Spray", kind = "scatter", asset = art.Id, size = 20, spacing = 100, scale = 1.0 }));

        // Before anything is recorded, pressure is ignored rather than passed through.
        double flat = Copies(context, "Spray", pressure: 0.25)[0].GetProperty("copies")[0].GetProperty("scale").GetDouble();
        Assert.Equal(1.0, flat, 6);

        JsonElement set = JsonSerializer.SerializeToElement(EditorOperations.Invoke(
            context, "brush.setDynamics", Params(new { name = "Spray", target = "scatterScale", preset = "linear" })));
        Assert.Equal("ScatterScale", set.GetProperty("target").GetString());
        Assert.True(set.GetProperty("enabled").GetBoolean());

        double light = Copies(context, "Spray", pressure: 0.25)[0].GetProperty("copies")[0].GetProperty("scale").GetDouble();
        double heavy = Copies(context, "Spray", pressure: 0.75)[0].GetProperty("copies")[0].GetProperty("scale").GetDouble();

        Assert.True(heavy > light, $"more pressure should size a copy up ({heavy} against {light})");
        Assert.Equal(0.25, light, 6);
        Assert.Equal(0.75, heavy, 6);

        // The response is the brush's, so it is reported by brush.list - which is how a driver sees it exists.
        string list = JsonSerializer.Serialize(EditorOperations.Invoke(context, "brush.list", default));
        Assert.Contains("\"target\":\"ScatterScale\"", list, StringComparison.Ordinal);
    }

    /// <summary>A brush of another kind is refused by name, and the refusal says which readout to use instead.</summary>
    [Fact]
    public void TheCopyReadoutRefusesABrushThatScattersNothing()
    {
        (AutomationContext context, _, _) = Host();
        PathItem art = Art(context.ViewModel);

        EditorOperations.Invoke(context, "brush.create", Params(new { name = "Chisel", roundness = 0.3 }));
        EditorOperations.Invoke(context, "brush.create",
            Params(new { name = "Rail", kind = "pattern", side = art.Id, size = 12 }));
        EditorOperations.Invoke(context, "brush.create",
            Params(new { name = "Vine", kind = "art", asset = art.Id, size = 12 }));

        var nib = Assert.Throws<EditorOperationException>(
            () => EditorOperations.Invoke(context, "brush.scatter", Params(new { name = "Chisel" })));
        Assert.Contains("calligraphic", nib.Message, StringComparison.OrdinalIgnoreCase);

        var pattern = Assert.Throws<EditorOperationException>(
            () => EditorOperations.Invoke(context, "brush.scatter", Params(new { name = "Rail" })));
        Assert.Contains("brush.tiles", pattern.Message, StringComparison.Ordinal);

        var artBrush = Assert.Throws<EditorOperationException>(
            () => EditorOperations.Invoke(context, "brush.scatter", Params(new { name = "Vine" })));
        Assert.Contains("brush.placements", artBrush.Message, StringComparison.Ordinal);

        // And the other two readouts refuse a scatter brush by name, pointing at brush.scatter in their turn.
        EditorOperations.Invoke(context, "brush.create",
            Params(new { name = "Spray", kind = "scatter", asset = art.Id, size = 20, spacing = 40 }));

        var tiles = Assert.Throws<EditorOperationException>(
            () => EditorOperations.Invoke(context, "brush.tiles", Params(new { name = "Spray" })));
        Assert.Contains("brush.scatter", tiles.Message, StringComparison.Ordinal);

        var placements = Assert.Throws<EditorOperationException>(
            () => EditorOperations.Invoke(context, "brush.placements", Params(new { name = "Spray" })));
        Assert.Contains("brush.scatter", placements.Message, StringComparison.Ordinal);

        // A scatter brush that names no artwork is refused by name too: the readout is where its copies go, and a
        // brush with nothing to repeat has no copies rather than an empty list a caller would read as "none fit".
        EditorOperations.Invoke(context, "brush.create",
            Params(new { name = "Ghost", kind = "scatter", size = 20, spacing = 40 }));

        var noAsset = Assert.Throws<EditorOperationException>(
            () => EditorOperations.Invoke(context, "brush.scatter", Params(new { name = "Ghost" })));
        Assert.Contains("names no asset", noAsset.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A scatter brush and its asset survive a save and a reload, and the reloaded document has no missing asset -
    /// which is the half a parameter round trip alone could not see.
    /// </summary>
    [Fact]
    public void AScatterBrushAndItsArtworkSurviveAReload()
    {
        (AutomationContext context, CadDocument document, _) = Host();
        PathItem art = Art(context.ViewModel);

        EditorOperations.Invoke(context, "brush.create",
            Params(new { name = "Spray", kind = "scatter", asset = art.Id, size = 20, spacing = 40, offset = 5, offsetRandomness = 3 }));
        EditorOperations.Invoke(context, "brush.apply", Params(new { name = "Spray" }));

        CadDocument reloaded = VccadDocumentSerializer.Deserialize(
            VccadDocumentSerializer.SerializeToBytes(document));

        Assert.Equal("Spray", reloaded.FindBrush("Spray")!.Name);
        Assert.True(reloaded.FindBrush("Spray")!.IsScatter);
        Assert.Equal(5.0, reloaded.FindBrush("Spray")!.ScatterSpec!.Offset.Value, 6);
        Assert.Equal(3.0, reloaded.FindBrush("Spray")!.ScatterSpec.Offset.Randomness, 6);

        PathItem restored = reloaded.Artboards[0].Layers[0].Children.OfType<PathItem>().Single();
        Assert.Equal("Spray", restored.Stroke.Brush!.Name);

        // Both sides of the reference travelled: the brush still names the item, and the item is there to be found.
        Assert.Equal(art.Id, reloaded.FindBrush("Spray")!.ScatterSpec.Asset);
        Assert.NotNull(reloaded.FindItem(art.Id));
        Assert.Empty(reloaded.MissingBrushAssets());
    }

    /// <summary>
    /// **#185's rule, for this kind.** A capability that exists in the model and cannot be reached through the
    /// registry is a defect: every one of the scatter brush's capabilities is an operation in the catalogue a
    /// person reads in the diagnostics overlay and a driver reads as its tool list.
    /// </summary>
    [Fact]
    public void EveryScatterBrushCapabilityIsInTheOperationCatalogue()
    {
        var names = EditorOperations.All.Select(o => o.Name).ToHashSet(StringComparer.Ordinal);
        string catalog = EditorOperations.Catalog();

        string[] required =
        {
            "brush.create",       // make one of the kind
            "brush.list",         // read its controls and their ranges back
            "brush.set",          // change every control and every range
            "brush.setDynamics",  // and switch the pen's response on, which is what pressure moves
            "brush.apply",        // put it on a stroke
            "brush.scatter",      // read where the copies go
            "brush.rename",       // and the asset operations every brush has
            "brush.delete",
            "brush.missingAssets",
        };

        foreach (string name in required)
        {
            Assert.True(names.Contains(name), $"'{name}' is not in the operation registry");
            Assert.Contains(name, catalog, StringComparison.Ordinal);
        }

        // The kind is one a caller can ask for by name, and the refusal for the ones this build does not make
        // names all four it does.
        string create = EditorOperations.All.Single(o => o.Name == "brush.create").Parameters;
        Assert.Contains("scatter", create, StringComparison.OrdinalIgnoreCase);
    }
}
