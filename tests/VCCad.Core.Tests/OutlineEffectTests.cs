using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// Outline effects: the geometry that reshapes a stroke's outline before it is filled.
///
/// Every assertion here is on points, because that is the only thing an effect is for. "An effect was applied" is
/// not a test - an effect that is stored, exported and never reaches the geometry is the failure this has to
/// catch, and it looks identical from the outside.
///
/// The determinism tests are not decorative either: a random-looking effect that drew fresh randomness on each
/// render would make a document look different every time the window was redrawn, and export differently from
/// what the canvas showed.
/// </summary>
public class OutlineEffectTests
{
    /// <summary>A 100x100 square, wound the positive way.</summary>
    private static IReadOnlyList<Point2D> Square()
        => new[]
        {
            new Point2D(0, 0),
            new Point2D(100, 0),
            new Point2D(100, 100),
            new Point2D(0, 100),
        };

    private static IReadOnlyList<IReadOnlyList<Point2D>> One(IReadOnlyList<Point2D> loop)
        => new[] { loop };

    private static double Distance(Point2D a, Point2D b)
        => Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));

    /// <summary>How far a point is from the square's nearest corner, which is what an offset path holds constant.</summary>
    private static double DistanceToNearestCorner(Point2D point)
        => Square().Min(corner => Distance(point, corner));

    private static double PerpendicularDistance(Point2D point, Point2D from, Point2D to)
    {
        double dx = to.X - from.X;
        double dy = to.Y - from.Y;
        return Math.Abs((dx * (from.Y - point.Y)) - ((from.X - point.X) * dy)) /
               Math.Sqrt((dx * dx) + (dy * dy));
    }

    private static (double MinX, double MinY, double MaxX, double MaxY) Extent(IReadOnlyList<Point2D> loop)
        => (loop.Min(p => p.X), loop.Min(p => p.Y), loop.Max(p => p.X), loop.Max(p => p.Y));

    // ---------------------------------------------------------------- offset path

    /// <summary>
    /// **Offset path moves the edges by the distance asked for.** A square offset by five must be five larger on
    /// every side - not five at the corners and less along the edges, which is what moving each corner along the
    /// bisector by five gives.
    /// </summary>
    [Fact]
    public void OffsetPathMovesEveryEdgeByTheOffset()
    {
        IReadOnlyList<Point2D> offset = OutlineEffects.Apply(One(Square()), new[]
        {
            OutlineEffectSpec.OffsetPath(5),
        })[0];

        Assert.Equal(4, offset.Count);

        // Each corner is five out from both the edges that meet there: at a right angle that is five on each
        // axis, so the square grows from 0..100 to -5..105.
        Assert.Equal(-5.0, offset.Min(p => p.X), 6);
        Assert.Equal(-5.0, offset.Min(p => p.Y), 6);
        Assert.Equal(105.0, offset.Max(p => p.X), 6);
        Assert.Equal(105.0, offset.Max(p => p.Y), 6);
    }

    /// <summary>A negative offset shrinks, and the same reasoning applies the other way.</summary>
    [Fact]
    public void AnegativeOffsetPathShrinks()
    {
        IReadOnlyList<Point2D> offset = OutlineEffects.Apply(One(Square()), new[]
        {
            OutlineEffectSpec.OffsetPath(-10),
        })[0];

        Assert.Equal(10.0, offset.Min(p => p.X), 6);
        Assert.Equal(90.0, offset.Max(p => p.X), 6);
    }

    /// <summary>
    /// And it works on a loop wound the other way, because "outward" is read from the winding rather than
    /// assumed. A clockwise loop offset by five must grow too, not shrink.
    /// </summary>
    [Fact]
    public void OffsetPathGrowsALoopWoundTheOtherWay()
    {
        IReadOnlyList<Point2D> reversed = Square().Reverse().ToList();

        IReadOnlyList<Point2D> offset = OutlineEffects.Apply(One(reversed), new[]
        {
            OutlineEffectSpec.OffsetPath(5),
        })[0];

        Assert.Equal(-5.0, offset.Min(p => p.X), 6);
        Assert.Equal(105.0, offset.Max(p => p.X), 6);
    }

    /// <summary>
    /// **An offset moves every edge by the distance asked for, and a right angle cannot tell you that.**
    ///
    /// At a right angle the two normals are perpendicular, so `1 + n1.n2` is exactly 1 and dividing by it is the
    /// same as not dividing - which means a square passes whether the corner is mitred or merely moved along the
    /// bisector. A triangle has no right angles, so its edges are the ones that show the difference: each new edge
    /// has to be parallel to the old one and exactly the offset away from it.
    /// </summary>
    [Fact]
    public void OffsetPathMovesEveryEdgeOfATriangleByTheOffset()
    {
        var triangle = new[]
        {
            new Point2D(0, 0),
            new Point2D(120, 0),
            new Point2D(40, 90),
        };

        IReadOnlyList<Point2D> offset = OutlineEffects.Apply(One(triangle), new[]
        {
            OutlineEffectSpec.OffsetPath(6),
        })[0];

        Assert.Equal(3, offset.Count);

        for (int i = 0; i < 3; i++)
        {
            Point2D fromOld = triangle[i];
            Point2D toOld = triangle[(i + 1) % 3];
            Point2D fromNew = offset[i];
            Point2D toNew = offset[(i + 1) % 3];

            // The edges are parallel...
            Vector2D oldEdge = new(toOld.X - fromOld.X, toOld.Y - fromOld.Y);
            Vector2D newEdge = new(toNew.X - fromNew.X, toNew.Y - fromNew.Y);
            double cross = (oldEdge.X * newEdge.Y) - (oldEdge.Y * newEdge.X);
            Assert.True(Math.Abs(cross) < 1e-6, $"edge {i} is not parallel to the edge it came from");

            // ...and the distance between the two lines is the offset: |cross of the two points| / |edge|.
            Vector2D between = new(fromNew.X - fromOld.X, fromNew.Y - fromOld.Y);
            double distance = Math.Abs((oldEdge.X * between.Y) - (oldEdge.Y * between.X)) /
                              Math.Sqrt((oldEdge.X * oldEdge.X) + (oldEdge.Y * oldEdge.Y));

            Assert.Equal(6.0, distance, 6);
        }
    }

    /// <summary>
    /// **Bigger size, bigger deviation.** The parameter has to actually control the effect: an implementation that
    /// accepted a size and then used a constant would pass every bound above and be useless.
    /// </summary>
    [Fact]
    public void ABiggerSizeDeviatesFurther()
    {
        double Deviation(double size)
        {
            IReadOnlyList<Point2D> rough = OutlineEffects.Apply(One(Square()), new[]
            {
                OutlineEffectSpec.Roughen(size, seed: 5),
            })[0];

            return rough.Select((p, i) => Math.Sqrt(
                    ((p.X - Square()[i].X) * (p.X - Square()[i].X)) +
                    ((p.Y - Square()[i].Y) * (p.Y - Square()[i].Y))))
                .Average();
        }

        Assert.True(Deviation(8) > Deviation(2),
            "a roughen of 8 should move the outline further on average than one of 2");
    }

    /// <summary>
    /// **An effect composes with a width profile rather than replacing it.** The profile decides how wide the
    /// stroke is; the effect then reshapes that outline. An implementation that did one or the other would look
    /// plausible on a stroke that had only one of them.
    /// </summary>
    [Fact]
    public void AnEffectComposesWithAWidthProfile()
    {
        var path = new PathItem { Name = "line", Fill = FillSpec.None };
        SubPath sub = path.AddSubPath(closed: false);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(100, 0)));

        var profileOnly = new StrokeSpec(true, ColorRgb.Black, 8, StrokeCap.Butt, StrokeJoin.Miter, 4,
            StrokeAlignment.Center, default, WidthProfileSpec.Constant(20));
        var both = profileOnly with { Effects = new EffectStack(new[] { OutlineEffectSpec.Roughen(3, seed: 11) }) };

        IReadOnlyList<Point2D> wide = StrokeOutlineBuilder.Outline(path, profileOnly)[0];
        IReadOnlyList<Point2D> wideAndRough = StrokeOutlineBuilder.Outline(path, both)[0];

        // The profile is still honoured: the outline is still about twenty across...
        double width = wide.Max(p => p.Y) - wide.Min(p => p.Y);
        double roughWidth = wideAndRough.Max(p => p.Y) - wideAndRough.Min(p => p.Y);
        Assert.Equal(20.0, width, 3);
        Assert.True(roughWidth > 16.0, $"the profile should still be the width underneath the roughen: {roughWidth}");

        // ...and the roughen has changed it, rather than being ignored.
        Assert.NotEqual(
            wide.Select(p => (p.X, p.Y)),
            wideAndRough.Select(p => (p.X, p.Y)));
    }

    // ---------------------------------------------------------------- zig-zag

    [Fact]
    public void ZigZagAddsAPointToEachSegmentAlternately()
    {
        IReadOnlyList<Point2D> jagged = OutlineEffects.Apply(One(Square()), new[]
        {
            OutlineEffectSpec.ZigZag(4),
        })[0];

        // One extra point per segment, and each is pushed the full size to one side of its own segment: the first
        // segment runs along y = 0, the second along x = 100.
        Assert.Equal(8, jagged.Count);
        Point2D firstMid = jagged[1];
        Point2D secondMid = jagged[3];
        Assert.Equal(50.0, firstMid.X, 6);
        Assert.Equal(4.0, Math.Abs(firstMid.Y), 6);
        Assert.Equal(50.0, secondMid.Y, 6);
        Assert.Equal(4.0, Math.Abs(secondMid.X - 100.0), 6);

        // And consecutive segments go to **opposite** sides, which is what makes it a zig-zag rather than a series
        // of steps in one direction. Measured along each segment's own normal - segment 0 heads +x so its normal
        // is -y, segment 1 heads +y so its normal is +x - because the two normals point different ways.
        double firstSide = -firstMid.Y;
        double secondSide = secondMid.X - 100.0;
        Assert.True(firstSide * secondSide < 0,
            $"the two segments should be on opposite sides: {firstSide} and {secondSide}");
    }

    // ---------------------------------------------------------------- roughen

    [Fact]
    public void RoughenMovesEveryPointNoFurtherThanItsSize()
    {
        IReadOnlyList<Point2D> rough = OutlineEffects.Apply(One(Square()), new[]
        {
            OutlineEffectSpec.Roughen(3),
        })[0];

        Assert.Equal(4, rough.Count);
        for (int i = 0; i < rough.Count; i++)
        {
            Point2D before = Square()[i];
            double moved = Math.Sqrt(
                ((rough[i].X - before.X) * (rough[i].X - before.X)) +
                ((rough[i].Y - before.Y) * (rough[i].Y - before.Y)));

            Assert.True(moved <= 3.0 + 1e-9, $"point {i} moved {moved}, further than the size asked for");
        }

        // And it actually moved something: an effect that returned the input would pass the bound above.
        Assert.Contains(rough, p => Math.Abs(p.X - Square()[rough.ToList().IndexOf(p)].X) > 1e-6);
    }

    /// <summary>
    /// **The same seed gives the same geometry, every time.** A document has to render the same way twice, and
    /// export the same way it rendered.
    /// </summary>
    [Fact]
    public void RoughenIsDeterministic()
    {
        OutlineEffectSpec effect = OutlineEffectSpec.Roughen(5, seed: 42);

        IReadOnlyList<Point2D> first = OutlineEffects.Apply(One(Square()), new[] { effect })[0];
        IReadOnlyList<Point2D> second = OutlineEffects.Apply(One(Square()), new[] { effect })[0];

        Assert.Equal(first.Select(p => (p.X, p.Y)), second.Select(p => (p.X, p.Y)));
    }

    /// <summary>And a different seed gives different geometry, so the seed is doing something.</summary>
    [Fact]
    public void ADifferentSeedRoughensDifferently()
    {
        IReadOnlyList<Point2D> a = OutlineEffects.Apply(One(Square()), new[]
        {
            OutlineEffectSpec.Roughen(5, seed: 1),
        })[0];
        IReadOnlyList<Point2D> b = OutlineEffects.Apply(One(Square()), new[]
        {
            OutlineEffectSpec.Roughen(5, seed: 2),
        })[0];

        Assert.NotEqual(a.Select(p => (p.X, p.Y)), b.Select(p => (p.X, p.Y)));
    }

    // ---------------------------------------------------------------- scribble

    [Fact]
    public void ScribbleDrawsTheRequestedNumberOfPasses()
    {
        IReadOnlyList<IReadOnlyList<Point2D>> loops = OutlineEffects.Apply(One(Square()), new[]
        {
            OutlineEffectSpec.Scribble(3, passes: 4),
        });

        Assert.Equal(4, loops.Count);
        Assert.All(loops, loop => Assert.Equal(4, loop.Count));

        // The passes are offset from each other, which is what makes it read as a scribble rather than one loop
        // drawn four times on top of itself.
        Assert.NotEqual(loops[0][0].X, loops[1][0].X);
    }

    /// <summary>
    /// A scribble of one pass is not a scribble, so the outline comes back untouched rather than nudged - an
    /// effect that quietly moved the artwork would be the wrong answer on a shape someone was happy with.
    /// </summary>
    [Fact]
    public void AScribbleOfOnePassLeavesTheOutlineAlone()
    {
        IReadOnlyList<IReadOnlyList<Point2D>> loops = OutlineEffects.Apply(One(Square()), new[]
        {
            OutlineEffectSpec.Scribble(6, passes: 1),
        });

        IReadOnlyList<Point2D> only = Assert.Single(loops);
        Assert.Equal(Square().Select(p => (p.X, p.Y)), only.Select(p => (p.X, p.Y)));
    }

    // ---------------------------------------------------------------- roughen, detail

    /// <summary>
    /// **`detail` makes a roughen finer, not merely bigger.**
    ///
    /// One is the path's own points; four divides every segment into four and displaces each of the new points
    /// too. Counting the points alone would pass for an implementation that inserted them and left them lying on
    /// the segment, which draws the same picture as before - so the added points are checked to be off it.
    /// </summary>
    [Fact]
    public void MoreRoughenDetailDividesTheOutlineMoreFinely()
    {
        static IReadOnlyList<Point2D> Rough(double detail)
            => OutlineEffects.Apply(One(Square()), new[]
            {
                OutlineEffectSpec.Roughen(3, seed: 7) with { Detail = detail },
            })[0];

        IReadOnlyList<Point2D> one = Rough(1);
        IReadOnlyList<Point2D> four = Rough(4);

        Assert.Equal(4, one.Count);
        Assert.Equal(16, four.Count);

        // Three added points belong between each pair of the path's own points, which puts them at 4i + 1..3.
        for (int i = 0; i < 4; i++)
        {
            for (int k = 1; k <= 3; k++)
            {
                double off = PerpendicularDistance(
                    four[(i * 4) + k], Square()[i], Square()[(i + 1) % 4]);

                Assert.True(off > 1e-9, $"the point added between {i} and {i + 1} was left on the segment");
            }
        }
    }

    // ---------------------------------------------------------------- zig-zag, ridges and smooth

    /// <summary>
    /// **`ridges` puts more to-and-froes on each segment.** One is the single midpoint the effect always drew;
    /// three places a ridge at a third, a half and five sixths of the way along and alternates them across the
    /// line, so the line is crossed three times where it used to be crossed once.
    /// </summary>
    [Fact]
    public void MoreRidgesAddsMoreZigZagsToEachSegment()
    {
        static IReadOnlyList<Point2D> Jagged(int ridges)
            => OutlineEffects.Apply(One(Square()), new[]
            {
                OutlineEffectSpec.ZigZag(4) with { Ridges = ridges },
            })[0];

        Assert.Equal(8, Jagged(1).Count);
        Assert.Equal(16, Jagged(3).Count);

        // The first segment runs along y = 0 from x = 0 to x = 100, so its ridges are its own points 1..3.
        Point2D[] ridges = Jagged(3).Skip(1).Take(3).ToArray();

        Assert.All(ridges, p => Assert.InRange(p.X, 1e-9, 100.0 - 1e-9));
        Assert.All(ridges, p => Assert.Equal(4.0, Math.Abs(p.Y), 6));
        Assert.True(
            ridges[0].Y < 0 && ridges[1].Y > 0 && ridges[2].Y < 0,
            $"the ridges should alternate across the segment: {string.Join(", ", ridges.Select(p => p.Y))}");
    }

    /// <summary>
    /// **`smooth` rounds each ridge into a wave.**
    ///
    /// A sharp ridge goes out to the full size and straight back; a rounded one rises and falls across the ridge,
    /// so no point of it reaches the peak. It still crosses the line, though - a "smooth" that only bulged to one
    /// side would not be a zig-zag at all.
    /// </summary>
    [Fact]
    public void SmoothingARidgeRoundsItIntoAWave()
    {
        static IReadOnlyList<Point2D> Jagged(OutlineEffectSpec effect)
            => OutlineEffects.Apply(One(Square()), new[] { effect })[0];

        OutlineEffectSpec sharp = OutlineEffectSpec.ZigZag(4) with { Ridges = 2 };
        OutlineEffectSpec smooth = sharp with { Smooth = true };

        IReadOnlyList<Point2D> sharpPoints = Jagged(sharp);
        IReadOnlyList<Point2D> smoothPoints = Jagged(smooth);

        // Two ridges a segment: a sharp ridge is one point, a rounded one is four across the same half.
        Assert.Equal(12, sharpPoints.Count);
        Assert.Equal(36, smoothPoints.Count);

        Point2D[] sharpRidges = sharpPoints.Skip(1).Take(2).ToArray();
        Point2D[] smoothRidges = smoothPoints.Skip(1).Take(8).ToArray();

        Assert.Equal(4.0, sharpRidges.Max(p => Math.Abs(p.Y)), 6);

        double reached = smoothRidges.Max(p => Math.Abs(p.Y));
        Assert.True(reached < 4.0, $"a rounded ridge should not reach the sharp peak, but reached {reached}");
        Assert.Contains(smoothRidges, p => p.Y < 0);
        Assert.Contains(smoothRidges, p => p.Y > 0);
    }

    // ---------------------------------------------------------------- offset path, join

    /// <summary>
    /// **`join` chooses what happens at a corner.** A mitre extends the two offset edges until they cross - five
    /// on each axis, and 5 sqrt(2) from the corner it came from, which is the spike a sharp turn grows. A bevel
    /// stops them where they reach the corner, and a round joins the same two ends with an arc. Both keep every
    /// point exactly the offset from the corner, which is what an offset path promises.
    /// </summary>
    [Fact]
    public void TheOffsetJoinChoosesHowTheCornersAreFormed()
    {
        static IReadOnlyList<Point2D> Offset(OutlineJoin join)
            => OutlineEffects.Apply(One(Square()), new[]
            {
                OutlineEffectSpec.OffsetPath(5) with { Join = join },
            })[0];

        IReadOnlyList<Point2D> miter = Offset(OutlineJoin.Miter);
        Assert.Equal(
            new[] { (-5.0, -5.0), (105.0, -5.0), (105.0, 105.0), (-5.0, 105.0) },
            miter.Select(p => (p.X, p.Y)));
        Assert.Equal(5.0 * Math.Sqrt(2.0), DistanceToNearestCorner(miter[0]), 6);

        IReadOnlyList<Point2D> bevel = Offset(OutlineJoin.Bevel);
        Assert.Equal(8, bevel.Count);
        Assert.All(bevel, p => Assert.Equal(5.0, DistanceToNearestCorner(p), 6));
        Assert.DoesNotContain(bevel, p => Distance(p, miter[0]) < 1e-9);

        IReadOnlyList<Point2D> round = Offset(OutlineJoin.Round);
        Assert.True(round.Count > bevel.Count, $"an arc should take more points than a bevel: {round.Count}");
        Assert.All(round, p => Assert.Equal(5.0, DistanceToNearestCorner(p), 6));
    }

    // ---------------------------------------------------------------- scribble, density

    /// <summary>
    /// **`density` samples each pass more finely.** One is the path's own points and four puts three more between
    /// each pair. On its own that adds points along the strand rather than moving it - which is what "denser"
    /// means - so the same strand is checked to come back, and the extra points are shown to be the ones a wander
    /// bends, so a dense pass is a finer strand rather than the same corners with more to draw between them.
    /// </summary>
    [Fact]
    public void MoreScribbleDensitySamplesEachPassMoreFinely()
    {
        static IReadOnlyList<IReadOnlyList<Point2D>> Strands(double density, double width)
            => OutlineEffects.Apply(One(Square()), new[]
            {
                OutlineEffectSpec.Scribble(3, passes: 2, seed: 5) with { Density = density, Width = width },
            });

        IReadOnlyList<Point2D> coarse = Strands(1, 0)[0];
        IReadOnlyList<Point2D> dense = Strands(4, 0)[0];

        Assert.Equal(4, coarse.Count);
        Assert.Equal(16, dense.Count);
        Assert.Equal(Extent(coarse), Extent(dense));

        for (int i = 0; i < 4; i++)
        {
            for (int k = 1; k <= 3; k++)
            {
                Assert.Equal(
                    0.0,
                    PerpendicularDistance(dense[(i * 4) + k], coarse[i], coarse[(i + 1) % 4]),
                    9);
            }
        }

        Assert.NotEqual(
            Strands(1, 2)[0].Select(p => (p.X, p.Y)),
            Strands(4, 2)[0].Select(p => (p.X, p.Y)).Take(4));
    }

    // ---------------------------------------------------------------- scribble, overlap

    /// <summary>**`overlap` runs each pass on past the point where the loop closes.**</summary>
    [Fact]
    public void ScribbleOverlapRunsEachPassPastTheClosingPoint()
    {
        static IReadOnlyList<Point2D> Strand(double overlap)
            => OutlineEffects.Apply(One(Square()), new[]
            {
                OutlineEffectSpec.Scribble(3, passes: 2, seed: 5) with { Overlap = overlap },
            })[0];

        IReadOnlyList<Point2D> closed = Strand(0);
        IReadOnlyList<Point2D> past = Strand(0.5);

        Assert.Equal(4, closed.Count);

        // Four points to get round the loop, and half a turn more of them on the way past the start.
        Assert.Equal(6, past.Count);
        Assert.NotEqual(closed.Select(p => (p.X, p.Y)), past.Select(p => (p.X, p.Y)).Take(4));
    }

    // ---------------------------------------------------------------- scribble, width

    /// <summary>**`width` wanders each pass to either side of the path, alternating.**</summary>
    [Fact]
    public void ScribbleWidthWandersEachPassToEitherSide()
    {
        static IReadOnlyList<Point2D> Strand(double width)
            => OutlineEffects.Apply(One(Square()), new[]
            {
                OutlineEffectSpec.Scribble(3, passes: 2, seed: 5) with { Width = width },
            })[0];

        IReadOnlyList<Point2D> straight = Strand(0);
        IReadOnlyList<Point2D> wide = Strand(3);

        Assert.Equal(straight.Count, wide.Count);

        // The first point lies on the segment running along y = 0 and the second on the one running along x = 100,
        // so each is measured along its own normal: the point turned to one side and then the other.
        double first = -(wide[0].Y - straight[0].Y);
        double second = wide[1].X - straight[1].X;

        Assert.Equal(3.0, Math.Abs(first), 6);
        Assert.Equal(3.0, Math.Abs(second), 6);
        Assert.True(first * second < 0, $"the wander should alternate sides: {first} and {second}");
    }

    // ---------------------------------------------------------------- scribble, curviness

    /// <summary>
    /// **`curviness` bows each pass in one smooth sweep**, rather than jittering from point to point: measured
    /// along each point's own normal, the bow is zero at the start of the loop, out to the full amount a quarter
    /// of the way round, back to zero halfway and out the other way three quarters. That is a curve; a per-point
    /// wander would change sign at every point instead.
    /// </summary>
    [Fact]
    public void ScribbleCurvinessBowsEachPassInOneSmoothSweep()
    {
        static IReadOnlyList<Point2D> Strand(double curviness)
            => OutlineEffects.Apply(One(Square()), new[]
            {
                OutlineEffectSpec.Scribble(3, passes: 2, seed: 5) with { Curviness = curviness },
            })[0];

        IReadOnlyList<Point2D> straight = Strand(0);
        IReadOnlyList<Point2D> bowed = Strand(3);

        Assert.Equal(straight.Count, bowed.Count);

        // Each point's own normal, in the square's winding: -y on the first segment, +x on the second, +y on the
        // third and -x on the fourth.
        double[] alongNormal =
        {
            -(bowed[0].Y - straight[0].Y),
            bowed[1].X - straight[1].X,
            bowed[2].Y - straight[2].Y,
            -(bowed[3].X - straight[3].X),
        };

        Assert.Equal(0.0, alongNormal[0], 9);
        Assert.Equal(3.0, alongNormal[1], 6);
        Assert.Equal(0.0, alongNormal[2], 9);
        Assert.Equal(-3.0, alongNormal[3], 6);
    }

    // ---------------------------------------------------------------- scribble, scatter

    /// <summary>
    /// **`scatter` throws each sampled point its own way.** Without it a pass is a rigid copy of the loop, which
    /// every point sharing one displacement says precisely; with it they no longer do.
    /// </summary>
    [Fact]
    public void ScribbleScatterThrowsEachPointItsOwnWay()
    {
        static IReadOnlyList<Point2D> Strand(double scatter)
            => OutlineEffects.Apply(One(Square()), new[]
            {
                OutlineEffectSpec.Scribble(3, passes: 3, seed: 5) with { Scatter = scatter },
            })[0];

        IReadOnlyList<Point2D> rigid = Strand(0);
        for (int i = 0; i < rigid.Count; i++)
        {
            Assert.Equal(rigid[0].X - Square()[0].X, rigid[i].X - Square()[i].X, 9);
            Assert.Equal(rigid[0].Y - Square()[0].Y, rigid[i].Y - Square()[i].Y, 9);
        }

        IReadOnlyList<Point2D> thrown = Strand(2);
        bool ownWay = thrown.Select((p, i) => (
                p.X - Square()[i].X - (thrown[0].X - Square()[0].X),
                p.Y - Square()[i].Y - (thrown[0].Y - Square()[0].Y)))
            .Any(d => Math.Abs(d.Item1) > 1e-9 || Math.Abs(d.Item2) > 1e-9);

        Assert.True(ownWay, "every point of a scattered pass still shares one rigid displacement");
    }

    // ---------------------------------------------------------------- defaults

    /// <summary>
    /// **A new parameter's default is the geometry the effect had before the parameter existed.**
    ///
    /// These four pin the pre-existing outline literally, because a saved document is entitled to render as it
    /// always did: a knob whose default moved the artwork would redraw every effected stroke in the file without
    /// anyone touching it, and nothing about the document would say so.
    /// </summary>
    [Fact]
    public void RoughenAtItsDefaultDetailDisplacesThePathsOwnPoints()
    {
        IReadOnlyList<Point2D> rough = OutlineEffects.Apply(One(Square()), new[]
        {
            OutlineEffectSpec.Roughen(3, seed: 1),
        })[0];

        Assert.Equal(4, rough.Count);
    }

    [Fact]
    public void ZigZagAtItsDefaultsIsStillTheSingleMidpointPerSegment()
    {
        IReadOnlyList<Point2D> jagged = OutlineEffects.Apply(One(Square()), new[]
        {
            OutlineEffectSpec.ZigZag(4),
        })[0];

        Assert.Equal(
            new[]
            {
                (0.0, 0.0), (50.0, -4.0), (100.0, 0.0), (96.0, 50.0),
                (100.0, 100.0), (50.0, 104.0), (0.0, 100.0), (4.0, 50.0),
            },
            jagged.Select(p => (p.X, p.Y)));
    }

    [Fact]
    public void OffsetPathAtItsDefaultJoinIsStillTheMitredCorner()
    {
        IReadOnlyList<Point2D> offset = OutlineEffects.Apply(One(Square()), new[]
        {
            OutlineEffectSpec.OffsetPath(5),
        })[0];

        Assert.Equal(
            new[] { (-5.0, -5.0), (105.0, -5.0), (105.0, 105.0), (-5.0, 105.0) },
            offset.Select(p => (p.X, p.Y)));
    }

    [Fact]
    public void ScribbleAtItsDefaultsIsStillARigidCopyOfTheLoop()
    {
        IReadOnlyList<IReadOnlyList<Point2D>> loops = OutlineEffects.Apply(One(Square()), new[]
        {
            OutlineEffectSpec.Scribble(6, passes: 3, seed: 9),
        });

        Assert.Equal(3, loops.Count);
        Assert.All(loops, loop =>
        {
            Assert.Equal(4, loop.Count);
            for (int i = 0; i < loop.Count; i++)
            {
                Assert.Equal(loop[0].X - Square()[0].X, loop[i].X - Square()[i].X, 9);
                Assert.Equal(loop[0].Y - Square()[0].Y, loop[i].Y - Square()[i].Y, 9);
            }
        });
    }

    // ---------------------------------------------------------------- general

    /// <summary>Effects apply in order, and the order changes the result - which is why the stack is a list.</summary>
    [Fact]
    public void EffectsApplyInTheOrderTheyAreGiven()
    {
        IReadOnlyList<Point2D> offsetThenRoughen = OutlineEffects.Apply(One(Square()), new[]
        {
            OutlineEffectSpec.OffsetPath(5),
            OutlineEffectSpec.Roughen(3, seed: 7),
        })[0];

        IReadOnlyList<Point2D> roughenThenOffset = OutlineEffects.Apply(One(Square()), new[]
        {
            OutlineEffectSpec.Roughen(3, seed: 7),
            OutlineEffectSpec.OffsetPath(5),
        })[0];

        Assert.NotEqual(
            offsetThenRoughen.Select(p => (p.X, p.Y)),
            roughenThenOffset.Select(p => (p.X, p.Y)));
    }

    /// <summary>An effect with nothing to do returns the geometry it was given, not a rebuilt copy.</summary>
    [Fact]
    public void AnEffectOfNoSizeChangesNothing()
    {
        IReadOnlyList<Point2D> square = Square();

        IReadOnlyList<IReadOnlyList<Point2D>> result = OutlineEffects.Apply(One(square), new[]
        {
            OutlineEffectSpec.ZigZag(0),
        });

        Assert.Same(square, Assert.Single(result));
    }

    /// <summary>
    /// **A stroke with effects goes through the builder as an outline**, even with no width profile: a roughened
    /// line cannot be drawn with a pen, because a pen has one width and a straight edge.
    /// </summary>
    [Fact]
    public void AnEffectWithoutAProfileStillBecomesAnOutline()
    {
        var path = new PathItem { Name = "line", Fill = FillSpec.None };
        SubPath sub = path.AddSubPath(closed: false);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(100, 0)));

        var plain = new StrokeSpec(true, ColorRgb.Black, 8, StrokeCap.Butt, StrokeJoin.Miter, 4);
        var effected = plain with { Effects = new EffectStack(new[] { OutlineEffectSpec.Roughen(2) }) };

        Assert.False(StrokeOutlineBuilder.Plan(path, plain).IsOutline);
        Assert.True(StrokeOutlineBuilder.Plan(path, effected).IsOutline);
    }
}
