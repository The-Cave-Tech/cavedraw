using System.Text.Json;
using VCCad.App.Automation;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Core.Serialization;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// Brushes as reusable assets and as an operation: create, list, apply, read back, set, rename and delete
/// (issue #99).
///
/// The assertion that matters throughout is that editing the **asset** moves the strokes that name it, which is
/// the difference between an asset and a copy. The other half is that applying one changes what the stroke is
/// **drawn as** - an operation that set a member nothing rendered would pass every test that read the model.
/// </summary>
public class CalligraphicBrushOperationTests
{
    private static JsonElement Params(object value) => JsonSerializer.SerializeToElement(value);

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

    private static object Nib(string name = "Chisel", double angle = 90, double roundness = 0.25, double diameter = 20)
        => new { name, angle, roundness, diameter };

    [Fact]
    public void ABrushCanBeCreatedListedAndApplied()
    {
        (AutomationContext context, _, PathItem path) = Host();

        EditorOperations.Invoke(context, "brush.create", Params(Nib()));

        string list = JsonSerializer.Serialize(EditorOperations.Invoke(context, "brush.list", default));
        Assert.Contains("Chisel", list, StringComparison.Ordinal);
        Assert.Contains("\"roundness\":0.25", list, StringComparison.Ordinal);
        Assert.Contains("\"diameter\":20", list, StringComparison.Ordinal);
        Assert.Contains("calligraphic", list, StringComparison.Ordinal);

        // Creating it does not apply it: an asset sits in the document until something uses it.
        Assert.False(path.Stroke.HasBrush);

        EditorOperations.Invoke(context, "brush.apply", Params(new { name = "Chisel" }));

        Assert.True(path.Stroke.HasBrush);
        Assert.Equal("Chisel", path.Stroke.Brush!.Name);
        Assert.Equal(0.25, path.Stroke.Brush.Roundness, 6);
    }

    /// <summary>
    /// **The operation and the geometry are one path.** Applying a nib has to change what the stroke is drawn
    /// as - a broadside nib draws the diameter, not the stroke's own 4pt width - and the plan the canvas and the
    /// exporter both consume is where that is visible.
    /// </summary>
    [Fact]
    public void ApplyingABrushChangesTheGeometryTheStrokeIsDrawnAs()
    {
        (AutomationContext context, _, PathItem path) = Host();
        EditorOperations.Invoke(context, "brush.create", Params(Nib(angle: 90, roundness: 0.25, diameter: 20)));
        EditorOperations.Invoke(context, "brush.apply", Params(new { name = "Chisel" }));

        StrokeRenderPlan plan = StrokeOutlineBuilder.Plan(path, path.Stroke);

        Assert.True(plan.IsOutline);
        IReadOnlyList<Point2D> outline = Assert.Single(plan.Outlines);
        Assert.Equal(20.0, outline.Max(p => p.Y) - outline.Min(p => p.Y), 6);

        // And taking the brush off puts the stroke back at its own width, which is the state before applying it.
        EditorOperations.Invoke(context, "brush.clear", default);
        Assert.False(path.Stroke.HasBrush);
        Assert.Equal(4.0, StrokeOutlineBuilder.Plan(path, path.Stroke).Width, 6);
    }

