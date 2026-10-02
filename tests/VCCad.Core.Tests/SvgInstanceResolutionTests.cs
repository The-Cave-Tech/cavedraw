using VCCad.Core.Commands;
using VCCad.Core.Model;
using VCCad.Core.Svg;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// The honouring step for `use`: an edit to a definition reaching every instance.
///
/// #117's import half landed already - the reader keeps the instance and records the id it came from, and the
/// writer round-trips that link. What neither half did was **follow** the link: the definition was skipped with
/// `defs`, so the document held copies and no definition at all, and an edit had nothing to reach.
///
/// A first probe asked "is there an item named `box` in the document?" and **passed for the wrong reason** -
/// `ReadUse` names the instance after the definition id when the `use` has no id of its own, so the instance
/// itself answers to the name. The question that has to be asked is whether there is a *definition*, distinct
/// from the instance, that an edit can be made to.
/// </summary>
public class SvgInstanceResolutionTests
{
    private const string Head =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" " +
        "width=\"400\" height=\"400\" viewBox=\"0 0 400 400\">";

    private static SvgImportResult Read(string body) => SvgReader.Read(Head + body + "</svg>");

    private static ArtGroup[] Instances(CadDocument document, string source)
        => document.AllGroups().Where(group => group.SourceId == source).ToArray();

    /// <summary>The first path inside a definition's content, however deep - the shape a person would edit.</summary>
    private static PathItem DefinitionShape(CadDocument document, string id)
    {
        ArtGroup entry = Assert.IsType<ArtGroup>(document.FindDefinition(id));
        return Deep(entry.Children).OfType<PathItem>().First();
    }

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
    /// composition <see cref="VCCad.Core.Selection.SelectionEngine.ToArtboard"/> states, so a test that asks
    /// "where is the picture" is asking the same question the canvas asks.
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

    private static PathItem FirstShape(ArtGroup instance)
        => Deep(instance.Children).OfType<PathItem>().First();

    private const string TwoUses =
        "<defs><rect id=\"box\" x=\"0\" y=\"0\" width=\"10\" height=\"10\"/></defs>" +
        "<use xlink:href=\"#box\" x=\"50\" y=\"30\"/>" +
        "<use xlink:href=\"#box\" x=\"150\" y=\"30\"/>";

    // ---------------------------------------------------------------- the gap

    /// <summary>
    /// **The gap, stated as the requirement.** Edit the definition, and every instance shows the edit.
    ///
    /// Before the change this test did not compile, and the compiler said why: `error CS1061: 'CadDocument' does
    /// not contain a definition for 'FindDefinition'` and `error CS0246: The type or namespace name
    /// 'RefreshInstancesCommand' could not be found`. There was no definition to edit and no step that followed
    /// the link - which is exactly the half `94bd8b7` recorded as missing.
    /// </summary>
    [Fact]
    public void EditingADefinitionChangesEveryInstance()
    {
        SvgImportResult result = Read(TwoUses);
        CadDocument document = result.Document;

        // The definition is a document asset, like a width profile or a filter - not a copy buried in an
        // instance, which is what "a reference, not a copy" means and what an edit has to be able to change.
        PathItem shape = DefinitionShape(document, "box");
        ArtGroup[] instances = Instances(document, "box");
        Assert.Equal(2, instances.Length);

        // It has content of its own, separate from the instances' copies of it.
        Assert.Equal(0.0, shape.SubPaths[0].Nodes[0].Anchor.X, 9);
        Assert.Equal(37.5, At(FirstShape(instances[0]), 0).X, 9);

        // The edit.
        shape.TranslateGeometryBy(new Vector2D(5, 0));

        // The step that honours the link.
        new RefreshInstancesCommand(document).Do();

        // **Every** instance, not just the one the edit was made through - each at its own placement, which the
        // re-resolution left alone.
        Assert.Empty(document.MissingDefinitions());
        Assert.Equal(41.25, At(FirstShape(instances[0]), 0).X, 9);
        Assert.Equal(116.25, At(FirstShape(instances[1]), 0).X, 9);

        // The instances' own placement is untouched - only their content was rebuilt.
        Assert.Equal(50.0, instances[0].Transform.Transform(new Point2D(0, 0)).X, 9);
        Assert.Equal(150.0, instances[1].Transform.Transform(new Point2D(0, 0)).X, 9);
    }

