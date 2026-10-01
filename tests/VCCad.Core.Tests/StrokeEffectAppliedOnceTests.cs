using VCCad.Core.Model;
using VCCad.Core.Serialization;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// **An outline effect is applied once.**
///
/// This is the failure the shared builder exists to make impossible, and it is worth its own test because it is
/// invisible from the outside. An effect that is applied during planning *and* written back into the model - an
/// offset baked into the effect's own size, a roughen re-seeded from its own output, a plan cached and planned
/// over again - looks correct the first time it is asked and wrong every time after: the outline grows a little
/// on each redraw, and the exported page no longer matches the canvas the person approved. Nothing about the
/// model says so, which is why the assertion has to be on geometry, across more than one plan.
///
/// The three plans below are the three occasions a render really happens: the canvas drawing it, the next frame
/// drawing it again, and the exporter drawing the document after it has been saved and read back.
/// </summary>
public class StrokeEffectAppliedOnceTests
{
    private static PathItem Line()
    {
        var path = new PathItem { Name = "line", Fill = FillSpec.None };
        SubPath sub = path.AddSubPath(closed: false);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(100, 0)));
        return path;
    }

    /// <summary>
    /// A twenty-wide band, offset six outward and then roughened. Planned twice on the same stroke and once more
    /// after a save and reload, the geometry must be the same three times - and the band must still be the
    /// twenty-plus-six-twice-over that one application gives, not the twenty-plus-twelve that two would.
    ///
    /// The extent assertion and the repeated plans catch different things. Planning the same object again catches
    /// **state that accumulates between calls**: a model written back to, a cached plan mutated in place. The
    /// extent catches an effect applied twice **within one plan**, which every plan would then agree about and
    /// the equality alone would happily accept.
    /// </summary>
    [Fact]
    public void AnEffectIsAppliedOnceHoweverOftenTheStrokeIsPlanned()
    {
        CadDocument document = CadDocument.CreateDefault();
        PathItem path = Line();
        document.Artboards[0].Layers[0].AddItem(path);

        path.Stroke = new StrokeSpec(
            true,
            new ColorRgb(0, 0, 0),
            8,
            StrokeCap.Butt,
            StrokeJoin.Miter,
            4,
            StrokeAlignment.Center,
            default,
            WidthProfileSpec.Constant(20),
            new EffectStack(new[]
            {
                OutlineEffectSpec.OffsetPath(6),
                OutlineEffectSpec.Roughen(2, seed: 4),
            }));

        StrokeRenderPlan first = StrokeOutlineBuilder.Plan(path, path.Stroke);

        // The canvas's next frame: the same stroke, planned again.
        StrokeRenderPlan second = StrokeOutlineBuilder.Plan(path, path.Stroke);

        // The exporter's view: the document saved, read back, and planned from the revived model.
        CadDocument reloaded = VccadDocumentSerializer.Deserialize(
            VccadDocumentSerializer.SerializeToBytes(document));
        PathItem revived = reloaded.Artboards[0].Layers[0].Children.OfType<PathItem>().Single();
        StrokeRenderPlan afterReload = StrokeOutlineBuilder.Plan(revived, revived.Stroke);

        AssertSameGeometry(first, second);
        AssertSameGeometry(first, afterReload);

        // And a hundred redraws later it is still the first plan's geometry, not the hundredth application's.
        for (int i = 0; i < 100; i++)
        {
            AssertSameGeometry(first, StrokeOutlineBuilder.Plan(path, path.Stroke));
        }

        // The band is 20 across, the offset moves each edge 6 further out, and the roughen moves any one vertex
        // by at most 2: 32 ± 4. A second offset would leave it 44 across before roughening, so this bound is what
        // fails if the effect is applied twice in the one plan.
        double height = first.Outlines[0].Max(p => p.Y) - first.Outlines[0].Min(p => p.Y);
        Assert.True(height <= 36.0 + 1e-9, $"the offset appears to have compounded: the band is {height} across");

        // Planning changed nothing about the model, which is the property that makes the plans above comparable
        // at all: two effects, still an offset of six, still a twenty-wide profile of two points.
        Assert.Equal(2, path.Stroke.AllEffects.Count);
        Assert.Equal(6.0, path.Stroke.AllEffects[0].Size, 9);
        Assert.Equal(20.0, path.Stroke.WidthProfile!.Points[0].LeftWidth, 9);
        Assert.Equal(2, path.Stroke.WidthProfile.Points.Count);
    }

    /// <summary>The geometry equality the test above rests on, stated once so it cannot drift.</summary>
    private static void AssertSameGeometry(StrokeRenderPlan expected, StrokeRenderPlan actual)
    {
        Assert.Equal(expected.IsOutline, actual.IsOutline);
        Assert.True(expected.IsOutline, "this stroke must resolve to an outline for the comparison to mean anything");
        Assert.Equal(expected.Outlines.Count, actual.Outlines.Count);

        for (int i = 0; i < expected.Outlines.Count; i++)
        {
            IReadOnlyList<Point2D> a = expected.Outlines[i];
            IReadOnlyList<Point2D> b = actual.Outlines[i];
            Assert.Equal(a.Count, b.Count);
            for (int j = 0; j < a.Count; j++)
            {
                Assert.Equal(a[j].X, b[j].X, 9);
                Assert.Equal(a[j].Y, b[j].Y, 9);
            }
        }
    }
}
