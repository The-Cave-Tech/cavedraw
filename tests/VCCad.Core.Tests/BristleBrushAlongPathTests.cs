using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// A bristle brush sweeps a bundle of bristles along the path, each drawn as its own stroke, so the line looks
/// painted rather than inked (issue #103).
///
/// The assertions are on the **geometry** - where each bristle runs, how far it sits across the path, which way it
/// points and how long it is - and not on the parameters. A parameter round trip cannot see the defect this
/// family keeps producing: a member stored, serialized and asserted perfectly while the step that honours it never
/// runs. So every modulation the issue names is measured as a change in the bristles themselves:
///
/// - **The count, and the bound on it.** The bundle holds the number the brush asked for until that number passes
///   the engine's limit, and past it the shortage is **reported** rather than drawn quietly.
/// - **Pressure widens the bundle.** The same brush at two pressures puts its outermost bristle further from the
///   centreline at the higher one.
/// - **Tilt turns the bristles.** A pen laid over turns every bristle's own direction by the amount the brush
///   states, measured as the angle of the bristle's own polyline.
/// - **Determinism.** Two answers for one document are the same bristles, because the stray is a pure function of
///   the path and the parameters rather than a fresh <see cref="Random"/>.
/// </summary>
public class BristleBrushAlongPathTests
{
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

    private static BrushSpec Brush(BristleBrushSpec? spec = null, double size = 40.0)
        => BrushSpec.Bristle("Bristle", size, spec);

    private static IReadOnlyList<BristleStroke> Bristles(
        PathItem path, BrushSpec brush, double pressure = 1.0, double tilt = 0.0, double scale = 1.0)
        => BristleBrushPath.Strokes(path, brush, scale, PenProfile.Constant(pressure, tilt)).Bristles;

    /// <summary>The direction of a bristle's own polyline, in degrees, from its first point to its last.</summary>
    private static double Direction(BristleStroke bristle)
    {
        Point2D from = bristle.Points[0];
        Point2D to = bristle.Points[^1];
        return Math.Atan2(to.Y - from.Y, to.X - from.X) * 180.0 / Math.PI;
    }

    private static double PolylineLength(IReadOnlyList<Point2D> points)
    {
        double total = 0.0;
        for (int i = 1; i < points.Count; i++)
        {
            total += (points[i] - points[i - 1]).Length;
        }

        return total;
    }

    // ---------------------------------------------------------------------------------------------------------
    // 1. The count, and the bound on it.
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// **The bundle holds exactly the number of bristles the brush asks for**, so "how many bristles" is a setting
    /// a person makes rather than a suggestion an engine rounds.
    /// </summary>
    [Fact]
    public void TheBundleHoldsTheCountTheBrushAskedFor()
    {
        IReadOnlyList<BristleStroke> bristles = Bristles(
            Open(new Point2D(0, 0), new Point2D(200, 0)),
            Brush(new BristleBrushSpec(Count: 7)));

        Assert.Equal(7, bristles.Count);
    }

    /// <summary>
    /// **A count past the engine's bound is reported, not silently served short.** Five thousand bristles is five
    /// thousand strokes and the engine draws a stated maximum; what must not happen is a drawing that looks like it
    /// lost detail with nothing saying why, so the shortage comes back as a flag with the number that was asked for.
    /// </summary>
    [Fact]
    public void ACountPastTheEnginesBoundIsReportedRatherThanQuietlyShort()
    {
        BristleBundle bundle = BristleBrushPath.Strokes(
            Open(new Point2D(0, 0), new Point2D(200, 0)),
            Brush(new BristleBrushSpec(Count: 5_000)));

        Assert.True(bundle.CountBoundHit, "asking for five thousand bristles has to say the bound was hit");
        Assert.Equal(5_000, bundle.Requested);
        Assert.Equal(BristleBrushPath.MaxBristles, bundle.Bristles.Count);
        Assert.True(bundle.Bristles.Count < bundle.Requested);
    }

    /// <summary>A bundle inside the bound does not claim to have been cut short, which is what makes the flag mean something.</summary>
    [Fact]
    public void ABundleInsideTheBoundIsNotReportedAsBound()
    {
        BristleBundle bundle = BristleBrushPath.Strokes(
            Open(new Point2D(0, 0), new Point2D(200, 0)),
            Brush(new BristleBrushSpec(Count: 18)));

        Assert.False(bundle.CountBoundHit);
        Assert.Equal(18, bundle.Requested);
        Assert.Equal(18, bundle.Bristles.Count);
    }

