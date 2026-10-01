using System.Text.Json;
using VCCad.App.Automation;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Core.Serialization;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// Tablet dynamics through the operation registry, and through a save.
///
/// A curve is document state, so a document that did not carry its curves would draw differently on whichever
/// machine happened to have the right settings - which is the same reason a width profile travels with the file.
/// </summary>
public class DynamicsOperationTests
{
    private static JsonElement Params(object value) => JsonSerializer.SerializeToElement(value);

    private static (AutomationContext Context, CadDocument Document, PathItem Path) Host()
    {
        var vm = new EditorViewModel();
        var path = new PathItem { Name = "line", Fill = FillSpec.None };
        SubPath sub = path.AddSubPath(closed: false);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(60, 0)));
        path.Stroke = new StrokeSpec(true, ColorRgb.Black, 8, StrokeCap.Butt, StrokeJoin.Miter, 4);
        vm.Document.Artboards[0].Layers[0].AddItem(path);
        vm.SelectObject(path);

        return (new AutomationContext { ViewModel = vm }, vm.Document, path);
    }

    [Fact]
    public void DynamicsDefaultToPressureDrivingWidth()
    {
        (AutomationContext context, _, PathItem path) = Host();

        EditorOperations.Invoke(context, "style.setDynamics", Params(new { preset = "hard" }));

        Assert.True(path.Stroke.HasDynamics);
        DynamicsTargetSpec width = path.Stroke.Dynamics!.For(DynamicsTarget.Width);
        Assert.True(width.Enabled);
        Assert.Equal(DynamicsCurve.FromPreset(DynamicsPreset.Hard), width.Curve);

        string json = JsonSerializer.Serialize(EditorOperations.Invoke(context, "style.strokes", default));
        Assert.Contains("\"target\":\"Width\"", json, StringComparison.Ordinal);
    }

    /// <summary>Setting one target leaves the others as they were, rather than switching them all off.</summary>
    [Fact]
    public void SettingOneTargetKeepsTheOthers()
    {
        (AutomationContext context, _, PathItem path) = Host();

        EditorOperations.Invoke(context, "style.setDynamics", Params(new { target = "width", preset = "soft" }));
        EditorOperations.Invoke(context, "style.setDynamics", Params(new { target = "opacity", preset = "linear" }));

        Assert.True(path.Stroke.Dynamics!.For(DynamicsTarget.Width).Enabled);
        Assert.True(path.Stroke.Dynamics.For(DynamicsTarget.Opacity).Enabled);
        Assert.Equal(DynamicsCurve.FromPreset(DynamicsPreset.Soft),
            path.Stroke.Dynamics.For(DynamicsTarget.Width).Curve);
        Assert.Equal(DynamicsCurve.FromPreset(DynamicsPreset.Linear),
            path.Stroke.Dynamics.For(DynamicsTarget.Opacity).Curve);
    }

    [Fact]
    public void ACustomCurveIsKept()
    {
        (AutomationContext context, _, PathItem path) = Host();

        EditorOperations.Invoke(context, "style.setDynamics",
            Params(new { target = "width", curve = new[] { 0.2, 0.9, 0.8, 0.1 } }));

        Assert.Equal(new DynamicsCurve(0.2, 0.9, 0.8, 0.1), path.Stroke.Dynamics!.For(DynamicsTarget.Width).Curve);
    }

    /// <summary>
    /// A curve with the wrong number of numbers is refused: a curve is two points, and a three-number one is a
    /// mistake about which four they are - the mistake that would silently make the curve do something else.
    /// </summary>
    [Fact]
    public void AMalformedCurveIsRefused()
    {
        (AutomationContext context, _, PathItem path) = Host();

        EditorOperationException error = Assert.Throws<EditorOperationException>(
            () => EditorOperations.Invoke(context, "style.setDynamics",
                Params(new { target = "width", curve = new[] { 0.2, 0.9, 0.8 } })));

        Assert.Contains("four numbers", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(path.Stroke.HasDynamics);
    }

    [Fact]
    public void AnUnknownTargetOrPresetIsRefused()
    {
        (AutomationContext context, _, _) = Host();

        Assert.Throws<EditorOperationException>(
            () => EditorOperations.Invoke(context, "style.setDynamics", Params(new { target = "colour" })));
        Assert.Throws<EditorOperationException>(
            () => EditorOperations.Invoke(context, "style.setDynamics", Params(new { preset = "wobbly" })));
    }

    [Fact]
    public void DynamicsCanBeCleared()
    {
        (AutomationContext context, _, PathItem path) = Host();
        EditorOperations.Invoke(context, "style.setDynamics", Params(new { preset = "soft" }));

        EditorOperations.Invoke(context, "style.clearDynamics", default);

        Assert.False(path.Stroke.HasDynamics);
        Assert.Null(path.Stroke.Dynamics);
    }

    [Fact]
    public void SettingDynamicsIsOneUndoStep()
    {
        (AutomationContext context, _, PathItem path) = Host();

        EditorOperations.Invoke(context, "style.setDynamics", Params(new { preset = "hard" }));
        context.ViewModel.ActiveSession.Undo();

        Assert.False(path.Stroke.HasDynamics);
    }

    /// <summary>**A curve survives a save and a reload**, control points and all.</summary>
    [Fact]
    public void CurvesSurviveSaveAndReload()
    {
        (AutomationContext context, CadDocument document, PathItem path) = Host();
        EditorOperations.Invoke(context, "style.setDynamics",
            Params(new { target = "width", curve = new[] { 0.3, 0.1, 0.7, 0.9 } }));
        EditorOperations.Invoke(context, "style.setDynamics", Params(new { target = "angle", preset = "linear" }));

        CadDocument reloaded = VccadDocumentSerializer.Deserialize(
            VccadDocumentSerializer.SerializeToBytes(document));
        StrokeSpec back = reloaded.Artboards[0].Layers[0].Children.OfType<PathItem>().Single().Stroke;

        Assert.True(back.HasDynamics);
        Assert.Equal(new DynamicsCurve(0.3, 0.1, 0.7, 0.9), back.Dynamics!.For(DynamicsTarget.Width).Curve);
        Assert.True(back.Dynamics.For(DynamicsTarget.CalligraphicAngle).Enabled);
        Assert.False(back.Dynamics.For(DynamicsTarget.Opacity).Enabled);

        // Equal as a value, which catches a spec comparing by reference.
        Assert.Equal(path.Stroke, back);
    }

    [Fact]
    public void AStrokeWithoutDynamicsWritesNoMember()
    {
        (AutomationContext context, CadDocument document, _) = Host();

        string json = System.Text.Encoding.UTF8.GetString(VccadDocumentSerializer.SerializeToBytes(document));

        Assert.DoesNotContain("Dynamics", json);
    }
}
