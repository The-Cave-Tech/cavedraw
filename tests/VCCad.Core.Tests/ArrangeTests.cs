using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// Aligning and distributing.
///
/// The tests assert **the relation the operation is for** - that the edges are equal, that the gaps are
/// equal - rather than that something moved, and they use objects of **unequal sizes**, because equal
/// sizes hide an error in which measurement is being equalised.
///
/// The overlapping cases are the ones this feature is really about, and the arithmetic behind them is not
/// a detail: when objects overlap, the sum of their widths is greater than the span they occupy, so the
/// gap that would make the spacing equal is negative and there is no equal-gap answer at all.
/// </summary>
public class ArrangeTests
{
    private static PathItem Box(string name, double x, double y, double width, double height)
        => PathFactory.CreateRectangle(name, new Rect2D(x, y, width, height));

    /// <summary>Applies the deltas the engine returns, the way the caller would.</summary>
    private static void Apply(IReadOnlyList<(LayerItem Item, Vector2D Delta)> moves)
    {
        foreach ((LayerItem item, Vector2D delta) in moves)
        {
            ((PathItem)item).TranslateGeometryBy(delta);
        }
    }

    private static Rect2D At(LayerItem item) => ItemBounds.Of(item);

    // ---- align -----------------------------------------------------------------

    /// <summary>Every alignment: the chosen measurement ends up equal across the selection.</summary>
    [Theory]
    [InlineData(ArrangeAxis.Horizontal, ArrangeEdge.Start)]
    [InlineData(ArrangeAxis.Horizontal, ArrangeEdge.Centre)]
    [InlineData(ArrangeAxis.Horizontal, ArrangeEdge.End)]
    [InlineData(ArrangeAxis.Vertical, ArrangeEdge.Start)]
    [InlineData(ArrangeAxis.Vertical, ArrangeEdge.Centre)]
    [InlineData(ArrangeAxis.Vertical, ArrangeEdge.End)]
    public void AligningPutsTheChosenEdgeOnOneLine(ArrangeAxis axis, ArrangeEdge edge)
    {
        var items = new List<LayerItem>
        {
            Box("a", 0, 0, 10, 10),
            Box("b", 50, 20, 30, 25),
            Box("c", 100, 40, 20, 15),
        };

        // The line is the selection's own extent, measured before anything moves.
        double expected = edge switch
        {
            ArrangeEdge.Start => axis == ArrangeAxis.Horizontal ? 0 : 0,
            ArrangeEdge.End => axis == ArrangeAxis.Horizontal ? 120 : 55,
            _ => axis == ArrangeAxis.Horizontal ? 60 : 27.5,
        };

        Apply(Arrange.Align(items, axis, edge));

        foreach (LayerItem item in items)
        {
            Rect2D box = At(item);
            double actual = edge switch
            {
                ArrangeEdge.Start => axis == ArrangeAxis.Horizontal ? box.X : box.Y,
                ArrangeEdge.End => axis == ArrangeAxis.Horizontal ? box.X + box.Width : box.Y + box.Height,
                _ => axis == ArrangeAxis.Horizontal
                    ? box.X + (box.Width / 2)
                    : box.Y + (box.Height / 2),
            };

            Assert.Equal(expected, actual, 6);
        }
    }

    /// <summary>Aligning across the other axis changes nothing there.</summary>
    [Fact]
    public void AligningDoesNotMoveTheOtherAxis()
    {
        var items = new List<LayerItem>
        {
            Box("a", 0, 0, 10, 10),
            Box("b", 50, 20, 30, 25),
        };

        Apply(Arrange.Align(items, ArrangeAxis.Horizontal, ArrangeEdge.Start));

        Assert.Equal(0, At(items[0]).Y, 6);
        Assert.Equal(20, At(items[1]).Y, 6);
    }

    /// <summary>An object already on the line is not given a move at all, rather than a zero one.</summary>
    [Fact]
    public void AnObjectAlreadyOnTheLineIsNotMoved()
    {
        var items = new List<LayerItem>
        {
            Box("a", 0, 0, 10, 10),
            Box("b", 50, 20, 30, 25),
        };

        IReadOnlyList<(LayerItem Item, Vector2D Delta)> moves =
            Arrange.Align(items, ArrangeAxis.Horizontal, ArrangeEdge.Start);

        Assert.DoesNotContain(moves, m => ReferenceEquals(m.Item, items[0]));
        Assert.Single(moves);
    }

