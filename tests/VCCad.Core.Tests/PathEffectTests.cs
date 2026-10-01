using VCCad.Core.Model;
using VCCad.Core.Serialization;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// Inkscape's live path effects, as the stroke subsystem's variable-width case (issue #130).
///
/// Inkscape does not stroke a powerstroke: it **replaces the stroke with the region the profile covers**, which is
/// the same thing this repository's <see cref="WidthProfileSpec"/> does. So the work is a translation - read the
/// effect's own parameters into width points along the path - and then the existing outline machinery draws it.
///
/// Two things are asserted here rather than "the effect was applied". The **widths** are checked against the
/// effect's own numbers at named positions, because a translation that got the halves or the `scale_width` wrong
/// would still produce a profile. And the **outline** is checked for simplicity at a join, because that is where
/// offsetting a path goes wrong: the inner side of a sharp corner is where a naive offset crosses itself and a
/// filled loop becomes a bow-tie.
/// </summary>
public class PathEffectTests
{
    // ------------------------------------------------------- the translation

    /// <summary>
    /// One of Inkscape's own powerstroke elements, parameter for parameter, as the corpus files carry them.
    /// </summary>
    private static PathEffectSpec PowerStroke(params (string Name, string Value)[] overrides)
    {
        var parameters = new List<KeyValuePair<string, string>>
        {
            new("is_visible", "true"),
            new("offset_points", "0,2 | 3,5 | 8,1"),
            new("not_jump", "false"),
            new("sort_points", "true"),
            new("interpolator_type", "CubicBezierSmooth"),
            new("interpolator_beta", "0.2"),
            new("start_linecap_type", "zerowidth"),
            new("linejoin_type", "extrp_arc"),
            new("miter_limit", "4"),
            new("scale_width", "1"),
            new("end_linecap_type", "zerowidth"),
        };

        foreach ((string name, string value) in overrides)
        {
            parameters.RemoveAll(pair => pair.Key == name);
            parameters.Add(new KeyValuePair<string, string>(name, value));
        }

        return new PathEffectSpec("powerstroke", "path-effect1", "1.4", parameters);
    }

    /// <summary>An open path with the given number of straight segments, so the curve axis is known exactly.</summary>
    private static PathItem Segments(int count)
    {
        var points = new Point2D[count + 1];
        for (int i = 0; i <= count; i++)
        {
            points[i] = new Point2D(i * 10, 0);
        }

        return Path(false, points);
    }

    private static StrokeSpec Plain()
        => new(true, ColorRgb.Black, 1, StrokeCap.Butt, StrokeJoin.Miter, 4);

    /// <summary>
    /// **The widths are the effect's own numbers, at the effect's own positions.**
    ///
    /// Inkscape stores a powerstroke knot as the **offset from the centreline** and builds the outline by moving
    /// the path out by it and in by it, so the drawn width is twice the stored value. On this eight-segment path
    /// the knots at 0, 3 and 8 sit at 0, 0.375 and 1 - the position axis is segments, not arc length, which is the
    /// one thing about this translation that is easy to get wrong and invisible when you do.
    /// </summary>
    [Fact]
    public void APowerStrokeBecomesWidthPointsAtTheEffectsOwnPositions()
    {
        PathEffectTranslation translation = PathEffects.Translate(PowerStroke(), Segments(8), Plain());

        Assert.True(translation.IsSupported, translation.Refusal);
        WidthProfileSpec profile = translation.Stroke!.WidthProfile!;

        Assert.Equal(3, profile.Points.Count);

        Assert.Equal(0.0, profile.Points[0].Position, 9);
        Assert.Equal(4.0, profile.Points[0].LeftWidth, 9);
        Assert.Equal(4.0, profile.Points[0].RightWidth, 9);

        Assert.Equal(0.375, profile.Points[1].Position, 9);
        Assert.Equal(10.0, profile.Points[1].LeftWidth, 9);
        Assert.Equal(10.0, profile.Points[1].RightWidth, 9);

        Assert.Equal(1.0, profile.Points[2].Position, 9);
        Assert.Equal(2.0, profile.Points[2].LeftWidth, 9);

        // The half-widths the outline is actually built from, at a knot and half way between two of them. The
        // halfway point is only pinned because the interpolation is pinned: a cubic ease is exactly half way at
        // half way, which is what makes the number checkable rather than merely plausible.
        Assert.Equal((2.0, 2.0), profile.HalvesAt(0.0)!.Value);
        Assert.Equal((5.0, 5.0), profile.HalvesAt(0.375)!.Value);
        Assert.Equal(3.5, profile.HalvesAt(0.1875)!.Value.Left, 9);
        Assert.Equal(1.0, profile.HalvesAt(1.0)!.Value.Left, 9);
    }

