using VCCad.Core.Model;
using VCCad.Core.Selection;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// Where a path is, for the purpose of being clicked.
///
/// The rule this pins: **a path is only where it is painted**. Inside its fill if it has one, along its
/// stroke if it has that, and nowhere if it has neither. Its bounding box is not the object.
///
/// The bug this came from was measured on a real file: page 1 of the LILLIE sample has an unfilled,
/// stroked, page-sized frame (540x720) as the topmost object in the pattern group, and the hit test used
/// bounding boxes - so the frame swallowed **every click on the page**. The report was "it registers as
/// though I'm clicking the bounding rectangle", and that is exactly what the code did.
/// </summary>
public class PathHitTestTests
{
    private const double Tolerance = 3.0;

    private static PathItem Rectangle(double x, double y, double w, double h)
        => PathFactory.CreateRectangle("r", new Rect2D(x, y, w, h));

    /// <summary>A filled shape is hit inside its fill.</summary>
    [Fact]
    public void AFilledPathIsHitInsideItsFill()
    {
        PathItem path = Rectangle(100, 100, 200, 100);
        path.Fill = FillSpec.Solid(ColorRgb.Black);
        path.Stroke = StrokeSpec.None;

        Assert.True(SelectionEngine.Hits(path, new Point2D(200, 150), Tolerance));
    }

    /// <summary>
    /// **An unfilled, stroked shape is not hit in its interior.** This is the LILLIE frame: page-sized,
    /// no fill, a stroke, and topmost - so treating its box as the object makes the whole page unclickable.
    /// </summary>
    [Fact]
    public void AnUnfilledStrokedPathIsNotHitInItsInterior()
    {
        PathItem path = Rectangle(36, 36, 540, 720);
        path.Fill = FillSpec.None;
        path.Stroke = StrokeSpec.Hairline(ColorRgb.Black);

        Assert.False(SelectionEngine.Hits(path, new Point2D(306, 396), Tolerance),
            "the middle of an unfilled frame is empty space, not the frame");
    }

    /// <summary>Its outline is still clickable - that is what the stroke draws.</summary>
    [Fact]
    public void AnUnfilledStrokedPathIsHitOnItsOutline()
    {
        PathItem path = Rectangle(100, 100, 200, 100);
        path.Fill = FillSpec.None;
        path.Stroke = StrokeSpec.Hairline(ColorRgb.Black);

        Assert.True(SelectionEngine.Hits(path, new Point2D(300, 150), Tolerance), "right edge");
        Assert.True(SelectionEngine.Hits(path, new Point2D(200, 100), Tolerance), "top edge");
        Assert.True(SelectionEngine.Hits(path, new Point2D(100, 200), Tolerance), "bottom-left corner");
    }

    /// <summary>A path with no fill and no stroke is not there at all, even on its own outline.</summary>
    [Fact]
    public void APathWithNoPaintIsNotHitAnywhere()
    {
        PathItem path = Rectangle(100, 100, 200, 100);
        path.Fill = FillSpec.None;
        path.Stroke = StrokeSpec.None;

        Assert.False(SelectionEngine.Hits(path, new Point2D(200, 150), Tolerance), "interior");
        Assert.False(SelectionEngine.Hits(path, new Point2D(300, 150), Tolerance), "edge");
    }

    /// <summary>
    /// A hole is not part of the shape. The pattern pieces are filled rings, so a click in the middle of
    /// one must not select it - and a click on the ring itself must.
    /// </summary>
    [Fact]
    public void AHoleIsNotPartOfTheShape()
    {
        var ring = new PathItem { Name = "ring" };
        ring.SubPaths.Add(ClosedRect(100, 100, 200, 200));
        ring.SubPaths.Add(ClosedRect(150, 150, 100, 100));
        ring.Fill = FillSpec.Solid(ColorRgb.Black, FillRule.EvenOdd);
        ring.Stroke = StrokeSpec.None;

        Assert.False(SelectionEngine.Hits(ring, new Point2D(200, 200), Tolerance), "inside the hole");
        Assert.True(SelectionEngine.Hits(ring, new Point2D(120, 200), Tolerance), "on the ring");
    }

    /// <summary>
    /// The bounding box is not the object. A stroke running corner to corner is not hit in the empty half
    /// of its own box.
    /// </summary>
    [Fact]
    public void TheBoundingBoxIsNotTheObject()
    {
        var line = new PathItem { Name = "line" };
        var open = new SubPath { IsClosed = false };
        open.Nodes.Add(new PathNode(new Point2D(100, 100)));
        open.Nodes.Add(new PathNode(new Point2D(300, 300)));
        line.SubPaths.Add(open);
        line.Fill = FillSpec.None;
        line.Stroke = StrokeSpec.Hairline(ColorRgb.Black);

        Assert.False(SelectionEngine.Hits(line, new Point2D(280, 120), Tolerance),
            "that point is inside the box and nowhere near the line");
        Assert.True(SelectionEngine.Hits(line, new Point2D(200, 200), Tolerance), "on the line");
    }

    /// <summary>Hidden and locked still win over everything above.</summary>
    [Fact]
    public void VisibilityAndLockStillWin()
    {
        PathItem path = Rectangle(100, 100, 200, 100);
        path.Fill = FillSpec.Solid(ColorRgb.Black);
        path.IsVisible = false;

        Assert.False(SelectionEngine.Hits(path, new Point2D(200, 150), Tolerance));

        path.IsVisible = true;
        path.IsLocked = true;
        Assert.False(SelectionEngine.Hits(path, new Point2D(200, 150), Tolerance));
    }

    private static SubPath ClosedRect(double x, double y, double w, double h)
    {
        var sub = new SubPath { IsClosed = true };
        foreach ((double px, double py) in new[] { (x, y), (x + w, y), (x + w, y + h), (x, y + h) })
        {
            sub.Nodes.Add(new PathNode(new Point2D(px, py)));
        }

        return sub;
    }
}
