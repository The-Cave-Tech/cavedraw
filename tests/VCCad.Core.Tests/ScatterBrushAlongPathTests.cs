using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// A scatter brush lays an asset repeatedly along the path, each copy drawn from a range around the value the brush
/// was set to, so a scatter looks hand-placed rather than machined (issue #102).
///
/// The assertions are on the geometry and on the numbers each copy is drawn with, not on the parameters: a copy is
/// a document item's own box placed by a transform, so a test measures where the artwork lands - the same
/// relationship the art brush has with its asset. Three things are pinned that a parameter round trip could not see:
///
/// - **The randomness is a range, and it is really applied.** Every copy's rotation, scale and offset is asserted
///   inside the range the brush states, and then the copies are asserted to **differ** from one another. Range
///   alone would pass for an implementation that ignored the randomness and drew the fixed value every time, which
///   is the defect the whole feature exists to avoid.
/// - **Reproducibility.** The copies are a pure function of the path and the parameters through a stable sequence,
///   so two renders of one document agree exactly. That is asserted twice, and it is why the sequence is not a
///   fresh <see cref="Random"/> - which would make every frame a different picture.
/// - **Zero randomness reproduces the fixed placement exactly.** A range of zero is not "a very small range": it
///   is the stated value, to the last bit, which is what lets a caller pin a scatter down.
/// </summary>
public class ScatterBrushAlongPathTests
{
    /// <summary>A ten by ten copy whose box starts at the origin, which is what an item's own bounds are.</summary>
    private static readonly Rect2D TenByTen = new(0, 0, 10, 10);

    private static readonly Guid Copy = Guid.Parse("55555555-5555-5555-5555-555555555555");

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

    private static Rect2D? Bounds(Guid id) => id == Copy ? TenByTen : null;

    private static IReadOnlyList<ScatterBrushPlacement> Place(
        PathItem path, BrushSpec brush, double scale = 1.0, double pressure = 1.0)
        => ScatterBrushPath.Placements(path, brush, Bounds, scale, PenProfile.Constant(pressure));

    /// <summary>
    /// A scatter brush with every axis stated; an axis left out keeps the model's own default. The size is 20 across
    /// a ten-wide copy, so one copy is drawn 20 across and 20 along at a scale of 1.
    /// </summary>
    private static BrushSpec Spray(
        ScatterParameter? spacing = null,
        ScatterParameter? rotation = null,
        ScatterParameter? scale = null,
        ScatterParameter? offset = null,
        ScatterParameter? opacity = null,
        double size = 20.0,
        Guid? asset = null,
        DynamicsSpec? dynamics = null)
        => BrushSpec.Scatter(
            "Spray", asset ?? Copy, size, spacing, rotation, scale, offset, opacity, dynamics);

    /// <summary>The centre of the copy's own box as the placement puts it on the path.</summary>
    private static Point2D Centre(ScatterBrushPlacement placement)
        => placement.Transform.Transform(new Point2D(TenByTen.X + (TenByTen.Width / 2.0), TenByTen.Y + (TenByTen.Height / 2.0)));

    /// <summary>The direction the copy's own +X axis is pointing, in degrees, after the placement turned it.</summary>
    private static double AcrossDegrees(ScatterBrushPlacement placement)
    {
        Point2D origin = placement.Transform.Transform(new Point2D(0, 0));
        Vector2D along = placement.Transform.Transform(new Point2D(1, 0)) - origin;
        return Math.Atan2(along.Y, along.X) * 180.0 / Math.PI;
    }

