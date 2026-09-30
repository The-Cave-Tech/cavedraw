using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// Union, subtract, intersect and exclude.
///
/// **Area is the oracle.** A boolean result can look plausible in a screenshot and be wrong - a doubled
/// edge, a missing hole, a contour wound the wrong way - and every one of those shows up immediately in
/// the area. The stronger check is used where it matters too: for points sampled across the bounding
/// box, whether the result is filled must equal the boolean of whether the inputs are, which tests the
/// shape rather than a single number.
/// </summary>
public class PathBooleanTests
{
    private static PathItem Square(double x, double y, double size)
    {
        PathItem path = PathFactory.CreateRectangle("square", new Rect2D(x, y, size, size));
        path.Fill = FillSpec.Solid(ColorRgb.Black);
        return path;
    }

    private static PathItem Circle(double cx, double cy, double radius)
    {
        PathItem path = PathFactory.CreateEllipse("circle", new Point2D(cx, cy), radius, radius);
        path.Fill = FillSpec.Solid(ColorRgb.Black);
        return path;
    }

    private static double Area(PathItem path)
    {
        double total = 0;
        foreach (FlattenedOutline outline in PathFlattener.Flatten(path))
        {
            total += outline.SignedArea;
        }

        return Math.Abs(total);
    }

    private static bool Filled(PathItem? path, Point2D point, double tolerance = 0)
    {
        if (path is null)
        {
            return false;
        }

        return PathFlattener.IsFilled(PathFlattener.Flatten(path, tolerance == 0 ? null : tolerance), FillRule.NonZero, point);
    }

    // ---- areas -----------------------------------------------------------------

    [Fact]
    public void UnionOfTwoOverlappingSquaresIsTheirSumLessTheOverlap()
    {
        PathItem? result = PathBoolean.Combine(
            new[] { Square(0, 0, 100), Square(50, 50, 100) }, BooleanOp.Union);

        Assert.NotNull(result);
        Assert.Equal(17500, Area(result!), 0);
        Assert.Single(result!.SubPaths);
    }

    /// <summary>
    /// Subtracting an inner square from an outer one gives **one object with two contours** - the band -
    /// which is the whole point of a compound path and the case the engine has to get right.
    /// </summary>
    [Fact]
    public void SubtractingAnInnerSquareLeavesABandWithAHole()
    {
        PathItem? result = PathBoolean.Combine(
            new[] { Square(0, 0, 100), Square(25, 25, 50) }, BooleanOp.Subtract);

        Assert.NotNull(result);
        Assert.Equal(2, result!.SubPaths.Count);
        Assert.Equal(7500, Area(result), 0);

        // The hole is a hole: the centre is empty, the band is not.
        Assert.False(Filled(result, new Point2D(50, 50)));
        Assert.True(Filled(result, new Point2D(10, 50)));
    }

    [Fact]
    public void IntersectKeepsOnlyTheOverlap()
    {
        PathItem? result = PathBoolean.Combine(
            new[] { Square(0, 0, 100), Square(50, 50, 100) }, BooleanOp.Intersect);

        Assert.NotNull(result);
        Assert.Equal(2500, Area(result!), 0);
    }

    [Fact]
    public void ExcludeKeepsWhatAnOddNumberOfShapesCover()
    {
        PathItem? result = PathBoolean.Combine(
            new[] { Square(0, 0, 100), Square(50, 50, 100) }, BooleanOp.Exclude);

        Assert.NotNull(result);
        Assert.Equal(15000, Area(result!), 0);

        // The overlap is covered twice, so it is not in the result; the rest is.
        Assert.False(Filled(result, new Point2D(75, 75)));
        Assert.True(Filled(result, new Point2D(20, 20)));
        Assert.True(Filled(result, new Point2D(120, 120)));
    }

    // ---- the cases that break clippers -----------------------------------------

    /// <summary>Shapes that do not touch: the union is both, as two contours.</summary>
    [Fact]
    public void DisjointShapesUnionToBoth()
    {
        PathItem? result = PathBoolean.Combine(
            new[] { Square(0, 0, 100), Square(200, 0, 100) }, BooleanOp.Union);

        Assert.NotNull(result);
        Assert.Equal(2, result!.SubPaths.Count);
        Assert.Equal(20000, Area(result), 0);
    }

