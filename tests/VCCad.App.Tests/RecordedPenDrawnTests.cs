using System.Text.Json;
using VCCad.App.Automation;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Core.Serialization;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// **What a drawn tablet stroke does to the brushes whose response is resolved at render time** (issue #107).
///
/// The curves and the width profile they produce were already tested, and a stroke drew the profile the pressure
/// made. What was missing is the **record of what the pen actually reported**, which is the only input a scatter
/// brush's copy size and a bristle bundle's spread can read at render time - they are not baked into geometry the
/// way a width profile is. So both kinds were placed for a fully pressed, upright pen and the readings the seams
/// already honoured were unreachable from a stored document.
///
/// These tests draw through `path.drawFreehand` - the same session call the canvas pencil makes, so a person and a
/// driver draw one line - and then read the **geometry the renderers fill** back out of the operation registry. The
/// readout is deliberately asked with no `pressure`, because that is the question a driver asks after drawing: what
/// does this line look like? Before the record existed the answer was a default; now it is the drawing.
/// </summary>
public class RecordedPenDrawnTests
{
    private static JsonElement Params(object value) => JsonSerializer.SerializeToElement(value);

    private static JsonElement Invoke(AutomationContext context, string op, object? parameters = null)
        => JsonSerializer.SerializeToElement(
            EditorOperations.Invoke(context, op, parameters is null ? default : Params(parameters)));

