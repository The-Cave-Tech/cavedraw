using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// An art brush maps its asset **along** the path - repeated or stretched - with the art turned to the tangent
/// where each piece of it sits (issue #100).
///
/// The assertions are on the geometry, not on the parameters: a placement is the affine transform that carries
/// the asset's own box onto the path, so what is measured here is where the art actually lands, which way it
/// points and how big it comes out. The two things that decide an art brush rather than a nib are both pinned:
/// the size comes from the **brush**, not from the stroke's width, and the placements are recomputed from the
/// path every time they are asked for, so editing the path moves the art without the brush being re-applied.
///
/// The asset's own frame is fixed by these tests as **+X across the path and +Y along it**, which is the
/// convention <see cref="ArtBrushPath"/> documents: the art's width is the brush's size, and its height is what
/// tiles or stretches along the path.
/// </summary>
public class ArtBrushAlongPathTests
{
    /// <summary>A 10 by 10 asset whose box starts at the origin, which is what an item's own bounds are.</summary>
    private static readonly Rect2D Asset = new(0, 0, 10, 10);

    private static PathItem Open(params Point2D[] points)
    {
        var path = new PathItem { Name = "line", Fill = FillSpec.None };
        SubPath sub = path.AddSubPath(closed: false);
        foreach (Point2D point in points)
        {
            sub.Nodes.Add(new PathNode(point));
        }

        path.Stroke = new StrokeSpec(true, ColorRgb.Black, 4, StrokeCap.Butt, StrokeJoin.Miter, 4);
        return path;
    }

    private static BrushSpec Art(double size = 20, ArtStretch stretch = ArtStretch.Repeat,
        bool flipAcross = false, bool flipAlong = false)
        => BrushSpec.Art("Tile", Guid.NewGuid(), size, stretch, flipAcross, flipAlong);

    /// <summary>The asset's box as the placement puts it on the path, so a test measures the art and not the numbers.</summary>
    private static Rect2D Placed(ArtBrushPlacement placement, Rect2D? asset = null)
    {
        Rect2D box = asset ?? Asset;
        return Rect2D.FromPoints(
            placement.Transform.Transform(new Point2D(box.X, box.Y)),
            placement.Transform.Transform(new Point2D(box.Right, box.Bottom)));
    }

    /// <summary>
    /// Moves one node of an open path, handles and all, which is what dragging a point does: moving the anchor
    /// alone would leave the handles where they were and bow the segment into a curve.
    /// </summary>
    private static void Move(PathItem path, int index, Point2D to)
    {
        PathNode node = path.SubPaths[0].Nodes[index];
        node.Anchor = to;
        node.InHandle = to;
        node.OutHandle = to;
    }

    /// <summary>Where one point of the asset's own frame ends up, which is how a direction is measured.</summary>
    private static Point2D On(ArtBrushPlacement placement, double x, double y)
        => placement.Transform.Transform(new Point2D(x, y));

    [Fact]
    public void AnArtBrushRepeatsItsAssetAlongThePath()
    {
        PathItem path = Open(new Point2D(0, 0), new Point2D(100, 0));

        IReadOnlyList<ArtBrushPlacement> placements = ArtBrushPath.Placements(path, Art(), Asset);

        // A 10pt-tall asset scaled to 20 across is 20 along as well, so a 100pt path holds five of them.
        Assert.Equal(5, placements.Count);
        Assert.Equal(new[] { 0.0, 20.0, 40.0, 60.0, 80.0 }, placements.Select(p => p.Position).ToArray());
        Assert.All(placements, p => Assert.Equal(20.0, p.Length, 6));

        // Every repeat covers the same 20pt band across the path, centred on it - the brush's size, not the
        // stroke's own 4pt width.
        foreach (ArtBrushPlacement placement in placements)
        {
            Rect2D box = Placed(placement);
            Assert.Equal(20.0, box.Width, 6);
            Assert.Equal(20.0, box.Height, 6);
            Assert.Equal(-10.0, box.Y, 6);
            Assert.Equal(10.0, box.Bottom, 6);
        }

        Assert.Equal(0.0, Placed(placements[0]).X, 6);
        Assert.Equal(80.0, Placed(placements[4]).X, 6);
    }

