using System.Text.Json;
using VCCad.App.Automation;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// The bristle brush through the one operation registry: creating one of the kind, setting each of its controls,
/// applying it, reading it back, and reading where its bristles go (issue #103).
///
/// The operations are the only place a person and a driver can act, so the assertions here are about **reach**: that
/// the registry makes a bundle, that every control can be set through it, that the kind appears in
/// <c>brush.kinds</c> because the reader accepts it rather than because a list was edited, and that the readout
/// turns "the bristles follow the path" into numbers a caller with no eyes can check - including the bound, which
/// must be reported rather than served short. The geometry is pinned in <c>BristleBrushAlongPathTests</c>, the
/// drawing in <c>BristleBrushCanvasTests</c> and the page in <c>BristleBrushExportTests</c>.
/// </summary>
public class BristleBrushOperationTests
{
    private static JsonElement Params(object value) => JsonSerializer.SerializeToElement(value);

    /// <summary>A 200pt line with the selection on it, which is what every test below brushes.</summary>
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

    private static JsonElement Read(AutomationContext context, string op, object parameters)
        => JsonSerializer.SerializeToElement(
            EditorOperations.Invoke(context, op, Params(parameters)));

    /// <summary>The bristle readout for the one selected path, as raw JSON.</summary>
    private static JsonElement Bristles(
        AutomationContext context, string brush, double? pressure = null, double? tilt = null)
    {
        var parameters = new Dictionary<string, object> { ["name"] = brush };
        if (pressure is { } p)
        {
            parameters["pressure"] = p;
        }

        if (tilt is { } t)
        {
            parameters["tilt"] = t;
        }

        return Read(context, "brush.bristles", parameters)[0];
    }

    /// <summary>
    /// **The kind is one this build makes, and it says so without a list being edited.** <c>brush.kinds</c> asks the
    /// reader <c>brush.create</c> would use, so a kind that lands in the reader appears there; a hardcoded list
    /// would report a build that makes a brush it cannot.
    /// </summary>
    [Fact]
    public void BristleIsAKindThisBuildMakes()
    {
        (AutomationContext context, _, _) = Host();

        var kinds = (string[]?)EditorOperations.Invoke(context, "brush.kinds", default);
        Assert.Contains("bristle", kinds);
    }

    /// <summary>
    /// **A bristle brush is created with every control stated, listed with each of them, and applied to a stroke.**
    /// Each control is asserted after the round trip through the registry, because a parameter the operation accepts
    /// and drops is a control a person can turn that does nothing.
    /// </summary>
    [Fact]
    public void ABristleBrushCanBeCreatedListedAndApplied()
    {
        (AutomationContext context, _, PathItem path) = Host();

        EditorOperations.Invoke(context, "brush.create",
            Params(new
            {
                name = "Scrub",
                kind = "bristle",
                size = 30,
                count = 19,
                length = 45,
                stiffness = 0.7,
                thickness = 1.5,
                spread = 1.2,
                randomness = 0.4,
                pressureSpread = 0.8,
                tiltTurn = 2.0,
                colourJitter = 0.3,
            }));

        string list = JsonSerializer.Serialize(EditorOperations.Invoke(context, "brush.list", default));
        Assert.Contains("\"kind\":\"bristle\"", list, StringComparison.Ordinal);

        JsonElement bristle = Read(context, "brush.list", new { })[0].GetProperty("bristle");
        Assert.Equal(19, bristle.GetProperty("count").GetInt32());
        Assert.Equal(45.0, bristle.GetProperty("length").GetDouble(), 6);
        Assert.Equal(0.7, bristle.GetProperty("stiffness").GetDouble(), 6);
        Assert.Equal(1.5, bristle.GetProperty("thickness").GetDouble(), 6);
        Assert.Equal(1.2, bristle.GetProperty("spread").GetDouble(), 6);
        Assert.Equal(0.4, bristle.GetProperty("randomness").GetDouble(), 6);
        Assert.Equal(0.8, bristle.GetProperty("pressureSpread").GetDouble(), 6);
        Assert.Equal(2.0, bristle.GetProperty("tiltTurn").GetDouble(), 6);
        Assert.Equal(0.3, bristle.GetProperty("colourJitter").GetDouble(), 6);
        Assert.Equal(BristleBrushPath.MaxBristles, bristle.GetProperty("maxBristles").GetInt32());

        // Creating it does not apply it: an asset sits in the document until something uses it.
        Assert.False(path.Stroke.HasBrush);

        EditorOperations.Invoke(context, "brush.apply", Params(new { name = "Scrub" }));

        Assert.True(path.Stroke.Brush!.IsBristle);
        Assert.Equal(BrushKind.Bristle, path.Stroke.Brush.Kind);
        Assert.Equal(30.0, path.Stroke.Brush.Diameter, 6);
        Assert.Equal(19, path.Stroke.Brush.BristleSpec!.Count);
        Assert.Equal(2.0, path.Stroke.Brush.BristleSpec.TiltTurn, 6);
    }

