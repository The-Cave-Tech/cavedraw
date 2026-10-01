using VCCad.Core.Model;

namespace VCCad.Pdf.Tests;

/// <summary>
/// Reading the tree the way the importer now builds it.
///
/// A `W ... n` clip is a container in the file - "everything drawn from here until the state is restored is
/// cut to this outline" - so the importer records it as a group holding what it clips, rather than as a
/// decoration on each object. These helpers let a test ask the question it actually means: **what limits this
/// object**, rather than "what is written on it".
/// </summary>
internal static class Imported
{
    /// <summary>The clips limiting an item: every ancestor's and its own, outermost first.</summary>
    public static IReadOnlyList<ClipSpec> ClipsOf(LayerItem item)
    {
        var chain = new List<LayerItem>();
        for (LayerItem? current = item; current is not null; current = current.Container as LayerItem)
        {
            chain.Add(current);
        }

        chain.Reverse();
        return chain.SelectMany(i => i.Clips).ToList();
    }

    /// <summary>The one clip limiting an item, wherever in its ancestry it sits.</summary>
    public static ClipSpec ClipOf(LayerItem item) => ClipsOf(item).Single();

    /// <summary>The item itself when it is a path, or the path inside the mask that holds it.</summary>
    public static PathItem PathInside(LayerItem item)
        => item as PathItem
           ?? Descendants(item).OfType<PathItem>().First();

    /// <summary>Everything under an item, however deeply nested.</summary>
    public static IEnumerable<LayerItem> Descendants(LayerItem item)
    {
        if (item is not ArtGroup group)
        {
            yield break;
        }

        foreach (LayerItem child in group.Children)
        {
            yield return child;
            foreach (LayerItem nested in Descendants(child))
            {
                yield return nested;
            }
        }
    }

    /// <summary>
    /// The one item of a kind on a document's first page, wherever the importer's masking put it.
    ///
    /// A test that wants 	he path on this page should not have to know how many groups the importer
    /// wrapped it in, and before masking groups existed it did not: the path was a direct child of the layer.
    /// The intent is the same and this says it without a guess about depth.
    /// </summary>
    public static T OneOn<T>(CadDocument document) where T : LayerItem
        => Everything(document.Artboards[0]).OfType<T>().Single();

    /// <summary>The one path on a document's first page.</summary>
    public static PathItem PathOn(CadDocument document) => OneOn<PathItem>(document);

    /// <summary>Every item on a document's first page, however deeply nested.</summary>
    public static IEnumerable<LayerItem> Page(CadDocument document) => Everything(document.Artboards[0]);

    /// <summary>Everything on a page, however deeply nested - the tree the importer produced.</summary>
    public static IEnumerable<LayerItem> Everything(Artboard artboard)
        => artboard.Layers.SelectMany(l => l.Children.SelectMany(c => new[] { c }.Concat(Descendants(c))));
}