    /// <summary>
    /// **The tangent test.** The art is turned to the direction the path is travelling where it is placed: on the
    /// horizontal leg local +Y runs to the right, and on the vertical leg it runs downwards, which is that
    /// segment's own direction.
    /// </summary>
    [Fact]
    public void EachPlacementTurnsTheArtToTheTangentThere()
    {
        PathItem path = Open(new Point2D(0, 0), new Point2D(30, 0), new Point2D(30, 40));

        IReadOnlyList<ArtBrushPlacement> placements = ArtBrushPath.Placements(path, Art(), Asset);

        Assert.Equal(4, placements.Count);
        Assert.Equal(new[] { 0.0, 20.0, 40.0, 60.0 }, placements.Select(p => p.Position).ToArray());

        // The two on the horizontal leg point along +X; the two on the vertical leg along +Y.
        Assert.Equal(new[] { 0.0, 0.0 }, placements.Take(2).Select(p => p.TangentRadians).ToArray());

        // The art's own +Y axis is the path's direction of travel wherever it is placed, and its +X axis runs
        // across the path - to the left of travel - where it is the brush's size, 20, wide.
        Vector2D along0 = On(placements[0], 5, 10) - On(placements[0], 5, 0);
        Assert.Equal(20.0, along0.X, 6);
        Assert.Equal(0.0, along0.Y, 6);

        Vector2D across0 = On(placements[0], 10, 5) - On(placements[0], 0, 5);
        Assert.Equal(0.0, across0.X, 6);
        Assert.Equal(-20.0, across0.Y, 6);

        Assert.Equal(new Point2D(30, 10), placements[2].Point);

        Vector2D along2 = On(placements[2], 5, 10) - On(placements[2], 5, 0);
        Assert.Equal(0.0, along2.X, 6);
        Assert.Equal(20.0, along2.Y, 6);

        Vector2D across2 = On(placements[2], 10, 5) - On(placements[2], 0, 5);
        Assert.Equal(20.0, across2.X, 6);
        Assert.Equal(0.0, across2.Y, 6);
    }

    /// <summary>
    /// The two axes mean two different things, so a **non-square** asset says which is which: its width becomes
    /// the brush's size across the path and its height runs along it, at the proportional scale.
    /// </summary>
    [Fact]
    public void TheAssetsWidthBecomesTheBrushSizeAndItsHeightRunsAlongThePath()
    {
        PathItem path = Open(new Point2D(0, 0), new Point2D(80, 0));
        var asset = new Rect2D(0, 0, 8, 4);

        IReadOnlyList<ArtBrushPlacement> placements = ArtBrushPath.Placements(path, Art(size: 16), asset);

        // 16 across an 8-wide asset is a scale of 2, so the 4-tall asset is 8 long and an 80pt path holds ten.
        Assert.Equal(10, placements.Count);
        Assert.All(placements, p => Assert.Equal(8.0, p.Length, 6));

        Rect2D box = Placed(placements[0], asset);
        Assert.Equal(16.0, box.Height, 6);
        Assert.Equal(8.0, box.Width, 6);
    }

    /// <summary>A diagonal path, so the tangent is something other than a right angle.</summary>
    [Fact]
    public void ADiagonalPathPlacesTheArtAlongItsOwnDirection()
    {
        PathItem path = Open(new Point2D(10, 20), new Point2D(40, 60));

        IReadOnlyList<ArtBrushPlacement> placements =
            ArtBrushPath.Placements(path, Art(stretch: ArtStretch.StretchToFit), Asset);

        ArtBrushPlacement placement = Assert.Single(placements);
        Assert.Equal(Math.Atan2(40, 30), placement.TangentRadians, 9);

        // The asset's +Y axis follows the segment, and its length is the path's own length.
        Vector2D along = On(placement, 5, 10) - On(placement, 5, 0);
        Assert.Equal(0.6, along.X / 50.0, 6);
        Assert.Equal(0.8, along.Y / 50.0, 6);
        Assert.Equal(50.0, Math.Sqrt((along.X * along.X) + (along.Y * along.Y)), 6);
    }