    /// <summary>
    /// Two squares sharing an edge. Every edge of the shared border is duplicated in the input, which is
    /// exactly the coincidence that derails an edge-by-edge clipper; here the border is one piece of
    /// geometry, classified once, and vanishes because both its sides are inside.
    /// </summary>
    [Fact]
    public void ShapesSharingAnEdgeUnionCleanly()
    {
        PathItem? result = PathBoolean.Combine(
            new[] { Square(0, 0, 100), Square(100, 0, 100) }, BooleanOp.Union);

        Assert.NotNull(result);
        Assert.Equal(20000, Area(result!), 0);

        // One outline, not two abutting ones: the shared border is gone.
        Assert.Single(result!.SubPaths);
    }

    /// <summary>A shape subtracted from itself covers nothing, and that is reported rather than left as
    /// an invisible object.</summary>
    [Fact]
    public void SubtractingAShapeFromItselfIsEmpty()
    {
        PathItem? result = PathBoolean.Combine(
            new[] { Square(0, 0, 100), Square(0, 0, 100) }, BooleanOp.Subtract);

        Assert.Null(result);
    }

    /// <summary>Union with a congruent shape is that shape: the coincident outlines collapse to one.</summary>
    [Fact]
    public void UnionWithACongruentShapeIsThatShape()
    {
        PathItem? result = PathBoolean.Combine(
            new[] { Square(0, 0, 100), Square(0, 0, 100) }, BooleanOp.Union);

        Assert.NotNull(result);
        Assert.Single(result!.SubPaths);
        Assert.Equal(10000, Area(result), 0);
    }

    /// <summary>A shape entirely inside another: the union is the outer, the intersect is the inner.</summary>
    [Fact]
    public void AContainedShapeUnionsToTheContainerAndIntersectsToTheInner()
    {
        PathItem big = Square(0, 0, 100);
        PathItem small = Square(25, 25, 50);

        Assert.Equal(10000, Area(PathBoolean.Combine(new[] { big, small }, BooleanOp.Union)!), 0);
        Assert.Equal(2500, Area(PathBoolean.Combine(new[] { big, small }, BooleanOp.Intersect)!), 0);
    }

    /// <summary>Curved input: two overlapping circles, checked against the hand-computed lens area.</summary>
    [Fact]
    public void CurvedShapesCombineToTheAreaTheMathematicsSays()
    {
        // Two unit-radius circles whose centres are one radius apart: the lens is
        // 2r^2 acos(d/2r) - (d/2) sqrt(4r^2 - d^2) with d = r.
        const double r = 50;
        double lens = (2 * r * r * Math.Acos(0.5)) - ((r / 2) * Math.Sqrt(4 * r * r - (r * r)));
        double circle = Math.PI * r * r;

        PathItem? intersected = PathBoolean.Combine(
            new[] { Circle(0, 0, r), Circle(r, 0, r) }, BooleanOp.Intersect);

        Assert.NotNull(intersected);
        Assert.True(Math.Abs(Area(intersected!) - lens) < lens * 0.01,
            $"the lens is {Area(intersected!)}, expected about {lens}");

        PathItem? unioned = PathBoolean.Combine(
            new[] { Circle(0, 0, r), Circle(r, 0, r) }, BooleanOp.Union);

        Assert.True(Math.Abs(Area(unioned!) - ((2 * circle) - lens)) < (2 * circle) * 0.01,
            $"the union is {Area(unioned!)}, expected about {(2 * circle) - lens}");
    }

    // ---- the shape, not just the number ----------------------------------------

