using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// Width profiles: a stroke can vary in width along its length, and independently on each side.
///
/// The assertions are on **geometry** - where the edges end up - rather than on "a profile was set". A profile
/// that is stored, round-trips and is never read is the failure this is written to catch, and the only way to
/// catch it is to look at the outline.
/// </summary>
public class WidthProfileTests
{
    private static PathItem Line(bool closed = false, params Point2D[] points)
    {
        var path = new PathItem { Name = "line", Fill = FillSpec.None };
        SubPath sub = path.AddSubPath(closed);
        foreach (Point2D point in points)
        {
            sub.Nodes.Add(new PathNode(point));
        }

        return path;
    }

    // ---------------------------------------------------------------- the profile itself

    [Fact]
    public void PointsAreOrderedHoweverTheyAreGiven()
    {
        var profile = new WidthProfileSpec("Odd", new[]
        {
            WidthPoint.Even(1.0, 3),
            WidthPoint.Even(0.0, 9),
            WidthPoint.Even(0.5, 6),
        });

        Assert.Equal(new[] { 0.0, 0.5, 1.0 }, profile.Points.Select(p => p.Position).ToArray());
    }

    [Fact]
    public void AConstantProfileIsTheSameWidthEverywhere()
    {
        WidthProfileSpec profile = WidthProfileSpec.Constant(12);

        foreach (double t in new[] { 0.0, 0.13, 0.5, 0.87, 1.0 })
        {
            Assert.Equal(6.0, profile.HalvesAt(t)!.Value.Left, 6);
            Assert.Equal(6.0, profile.HalvesAt(t)!.Value.Right, 6);
        }
    }

    [Fact]
    public void ALinearTaperInterpolatesStraight()
    {
        var profile = new WidthProfileSpec("Taper", new[]
        {
            WidthPoint.Even(0.0, 16),
            WidthPoint.Even(1.0, 0),
        });

        // A quarter of the way along a straight taper is three quarters of the width.
        Assert.Equal(6.0, profile.HalvesAt(0.25)!.Value.Left, 6);
        Assert.Equal(4.0, profile.HalvesAt(0.5)!.Value.Left, 6);
    }

    /// <summary>
    /// A cubic taper eases at both ends, which is what makes it read as drawn rather than as a cone. At a
    /// quarter of the way it must be **narrower** than the straight line between the same two widths - that is
    /// the whole difference, and a test that only checked the midpoint would not see it, because 0.5 of a
    /// smoothstep is 0.5.
    /// </summary>
    [Fact]
    public void ACubicTaperEasesAtBothEnds()
    {
        var profile = new WidthProfileSpec("Eased", new[]
        {
            WidthPoint.Even(0.0, 16),
            WidthPoint.Even(1.0, 0, WidthInterpolation.Cubic),
        });

        double quarter = profile.HalvesAt(0.25)!.Value.Left;
        Assert.True(quarter > 6.0, $"a straight taper gives 6 a quarter along, and eased holds more: {quarter}");

        // Smoothstep at a quarter is 0.15625, so the half-width is 8 - 8*0.15625.
        Assert.Equal(6.75, quarter, 6);
    }

    [Fact]
    public void PositionsOutsideThePathAreClamped()
    {
        var profile = new WidthProfileSpec("Taper", new[]
        {
            WidthPoint.Even(0.0, 10),
            WidthPoint.Even(1.0, 2),
        });

        Assert.Equal(5.0, profile.HalvesAt(-4)!.Value.Left, 6);
        Assert.Equal(1.0, profile.HalvesAt(9)!.Value.Left, 6);
    }

    /// <summary>An empty profile has no opinion, which is not the same as a width of zero.</summary>
    [Fact]
    public void AnEmptyProfileReportsNothing()
    {
        var profile = new WidthProfileSpec("Empty", Array.Empty<WidthPoint>());

        Assert.Null(profile.HalvesAt(0.5));
        Assert.True(profile.IsEmpty);
    }

    // ---------------------------------------------------------------- the outline

    /// <summary>
    /// A straight path with a constant profile is a band of that width: half of it above the centreline and half
    /// below. This is the case where the answer is known by hand, which is why it is the first geometry test.
    /// </summary>
    [Fact]
    public void AStraightPathIsOffsetByHalfTheWidthOnEachSide()
    {
        PathItem path = Line(closed: false, new Point2D(0, 0), new Point2D(100, 0));

        IReadOnlyList<IReadOnlyList<Point2D>> outline =
            PathOffset.Outline(PathFlattener.FlattenForStroke(path), WidthProfileSpec.Constant(10), fallbackWidth: 10);

        Assert.Single(outline);
        Assert.Contains(outline[0], p => Math.Abs(p.Y - (-5)) < 1e-6);
        Assert.Contains(outline[0], p => Math.Abs(p.Y - 5) < 1e-6);
        Assert.All(outline[0], p => Assert.InRange(p.X, -1e-6, 100 + 1e-6));
    }

