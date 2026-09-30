using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// A compound path as an **input** to a boolean, not only as an output.
///
/// The boolean tests covered compound results thoroughly - a band with a hole, the smiley, a PDF round
/// trip - but every one of them fed the engine single-contour inputs. This asks the other question, and
/// asks it with a shape whose arithmetic is exact so a wrong answer cannot hide behind rounding: a square
/// ring, an outer 100x100 square with a 40x40 square missing from its middle.
///
/// The two claims that matter are separate. **Area** catches a hole that was double-counted or dropped.
/// **Whether a point is filled** catches a hole that was filled in, which can leave the area unchanged
/// when the shape covering it happens to make up the difference.
/// </summary>
public class CompoundBooleanInputTests
{
    private const double Outer = 100;
    private const double Inner = 40;
    private const double RingArea = (Outer * Outer) - (Inner * Inner);

    /// <summary>An outer square with an inner square missing: a compound path with one hole.</summary>
    private static PathItem Ring(double x = 0, double y = 0, FillRule rule = FillRule.NonZero,
        bool holeWindsAgainst = true)
    {
        PathItem path = PathFactory.CreateRectangle("ring", new Rect2D(x, y, Outer, Outer));
        path.Fill = FillSpec.Solid(ColorRgb.Black, rule);

        PathItem hole = PathFactory.CreateRectangle("hole", new Rect2D(
            x + ((Outer - Inner) / 2), y + ((Outer - Inner) / 2), Inner, Inner));

        if (holeWindsAgainst)
        {
            hole.SubPaths[0].Reverse();
        }

        SubPath target = path.AddSubPath(closed: true);
        foreach (PathNode node in hole.SubPaths[0].Nodes)
        {
            target.Nodes.Add(new PathNode(node.Anchor, node.InHandle, node.OutHandle));
        }

        path.GeometryChanged();
        return path;
    }

    private static PathItem Square(double x, double y, double size)
    {
        PathItem path = PathFactory.CreateRectangle("square", new Rect2D(x, y, size, size));
        path.Fill = FillSpec.Solid(ColorRgb.Black);
        return path;
    }

    private static double Area(PathItem path)
        => Math.Abs(PathFlattener.Flatten(path).Sum(o => o.SignedArea));

    private static bool Filled(PathItem? path, Point2D point)
        => path is not null &&
           PathFlattener.IsFilled(PathFlattener.Flatten(path), path.Fill.Rule, point);

    /// <summary>The middle of the ring, which is its hole.</summary>
    private static readonly Point2D Hole = new(50, 50);

    /// <summary>A point in the band of the ring.</summary>
    private static readonly Point2D Band = new(10, 50);

    // ---- union -----------------------------------------------------------------

    /// <summary>
    /// A union with a shape that does not touch: the hole is still a hole. A union that filled the hole
    /// would be wrong in a way the area alone would not show, because the two numbers are the same either
    /// way - so both are asserted.
    /// </summary>
    [Fact]
    public void UnionWithADisjointShapeKeepsTheHole()
    {
        PathItem? result = PathBoolean.Combine(
            new[] { Ring(), Square(300, 0, 100) }, BooleanOp.Union);

        Assert.NotNull(result);
        Assert.Equal(RingArea + 10000, Area(result!), 3);
        Assert.False(Filled(result, Hole), "the hole must survive a union with a shape nowhere near it");
        Assert.True(Filled(result, Band));
        Assert.True(Filled(result, new Point2D(350, 50)));
    }

    /// <summary>
    /// A union with a shape that **covers** the hole fills it - the opposite answer in the middle, with
    /// the same shape fed in. This is the pair of cases that makes the point.
    /// </summary>
    [Fact]
    public void UnionWithAShapeCoveringTheHoleFillsIt()
    {
        PathItem? result = PathBoolean.Combine(
            new[] { Ring(), Square(-50, -50, 200) }, BooleanOp.Union);

        Assert.NotNull(result);
        Assert.Equal(40000, Area(result!), 3);
        Assert.True(Filled(result, Hole), "the covering shape fills the hole");
    }

    // ---- subtract --------------------------------------------------------------

