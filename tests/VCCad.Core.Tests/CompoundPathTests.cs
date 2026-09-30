using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// What makes several outlines one object with holes: which are holes, which way they wind, and how to
/// take one apart.
///
/// The two fill rules differ for real - two outlines wound the same way are a solid disc under nonzero
/// and a ring under even-odd - so the tests assert the **painted picture** rather than the geometry:
/// whether a point is filled.
/// </summary>
public class CompoundPathTests
{
    /// <summary>A path holding a disc and a concentric circle inside it, wound the same way or not.</summary>
    private static PathItem DiscWithInner(double outer, double inner, bool oppositeWindings)
    {
        PathItem path = PathFactory.CreateEllipse("face", new Point2D(0, 0), outer, outer);
        path.Fill = FillSpec.Solid(ColorRgb.Black);

        PathItem hole = PathFactory.CreateEllipse("eye", new Point2D(0, 0), inner, inner);
        if (oppositeWindings)
        {
            hole.SubPaths[0].Reverse();
        }

        SubPath inner2 = path.AddSubPath(closed: true);
        foreach (PathNode node in hole.SubPaths[0].Nodes)
        {
            inner2.Nodes.Add(new PathNode(node.Anchor, node.InHandle, node.OutHandle));
        }

        path.GeometryChanged();
        return path;
    }

    private static bool FilledAt(PathItem path, Point2D point)
        => PathFlattener.IsFilled(PathFlattener.Flatten(path), FillRule.NonZero, point);

    [Fact]
    public void SeveralClosedOutlinesAreACompoundPath()
    {
        Assert.True(CompoundPaths.IsCompound(DiscWithInner(50, 20, true)));
        Assert.False(CompoundPaths.IsCompound(PathFactory.CreateEllipse("one", new Point2D(0, 0), 50, 50)));

        // An open subpath cannot be a hole.
        PathItem open = PathFactory.CreateEllipse("open", new Point2D(0, 0), 50, 50);
        open.SubPaths[0].IsClosed = false;
        open.AddSubPath(closed: true).Nodes.Add(new PathNode(new Point2D(0, 0)));
        Assert.False(CompoundPaths.IsCompound(open));
    }

    /// <summary>The inner outline is a hole by nesting, whichever way it happens to wind.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheInnerOutlineIsAHoleByNesting(bool oppositeWindings)
    {
        IReadOnlyList<bool> holes = CompoundPaths.Holes(DiscWithInner(50, 20, oppositeWindings));

        Assert.False(holes[0]);
        Assert.True(holes[1]);
    }

    /// <summary>Nested outlines alternate: a ring inside a ring is an island again.</summary>
    [Fact]
    public void NestingAlternates()
    {
        PathItem path = PathFactory.CreateEllipse("rings", new Point2D(0, 0), 50, 50);
        foreach (double radius in new[] { 35.0, 20.0 })
        {
            PathItem ring = PathFactory.CreateEllipse("ring", new Point2D(0, 0), radius, radius);
            SubPath sub = path.AddSubPath(closed: true);
            foreach (PathNode node in ring.SubPaths[0].Nodes)
            {
                sub.Nodes.Add(new PathNode(node.Anchor, node.InHandle, node.OutHandle));
            }
        }

        path.GeometryChanged();

        IReadOnlyList<bool> holes = CompoundPaths.Holes(path);
        Assert.Equal(new[] { false, true, false }, holes);
    }

    /// <summary>
    /// The case that makes normalising worth having: an inner circle wound the **same** way as its
    /// container fills solid under the nonzero rule, and normalising turns it into a hole.
    /// </summary>
    [Fact]
    public void NormalisingTurnsASameWoundInnerOutlineIntoAHole()
    {
        PathItem path = DiscWithInner(50, 20, oppositeWindings: false);

        // Wound the same way: the windings add, so the middle is covered - which is the bug.
        Assert.True(FilledAt(path, new Point2D(0, 0)));

        Assert.True(CompoundPaths.Normalise(path), "normalising should have had something to do");

        // Now the hole is a hole.
        Assert.False(FilledAt(path, new Point2D(0, 0)));
        Assert.True(FilledAt(path, new Point2D(35, 0)));
    }

    /// <summary>Already-correct windings are left alone, so normalising is safe to call anywhere.</summary>
    [Fact]
    public void NormalisingCorrectWindingsChangesNothing()
    {
        PathItem path = DiscWithInner(50, 20, oppositeWindings: true);

        Assert.False(CompoundPaths.Normalise(path));
        Assert.False(FilledAt(path, new Point2D(0, 0)));
    }

    /// <summary>Reversing one outline turns a hole into an island: same geometry, opposite paint.</summary>
    [Fact]
    public void ReversingASubpathTurnsAHoleIntoAnIsland()
    {
        PathItem path = DiscWithInner(50, 20, oppositeWindings: true);
        Assert.False(FilledAt(path, new Point2D(0, 0)));

        Assert.True(CompoundPaths.Reverse(path, 1));

        Assert.True(FilledAt(path, new Point2D(0, 0)));
    }

    /// <summary>Releasing gives one object per outline, with the appearance and the geometry intact.</summary>
    [Fact]
    public void ReleasingACompoundPathGivesItsParts()
    {
        PathItem path = DiscWithInner(50, 20, oppositeWindings: true);
        path.Name = "eye";
        path.Fill = FillSpec.Solid(new ColorRgb(0.2, 0.4, 0.6));

        IReadOnlyList<PathItem> pieces = CompoundPaths.Release(path);

        Assert.Equal(2, pieces.Count);
        Assert.All(pieces, piece => Assert.Single(piece.SubPaths));
        Assert.All(pieces, piece => Assert.Equal("eye", piece.Name));
        Assert.Equal(path.Fill, pieces[0].Fill);

        // The parts are the whole: the disc is still 50 across and the hole 20.
        Rect2D outer = pieces[0].BoundingBox();
        Rect2D inner = pieces[1].BoundingBox();
        Assert.Equal(100, outer.Width, 1);
        Assert.Equal(40, inner.Width, 1);
    }
}
