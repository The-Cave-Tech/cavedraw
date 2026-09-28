using VCCad.Core.Model;
using VCCad.Geometry;
using VCCad.Pdf;
using Xunit;

namespace VCCad.Pdf.Tests;

/// <summary>
/// Clipping to a rectangle, as a PDF content stream's W/W* operator requires.
///
/// This is what a paginated pattern depends on: the artwork is drawn at the correct
/// scale and position but straddles the sheet edges, and the clip is what limits it
/// to the sheet.
/// </summary>
public class PathClipperTests
{
    private static SubPath Rectangle(double x, double y, double w, double h)
    {
        var subPath = new SubPath { IsClosed = true };
        subPath.AppendNode(new Point2D(x, y));
        subPath.AppendNode(new Point2D(x + w, y));
        subPath.AppendNode(new Point2D(x + w, y + h));
        subPath.AppendNode(new Point2D(x, y + h));
        return subPath;
    }

    private static SubPath Polyline(params (double X, double Y)[] points)
    {
        var subPath = new SubPath();
        foreach ((double x, double y) in points)
        {
            subPath.AppendNode(new Point2D(x, y));
        }

        return subPath;
    }

    [Fact]
    public void GeometryInsideTheClipIsReturnedUntouched()
    {
        // Fidelity rule: the vast majority of content must keep its original curves
        // and node structure, so clipping must not rewrite what it does not cut.
        SubPath inside = Rectangle(10, 10, 50, 50);
        var clip = new Rect2D(0, 0, 100, 100);

        List<SubPath> result = PathClipper.Clip(new[] { inside }, clip);

        Assert.Single(result);
        Assert.Same(inside, result[0]);
    }

    [Fact]
    public void GeometryEntirelyOutsideTheClipIsDropped()
    {
        SubPath outside = Rectangle(200, 200, 50, 50);

        List<SubPath> result = PathClipper.Clip(new[] { outside }, new Rect2D(0, 0, 100, 100));

        Assert.Empty(result);
    }

    [Fact]
    public void StraddlingRectangleIsCutToTheVisiblePart()
    {
        // A 100x100 square centred on the clip's right edge: only half is visible.
        SubPath straddling = Rectangle(50, 0, 100, 100);
        var clip = new Rect2D(0, 0, 100, 100);

        List<SubPath> result = PathClipper.Clip(new[] { straddling }, clip);

        Assert.Single(result);
        Rect2D bounds = result[0].BoundingBox();
        Assert.Equal(50.0, bounds.Left, 3);
        Assert.Equal(100.0, bounds.Right, 3);
        Assert.Equal(0.0, bounds.Top, 3);
        Assert.Equal(100.0, bounds.Bottom, 3);
        Assert.True(result[0].IsClosed);
    }

    [Fact]
    public void StraddlingPolygonKeepsTheVisibleArea()
    {
        // Half a 100x100 square is 5000 sq pt of visible area.
        SubPath straddling = Rectangle(50, 0, 100, 100);
        List<SubPath> result = PathClipper.Clip(new[] { straddling }, new Rect2D(0, 0, 100, 100));

        double area = Math.Abs(SignedArea(result[0]));
        Assert.Equal(5000.0, area, 1.0);
    }

    [Fact]
    public void OpenStrokeIsCutIntoItsVisibleRuns()
    {
        // A horizontal line crossing the clip and continuing well past it on both
        // sides: the visible run is the part inside.
        SubPath line = Polyline((-500, 50), (500, 50));

        List<SubPath> result = PathClipper.Clip(new[] { line }, new Rect2D(0, 0, 100, 100));

        Assert.Single(result);
        Rect2D bounds = result[0].BoundingBox();
        Assert.Equal(0.0, bounds.Left, 3);
        Assert.Equal(100.0, bounds.Right, 3);
        Assert.False(result[0].IsClosed);
    }

    [Fact]
    public void AStrokeLeavingAndReEnteringTheClipBecomesTwoPieces()
    {
        // Up out of the clip, back in, then out again: the visible parts are two
        // separate runs, and a stroke between them must not be invented.
        SubPath zigzag = Polyline(
            (10, 50), (50, -100), (90, 50), (120, 50));

        List<SubPath> result = PathClipper.Clip(new[] { zigzag }, new Rect2D(0, 0, 100, 100));

        Assert.Equal(2, result.Count);
        Assert.All(result, piece => Assert.True(piece.BoundingBox().Top >= -0.001));
    }

    [Fact]
    public void CurvesAreFlattenedOnlyWhenTheyAreActuallyCut()
    {
        // A curved subpath fully inside the clip must keep its Bézier handles.
        var curve = new SubPath();
        curve.Nodes.Add(new PathNode(new Point2D(10, 10), new Point2D(10, 10), new Point2D(30, 40)));
        curve.Nodes.Add(new PathNode(new Point2D(60, 60), new Point2D(40, 40), new Point2D(60, 60)));

        List<SubPath> untouched = PathClipper.Clip(new[] { curve }, new Rect2D(0, 0, 100, 100));
        Assert.Same(curve, untouched[0]);
        Assert.False(untouched[0].Nodes[0].HasStraightOutgoing);

        // The same curve crossing the boundary is cut, so it is represented as
        // straight segments within the clip.
        List<SubPath> cut = PathClipper.Clip(new[] { curve }, new Rect2D(0, 0, 40, 40));
        Assert.Single(cut);
        Assert.True(cut[0].BoundingBox().Right <= 40.001);
        Assert.All(cut[0].Nodes, node => Assert.True(node.HasStraightIncoming || node == cut[0].Nodes[0]));
    }

    [Fact]
    public void CrossesBoundaryDetectsTheStraddle()
    {
        var clip = new Rect2D(0, 0, 100, 100);

        Assert.False(PathClipper.CrossesBoundary(new[] { Rectangle(10, 10, 50, 50) }, clip));
        Assert.True(PathClipper.CrossesBoundary(new[] { Rectangle(50, 0, 100, 100) }, clip));
        Assert.True(PathClipper.CrossesBoundary(new[] { Rectangle(200, 200, 50, 50) }, clip));
    }

    private static double SignedArea(SubPath subPath)
    {
        IReadOnlyList<PathNode> nodes = subPath.Nodes;
        double area = 0;
        for (int i = 0; i < nodes.Count; i++)
        {
            Point2D a = nodes[i].Anchor;
            Point2D b = nodes[(i + 1) % nodes.Count].Anchor;
            area += (a.X * b.Y) - (b.X * a.Y);
        }

        return area / 2.0;
    }
}
