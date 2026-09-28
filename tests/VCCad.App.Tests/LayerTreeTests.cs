using VCCad.App.Views.Panes;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// The rows the Layers panel shows, and the depth it draws its rules from.
///
/// This is the traversal the panel and the object.explorer operation share. If they could
/// disagree, an operation that reports what the panel "would" show would be worth nothing,
/// and the panel is the thing that cannot be read by a driver without pixels.
/// </summary>
public class LayerTreeTests
{
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

    private static CadDocument OnePage()
    {
        var document = new CadDocument();
        var artboard = new Artboard(new Size2D(612, 792), new Point2D(0, 0)) { Name = "Page 1" };
        Layer layer = artboard.AddLayer("Artwork");
        layer.AddItem(Box("ignored", 100, 100));
        document.AddArtboard(artboard);
        return document;
    }

    [Fact]
    public void APageThenItsLayerThenItsObjects()
    {
        IReadOnlyList<LayerRow> rows = LayerTree.Rows(OnePage());

        Assert.Equal(3, rows.Count);
        Assert.Equal(LayerRowKind.Artboard, rows[0].Kind);
        Assert.Equal(0, rows[0].Depth);
        Assert.Equal(LayerRowKind.Layer, rows[1].Kind);
        Assert.Equal(1, rows[1].Depth);
        Assert.Equal(LayerRowKind.Path, rows[2].Kind);
        Assert.Equal(2, rows[2].Depth);
    }

    [Fact]
    public void ARowsChildrenComeAfterItAndAreDeeper()
    {
        CadDocument document = OnePage();
        Layer layer = document.Artboards[0].Layers[0];
        var group = new ArtGroup { Name = "group" };
        layer.AddItem(group);
        group.AddItem(Box("inner", 10, 10));

        IReadOnlyList<LayerRow> rows = LayerTree.Rows(document);
        int groupAt = rows.ToList().FindIndex(r => r.Kind == LayerRowKind.Group);

        Assert.True(groupAt >= 0);
        Assert.Equal(2, rows[groupAt].Depth);
        Assert.Equal(1, rows[groupAt].ChildCount);
        Assert.Equal(3, rows[groupAt + 1].Depth);
    }

    [Fact]
    public void TheLabelsAreTheOnesThePanelPrints()
    {
        IReadOnlyList<LayerRow> rows = LayerTree.Rows(OnePage());

        // Named by geometry, not by the PathItem's own Name - which the fixture sets to
        // "ignored" precisely so a test that echoed it would fail.
        Assert.Equal("Rectangle", rows[2].Label);
        Assert.False(rows[2].IsUserNamed);
    }

    [Fact]
    public void ANameAPersonGaveIsMarkedAsTheirs()
    {
        CadDocument document = OnePage();
        LayerItem item = document.Artboards[0].Layers[0].Children[0];
        item.Name = "Left sleeve";
        item.NameIsUserSet = true;

        LayerRow row = LayerTree.Rows(document).First(r => r.ItemId == item.Id);

        Assert.Equal("Left sleeve", row.Label);
        Assert.True(row.IsUserNamed);
    }

    [Fact]
    public void AnObjectOutsideEveryPageIsOnThePasteboard()
    {
        CadDocument document = OnePage();
        Layer layer = document.Artboards[0].Layers[0];
        layer.AddItem(Box("far away", 5000, 5000));

        IReadOnlyList<LayerRow> rows = LayerTree.Rows(document);

        int pasteAt = rows.ToList().FindIndex(r => r.Kind == LayerRowKind.Pasteboard);
        Assert.True(pasteAt >= 0, "the out-of-page object should be on the pasteboard");
        Assert.Equal(0, rows[pasteAt].Depth);
        Assert.Equal(1, rows[pasteAt + 1].Depth);
    }

    [Fact]
    public void AGroupThatIsCollapsedHidesItsChildren()
    {
        CadDocument document = OnePage();
        Layer layer = document.Artboards[0].Layers[0];
        var group = new ArtGroup { Name = "group" };
        layer.AddItem(group);
        group.AddItem(Box("inner", 10, 10));

        int open = LayerTree.Rows(document).Count;
        int closed = LayerTree.Rows(document, item => item != group).Count;

        Assert.Equal(open - 1, closed);
    }

    [Fact]
    public void OnlyGroupsCanBeOpened()
    {
        CadDocument document = OnePage();
        Layer layer = document.Artboards[0].Layers[0];
        layer.AddItem(new ArtGroup { Name = "empty" });

        IReadOnlyList<LayerRow> rows = LayerTree.Rows(document);

        Assert.True(rows.Single(r => r.Kind == LayerRowKind.Artboard).Expandable);
        Assert.True(rows.Single(r => r.Kind == LayerRowKind.Layer).Expandable);

        // A path has nothing under it, so the panel draws no triangle for it.
        Assert.False(rows.Single(r => r.Kind == LayerRowKind.Path).Expandable);

        // And a group with no children is expandable in shape but empty, so also no triangle.
        LayerRow empty = rows.Single(r => r.Kind == LayerRowKind.Group);
        Assert.Equal(0, empty.ChildCount);
        Assert.False(empty.Expandable);
    }

    [Fact]
    public void EveryArtboardGetsItsOwnRows()
    {
        var document = new CadDocument();
        for (int i = 0; i < 3; i++)
        {
            var artboard = new Artboard(
                new Size2D(612, 792), new Point2D(i * 652, 0)) { Name = $"Page {i + 1}" };
            Layer layer = artboard.AddLayer("Artwork");
            layer.AddItem(Box($"p{i}", 100, 100));
            document.AddArtboard(artboard);
        }

        IReadOnlyList<LayerRow> rows = LayerTree.Rows(document);

        Assert.Equal(3, rows.Count(r => r.Kind == LayerRowKind.Artboard));
        Assert.Equal(3, rows.Count(r => r.Kind == LayerRowKind.Path));

        // And each object sits under its own page, which is the bug this panel had.
        Assert.Equal(new[] { "Page 1", "Page 2", "Page 3" },
            rows.Where(r => r.Kind == LayerRowKind.Artboard).Select(r => r.Label));

        foreach (LayerRow page in rows.Where(r => r.Kind == LayerRowKind.Artboard))
        {
            Assert.Equal(1, page.ChildCount);
        }
    }

    [Fact]
    public void RowsCarryTheIdTheApiAddressesThemBy()
    {
        CadDocument document = OnePage();
        LayerItem item = document.Artboards[0].Layers[0].Children[0];

        LayerRow row = LayerTree.Rows(document).First(r => r.ItemId is not null);
        Assert.Equal(item.Id, row.ItemId);

        // Pages, layers and the pasteboard are containers, not objects, and have no id.
        Assert.All(
            LayerTree.Rows(document).Where(r =>
                r.Kind is LayerRowKind.Artboard or LayerRowKind.Layer),
            r => Assert.Null(r.ItemId));
    }
}
