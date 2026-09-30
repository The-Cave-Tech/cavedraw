using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// Flattening curves to polygons, and what a set of outlines means once flattened.
///
/// This is the foundation the boolean operations stand on, and the decision recorded in issue #54:
/// exact boolean geometry on Béziers is research-grade, so shapes are flattened finely enough that
/// nobody can see it and the arithmetic afterwards is arithmetic on points. The tolerance is therefore
/// not a detail - every boolean result inherits it - so it is measured here rather than asserted.
/// </summary>
public class PathFlatteningTests
{
    private const double Mm = 72.0 / 25.4;

    private static PathItem Circle(double cx, double cy, double radius, bool clockwise = true)
    {
        PathItem circle = PathFactory.CreateEllipse("circle", new Point2D(cx, cy), radius, radius);
        if (!clockwise)
        {
            circle.SubPaths[0].Reverse();
        }

        return circle;
    }

    /// <summary>
    /// The tolerance is what it claims: every flattened point of a circle is within it of the true
    /// radius. This is the measurement that makes the constant a decision rather than a guess.
    /// </summary>
    [Fact]
    public void AFlattenedCircleStaysWithinTheToleranceOfIt()
    {
        const double radius = 25 * Mm;
        PathItem circle = Circle(0, 0, radius);

        IReadOnlyList<FlattenedOutline> outlines = PathFlattener.Flatten(circle);

        FlattenedOutline outline = Assert.Single(outlines);
        Assert.True(outline.Points.Count > 32,
            $"a circle of this size should be finely flattened, got {outline.Points.Count} points");

        double worst = outline.Points.Max(point =>
            Math.Abs(Math.Sqrt((point.X * point.X) + (point.Y * point.Y)) - radius));

        Assert.True(worst <= PathFlattener.Tolerance,
            $"the worst deviation is {worst / Mm:0.####} mm, tolerance is {PathFlattener.ToleranceMm} mm");
    }

    /// <summary>A finer tolerance is finer, and a coarser one is coarser - the knob works.</summary>
    [Fact]
    public void AFinerToleranceUsesMorePoints()
    {
        PathItem circle = Circle(0, 0, 25 * Mm);

        int fine = PathFlattener.Flatten(circle, tolerance: 0.01 * Mm).Single().Points.Count;
        int coarse = PathFlattener.Flatten(circle, tolerance: 1.0 * Mm).Single().Points.Count;

        Assert.True(fine > coarse, $"fine {fine} should exceed coarse {coarse}");
    }

    /// <summary>A straight-edged shape flattens to its own corners: nothing is invented.</summary>
    [Fact]
    public void ARectangleFlattensToItsFourCorners()
    {
        PathItem rectangle = PathFactory.CreateRectangle("box", new Rect2D(0, 0, 40, 25));

        FlattenedOutline outline = Assert.Single(PathFlattener.Flatten(rectangle));

        Assert.Equal(4, outline.Points.Count);
        Assert.Equal(40 * 25, outline.Area, 6);
    }

    /// <summary>Winding direction is visible in the sign of the area, which is how a hole is told.</summary>
    [Fact]
    public void TheTwoWindingDirectionsHaveOppositeAreas()
    {
        FlattenedOutline clockwise = PathFlattener.Flatten(Circle(0, 0, 10 * Mm, clockwise: true)).Single();
        FlattenedOutline anticlockwise = PathFlattener.Flatten(Circle(0, 0, 10 * Mm, clockwise: false)).Single();

        Assert.NotEqual(Math.Sign(clockwise.SignedArea), Math.Sign(anticlockwise.SignedArea));
        Assert.Equal(clockwise.Area, anticlockwise.Area, 3);
        Assert.True(clockwise.WindsAgainst(anticlockwise));
    }

