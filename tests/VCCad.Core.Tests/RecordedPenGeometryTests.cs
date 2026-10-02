using VCCad.Core.Model;
using VCCad.Core.Serialization;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// **What a recorded pen does to the geometry that is drawn** (issue #107).
///
/// The model could already carry a pen response - the curves, the derived speed and the width profile the pressure
/// produced - and every one of those was tested as a pure function while a stored document had no member saying
/// what the pen actually reported. So the two brush kinds whose response is resolved at **render time** rather than
/// baked into a width profile were placed for a fully pressed, upright pen: a scatter's copies all came out one
/// size and one opacity, and a bristle bundle was always the width a heavy pen gives.
///
/// These tests are therefore on the **geometry the renderers fill** - `ScatterBrushPath.Placements` and
/// `StrokeOutlineBuilder.Outline`, the one seam the canvas, the PDF writer and the SVG writer all consume. Every
/// directional claim is paired with a control at the other extreme: the same brush with no recorded pen, so "the
/// pen is honoured" is told apart from "something changed".
/// </summary>
public class RecordedPenGeometryTests
{
    private static readonly Rect2D TenByTen = new(0, 0, 10, 10);

    private static readonly Guid Copy = Guid.Parse("66666666-6666-6666-6666-666666666666");

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

    private static IReadOnlyList<ScatterBrushPlacement> Copies(
        PathItem path, BrushSpec brush, PenProfile? pen)
        => ScatterBrushPath.Placements(path, brush, Bounds, 1.0, pen);

    /// <summary>A scatter brush of one ten-wide copy, a stated pitch, and whatever response is picked.</summary>
    private static BrushSpec Spray(DynamicsSpec? dynamics = null, double spacing = 50.0)
        => BrushSpec.Scatter(
            "Spray", Copy, size: 20.0, spacing: new ScatterParameter(spacing), dynamics: dynamics);

    /// <summary>A pen that starts almost unpressed and ends fully pressed, so the ends are told apart.</summary>
    private static PenProfile Ramp()
        => new(new[] { new PenSample(0.0, 0.1), new PenSample(1.0, 1.0) });

    // ---------------------------------------------------------------------------------------------------------
    // 1. The profile itself.
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// **The pen is read where a copy sits, and clamped outside the stroke.** The two ends are the values that were
    /// recorded and the middle is between them - which is the whole reason this is a profile rather than one number
    /// for the path.
    /// </summary>
    [Fact]
    public void APenProfileIsReadAlongTheStrokeAndClampedAtItsEnds()
    {
        var pen = new PenProfile(new[] { new PenSample(0.0, 0.2, 10.0), new PenSample(1.0, 1.0, 50.0) });

        Assert.Equal(0.2, pen.At(0.0).Pressure, 9);
        Assert.Equal(1.0, pen.At(1.0).Pressure, 9);
        Assert.Equal(0.6, pen.At(0.5).Pressure, 9);
        Assert.Equal(30.0, pen.At(0.5).TiltDegrees, 9);

        // Clamped rather than extrapolated: a position off the end of a stroke is still drawn at the pen's reading
        // at that end, not at a pressure the tablet never reported.
        Assert.Equal(0.2, pen.At(-4.0).Pressure, 9);
        Assert.Equal(1.0, pen.At(9.0).Pressure, 9);
    }

    /// <summary>
    /// **A stroke that records no pen is a fully pressed, upright one.** This is the control every other test in
    /// this file leans on: a document written before pens were recorded, and a line drawn with a mouse, must draw
    /// exactly what they drew before the member existed.
    /// </summary>
    [Fact]
    public void AStrokeThatRecordsNoPenIsFullyPressedAndUpright()
    {
        StrokeSpec stroke = StrokeSpec.Hairline(ColorRgb.Black);

        Assert.False(stroke.HasPen);
        Assert.Equal(1.0, stroke.PressureAt(0.5), 9);
        Assert.Equal(0.0, stroke.TiltAt(0.5), 9);
        Assert.Equal(PenReading.Full, stroke.PenAt(0.5));
    }

    /// <summary>
    /// **A mouse records nothing.** Every sample is a fully pressed, upright reading, so the profile is the
    /// identity: storing it would grow the bytes of every mouse-drawn document while changing nothing about it,
    /// which is the absence rule the whole model follows.
    /// </summary>
    [Fact]
    public void AFullyPressedUprightRecordingIsTheIdentityAndIsNotStored()
    {
        var mouse = new[]
        {
            new InputSample(new Point2D(0, 0), 0.0, 1.0, 0.0, 0.0),
            new InputSample(new Point2D(100, 0), 1.0, 1.0, 0.0, 0.0),
        };

        Assert.Null(PenProfile.FromSamples(mouse));

        var pen = new[]
        {
            new InputSample(new Point2D(0, 0), 0.0, 0.2, 0.0, 0.0),
            new InputSample(new Point2D(100, 0), 1.0, 1.0, 0.0, 0.0),
        };

        PenProfile? recorded = PenProfile.FromSamples(pen);
        Assert.NotNull(recorded);
        Assert.Equal(0.2, recorded!.At(0.0).Pressure, 9);
        Assert.Equal(1.0, recorded.At(1.0).Pressure, 9);
    }