    /// <summary>
    /// **The size test.** The art is scaled by the brush's size and not by the stroke's width: the same brush on
    /// a hairline and on a fat stroke puts the same art in the same places.
    /// </summary>
    [Fact]
    public void TheArtIsScaledByTheBrushSizeAndNotByTheStrokeWidth()
    {
        PathItem path = Open(new Point2D(0, 0), new Point2D(100, 0));
        BrushSpec brush = Art(size: 20);

        IReadOnlyList<ArtBrushPlacement> thin =
            ArtBrushPath.Placements(path, brush, Asset);
        path.Stroke = path.Stroke with { Width = 400 };
        IReadOnlyList<ArtBrushPlacement> fat =
            ArtBrushPath.Placements(path, brush, Asset);

        Assert.Equal(thin.Select(p => p.Transform), fat.Select(p => p.Transform));
        Assert.Equal(20.0, Placed(thin[0]).Height, 6);
        Assert.Equal(20.0, Placed(fat[0]).Height, 6);
    }

    /// <summary>
    /// **The test the issue asks for by name: changing the path changes the art, and the brush is not re-applied.**
    /// The placements are computed from the path every time, so the brush on the stroke is untouched by the edit -
    /// which is what makes this a stroke property rather than a copy of the art pasted along the line.
    /// </summary>
    [Fact]
    public void EditingThePathMovesTheArtWithoutReApplyingTheBrush()
    {
        PathItem path = Open(new Point2D(0, 0), new Point2D(100, 0));
        BrushSpec brush = Art();
        path.Stroke = path.Stroke with { Brush = brush };

        IReadOnlyList<ArtBrushPlacement> before = ArtBrushPath.Placements(path, brush, Asset);
        Assert.Equal(5, before.Count);
        Assert.Equal(0.0, before[0].TangentRadians, 9);

        // The person drags the far end of the line up and back, and nothing else happens: no re-apply, no
        // re-create, no copy of the art left lying along the old line.
        Move(path, 1, new Point2D(60, 40));
        Assert.Equal(brush, path.Stroke.Brush);

        IReadOnlyList<ArtBrushPlacement> after = ArtBrushPath.Placements(path, path.Stroke.Brush!, Asset);

        // A 72.11pt diagonal holds four 20pt repeats rather than five, and every one of them has both moved and
        // turned - which is the art following the path the person actually drew.
        Assert.Equal(4, after.Count);
        Assert.Equal(Math.Atan2(40, 60), after[0].TangentRadians, 9);
        Assert.NotEqual(before[0].Transform, after[0].Transform);
        Assert.NotEqual(before[^1].Point, after[^1].Point);
        Assert.NotEqual(0.0, after[^1].Point.Y, 6);
    }

    [Fact]
    public void StretchToFitSpansTheWholePathOnceAndRepeatDoesNot()
    {
        PathItem path = Open(new Point2D(0, 0), new Point2D(90, 0));

        ArtBrushPlacement fit = Assert.Single(
            ArtBrushPath.Placements(path, Art(stretch: ArtStretch.StretchToFit), Asset));

        Assert.Equal(0.0, fit.Position, 6);
        Assert.Equal(90.0, fit.Length, 6);

        // Along the path the fit is the whole 90; across it, it is the brush's own 20 and nothing more.
        Rect2D fitted = Placed(fit);
        Assert.Equal(90.0, fitted.Width, 6);
        Assert.Equal(20.0, fitted.Height, 6);

        // Repeat is the other answer: whole assets of their natural size, and the last one runs off the end
        // rather than being squashed to fit it.
        IReadOnlyList<ArtBrushPlacement> repeated =
            ArtBrushPath.Placements(path, Art(stretch: ArtStretch.Repeat), Asset);

        Assert.Equal(5, repeated.Count);
        Assert.All(repeated, p => Assert.Equal(20.0, p.Length, 6));
        Assert.Equal(100.0, repeated[^1].Position + repeated[^1].Length, 6);
    }

    /// <summary>Scale proportionally keeps the asset's aspect and distorts nothing, so it does not span the path.</summary>
    [Fact]
    public void ScaleProportionallyKeepsTheAssetsAspect()
    {
        PathItem path = Open(new Point2D(0, 0), new Point2D(90, 0));

        ArtBrushPlacement placement = Assert.Single(
            ArtBrushPath.Placements(path, Art(stretch: ArtStretch.ScaleProportionally), Asset));

        Rect2D box = Placed(placement);
        Assert.Equal(20.0, box.Width, 6);
        Assert.Equal(20.0, box.Height, 6);
        Assert.Equal(0.0, placement.Position, 6);
        Assert.Equal(20.0, placement.Length, 6);
    }

