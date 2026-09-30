using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// Editing one segment of a shape edits all of them.
///
/// The requirement, in the words it was asked for: *"if you make a segment concave while editing a
/// star, then all 10 of the segments should reflect the change appropriately... making the segment
/// inward it should make all the segments bow inward"*. So these measure, per segment, whether the
/// middle of the curve moved - and which way.
/// </summary>
public class ShapeSymmetryTests
{
    private static readonly ShapeParameters Base = new()
    {
        Centre = new Point2D(200, 200),
        Width = 200,
        Height = 200,
        Points = 5,
        InnerRatio = 0.45,
    };

    /// <summary>The point at the middle of a cubic segment, from the nodes that define it.</summary>
    private static Point2D Midpoint(SubPath sub, int segment)
    {
        int count = sub.Nodes.Count;
        PathNode a = sub.Nodes[segment % count];
        PathNode b = sub.Nodes[(segment + 1) % count];

        return new Point2D(
            (a.Anchor.X + (3 * a.OutHandle.X) + (3 * b.InHandle.X) + b.Anchor.X) / 8,
            (a.Anchor.Y + (3 * a.OutHandle.Y) + (3 * b.InHandle.Y) + b.Anchor.Y) / 8);
    }

    private static double DistanceFrom(Point2D point, Point2D centre)
        => Math.Sqrt(Math.Pow(point.X - centre.X, 2) + Math.Pow(point.Y - centre.Y, 2));

    private static Point2D[] Midpoints(PathItem path)
    {
        SubPath sub = path.SubPaths[0];
        return Enumerable.Range(0, sub.SegmentCount).Select(i => Midpoint(sub, i)).ToArray();
    }

    /// <summary>Every equivalent segment of a star moved - all ten of them.</summary>
    [Fact]
    public void BowingOneSegmentOfAStarBowsAllTen()
    {
        PathItem star = ShapeLibrary.Create(ShapeKind.Star, Base);
        SubPath sub = star.SubPaths[0];
        Assert.Equal(10, sub.SegmentCount);

        Point2D[] before = Midpoints(star);
        Assert.True(ShapeSymmetry.Bow(star, 0, -6));
        Point2D[] after = Midpoints(star);

        for (int i = 0; i < 10; i++)
        {
            double moved = DistanceFrom(before[i], after[i]);
            Assert.True(moved > 5.9 && moved < 6.1,
                $"segment {i} moved {moved:0.###} but every segment should bow by 6");
        }
    }

    /// <summary>
    /// Inward on all ten. This is the part that is easy to get wrong: a reflected segment whose winding
    /// runs the other way would bow *outward* if the direction came from the winding rather than from
    /// the shape's centre.
    /// </summary>
    [Fact]
    public void BowingInwardMovesEverySegmentTowardTheCentre()
    {
        PathItem star = ShapeLibrary.Create(ShapeKind.Star, Base);
        Point2D centre = Base.Centre;

        Point2D[] before = Midpoints(star);
        ShapeSymmetry.Bow(star, 0, -6);
        Point2D[] after = Midpoints(star);

        for (int i = 0; i < 10; i++)
        {
            double was = DistanceFrom(before[i], centre);
            double now = DistanceFrom(after[i], centre);
            Assert.True(now < was, $"segment {i} moved away from the centre: {was:0.#} -> {now:0.#}");
        }
    }

    [Fact]
    public void BowingOutwardMovesEverySegmentAwayFromTheCentre()
    {
        PathItem star = ShapeLibrary.Create(ShapeKind.Star, Base);
        Point2D centre = Base.Centre;

        Point2D[] before = Midpoints(star);
        ShapeSymmetry.Bow(star, 3, 6);
        Point2D[] after = Midpoints(star);

        for (int i = 0; i < 10; i++)
        {
            Assert.True(
                DistanceFrom(after[i], centre) > DistanceFrom(before[i], centre),
                $"segment {i} did not bow outward");
        }
    }

    /// <summary>A polygon's sides are all equivalents, so all of them bow.</summary>
    [Fact]
    public void BowingOneSideOfAPolygonBowsAllOfThem()
    {
        PathItem polygon = ShapeLibrary.Create(ShapeKind.Polygon, Base with { Points = 6 });
        Assert.Equal(6, polygon.SubPaths[0].SegmentCount);

        Point2D[] before = Midpoints(polygon);
        ShapeSymmetry.Bow(polygon, 2, -4);
        Point2D[] after = Midpoints(polygon);

        for (int i = 0; i < 6; i++)
        {
            Assert.True(DistanceFrom(before[i], after[i]) > 3.9,
                $"side {i} did not bow");
        }
    }

