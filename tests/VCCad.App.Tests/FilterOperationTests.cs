using System.Text.Json;
using VCCad.App.Automation;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Core.Serialization;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// Filters through the operation registry: creating one as a graph, and applying it to the selection.
///
/// The wiring is asserted rather than the order, because a filter is a directed graph - a driver that says
/// `in: "soft"` gets the buffer called `soft`, not whatever ran before it.
/// </summary>
public class FilterOperationTests
{
    private static JsonElement Params(object value) => JsonSerializer.SerializeToElement(value);

    private static (AutomationContext Context, PathItem Path) Host()
    {
        var vm = new EditorViewModel();
        var path = new PathItem { Name = "shape", Fill = FillSpec.Solid(ColorRgb.Black) };
        SubPath sub = path.AddSubPath(closed: true);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(10, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(10, 10)));
        path.Strokes.Clear();
        path.Strokes.Add(StrokeSpec.None);

        vm.Document.Artboards[0].Layers[0].AddItem(path);
        vm.SelectObject(path);
        return (new AutomationContext { ViewModel = vm }, path);
    }

    private static object ShadowGraph() => new
    {
        primitives = new object[]
        {
            new { kind = "gaussianBlur", @in = "SourceAlpha", radius = 3, result = "soft" },
            new { kind = "offset", @in = "soft", dx = 2, dy = 3, result = "moved" },
            new { kind = "flood", floodColor = new[] { 0, 0, 0 }, floodOpacity = 0.5, result = "ink" },
            new { kind = "composite", @in = "ink", in2 = "moved", @operator = "in", result = "shadow" },
            new { kind = "blend", @in = "SourceGraphic", in2 = "shadow", mode = "multiply" },
        },
    };

    [Fact]
    public void AFilterCanBeCreatedAsAGraph()
    {
        (AutomationContext context, _) = Host();

        EditorOperations.Invoke(context, "filter.create",
            Params(new { name = "drop", primitives = ((dynamic)ShadowGraph()).primitives }));

        FilterSpec filter = context.Document.FindFilter("drop")!;
        Assert.Equal(5, filter.Primitives.Count);

        // The wiring survived: two steps read `soft`, one of them not the step before it.
        Assert.Equal("soft", filter.Primitives[1].Input);
        Assert.Equal("moved", filter.Primitives[3].Input2);
        Assert.Equal("multiply", filter.Primitives[4].Mode);

        string json = JsonSerializer.Serialize(EditorOperations.Invoke(context, "filter.list", default));
        Assert.Contains("\"name\":\"drop\"", json, StringComparison.Ordinal);
        Assert.Contains("\"result\":\"shadow\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyingAFilterSetsTheReference()
    {
        (AutomationContext context, PathItem path) = Host();
        EditorOperations.Invoke(context, "filter.create",
            Params(new { name = "drop", primitives = ((dynamic)ShadowGraph()).primitives }));

        EditorOperations.Invoke(context, "filter.apply", Params(new { name = "drop" }));

        Assert.Equal("drop", path.FilterId);

        string json = JsonSerializer.Serialize(EditorOperations.Invoke(context, "filter.read", default));
        Assert.Contains("\"filter\":\"drop\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyNameRemovesTheFilter()
    {
        (AutomationContext context, PathItem path) = Host();
        EditorOperations.Invoke(context, "filter.create",
            Params(new { name = "drop", primitives = ((dynamic)ShadowGraph()).primitives }));
        EditorOperations.Invoke(context, "filter.apply", Params(new { name = "drop" }));

        EditorOperations.Invoke(context, "filter.apply", Params(new { name = "" }));

        Assert.Null(path.FilterId);
    }

    [Fact]
    public void AnUnknownFilterIsRefusedRatherThanApplied()
    {
        (AutomationContext context, PathItem path) = Host();

        EditorOperationException error = Assert.Throws<EditorOperationException>(
            () => EditorOperations.Invoke(context, "filter.apply", Params(new { name = "nothing" })));

        Assert.Contains("no filter called", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(path.FilterId);
    }

    [Fact]
    public void AFilterWithNoPrimitivesIsRefused()
    {
        (AutomationContext context, _) = Host();

        EditorOperationException error = Assert.Throws<EditorOperationException>(
            () => EditorOperations.Invoke(context, "filter.create", Params(new { name = "empty" })));

        Assert.Contains("at least one primitive", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A filter is document state, so it and the reference to it travel in the sidecar together.</summary>
    [Fact]
    public void TheFilterAndItsReferenceSurviveSaveAndReload()
    {
        (AutomationContext context, PathItem path) = Host();
        EditorOperations.Invoke(context, "filter.create",
            Params(new { name = "drop", primitives = ((dynamic)ShadowGraph()).primitives }));
        EditorOperations.Invoke(context, "filter.apply", Params(new { name = "drop" }));

        CadDocument reloaded = VccadDocumentSerializer.Deserialize(
            VccadDocumentSerializer.SerializeToBytes(context.Document));

        FilterSpec back = reloaded.FindFilter("drop")!;
        Assert.Equal(context.Document.FindFilter("drop")!, back);
        Assert.Equal("drop", reloaded.AllPaths().Single().FilterId);

        // And the reference is written as absent when there is none, so an unfiltered document is unchanged.
        string json = System.Text.Encoding.UTF8.GetString(
            VccadDocumentSerializer.SerializeToBytes(CadDocument.CreateDefault()));
        Assert.DoesNotContain("FilterId", json);
    }
}
