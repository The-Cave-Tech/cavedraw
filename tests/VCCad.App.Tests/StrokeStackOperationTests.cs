using System.Text.Json;
using VCCad.App.Automation;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Core.Serialization;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// The operations that put a path's stroke stack under a caller's control.
///
/// The stack model and its readers landed first, deliberately: making a second stroke **creatable** before the
/// canvas, the exporter, the dump and the hit-test could see one would have produced a document that renders
/// wrong. These are the operations that close the loop, and every one is checked through the same registry a
/// person's panel will use.
/// </summary>
public class StrokeStackOperationTests
{
    private static JsonElement Params(object value) => JsonSerializer.SerializeToElement(value);

    private static (AutomationContext Context, PathItem Path) Host()
    {
        var vm = new EditorViewModel();
        var path = new PathItem { Name = "line", Fill = FillSpec.None };
        SubPath sub = path.AddSubPath(closed: false);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(60, 20)));
        path.Stroke = new StrokeSpec(true, ColorRgb.Black, 4, StrokeCap.Butt, StrokeJoin.Miter, 4);
        vm.Document.Artboards[0].Layers[0].AddItem(path);
        vm.SelectObject(path);

        return (new AutomationContext { ViewModel = vm }, path);
    }

    private static string[] StrokeJson(AutomationContext context)
        => EditorOperations.Invoke(context, "style.strokes", default) is System.Collections.IEnumerable rows
            ? JsonSerializer.Serialize(rows).Split("\"width\":").Skip(1).ToArray()
            : Array.Empty<string>();

    [Fact]
    public void ReadBackReportsTheStack()
    {
        (AutomationContext context, PathItem path) = Host();
        EditorOperations.Invoke(context, "style.addStroke", Params(new { width = 2 }));

        string json = JsonSerializer.Serialize(EditorOperations.Invoke(context, "style.strokes", default));

        Assert.Contains("\"count\":2", json, StringComparison.Ordinal);
        Assert.Contains("\"width\":2", json, StringComparison.Ordinal);
        Assert.Contains("\"width\":4", json, StringComparison.Ordinal);
        Assert.Equal(2, path.Strokes.Count);
    }

    /// <summary>Adding with no parameters copies the top stroke, which is what pressing add gives a person.</summary>
    [Fact]
    public void AddingWithNoParametersCopiesTheTopStroke()
    {
        (AutomationContext context, PathItem path) = Host();
        path.Stroke = new StrokeSpec(true, ColorRgb.Red, 6, StrokeCap.Round, StrokeJoin.Bevel, 9,
            StrokeAlignment.Inside);

        EditorOperations.Invoke(context, "style.addStroke", default);

        Assert.Equal(2, path.Strokes.Count);
        Assert.Equal(path.Strokes[0], path.Strokes[1]);
        Assert.Equal(6.0, path.Strokes[1].Width, 3);
        Assert.Equal(StrokeCap.Round, path.Strokes[1].Cap);
    }

    /// <summary>And a parameter given overrides the copy rather than being ignored.</summary>
    [Fact]
    public void AddingWithParametersOverridesTheCopy()
    {
        (AutomationContext context, PathItem path) = Host();

        EditorOperations.Invoke(context, "style.addStroke",
            Params(new { color = new[] { 0, 0, 255 }, width = 1.5, cap = "round" }));

        StrokeSpec added = path.Strokes[^1];
        Assert.Equal(1.5, added.Width, 3);
        Assert.Equal(StrokeCap.Round, added.Cap);
        Assert.Equal(1.0, added.Color.B, 3);

        // The one it was copied from is untouched, which is the half that a "set the stroke" would have broken.
        Assert.Equal(4.0, path.Strokes[0].Width, 3);
        Assert.Equal(ColorRgb.Black, path.Strokes[0].Color);
    }

    [Fact]
    public void RemovingDefaultsToTheTopStroke()
    {
        (AutomationContext context, PathItem path) = Host();
        EditorOperations.Invoke(context, "style.addStroke", Params(new { width = 9 }));

        EditorOperations.Invoke(context, "style.removeStroke", default);

        StrokeSpec only = Assert.Single(path.Strokes);
        Assert.Equal(4.0, only.Width, 3);
    }

    [Fact]
    public void RemovingByIndexRemovesThatStroke()
    {
        (AutomationContext context, PathItem path) = Host();
        EditorOperations.Invoke(context, "style.addStroke", Params(new { width = 9 }));

        EditorOperations.Invoke(context, "style.removeStroke", Params(new { index = 0 }));

        StrokeSpec only = Assert.Single(path.Strokes);
        Assert.Equal(9.0, only.Width, 3);
    }

    /// <summary>
    /// Removing the last stroke leaves an invisible one, so the stack a caller reads is never empty - and a path
    /// that has lost its stroke does not become a path with no stroke at all.
    /// </summary>
    [Fact]
    public void RemovingTheLastStrokeLeavesAnInvisibleOne()
    {
        (AutomationContext context, PathItem path) = Host();

        EditorOperations.Invoke(context, "style.removeStroke", default);

        StrokeSpec only = Assert.Single(path.Strokes);
        Assert.False(only.IsVisible);
        Assert.False(path.HasVisibleStroke);
    }

    [Fact]
    public void ReorderingMovesAStrokeWithinTheStack()
    {
        (AutomationContext context, PathItem path) = Host();
        EditorOperations.Invoke(context, "style.addStroke", Params(new { width = 1 }));
        Assert.Equal(new[] { 4.0, 1.0 }, path.Strokes.Select(s => s.Width).ToArray());

        EditorOperations.Invoke(context, "style.reorderStroke", Params(new { from = 1, to = 0 }));

        Assert.Equal(new[] { 1.0, 4.0 }, path.Strokes.Select(s => s.Width).ToArray());
    }

    /// <summary>Every one of the three is a single undo step, because each is one command over the whole stack.</summary>
    [Theory]
    [InlineData("add")]
    [InlineData("remove")]
    [InlineData("reorder")]
    public void EachEditIsOneUndoStep(string kind)
    {
        (AutomationContext context, PathItem path) = Host();
        EditorOperations.Invoke(context, "style.addStroke", Params(new { width = 1 }));
        int before = path.Strokes.Count;

        switch (kind)
        {
            case "add":
                EditorOperations.Invoke(context, "style.addStroke", Params(new { width = 7 }));
                break;
            case "remove":
                EditorOperations.Invoke(context, "style.removeStroke", default);
                break;
            default:
                EditorOperations.Invoke(context, "style.reorderStroke", Params(new { from = 0, to = 1 }));
                break;
        }

        context.ViewModel.ActiveSession.Undo();

        Assert.Equal(before, path.Strokes.Count);
        Assert.Equal(new[] { 4.0, 1.0 }, path.Strokes.Select(s => s.Width).ToArray());
    }

    /// <summary>
    /// Outline Stroke refuses a path with a stack rather than expanding the bottom stroke and dropping the rest.
    /// A refusal leaves the path exactly as it was; a half-expanded path is a drawing somebody has lost.
    /// </summary>
    [Fact]
    public void ExpandingAStackIsRefusedRatherThanHalfDone()
    {
        (AutomationContext context, PathItem path) = Host();
        EditorOperations.Invoke(context, "style.addStroke", Params(new { width = 2 }));

        EditorOperationException error = Assert.Throws<EditorOperationException>(
            () => EditorOperations.Invoke(context, "path.expandStroke", default));

        Assert.Contains("more than one", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, path.Strokes.Count);
    }

    /// <summary>
    /// The test the whole issue was about: a stack built through the operations is visible to **every** reader,
    /// not just the one that was thought of. The dump and the export are the two that fail silently.
    /// </summary>
    [Fact]
    public void AStackBuiltThroughTheOperationsIsSeenByTheDumpAndTheSidecar()
    {
        (AutomationContext context, PathItem path) = Host();
        EditorOperations.Invoke(context, "style.addStroke",
            Params(new { color = new[] { 0, 0, 255 }, width = 1.5 }));

        // The dump must show both strokes: one that hid the second would make two different documents compare
        // equal, which quietly weakens every round-trip test built on that text.
        string dump = ModelDump.Of(context.Document);
        Assert.Contains("strokes=", dump, StringComparison.Ordinal);

        // And the sidecar must carry both, back to a stack of two.
        CadDocument reloaded = VccadDocumentSerializer.Deserialize(
            VccadDocumentSerializer.SerializeToBytes(context.Document));
        PathItem restored = reloaded.Artboards[0].Layers[0].Children.OfType<PathItem>().Single();

        Assert.Equal(2, restored.Strokes.Count);
        Assert.Equal(new[] { 4.0, 1.5 }, restored.Strokes.Select(s => s.Width).ToArray());
        Assert.Equal(1.0, restored.Strokes[1].Color.B, 3);
    }

    // ---------------------------------------------------------------- per-stroke paint

    /// <summary>
    /// **A person selects a stroke of the stack, and a driver names its index - they act on the same one.**
    ///
    /// This is the question a stack raises that a single stroke does not: "set the opacity" has to name *which*
    /// stroke it means. The answer here is the index, counted from the bottom, and the assertion is that setting
    /// it on index 1 leaves index 0 exactly as it was. An operation that wrote the top of the stack, or every
    /// stroke, would pass a test that only looked at the stroke it changed.
    /// </summary>
    [Fact]
    public void OpacityAndBlendReachTheStrokeTheIndexNamesAndNoOther()
    {
        (AutomationContext context, PathItem path) = Host();
        EditorOperations.Invoke(context, "style.addStroke", Params(new { width = 2 }));

        EditorOperations.Invoke(context, "style.setStroke",
            Params(new { index = 1, opacity = 0.35, blend = "multiply" }));

        Assert.Equal(0.35, path.Strokes[1].Opacity!.Value, 6);
        Assert.Equal(BlendMode.Multiply, path.Strokes[1].Blend);

        // The stroke beneath is untouched, including staying **unstated** - not turned into an explicit 1.
        Assert.Null(path.Strokes[0].Opacity);
        Assert.Null(path.Strokes[0].Blend);
    }

    /// <summary>
    /// **An unstated opacity is left as the stroke has it**, so naming one member of one stroke does not reset
    /// the others. The stroke pane's other fields already behave this way; the paint members have to as well, or
    /// typing an opacity would silently make every stroke opaque.
    /// </summary>
    [Fact]
    public void SettingBlendAloneLeavesTheOpacityAsTheStrokeHasIt()
    {
        (AutomationContext context, PathItem path) = Host();
        EditorOperations.Invoke(context, "style.setStroke", Params(new { index = 0, opacity = 0.5 }));

        EditorOperations.Invoke(context, "style.setStroke", Params(new { index = 0, blend = "screen" }));

        Assert.Equal(0.5, path.Strokes[0].Opacity!.Value, 6);
        Assert.Equal(BlendMode.Screen, path.Strokes[0].Blend);
    }

    /// <summary>
    /// **The stroke a caller set is the stroke a caller reads.** A readout that omitted the two members would
    /// make the operation's effect invisible to a driver with no eyes, which is the same defect as not having
    /// the operation at all. `effectiveOpacity` is reported beside the stated value so a caller that only wants
    /// to know what is drawn does not have to decide what an absent member means.
    /// </summary>
    [Fact]
    public void ReadingTheStackReportsOpacityAndBlendPerStroke()
    {
        (AutomationContext context, PathItem path) = Host();
        EditorOperations.Invoke(context, "style.addStroke", Params(new { width = 2 }));
        EditorOperations.Invoke(context, "style.setStroke", Params(new { index = 1, opacity = 0.25, blend = "screen" }));

        JsonElement rows = JsonSerializer.SerializeToElement(
            EditorOperations.Invoke(context, "style.strokes", default));
        JsonElement strokes = rows[0].GetProperty("strokes");

        Assert.Equal(JsonValueKind.Null, strokes[0].GetProperty("opacity").ValueKind);
        Assert.Equal(1.0, strokes[0].GetProperty("effectiveOpacity").GetDouble(), 6);
        Assert.Equal(JsonValueKind.Null, strokes[0].GetProperty("blend").ValueKind);

        Assert.Equal(0.25, strokes[1].GetProperty("opacity").GetDouble(), 6);
        Assert.Equal(0.25, strokes[1].GetProperty("effectiveOpacity").GetDouble(), 6);
        Assert.Equal("screen", strokes[1].GetProperty("blend").GetString());
    }

    /// <summary>
    /// **A blend mode this build does not know is refused, not painted as normal.**
    ///
    /// Reading an unknown name as `Normal` would composite the stroke over its backdrop and say nothing about
    /// it - a picture nobody asked for, arrived at silently. The refusal names the modes that do work, so a
    /// driver can correct itself from the error alone.
    /// </summary>
    [Fact]
    public void AnUnknownBlendModeIsRefusedByName()
    {
        (AutomationContext context, PathItem path) = Host();

        EditorOperationException error = Assert.Throws<EditorOperationException>(
            () => EditorOperations.Invoke(context, "style.setStroke",
                Params(new { index = 0, blend = "colour-burn" })));

        Assert.Contains("colour-burn", error.Message, StringComparison.Ordinal);
        Assert.Contains("multiply", error.Message, StringComparison.Ordinal);

        // Refused means the stroke is exactly as it was, not partly edited.
        Assert.Null(path.Strokes[0].Blend);
        Assert.Null(path.Strokes[0].Opacity);
    }

    /// <summary>
    /// **The paint members need an index, and are refused without one rather than guessed at.**
    ///
    /// `style.setStroke` without an index replaces the whole stack, so "opacity 0.4" could mean the first
    /// stroke, the last, or all of them - three different documents. A refusal is the only honest answer, and
    /// it leaves the path untouched.
    /// </summary>
    [Fact]
    public void OpacityWithoutAnIndexIsRefusedRatherThanAppliedToOneStroke()
    {
        (AutomationContext context, PathItem path) = Host();
        EditorOperations.Invoke(context, "style.addStroke", Params(new { width = 2 }));

        EditorOperationException error = Assert.Throws<EditorOperationException>(
            () => EditorOperations.Invoke(context, "style.setStroke", Params(new { opacity = 0.4 })));

        Assert.Contains("index", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.All(path.Strokes, stroke => Assert.Null(stroke.Opacity));
    }

    /// <summary>
    /// **One undo step, and it restores the absence rather than an explicit 1.** An undo that put back "opacity
    /// 1" where the stroke had stated nothing would leave a document that is not the one the person started
    /// with, and every save after that would carry the difference.
    /// </summary>
    [Fact]
    public void SettingOpacityIsOneUndoStepThatRestoresTheAbsence()
    {
        (AutomationContext context, PathItem path) = Host();
        EditorOperations.Invoke(context, "style.addStroke", Params(new { width = 2 }));

        EditorOperations.Invoke(context, "style.setStroke", Params(new { index = 1, opacity = 0.2 }));
        Assert.Equal(0.2, path.Strokes[1].Opacity!.Value, 6);

        context.ViewModel.ActiveSession.Undo();

        Assert.Null(path.Strokes[1].Opacity);
        Assert.Equal(2, path.Strokes.Count);
    }

    /// <summary>
    /// **`style.commonStroke` says when a selection disagrees about the paint**, and stays null - not 1 - when
    /// the whole selection agrees that nobody stated anything. A panel reading 1 for a stroke with no stated
    /// opacity would show a number the document does not contain.
    /// </summary>
    [Fact]
    public void TheCommonStrokeReportDistinguishesUnstatedFromMixed()
    {
        (AutomationContext context, PathItem path) = Host();
        EditorOperations.Invoke(context, "style.addStroke", Params(new { width = 2 }));

        JsonElement agreed = JsonSerializer.SerializeToElement(
            EditorOperations.Invoke(context, "style.commonStroke", Params(new { index = 1 })));
        Assert.Equal(JsonValueKind.Null, agreed.GetProperty("opacity").ValueKind);
        Assert.False(agreed.GetProperty("opacityMixed").GetBoolean());
        Assert.Equal(JsonValueKind.Null, agreed.GetProperty("blend").ValueKind);

        // Two selected paths that disagree: one states an opacity on this stroke, the other does not.
        var other = new PathItem { Name = "other", Fill = FillSpec.None };
        SubPath sub = other.AddSubPath(closed: false);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(10, 0)));
        other.Strokes.Clear();
        other.Strokes.Add(path.Strokes[0]);
        other.Strokes.Add(new StrokeSpec(true, ColorRgb.Black, 2, StrokeCap.Butt, StrokeJoin.Miter, 4)
        {
            Opacity = 0.5,
            Blend = BlendMode.Multiply,
        });
        context.ViewModel.Document.Artboards[0].Layers[0].AddItem(other);
        context.ViewModel.SelectRange(new LayerItem[] { path, other }, additive: false);

        JsonElement mixed = JsonSerializer.SerializeToElement(
            EditorOperations.Invoke(context, "style.commonStroke", Params(new { index = 1 })));

        Assert.True(mixed.GetProperty("opacityMixed").GetBoolean());
        Assert.True(mixed.GetProperty("blendMixed").GetBoolean());
        Assert.Equal(JsonValueKind.Null, mixed.GetProperty("opacity").ValueKind);
        Assert.Equal(JsonValueKind.Null, mixed.GetProperty("blend").ValueKind);
    }
}