    /// <summary>
    /// Subtracting a square from part of the ring removes exactly the overlap and leaves the rest of the
    /// hole alone.
    ///
    /// The overlap, by hand: the square cuts the outer square's bottom-right quadrant, 50x50 = 2500, and
    /// of the 40-wide hole it covers the 20x20 corner between 50 and 70 = 400. So 2500 - 400 = 2100 comes
    /// off the ring's 8400.
    ///
    /// This test caught its own arithmetic on the first run - it said 10x10 for the hole's share and
    /// expected 6000 where the engine said 6300. The engine was right, and checking the working rather
    /// than trusting the expectation is the only reason that was found.
    /// </summary>
    [Fact]
    public void SubtractingFromPartOfTheRingRemovesExactlyTheOverlap()
    {
        const double overlap = (50 * 50) - (20 * 20);

        PathItem? result = PathBoolean.Combine(
            new[] { Ring(), Square(50, 50, 100) }, BooleanOp.Subtract);

        Assert.NotNull(result);
        Assert.Equal(RingArea - overlap, Area(result!), 3);

        // The far part of the band survives; the removed quadrant does not.
        Assert.True(Filled(result, new Point2D(10, 50)));
        Assert.False(Filled(result, new Point2D(80, 80)));
    }

    /// <summary>A square subtracted from the whole face of the ring leaves nothing.</summary>
    [Fact]
    public void SubtractingASquareThatCoversTheRingLeavesNothing()
    {
        Assert.Null(PathBoolean.Combine(
            new[] { Ring(), Square(-50, -50, 200) }, BooleanOp.Subtract));
    }

    // ---- intersect and exclude -------------------------------------------------

    /// <summary>Intersecting a ring with a shape that covers it gives the ring back, hole and all.</summary>
    [Fact]
    public void IntersectingWithACoveringShapeGivesTheRingBack()
    {
        PathItem? result = PathBoolean.Combine(
            new[] { Ring(), Square(-50, -50, 200) }, BooleanOp.Intersect);

        Assert.NotNull(result);
        Assert.Equal(RingArea, Area(result!), 3);
        Assert.False(Filled(result, Hole));
        Assert.True(Filled(result, Band));
    }

    /// <summary>
    /// Excluding a covering square gives the square **less** the ring: the hole filled and the band
    /// hollow. Two mistakes are possible here and they are opposites - forgetting to fill the hole, or
    /// inverting the whole thing - so both the middle and the band are asserted.
    /// </summary>
    [Fact]
    public void ExcludingACoveringShapeInvertsTheRingInsideIt()
    {
        PathItem? result = PathBoolean.Combine(
            new[] { Ring(), Square(-50, -50, 200) }, BooleanOp.Exclude);

        Assert.NotNull(result);
        Assert.Equal(40000 - RingArea, Area(result!), 3);
        Assert.True(Filled(result, Hole), "the hole is inside the square and outside the ring");
        Assert.False(Filled(result, Band));
        Assert.True(Filled(result, new Point2D(-20, -20)));
    }

    // ---- compound against compound ---------------------------------------------

    /// <summary>
    /// Two rings that overlap: the result must be checked by shape rather than by a number I would have
    /// to compute by hand, so every sampled point is compared against the boolean of the inputs.
    /// </summary>
    [Theory]
    [InlineData(BooleanOp.Union)]
    [InlineData(BooleanOp.Subtract)]
    [InlineData(BooleanOp.Intersect)]
    [InlineData(BooleanOp.Exclude)]
    public void CompoundAgainstCompoundAgreesWithThePredicateEverywhere(BooleanOp op)
    {
        PathItem a = Ring(0, 0);
        PathItem b = Ring(60, 60);

        PathItem? result = PathBoolean.Combine(new[] { a, b }, op);

        IReadOnlyList<FlattenedOutline> outlinesOfA = PathFlattener.Flatten(a);
        IReadOnlyList<FlattenedOutline> outlinesOfB = PathFlattener.Flatten(b);

        for (double x = -10; x <= 170; x += 5)
        {
            for (double y = -10; y <= 170; y += 5)
            {
                var point = new Point2D(x, y);
                bool inA = PathFlattener.IsFilled(outlinesOfA, FillRule.NonZero, point);
                bool inB = PathFlattener.IsFilled(outlinesOfB, FillRule.NonZero, point);

                bool expected = op switch
                {
                    BooleanOp.Union => inA || inB,
                    BooleanOp.Subtract => inA && !inB,
                    BooleanOp.Intersect => inA && inB,
                    _ => inA != inB,
                };

                Assert.Equal(expected, Filled(result, point));
            }
        }
    }

