using System.Text;
using System.Text.Json;
using VCCad.App.Automation;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// Refreshing instances through the operation registry (issue #185).
///
/// <c>RefreshInstancesCommand</c> and its model-level tests (<c>SvgInstanceResolutionTests</c>) landed first, and
/// the command was reachable by nothing: a person could not refresh instances and neither could a driver. These
/// tests **drive the operation**, because a test that called the command would pass while the capability stayed
/// unreachable - which is the defect the issue names. The assertions are on the geometry the instances come out
/// with, so an operation that reported "refreshed" while writing nothing would fail.
/// </summary>
public class InstanceRefreshOperationTests
{
    private const string Head =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" " +
        "width=\"400\" height=\"400\" viewBox=\"0 0 400 400\">";

    /// <summary>One definition and two uses of it - the fixture the model-level tests drive the command with.</summary>
    private const string TwoUses =
        "<defs><rect id=\"box\" x=\"0\" y=\"0\" width=\"10\" height=\"10\"/></defs>" +
        "<use xlink:href=\"#box\" x=\"50\" y=\"30\"/>" +
        "<use xlink:href=\"#box\" x=\"150\" y=\"30\"/>";

    private static JsonElement Params(object value) => JsonSerializer.SerializeToElement(value);

    private static string Base64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    private static AutomationContext Host() => new() { ViewModel = new EditorViewModel() };

    /// <summary>Imports an SVG through the registry, which is the only way a driver gets a document here.</summary>
    private static CadDocument Import(AutomationContext context, string body)
    {
        EditorOperations.Invoke(context, "document.importSvg",
            Params(new { svgBase64 = Base64(Head + body + "</svg>") }));
        return context.Document;
    }

    private static ArtGroup[] Instances(CadDocument document, string source)
        => document.AllGroups().Where(group => group.SourceId == source).ToArray();

    /// <summary>The first path inside a definition's content, however deep - the shape a person would edit.</summary>
    private static PathItem DefinitionShape(CadDocument document, string id)
        => Deep(Assert.IsType<ArtGroup>(document.FindDefinition(id)).Children).OfType<PathItem>().First();

    private static PathItem FirstShape(ArtGroup instance)
        => Deep(instance.Children).OfType<PathItem>().First();

    private static IEnumerable<LayerItem> Deep(IEnumerable<LayerItem> items)
    {
        foreach (LayerItem item in items)
        {
            yield return item;

            if (item is ArtGroup group)
            {
                foreach (LayerItem nested in Deep(group.Children))
                {
                    yield return nested;
                }
            }
        }
    }

    /// <summary>
    /// A node's coordinate in the artboard's frame, every enclosing group's transform composed in - the
    /// composition <see cref="VCCad.Core.Selection.SelectionEngine.ToArtboard"/> states, so this asks the question
    /// the canvas asks. The instance's own placement is one of the terms, which is why it has to be the frame and
    /// not the raw node.
    /// </summary>
    private static Point2D At(LayerItem item, int node)
    {
        AffineTransform transform = AffineTransform.Identity;

        for (IItemContainer? container = item.Container;
             container is not null;
             container = (container as LayerItem)?.Container)
        {
            if (container is ArtGroup group)
            {
                transform = group.Transform.Compose(transform);
            }
        }

        return transform.Transform(((PathItem)item).SubPaths[0].Nodes[node].Anchor);
    }

    private static JsonElement Refresh(AutomationContext context)
        => JsonSerializer.SerializeToElement(EditorOperations.Invoke(context, "instance.refresh", default));

    /// <summary>
    /// **The acceptance in issue #185.** Edit the definition, run the operation, and every instance shows the
    /// edit - driven through the registry, at each instance's own placement, with the reply saying how many
    /// instances moved.
    /// </summary>
    [Fact]
    public void TheOperationCarriesAnEditToADefinitionIntoEveryInstance()
    {
        AutomationContext context = Host();
        CadDocument document = Import(context, TwoUses);

        ArtGroup[] instances = Instances(document, "box");
        Assert.Equal(2, instances.Length);
        Assert.Equal(37.5, At(FirstShape(instances[0]), 0).X, 9);
        Assert.Equal(112.5, At(FirstShape(instances[1]), 0).X, 9);

        // The edit, made where a person makes it: in the definition the instances name.
        DefinitionShape(document, "box").TranslateGeometryBy(new Vector2D(5, 0));

        // The instance holds a copy as well as the link, so that copy is stale until the registry step runs. If
        // this line ever moves on its own, the operation below is no longer what carries the edit and this test
        // has stopped testing what it claims to.
        Assert.Equal(37.5, At(FirstShape(instances[0]), 0).X, 9);

        JsonElement result = Refresh(context);

        // A caller can tell a refresh from a no-op, and no link was refused.
        Assert.Equal(2, result.GetProperty("refreshed").GetInt32());
        Assert.Empty(result.GetProperty("notFollowed").EnumerateArray());

        // **Every** instance - not only the first - and each stayed where its own `use` put it.
        Assert.Equal(41.25, At(FirstShape(instances[0]), 0).X, 9);
        Assert.Equal(116.25, At(FirstShape(instances[1]), 0).X, 9);
        Assert.Equal(50.0, instances[0].Transform.Transform(new Point2D(0, 0)).X, 9);
        Assert.Equal(150.0, instances[1].Transform.Transform(new Point2D(0, 0)).X, 9);
    }