    // ---------------------------------------------------------------------------------------------------------
    // 2. Pressure widens the bundle.
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// **Pressure spreads the bristles further across the path.** The same document is asked twice with only the
    /// pen's pressure changed: the outermost bristle sits further from the centreline under the heavier pen, and
    /// both stay inside the spread the brush states. A member that is stored and never applied answers the two
    /// pressures identically, which is exactly what this catches.
    /// </summary>
    [Fact]
    public void MorePressureSpreadsTheBundleWider()
    {
        PathItem path = Open(new Point2D(0, 0), new Point2D(200, 0));
        BrushSpec brush = Brush(new BristleBrushSpec(Count: 9, Spread: 1.0, PressureSpread: 1.0, Randomness: 0.0));

        double light = Bristles(path, brush, pressure: 0.0).Max(b => Math.Abs(b.Offset));
        double heavy = Bristles(path, brush, pressure: 1.0).Max(b => Math.Abs(b.Offset));

        // A size of forty and a spread of one is half a bundle of twenty either side; at no pressure the bundle
        // collapses to the centreline, which is what a pressure response of one means.
        Assert.Equal(20.0, heavy, 9);
        Assert.Equal(0.0, light, 9);
        Assert.True(heavy > light, $"a heavier pen has to spread the bundle wider ({heavy} against {light})");

        // Halfway pressed is halfway spread, so the response is the stated amount rather than a switch.
        double half = Bristles(path, brush, pressure: 0.5).Max(b => Math.Abs(b.Offset));
        Assert.Equal(10.0, half, 9);
    }

    /// <summary>
    /// **A brush that states no pressure response ignores the pen.** With pressure-to-spread at zero the bundle is
    /// the same at any pressure, which is the rule the width dynamics already follow: a stroke drawn by a mouse has
    /// no pressure to pass on, and reading the absence as a light press would thin everything drawn with one.
    /// </summary>
    [Fact]
    public void ABrushWithNoPressureResponseIgnoresThePen()
    {
        PathItem path = Open(new Point2D(0, 0), new Point2D(200, 0));
        BrushSpec brush = Brush(new BristleBrushSpec(Count: 5, PressureSpread: 0.0, Randomness: 0.0));

        Assert.Equal(
            Bristles(path, brush, pressure: 0.0).Max(b => Math.Abs(b.Offset)),
            Bristles(path, brush, pressure: 1.0).Max(b => Math.Abs(b.Offset)),
            9);
    }

    // ---------------------------------------------------------------------------------------------------------
    // 3. Tilt turns the bristles.
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// **The pen's tilt reaches the bristles' own direction.** The path runs along +X, so every bristle runs along
    /// +X too until the pen is laid over; at a tilt of thirty degrees and a response of one, every bristle's own
    /// polyline points thirty degrees off the line. A tilt recorded and never honoured leaves every bristle flat.
    /// </summary>
    [Fact]
    public void TiltTurnsTheBristles()
    {
        PathItem path = Open(new Point2D(0, 0), new Point2D(200, 0));
        BrushSpec brush = Brush(new BristleBrushSpec(Count: 6, Randomness: 0.0, Stiffness: 1.0, TiltTurn: 1.0));

        Assert.All(Bristles(path, brush), bristle => Assert.Equal(0.0, Direction(bristle), 6));

        IReadOnlyList<BristleStroke> tilted = Bristles(path, brush, tilt: 30.0);
        Assert.All(tilted, bristle => Assert.Equal(30.0, Direction(bristle), 6));

        // The turn is reported on the bristle as well as visible in its points, because that is what
        // `brush.bristles` hands a driver that cannot see.
        Assert.All(tilted, bristle => Assert.Equal(30.0, bristle.TurnDegrees, 9));
    }

