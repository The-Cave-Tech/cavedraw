using VCCad.Core.Model;
using VCCad.Core.Selection;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// What a marquee selects.
///
/// The rules turn on where the drag STARTED, which is the part that is easy to get wrong:
/// whether a marquee is about one page or about whole pages is decided the moment the button
/// goes down, and must not change as the pointer moves. A drag that began on a page is about
/// that page however far it is taken.
///
/// Orientation matters too. A marquee dragged up and to the left encloses exactly the same
/// region as one dragged down and to the right, and every one of these is run both ways.
/// </summary>
public class MarqueeSelectionTests
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

    /// <summary>The four ways to drag the same rectangle, so orientation is always covered.</summary>
    public static IEnumerable<object[]> Orientations()
    {
        yield return new object[] { 0 };
        yield return new object[] { 1 };
        yield return new object[] { 2 };
        yield return new object[] { 3 };
    }

    private static (Point2D From, Point2D To) Corners(double x0, double y0, double x1, double y1, int variant)
        => variant switch
        {
            0 => (new Point2D(x0, y0), new Point2D(x1, y1)),
            1 => (new Point2D(x1, y0), new Point2D(x0, y1)),
            2 => (new Point2D(x0, y1), new Point2D(x1, y0)),
            _ => (new Point2D(x1, y1), new Point2D(x0, y0)),
        };

    [Theory]
    [MemberData(nameof(Orientations))]
    public void AMarqueeEnclosesTheSameThingWhicheverWayItIsDragged(int variant)
    {
        CadDocument document = Pages(1, (board, _) =>
        {
            board.Layers[0].AddItem(Box("inside", 100, 100, 50));
            board.Layers[0].AddItem(Box("outside", 400, 400, 50));
        });

        (Point2D from, Point2D to) = Corners(50, 50, 200, 200, variant);
        SelectionResult result = SelectionEngine.Marquee(document, from, to);

        Assert.Equal("inside", Assert.Single(result.Items).Name);
    }

    [Theory]
    [MemberData(nameof(Orientations))]
    public void APartlyCoveredObjectIsNotSelected(int variant)
    {
        CadDocument document = Pages(1, (board, _) =>
            board.Layers[0].AddItem(Box("straddles", 100, 100, 200)));

        (Point2D from, Point2D to) = Corners(50, 50, 200, 200, variant);
        SelectionResult result = SelectionEngine.Marquee(document, from, to);

        Assert.Empty(result.Items);
    }

    [Fact]
    public void AMarqueeInsideOnePageIgnoresTheOtherPages()
    {
        // Each page has an object at the same local place. A marquee over page 1's must take
        // page 1's and none of the others - the fault that started all this.
        CadDocument document = Pages(4, (board, i) =>
            board.Layers[0].AddItem(Box($"on-page-{i + 1}", 100, 100, 50)));

        SelectionResult result = SelectionEngine.Marquee(
            document, new Point2D(50, 50), new Point2D(200, 200));

        Assert.Equal("on-page-1", Assert.Single(result.Items).Name);
        Assert.Same(document.Artboards[0], result.Focused);
    }

    [Fact]
    public void AMarqueeStartedOnAPageMayGrowBeyondItWithoutChangingTheRule()
    {
        // Began on page 1, dragged far enough to touch page 2. It is still page 1's objects
        // being selected: the rule is set by where the drag started, and nothing the pointer
        // does afterwards should change what the person is doing.
        CadDocument document = Pages(3, (board, i) =>
        {
            board.Layers[0].AddItem(Box($"near-{i + 1}", 100, 100, 50));
            board.Layers[0].AddItem(Box($"far-{i + 1}", 500, 100, 50));
        });

        SelectionResult result = SelectionEngine.Marquee(
            document, new Point2D(50, 50), new Point2D(W + 600, 300));

        // Page 2's and page 3's objects are outside the marquee's reach entirely, and
        // page 1's two are inside it - the rule being that only page 1 was ever in play.
        Assert.DoesNotContain(result.Items, i => i.Name.EndsWith("-2"));
        Assert.DoesNotContain(result.Items, i => i.Name.EndsWith("-3"));
        Assert.Equal("near-1", result.Items[0].Name);
        Assert.All(result.Items, i => Assert.Equal("1", i.Name.Split('-')[1]));

        // And the page stays focused even though the marquee left it.
        Assert.Same(document.Artboards[0], result.Focused);
        Assert.Empty(result.Artboards);
    }

    [Fact]
    public void AGrowingMarqueeBeyondTheFocusedArtboardStillDisqualifiesOtherPages()
    {
        // The rule from the objective, tested where it is easiest to get wrong: a marquee now
        // covering three whole pages of artwork still selects only the objects of the page it
        // began on, because it began on that page.
        CadDocument document = Pages(3, (board, i) =>
            board.Layers[0].AddItem(Box($"item-{i + 1}", 100, 100, 50)));

        // The far corner is inside page 2, and page 2's object is well within the marquee.
        // If the rule were decided by where the pointer IS rather than where it started, page
        // 2's object is what would come back - which is what makes this worth asserting.
        SelectionResult result = SelectionEngine.Marquee(
            document, new Point2D(20, 20), new Point2D(W + 600, H));

        Assert.Empty(result.Artboards);
        Assert.Equal("item-1", Assert.Single(result.Items).Name);
    }

    [Fact]
    public void AMarqueeFromThePasteboardTakesTheFirstPageItTouches()
    {
        CadDocument document = Pages(3, (board, i) =>
            board.Layers[0].AddItem(Box($"item-{i + 1}", 100, 100, 50)));

        // Starts in the gap between page 1 and page 2, so the first page it touches is page
        // 2 - and it covers page 2's object without covering page 2 whole.
        SelectionResult result = SelectionEngine.Marquee(
            document, new Point2D(W + 10, 50), new Point2D(W + 200, 200));

        Assert.Empty(result.Artboards);
        Assert.Equal("item-2", Assert.Single(result.Items).Name);
    }

    [Fact]
    public void AMarqueeThatEnclosesAPageWholeSelectsThePageNotItsContents()
    {
        CadDocument document = Pages(2, (board, i) =>
            board.Layers[0].AddItem(Box($"item-{i + 1}", 100, 100, 50)));

        SelectionResult result = SelectionEngine.Marquee(
            document, new Point2D(-50, -50), new Point2D(W + 50, H + 50));

        Assert.Empty(result.Items);
        Assert.Equal(2, result.Artboards.Count);
    }

    [Fact]
    public void EnclosingOnePageWholeAndTouchingAnotherTakesBothWhole()
    {
        CadDocument document = Pages(3, (board, i) =>
            board.Layers[0].AddItem(Box($"item-{i + 1}", 100, 100, 50)));

        // Page 1 whole, and far enough to clip page 2 and page 3.
        SelectionResult result = SelectionEngine.Marquee(
            document, new Point2D(-20, -20), new Point2D(W * 3 + 100, H + 20));

        Assert.Empty(result.Items);
        Assert.Equal(3, result.Artboards.Count);
    }

    [Fact]
    public void AMarqueeOverNothingSelectsNothing()
    {
        CadDocument document = Pages(3, (board, i) =>
            board.Layers[0].AddItem(Box("a", 400, 400, 20)));

        SelectionResult result = SelectionEngine.Marquee(
            document, new Point2D(20, 20), new Point2D(60, 60));

        Assert.Empty(result.Items);
        Assert.Empty(result.Artboards);
    }

    [Fact]
    public void AClippedObjectIsSelectedByWhatSurvivesRatherThanByItsBounds()
    {
        // A wide rectangle cut down to its left fifth. A marquee over the left fifth selects
        // it; one over the cut-away part does not, even though the rectangle's bounds reach
        // there.
        CadDocument document = Pages(1, (board, _) =>
        {
            PathItem wide = Box("clipped", 0, 0, 500);
            wide.Clips.Add(RectClip(0, 0, 100, 500));
            board.Layers[0].AddItem(wide);
        });

        SelectionResult overVisible = SelectionEngine.Marquee(
            document, new Point2D(-10, -10), new Point2D(110, 510));

        Assert.Equal("clipped", Assert.Single(overVisible.Items).Name);

        SelectionResult overCutAway = SelectionEngine.Marquee(
            document, new Point2D(200, 100), new Point2D(300, 300));

        Assert.Empty(overCutAway.Items);
    }

    [Fact]
    public void AClippedObjectIsNotSelectedByAMarqueeOverEverythingItUsedToBe()
    {
        // Enclosing the object's own bounds is not enough: the visible part is what counts,
        // and the clip cut most of it away.
        CadDocument document = Pages(1, (board, _) =>
        {
            PathItem wide = Box("clipped", 0, 0, 500);
            wide.Clips.Add(RectClip(0, 0, 100, 20));
            board.Layers[0].AddItem(wide);
        });

        // Covers x 0..150 and y 0..30 only - the strip that survives.
        Assert.Single(SelectionEngine.Marquee(
            document, new Point2D(-5, -5), new Point2D(150, 30)).Items);

        // A marquee over the whole rectangle also selects it, because it contains the strip.
        Assert.Single(SelectionEngine.Marquee(
            document, new Point2D(-5, -5), new Point2D(520, 520)).Items);
    }

    /// <summary>A rectangular clip, in document coordinates.</summary>
    private static ClipSpec RectClip(double x, double y, double w, double h)
    {
        var clip = new ClipSpec { Rule = FillRule.NonZero };
        var sub = new SubPath { IsClosed = true };
        sub.Nodes.Add(new PathNode(new Point2D(x, y)));
        sub.Nodes.Add(new PathNode(new Point2D(x + w, y)));
        sub.Nodes.Add(new PathNode(new Point2D(x + w, y + h)));
        sub.Nodes.Add(new PathNode(new Point2D(x, y + h)));
        clip.SubPaths.Add(sub);
        return clip;
    }
}
