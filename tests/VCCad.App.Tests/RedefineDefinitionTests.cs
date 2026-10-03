using System.Text.Json;
using VCCad.App.Automation;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// **A definition's geometry is edited by redefining it** (issues #135 and #202).
///
/// A definition lives in the document's library, which is not on an artboard, so there is nothing to select and draw
/// into. The route that needs no new mode is the one Illustrator uses - edit a copy where you can see it, then take
/// the result into the definition - and this is that second half, asserted on the **model**: the definition holds the
/// new content, and every instance follows it on the refresh that already exists.
/// </summary>
public class RedefineDefinitionTests
{
    private static JsonElement Invoke(AutomationContext context, string op, object? parameters = null)
        => JsonSerializer.SerializeToElement(EditorOperations.Invoke(
            context, op, parameters is null ? default : JsonSerializer.SerializeToElement(parameters)));

    private static PathItem Box(double size, string name)
    {
        var path = new PathItem { Name = name, Fill = FillSpec.Solid(ColorRgb.Black) };
        SubPath sub = path.AddSubPath(closed: true);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(size, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(size, size)));
        sub.Nodes.Add(new PathNode(new Point2D(0, size)));
        return path;
    }

    [Fact]
    public void RedefiningReplacesTheContentAndEveryInstanceFollows()
    {
        var viewModel = new EditorViewModel();
        CadDocument document = viewModel.Document;

        // A definition with one small box, placed twice.
        ArtGroup definition = document.AddDefinition("sym");
        definition.AddItem(Box(10, "old"));
        var context = new AutomationContext { ViewModel = viewModel };
        Invoke(context, "definition.place", new { name = "sym", x = 10.0, y = 10.0 });
        Invoke(context, "definition.place", new { name = "sym", x = 60.0, y = 10.0 });
        Assert.Equal(2, document.AllGroups().Count(InstanceResolver.IsInstance));

        // A bigger box drawn on the canvas replaces the definition's content.
        PathItem replacement = Box(40, "new");
        document.Artboards[0].Layers[0].AddItem(replacement);
        viewModel.SelectRange(new[] { replacement }, additive: false);

        JsonElement result = Invoke(context, "definition.redefine", new { name = "sym" });
        Assert.True(result.GetProperty("redefined").GetBoolean());
        Assert.Equal(1, result.GetProperty("childCount").GetInt32());

        // The definition holds a **copy** of the new art, not the art itself: editing the canvas copy must not
        // change what an instance draws.
        Assert.Equal("new", definition.Children.OfType<PathItem>().Single().Name);
        Assert.NotSame(replacement, definition.Children[0]);
        Assert.Contains(replacement, document.Artboards[0].Layers[0].Children);

        // Every instance follows on the refresh that already exists - and keeps its own placement.
        JsonElement refreshed = Invoke(context, "instance.refresh");
        Assert.Equal(2, refreshed.GetProperty("refreshed").GetInt32());
        foreach (ArtGroup instance in document.AllGroups().Where(InstanceResolver.IsInstance))
        {
            Assert.Equal("new", instance.Children.OfType<PathItem>().Single().Name);
        }

    }

    /// <summary>
    /// **Undo restores the definition's previous content** - in its own test, because `document.undo` undoes the
    /// *last* change and the instance refresh above is itself one: asserting it there would have been asserting that
    /// undoing a refresh restores a definition, which is not the same claim.
    /// </summary>
    [Fact]
    public void UndoPutsThePreviousContentBack()
    {
        var viewModel = new EditorViewModel();
        CadDocument document = viewModel.Document;
        ArtGroup definition = document.AddDefinition("sym");
        definition.AddItem(Box(10, "old"));
        var context = new AutomationContext { ViewModel = viewModel };

        PathItem replacement = Box(40, "new");
        document.Artboards[0].Layers[0].AddItem(replacement);
        viewModel.SelectRange(new[] { replacement }, additive: false);
        Invoke(context, "definition.redefine", new { name = "sym" });
        Assert.Equal("new", definition.Children.OfType<PathItem>().Single().Name);

        Invoke(context, "document.undo");
        Assert.Equal("old", definition.Children.OfType<PathItem>().Single().Name);
    }

    [Fact]
    public void RedefiningRefusesWhatItCannotDoHonestly()
    {
        var viewModel = new EditorViewModel();
        CadDocument document = viewModel.Document;
        ArtGroup definition = document.AddDefinition("sym");
        definition.AddItem(Box(10, "old"));
        var context = new AutomationContext { ViewModel = viewModel };

        JsonElement missing = Invoke(context, "definition.redefine", new { name = "nowhere" });
        Assert.False(missing.GetProperty("redefined").GetBoolean());
        Assert.Contains("nowhere", missing.GetProperty("refusal").GetString()!);

        JsonElement empty = Invoke(context, "definition.redefine", new { name = "sym" });
        Assert.False(empty.GetProperty("redefined").GetBoolean());
        Assert.Contains("nothing is selected", empty.GetProperty("refusal").GetString()!);


        // And none of the refused calls changed anything.
        Assert.Equal("old", definition.Children.OfType<PathItem>().Single().Name);
    }
}