    /// <summary>
    /// **A tilt response of zero ignores the pen's tilt**, for the reason the pressure response does: a brush that
    /// states no response is not turned by a signal it never asked for.
    /// </summary>
    [Fact]
    public void ABrushWithNoTiltResponseIgnoresThePen()
    {
        PathItem path = Open(new Point2D(0, 0), new Point2D(200, 0));
        BrushSpec brush = Brush(new BristleBrushSpec(Count: 4, Randomness: 0.0, TiltTurn: 0.0));

        Assert.All(
            Bristles(path, brush, tilt: 45.0),
            bristle => Assert.Equal(0.0, Direction(bristle), 6));
    }

    // ---------------------------------------------------------------------------------------------------------
    // 4. Determinism, and what zero randomness means.
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// **Two answers for one document are the same bristles.** The stray is a pure function of the path and the
    /// brush's parameters through a stable sequence rather than a fresh generator, which is what makes the same
    /// document paint the same picture on every frame and export as the same bytes twice.
    /// </summary>
    [Fact]
    public void TwoBundlesOfOneDocumentAreTheSameBristles()
    {
        PathItem path = Open(new Point2D(0, 0), new Point2D(140, 0), new Point2D(140, 60));
        BrushSpec brush = Brush(new BristleBrushSpec(Count: 11, Randomness: 0.8, Length: 30.0));

        IReadOnlyList<BristleStroke> first = Bristles(path, brush);
        IReadOnlyList<BristleStroke> second = Bristles(path, brush);

        Assert.Equal(first.Count, second.Count);
        for (int i = 0; i < first.Count; i++)
        {
            Assert.Equal(first[i].Offset, second[i].Offset, 12);
            Assert.Equal(first[i].Start, second[i].Start, 12);
            Assert.Equal(first[i].TurnDegrees, second[i].TurnDegrees, 12);
            Assert.Equal(first[i].Shade, second[i].Shade, 12);
            Assert.Equal(first[i].Points.Count, second[i].Points.Count);
            for (int p = 0; p < first[i].Points.Count; p++)
            {
                Assert.Equal(first[i].Points[p].X, second[i].Points[p].X, 12);
                Assert.Equal(first[i].Points[p].Y, second[i].Points[p].Y, 12);
            }
        }
    }

    /// <summary>
    /// **A randomness of zero is the model's own ideal bundle**, not a draw that happens to be nothing: the
    /// bristles are evenly spaced from one edge of the bundle to the other, they all start at the beginning and
    /// they all point the way the path runs. That is what lets a caller pin a bristle brush down and assert it to
    /// the last bit.
    /// </summary>
    [Fact]
    public void ZeroRandomnessIsTheModelsIdealBundle()
    {
        PathItem path = Open(new Point2D(0, 0), new Point2D(100, 0));
        BrushSpec brush = Brush(
            new BristleBrushSpec(Count: 5, Spread: 1.0, Randomness: 0.0, Stiffness: 1.0), size: 40.0);

        IReadOnlyList<BristleStroke> bristles = Bristles(path, brush);

        Assert.Equal(new[] { -20.0, -10.0, 0.0, 10.0, 20.0 }, bristles.Select(b => b.Offset).ToArray());
        Assert.All(bristles, b => Assert.Equal(0.0, b.Start, 9));
        Assert.All(bristles, b => Assert.Equal(0.0, b.TurnDegrees, 9));
    }

    // ---------------------------------------------------------------------------------------------------------
    // 5. Length and stiffness - the two members that shape a bristle rather than place it.
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// **A stated length is how far a bristle runs along the path**, measured as the arc length of its own points
    /// rather than read off the parameter. A length the engine held and never used draws every bristle the whole
    /// way along the path.
    /// </summary>
    [Fact]
    public void AStatedLengthIsHowFarABristleRuns()
    {
        PathItem path = Open(new Point2D(0, 0), new Point2D(200, 0));
        BrushSpec brush = Brush(new BristleBrushSpec(Count: 4, Length: 40.0, Randomness: 0.0, Stiffness: 1.0));

        foreach (BristleStroke bristle in Bristles(path, brush))
        {
            Assert.Equal(40.0, bristle.Length, 9);

            // The sampled polyline is at the stated step, so its length is the run to within one sample.
            Assert.InRange(PolylineLength(bristle.Points), 39.0, 41.0);
        }

        // A length of zero is the model's default and runs the whole path - the bristles are streaks spanning the
        // stroke rather than dashes on it.
        BrushSpec whole = Brush(new BristleBrushSpec(Count: 4, Length: 0.0, Randomness: 0.0));
        Assert.All(Bristles(path, whole), bristle => Assert.Equal(200.0, bristle.Length, 9));
    }