    // ---------------------------------------------------------------------------------------------------------
    // 1. The count and the pitch.
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// **The placement count on a known path length at a known spacing.** A 200pt path with a 50pt pitch holds four
    /// copies, at 0, 50, 100 and 150: the fifth would start at 200, which is the end of the path and not on it.
    /// </summary>
    [Fact]
    public void CopiesAreLaidAtTheStatedSpacingAlongAStraightPath()
    {
        IReadOnlyList<ScatterBrushPlacement> copies = Place(
            Open(new Point2D(0, 0), new Point2D(200, 0)), Spray(spacing: new ScatterParameter(50)));

        Assert.Equal(4, copies.Count);
        Assert.Equal(new[] { 0.0, 50.0, 100.0, 150.0 }, copies.Select(c => c.Position).ToArray());
        Assert.All(copies, c => Assert.Equal(1.0, c.Scale, 9));
        Assert.All(copies, c => Assert.Equal(0.0, c.RotationDegrees, 9));
        Assert.All(copies, c => Assert.Equal(0.0, c.Offset, 9));

        // The copy is 20 across a ten-wide box, so a scale of 1 draws it 20 across the path - measured, not assumed.
        Assert.All(copies, c => Assert.Equal(20.0, (c.Transform.Transform(new Point2D(10, 5)) - c.Transform.Transform(new Point2D(0, 5))).Length, 6));
    }

    /// <summary>A spacing of zero or less is the model's own default and lays the copies end to end at their own size.</summary>
    [Fact]
    public void ADefaultSpacingLaysTheCopiesEndToEnd()
    {
        IReadOnlyList<ScatterBrushPlacement> copies = Place(
            Open(new Point2D(0, 0), new Point2D(100, 0)), Spray());

        // 20 along per copy over a 100pt path is five copies, not one and not a hundred.
        Assert.Equal(5, copies.Count);
        Assert.Equal(new[] { 0.0, 20.0, 40.0, 60.0, 80.0 }, copies.Select(c => c.Position).ToArray());
    }

    // ---------------------------------------------------------------------------------------------------------
    // 2. Each parameter is applied, not merely held.
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// **The rotation is the copy's own on top of the turn the path gives it.** The path runs along +X, so the
    /// path's frame turns a copy's +X to -90 degrees; thirty degrees of its own makes that -60.
    /// </summary>
    [Fact]
    public void ACopyIsTurnedByItsOwnRotationOnTopOfTheTangent()
    {
        IReadOnlyList<ScatterBrushPlacement> copies = Place(
            Open(new Point2D(0, 0), new Point2D(100, 0)),
            Spray(spacing: new ScatterParameter(100), rotation: new ScatterParameter(30)));

        ScatterBrushPlacement copy = Assert.Single(copies);
        Assert.Equal(-60.0, AcrossDegrees(copy), 6);
        Assert.Equal(30.0, copy.RotationDegrees, 9);
    }

    /// <summary>
    /// **The offset moves a copy across the path, to the left of travel.** Travelling along +X in this Y-down space,
    /// left is -Y, so a +5 offset puts the copy's centre five points above the line.
    /// </summary>
    [Fact]
    public void AnOffsetMovesACopyAcrossThePathToTheLeftOfTravel()
    {
        IReadOnlyList<ScatterBrushPlacement> copies = Place(
            Open(new Point2D(0, 0), new Point2D(100, 0)),
            Spray(spacing: new ScatterParameter(100), offset: new ScatterParameter(5)));

        ScatterBrushPlacement copy = Assert.Single(copies);
        Point2D centre = Centre(copy);
        Assert.Equal(0.0, centre.X, 6);
        Assert.Equal(-5.0, centre.Y, 6);
        Assert.Equal(5.0, copy.Offset, 9);
    }

    /// <summary>
    /// A copy is scaled by its own scale as well as by the brush's size: a scale of 2 on a 20-across brush draws the
    /// copy 40 across, measured on the transform rather than read off the parameter.
    /// </summary>
    [Fact]
    public void ACopyIsDrawnAtItsOwnScale()
    {
        IReadOnlyList<ScatterBrushPlacement> copies = Place(
            Open(new Point2D(0, 0), new Point2D(100, 0)),
            Spray(spacing: new ScatterParameter(100), scale: new ScatterParameter(2)));

        ScatterBrushPlacement copy = Assert.Single(copies);
        Assert.Equal(2.0, copy.Scale, 9);
        Assert.Equal(40.0, (copy.Transform.Transform(new Point2D(10, 5)) - copy.Transform.Transform(new Point2D(0, 5))).Length, 6);
    }

