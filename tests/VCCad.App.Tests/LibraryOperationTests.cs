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
    public void RenamingADefinitionCarriesEveryInstanceWithItAndUndoesBothHalves()
    {
        AutomationContext context = Context(out CadDocument document, out ArtGroup definition);
        Invoke(context, "definition.place", new { name = "sym", x = 0.0, y = 0.0 });
        Invoke(context, "definition.place", new { name = "sym", x = 40.0, y = 0.0 });

        JsonElement renamed = Invoke(context, "definition.rename", new { from = "sym", to = "symbol-a" });
        Assert.True(renamed.GetProperty("renamed").GetBoolean());
        Assert.Equal(2, renamed.GetProperty("updatedInstances").GetInt32());

        // Both halves: the definition is renamed **and** the instances name the new one, which is what keeps
        // `instance.refresh` able to follow the link.
        Assert.Null(document.FindDefinition("sym"));
        Assert.Same(definition, document.FindDefinition("symbol-a"));
        ArtGroup[] instances = document.AllGroups().Where(InstanceResolver.IsInstance).ToArray();
        Assert.Equal(2, instances.Length);
        Assert.All(instances, instance => Assert.Equal("symbol-a", instance.SourceId));

        // The library reads under the new name with the same count.
        JsonElement entry = Assert.Single(Invoke(context, "definition.list").EnumerateArray());
        Assert.Equal("symbol-a", entry.GetProperty("name").GetString());
        Assert.Equal(2, entry.GetProperty("usedBy").GetInt32());

        // **Undo puts the link back**, which is why the two halves are one command: an undo that renamed the
        // definition alone would leave both instances pointing at a name that no longer exists. The rename is the
        // last command at this point, so one undo is the rename and nothing else.
        Invoke(context, "document.undo");
        Assert.NotNull(document.FindDefinition("sym"));
        Assert.Null(document.FindDefinition("symbol-a"));
        Assert.All(
            document.AllGroups().Where(InstanceResolver.IsInstance),
            instance => Assert.Equal("sym", instance.SourceId));

        // And redo puts both halves back, after which the refresh follows the link under the new name.
        Invoke(context, "document.redo");
        Assert.NotNull(document.FindDefinition("symbol-a"));
        Assert.All(
            document.AllGroups().Where(InstanceResolver.IsInstance),
            instance => Assert.Equal("symbol-a", instance.SourceId));

        JsonElement refreshed = Invoke(context, "instance.refresh");
        Assert.Equal(2, refreshed.GetProperty("refreshed").GetInt32());
        Assert.Empty(refreshed.GetProperty("notFollowed").EnumerateArray());
    }

    [Fact]
    public void ARenameIsRefusedWhenTheNameIsTakenMissingOrUnchanged()
    {
        AutomationContext context = Context(out CadDocument document, out _);
        document.AddDefinition("other");

        JsonElement taken = Invoke(context, "definition.rename", new { from = "sym", to = "other" });
        Assert.False(taken.GetProperty("renamed").GetBoolean());
        Assert.Contains("already has a definition named", taken.GetProperty("refusal").GetString()!);

        JsonElement missing = Invoke(context, "definition.rename", new { from = "nowhere", to = "x" });
        Assert.False(missing.GetProperty("renamed").GetBoolean());
        Assert.Contains("nowhere", missing.GetProperty("refusal").GetString()!);

        JsonElement unchanged = Invoke(context, "definition.rename", new { from = "sym", to = "sym" });
        Assert.False(unchanged.GetProperty("renamed").GetBoolean());
        Assert.Contains("already named", unchanged.GetProperty("refusal").GetString()!);

        // Nothing was renamed by any of the refused calls.
        Assert.NotNull(document.FindDefinition("sym"));
        Assert.NotNull(document.FindDefinition("other"));
    }

    [Fact]
    public void CreatingADefinitionFromTheSelectionLeavesAnInstanceWhereTheArtworkWas()
    {
        var viewModel = new EditorViewModel();
        CadDocument document = viewModel.Document;
        PathItem artwork = Artwork();
        document.Artboards[0].Layers[0].AddItem(artwork);

        var context = new AutomationContext { ViewModel = viewModel };
        context.Session.SelectObject(artwork);
        Rect2D before = artwork.BoundingBox();

        JsonElement created = Invoke(context, "definition.create", new { name = "sym" });
        Assert.True(created.GetProperty("created").GetBoolean());
        Assert.Equal("sym", created.GetProperty("name").GetString());
        Assert.Equal(1, created.GetProperty("itemCount").GetInt32());

        // The artwork is in the library, out of the layer, and an instance stands where it was - at the same place,
        // which is the geometry half of "as create symbol from selection does".
        ArtGroup definition = document.FindDefinition("sym")!;
        Assert.NotNull(definition);
        Assert.Single(definition.Children);
        Assert.DoesNotContain(artwork, document.Artboards[0].Layers[0].Children);

        ArtGroup instance = Assert.Single(document.AllGroups().Where(InstanceResolver.IsInstance));
        Assert.Equal("sym", instance.SourceId);
        Rect2D after = instance.BoundingBox();
        Assert.Equal(before.X, after.X, 3);
        Assert.Equal(before.Y, after.Y, 3);
        Assert.Equal(before.Width, after.Width, 3);
        Assert.Equal(before.Height, after.Height, 3);

        // Editing the definition reaches the placement - the reason an instance is a link and not a copy.
        // **The undo is asserted first**, because it must undo the *create*: a command run after it (the refresh
        // below) would be the one undone, and the assertion would be about the wrong step - which it was, until
        // this test said so.
        Invoke(context, "document.undo");
        Assert.Contains(artwork, document.Artboards[0].Layers[0].Children);
        Assert.Null(document.FindDefinition("sym"));
        Assert.Empty(document.AllGroups().Where(InstanceResolver.IsInstance));

        // Redo puts the placement back, and then the edit to the definition follows through it.
        Invoke(context, "document.redo");
        Assert.Single(document.AllGroups().Where(InstanceResolver.IsInstance));
        definition.AddItem(Artwork());
        Assert.Equal(1, Invoke(context, "instance.refresh").GetProperty("refreshed").GetInt32());
        Assert.Equal(2, Assert.Single(document.AllGroups().Where(InstanceResolver.IsInstance)).Children.Count);
    }

    [Fact]
    public void CreatingADefinitionRefusesWhatItCannotDoHonestly()
    {
        AutomationContext context = Context(out _, out _);

        JsonElement empty = Invoke(context, "definition.create", new { });
        Assert.False(empty.GetProperty("created").GetBoolean());
        Assert.Contains("nothing is selected", empty.GetProperty("refusal").GetString()!);
    }

    [Fact]
    public void TheAutomaticDefinitionNameIsDeterministicAndSkipsWhatIsTaken()
    {
        var viewModel = new EditorViewModel();
        CadDocument document = viewModel.Document;
        PathItem first = Artwork();
        PathItem second = Artwork();
        document.Artboards[0].Layers[0].AddItem(first);
        document.Artboards[0].Layers[0].AddItem(second);

        var context = new AutomationContext { ViewModel = viewModel };
        context.Session.SelectObject(first);
        Assert.Equal("symbol", Invoke(context, "definition.create", new { }).GetProperty("name").GetString());

        // The second one is numbered rather than given the same name, which is what makes the library addressable.
        context.Session.SelectObject(second);
        Assert.Equal("symbol2", Invoke(context, "definition.create", new { }).GetProperty("name").GetString());

        // And asking for a name the library holds is refused rather than renamed or overwritten.
        PathItem third = Artwork();
        document.Artboards[0].Layers[0].AddItem(third);
        context.Session.SelectObject(third);
        JsonElement taken = Invoke(context, "definition.create", new { name = "symbol" });
        Assert.False(taken.GetProperty("created").GetBoolean());
        Assert.Contains("already has a definition named", taken.GetProperty("refusal").GetString()!);
    }

    private static PathItem Artwork()
    {
        var path = new PathItem { Name = "art", Fill = FillSpec.Solid(ColorRgb.Black) };
        SubPath sub = path.AddSubPath(closed: true);
        sub.Nodes.Add(new PathNode(new Point2D(10, 10)));
        sub.Nodes.Add(new PathNode(new Point2D(30, 10)));
        sub.Nodes.Add(new PathNode(new Point2D(30, 25)));
        sub.Nodes.Add(new PathNode(new Point2D(10, 25)));
        return path;
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