    /// <summary>
    /// The two flips mirror the art about the path in the two directions Illustrator's toggles name: one across
    /// the stroke, one along it.
    /// </summary>
    [Fact]
    public void TheFlipsMirrorTheArtAboutThePath()
    {
        PathItem path = Open(new Point2D(0, 0), new Point2D(100, 0));

        ArtBrushPlacement plain = ArtBrushPath.Placements(path, Art(), Asset)[0];
        ArtBrushPlacement across = ArtBrushPath.Placements(path, Art(flipAcross: true), Asset)[0];
        ArtBrushPlacement along = ArtBrushPath.Placements(path, Art(flipAlong: true), Asset)[0];

        // Across: the asset's own left edge swaps sides of the path.
        Assert.Equal(10.0, On(plain, 0, 5).Y, 6);
        Assert.Equal(-10.0, On(across, 0, 5).Y, 6);

        // Along: the asset's own top edge swaps ends of its piece - its +Y runs with the path, and against it.
        Assert.Equal(20.0, (On(plain, 5, 10) - On(plain, 5, 0)).X, 6);
        Assert.Equal(-20.0, (On(along, 5, 10) - On(along, 5, 0)).X, 6);
    }

    /// <summary>
    /// Nothing to place, nothing placed: an asset with no extent, a brush with no size, and a path with no
    /// segment each answer with no placements rather than with an invented one.
    /// </summary>
    [Fact]
    public void NothingToPlacePlacesNothing()
    {
        PathItem path = Open(new Point2D(0, 0), new Point2D(100, 0));

        Assert.Empty(ArtBrushPath.Placements(path, Art(), Rect2D.Empty));
        Assert.Empty(ArtBrushPath.Placements(path, Art(size: 0), Asset));
        Assert.Empty(ArtBrushPath.Placements(Open(new Point2D(5, 5)), Art(), Asset));
    }

    /// <summary>A nib is left alone by the art seam: it is the other kind, and it draws the stroke, not art.</summary>
    [Fact]
    public void ACalligraphicBrushPlacesNoArt()
    {
        PathItem path = Open(new Point2D(0, 0), new Point2D(100, 0));

        Assert.Empty(ArtBrushPath.Placements(path, BrushSpec.Calligraphic("Chisel", 35, 0.2, 24), Asset));
    }

    /// <summary>
    /// **An art brush is not a nib, so it does not go through the width seam.** The stroke's own width still
    /// answers "how wide is the stroke here" - an art brush has no half-width to give, and reading its diameter as
    /// one would draw a 20pt nib where the file has art.
    /// </summary>
    [Fact]
    public void AnArtBrushDoesNotAnswerTheWidthSeam()
    {
        PathItem path = Open(new Point2D(0, 0), new Point2D(100, 0));
        path.Stroke = path.Stroke with { Brush = Art(size: 20) };

        StrokeRenderPlan plan = StrokeOutlineBuilder.Plan(path, path.Stroke);

        Assert.True(plan.IsOutline);
        IReadOnlyList<Point2D> outline = Assert.Single(plan.Outlines);
        Assert.Equal(4.0, outline.Max(p => p.Y) - outline.Min(p => p.Y), 6);
    }

    /// <summary>The renderer's own scale reaches the art the way it reaches a nib, so a scaled group draws the same picture.</summary>
    [Fact]
    public void TheRenderersScaleReachesTheArt()
    {
        PathItem path = Open(new Point2D(0, 0), new Point2D(100, 0));

        ArtBrushPlacement plain = ArtBrushPath.Placements(path, Art(), Asset)[0];
        ArtBrushPlacement doubled = ArtBrushPath.Placements(path, Art(), Asset, scale: 2.0)[0];

        Assert.Equal(20.0, Placed(plain).Height, 6);
        Assert.Equal(40.0, Placed(doubled).Height, 6);
        Assert.Equal(40.0, doubled.Length, 6);
    }
}