    /// <summary>
    /// The brush's size, pitch and ranges are in the stroke's own units, so a renderer's scale carries the whole
    /// scatter with it: at twice the scale a 50pt pitch is a 100pt pitch and a 20-across copy is 40 across.
    /// </summary>
    [Fact]
    public void TheRenderersScaleScalesTheCopies()
    {
        IReadOnlyList<ScatterBrushPlacement> copies = Place(
            Open(new Point2D(0, 0), new Point2D(400, 0)), Spray(spacing: new ScatterParameter(50)), scale: 2.0);

        Assert.Equal(4, copies.Count);
        Assert.Equal(new[] { 0.0, 100.0, 200.0, 300.0 }, copies.Select(c => c.Position).ToArray());
        Assert.All(copies, c => Assert.Equal(40.0, (c.Transform.Transform(new Point2D(10, 5)) - c.Transform.Transform(new Point2D(0, 5))).Length, 6));
    }

    // ---------------------------------------------------------------------------------------------------------
    // 3. The randomness.
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// **Every copy stays inside the range the brush states, and the copies really differ.**
    ///
    /// The first half alone is not the claim: an implementation that ignored the randomness and drew the fixed
    /// value at every copy would satisfy every range assertion below. So the copies are also asserted to differ
    /// from one another on the three axes that are stated with a range, which is what tells "scattered" apart from
    /// "machined".
    /// </summary>
    [Fact]
    public void RandomnessStaysInsideItsRangeAndIsReallyApplied()
    {
        var spacing = new ScatterParameter(50, 6);
        var rotation = new ScatterParameter(30, 10);
        var scale = new ScatterParameter(1.0, 0.25);
        var offset = new ScatterParameter(0, 8);

        IReadOnlyList<ScatterBrushPlacement> copies = Place(
            Open(new Point2D(0, 0), new Point2D(400, 0)),
            Spray(spacing: spacing, rotation: rotation, scale: scale, offset: offset));

        Assert.True(copies.Count > 3, "a 400pt path at a 50pt pitch has to hold more than three copies");

        foreach (ScatterBrushPlacement copy in copies)
        {
            Assert.InRange(copy.RotationDegrees, rotation.Min, rotation.Max);
            Assert.InRange(copy.Scale, scale.Min, scale.Max);
            Assert.InRange(copy.Offset, offset.Min, offset.Max);
        }

        // The pitch between consecutive copies is the spacing plus its own draw from the range. The **last** copy
        // has no successor, so the gaps are one shorter than the copies.
        for (int i = 0; i + 1 < copies.Count; i++)
        {
            Assert.InRange(copies[i + 1].Position - copies[i].Position, spacing.Min, spacing.Max);
        }

        // And the scatter is not the fixed value repeated: at least two copies differ on each ranged axis.
        Assert.True(copies.Select(c => Math.Round(c.RotationDegrees, 6)).Distinct().Count() > 1,
            "the rotation range has to move the copies apart from one another");
        Assert.True(copies.Select(c => Math.Round(c.Scale, 6)).Distinct().Count() > 1,
            "the scale range has to move the copies apart from one another");
        Assert.True(copies.Select(c => Math.Round(c.Offset, 6)).Distinct().Count() > 1,
            "the offset range has to move the copies apart from one another");
    }