    /// <summary>
    /// **A transformed group around the instance is not applied twice.**
    ///
    /// The frame is the one <see cref="VCCad.Core.Selection.SelectionEngine.ToArtboard"/> composes: the enclosing
    /// group's transform, then the instance's placement, then the definition's own content. Re-resolution replaces
    /// the instance's **children** only, so the group's transform stays above the instance and enters the
    /// composition exactly once.
    /// </summary>
    [Fact]
    public void AnEditReachesAnInstanceInsideATransformedGroupWithoutApplyingItTwice()
    {
        SvgImportResult result = Read(
            "<defs><rect id=\"box\" width=\"10\" height=\"10\"/></defs>" +
            "<g transform=\"translate(20,0)\"><use href=\"#box\" x=\"50\" y=\"30\"/></g>");

        CadDocument document = result.Document;
        ArtGroup instance = Assert.Single(Instances(document, "box"));

        DefinitionShape(document, "box").TranslateGeometryBy(new Vector2D(5, 0));
        new RefreshInstancesCommand(document).Do();

        // 20 (the group) + 50 (the use) + 5 (the edit) user units, at three quarters of a point each.
        Assert.Equal(56.25, At(FirstShape(instance), 0).X, 9);
        Assert.Equal(22.5, At(FirstShape(instance), 0).Y, 9);
    }

    /// <summary>
    /// **A chain is followed the whole way down.** One definition uses another, so the content an instance holds
    /// contains an instance of its own; an edit to the innermost definition has to reach through both.
    /// </summary>
    [Fact]
    public void AnEditReachesThroughAChainOfDefinitions()
    {
        SvgImportResult result = Read(
            "<defs><rect id=\"box\" width=\"4\" height=\"4\"/>" +
            "<g id=\"first\"><use href=\"#box\" x=\"7\"/></g></defs>" +
            "<use href=\"#first\" x=\"100\"/>");

        CadDocument document = result.Document;
        ArtGroup outer = Assert.Single(Instances(document, "first"));
        Assert.Single(Instances(document, "box"));

        DefinitionShape(document, "box").TranslateGeometryBy(new Vector2D(3, 0));
        new RefreshInstancesCommand(document).Do();

        // The inner instance was rebuilt from the edited definition, and it sits inside the outer one.
        Assert.Single(Instances(document, "box"));
        Assert.Equal(82.5, At(FirstShape(outer), 0).X, 9);
    }

    /// <summary>
    /// **A cycle an edit introduces is refused by name, not followed.** A definition can gain an instance of
    /// anything - that is how a chain is made - so the edit path has to refuse the one that loops, or the reader's
    /// own circular-`use` rule would be a rule nothing downstream honoured.
    /// </summary>
    [Fact]
    public void ACycleAnEditIntroducesIsRefusedByName()
    {
        SvgImportResult result = Read(
            "<defs><g id=\"a\"><rect width=\"4\" height=\"4\"/></g></defs><use href=\"#a\"/>");

        CadDocument document = result.Document;
        ArtGroup entry = Assert.IsType<ArtGroup>(document.FindDefinition("a"));

        // The edit: definition `a` comes to contain an instance of `a`.
        entry.AddItem(new ArtGroup { SourceId = "a" });

        var command = new RefreshInstancesCommand(document);
        command.Do();

        Assert.NotNull(command.LastResolution);
        Assert.Contains(
            command.LastResolution!.NotFollowed,
            problem => problem.Contains("circular", StringComparison.OrdinalIgnoreCase) &&
                       problem.Contains('a'));
    }

    /// <summary>
    /// **A definition the document does not have leaves the instance drawing, and says so.**
    ///
    /// Deleting a definition must not blank the instances: an instance holds a copy as well as the link, and the
    /// copy is what it has to keep drawing. The dangling name is the fact that has to be reported - otherwise a
    /// lost asset looks like a design decision, which is the same rule a missing filter and a missing brush follow.
    /// </summary>
    [Fact]
    public void ADeletedDefinitionLeavesTheCopyAndIsReported()
    {
        SvgImportResult result = Read(TwoUses);
        CadDocument document = result.Document;
        ArtGroup[] instances = Instances(document, "box");

        Assert.True(document.RemoveDefinition("box"));

        var command = new RefreshInstancesCommand(document);
        command.Do();

        // Still drawing, in the same place: the copy is what an orphaned instance has left.
        Assert.Equal(37.5, At(FirstShape(instances[0]), 0).X, 9);
        Assert.Equal(112.5, At(FirstShape(instances[1]), 0).X, 9);

        Assert.Equal(0, command.LastResolution!.Refreshed);
        Assert.Contains(
            command.LastResolution.NotFollowed,
            problem => problem.Contains("box") && problem.Contains("no definition"));

        // And the document can be asked the same question without running a resolution.
        Assert.Equal(2, document.MissingDefinitions().Count(entry => entry.Id == "box"));
    }

