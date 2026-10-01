using VCCad.Core.Model;
using VCCad.Core.Samples;
using Xunit;

namespace VCCad.Pdf.Tests;

/// <summary>
/// The imported tree has to be the file's tree.
///
/// Two rules, both learned from the LILLIE sample:
///
/// **We do not invent names.** A layer called "Imported" existed in our tree and nowhere in the file - the
/// file's own names are `Layer 1`, `Background`, `Headers/Footers`, and the string "Imported" does not
/// appear in it at all. Content the file does not tag belongs to the page, and gets an unnamed layer rather
/// than a name we made up.
///
/// **We do not throw the grouping away.** A form XObject is the file's own grouping. It used to be inlined
/// whenever its contents spanned optional-content layers, because a group can only sit in one layer - and
/// that flattened every page whose content is a single form, which is most of them. An optional-content
/// group is a *tag on content*, not a container, so inside a form it is represented as a group named from
/// the file. That is how a partner application shows it, and it is what the file says.
/// </summary>
public class ImportedStructureTests
{
    private static string? SamplePath(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string? candidate = SampleLibrary.Find(fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }

    private static CadDocument? Import(string fileName)
    {
        string? path = SamplePath(fileName);
        return path is null ? null : PdfImporter.Import(File.ReadAllBytes(path));
    }

    /// <summary>No layer carries a name the file does not have.</summary>
    [Fact]
    public void NoLayerIsNamedSomethingTheFileDoesNotContain()
    {
        CadDocument? document = Import("3464_LILLIE_View_A_Sides_color.pdf");
        if (document is null)
        {
            return; // sample not present in this checkout: skip cleanly
        }

        foreach (Artboard page in document.Artboards)
        {
            foreach (Layer layer in page.Layers)
            {
                Assert.NotEqual("Imported", layer.Name);
            }
        }
    }

    /// <summary>
    /// The page's grouping survives: the top form is a group, and an optional-content group inside it is a
    /// group named from the file rather than a layer beside everything else.
    /// </summary>
    [Fact]
    public void TheFormsGroupingSurvivesTheImport()
    {
        CadDocument? document = Import("3464_LILLIE_View_A_Sides_color.pdf");
        if (document is null)
        {
            return;
        }

        Artboard page = document.Artboards[0];
        List<ArtGroup> groups = Walk(page).OfType<ArtGroup>().ToList();

        Assert.NotEmpty(groups);

        // The file names an optional-content group "Layer 1"; inside a form it is a group by that name.
        Assert.Contains(groups, g => g.Name == "Layer 1");

        // And something is actually inside it, rather than it being an empty shell.
        ArtGroup layerOne = groups.First(g => g.Name == "Layer 1");
        Assert.NotEmpty(layerOne.Children);
    }

    /// <summary>The pattern group and its contents are nested, not loose on the page.</summary>
    [Fact]
    public void PageOneHasNestingRatherThanOneFlatList()
    {
        CadDocument? document = Import("3464_LILLIE_View_A_Sides_color.pdf");
        if (document is null)
        {
            return;
        }

        Artboard page = document.Artboards[0];

        int direct = page.Layers.Sum(l => l.Children.Count);
        int nested = Walk(page).Count();

        Assert.True(nested > direct,
            $"nothing is nested: {direct} direct children and {nested} total, so the tree is flat");
    }

    private static IEnumerable<LayerItem> Walk(Artboard page)
    {
        foreach (Layer layer in page.Layers)
        {
            foreach (LayerItem item in Walk(layer.Children))
            {
                yield return item;
            }
        }
    }

    private static IEnumerable<LayerItem> Walk(IEnumerable<LayerItem> items)
    {
        foreach (LayerItem item in items)
        {
            yield return item;
            if (item is ArtGroup group)
            {
                foreach (LayerItem nested in Walk(group.Children))
                {
                    yield return nested;
                }
            }
        }
    }
}