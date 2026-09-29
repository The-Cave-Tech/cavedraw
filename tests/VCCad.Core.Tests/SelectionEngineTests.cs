using VCCad.Core.Model;
using VCCad.Core.Selection;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// What a click selects, and which artboard it belongs to.
///
/// The reported fault was that clicking inside page 1 selected items on page 6. It was not a
/// near miss: the hit test took the point, subtracted EVERY artboard's origin in turn, and
/// asked each one's objects whether they were under the result. A point inside page 1 is also
/// a point inside page 6 shifted by five pages, so page 6 answered.
///
/// The fix is one rule - a point belongs to the artboard that contains it and to no other -
/// and these tests are built as real documents rather than by stubbing a hit test, because the
/// fault was in the traversal and not in the geometry.
/// </summary>
public class SelectionEngineTests
{
    private const double W = 612;
    private const double H = 792;

    private static PathItem Box(string name, double x, double y, double size = 50)
    {
        var path = new PathItem { Name = name };
        SubPath sub = path.AddSubPath(closed: true);
        sub.Nodes.Add(new PathNode(new Point2D(x, y)));
        sub.Nodes.Add(new PathNode(new Point2D(x + size, y)));
        sub.Nodes.Add(new PathNode(new Point2D(x + size, y + size)));
        sub.Nodes.Add(new PathNode(new Point2D(x, y + size)));
        return path;
    }

    /// <summary>
    /// A page per column, laid out the way the document lays a tiled pattern out - which is
    /// what makes a point in one page a point in another at the same offset.
    /// </summary>
    private static CadDocument Pages(int count, Action<Artboard, int>? fill = null)
    {
        var document = new CadDocument();

        for (int i = 0; i < count; i++)
        {
            var artboard = new Artboard(
                new Size2D(W, H), new Point2D(i * (W + 40), 0)) { Name = $"Page {i + 1}" };

            Layer layer = artboard.AddLayer("Artwork");
            fill?.Invoke(artboard, i);
            document.AddArtboard(artboard);
        }

        return document;
    }

    [Fact]
    public void APointBelongsToThePageThatContainsIt()
    {
        // One object per page, each at the same local position. A click there must find the
        // one on its own page and nothing else.
        CadDocument document = Pages(6, (board, i) =>
            board.Layers[0].AddItem(Box($"on-page-{i + 1}", 100, 100)));

        for (int i = 0; i < 6; i++)
        {
            Artboard page = document.Artboards[i];
            Point2D point = new(page.X + 120, 120);

            SelectionResult result = SelectionEngine.Click(document, point);

            LayerItem selected = Assert.Single(result.Items);
            Assert.Equal($"on-page-{i + 1}", selected.Name);
            Assert.Same(page, result.Focused);
        }
    }

    [Fact]
    public void ClickingPageOneDoesNotSelectPageSix()
    {
        // The reported case, named after it, so a regression says what it broke.
        CadDocument document = Pages(6, (board, i) =>
            board.Layers[0].AddItem(Box($"on-page-{i + 1}", 100, 100)));

        Point2D insidePageOne = new(120, 120);
        SelectionResult result = SelectionEngine.Click(document, insidePageOne);

        Assert.DoesNotContain(result.Items, i => i.Name == "on-page-6");
        Assert.Single(result.Items);
        Assert.Equal("on-page-1", result.Items[0].Name);
    }

    [Fact]
    public void AnEmptyPageFocusesItselfAndSelectsNothing()
    {
        CadDocument document = Pages(3);

        SelectionResult result = SelectionEngine.Click(document, new Point2D(120, 120));

        Assert.Empty(result.Items);
        Assert.Same(document.Artboards[0], result.Focused);
    }

    [Fact]
    public void ClickingOutsideEveryPageClearsTheFocus()
    {
        CadDocument document = Pages(3, (board, i) => board.Layers[0].AddItem(Box("a", 10, 10)));

        // Between the first two pages is pasteboard.
        SelectionResult result = SelectionEngine.Click(document, new Point2D(W + 20, 100));

        Assert.Empty(result.Items);
        Assert.Null(result.Focused);
    }

    [Fact]
    public void APointOnTheEdgeIsInsideThePage()
    {
        CadDocument document = Pages(2, (board, i) => board.Layers[0].AddItem(Box("a", 0, 0)));

        Assert.Same(document.Artboards[0], SelectionEngine.ArtboardAt(
            document.Artboards, new Point2D(0, 0)));
        Assert.Same(document.Artboards[0], SelectionEngine.ArtboardAt(
            document.Artboards, new Point2D(W, H)));

        // And the gap between pages is not in either of them.
        Assert.Null(SelectionEngine.ArtboardAt(
            document.Artboards, new Point2D(W + 1, 100)));
    }

    [Fact]
    public void AnInvisiblePageHoldsNothing()
    {
        CadDocument document = Pages(2, (board, i) => board.Layers[0].AddItem(Box("a", 100, 100)));
        document.Artboards[0].IsVisible = false;

        Assert.Null(SelectionEngine.ArtboardAt(document.Artboards, new Point2D(120, 120)));
        Assert.Empty(SelectionEngine.Click(document, new Point2D(120, 120)).Items);
    }

    [Fact]
    public void ALockedObjectIsNotPicked()
    {
        CadDocument document = Pages(1, (board, i) =>
        {
            PathItem box = Box("locked", 100, 100);
            box.IsLocked = true;
            board.Layers[0].AddItem(box);
        });

        Assert.Empty(SelectionEngine.Click(document, new Point2D(120, 120)).Items);
    }

    [Fact]
    public void AnObjectOnNoPageIsPickedFromThePasteboard()
    {
        var document = new CadDocument();
        document.AddArtboard(new Artboard(new Size2D(W, H), new Point2D(0, 0)) { Name = "Page 1" });
        document.Orphans.AddItem(Box("loose", 900, 900));

        SelectionResult result = SelectionEngine.Click(document, new Point2D(920, 920));

        Assert.Single(result.Items);
        Assert.Equal("loose", result.Items[0].Name);

        // The pasteboard is not an artboard, so nothing is focused.
        Assert.Null(result.Focused);
    }

    [Fact]
    public void TheTopmostObjectWins()
    {
        CadDocument document = Pages(1, (board, _) =>
        {
            board.Layers[0].AddItem(Box("under", 100, 100, 80));
            board.Layers[0].AddItem(Box("over", 110, 110, 40));
        });

        SelectionResult result = SelectionEngine.Click(document, new Point2D(120, 120));

        // Later in the layer paints above, which is what the pointer should find.
        Assert.Equal("over", Assert.Single(result.Items).Name);
    }
}