    /// <summary>Refreshing is one undoable step, and undo restores the content the instances had.</summary>
    [Fact]
    public void RefreshingInstancesIsUndoable()
    {
        SvgImportResult result = Read(TwoUses);
        CadDocument document = result.Document;
        ArtGroup[] instances = Instances(document, "box");

        DefinitionShape(document, "box").TranslateGeometryBy(new Vector2D(5, 0));

        var command = new RefreshInstancesCommand(document);
        command.Do();
        Assert.Equal(41.25, At(FirstShape(instances[0]), 0).X, 9);

        command.Undo();
        Assert.Equal(37.5, At(FirstShape(instances[0]), 0).X, 9);

        // The contract says Do must be repeatable after Undo.
        command.Do();
        Assert.Equal(41.25, At(FirstShape(instances[0]), 0).X, 9);
    }

    /// <summary>
    /// **The reference survives a save after an edit.** The instance is written as the copy it holds plus the id
    /// it is a copy of, and the definition is written into `defs` beside it, so a file that was saved and opened
    /// again still has somewhere for an edit to go rather than a flattened copy.
    /// </summary>
    [Fact]
    public void TheLinkAndTheDefinitionSurviveTheRoundTripAfterAnEdit()
    {
        SvgImportResult imported = Read(TwoUses);
        DefinitionShape(imported.Document, "box").TranslateGeometryBy(new Vector2D(5, 0));
        new RefreshInstancesCommand(imported.Document).Do();

        string svg = SvgWriter.Write(imported.Document);

        // The instance still says what it is an instance of...
        Assert.Contains("data-source=\"box\"", svg, StringComparison.Ordinal);

        // ...and the definition it names is in the file, with the edit in it.
        Assert.Contains("id=\"box\"", svg, StringComparison.Ordinal);
        Assert.Contains("<defs", svg, StringComparison.Ordinal);

        SvgImportResult again = SvgReader.Read(svg);
        Assert.Empty(again.Missing);

        ArtGroup[] instances = Instances(again.Document, "box");
        Assert.Equal(2, instances.Length);
        Assert.Empty(again.Document.MissingDefinitions());

        // The definition came back with the edited geometry, so refreshing the reopened document is a no-op
        // rather than a revert.
        PathItem shape = DefinitionShape(again.Document, "box");
        Assert.Equal(5.0, shape.SubPaths[0].Nodes[0].Anchor.X, 9);

        new RefreshInstancesCommand(again.Document).Do();
        Assert.Equal(41.25, At(FirstShape(instances[0]), 0).X, 9);
        Assert.Equal(116.25, At(FirstShape(instances[1]), 0).X, 9);
    }

    /// <summary>
    /// **An instance keeps the paint its own `use` stated**, which is what the definition's content alone does not
    /// say. SVG's `use` inherits its computed style into what it draws, so a `use` that says `fill="red"` draws a
    /// red instance of a black definition - and the importer still honours that, because the re-resolution half
    /// did not touch the read.
    /// </summary>
    [Fact]
    public void AnInstanceKeepsThePaintItsOwnUseStated()
    {
        SvgImportResult result = Read(
            "<defs><rect id=\"box\" width=\"10\" height=\"10\"/></defs>" +
            "<use href=\"#box\" fill=\"red\"/>" +
            "<use href=\"#box\" x=\"50\"/>");

        ArtGroup[] instances = Instances(result.Document, "box");
        Assert.Equal(2, instances.Length);

        Assert.Equal(1.0, FirstShape(instances[0]).Fill.Color.R, 6);
        Assert.Equal(0.0, FirstShape(instances[0]).Fill.Color.G, 6);

        // The definition itself keeps the paint the file gave *it*, not the paint one of its uses overrode.
        PathItem definition = DefinitionShape(result.Document, "box");
        Assert.NotEqual(FirstShape(instances[0]).Fill.Color, definition.Fill.Color);
    }
}
