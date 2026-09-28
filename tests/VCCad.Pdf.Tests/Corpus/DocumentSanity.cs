using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Pdf.Tests.Corpus;

/// <summary>
/// Structural sanity checks shared by the corpus tests: a document the importer
/// produced must have at least one artboard, finite positive artboard sizes and
/// finite geometry throughout. Mirrors the checks in
/// <c>VeraPdfCorpusTests</c> so both corpus sweeps apply the same bar.
/// </summary>
internal static class DocumentSanity
{
    /// <summary>Asserts that <paramref name="document"/> is structurally usable.</summary>
    public static void AssertSane(CadDocument document, string name)
    {
        Assert.NotNull(document);
        Assert.NotEmpty(document.Artboards);
        foreach (Artboard artboard in document.Artboards)
        {
            Assert.True(double.IsFinite(artboard.Width) && double.IsFinite(artboard.Height),
                $"{name}: non-finite artboard size");
            Assert.True(artboard.Width > 0 && artboard.Height > 0, $"{name}: empty artboard");

            foreach (LayerItem item in artboard.Layers.SelectMany(layer => layer.Children))
            {
                AssertFinite(item, name);
            }
        }
    }

    private static void AssertFinite(LayerItem item, string name)
    {
        switch (item)
        {
            case PathItem path:
                foreach (SubPath subPath in path.SubPaths)
                {
                    foreach (PathNode node in subPath.Nodes)
                    {
                        Assert.True(IsFinite(node.Anchor), $"{name}: non-finite anchor");
                        Assert.True(IsFinite(node.InHandle) && IsFinite(node.OutHandle),
                            $"{name}: non-finite handle");
                    }
                }

                break;

            case TextItem text:
                Assert.True(IsFinite(text.Origin), $"{name}: non-finite text origin");
                Assert.True(double.IsFinite(text.RotationRadians), $"{name}: non-finite rotation");
                break;

            case ArtGroup group:
                foreach (LayerItem child in group.Children)
                {
                    AssertFinite(child, name);
                }

                break;
        }
    }

    private static bool IsFinite(Point2D point)
        => double.IsFinite(point.X) && double.IsFinite(point.Y);

    /// <summary>Counts every model item on every artboard (all layers, recursive).</summary>
    public static int CountItems(CadDocument document)
    {
        int count = 0;
        foreach (Artboard artboard in document.Artboards)
        {
            foreach (Layer layer in artboard.Layers)
            {
                foreach (LayerItem item in layer.Children)
                {
                    count += CountItem(item);
                }
            }
        }

        return count;
    }

    private static int CountItem(LayerItem item)
        => item switch
        {
            ArtGroup group => 1 + group.Children.Sum(CountItem),
            _ => 1,
        };

    /// <summary>Enumerates every model item (recursing into groups).</summary>
    public static IEnumerable<LayerItem> Items(CadDocument document)
        => document.Artboards.SelectMany(artboard => artboard.Layers)
            .SelectMany(layer => layer.Children)
            .SelectMany(Expand);

    private static IEnumerable<LayerItem> Expand(LayerItem item)
    {
        yield return item;
        if (item is ArtGroup group)
        {
            foreach (LayerItem child in group.Children.SelectMany(Expand))
            {
                yield return child;
            }
        }
    }
}
