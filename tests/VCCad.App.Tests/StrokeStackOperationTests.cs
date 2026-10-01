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
}