    /// <summary>`scale_width` scales every knot's offset, and only that: it is the effect's width multiplier.</summary>
    [Fact]
    public void ScaleWidthMultipliesEveryWidth()
    {
        PathEffectTranslation translation =
            PathEffects.Translate(PowerStroke(("scale_width", "2.5")), Segments(8), Plain());

        WidthProfileSpec profile = translation.Stroke!.WidthProfile!;

        // 2 * 2 * 2.5, 2 * 5 * 2.5 and 2 * 1 * 2.5.
        Assert.Equal(10.0, profile.Points[0].LeftWidth, 9);
        Assert.Equal(25.0, profile.Points[1].LeftWidth, 9);
        Assert.Equal(5.0, profile.Points[2].LeftWidth, 9);
    }

    /// <summary>
    /// **The parameterisation is the path's, so the same effect on a longer path lands in different places.**
    ///
    /// The knots are stored as a segment index, so a knot at 4 of 8 is halfway along a nine-node path and a third
    /// of the way along a thirteen-node one. A translation that read them as fractions of the path directly would
    /// pass the test above on this exact path and be wrong on every other.
    /// </summary>
    [Fact]
    public void TheSameKnotsLandElsewhereOnALongerPath()
    {
        WidthProfileSpec eight = PathEffects
            .Translate(PowerStroke(("offset_points", "4,2")), Segments(8), Plain()).Stroke!.WidthProfile!;
        WidthProfileSpec twelve = PathEffects
            .Translate(PowerStroke(("offset_points", "4,2")), Segments(12), Plain()).Stroke!.WidthProfile!;

        Assert.Equal(0.5, eight.Points[0].Position, 9);
        Assert.Equal(1.0 / 3.0, twelve.Points[0].Position, 9);
    }

    /// <summary>
    /// **A closed subpath has one more segment than it has gaps between nodes**, which is what makes its last
    /// node join its first. Counting nodes instead puts every knot one segment too far along, and the profile is
    /// then skewed rather than obviously wrong.
    /// </summary>
    [Fact]
    public void AClosedPathCountsItsClosingSegment()
    {
        PathItem square = Path(
            true, new Point2D(0, 0), new Point2D(100, 0), new Point2D(100, 100), new Point2D(0, 100));
        PathItem open = Path(
            false, new Point2D(0, 0), new Point2D(100, 0), new Point2D(100, 100), new Point2D(0, 100));

        Assert.Equal(4, PathEffects.CurveCount(square));
        Assert.Equal(3, PathEffects.CurveCount(open));

        WidthProfileSpec closed = PathEffects
            .Translate(PowerStroke(("offset_points", "2,3")), square, Plain()).Stroke!.WidthProfile!;

        Assert.Equal(0.5, closed.Points[0].Position, 9);
    }

    /// <summary>
    /// **The join is the file's, and an extrapolated arc is a mitre.** The corpus's powerstroke join case asks for
    /// <c>extrp_arc</c>, so a translation that left the stroke's own join in place would draw the corner as
    /// whatever the model happened to default to.
    /// </summary>
    [Fact]
    public void TheJoinAndCapsComeFromTheEffect()
    {
        StrokeSpec round = PathEffects
            .Translate(PowerStroke(("linejoin_type", "round")), Segments(8), Plain()).Stroke!;
        Assert.Equal(StrokeJoin.Round, round.Join);

        StrokeSpec arc = PathEffects
            .Translate(PowerStroke(("linejoin_type", "extrp_arc")), Segments(8), Plain()).Stroke!;
        Assert.Equal(StrokeJoin.Miter, arc.Join);

        StrokeSpec square = PathEffects
            .Translate(
                PowerStroke(("start_linecap_type", "square"), ("end_linecap_type", "square")),
                Segments(8), Plain()).Stroke!;
        Assert.Equal(StrokeCap.Square, square.Cap);
    }

    /// <summary>
    /// **An effect this build does not implement is refused by name, and the geometry is left alone.**
    ///
    /// The refusal carries no stroke at all, which is the point: the path keeps the geometry the file drew, and
    /// nothing here redraws it as though the effect were not there. Inkscape's <c>bend_path</c> is the case - the
    /// corpus is full of effects that are not powerstrokes, and a reader that quietly dropped them would show a
    /// drawing that looks deliberate and is not the file's.
    /// </summary>
    [Fact]
    public void AnUnknownEffectIsRefusedByNameAndLeavesTheGeometryAlone()
    {
        var bend = new PathEffectSpec(
            "bend_path", "path-effect122389-7", "1.4",
            new[]
            {
                new KeyValuePair<string, string>("effect", "bend_path"),
                new KeyValuePair<string, string>("bendpath", "m 1121.5,272.7 c 42.6,20.1 85.6,34.1 131.5,0"),
            });

        PathEffectTranslation translation = PathEffects.Translate(bend, Segments(8), Plain());

        Assert.False(translation.IsSupported);
        Assert.Null(translation.Stroke);
        Assert.Contains("bend_path", translation.Refusal!);
        Assert.Contains("powerstroke", translation.Refusal!);
    }