    /// <summary>
    /// The same check with a **compound** shape paired against a **plain** one, which is the shape of
    /// every real use: a letter with a counter, cut against something.
    /// </summary>
    [Theory]
    [InlineData(BooleanOp.Union)]
    [InlineData(BooleanOp.Subtract)]
    [InlineData(BooleanOp.Intersect)]
    [InlineData(BooleanOp.Exclude)]
    public void ACompoundAgainstAPlainShapeAgreesWithThePredicateEverywhere(BooleanOp op)
    {
        PathItem ring = Ring(0, 0);
        PathItem square = Square(60, 20, 70);

        PathItem? result = PathBoolean.Combine(new[] { ring, square }, op);

        IReadOnlyList<FlattenedOutline> ringOutlines = PathFlattener.Flatten(ring);
        IReadOnlyList<FlattenedOutline> squareOutlines = PathFlattener.Flatten(square);

        for (double x = -10; x <= 150; x += 5)
        {
            for (double y = -10; y <= 150; y += 5)
            {
                var point = new Point2D(x, y);
                bool inRing = PathFlattener.IsFilled(ringOutlines, FillRule.NonZero, point);
                bool inSquare = PathFlattener.IsFilled(squareOutlines, FillRule.NonZero, point);

                bool expected = op switch
                {
                    BooleanOp.Union => inRing || inSquare,
                    BooleanOp.Subtract => inRing && !inSquare,
                    BooleanOp.Intersect => inRing && inSquare,
                    _ => inRing != inSquare,
                };

                Assert.Equal(expected, Filled(result, point));
            }
        }
    }

    // ---- divide and the fill rules ---------------------------------------------

    /// <summary>Dividing a compound path: the pieces together are the union, none lost or doubled.</summary>
    [Fact]
    public void DividingWithACompoundInputCoversTheUnion()
    {
        PathItem ring = Ring();
        PathItem square = Square(60, 20, 70);

        IReadOnlyList<PathItem> pieces = PathBoolean.Divide(new[] { ring, square });

        Assert.True(pieces.Count >= 2, $"expected several pieces, got {pieces.Count}");

        double total = pieces.Sum(Area);
        double union = Area(PathBoolean.Combine(new[] { ring, square }, BooleanOp.Union)!);

        Assert.Equal(union, total, 3);
    }

    /// <summary>
    /// The two fill rules are two ways of saying the same thing, so a ring with its hole wound the same
    /// way under **even-odd** must behave exactly like one wound against it under **nonzero** - in every
    /// operation. If the engine were reading windings when it should be reading outlines, this is what
    /// would catch it.
    /// </summary>
    [Theory]
    [InlineData(BooleanOp.Union)]
    [InlineData(BooleanOp.Subtract)]
    [InlineData(BooleanOp.Intersect)]
    [InlineData(BooleanOp.Exclude)]
    public void TheTwoFillRulesAgreeAboutACompoundInput(BooleanOp op)
    {
        PathItem nonzero = Ring(rule: FillRule.NonZero, holeWindsAgainst: true);
        PathItem evenOdd = Ring(rule: FillRule.EvenOdd, holeWindsAgainst: false);
        PathItem square = Square(40, 40, 80);

        PathItem? viaNonZero = PathBoolean.Combine(new[] { nonzero, square }, op);
        PathItem? viaEvenOdd = PathBoolean.Combine(new[] { evenOdd, square }, op);

        Assert.Equal(viaNonZero is null, viaEvenOdd is null);

        if (viaNonZero is null)
        {
            return;
        }

        Assert.Equal(Area(viaNonZero!), Area(viaEvenOdd!), 3);

        // And the same picture, point by point.
        for (double x = -10; x <= 140; x += 10)
        {
            for (double y = -10; y <= 140; y += 10)
            {
                var point = new Point2D(x, y);
                Assert.Equal(
                    Filled(viaNonZero, point),
                    PathFlattener.IsFilled(
                        PathFlattener.Flatten(viaEvenOdd!), FillRule.EvenOdd, point));
            }
        }
    }
}