    // ---------------------------------------------------------------------------------------------------------
    // 2. Pressure reaches a scatter brush's copies, per copy.
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// **A copy is drawn the size the pen was at its own place.** The same brush under a ramp from almost nothing
    /// to fully pressed puts a small copy at the light end and a large one at the heavy end - so this is a fact
    /// about where each copy sits rather than about the stroke as a whole, which one pressure for the run could
    /// never be.
    /// </summary>
    [Fact]
    public void AScatterCopysSizeFollowsThePressureAtItsOwnPlace()
    {
        PathItem path = Open(new Point2D(0, 0), new Point2D(200, 0));
        BrushSpec brush = Spray(DynamicsSpec.RespondingTo(DynamicsTarget.ScatterScale));

        IReadOnlyList<ScatterBrushPlacement> sized = Copies(path, brush, Ramp());

        // A 200 long line at a 50 pitch is four copies, at 0, 50, 100 and 150 - so a linear ramp from 0.1 to 1.0
        // reads them as 0.1, 0.325, 0.55 and 0.775 of the brush's size.
        Assert.Equal(4, sized.Count);
        Assert.Equal(0.1, sized[0].Scale, 6);
        Assert.Equal(0.325, sized[1].Scale, 6);
        Assert.Equal(0.55, sized[2].Scale, 6);
        Assert.Equal(0.775, sized[3].Scale, 6);

        // The copy is really smaller, not merely labelled one: the placement's own transform scales the artwork.
        Assert.True(sized[0].Length < sized[3].Length);
    }

    /// <summary>
    /// The control the size test needs: **the same brush with no recorded pen draws every copy at the brush's own
    /// size.** Without this, "the sizes differ" could be satisfied by any variation at all.
    /// </summary>
    [Fact]
    public void AScatterWithNoRecordedPenDrawsEveryCopyAtTheBrushsSize()
    {
        PathItem path = Open(new Point2D(0, 0), new Point2D(200, 0));
        BrushSpec brush = Spray(DynamicsSpec.RespondingTo(DynamicsTarget.ScatterScale));

        Assert.All(Copies(path, brush, pen: null), copy => Assert.Equal(1.0, copy.Scale, 9));
    }

    /// <summary>
    /// **Opacity is a separate response and it lands too.** With only the opacity target switched on the copies are
    /// all the brush's own size and each is painted at the pressure where it sits - which is what the per-copy
    /// opacity on a placement is for.
    /// </summary>
    [Fact]
    public void AScatterCopyOpacityFollowsThePressureAtItsOwnPlace()
    {
        PathItem path = Open(new Point2D(0, 0), new Point2D(200, 0));
        BrushSpec brush = Spray(DynamicsSpec.RespondingTo(DynamicsTarget.Opacity));

        IReadOnlyList<ScatterBrushPlacement> faded = Copies(path, brush, Ramp());

        Assert.All(faded, copy => Assert.Equal(1.0, copy.Scale, 9));
        Assert.Equal(0.1, faded[0].Opacity, 6);
        Assert.Equal(0.775, faded[^1].Opacity, 6);

        Assert.All(Copies(path, brush, pen: null), copy => Assert.Equal(1.0, copy.Opacity, 9));
    }

    /// <summary>
    /// And a recording reaches the copies only through a response that was picked: with both targets off the
    /// pressure changes neither size nor opacity, which is the rule the width dynamics already follow.
    /// </summary>
    [Fact]
    public void ARecordedPenChangesNothingWhenNoResponseIsSwitchedOn()
    {
        PathItem path = Open(new Point2D(0, 0), new Point2D(200, 0));
        BrushSpec brush = Spray();

        IReadOnlyList<ScatterBrushPlacement> flat = Copies(path, brush, Ramp());

        Assert.All(flat, copy =>
        {
            Assert.Equal(1.0, copy.Scale, 9);
            Assert.Equal(1.0, copy.Opacity, 9);
        });
    }

    // ---------------------------------------------------------------------------------------------------------
    // 3. Pressure and tilt reach a bristle brush through the one outline seam.
    // ---------------------------------------------------------------------------------------------------------

    private static BrushSpec Bristles(BristleBrushSpec spec)
        => BrushSpec.Bristle("Bristle", size: 40.0, bristles: spec);

    private static StrokeSpec BristleStroke(BrushSpec brush, PenProfile? pen)
        => new(true, ColorRgb.Black, 4, StrokeCap.Butt, StrokeJoin.Miter, 4) { Brush = brush, Pen = pen };

