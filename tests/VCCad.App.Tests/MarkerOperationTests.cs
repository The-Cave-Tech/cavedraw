using System.Text.Json;
using VCCad.App.Automation;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// The marker operations (issue #202): what a path's three slots name, and setting them as one undo step.
///
/// The reference is the thing this asserts - the *model*, not that a call returned - because a marker is a property
/// of the path rather than a second object beside it, and the point of the member is that it can be read and
/// changed. A **dangling reference is a first-class state**: it is what the reader produces for a file that names a
/// marker nobody defines, so setting one is allowed and reported as unresolved rather than refused.
/// </summary>
public class MarkerOperationTests
{
    private static (AutomationContext Context, PathItem Path) Host()
    {
        var viewModel = new EditorViewModel();
        var path = new PathItem { Name = "line", Fill = FillSpec.None };
        SubPath sub = path.AddSubPath(closed: false);
        sub.Nodes.Add(new PathNode(new Point2D(10, 10)));
        sub.Nodes.Add(new PathNode(new Point2D(60, 10)));
        viewModel.Document.Artboards[0].Layers[0].AddItem(path);

        var context = new AutomationContext { ViewModel = viewModel };
        context.Session.SelectObject(path);
        return (context, path);
    }

    private static JsonElement Invoke(AutomationContext context, string op, object? parameters = null)
        => JsonSerializer.SerializeToElement(EditorOperations.Invoke(
            context, op, parameters is null ? default : JsonSerializer.SerializeToElement(parameters)));

    [Fact]
    public void SettingASlotChangesThePathAndSaysWhetherTheNameResolves()
    {
        (AutomationContext context, PathItem path) = Host();

        // A name the document does not define: set, and reported unresolved rather than refused - the reader
        // produces exactly this state for a dangling `marker-end`.
        JsonElement dangling = Invoke(context, "marker.set", new { slot = "end", name = "arrow" });
        Assert.Equal(1, dangling.GetProperty("changed").GetInt32());
        Assert.False(dangling.GetProperty("resolved").GetBoolean());
        Assert.Equal("arrow", path.MarkerEnd);

        // A name the library does hold resolves.
        context.ViewModel.Document.AddDefinition("head");
        JsonElement known = Invoke(context, "marker.set", new { slot = "start", name = "head" });
        Assert.True(known.GetProperty("resolved").GetBoolean());
        Assert.Equal("head", path.MarkerStart);
        Assert.Equal("arrow", path.MarkerEnd);

        // And the readout a picker gets names both slots, with the resolution of each.
        JsonElement listed = Invoke(context, "marker.list");
        JsonElement entry = Assert.Single(listed.EnumerateArray());
        Assert.Equal(path.Id, entry.GetProperty("itemId").GetGuid());
        Assert.Equal("head", entry.GetProperty("start").GetProperty("id").GetString());
        Assert.True(entry.GetProperty("start").GetProperty("resolved").GetBoolean());
        Assert.Equal("arrow", entry.GetProperty("end").GetProperty("id").GetString());
        Assert.False(entry.GetProperty("end").GetProperty("resolved").GetBoolean());
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("mid").ValueKind);
    }

    [Fact]
    public void ClearingASlotUndoesBackToWhatWasThere()
    {
        (AutomationContext context, PathItem path) = Host();
        Invoke(context, "marker.set", new { slot = "mid", name = "dot" });
        Assert.Equal("dot", path.MarkerMid);

        JsonElement cleared = Invoke(context, "marker.set", new { slot = "mid", name = (string?)null });
        Assert.Equal(1, cleared.GetProperty("changed").GetInt32());
        Assert.Null(path.MarkerMid);

        // Undo restores the reference that was there, not merely "a" reference - the command keeps the previous
        // value, including nothing.
        Invoke(context, "document.undo");
        Assert.Equal("dot", path.MarkerMid);
    }

    [Fact]
    public void AnUnknownSlotIsRefusedByNameAndChangesNothing()
    {
        (AutomationContext context, PathItem path) = Host();

        Assert.Throws<EditorOperationException>(() => Invoke(context, "marker.set", new { slot = "middle", name = "x" }));
        Assert.Null(path.MarkerStart);
        Assert.Null(path.MarkerMid);
        Assert.Null(path.MarkerEnd);
    }
}