    /// <summary>
    /// **Stiffness decides whether a bristle follows the path or is a rigid hair.** Across a right-angled bend the
    /// same bristle at stiffness one keeps the corner and at stiffness zero runs straight from where it starts, so
    /// the two answers differ in their own points - not in a parameter.
    /// </summary>
    [Fact]
    public void StiffnessDecidesWhetherABristleFollowsThePath()
    {
        PathItem path = Open(new Point2D(0, 0), new Point2D(100, 0), new Point2D(100, 100));
        var spec = new BristleBrushSpec(Count: 1, Randomness: 0.0, Stiffness: 1.0, Length: 0.0);
        BrushSpec brush = Brush(spec);

        BristleStroke stiff = Assert.Single(Bristles(path, brush));
        BristleStroke limp = Assert.Single(Bristles(path, brush with { BristleSpec = spec with { Stiffness = 0.0 } }));

        // The stiff bristle turns the corner with the path; the limp one runs straight from the start, so it ends
        // nowhere near where the path does.
        Assert.True(stiff.Points[^1].Y > 90.0, "a stiff bristle should follow the path round the bend");
        Assert.Equal(0.0, limp.Points[^1].Y, 6);
    }

    // ---------------------------------------------------------------------------------------------------------
    // 6. The honouring step: the outline the renderers fill.
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// **A bristle brush reaches the renderers as the union of its bristle strokes.** The plan every renderer
    /// consumes is an outline made of one loop per bristle, so the canvas, the PDF writer and the SVG writer fill
    /// the bristles without any of them learning a fifth drawing route - and the region is explicitly wider than
    /// the stroke's own width, which a brush that was stored and never honoured would not be.
    /// </summary>
    [Fact]
    public void TheOutlineIsTheUnionOfTheBristleStrokes()
    {
        PathItem path = Open(new Point2D(0, 0), new Point2D(200, 0));
        var spec = new BristleBrushSpec(Count: 6, Spread: 1.0, Randomness: 0.0, Thickness: 2.0);
        path.Stroke = path.Stroke with { Brush = Brush(spec), Width = 4.0 };

        StrokeRenderPlan plan = StrokeOutlineBuilder.Plan(path, path.Stroke);

        Assert.True(plan.IsOutline, "a stroke carrying a brush is drawn as its outline");
        Assert.Equal(6, plan.Outlines.Count);

        // The bundle is forty across and the stroke's own width is four, so the outline reaches far past the line
        // the pen would have drawn.
        double lowest = plan.Outlines.SelectMany(loop => loop).Min(p => p.Y);
        double highest = plan.Outlines.SelectMany(loop => loop).Max(p => p.Y);
        Assert.True(highest - lowest > 30.0, $"the bundle should span the brush's width ({(highest - lowest)})");

        // Six bristles in one region, all one colour until the brush jitters them.
        Assert.Null(plan.Paints);
    }

    /// <summary>
    /// **A colour jitter reaches the renderers as a colour per bristle.** A brush whose bristles differ states one
    /// paint per loop in the same order as the loops, so a renderer that fills them can paint each bristle its own
    /// shade - which a member that was stored and never applied could not.
    /// </summary>
    [Fact]
    public void AColourJitterReachesThePlanAsAPaintPerBristle()
    {
        PathItem path = Open(new Point2D(0, 0), new Point2D(200, 0));
        var spec = new BristleBrushSpec(Count: 5, Randomness: 0.9, ColourJitter: 0.8);
        path.Stroke = path.Stroke with { Brush = Brush(spec) };

        StrokeRenderPlan plan = StrokeOutlineBuilder.Plan(path, path.Stroke);

        IReadOnlyList<ColorRgb>? paints = plan.Paints;
        Assert.NotNull(paints);
        Assert.Equal(plan.Outlines.Count, paints!.Count);

        // The shades really do differ, which is the feature: a jitter of zero would paint every bristle the
        // stroke's own colour and there would be nothing to carry.
        Assert.True(paints.Select(p => Math.Round(p.R, 6)).Distinct().Count() > 1);
        Assert.All(paints, p => Assert.Equal(path.Stroke.Color.A, p.A, 12));
    }
}
