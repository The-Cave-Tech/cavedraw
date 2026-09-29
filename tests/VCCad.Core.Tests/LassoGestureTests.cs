using VCCad.Core.Model;
using VCCad.Core.Selection;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// The lasso as a gesture rather than as a rule.
///
/// A rectangular marquee is a rectangular path and a lasso is any path, so the two share one
/// selection call and cannot drift apart. What is worth testing separately is the gesture: the
/// path the pointer actually drew, closing itself, at any orientation.
/// </summary>
public class LassoGestureTests
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

    private static CadDocument Page(params LayerItem[] items)
    {
        var document = new CadDocument();
        Artboard page = document.AddArtboard(new Size2D(W, H), "Page 1", new Point2D(0, 0));
        Layer layer = page.AddLayer("Artwork");
        foreach (LayerItem item in items)
        {
            layer.AddItem(item);
        }

        return document;
    }

    /// <summary>A closed loop round a region, walked in the given direction.</summary>
    private static Point2D[] Loop(double x0, double y0, double x1, double y1, bool reversed)
    {
        // Over-sample the edges the way a pointer would, rather than handing over four corners.
        var points = new List<Point2D>();
        const int Steps = 6;

        for (int i = 0; i <= Steps; i++)
        {
            points.Add(new Point2D(x0 + ((x1 - x0) * i / Steps), y0));
        }

        for (int i = 1; i <= Steps; i++)
        {
            points.Add(new Point2D(x1, y0 + ((y1 - y0) * i / Steps)));
        }

        for (int i = 1; i <= Steps; i++)
        {
            points.Add(new Point2D(x1 - ((x1 - x0) * i / Steps), y1));
        }

        for (int i = 1; i <= Steps; i++)
        {
            points.Add(new Point2D(x0, y1 - ((y1 - y0) * i / Steps)));
        }

        if (reversed)
        {
            points.Reverse();
        }

        return points.ToArray();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ADrawnLoopSelectsWhatItEncloses(bool reversed)
    {
        CadDocument document = Page(Box("inside", 100, 100), Box("outside", 400, 400));

        SelectionResult result = SelectionEngine.ByLasso(
            document, new Point2D(50, 50), Loop(50, 50, 250, 250, reversed));

        Assert.Equal("inside", Assert.Single(result.Items).Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ALoopThatMissesSelectsNothing(bool reversed)
    {
        CadDocument document = Page(Box("far", 400, 400));

        SelectionResult result = SelectionEngine.ByLasso(
            document, new Point2D(20, 20), Loop(20, 20, 80, 80, reversed));

        Assert.Empty(result.Items);
    }

    [Fact]
    public void TheShapeClosesItselfWithoutTheCallerJoiningItBack()
    {
        // Three sides of a square, never returned to the start. The closing segment is drawn
        // by the selection code, so a scribble that does not come back still encloses a region.
        CadDocument document = Page(Box("inside", 100, 100));

        SelectionResult result = SelectionEngine.ByLasso(
            document, new Point2D(50, 50), new[]
            {
                new Point2D(250, 50), new Point2D(250, 250), new Point2D(50, 250),
            });

        Assert.Equal("inside", Assert.Single(result.Items).Name);
    }

    [Fact]
    public void AConcavePathSelectsWhatIsInsideIt()
    {
        // An L shape: the object in the notch must NOT be selected, because the notch is not
        // part of the region. A bounding box round this path would swallow it.
        CadDocument document = Page(Box("in-arm", 60, 60), Box("in-notch", 300, 300));

        Point2D[] path = new[]
        {
            new Point2D(20, 20), new Point2D(200, 20), new Point2D(200, 120),
            new Point2D(120, 120), new Point2D(120, 400), new Point2D(20, 400),
        };

        SelectionResult result = SelectionEngine.ByLasso(document, new Point2D(20, 20), path);

        // The notch at (300,300) is outside the L, and the arm's object is inside it.
        Assert.DoesNotContain(result.Items, i => i.Name == "in-notch");
    }

    [Fact]
    public void ALassoStartingOnAPageStaysAboutThatPage()
    {
        var document = new CadDocument();
        for (int i = 0; i < 3; i++)
        {
            Artboard page = document.AddArtboard(
                new Size2D(W, H), $"Page {i + 1}", new Point2D(i * (W + 40), 0));
            page.AddLayer("Artwork").AddItem(Box($"on-page-{i + 1}", 100, 100));
        }

        SelectionResult result = SelectionEngine.ByLasso(
            document, new Point2D(50, 50), Loop(50, 50, 250, 250, false));

        Assert.Equal("on-page-1", Assert.Single(result.Items).Name);
        Assert.Same(document.Artboards[0], result.Focused);
    }

    [Fact]
    public void APathTooShortToBeARegionSelectsNothing()
    {
        CadDocument document = Page(Box("a", 100, 100));

        SelectionResult result = SelectionEngine.ByLasso(
            document, new Point2D(50, 50), new[] { new Point2D(60, 60) });

        Assert.Empty(result.Items);
    }
}