    /// <summary>
    /// A profile is read **per subpath**, from 0 to 1, so knots spread over several subpaths are asking for a
    /// different profile on each of them - a shape the model's single profile per stroke cannot hold. Refused and
    /// named rather than drawn with the first subpath's widths on every one of them.
    /// </summary>
    [Fact]
    public void AKnotOnASecondSubpathIsRefusedRatherThanFlattened()
    {
        PathItem two = Path(false, new Point2D(0, 0), new Point2D(10, 0));
        SubPath second = two.AddSubPath(false);
        second.Nodes.Add(new PathNode(new Point2D(0, 20)));
        second.Nodes.Add(new PathNode(new Point2D(10, 20)));

        // One knot on each subpath, which is exactly what Inkscape writes for a compound powerstroke.
        PathEffectTranslation translation = PathEffects
            .Translate(PowerStroke(("offset_points", "0,2 | 2,5")), two, Plain());

        Assert.False(translation.IsSupported);
        Assert.Contains("subpath", translation.Refusal!);
    }

    /// <summary>
    /// A knot the path is not long enough to carry is refused rather than clamped to the end, where it would put
    /// a width the file asked for at a place the file did not ask for it.
    /// </summary>
    [Fact]
    public void AKnotOffTheEndOfThePathIsRefused()
    {
        PathEffectTranslation translation = PathEffects
            .Translate(PowerStroke(("offset_points", "0,2 | 12,5")), Segments(8), Plain());

        Assert.False(translation.IsSupported);
        Assert.Contains("off the path", translation.Refusal!);
    }

    /// <summary>
    /// A smooth interpolator is one the model cannot reproduce exactly, so it is **reported** rather than silently
    /// swapped - the widths at the knots are the file's own either way, and only the run between them differs.
    /// </summary>
    [Fact]
    public void ASmoothInterpolatorIsReportedRatherThanSilentlySwapped()
    {
        PathEffectTranslation translation =
            PathEffects.Translate(PowerStroke(("interpolator_type", "CentripetalCatmullRom")), Segments(8), Plain());

        Assert.True(translation.IsSupported);
        Assert.Contains(translation.Notes, note => note.Contains("CentripetalCatmullRom"));

        // And the linear case is not reported, because nothing is being substituted.
        PathEffectTranslation linear =
            PathEffects.Translate(PowerStroke(("interpolator_type", "Linear")), Segments(8), Plain());
        Assert.Empty(linear.Notes);
        Assert.All(linear.Stroke!.WidthProfile!.Points, p => Assert.Equal(WidthInterpolation.Linear, p.Interpolation));
    }

    /// <summary>
    /// **The description the file left on the path survives the sidecar round trip**, which is what keeps the
    /// effect's identity in the document after the effect has been translated for drawing. Without it the file
    /// would come back having lost the reason its stroke looks like that.
    /// </summary>
    [Fact]
    public void TheEffectReferenceAndOriginalPathSurviveTheSidecar()
    {
        PathItem path = Segments(8);
        path.ForeignAttributes["inkscape:path-effect"] = "#path-effect1";
        path.ForeignAttributes["inkscape:original-d"] = "M 0,0 L 80,0";

        CadDocument document = CadDocument.CreateDefault();
        document.Artboards[0].Layers[0].AddItem(path);

        CadDocument reloaded = VccadDocumentSerializer.Deserialize(
            VccadDocumentSerializer.SerializeToBytes(document));
        var restored = (PathItem)reloaded.Artboards[0].Layers[0].Children.Single();

        Assert.Equal("path-effect1", PathEffects.ReferenceOn(restored));
        Assert.Equal("M 0,0 L 80,0", PathEffects.SourcePathData(restored));
    }

    // ------------------------------------------------------------ the join

    private static PathItem Path(bool closed, params Point2D[] points)
    {
        var path = new PathItem { Name = "scratch", Fill = FillSpec.None };
        SubPath sub = path.AddSubPath(closed);
        foreach (Point2D point in points)
        {
            sub.Nodes.Add(new PathNode(point));
        }

        return path;
    }

    private static IReadOnlyList<Point2D> Outline(PathItem path, double width, StrokeJoin join = StrokeJoin.Miter)
        => Assert.Single(StrokeOutlineBuilder.Plan(
            path,
            new StrokeSpec(true, ColorRgb.Black, width, StrokeCap.Butt, join, 4,
                StrokeAlignment.Center, default, WidthProfileSpec.Constant(width))).Outlines);

