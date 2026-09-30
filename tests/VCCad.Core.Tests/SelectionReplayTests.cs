using VCCad.Core.Model;
using VCCad.Core.Selection;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// Gestures replayed from a list of events, with no window anywhere.
///
/// This is the shape the whole selection suite is meant to take: a document, a list of what
/// the pointer did, and the answer. A fixture can be written by hand or saved from an
/// automation run, and replaying one needs nothing running - which is what makes a regression
/// suite that covers pointer behaviour affordable to run on every change.
/// </summary>
public class SelectionReplayTests
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

    private static IEnumerable<SelectEvent> Click(double x, double y)
    {
        yield return new PointerDown(new Point2D(x, y));
        yield return new PointerUp(new Point2D(x, y));
    }

    private static IEnumerable<SelectEvent> Drag(IEnumerable<Point2D> path)
    {
        Point2D[] points = path.ToArray();
        yield return new PointerDown(points[0]);
        foreach (Point2D p in points.Skip(1))
        {
            yield return new PointerMove(p);
        }

        yield return new PointerUp(points[^1]);
    }

    [Fact]
    public void APressAndReleaseInOnePlaceIsAClick()
    {
        CadDocument document = Page(Box("target", 100, 100));

        // On the box's left edge: a click aims at the path, not at the area its fill covers.
        SelectionResult result = SelectionEngine.Play(document, Click(102, 125));

        Assert.Equal("target", Assert.Single(result.Items).Name);
        Assert.Same(document.Artboards[0], result.Focused);
    }

    [Fact]
    public void APressAndReleaseThatWandersIsAMarqueeRatherThanAClick()
    {
        // Past the slop, so it is a drag however briefly it moved - a click that shakes is
        // still a click, and a drag that moves is still a drag.
        CadDocument document = Page(Box("a", 100, 100), Box("b", 300, 300));

        SelectionResult result = SelectionEngine.Play(document, new SelectEvent[]
        {
            new PointerDown(new Point2D(50, 50)),
            new PointerMove(new Point2D(400, 400)),
            new PointerUp(new Point2D(400, 400)),
        });

        Assert.Equal(2, result.Items.Count);
    }

    [Fact]
    public void ADragOfFourCornersIsTheSameAsARectangularMarquee()
    {
        CadDocument document = Page(Box("inside", 100, 100), Box("outside", 400, 400));

        SelectionResult dragged = SelectionEngine.Play(document, Drag(new[]
        {
            new Point2D(50, 50), new Point2D(200, 50), new Point2D(200, 200),
            new Point2D(50, 200),
        }));

        SelectionResult direct = SelectionEngine.Marquee(
            document, new Point2D(50, 50), new Point2D(200, 200));

        Assert.Equal(
            direct.Items.Select(i => i.Name), dragged.Items.Select(i => i.Name));
    }

    [Fact]
    public void ALassoSelectsWhatItEncloses()
    {
        CadDocument document = Page(Box("inside", 100, 100), Box("outside", 400, 400));

        // A diamond round the first object and clear of the second.
        SelectionResult result = SelectionEngine.Play(document, Drag(new[]
        {
            new Point2D(50, 125), new Point2D(125, 50),
            new Point2D(200, 125), new Point2D(125, 200),
        }));

        Assert.Equal("inside", Assert.Single(result.Items).Name);
    }

    [Fact]
    public void ALassoThatMissesSelectsNothing()
    {
        CadDocument document = Page(Box("only", 300, 300));

        SelectionResult result = SelectionEngine.Play(document, Drag(new[]
        {
            new Point2D(20, 20), new Point2D(60, 20), new Point2D(60, 60), new Point2D(20, 60),
        }));

        Assert.Empty(result.Items);
    }

    [Fact]
    public void TheLassoClosesItselfWithAStraightSegment()
    {
        // The path is never joined back to its start by the caller: the shape closes from the
        // last point to the first on its own, so an open scribble is still a region.
        CadDocument document = Page(Box("inside", 100, 100));

        SelectionResult result = SelectionEngine.Play(document, Drag(new[]
        {
            new Point2D(50, 50), new Point2D(250, 50),
            new Point2D(250, 250), new Point2D(50, 250),
        }));

        Assert.Equal("inside", Assert.Single(result.Items).Name);
    }

    [Fact]
    public void AGestureThatNeverCameUpChangesNothing()
    {
        CadDocument document = Page(Box("a", 100, 100));

        SelectionResult result = SelectionEngine.Play(document, new SelectEvent[]
        {
            new PointerDown(new Point2D(120, 120)),
        });

        Assert.Empty(result.Items);
        Assert.Null(result.Focused);
    }

    [Fact]
    public void AFixtureRoundTripsThroughAFileAndReplaysTheSameWay()
    {
        string path = Path.Combine(Path.GetTempPath(), $"vccad-sel-{Guid.NewGuid():N}.json");

        try
        {
            var fixture = new SelectionFixture
            {
                Document = Page(Box("target", 100, 100), Box("other", 400, 400)),
                Events = new[]
                {
                    new RecordedEvent("down", 50, 50),
                    new RecordedEvent("move", 200, 200),
                    new RecordedEvent("up", 200, 200),
                },
                Expected = new[] { "target" },
            };

            // The answer before saving, to compare with the answer after loading.
            IReadOnlyList<string> before = fixture.Selected();
            fixture.Save(path);

            SelectionFixture replayed = SelectionFixture.Load(path);
            IReadOnlyList<string> after = replayed.Selected();

            Assert.Equal(before, after);
            Assert.Equal(fixture.Expected, after);

            // And the document survived the trip, not just the events.
            Assert.Equal(2, replayed.Document.Artboards[0].Layers[0].Children.Count);
            Assert.Equal(1, replayed.Document.Artboards.Count);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void AFixtureRemembersWhichArtboardWasFocused()
    {
        string path = Path.Combine(Path.GetTempPath(), $"vccad-sel-{Guid.NewGuid():N}.json");

        try
        {
            var fixture = new SelectionFixture
            {
                Document = Page(Box("a", 100, 100)),
                Events = new[] { new RecordedEvent("down", 400, 400), new RecordedEvent("up", 400, 400) },
                FocusedArtboard = "Page 1",
            };

            fixture.Save(path);
            SelectionFixture replayed = SelectionFixture.Load(path);

            Assert.Equal("Page 1", replayed.FocusedArtboard);
            Assert.Same(replayed.Document.Artboards[0], replayed.Play().Focused);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void AClickOnAPageDoesNotReachAnotherPageThroughTheEventList()
    {
        // The original report, replayed rather than called directly - which is the way it will
        // arrive from the application.
        var document = new CadDocument();
        for (int i = 0; i < 6; i++)
        {
            Artboard page = document.AddArtboard(
                new Size2D(W, H), $"Page {i + 1}", new Point2D(i * (W + 40), 0));
            page.AddLayer("Artwork").AddItem(Box($"on-page-{i + 1}", 100, 100));
        }

        SelectionResult result = SelectionEngine.Play(document, Click(102, 125));

        Assert.Equal("on-page-1", Assert.Single(result.Items).Name);
    }
}