    /// <summary>
    /// **A link that cannot be followed is refused by name, and the instance keeps drawing.** The report has to
    /// say *which* link and *why*: an instance quietly left with a stale copy is the loss the operation exists to
    /// make visible.
    /// </summary>
    [Fact]
    public void ARefusedLinkIsNamedAndTheInstanceKeepsDrawing()
    {
        AutomationContext context = Host();
        CadDocument document = Import(context, TwoUses);
        ArtGroup[] instances = Instances(document, "box");

        Assert.True(document.RemoveDefinition("box"));

        JsonElement result = Refresh(context);

        Assert.Equal(0, result.GetProperty("refreshed").GetInt32());

        string[] notFollowed = result.GetProperty("notFollowed")
            .EnumerateArray().Select(entry => entry.GetString() ?? string.Empty).ToArray();
        Assert.Contains(notFollowed, entry =>
            entry.Contains("box", StringComparison.Ordinal) &&
            entry.Contains("no definition", StringComparison.Ordinal));

        // Still drawing, in the same place: the copy is what an orphaned instance has left.
        Assert.Equal(37.5, At(FirstShape(instances[0]), 0).X, 9);
        Assert.Equal(112.5, At(FirstShape(instances[1]), 0).X, 9);
    }

    /// <summary>
    /// **A no-op is readable as a no-op.** A document with no instances refreshes nothing and refuses nothing,
    /// which is a different reply from "there were instances and every link was refused" in the test above.
    /// </summary>
    [Fact]
    public void ADocumentWithNoInstancesReportsANoOp()
    {
        AutomationContext context = Host();
        Import(context, "<rect x=\"0\" y=\"0\" width=\"10\" height=\"10\"/>");

        JsonElement result = Refresh(context);

        Assert.Equal(0, result.GetProperty("refreshed").GetInt32());
        Assert.Empty(result.GetProperty("notFollowed").EnumerateArray());
    }

    /// <summary>
    /// **The operation is on the undo stack as one step**, which is what makes it the same kind of edit a person
    /// makes rather than a mutation nothing can take back. Undo goes through the registry too.
    /// </summary>
    [Fact]
    public void RefreshingThroughTheOperationIsOneUndoStep()
    {
        AutomationContext context = Host();
        CadDocument document = Import(context, TwoUses);
        ArtGroup[] instances = Instances(document, "box");

        DefinitionShape(document, "box").TranslateGeometryBy(new Vector2D(5, 0));
        Assert.Equal(2, Refresh(context).GetProperty("refreshed").GetInt32());
        Assert.Equal(41.25, At(FirstShape(instances[0]), 0).X, 9);

        EditorOperations.Invoke(context, "document.undo", default);

        // The content the instance had before the refresh is back - it did not resolve again, which would have
        // restored the state the document was edited away from.
        Assert.Equal(37.5, At(FirstShape(instances[0]), 0).X, 9);
    }

    /// <summary>
    /// **The catalog names it**, which is how a person finds it in the Operations tab and how a driver discovers
    /// it - the search the issue was filed from. It takes no parameters, because the command behind it
    /// re-resolves every instance and a scope the command ignored would be a lie in the catalog.
    /// </summary>
    [Fact]
    public void TheCatalogNamesTheOperationWithNoScope()
    {
        AutomationContext context = Host();
        JsonElement catalog = JsonSerializer.SerializeToElement(
            EditorOperations.Invoke(context, "app.operations", default));

        JsonElement entry = Assert.Single(
            catalog.EnumerateArray(),
            operation => operation.GetProperty("op").GetString() == "instance.refresh");

        Assert.Equal(string.Empty, entry.GetProperty("parameters").GetString());
    }
}