    /// <summary>
    /// Whether a closed loop crosses itself, which is exactly what a bow-tie is.
    ///
    /// Adjacent edges share a vertex and a closed loop's first and last edges share one too, so those pairs are
    /// skipped - the question is whether two edges that are not joined meet anywhere.
    /// </summary>
    private static bool SelfIntersects(IReadOnlyList<Point2D> loop)
    {
        int n = loop.Count;
        for (int i = 0; i < n; i++)
        {
            for (int j = i + 1; j < n; j++)
            {
                if (j == i || j == (i + 1) % n || i == (j + 1) % n)
                {
                    continue;
                }

                if (SegmentsCross(loop[i], loop[(i + 1) % n], loop[j], loop[(j + 1) % n]))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool SegmentsCross(Point2D a, Point2D b, Point2D c, Point2D d)
    {
        double d1 = Cross(c, d, a);
        double d2 = Cross(c, d, b);
        double d3 = Cross(a, b, c);
        double d4 = Cross(a, b, d);

        return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0))
            && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
    }

    private static double Cross(Point2D o, Point2D a, Point2D b)
        => ((a.X - o.X) * (b.Y - o.Y)) - ((a.Y - o.Y) * (b.X - o.X));

    private static double Distance(Point2D a, Point2D b)
        => Math.Sqrt(((b.X - a.X) * (b.X - a.X)) + ((b.Y - a.Y) * (b.Y - a.Y)));

    /// <summary>
    /// **The inner side of a sharp corner is the intersection of the two offset edges, and there is no miter
    /// limit on it.**
    ///
    /// The miter limit exists to stop the *outer* corner growing a needle. Applied to the inner side as well it
    /// replaces a corner that is legitimately far away with one a half-width from the vertex, and the two inner
    /// edges then have to travel *past* it and back - so the loop crosses itself. Inkscape's powerstroke join test
    /// is this exact case, and a bow-tie is what a filled loop with a crossing looks like once it is painted.
    /// </summary>
    [Fact]
    public void ASharpCornerDoesNotCrossItselfOnTheInnerSide()
    {
        // Nearly doubled back: a 174-degree turn, which is past any miter limit and is precisely where the inner
        // corner's true position and the bevel's differ most.
        IReadOnlyList<Point2D> loop = Outline(
            Path(false, new Point2D(0, 0), new Point2D(100, 0), new Point2D(0, 10)), 10);

        Assert.False(SelfIntersects(loop), "the outline of a stroked sharp corner crosses itself");
    }

    /// <summary>
    /// The inner corner sits where the two offset edges actually meet: half the width over the cosine of half the
    /// turn, from the vertex, on the far side of the corner from the outer mitre. For this path that is about
    /// twenty half-widths out, against the one a miter-limited bevel stops at - so the two answers are not close,
    /// and the assertion can be tight.
    /// </summary>
    [Fact]
    public void TheInnerCornerIsWhereTheTwoOffsetEdgesMeet()
    {
        var vertex = new Point2D(100, 0);
        IReadOnlyList<Point2D> loop = Outline(
            Path(false, new Point2D(0, 0), vertex, new Point2D(0, 10)), 10);

        Vector2D incoming = new Point2D(100, 0) - new Point2D(0, 0);
        Vector2D outgoing = new Point2D(0, 10) - vertex;
        double turn = Math.Acos(
            ((incoming.X * outgoing.X) + (incoming.Y * outgoing.Y)) / (Length(incoming) * Length(outgoing)));
        double expected = 5.0 / Math.Cos(turn / 2.0);

        // The outer mitre runs along the bisector of the two **left** offset normals; the inner corner is the same
        // distance the other way. Naming the direction rather than searching for "a far point" keeps the test
        // about the corner instead of about where a point happens to sit in the loop.
        Vector2D leftA = Normalise(new Vector2D(incoming.Y, -incoming.X));
        Vector2D leftB = Normalise(new Vector2D(outgoing.Y, -outgoing.X));
        Vector2D u = Normalise(leftA + leftB);
        var spot = new Point2D(vertex.X - (u.X * expected), vertex.Y - (u.Y * expected));

        Point2D nearest = loop.OrderBy(p => Distance(p, spot)).First();
        Assert.True(
            Distance(nearest, spot) < 1e-6,
            $"the inner corner should be at {spot.X},{spot.Y} ({expected} from the vertex), nearest is " +
            $"{nearest.X},{nearest.Y}");
        Assert.Equal(expected, Distance(nearest, vertex), 6);
    }

    private static Vector2D Normalise(Vector2D v)
    {
        double length = Length(v);
        return length < 1e-12 ? new Vector2D(0, 0) : new Vector2D(v.X / length, v.Y / length);
    }

    private static double Length(Vector2D v) => Math.Sqrt((v.X * v.X) + (v.Y * v.Y));
}