    [Fact]
    public void AligningFewerThanTwoObjectsDoesNothing()
    {
        var items = new List<LayerItem> { Box("a", 0, 0, 10, 10) };

        Assert.Empty(Arrange.Align(items, ArrangeAxis.Horizontal, ArrangeEdge.Start));
        Assert.Empty(Arrange.Distribute(items, ArrangeAxis.Horizontal));
    }

    // ---- distribute: the case with room ----------------------------------------

    /// <summary>
    /// Equal gaps, with unequal widths - which is what makes it a test: equalising the wrong measurement
    /// (the starts, say) passes with equal objects and fails with these.
    /// </summary>
    [Fact]
    public void DistributingEqualisesTheGapsBetweenUnequalObjects()
    {
        var items = new List<LayerItem>
        {
            Box("a", 0, 0, 10, 10),
            Box("b", 100, 0, 30, 10),
            Box("c", 200, 0, 20, 10),
        };

        // The extent is 0..220 and the objects take 60 of it, so 160 is shared between two gaps.
        Apply(Arrange.Distribute(items, ArrangeAxis.Horizontal));

        double gap1 = At(items[1]).X - (At(items[0]).X + At(items[0]).Width);
        double gap2 = At(items[2]).X - (At(items[1]).X + At(items[1]).Width);

        Assert.Equal(80, gap1, 6);
        Assert.Equal(gap1, gap2, 6);

        // The extent is held: the first object has not moved and the last still ends where it did.
        Assert.Equal(0, At(items[0]).X, 6);
        Assert.Equal(220, At(items[2]).X + At(items[2]).Width, 6);
    }

    /// <summary>
    /// **Order is the stack, not the position.** Three objects whose z-order disagrees with where they
    /// sit come out laid out in z-order - which is the whole point of asking to distribute a stack.
    /// </summary>
    [Fact]
    public void DistributingLaysObjectsOutInStackOrder()
    {
        // In z-order: the wide one, then the narrow one, then the middle one. Positionally: middle,
        // wide, narrow.
        var items = new List<LayerItem>
        {
            Box("first", 0, 0, 40, 10),
            Box("second", 100, 0, 10, 10),
            Box("third", 200, 0, 25, 10),
        };

        Apply(Arrange.Distribute(items, ArrangeAxis.Horizontal));

        // Laid out in the order given, so each one's start is past the previous one's end.
        Assert.True(At(items[0]).X + At(items[0]).Width <= At(items[1]).X,
            "the second should follow the first in the order they were given");
        Assert.True(At(items[1]).X + At(items[1]).Width <= At(items[2]).X,
            "the third should follow the second");
    }

    /// <summary>The other anchor lays the stack out back towards the start, so the order along the axis mirrors.</summary>
    [Fact]
    public void TheEndAnchorLaysTheStackOutTheOtherWay()
    {
        var items = new List<LayerItem>
        {
            Box("a", 0, 0, 40, 10),
            Box("b", 100, 0, 10, 10),
            Box("c", 200, 0, 25, 10),
        };

        Apply(Arrange.Distribute(items, ArrangeAxis.Horizontal, ArrangeAnchor.End));

        // The last in the stack is placed first, so it is now the leftmost of the three.
        Assert.True(At(items[2]).X < At(items[0]).X,
            $"the last object should have been placed first: {At(items[2]).X} vs {At(items[0]).X}");
        Assert.Equal(0, At(items[2]).X, 6);
        // The extent is 0..225 (40 + 10 + 25 wide, with the gaps the two others leave).
        Assert.Equal(225, At(items[0]).X + At(items[0]).Width, 6);
    }