    /// <summary>
    /// **The same document renders identically every time.** The copies are a pure function of the path and the
    /// parameters, so asking twice gives the same numbers - which a fresh <see cref="Random"/> per call could not.
    /// </summary>
    [Fact]
    public void TheSamePathAndBrushGiveTheSameCopiesEveryTime()
    {
        PathItem path = Open(new Point2D(0, 0), new Point2D(300, 0), new Point2D(300, 80));
        BrushSpec brush = Spray(
            spacing: new ScatterParameter(45, 7),
            rotation: new ScatterParameter(20, 15),
            scale: new ScatterParameter(1.2, 0.3),
            offset: new ScatterParameter(3, 6),
            opacity: new ScatterParameter(0.8, 0.2));

        IReadOnlyList<ScatterBrushPlacement> first = Place(path, brush);
        IReadOnlyList<ScatterBrushPlacement> second = Place(path, brush);

        Assert.Equal(first.Count, second.Count);
        for (int i = 0; i < first.Count; i++)
        {
            Assert.Equal(first[i].Position, second[i].Position, 12);
            Assert.Equal(first[i].RotationDegrees, second[i].RotationDegrees, 12);
            Assert.Equal(first[i].Scale, second[i].Scale, 12);
            Assert.Equal(first[i].Offset, second[i].Offset, 12);
            Assert.Equal(first[i].Opacity, second[i].Opacity, 12);

            // The six numbers rather than the struct, so a transform that compares equal without carrying the same
            // placement cannot pass this.
            Assert.Equal(first[i].Transform.A, second[i].Transform.A, 12);
            Assert.Equal(first[i].Transform.B, second[i].Transform.B, 12);
            Assert.Equal(first[i].Transform.C, second[i].Transform.C, 12);
            Assert.Equal(first[i].Transform.D, second[i].Transform.D, 12);
            Assert.Equal(first[i].Transform.E, second[i].Transform.E, 12);
            Assert.Equal(first[i].Transform.F, second[i].Transform.F, 12);
        }
    }

    /// <summary>
    /// **A range of zero is the stated value exactly, bit for bit.** Not a near miss and not the mean of a range
    /// nobody asked for: zero randomness is how a caller pins a scatter down to a fixed run of copies.
    /// </summary>
    [Fact]
    public void ZeroRandomnessReproducesTheFixedPlacementExactly()
    {
        IReadOnlyList<ScatterBrushPlacement> copies = Place(
            Open(new Point2D(0, 0), new Point2D(200, 0)),
            Spray(
                spacing: new ScatterParameter(40, 0),
                rotation: new ScatterParameter(25, 0),
                scale: new ScatterParameter(1.5, 0),
                offset: new ScatterParameter(6, 0),
                opacity: new ScatterParameter(0.5, 0)));

        Assert.Equal(new[] { 0.0, 40.0, 80.0, 120.0, 160.0 }, copies.Select(c => c.Position).ToArray());
        Assert.All(copies, c => Assert.Equal(25.0, c.RotationDegrees));
        Assert.All(copies, c => Assert.Equal(1.5, c.Scale));
        Assert.All(copies, c => Assert.Equal(6.0, c.Offset));
        Assert.All(copies, c => Assert.Equal(0.5, c.Opacity, 12));
        Assert.All(copies, c => Assert.Equal(-65.0, AcrossDegrees(c), 9));
    }

    /// <summary>A copy's opacity is its own, and it is clamped to the 0..1 a pixel can be painted at.</summary>
    [Fact]
    public void ACopyOpacityIsClampedToWhatCanBePainted()
    {
        // A range that runs past both ends of a pixel's opacity: 0.9 plus-or-minus 0.5 reaches 1.4 and 0.4, so the
        // clamp is exercised on the way up and the copy is still a real one on the way down.
        IReadOnlyList<ScatterBrushPlacement> copies = Place(
            Open(new Point2D(0, 0), new Point2D(200, 0)),
            Spray(spacing: new ScatterParameter(25), opacity: new ScatterParameter(0.9, 0.5)));

        Assert.Equal(8, copies.Count);
        Assert.All(copies, c => Assert.InRange(c.Opacity, 0.0, 1.0));
        Assert.Contains(copies, c => c.Opacity < 1.0);
    }