    /// <summary>
    /// The smiley's arithmetic, in miniature: a disc with a smaller disc missing is an annulus - filled
    /// in the ring, hollow in the middle.
    /// </summary>
    [Fact]
    public void ADiscWithACircleInsideItFillsAsAnAnnulusUnderTheNonZeroRule()
    {
        PathItem face = Circle(0, 0, 30 * Mm, clockwise: true);
        PathItem hole = Circle(0, 0, 10 * Mm, clockwise: false);   // winds against the face

        // The compound path: both outlines in one object, as a subtract would leave them.
        PathItem compound = face;
        SubPath inner = compound.AddSubPath(closed: true);
        foreach (PathNode node in hole.SubPaths[0].Nodes)
        {
            inner.Nodes.Add(new PathNode(node.Anchor, node.InHandle, node.OutHandle));
        }

        IReadOnlyList<FlattenedOutline> outlines = PathFlattener.Flatten(compound);
        Assert.Equal(2, outlines.Count);

        var centre = new Point2D(0, 0);
        var inTheRing = new Point2D(20 * Mm, 0);
        var outside = new Point2D(40 * Mm, 0);

        // Nonzero: the hole winds the other way, so the windings cancel there.
        Assert.False(PathFlattener.IsFilled(outlines, FillRule.NonZero, centre));
        Assert.True(PathFlattener.IsFilled(outlines, FillRule.NonZero, inTheRing));
        Assert.False(PathFlattener.IsFilled(outlines, FillRule.NonZero, outside));

        // Even-odd: nesting depth alone decides, so the same picture needs the same windings here.
        Assert.False(PathFlattener.IsFilled(outlines, FillRule.EvenOdd, centre));
        Assert.True(PathFlattener.IsFilled(outlines, FillRule.EvenOdd, inTheRing));
        Assert.False(PathFlattener.IsFilled(outlines, FillRule.EvenOdd, outside));
    }

    /// <summary>
    /// And the difference between the rules is real: two outlines winding the SAME way are a solid disc
    /// under nonzero and an annulus under even-odd.
    /// </summary>
    [Fact]
    public void TheTwoRulesDisagreeWhenTheOutlinesShareAWinding()
    {
        PathItem disc = Circle(0, 0, 30 * Mm, clockwise: true);
        SubPath inner = disc.AddSubPath(closed: true);
        foreach (PathNode node in Circle(0, 0, 10 * Mm, clockwise: true).SubPaths[0].Nodes)
        {
            inner.Nodes.Add(new PathNode(node.Anchor, node.InHandle, node.OutHandle));
        }

        IReadOnlyList<FlattenedOutline> outlines = PathFlattener.Flatten(disc);
        var centre = new Point2D(0, 0);

        // Same winding: nonzero says the middle is covered, even-odd says it is the second ring.
        Assert.True(PathFlattener.IsFilled(outlines, FillRule.NonZero, centre));
        Assert.False(PathFlattener.IsFilled(outlines, FillRule.EvenOdd, centre));
    }

    /// <summary>Nesting alternates under even-odd, which is what a target's rings do.</summary>
    [Fact]
    public void NestedOutlinesAlternateUnderEvenOdd()
    {
        PathItem target = Circle(0, 0, 30 * Mm);
        foreach (double radius in new[] { 20.0, 10.0 })
        {
            SubPath ring = target.AddSubPath(closed: true);
            foreach (PathNode node in Circle(0, 0, radius * Mm).SubPaths[0].Nodes)
            {
                ring.Nodes.Add(new PathNode(node.Anchor, node.InHandle, node.OutHandle));
            }
        }

        IReadOnlyList<FlattenedOutline> outlines = PathFlattener.Flatten(target);

        // 30 filled, 20 hollow, 10 filled again - by depth, not by winding.
        Assert.True(PathFlattener.IsFilled(outlines, FillRule.EvenOdd, new Point2D(25 * Mm, 0)));
        Assert.False(PathFlattener.IsFilled(outlines, FillRule.EvenOdd, new Point2D(15 * Mm, 0)));
        Assert.True(PathFlattener.IsFilled(outlines, FillRule.EvenOdd, new Point2D(5 * Mm, 0)));
    }
}
