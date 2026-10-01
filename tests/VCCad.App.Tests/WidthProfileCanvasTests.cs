using VCCad.App.Controls;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// The canvas draws a variable-width stroke as the region it covers, in the right place.
///
/// **Place is the thing worth testing here.** Everything painted under the render pass is already inside the
/// world transform, and path geometry is stored relative to its artboard - so an outline built in path-local
/// coordinates and drawn without the artboard's origin lands somewhere else on the page. That mistake looks
/// exactly like a working feature on an artboard at the origin, which is where every hand-written test tends to
/// put things; hence the test that moves the artboard.
/// </summary>
public class WidthProfileCanvasTests
{
    private static (CadDocument Document, PathItem Path) Host(double artboardX = 0, double artboardY = 0)
    {
        CadDocument document = CadDocument.CreateDefault();
        document.Artboards[0].X = artboardX;
        document.Artboards[0].Y = artboardY;

        var path = new PathItem { Name = "line", Fill = FillSpec.None };
        SubPath sub = path.AddSubPath(closed: false);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(100, 0)));
        document.Artboards[0].Layers[0].AddItem(path);

        return (document, path);
    }

    private static StrokeSpec Stroked(WidthProfileSpec? profile)
        => new(true, ColorRgb.Black, 8, StrokeCap.Butt, StrokeJoin.Miter, 4,
            StrokeAlignment.Center, default, profile);

    [Fact]
    public void TheOutlineIsAsWideAsTheProfile()
    {
        (_, PathItem path) = Host();

        IReadOnlyList<Point2D> points = CanvasWorkspace.ProfileLoops(path, Stroked(WidthProfileSpec.Constant(10)))[0];

        // A constant ten-wide stroke on a horizontal line is five above and five below.
        Assert.Equal(-5.0, points.Min(p => p.Y), 3);
        Assert.Equal(5.0, points.Max(p => p.Y), 3);
        Assert.Equal(0.0, points.Min(p => p.X), 3);
        Assert.Equal(100.0, points.Max(p => p.X), 3);
    }

    /// <summary>
    /// And it is built where the path actually is, not at the origin. The artboard is moved so that the two
    /// answers differ, which is the only way this test can tell them apart.
    /// </summary>
    [Fact]
    public void TheOutlineIsBuiltInArtboardCoordinates()
    {
        (_, PathItem path) = Host(artboardX: 50, artboardY: 70);

        IReadOnlyList<Point2D> points = CanvasWorkspace.ProfileLoops(path, Stroked(WidthProfileSpec.Constant(10)))[0];

        Assert.Equal(50.0, points.Min(p => p.X), 3);
        Assert.Equal(150.0, points.Max(p => p.X), 3);
        Assert.Equal(65.0, points.Min(p => p.Y), 3);
        Assert.Equal(75.0, points.Max(p => p.Y), 3);
    }

    /// <summary>
    /// A taper is narrower at one end than the other, so its extent is the fat end and not the stroke's own
    /// width. The geometry's bounds cannot show the taper, but they can show that it is the profile that
    /// decides - a 20-wide taper is 20 across at its widest, not the stroke's 8.
    /// </summary>
    [Fact]
    public void ATaperReachesItsWidestRatherThanTheStrokeWidth()
    {
        (_, PathItem path) = Host();

        IReadOnlyList<Point2D> points = CanvasWorkspace.ProfileLoops(path, Stroked(WidthProfileSpec.Taper(20, 0)))[0];

        Assert.Equal(-10.0, points.Min(p => p.Y), 3);
        Assert.Equal(10.0, points.Max(p => p.Y), 3);
    }

    /// <summary>An empty profile says nothing, so the stroke's own width is what is drawn.</summary>
    [Fact]
    public void AnEmptyProfileUsesTheStrokeWidth()
    {
        (_, PathItem path) = Host();

        IReadOnlyList<Point2D> points = CanvasWorkspace.ProfileLoops(
            path, Stroked(new WidthProfileSpec("Empty", Array.Empty<WidthPoint>())))[0];

        Assert.Equal(-4.0, points.Min(p => p.Y), 3);
        Assert.Equal(4.0, points.Max(p => p.Y), 3);
    }

    /// <summary>
    /// Each side takes its own width, so a one-sided profile is not symmetric about the centreline. Left is to
    /// the left of travel: on a path drawn to the right, that is upward.
    /// </summary>
    [Fact]
    public void TheTwoSidesUseTheirOwnWidths()
    {
        (_, PathItem path) = Host();
        var profile = new WidthProfileSpec("One-sided", new[]
        {
            new WidthPoint(0.0, LeftWidth: 2, RightWidth: 20),
            new WidthPoint(1.0, LeftWidth: 2, RightWidth: 20),
        });

        IReadOnlyList<Point2D> points = CanvasWorkspace.ProfileLoops(path, Stroked(profile))[0];

        Assert.Equal(-1.0, points.Min(p => p.Y), 3);
        Assert.Equal(10.0, points.Max(p => p.Y), 3);
    }
}