    /// <summary>Both axes behave the same way, not just the horizontal one.</summary>
    [Fact]
    public void DistributingWorksVertically()
    {
        var items = new List<LayerItem>
        {
            Box("a", 0, 0, 10, 10),
            Box("b", 0, 100, 10, 30),
            Box("c", 0, 200, 10, 20),
        };

        Apply(Arrange.Distribute(items, ArrangeAxis.Vertical));

        double gap1 = At(items[1]).Y - (At(items[0]).Y + At(items[0]).Height);
        double gap2 = At(items[2]).Y - (At(items[1]).Y + At(items[1]).Height);

        Assert.Equal(80, gap1, 6);
        Assert.Equal(gap1, gap2, 6);
    }

    /// <summary>Two objects have nothing between them to even out, so they are left alone.</summary>
    [Fact]
    public void DistributingTwoObjectsDoesNothing()
    {
        var items = new List<LayerItem>
        {
            Box("a", 0, 0, 10, 10),
            Box("b", 100, 0, 10, 10),
        };

        Assert.Empty(Arrange.Distribute(items, ArrangeAxis.Horizontal));
    }

    // ---- distribute: the case with no room -------------------------------------

    /// <summary>
    /// Objects that overlap have taken all the room: the sum of their widths is greater than the span, so
    /// the equal-gap answer is negative. The graceful answer is equal **centres** over the same extent,
    /// which leaves their sizes and their order alone and does not throw anything away.
    /// </summary>
    [Fact]
    public void OverlappingObjectsAreSpacedByCentreInsteadOfByGap()
    {
        // Three objects 100 wide across 140: 300 of width in 140 of span.
        var items = new List<LayerItem>
        {
            Box("a", 0, 0, 100, 10),
            Box("b", 30, 0, 60, 10),
            Box("c", 40, 0, 100, 10),
        };

        Assert.True(Arrange.OverlapsTooMuchForGaps(items, ArrangeAxis.Horizontal),
            "this set cannot be spaced by gaps, which is the point of the test");

        Apply(Arrange.Distribute(items, ArrangeAxis.Horizontal));

        // The centres are evenly spaced across the same extent, inset by half of each end object's width.
        double centre0 = At(items[0]).X + (At(items[0]).Width / 2);
        double centre1 = At(items[1]).X + (At(items[1]).Width / 2);
        double centre2 = At(items[2]).X + (At(items[2]).Width / 2);

        Assert.Equal(50, centre0, 6);
        Assert.Equal(70, centre1, 6);
        Assert.Equal(90, centre2, 6);

        // The extent is still held at both ends, and nothing changed size.
        Assert.Equal(0, At(items[0]).X, 6);
        Assert.Equal(140, At(items[2]).X + At(items[2]).Width, 6);
        Assert.Equal(100, At(items[0]).Width, 6);
        Assert.Equal(60, At(items[1]).Width, 6);
    }

    /// <summary>Objects that exactly fill the span have a gap of zero, where both rules agree.</summary>
    [Fact]
    public void ObjectsThatExactlyFillTheSpanAgreeAtTheBoundary()
    {
        var items = new List<LayerItem>
        {
            Box("a", 0, 0, 40, 10),
            Box("b", 40, 0, 30, 10),
            Box("c", 70, 0, 30, 10),
        };

        Assert.False(Arrange.OverlapsTooMuchForGaps(items, ArrangeAxis.Horizontal));

        Apply(Arrange.Distribute(items, ArrangeAxis.Horizontal));

        // Already touching, and already even: nothing moves.
        Assert.Equal(0, At(items[0]).X, 6);
        Assert.Equal(40, At(items[1]).X, 6);
        Assert.Equal(70, At(items[2]).X, 6);
    }

    /// <summary>A path with no extent is handled rather than dividing by zero.</summary>
    [Fact]
    public void AnObjectWithNoExtentIsHandled()
    {
        var empty = new PathItem { Name = "empty" };
        empty.AddSubPath(closed: false).AppendNode(new Point2D(50, 50));

        var items = new List<LayerItem>
        {
            Box("a", 0, 0, 10, 10),
            empty,
            Box("c", 100, 0, 10, 10),
        };

        Apply(Arrange.Distribute(items, ArrangeAxis.Horizontal));

        // It takes its place in the sequence without the arithmetic falling over.
        Assert.Equal(0, At(items[0]).X, 6);
        Assert.Equal(110, At(items[2]).X + At(items[2]).Width, 6);
    }
}