    // ---------------------------------------------------------------------------------------------------------
    // 4. Pressure.
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// **More pressure is a bigger, more opaque copy.** The two targets are the existing dynamics - the same curves
    /// the pen already drives width and opacity with - so what is asserted is that the scatter seam reads them in
    /// the direction a pen means.
    /// </summary>
    [Fact]
    public void PressureChangesScaleAndOpacityInTheExpectedDirection()
    {
        // The two targets the scatter seam reads, on the existing dynamics the pen already drives width with - built
        // the way the model builds one, so this test drives the public surface rather than a helper written for it.
        var targets = Enum.GetValues<DynamicsTarget>().Select(_ => DynamicsTargetSpec.Off).ToArray();
        targets[(int)DynamicsTarget.ScatterScale] = DynamicsTargetSpec.Preset(DynamicsPreset.Linear);
        targets[(int)DynamicsTarget.Opacity] = DynamicsTargetSpec.Preset(DynamicsPreset.Linear);
        var dynamics = new DynamicsSpec(targets);

        BrushSpec brush = Spray(
            spacing: new ScatterParameter(50),
            scale: new ScatterParameter(1.0),
            opacity: new ScatterParameter(1.0),
            dynamics: dynamics);

        PathItem path = Open(new Point2D(0, 0), new Point2D(200, 0));

        IReadOnlyList<ScatterBrushPlacement> light = Place(path, brush, pressure: 0.25);
        IReadOnlyList<ScatterBrushPlacement> heavy = Place(path, brush, pressure: 0.75);

        Assert.Equal(light.Count, heavy.Count);
        Assert.True(heavy[0].Scale > light[0].Scale,
            $"0.75 pressure should scale a copy above 0.25 pressure ({heavy[0].Scale} vs {light[0].Scale})");
        Assert.True(heavy[0].Opacity > light[0].Opacity,
            $"0.75 pressure should paint a copy more opaque than 0.25 ({heavy[0].Opacity} vs {light[0].Opacity})");
        Assert.Equal(0.25, light[0].Scale, 6);
        Assert.Equal(0.75, heavy[0].Scale, 6);
    }

    /// <summary>
    /// With no dynamics recorded, pressure is **ignored** rather than passed through - the same rule the width
    /// dynamics follow. A pressure of zero on an ordinary brush must not erase the copies.
    /// </summary>
    [Fact]
    public void WithoutDynamicsPressureIsIgnored()
    {
        BrushSpec brush = Spray(spacing: new ScatterParameter(50), scale: new ScatterParameter(1.0), opacity: new ScatterParameter(0.4));
        IReadOnlyList<ScatterBrushPlacement> copies = Place(Open(new Point2D(0, 0), new Point2D(100, 0)), brush, pressure: 0.0);

        Assert.NotEmpty(copies);
        Assert.All(copies, c => Assert.Equal(1.0, c.Scale, 9));
        Assert.All(copies, c => Assert.Equal(0.4, c.Opacity, 9));
    }

    // ---------------------------------------------------------------------------------------------------------
    // 5. What places nothing.
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>An asset the document does not have places nothing: the model has no box to draw, and inventing one would be artwork nobody wrote.</summary>
    [Fact]
    public void AnAssetTheDocumentDoesNotHavePlacesNothing()
    {
        BrushSpec brush = Spray(spacing: new ScatterParameter(50), asset: Guid.NewGuid());
        Assert.Empty(Place(Open(new Point2D(0, 0), new Point2D(100, 0)), brush));
    }

    /// <summary>A brush that is not a scatter brush places nothing, whatever members it carries.</summary>
    [Fact]
    public void AnotherKindOfBrushPlacesNothingHere()
    {
        PathItem path = Open(new Point2D(0, 0), new Point2D(100, 0));
        Assert.Empty(Place(path, BrushSpec.Calligraphic("Chisel", 35, 0.2, 24)));
        Assert.Empty(Place(path, BrushSpec.Art("Vine", Copy, 20)));
    }

    /// <summary>A path with no segment has nowhere to scatter along.</summary>
    [Fact]
    public void APathWithNoSegmentPlacesNothing()
    {
        PathItem path = Open(new Point2D(0, 0));
        Assert.Empty(Place(path, Spray(spacing: new ScatterParameter(50))));
    }
}