    [Fact]
    public void TwoBrushesCannotShareAName()
    {
        (AutomationContext context, CadDocument document, _) = Host();
        EditorOperations.Invoke(context, "brush.create", Params(Nib()));

        EditorOperationException error = Assert.Throws<EditorOperationException>(
            () => EditorOperations.Invoke(context, "brush.create", Params(Nib())));

        Assert.Contains("already a brush", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(document.Brushes);
    }

    /// <summary>
    /// **The test that says a brush is an asset.** Renaming it moves the strokes that named it, so the stroke
    /// still points at something real and draws the same nib afterwards.
    /// </summary>
    [Fact]
    public void RenamingABrushMovesTheStrokesThatUseIt()
    {
        (AutomationContext context, CadDocument document, PathItem path) = Host();
        EditorOperations.Invoke(context, "brush.create", Params(Nib()));
        EditorOperations.Invoke(context, "brush.apply", Params(new { name = "Chisel" }));

        EditorOperations.Invoke(context, "brush.rename", Params(new { from = "Chisel", to = "Flat" }));

        Assert.Equal("Flat", document.Brushes.Single().Name);
        Assert.Equal("Flat", path.Stroke.Brush!.Name);
        Assert.Equal(0.25, path.Stroke.Brush.Roundness, 6);
        Assert.NotNull(document.FindBrush(path.Stroke.Brush.Name));
    }

    [Fact]
    public void UndoingARenameRestoresBothTheAssetAndTheStroke()
    {
        (AutomationContext context, CadDocument document, PathItem path) = Host();
        EditorOperations.Invoke(context, "brush.create", Params(Nib()));
        EditorOperations.Invoke(context, "brush.apply", Params(new { name = "Chisel" }));
        EditorOperations.Invoke(context, "brush.rename", Params(new { from = "Chisel", to = "Flat" }));

        context.ViewModel.ActiveSession.Undo();

        Assert.Equal("Chisel", document.Brushes.Single().Name);
        Assert.Equal("Chisel", path.Stroke.Brush!.Name);
    }

    [Fact]
    public void DeletingABrushClearsItFromTheStrokesAndKeepsTheirWidth()
    {
        (AutomationContext context, CadDocument document, PathItem path) = Host();
        EditorOperations.Invoke(context, "brush.create", Params(Nib()));
        EditorOperations.Invoke(context, "brush.apply", Params(new { name = "Chisel" }));

        EditorOperations.Invoke(context, "brush.delete", Params(new { name = "Chisel" }));

        Assert.Empty(document.Brushes);
        Assert.Null(path.Stroke.Brush);
        Assert.Equal(4.0, path.Stroke.Width, 3);
    }

    [Fact]
    public void EditingABrushReachesTheStrokesThatUseIt()
    {
        (AutomationContext context, CadDocument document, PathItem path) = Host();
        EditorOperations.Invoke(context, "brush.create", Params(Nib()));
        EditorOperations.Invoke(context, "brush.apply", Params(new { name = "Chisel" }));

        EditorOperations.Invoke(context, "brush.set", Params(new { name = "Chisel", angle = 30 }));

        Assert.Equal(30.0, document.Brushes.Single().AngleDegrees, 6);
        Assert.Equal(30.0, path.Stroke.Brush!.AngleDegrees, 6);

        // A member that was not given keeps what it had rather than being reset to this operation's default.
        Assert.Equal(0.25, document.Brushes.Single().Roundness, 6);
    }

    /// <summary>A roundness outside 0..1 is clamped rather than stored, because a nib is a shape that has none.</summary>
    [Fact]
    public void ANibIsClampedToAShapeItCanHave()
    {
        (AutomationContext context, CadDocument document, _) = Host();

        EditorOperations.Invoke(context, "brush.create",
            Params(new { name = "Chisel", angle = 0, roundness = 7.5, diameter = -3 }));

        Assert.Equal(1.0, document.Brushes.Single().Roundness, 6);
        Assert.Equal(0.0, document.Brushes.Single().Diameter, 6);
    }

    [Fact]
    public void AnUnknownBrushIsRefusedRatherThanIgnored()
    {
        (AutomationContext context, _, _) = Host();

        Assert.Throws<EditorOperationException>(
            () => EditorOperations.Invoke(context, "brush.apply", Params(new { name = "Nothing" })));
        Assert.Throws<EditorOperationException>(
            () => EditorOperations.Invoke(context, "brush.rename", Params(new { from = "Nothing", to = "Something" })));
        Assert.Throws<EditorOperationException>(
            () => EditorOperations.Invoke(context, "brush.delete", Params(new { name = "Nothing" })));
        Assert.Throws<EditorOperationException>(
            () => EditorOperations.Invoke(context, "brush.set", Params(new { name = "Nothing", angle = 10 })));
    }

    /// <summary>
    /// **A reference to an asset the document does not have is reported, not silently defaulted.** A stroke holds
    /// its brush as a value and keeps drawing after the asset is gone, which is exactly why this needs saying.
    /// </summary>
    [Fact]
    public void AStrokeNamingAMissingBrushIsReported()
    {
        (AutomationContext context, CadDocument document, PathItem path) = Host();
        EditorOperations.Invoke(context, "brush.create", Params(Nib()));
        EditorOperations.Invoke(context, "brush.apply", Params(new { name = "Chisel" }));

        Assert.Empty(JsonSerializer.Deserialize<JsonElement[]>(
            JsonSerializer.Serialize(EditorOperations.Invoke(context, "brush.missing", default)))!);

        document.RemoveBrush("Chisel");

        JsonElement[] missing = JsonSerializer.Deserialize<JsonElement[]>(
            JsonSerializer.Serialize(EditorOperations.Invoke(context, "brush.missing", default)))!;

        JsonElement report = Assert.Single(missing);
        Assert.Equal("Chisel", report.GetProperty("brush").GetString());
        Assert.Equal(path.Id.ToString(), report.GetProperty("itemId").GetString());
    }

    /// <summary>**Reading a stroke back says which brush it carries** - the read half of applying one.</summary>
    [Fact]
    public void ReadingAStrokeReportsTheBrushItCarries()
    {
        (AutomationContext context, _, _) = Host();
        EditorOperations.Invoke(context, "brush.create", Params(Nib(angle: 35, roundness: 0.2, diameter: 24)));
        EditorOperations.Invoke(context, "brush.apply", Params(new { name = "Chisel" }));

        JsonElement strokes = JsonSerializer.SerializeToElement(
            EditorOperations.Invoke(context, "style.strokes", default));

        JsonElement brush = strokes[0].GetProperty("strokes")[0].GetProperty("brush");
        Assert.Equal("Chisel", brush.GetProperty("name").GetString());
        Assert.Equal(35.0, brush.GetProperty("angle").GetDouble(), 6);
        Assert.Equal(0.2, brush.GetProperty("roundness").GetDouble(), 6);
        Assert.Equal(24.0, brush.GetProperty("diameter").GetDouble(), 6);
    }

    /// <summary>Applying a brush does not have to be undone by deleting the asset: brush.clear is the plain state.</summary>
    [Fact]
    public void ClearingABrushKeepsTheAsset()
    {
        (AutomationContext context, CadDocument document, PathItem path) = Host();
        EditorOperations.Invoke(context, "brush.create", Params(Nib()));
        EditorOperations.Invoke(context, "brush.apply", Params(new { name = "Chisel" }));

        EditorOperations.Invoke(context, "brush.clear", default);

        Assert.Null(path.Stroke.Brush);
        Assert.Single(document.Brushes);
    }

    /// <summary>The library is part of the document, so it has to survive a round trip.</summary>
    [Fact]
    public void TheLibrarySurvivesSaveAndReload()
    {
        (AutomationContext context, CadDocument document, PathItem path) = Host();
        EditorOperations.Invoke(context, "brush.create", Params(Nib(angle: 35, roundness: 0.2, diameter: 24)));
        EditorOperations.Invoke(context, "brush.apply", Params(new { name = "Chisel" }));

        CadDocument reloaded = VccadDocumentSerializer.Deserialize(
            VccadDocumentSerializer.SerializeToBytes(document));

        BrushSpec brush = Assert.Single(reloaded.Brushes);
        Assert.Equal("Chisel", brush.Name);
        Assert.Equal(35.0, brush.AngleDegrees, 6);

        PathItem restored = reloaded.Artboards[0].Layers[0].Children.OfType<PathItem>().Single();
        Assert.Equal("Chisel", restored.Stroke.Brush!.Name);
        Assert.Empty(reloaded.MissingBrushes());
    }
}