    /// <summary>
    /// **Every control is set one at a time through <c>brush.set</c>, and setting one leaves the rest alone.** A
    /// brush whose bundle was rebuilt from defaults on every edit would silently reset the other eight controls,
    /// which is the shape a person meets as "the panel forgets what I typed".
    /// </summary>
    [Fact]
    public void EveryBristleControlIsSetThroughTheRegistryWithoutDisturbingTheRest()
    {
        (AutomationContext context, CadDocument document, _) = Host();
        EditorOperations.Invoke(context, "brush.create", Params(new { name = "Scrub", kind = "bristle", size = 24 }));

        EditorOperations.Invoke(context, "brush.set", Params(new { name = "Scrub", count = 31 }));
        Assert.Equal(31, document.FindBrush("Scrub")!.BristleSpec!.Count);

        EditorOperations.Invoke(context, "brush.set", Params(new { name = "Scrub", length = 12 }));
        EditorOperations.Invoke(context, "brush.set", Params(new { name = "Scrub", stiffness = 0.2 }));
        EditorOperations.Invoke(context, "brush.set", Params(new { name = "Scrub", thickness = 2.5 }));
        EditorOperations.Invoke(context, "brush.set", Params(new { name = "Scrub", spread = 0.4 }));
        EditorOperations.Invoke(context, "brush.set", Params(new { name = "Scrub", randomness = 0.9 }));
        EditorOperations.Invoke(context, "brush.set", Params(new { name = "Scrub", pressureSpread = 0.1 }));
        EditorOperations.Invoke(context, "brush.set", Params(new { name = "Scrub", tiltTurn = 3.0 }));
        EditorOperations.Invoke(context, "brush.set", Params(new { name = "Scrub", colourJitter = 0.5 }));

        BristleBrushSpec spec = document.FindBrush("Scrub")!.BristleSpec!;
        Assert.Equal(31, spec.Count);
        Assert.Equal(12.0, spec.Length, 6);
        Assert.Equal(0.2, spec.Stiffness, 6);
        Assert.Equal(2.5, spec.Thickness, 6);
        Assert.Equal(0.4, spec.Spread, 6);
        Assert.Equal(0.9, spec.Randomness, 6);
        Assert.Equal(0.1, spec.PressureSpread, 6);
        Assert.Equal(3.0, spec.TiltTurn, 6);
        Assert.Equal(0.5, spec.ColourJitter, 6);

        // A control a person could not reach is a defect: the size goes with the bundle's controls, not only the
        // spec's, because it is the same member every other kind's size is.
        EditorOperations.Invoke(context, "brush.set", Params(new { name = "Scrub", size = 40 }));
        Assert.Equal(40.0, document.FindBrush("Scrub")!.Diameter, 6);
        Assert.Equal(31, document.FindBrush("Scrub")!.BristleSpec!.Count);
    }

    /// <summary>
    /// **The readout says where the bristles are and what they are drawn with.** Each bristle names its place along
    /// the path, its own offset across it, its turn and shade, and the two ends of its own centreline - which is
    /// what lets a driver with no eyes check that the bundle is what the brush says rather than only that a brush
    /// exists.
    /// </summary>
    [Fact]
    public void TheBristleReadoutReportsWhereEveryBristleRuns()
    {
        (AutomationContext context, _, _) = Host();
        EditorOperations.Invoke(context, "brush.create",
            Params(new { name = "Scrub", kind = "bristle", size = 40, count = 5, randomness = 0.0, spread = 1.0 }));
        EditorOperations.Invoke(context, "brush.apply", Params(new { name = "Scrub" }));

        JsonElement readout = Bristles(context, "Scrub");
        Assert.Equal(5, readout.GetProperty("bristles").GetArrayLength());
        Assert.Equal(5, readout.GetProperty("requested").GetInt32());
        Assert.False(readout.GetProperty("countBoundHit").GetBoolean());

        // A randomness of zero is the model's ideal bundle: five bristles evenly spread from -20 to +20, all
        // starting at the beginning and all running the whole 200pt path.
        double[] offsets = readout.GetProperty("bristles").EnumerateArray()
            .Select(b => b.GetProperty("offset").GetDouble()).ToArray();
        Assert.Equal(new[] { -20.0, -10.0, 0.0, 10.0, 20.0 }, offsets);
        Assert.All(
            readout.GetProperty("bristles").EnumerateArray(),
            b => Assert.Equal(200.0, b.GetProperty("length").GetDouble(), 3));
    }

    /// <summary>
    /// **The readout reports the bound rather than serving a short bundle quietly.** A brush asking for more
    /// bristles than the engine draws comes back with the flag set and the number that was asked for, which is the
    /// difference between a known limit and a drawing that looks like it lost detail.
    /// </summary>
    [Fact]
    public void TheReadoutReportsABundleThatHitTheEnginesBound()
    {
        (AutomationContext context, _, _) = Host();
        EditorOperations.Invoke(context, "brush.create",
            Params(new { name = "Scrub", kind = "bristle", size = 20, count = 4_000 }));
        EditorOperations.Invoke(context, "brush.apply", Params(new { name = "Scrub" }));

        JsonElement readout = Bristles(context, "Scrub");
        Assert.True(readout.GetProperty("countBoundHit").GetBoolean());
        Assert.Equal(4_000, readout.GetProperty("requested").GetInt32());
        Assert.Equal(BristleBrushPath.MaxBristles, readout.GetProperty("bristles").GetArrayLength());
    }

