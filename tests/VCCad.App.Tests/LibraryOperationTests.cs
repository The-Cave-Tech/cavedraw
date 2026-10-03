using System.Text.Json;
using VCCad.App.Automation;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// The library interface over definitions (issue #135), through the operations a person uses.
///
/// The clauses are the issue's: a placement is an **instance** that follows its definition when the definition
/// changes; the count of places that use a definition is readable before someone edits it; and deleting a
/// definition that is still referenced is **refused** rather than silently orphaning the document.
/// </summary>
public class LibraryOperationTests
{
    private static AutomationContext Context(out CadDocument document, out ArtGroup definition)
    {
        var viewModel = new EditorViewModel();
        document = viewModel.Document;

        definition = document.AddDefinition("sym");
        var path = new PathItem { Name = "inside", Fill = FillSpec.Solid(ColorRgb.Black) };
        SubPath sub = path.AddSubPath(closed: true);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(10, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(10, 10)));
        definition.AddItem(path);

        return new AutomationContext { ViewModel = viewModel };
    }

    private static JsonElement Invoke(AutomationContext context, string op, object? parameters = null)
        => JsonSerializer.SerializeToElement(EditorOperations.Invoke(
            context, op, parameters is null ? default : JsonSerializer.SerializeToElement(parameters)));

    [Fact]
    public void APlacementIsAnInstanceThatFollowsItsDefinition()
    {
        AutomationContext context = Context(out CadDocument document, out ArtGroup definition);

        JsonElement placed = Invoke(context, "definition.place", new { name = "sym", x = 40.0, y = 30.0 });
        Assert.True(placed.GetProperty("placed").GetBoolean());
        Assert.Equal("sym", placed.GetProperty("sourceId").GetString());

        // The placement is an instance, not a copy: it carries the id of the definition it came from.
        ArtGroup instance = Assert.Single(document.AllGroups().Where(InstanceResolver.IsInstance));
        Assert.Equal("sym", instance.SourceId);
        Assert.Single(instance.Children);

        // And the library says one place uses it, which is what a person reads before editing it.
        JsonElement list = Invoke(context, "definition.list");
        JsonElement entry = Assert.Single(list.EnumerateArray());
        Assert.Equal("sym", entry.GetProperty("name").GetString());
        Assert.Equal(1, entry.GetProperty("usedBy").GetInt32());
        Assert.Equal(instance.Id, entry.GetProperty("instanceIds")[0].GetGuid());

        // **Editing the definition reaches the instance** on the document-wide refresh: a shape added to the
        // definition is a shape the instance draws. An instance whose content did not follow would still hold one.
        var added = new PathItem { Name = "added", Fill = FillSpec.Solid(ColorRgb.Black) };
        SubPath sub = added.AddSubPath(closed: true);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(5, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(5, 5)));
        definition.AddItem(added);

        JsonElement refreshed = Invoke(context, "instance.refresh");
        Assert.Equal(1, refreshed.GetProperty("refreshed").GetInt32());
        Assert.Equal(2, instance.Children.Count);
    }

    [Fact]
    public void AReferencedDefinitionCannotBeDeletedAndAnUnreferencedOneCan()
    {
        AutomationContext context = Context(out CadDocument document, out ArtGroup definition);
        Invoke(context, "definition.place", new { name = "sym", x = 0.0, y = 0.0 });

        // Refused, and by name with the places that use it - the document is left exactly as it was.
        JsonElement refused = Invoke(context, "definition.delete", new { name = "sym" });
        Assert.False(refused.GetProperty("deleted").GetBoolean());
        Assert.Contains("still used by 1", refused.GetProperty("refusal").GetString()!);
        Assert.Same(definition, document.FindDefinition("sym"));
        Assert.Single(document.AllGroups().Where(InstanceResolver.IsInstance));

        // With the instance gone, the definition can be deleted. The instance is removed through the operation a
        // person's delete control uses, not by reaching into the tree.
        ArtGroup instance = document.AllGroups().Single(InstanceResolver.IsInstance);
        Invoke(context, "object.deleteIds", new { itemIds = new[] { instance.Id } });
        Assert.Empty(document.AllGroups().Where(InstanceResolver.IsInstance));

        JsonElement deleted = Invoke(context, "definition.delete", new { name = "sym" });
        Assert.True(deleted.GetProperty("deleted").GetBoolean());
        Assert.Null(document.FindDefinition("sym"));
    }

    [Fact]
    public void PlacingADefinitionTheDocumentDoesNotHaveIsRefusedByName()
    {
        AutomationContext context = Context(out _, out _);

        JsonElement placed = Invoke(context, "definition.place", new { name = "nowhere", x = 0.0, y = 0.0 });
        Assert.False(placed.GetProperty("placed").GetBoolean());
        Assert.Contains("nowhere", placed.GetProperty("refusal").GetString()!);
    }
}
