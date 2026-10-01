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
