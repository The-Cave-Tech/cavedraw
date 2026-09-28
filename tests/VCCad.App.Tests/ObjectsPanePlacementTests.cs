using Avalonia.Headless.XUnit;
using VCCad.App.Views.Panes;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// The Objects panel shows a document's contents, on every page.
///
/// A tiled pattern is several sheets laid out in a grid, and an item's geometry is stored
/// relative to its own sheet: a piece near the top-left of page 5 has coordinates near the
/// origin, while page 5 itself sits at x=2608 because of the four pages to its left. The
/// panel compared those two directly, which asks "is this item near the top-left of the
/// whole document" — true only of page 1.
///
/// So a real twelve-page pattern showed one page's contents and a collapsed "Pasteboard"
/// holding the other 2,900 objects, which is what was reported as "just pages and two
/// items". These tests pin the rule, and the shape of document that exposes it.
/// </summary>
public class ObjectsPanePlacementTests
{
    /// <summary>A path whose geometry is a rectangle at an artboard-local position.</summary>
    private static PathItem Box(string name, double x, double y, double size = 40)
    {
        var path = new PathItem { Name = name };
        SubPath sub = path.AddSubPath(closed: true);
        sub.Nodes.Add(new PathNode(new Point2D(x, y)));
        sub.Nodes.Add(new PathNode(new Point2D(x + size, y)));
        sub.Nodes.Add(new PathNode(new Point2D(x + size, y + size)));
        sub.Nodes.Add(new PathNode(new Point2D(x, y + size)));
        return path;
    }

    private static Rect2D Box2D(double x, double y, double size = 40)
        => new(x, y, size, size);

    /// <summary>
    /// A sheet of <paramref name="columns"/> x <paramref name="rows"/> artboards laid out
    /// the way the document lays a tiled pattern out, each with one layer holding paths.
    /// </summary>
    private static CadDocument Tiled(int columns, int rows, int itemsPerPage)
    {
        const double w = 612;
        const double h = 792;
        var document = new CadDocument();

        for (int row = 0; row < rows; row++)
        {
            for (int column = 0; column < columns; column++)
            {
                var artboard = new Artboard(
                    new Size2D(w, h), new Point2D(column * (w + 40), row * (h + 40)))
                {
                    Name = $"Page {row * columns + column + 1}",
                };

                Layer layer = artboard.AddLayer("Artwork");
                for (int i = 0; i < itemsPerPage; i++)
                {
                    // Local coordinates, near the origin, exactly as an imported page stores
                    // them: this is what made the comparison wrong.
                    layer.AddItem(Box($"Path {i}", 60 + (i % 8) * 50, 60 + (i / 8) * 50));
                }

                document.AddArtboard(artboard);
            }
        }

        return document;
    }

    [AvaloniaFact]
    public void EveryPageKeepsItsOwnItemsInAGridOfPages()
    {
        CadDocument document = Tiled(columns: 6, rows: 2, itemsPerPage: 10);
        (Dictionary<Layer, List<LayerItem>> byLayer, List<LayerItem> pasteboard) =
            ObjectsPane.Classify(document);

        Assert.Empty(pasteboard);

        foreach (Artboard artboard in document.Artboards)
        {
            foreach (Layer layer in artboard.Layers)
            {
                Assert.True(byLayer.TryGetValue(layer, out List<LayerItem>? mine),
                    $"no classification for {artboard.Name}");
                Assert.Equal(layer.Children.Count, mine!.Count);
            }
        }
    }

    [AvaloniaFact]
    public void APageFarFromTheOriginStillHoldsItsItems()
    {
        CadDocument document = Tiled(columns: 6, rows: 1, itemsPerPage: 4);
        Artboard far = document.Artboards[5];

        // The bug needed a page that is not the one at the origin: this one is at x=3260.
        Assert.True(far.X > 3000, $"the fixture should push a page far out, but x={far.X}");

        foreach (LayerItem child in far.Layers[0].Children)
        {
            Assert.True(
                ObjectsPane.IntersectsArtboard(Box2D(60, 60, 40), child.ArtboardOffset(), far),
                $"{far.Name} should hold an item at its own local 60,60");
        }
    }

    [AvaloniaFact]
    public void EveryArtboardInTheDocumentIsRepresented()
    {
        CadDocument document = Tiled(columns: 6, rows: 2, itemsPerPage: 3);
        (Dictionary<Layer, List<LayerItem>> byLayer, _) = ObjectsPane.Classify(document);

        int layers = document.Artboards.Sum(a => a.Layers.Count);
        Assert.Equal(12, document.Artboards.Count);
        Assert.Equal(layers, byLayer.Count);
    }

    [AvaloniaFact]
    public void AnItemDrawnOutsideItsPageGoesToThePasteboard()
    {
        // The overflow is real and is not misplaced: a piece drawn past its sheet's edge
        // belongs to no page. It must not be quietly claimed by one either.
        var document = new CadDocument();
        var artboard = new Artboard(new Size2D(612, 792), new Point2D(0, 0)) { Name = "Page 1" };
        Layer layer = artboard.AddLayer("Artwork");
        layer.AddItem(Box("Inside", 100, 100, 50));
        layer.AddItem(Box("Well past the edge", 5000, 5000, 50));
        document.AddArtboard(artboard);

        (Dictionary<Layer, List<LayerItem>> byLayer, List<LayerItem> pasteboard) =
            ObjectsPane.Classify(document);

        Assert.Single(pasteboard);
        Assert.Equal("Well past the edge", pasteboard[0].Name);
        Assert.Single(byLayer[layer]);
        Assert.Equal("Inside", byLayer[layer][0].Name);
    }

    [AvaloniaFact]
    public void ASecondPageIsNotFiledUnderTheFirst()
    {
        CadDocument document = Tiled(columns: 2, rows: 1, itemsPerPage: 3);

        // Page 2's items carry the offset of page 2, not page 1's. If the offset were ignored
        // they would land on whichever artboard sits at the origin.
        Artboard one = document.Artboards[0];
        Artboard two = document.Artboards[1];

        LayerItem item = two.Layers[0].Children[0];
        Assert.Equal(new Vector2D(two.X, two.Y), item.ArtboardOffset());

        Rect2D bounds = Box2D(60, 60);
        Assert.True(ObjectsPane.IntersectsArtboard(bounds, item.ArtboardOffset(), two));
        Assert.False(ObjectsPane.IntersectsArtboard(bounds, item.ArtboardOffset(), one));
    }

    [AvaloniaFact]
    public void APageThatIsNotAtTheOriginStillWorks()
    {
        // Nothing about the rule should depend on where the origin is: a single page at the
        // origin and the same page moved both work.
        var document = new CadDocument();
        var artboard = new Artboard(new Size2D(612, 792), new Point2D(2000, 900))
        {
            Name = "Page 1",
        };
        Layer layer = artboard.AddLayer("Artwork");
        layer.AddItem(Box("P", 10, 10, 20));
        document.AddArtboard(artboard);

        (Dictionary<Layer, List<LayerItem>> byLayer, List<LayerItem> pasteboard) =
            ObjectsPane.Classify(document);

        Assert.Empty(pasteboard);
        Assert.Single(byLayer[layer]);
    }
}