    /// <summary>
    /// The two sides take their own widths, which is what makes a profile a drawn line rather than a fattened
    /// one. Left is to the left of travel: on a path drawn to the right, that is upward.
    /// </summary>
    [Fact]
    public void EachSideUsesItsOwnWidth()
    {
        PathItem path = Line(closed: false, new Point2D(0, 0), new Point2D(100, 0));
        var profile = new WidthProfileSpec("One-sided", new[]
        {
            new WidthPoint(0.0, LeftWidth: 2, RightWidth: 10),
            new WidthPoint(1.0, LeftWidth: 2, RightWidth: 10),
        });

        IReadOnlyList<IReadOnlyList<Point2D>> outline =
            PathOffset.Outline(PathFlattener.FlattenForStroke(path), profile, fallbackWidth: 10);

        // Left 2 wide reaches 1 up, right 10 wide reaches 5 down.
        Assert.Contains(outline[0], p => Math.Abs(p.Y - (-1)) < 1e-6);
        Assert.Contains(outline[0], p => Math.Abs(p.Y - 5) < 1e-6);
        Assert.DoesNotContain(outline[0], p => Math.Abs(p.Y - (-5)) < 1e-6);
    }

    /// <summary>
    /// **An open path is measured along its own length.** The closing segment of a polygon is not part of an
    /// open polyline, and counting it would double the length a profile is measured against - so a taper that
    /// should reach zero at the far end would stop at half the width half way along. This is the test for that,
    /// and it fails if the flattener's closure is ignored.
    /// </summary>
    [Fact]
    public void AnOpenPathUsesItsWholeLengthForTheProfile()
    {
        PathItem path = Line(closed: false, new Point2D(0, 0), new Point2D(100, 0));
        WidthProfileSpec profile = WidthProfileSpec.Taper(20, 0);

        IReadOnlyList<Point2D> outline =
            PathOffset.Outline(PathFlattener.FlattenForStroke(path), profile, fallbackWidth: 20)[0];

        // At the far end the taper has reached zero, so both edges meet the centreline.
        double widestAtFarEnd = outline.Where(p => Math.Abs(p.X - 100) < 1e-6).Max(p => Math.Abs(p.Y));
        Assert.True(widestAtFarEnd < 0.01, $"the taper should have closed by x=100, not still be {widestAtFarEnd}");

        // And at the start it is the full 20 wide: 10 either side.
        double widestAtStart = outline.Where(p => Math.Abs(p.X) < 1e-6).Max(p => Math.Abs(p.Y));
        Assert.Equal(10.0, widestAtStart, 3);
    }

    /// <summary>A closed path does include its closing segment, so its profile runs all the way round.</summary>
    [Fact]
    public void AClosedPathMeasuresItsClosingSegmentToo()
    {
        PathItem square = Line(
            closed: true,
            new Point2D(0, 0), new Point2D(100, 0), new Point2D(100, 100), new Point2D(0, 100));

        IReadOnlyList<Point2D> outline =
            PathOffset.Outline(PathFlattener.FlattenForStroke(square), WidthProfileSpec.Constant(8), fallbackWidth: 8)[0];

        // A band round the square: every corner is pushed out or in by four, and the loop is twice the points.
        Assert.Equal(8, outline.Count);
        Assert.Contains(outline, p => Math.Abs(p.X - (-4)) < 1e-6);
        Assert.Contains(outline, p => Math.Abs(p.X - 104) < 1e-6);
        Assert.Contains(outline, p => Math.Abs(p.Y - (-4)) < 1e-6);
        Assert.Contains(outline, p => Math.Abs(p.Y - 104) < 1e-6);
    }

    /// <summary>An empty profile leaves the stroke as it was, rather than erasing it.</summary>
    [Fact]
    public void AnEmptyProfileFallsBackToTheStrokeWidth()
    {
        PathItem path = Line(closed: false, new Point2D(0, 0), new Point2D(100, 0));

        IReadOnlyList<Point2D> outline = PathOffset.Outline(
            PathFlattener.FlattenForStroke(path),
            new WidthProfileSpec("Empty", Array.Empty<WidthPoint>()),
            fallbackWidth: 6)[0];

        Assert.Contains(outline, p => Math.Abs(p.Y - (-3)) < 1e-6);
        Assert.Contains(outline, p => Math.Abs(p.Y - 3) < 1e-6);
    }
}
