using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// A hatch fills the region an object **paints**, not the box round it.
///
/// The questions here are answered by sampling points rather than by counting segments, because counting
/// proves nothing about shape: a hatch laid over a bounding box would have the same number of segments as one
/// clipped to the outline and would look wrong on the first piece anybody drew.
///
/// Every drawn piece must be inside the region, no piece may pass through a hole, and a shape with a notch
/// must leave the notch empty.
/// </summary>
public class HatchGeneratorTests
{
    private static PathItem Rect(string name, double x, double y, double w, double h, FillRule rule = FillRule.NonZero)
    {
        var path = new PathItem { Name = name, Fill = FillSpec.Solid(ColorRgb.Black, rule) };
        SubPath sub = path.AddSubPath(closed: true);
        sub.Nodes.Add(new PathNode(new Point2D(x, y)));
        sub.Nodes.Add(new PathNode(new Point2D(x + w, y)));
        sub.Nodes.Add(new PathNode(new Point2D(x + w, y + h)));
        sub.Nodes.Add(new PathNode(new Point2D(x, y + h)));
        return path;
    }

    /// <summary>An L: a square with a bite taken out of the top-right, which is concave.</summary>
    private static PathItem Ell()
    {
        var path = new PathItem { Name = "ell", Fill = FillSpec.Solid(ColorRgb.Black) };
        SubPath sub = path.AddSubPath(closed: true);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(100, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(100, 40)));
        sub.Nodes.Add(new PathNode(new Point2D(40, 40)));
        sub.Nodes.Add(new PathNode(new Point2D(40, 100)));
        sub.Nodes.Add(new PathNode(new Point2D(0, 100)));
        return path;
    }

    private static IReadOnlyList<HatchSegment> Hatch(PathItem path, HatchSpec hatch)
    {
        IReadOnlyList<FlattenedOutline> outlines = PathFlattener.Flatten(path);
        return HatchGenerator.Segments(hatch, outlines, path.Fill.Rule, path.BoundingBox());
    }

    private static Point2D Mid(HatchSegment segment)
    {
        // A little inside the piece along its own direction, so a midpoint that lands exactly on an edge for
        // a horizontal or vertical family is measured where the ink actually is.
        var mid = new Point2D((segment.A.X + segment.B.X) / 2, (segment.A.Y + segment.B.Y) / 2);
        double length = Math.Sqrt(
            ((segment.B.X - segment.A.X) * (segment.B.X - segment.A.X))
            + ((segment.B.Y - segment.A.Y) * (segment.B.Y - segment.A.Y)));
        if (length < 1e-6)
        {
            return mid;
        }

        double step = Math.Min(length * 0.25, 0.05);
        return new Point2D(
            mid.X + ((segment.B.X - segment.A.X) / length * step),
            mid.Y + ((segment.B.Y - segment.A.Y) / length * step));
    }

    /// <summary>A rectangle is hatched, and every piece is inside it.</summary>
    [Fact]
    public void ARectangleIsHatched()
    {
        PathItem rect = Rect("rect", 0, 0, 100, 100);
        IReadOnlyList<FlattenedOutline> outlines = PathFlattener.Flatten(rect);

        IReadOnlyList<HatchSegment> segments = Hatch(rect, HatchSpec.Single(0, 10));

        Assert.NotEmpty(segments);
        Assert.All(segments, s => Assert.True(
            Inside(outlines, FillRule.NonZero, s),
            $"a piece at ({s.A.X:0.#},{s.A.Y:0.#}) is outside the rectangle"));
    }

    /// <summary>The spacing is the distance between repetitions, measured along the normal.</summary>
    [Fact]
    public void TheSpacingIsTheDistanceBetweenLines()
    {
        PathItem rect = Rect("rect", 0, 0, 100, 100);
        IReadOnlyList<HatchSegment> segments = Hatch(rect, HatchSpec.Single(0, 25));

        // Horizontal families at 0 degrees: the y values are the repetitions.
        List<double> rows = segments.Select(s => Math.Round(s.A.Y, 6)).Distinct().OrderBy(y => y).ToList();
        Assert.True(rows.Count >= 3, "several repetitions");

        for (int i = 1; i < rows.Count; i++)
        {
            Assert.Equal(25.0, rows[i] - rows[i - 1], 3);
        }
    }

    /// <summary>A concave shape leaves its notch empty - the hatch follows the outline, not the box.</summary>
    [Fact]
    public void AConcaveShapeLeavesTheNotchEmpty()
    {
        PathItem ell = Ell();
        IReadOnlyList<FlattenedOutline> outlines = PathFlattener.Flatten(ell);

        IReadOnlyList<HatchSegment> segments = Hatch(ell, HatchSpec.Cross(9));

        Assert.NotEmpty(segments);
        Assert.All(segments, s => Assert.True(
            Inside(outlines, FillRule.NonZero, s),
            $"a piece at ({s.A.X:0.#},{s.A.Y:0.#}) is in the notch"));

        // The bite taken out of the corner is empty, whatever the family's angle.
        var inNotch = new Point2D(75, 75);
        Assert.False(PathFlattener.IsFilled(outlines, FillRule.NonZero, inNotch));
        Assert.DoesNotContain(segments, s => Near(s, inNotch, 1.0));
    }

    /// <summary>A hole is not hatched, and the ring around it is.</summary>
    [Fact]
    public void AHoleIsNotHatched()
    {
        PathItem ring = Rect("ring", 0, 0, 100, 100, FillRule.EvenOdd);
        ring.SubPaths.Add(Rect("hole", 40, 40, 20, 20).SubPaths[0].Clone());

        IReadOnlyList<FlattenedOutline> outlines = PathFlattener.Flatten(ring);
        IReadOnlyList<HatchSegment> segments = Hatch(ring, HatchSpec.Cross(7));

        Assert.NotEmpty(segments);
        Assert.All(segments, s => Assert.True(
            Inside(outlines, FillRule.EvenOdd, s),
            $"a piece at ({s.A.X:0.#},{s.A.Y:0.#}) is inside the hole"));

        // And the hole itself carries no ink.
        Assert.DoesNotContain(segments, s => Near(s, new Point2D(50, 50), 1.0));

        // While the ring does: a piece crosses the left band.
        Assert.Contains(segments, s => s.A.X < 40 || s.B.X < 40);
    }

    /// <summary>Nothing to draw says so rather than drawing something.</summary>
    [Fact]
    public void AnEmptyOrDegenerateHatchDrawsNothing()
    {
        PathItem rect = Rect("rect", 0, 0, 100, 100);

        Assert.Empty(Hatch(rect, new HatchSpec(Array.Empty<HatchLineSpec>())));
        Assert.Empty(Hatch(rect, HatchSpec.Single(45, 0)));
        Assert.Empty(Hatch(rect, HatchSpec.Single(45, 5, width: 0)));
    }

    /// <summary>
    /// Whether a piece lies in the region, allowing for one that runs along its boundary.
    ///
    /// A hatch line can legitimately sit exactly on an edge - the last repetition of a family often does - and a
    /// point on the outline is not reliably "inside" by a fill test. Stepping a hair to either side of the piece
    /// and asking whether either side is inside is the honest question for a stroke: the ink is on the region
    /// or on its edge, never outside it.
    /// </summary>
    private static bool Inside(IReadOnlyList<FlattenedOutline> outlines, FillRule rule, HatchSegment segment)
    {
        Point2D mid = Mid(segment);
        double dx = segment.B.X - segment.A.X;
        double dy = segment.B.Y - segment.A.Y;
        double length = Math.Sqrt((dx * dx) + (dy * dy));
        if (length < 1e-9)
        {
            return PathFlattener.IsFilled(outlines, rule, mid);
        }

        var normal = new Vector2D(-dy / length, dx / length);
        foreach (double step in new[] { 0.0, 0.05, -0.05 })
        {
            if (PathFlattener.IsFilled(outlines, rule, mid + (normal * step)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether a segment passes within a distance of a point.</summary>
    private static bool Near(HatchSegment segment, Point2D point, double tolerance)
    {
        double dx = segment.B.X - segment.A.X;
        double dy = segment.B.Y - segment.A.Y;
        double lengthSquared = (dx * dx) + (dy * dy);
        if (lengthSquared < 1e-12)
        {
            return Math.Sqrt(
                ((point.X - segment.A.X) * (point.X - segment.A.X))
                + ((point.Y - segment.A.Y) * (point.Y - segment.A.Y))) <= tolerance;
        }

        double t = Math.Clamp(
            (((point.X - segment.A.X) * dx) + ((point.Y - segment.A.Y) * dy)) / lengthSquared, 0, 1);
        double px = segment.A.X + (t * dx);
        double py = segment.A.Y + (t * dy);
        return Math.Sqrt(((point.X - px) * (point.X - px)) + ((point.Y - py) * (point.Y - py))) <= tolerance;
    }
}
