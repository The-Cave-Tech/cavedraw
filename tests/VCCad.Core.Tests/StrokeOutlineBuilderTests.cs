using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// The shared stroke builder: one place that decides what a stroke is drawn as.
///
/// The issue asks for three properties, and each is a test rather than a claim: a plain stroke resolves to the
/// geometry it does today, a profile resolves to the same outline whoever asks, and resolving is **pure** so
/// nothing is applied twice.
/// </summary>
public class StrokeOutlineBuilderTests
{
    private static PathItem Line(params Point2D[] points)
    {
        var path = new PathItem { Name = "line", Fill = FillSpec.None };
        SubPath sub = path.AddSubPath(closed: false);
        foreach (Point2D point in points)
        {
            sub.Nodes.Add(new PathNode(point));
        }

        return path;
    }

    private static StrokeSpec Stroked(WidthProfileSpec? profile = null, double width = 8)
        => new(true, ColorRgb.Black, width, StrokeCap.Butt, StrokeJoin.Miter, 4,
            StrokeAlignment.Center, default, profile);

    /// <summary>
    /// **The compatibility case.** A stroke with no profile is a stroke: it must not become a filled outline,
    /// because a stroked path's caps and joins are the renderer's and a filled region's are not.
    /// </summary>
    [Fact]
    public void APlainStrokeStaysAStrokeAtItsOwnWidth()
    {
        PathItem path = Line(new Point2D(0, 0), new Point2D(100, 0));

        StrokeRenderPlan plan = StrokeOutlineBuilder.Plan(path, Stroked(width: 12));

        Assert.False(plan.IsOutline);
        Assert.Equal(12.0, plan.Width, 6);
        Assert.Empty(plan.Outlines);
    }

    /// <summary>And the scale a renderer asks for is applied in the same place, once.</summary>
    [Fact]
    public void APlainStrokeScalesWithTheRenderer()
    {
        PathItem path = Line(new Point2D(0, 0), new Point2D(100, 0));

        StrokeRenderPlan plan = StrokeOutlineBuilder.Plan(path, Stroked(width: 12), scale: 2.5);

        Assert.False(plan.IsOutline);
        Assert.Equal(30.0, plan.Width, 6);
    }

    /// <summary>A profile makes it an outline, and the path is not consulted for a width.</summary>
    [Fact]
    public void AProfileBecomesAnOutline()
    {
        PathItem path = Line(new Point2D(0, 0), new Point2D(100, 0));

        StrokeRenderPlan plan = StrokeOutlineBuilder.Plan(path, Stroked(WidthProfileSpec.Taper(20, 0)));

        Assert.True(plan.IsOutline);
        Assert.Single(plan.Outlines);
        Assert.Contains(plan.Outlines[0], p => Math.Abs(p.Y - (-10)) < 1e-6);
    }

    /// <summary>An empty profile is not a profile, so it stays a stroke like any other.</summary>
    [Fact]
    public void AnEmptyProfileStaysAStroke()
    {
        PathItem path = Line(new Point2D(0, 0), new Point2D(100, 0));

        StrokeRenderPlan plan = StrokeOutlineBuilder.Plan(
            path, Stroked(new WidthProfileSpec("Empty", Array.Empty<WidthPoint>()), width: 9));

        Assert.False(plan.IsOutline);
        Assert.Equal(9.0, plan.Width, 6);
    }

    /// <summary>
    /// **The double-application test.** Planning is pure: the same input gives the same output, however many
    /// times it is asked, and nothing about the path or the stroke changes.
    ///
    /// This is the shape of the bug the issue warns about. An effect applied during planning that also wrote
    /// back to the model would be applied again on the next render and again on export, and the symptom is an
    /// effect that gets stronger every time the window is redrawn - which is why it is worth a test before any
    /// effect exists to get it wrong with.
    /// </summary>
    [Fact]
    public void PlanningIsPure()
    {
        PathItem path = Line(new Point2D(0, 0), new Point2D(100, 0));
        StrokeSpec stroke = Stroked(WidthProfileSpec.Taper(20, 0), width: 8);
        path.Stroke = stroke;
        int revision = path.GeometryRevision;

        StrokeRenderPlan first = StrokeOutlineBuilder.Plan(path, stroke);
        StrokeRenderPlan second = StrokeOutlineBuilder.Plan(path, stroke);

        Assert.Equal(revision, path.GeometryRevision);
        Assert.Equal(8.0, path.Stroke.Width, 6);
        Assert.Same(stroke, path.Stroke);
        Assert.Equal(first.IsOutline, second.IsOutline);
        Assert.Equal(first.Outlines.Count, second.Outlines.Count);

        for (int i = 0; i < first.Outlines.Count; i++)
        {
            IReadOnlyList<Point2D> a = first.Outlines[i];
            IReadOnlyList<Point2D> b = second.Outlines[i];
            Assert.Equal(a.Count, b.Count);
            for (int j = 0; j < a.Count; j++)
            {
                Assert.Equal(a[j].X, b[j].X, 9);
                Assert.Equal(a[j].Y, b[j].Y, 9);
            }
        }
    }

    /// <summary>
    /// **The agreement test.** The scale a renderer passes applies to the stroke's **widths and not to the
    /// path**. That is the contract that lets one builder serve both: the canvas paints inside the world
    /// transform and passes 1, and the exporter passes the group's scale for the width while its own `toDoc`
    /// matrix places the geometry. A builder that scaled the path as well would double the transform and draw
    /// every profiled stroke at twice its size, in the wrong place.
    /// </summary>
    [Fact]
    public void AProfileOutlineScalesItsWidthAndNotItsPath()
    {
        PathItem path = Line(new Point2D(0, 0), new Point2D(100, 0));
        StrokeSpec stroke = Stroked(WidthProfileSpec.Taper(20, 4));

        IReadOnlyList<Point2D> plain = StrokeOutlineBuilder.Outline(path, stroke)[0];
        IReadOnlyList<Point2D> scaled = StrokeOutlineBuilder.Outline(path, stroke, scale: 2.0)[0];

        Assert.Equal(plain.Count, scaled.Count);

        // Twice as wide...
        Assert.Equal(
            (plain.Max(p => p.Y) - plain.Min(p => p.Y)) * 2.0,
            scaled.Max(p => p.Y) - scaled.Min(p => p.Y),
            6);

        // ...along the same path.
        Assert.Equal(plain.Min(p => p.X), scaled.Min(p => p.X), 6);
        Assert.Equal(plain.Max(p => p.X), scaled.Max(p => p.X), 6);
    }
}