    /// <summary>A ten by ten square of artwork on the pasteboard, which is what a scatter brush repeats.</summary>
    private static PathItem Art(EditorViewModel vm)
    {
        var art = new PathItem { Name = "copy", Fill = FillSpec.Solid(ColorRgb.Black) };
        SubPath sub = art.AddSubPath(closed: true);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(10, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(10, 10)));
        sub.Nodes.Add(new PathNode(new Point2D(0, 10)));
        vm.Document.Orphans.AddItem(art);
        return art;
    }

    private static AutomationContext Host()
    {
        var vm = new EditorViewModel();
        return new AutomationContext { ViewModel = vm };
    }

    /// <summary>A straight line drawn from the points a mouse reported, in document space.</summary>
    private static PathItem DrawWithMouse(AutomationContext context)
    {
        JsonElement result = Invoke(context, "path.drawFreehand", new
        {
            points = new[] { new[] { 0.0, 0.0 }, new[] { 200.0, 0.0 } },
        });

        Assert.True(result.GetProperty("drawn").GetBoolean(), "the stroke should have been drawn");
        return Assert.IsType<PathItem>(context.Document.FindItem(result.GetProperty("itemId").GetGuid()));
    }

    /// <summary>The samples a pen reports along a straight line: position, time, pressure and tilt per sample.</summary>
    private static object Sampling(double from, double to, double tilt = 0.0)
        => new
        {
            samples = new object[]
            {
                new { x = 0.0, y = 0.0, time = 0.0, pressure = from, tiltX = tilt, tiltY = 0.0 },
                new { x = 200.0, y = 0.0, time = 1.0, pressure = to, tiltX = tilt, tiltY = 0.0 },
            },
        };

    private static PathItem DrawWithPen(AutomationContext context, double from, double to, double tilt = 0.0)
    {
        JsonElement result = Invoke(context, "path.drawFreehand", Sampling(from, to, tilt));
        Assert.True(result.GetProperty("drawn").GetBoolean(), "the stroke should have been drawn");
        return Assert.IsType<PathItem>(context.Document.FindItem(result.GetProperty("itemId").GetGuid()));
    }

    // ---------------------------------------------------------------------------------------------------------
    // 1. The pen reaches the stroke that was drawn, and the registry reports it.
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// **A pen-drawn stroke keeps the pen's own readings**, and a mouse-drawn one keeps none - the absence rule
    /// that makes "nobody recorded a pen" different from "the pen was pressed all the way". The registry reports
    /// the record, because it is the input every render-time response is resolved from and a driver with no eyes
    /// has no other way to know it.
    /// </summary>
    [Fact]
    public void APenDrawnStrokeRecordsThePenAndAMouseDrawnOneDoesNot()
    {
        AutomationContext pen = Host();
        PathItem recorded = DrawWithPen(pen, 0.1, 1.0);

        Assert.True(recorded.Stroke.HasPen);
        Assert.Equal(0.1, recorded.Stroke.PressureAt(0.0), 6);
        Assert.Equal(1.0, recorded.Stroke.PressureAt(1.0), 6);

        JsonElement strokes = Invoke(pen, "style.strokes");
        JsonElement described = strokes[0].GetProperty("strokes")[0].GetProperty("pen");
        Assert.Equal(JsonValueKind.Object, described.ValueKind);
        Assert.Equal(0.1, described.GetProperty("samples")[0].GetProperty("pressure").GetDouble(), 6);

        AutomationContext mouse = Host();
        PathItem plain = DrawWithMouse(mouse);

        Assert.False(plain.Stroke.HasPen);
        JsonElement plainStrokes = Invoke(mouse, "style.strokes");
        Assert.Equal(
            JsonValueKind.Null,
            plainStrokes[0].GetProperty("strokes")[0].GetProperty("pen").ValueKind);
    }

    // ---------------------------------------------------------------------------------------------------------
    // 2. A scatter brush's copies are drawn the size the pen was at each copy.
    // ---------------------------------------------------------------------------------------------------------

    private static AutomationContext Scatter()
    {
        AutomationContext context = Host();
        PathItem asset = Art(context.ViewModel);
        BrushSpec brush = BrushSpec.Scatter(
            "Spray", asset.Id, size: 20.0,
            spacing: new ScatterParameter(50.0),
            dynamics: DynamicsSpec.RespondingTo(DynamicsTarget.ScatterScale));
        context.Document.AddBrush(brush);
        context.ViewModel.CurrentStroke = context.ViewModel.CurrentStroke with { Width = 20, Brush = brush };
        return context;
    }

    /// <summary>
    /// **The readout a driver asks for after drawing describes the drawing.** No `pressure` is passed, so the
    /// answer comes from the stroke's own record: the copy near where the pen was light is small and the one where
    /// it was heavy is full size. Before the record existed this readout had only a default and answered with the
    /// brush's own size for every copy, which is exactly the divergence the parity rule exists to prevent.
    /// </summary>
    [Fact]
    public void TheScatterReadoutFollowsThePenTheStrokeWasDrawnWith()
    {
        AutomationContext context = Scatter();
        PathItem drawn = DrawWithPen(context, 0.1, 1.0);

        JsonElement read = Invoke(context, "brush.scatter", new { name = "Spray" })[0];
        JsonElement[] copies = read.GetProperty("copies").EnumerateArray().ToArray();

        Assert.True(copies.Length >= 2, "a two-hundred point line at a fifty pitch should place several copies");
        Assert.Equal(0.1, copies[0].GetProperty("scale").GetDouble(), 4);
        Assert.True(copies[^1].GetProperty("scale").GetDouble() > copies[0].GetProperty("scale").GetDouble() * 2.0,
            "the copy where the pen was heaviest should be drawn larger than where it was lightest");

        // And the record is echoed, so a caller can tell "read from the stroke" from "read from a default".
        Assert.Equal(JsonValueKind.Object, read.GetProperty("pen").ValueKind);
    }

    /// <summary>
    /// The control: **the same brush and the same path drawn with a mouse places every copy at the brush's size.**
    /// A mouse reports no pen, so nothing is recorded and nothing is applied - the rule that has kept every
    /// mouse-drawn line the width the tool says.
    /// </summary>
    [Fact]
    public void AMouseDrawnScatterStrokePlacesEveryCopyAtTheBrushsSize()
    {
        AutomationContext context = Scatter();
        DrawWithMouse(context);

        JsonElement read = Invoke(context, "brush.scatter", new { name = "Spray" })[0];
        Assert.Equal(JsonValueKind.Null, read.GetProperty("pen").ValueKind);
        Assert.All(
            read.GetProperty("copies").EnumerateArray(),
            copy => Assert.Equal(1.0, copy.GetProperty("scale").GetDouble(), 6));
    }

    /// <summary>
    /// **The record is document state, and the drawing follows it through a save and a reload.** A response that
    /// vanished when the file was re-opened would draw a different picture on the next machine, which is exactly
    /// the defect this member exists to remove.
    /// </summary>
    [Fact]
    public void TheRecordedPenSurvivesASaveAndTheDrawingFollowsIt()
    {
        AutomationContext context = Scatter();
        PathItem drawn = DrawWithPen(context, 0.1, 1.0);
        double before = Invoke(context, "brush.scatter", new { name = "Spray" })[0]
            .GetProperty("copies")[0].GetProperty("scale").GetDouble();

        byte[] bytes = VccadDocumentSerializer.SerializeToBytes(context.Document);
        var reloaded = new EditorViewModel();
        CadDocument document = VccadDocumentSerializer.Deserialize(bytes);
        PathItem back = document.Artboards[0].Layers[0].Children.OfType<PathItem>()
            .Concat(document.Orphans.Children.OfType<PathItem>())
            .First(item => item.Id == drawn.Id);

        Assert.True(back.Stroke.HasPen);
        Assert.Equal(0.1, back.Stroke.PressureAt(0.0), 6);

        // The seam the renderers fill reads the reloaded record, and places the same copy at the same size.
        BrushSpec brush = document.FindBrush("Spray")!;
        double after = ScatterBrushPath
            .Placements(back, brush, _ => new Rect2D(0, 0, 10, 10), 1.0, back.Stroke.Pen)[0].Scale;
        Assert.Equal(before, after, 6);
    }

    // ---------------------------------------------------------------------------------------------------------
    // 3. A bristle brush's bundle is drawn at the pressure the pen was under.
    // ---------------------------------------------------------------------------------------------------------

    private static AutomationContext Bristle()
    {
        AutomationContext context = Host();
        BrushSpec brush = BrushSpec.Bristle(
            "Bristle", size: 40.0,
            bristles: new BristleBrushSpec(
                Count: 9, Spread: 1.0, PressureSpread: 1.0, Randomness: 0.0, Stiffness: 1.0));
        context.Document.AddBrush(brush);
        context.ViewModel.CurrentStroke = context.ViewModel.CurrentStroke with { Width = 20, Brush = brush };
        return context;
    }

    private static double WidestBristle(JsonElement read)
        => read.GetProperty("bristles").EnumerateArray()
            .Max(bristle => Math.Abs(bristle.GetProperty("offset").GetDouble()));

    /// <summary>
    /// **A bristle bundle drawn under a light pen is narrower than the same bundle drawn with a mouse.** The
    /// readout is again asked with no `pressure`: what the pen recorded opens or closes the bundle, and the
    /// geometry the canvas and the exporters fill is that bundle.
    /// </summary>
    [Fact]
    public void ABristleBundleDrawnWithALightPenIsNarrowerThanOneDrawnWithAMouse()
    {
        AutomationContext light = Bristle();
        DrawWithPen(light, 0.1, 0.1);
        double narrow = WidestBristle(Invoke(light, "brush.bristles", new { name = "Bristle" })[0]);

        AutomationContext mouse = Bristle();
        DrawWithMouse(mouse);
        double full = WidestBristle(Invoke(mouse, "brush.bristles", new { name = "Bristle" })[0]);

        Assert.Equal(20.0, full, 6);
        Assert.True(narrow < full / 5.0,
            $"a tenth-pressed pen should close the bundle: {narrow} against {full}");
    }

    /// <summary>
    /// **A stated pressure overrides the stroke's own record.** The operation parameter stays an override rather
    /// than being ignored, so a driver can ask what the bundle would look like under a different pen without
    /// redrawing the line - and the readout says which pen it used.
    /// </summary>
    [Fact]
    public void AStatedPressureOverridesTheStrokesRecord()
    {
        AutomationContext context = Bristle();
        DrawWithPen(context, 0.1, 0.1);

        JsonElement stated = Invoke(context, "brush.bristles", new { name = "Bristle", pressure = 1.0 })[0];
        Assert.Equal(20.0, WidestBristle(stated), 6);
        Assert.Equal(1.0, stated.GetProperty("pressure").GetDouble(), 6);

        // The pen used is the stated one, and the readout says so - it does not silently keep the stroke's record.
        JsonElement pen = stated.GetProperty("pen");
        Assert.Equal(JsonValueKind.Object, pen.ValueKind);
        Assert.Equal(1.0, pen.GetProperty("samples")[0].GetProperty("pressure").GetDouble(), 6);
    }
}