    /// <summary>
    /// A rectangle's opposite sides bow together, and the other pair is a different orbit.
    ///
    /// This is the symmetry being *real* rather than "everything moves": a rectangle with a belly on
    /// its top and bottom is a barrel, which is symmetric; one with a belly on its top and a waist on
    /// its left is not, and a shape that did that to itself would be wrong. The star is the case where
    /// every segment really is an equivalent of every other.
    /// </summary>
    [Fact]
    public void BowingOneSideOfARectangleBowsItsOppositeAndNotItsNeighbours()
    {
        PathItem rectangle = ShapeLibrary.Create(
            ShapeKind.Rectangle, Base with { Width = 300, Height = 200 });
        Assert.Equal(4, rectangle.SubPaths[0].SegmentCount);

        IReadOnlyList<int> orbit = ShapeSymmetry.Orbit(rectangle, 0);
        Assert.Equal(new[] { 0, 2 }, orbit);

        Point2D[] before = Midpoints(rectangle);
        ShapeSymmetry.Bow(rectangle, 0, -5);
        Point2D[] after = Midpoints(rectangle);

        Assert.True(DistanceFrom(before[0], after[0]) > 4.9, "the side that was bowed did not bow");
        Assert.True(DistanceFrom(before[2], after[2]) > 4.9, "the opposite side did not bow with it");
        Assert.True(DistanceFrom(before[1], after[1]) < 1e-9, "a neighbour should not have moved");
        Assert.True(DistanceFrom(before[3], after[3]) < 1e-9, "a neighbour should not have moved");
    }

    /// <summary>
    /// A rounded rectangle's sides and its corner arcs are different edits, and the symmetry has to
    /// know that: bowing a side must not deform the corners.
    /// </summary>
    [Fact]
    public void ARoundedRectanglesSidesAndCornersAreDifferentOrbits()
    {
        PathItem rounded = ShapeLibrary.Create(
            ShapeKind.RoundedRectangle, Base with { Width = 300, Height = 200, CornerRadius = 20 });
        Assert.Equal(8, rounded.SubPaths[0].SegmentCount);

        // The straight sides and the arcs alternate, so indices of the same parity are the same kind
        // of segment. Whichever parity index 1 is, its orbit is the four of that kind.
        IReadOnlyList<int> orbit = ShapeSymmetry.Orbit(rounded, 1);
        Assert.Equal(2, orbit.Count);
        Assert.All(orbit, index => Assert.Equal(1 % 2, index % 2));

        Point2D[] before = Midpoints(rounded);
        ShapeSymmetry.Bow(rounded, 1, -5);
        Point2D[] after = Midpoints(rounded);

        for (int i = 0; i < 8; i++)
        {
            double moved = DistanceFrom(before[i], after[i]);
            if (orbit.Contains(i))
            {
                Assert.True(moved > 4.9, $"side {i} should have bowed, moved {moved:0.###}");
            }
            else
            {
                Assert.True(moved < 1e-9, $"corner arc {i} should not have moved, moved {moved:0.###}");
            }
        }
    }

    /// <summary>A path that is not a shape has no equivalents: only the segment asked for moves.</summary>
    [Fact]
    public void AnOrdinaryPathBowsOnlyTheSegmentItWasGiven()
    {
        PathItem plain = PathFactory.CreateRectangle("box", new Rect2D(0, 0, 100, 100));

        Assert.Equal(new[] { 0 }, ShapeSymmetry.Orbit(plain, 0));
        Assert.False(ShapeSymmetry.Bow(plain, 0, -5));
        Assert.Equal(1, ShapeSymmetry.RotationOrder(plain));
    }

    /// <summary>Detaching stops the symmetry - the outline is now an ordinary path to edit freely.</summary>
    [Fact]
    public void ADetachedShapeStopsBeingSymmetric()
    {
        PathItem star = ShapeLibrary.Create(ShapeKind.Star, Base);
        star.DetachShape();

        Assert.Single(ShapeSymmetry.Orbit(star, 0));
        Assert.False(ShapeSymmetry.Bow(star, 0, -5));
    }

    /// <summary>
    /// Bowing moves the curve, not the shape: the anchors stay exactly where they were, which is what
    /// keeps the star's points on their radius while its sides bow.
    /// </summary>
    [Fact]
    public void BowingLeavesTheAnchorsAlone()
    {
        PathItem star = ShapeLibrary.Create(ShapeKind.Star, Base);
        Point2D[] anchors = star.SubPaths[0].Nodes.Select(n => n.Anchor).ToArray();

        ShapeSymmetry.Bow(star, 0, -8);

        for (int i = 0; i < anchors.Length; i++)
        {
            Assert.Equal(anchors[i], star.SubPaths[0].Nodes[i].Anchor);
        }
    }

    /// <summary>Bowing announces itself, or a renderer's cached geometry goes stale.</summary>
    [Fact]
    public void BowingBumpsTheGeometryRevision()
    {
        PathItem star = ShapeLibrary.Create(ShapeKind.Star, Base);
        int before = star.GeometryRevision;

        ShapeSymmetry.Bow(star, 0, -5);

        Assert.True(star.GeometryRevision > before);
    }

    /// <summary>The orbit of every star segment is all ten of them, which is the ten that must agree.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(7)]
    public void EverySegmentOfAStarHasTheSameTenEquivalentSegments(int segment)
    {
        PathItem star = ShapeLibrary.Create(ShapeKind.Star, Base);

        IReadOnlyList<int> orbit = ShapeSymmetry.Orbit(star, segment);

        Assert.Equal(10, orbit.Count);
        Assert.Contains(segment, orbit);
    }
}