    /// <summary>
    /// The strong check: across a grid of points, whether the result is filled equals the boolean of
    /// whether the inputs are. Area alone would not catch a result that covers the right amount in the
    /// wrong places.
    /// </summary>
    [Theory]
    [InlineData(BooleanOp.Union)]
    [InlineData(BooleanOp.Subtract)]
    [InlineData(BooleanOp.Intersect)]
    [InlineData(BooleanOp.Exclude)]
    public void TheResultAgreesWithThePredicateEverywhere(BooleanOp op)
    {
        PathItem a = Circle(60, 60, 45);
        PathItem b = Square(40, 40, 80);

        PathItem? result = PathBoolean.Combine(new[] { a, b }, op);

        IReadOnlyList<FlattenedOutline> outlinesOfA = PathFlattener.Flatten(a);
        IReadOnlyList<FlattenedOutline> outlinesOfB = PathFlattener.Flatten(b);

        for (double x = 0; x <= 150; x += 5)
        {
            for (double y = 0; y <= 150; y += 5)
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

                Assert.Equal(expected, Filled(result, point, tolerance: 0.01));
            }
        }
    }

    // ---- the result is a usable object -----------------------------------------

    /// <summary>The result keeps the surviving path's appearance and is filled, with its own contours.</summary>
    [Fact]
    public void TheResultCarriesTheSurvivingPathsAppearance()
    {
        PathItem big = Square(0, 0, 100);
        big.Fill = FillSpec.Solid(new ColorRgb(0.9, 0.1, 0.1));
        big.Name = "the one that survives";

        PathItem? result = PathBoolean.Combine(new[] { big, Square(25, 25, 50) }, BooleanOp.Subtract);

        Assert.NotNull(result);
        Assert.Equal(big.Fill, result!.Fill);
        Assert.Equal("the one that survives", result.Name);
    }

    /// <summary>Somewhere no boolean produces anything: an empty result is null, not an empty path.</summary>
    [Fact]
    public void TwoDisjointShapesDoNotIntersectToAnything()
    {
        Assert.Null(PathBoolean.Combine(
            new[] { Square(0, 0, 100), Square(500, 500, 100) }, BooleanOp.Intersect));
    }

    // ---- divide ----------------------------------------------------------------

    /// <summary>
    /// Two overlapping squares divide into **three** pieces - each square's own part and the overlap -
    /// and together they are the union, which is the check that nothing was lost or counted twice.
    /// </summary>
    [Fact]
    public void TwoOverlappingSquaresDivideIntoThreePieces()
    {
        IReadOnlyList<PathItem> pieces = PathBoolean.Divide(new[] { Square(0, 0, 100), Square(50, 50, 100) });

        Assert.Equal(3, pieces.Count);
        Assert.All(pieces, piece => Assert.Single(piece.SubPaths));

        var areas = pieces.Select(Area).OrderBy(a => a).ToList();
        Assert.Equal(2500, areas[0], 0);   // the overlap
        Assert.Equal(7500, areas[1], 0);   // one square's own part
        Assert.Equal(7500, areas[2], 0);   // the other's

        Assert.Equal(17500, areas.Sum(), 0);
    }

    /// <summary>Shapes that only touch divide into their own pieces and nothing else.</summary>
    [Fact]
    public void DisjointShapesDivideIntoThemselves()
    {
        IReadOnlyList<PathItem> pieces = PathBoolean.Divide(new[] { Square(0, 0, 100), Square(200, 0, 100) });

        Assert.Equal(2, pieces.Count);
        Assert.All(pieces, piece => Assert.Equal(10000, Area(piece), 0));
    }

    /// <summary>
    /// A shape inside another divides into the inner shape and the ring around it - two pieces, not
    /// three, because there is no region that is both inside and outside.
    /// </summary>
    [Fact]
    public void AContainedShapeDividesIntoItAndItsRing()
    {
        IReadOnlyList<PathItem> pieces = PathBoolean.Divide(new[] { Square(0, 0, 100), Square(25, 25, 50) });

        Assert.Equal(2, pieces.Count);

        var areas = pieces.Select(Area).OrderBy(a => a).ToList();
        Assert.Equal(2500, areas[0], 0);
        Assert.Equal(7500, areas[1], 0);
    }

    /// <summary>Divide refuses a selection so large the combination count would explode.</summary>
    [Fact]
    public void DivideRefusesTooManyPaths()
    {
        var many = Enumerable.Range(0, PathBoolean.MaxDivideInputs + 1)
            .Select(i => Square(i * 10, 0, 20))
            .ToArray();

        Assert.Throws<InvalidOperationException>(() => PathBoolean.Divide(many));
    }
}