    /// <summary>The vertical extent of the loops the renderers fill, which is how wide the bundle is across the path.</summary>
    private static double InkHeight(PathItem path, StrokeSpec stroke)
    {
        double min = double.PositiveInfinity;
        double max = double.NegativeInfinity;
        foreach (IReadOnlyList<Point2D> loop in StrokeOutlineBuilder.Outline(path, stroke))
        {
            foreach (Point2D point in loop)
            {
                min = Math.Min(min, point.Y);
                max = Math.Max(max, point.Y);
            }
        }

        Assert.True(max >= min, "the outline should have points");
        return max - min;
    }

    /// <summary>
    /// **A recorded pen reaches a bristle bundle through the outline seam.** The bundle is asked for twice - once
    /// with an almost unpressed pen recorded and once fully pressed - and the region the renderers fill is narrow
    /// under the light pen and full width under the heavy one. Before the member existed this seam was called for a
    /// fully pressed pen, so both asked the same question and got the same picture.
    /// </summary>
    [Fact]
    public void ALightRecordedPenNarrowsTheBristleOutlineThatIsDrawn()
    {
        PathItem path = Open(new Point2D(0, 0), new Point2D(200, 0));
        BrushSpec brush = Bristles(new BristleBrushSpec(
            Count: 9, Spread: 1.0, PressureSpread: 1.0, Randomness: 0.0, Stiffness: 1.0));

        double light = InkHeight(path, BristleStroke(brush, PenProfile.Constant(0.0)));
        double heavy = InkHeight(path, BristleStroke(brush, PenProfile.Constant(1.0)));
        double none = InkHeight(path, BristleStroke(brush, pen: null));

        // A size of forty and a spread of one is twenty either side of the centreline, plus half a bristle's own
        // thickness at each edge; at no pressure the bundle collapses onto the centreline, so only that is left.
        Assert.Equal(40.5, heavy, 6);
        Assert.True(light < heavy / 10.0, $"a light pen should collapse the bundle: {light} against {heavy}");

        // And a stroke that records no pen is drawn as the heavy one - the absence means a fully pressed pen.
        Assert.Equal(heavy, none, 9);
    }

    /// <summary>
    /// **A recorded tilt reaches the bundle the same way.** The bristles are turned by the pen's lean where the
    /// stroke records one, which shows in the region the renderers fill: a turned bundle is not the band an upright
    /// one is. The control is the same brush with an upright recording.
    /// </summary>
    [Fact]
    public void ARecordedTiltTurnsTheBristlesThatAreDrawn()
    {
        PathItem path = Open(new Point2D(0, 0), new Point2D(200, 0));
        BrushSpec brush = Bristles(new BristleBrushSpec(
            Count: 5, Spread: 0.0, Randomness: 0.0, Stiffness: 1.0, TiltTurn: 1.0));

        double upright = InkHeight(path, BristleStroke(brush, PenProfile.Constant(1.0, 0.0)));
        double laidOver = InkHeight(path, BristleStroke(brush, PenProfile.Constant(1.0, 30.0)));

        // With no spread every bristle is on the centreline, so an upright bundle inks only its own thickness while
        // a bundle turned thirty degrees runs diagonally across a two-hundred point path.
        Assert.True(upright < 2.0, $"an upright bundle should be a thin band: {upright}");
        Assert.True(laidOver > 50.0, $"a bundle turned thirty degrees should cover a diagonal band: {laidOver}");
    }

    // ---------------------------------------------------------------------------------------------------------
    // 4. The record is document state.
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// **A recorded pen survives a save and a reload**, and an ordinary stroke still writes no member: the two
    /// halves of the absence rule, asserted on the bytes rather than on the model.
    /// </summary>
    [Fact]
    public void ARecordedPenRoundTripsAndAnUnrecordedOneWritesNoBytes()
    {
        CadDocument plain = CadDocument.CreateDefault();
        plain.Artboards[0].Layers[0].AddItem(Open(new Point2D(0, 0), new Point2D(100, 0)));

        string noPen = VccadDocumentSerializer.Serialize(plain);
        Assert.DoesNotContain("\"pen\"", noPen, StringComparison.OrdinalIgnoreCase);

        CadDocument recorded = CadDocument.CreateDefault();
        PathItem path = Open(new Point2D(0, 0), new Point2D(100, 0));
        path.Stroke = path.Stroke with { Pen = Ramp() };
        recorded.Artboards[0].Layers[0].AddItem(path);

        string json = VccadDocumentSerializer.Serialize(recorded);
        Assert.Contains("\"pen\"", json, StringComparison.OrdinalIgnoreCase);

        CadDocument reloaded = VccadDocumentSerializer.Deserialize(json);
        PathItem back = reloaded.Artboards[0].Layers[0].Children.OfType<PathItem>().Single();
        Assert.Equal(Ramp(), back.Stroke.Pen);

        // Deterministic: one more turn through the round trip is the same bytes, as every document must be.
        Assert.Equal(json, VccadDocumentSerializer.Serialize(reloaded));
    }
}