    /// <summary>
    /// **The pen's pressure and tilt reach the readout, and through it the geometry.** Two readouts of one document
    /// differ only in the pen: a heavier pen opens the bundle and a laid-over pen turns every bristle. A readout
    /// that echoed its parameters instead of the geometry would answer both the same.
    /// </summary>
    [Fact]
    public void PressureAndTiltReachTheReadout()
    {
        (AutomationContext context, _, _) = Host();
        EditorOperations.Invoke(context, "brush.create",
            Params(new
            {
                name = "Scrub", kind = "bristle", size = 40, count = 7,
                randomness = 0.0, spread = 1.0, pressureSpread = 1.0, tiltTurn = 1.0, stiffness = 1.0,
            }));
        EditorOperations.Invoke(context, "brush.apply", Params(new { name = "Scrub" }));

        double Flat(JsonElement readout) => readout.GetProperty("bristles").EnumerateArray()
            .Max(b => Math.Abs(b.GetProperty("offset").GetDouble()));

        double light = Flat(Bristles(context, "Scrub", pressure: 0.0));
        double heavy = Flat(Bristles(context, "Scrub", pressure: 1.0));
        Assert.True(heavy > light, $"a heavier pen has to open the bundle ({heavy} against {light})");

        double Turn(JsonElement readout) => readout.GetProperty("bristles").EnumerateArray()
            .Max(b => Math.Abs(b.GetProperty("turnDegrees").GetDouble()));

        Assert.Equal(0.0, Turn(Bristles(context, "Scrub")), 6);
        Assert.Equal(25.0, Turn(Bristles(context, "Scrub", tilt: 25.0)), 6);
    }

    /// <summary>
    /// **The readout refuses a brush of another kind by name**, saying which door to use instead. A nib has no
    /// bristles, and answering with an empty list would look like a bundle that drew nothing.
    /// </summary>
    [Fact]
    public void TheBristleReadoutRefusesAnotherKindByName()
    {
        (AutomationContext context, _, _) = Host();
        EditorOperations.Invoke(context, "brush.create",
            Params(new { name = "Chisel", kind = "calligraphic", diameter = 10 }));

        EditorOperationException error = Assert.Throws<EditorOperationException>(
            () => EditorOperations.Invoke(context, "brush.bristles", Params(new { name = "Chisel" })));

        Assert.Contains("calligraphic", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// **A bristle brush can be removed from the document once it is made.** The two halves a rename and a delete
    /// have to reach: the brush's name, and the strokes that were referring to it - a renamed brush whose strokes
    /// still named the old name would leave them pointing at nothing, and a deleted brush whose strokes still
    /// carried it would draw a brush the document no longer has.
    /// </summary>
    [Fact]
    public void ABristleBrushCanBeRenamedAndDeleted()
    {
        (AutomationContext context, CadDocument document, PathItem path) = Host();
        EditorOperations.Invoke(context, "brush.create",
            Params(new { name = "Scrub", kind = "bristle", size = 22, count = 6 }));
        EditorOperations.Invoke(context, "brush.apply", Params(new { name = "Scrub" }));

        EditorOperations.Invoke(context, "brush.rename", Params(new { from = "Scrub", to = "Drybrush" }));

        Assert.Null(document.FindBrush("Scrub"));
        Assert.Equal(6, document.FindBrush("Drybrush")!.BristleSpec!.Count);
        Assert.Equal("Drybrush", path.Stroke.Brush!.Name);
        Assert.True(path.Stroke.Brush.IsBristle);

        EditorOperations.Invoke(context, "brush.delete", Params(new { name = "Drybrush" }));

        Assert.Null(document.FindBrush("Drybrush"));
        Assert.Null(path.Stroke.Brush);
    }

    /// <summary>
    /// **A stroke keeps its brush through a sidecar round trip made by the registry's own document**, so what a
    /// driver set up is what a reloaded document holds. This is the reach half of the serialization tests.
    /// </summary>
    [Fact]
    public void ARegistryMadeBristleBrushSurvivesASaveAndReload()
    {
        (AutomationContext context, CadDocument document, _) = Host();
        EditorOperations.Invoke(context, "brush.create",
            Params(new { name = "Scrub", kind = "bristle", size = 26, count = 15, tiltTurn = 1.5 }));
        EditorOperations.Invoke(context, "brush.apply", Params(new { name = "Scrub" }));

        string json = VCCad.Core.Serialization.VccadDocumentSerializer.Serialize(document);
        CadDocument reloaded = VCCad.Core.Serialization.VccadDocumentSerializer.Deserialize(json);

        BrushSpec brush = reloaded.FindBrush("Scrub")!;
        Assert.True(brush.IsBristle);
        Assert.Equal(15, brush.BristleSpec!.Count);
        Assert.Equal(1.5, brush.BristleSpec.TiltTurn, 6);
        Assert.True(reloaded.AllPaths().Single().Stroke.Brush!.IsBristle);
    }
}
