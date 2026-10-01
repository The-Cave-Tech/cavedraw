using VCCad.App.Controls;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// The width-profile annotators' arithmetic: where a profile's handles sit, which grip a pointer is on,
/// and what dragging one does to a width point.
///
/// Pinned without a window because this is the part that has to agree with the geometry the canvas
/// paints: the grips are placed by the same arc-length parameterisation <see cref="PathOffset"/> offsets
/// along, so a handle sits where the outline it draws was measured from.
/// </summary>
public class WidthProfileAnnotatorTests
{
    /// <summary>A horizontal line from (100,200) to (400,200). Left of travel is up: Y grows downward.</summary>
    private static PathItem Line()
    {
        var path = new PathItem { Name = "line", Fill = FillSpec.None };
        SubPath sub = path.AddSubPath(closed: false);
        sub.Nodes.Add(new PathNode(new Point2D(100, 200)));
        sub.Nodes.Add(new PathNode(new Point2D(400, 200)));
        path.Stroke = new StrokeSpec(true, ColorRgb.Black, 8, StrokeCap.Butt, StrokeJoin.Miter, 4);
        return path;
    }

    private static WidthProfileSpec Even(params double[] widths)
        => new("Brush", widths.Select((w, i) =>
            WidthPoint.Even(i / (double)Math.Max(1, widths.Length - 1), w)));

    [Fact]
    public void AHandleIsOfferedForEveryWidthPointWithAGripOnEachEdge()
    {
        PathItem path = Line();
        WidthProfileSpec profile = Even(20, 8);

        IReadOnlyList<WidthProfileHandle> handles = WidthProfileAnnotators.Handles(path, profile);

        Assert.Equal(2, handles.Count);
        Assert.Equal(new[] { 0, 1 }, handles.Select(h => h.Index).ToArray());

        // Each grip is half the stroke's width out from the centreline, on its own side. "Half"
        // because a width point's left and right widths are full widths, not offsets.
        Assert.Equal(new Point2D(100, 190), handles[0].Left.Point);
        Assert.Equal(new Point2D(100, 210), handles[0].Right.Point);
        Assert.Equal(new Point2D(400, 196), handles[1].Left.Point);
        Assert.Equal(new Point2D(400, 204), handles[1].Right.Point);
    }

    /// <summary>The case the feature exists for: a stroke that swells on one side only.</summary>
    [Fact]
    public void ALopsidedPointHasItsGripsAtTwoDifferentDistances()
    {
        PathItem path = Line();
        var profile = new WidthProfileSpec("Brush", new[]
        {
            new WidthPoint(0, 20, 4),
            new WidthPoint(1, 20, 4),
        });

        WidthProfileHandle handle = WidthProfileAnnotators.Handles(path, profile)[0];

        Assert.Equal(190.0, handle.Left.Point.Y, 6);
        Assert.Equal(202.0, handle.Right.Point.Y, 6);
    }

    [Fact]
    public void ThereAreNoHandlesWithoutAProfile()
    {
        PathItem path = Line();

        Assert.Empty(WidthProfileAnnotators.Handles(path, null));
        Assert.Empty(WidthProfileAnnotators.Handles(path, new WidthProfileSpec("empty", Array.Empty<WidthPoint>())));
        Assert.Empty(WidthProfileAnnotators.Grips(path, null));
    }

    [Fact]
    public void HitTestFindsTheGripUnderThePointerAndNothingAwayFromIt()
    {
        PathItem path = Line();
        WidthProfileSpec profile = Even(20, 20);

        WidthProfileGrip? left = WidthProfileAnnotators.HitTest(path, profile, new Point2D(100, 190), 6);
        Assert.NotNull(left);
        Assert.Equal(0, left!.Value.Index);
        Assert.Equal(WidthProfileSide.Left, left.Value.Side);

        WidthProfileGrip? right = WidthProfileAnnotators.HitTest(path, profile, new Point2D(400, 210), 6);
        Assert.Equal(WidthProfileSide.Right, right!.Value.Side);

        Assert.Null(WidthProfileAnnotators.HitTest(path, profile, new Point2D(250, 200), 6));
    }

    [Fact]
    public void DraggingAGripSetsOnlyThatSideOfThatPoint()
    {
        PathItem path = Line();
        WidthProfileSpec profile = Even(20, 20);
        WidthProfileGrip grip = WidthProfileAnnotators.Handles(path, profile)[0].Left;

        // 30 points further out on the left, so the width there becomes 60.
        WidthPoint dragged = WidthProfileAnnotators.Drag(path, profile, grip, new Point2D(100, 170));

        Assert.Equal(60.0, dragged.LeftWidth, 6);
        Assert.Equal(20.0, dragged.RightWidth, 6);

        WidthProfileSpec result = WidthProfileAnnotators.Dragged(path, profile, grip, new Point2D(100, 170));
        Assert.Equal(60.0, result.Points[0].LeftWidth, 6);
        Assert.Equal(20.0, result.Points[1].LeftWidth, 6);
    }

    /// <summary>
    /// Past the centreline the width stops at zero. A negative one is not a thinner stroke: the offset
    /// edge would cross to the other side of the path and the outline would turn inside out.
    /// </summary>
    [Fact]
    public void DraggingPastTheCentrelineClampsAtZeroRatherThanInverting()
    {
        PathItem path = Line();
        WidthProfileSpec profile = Even(20, 20);
        WidthProfileGrip grip = WidthProfileAnnotators.Handles(path, profile)[1].Left;

        WidthPoint dragged = WidthProfileAnnotators.Drag(path, profile, grip, new Point2D(400, 400));

        Assert.Equal(0.0, dragged.LeftWidth, 9);
        Assert.Equal(20.0, dragged.RightWidth, 6);
    }

    /// <summary>Travelling the other way mirrors the sides, which is the whole point of a normal.</summary>
    [Fact]
    public void LeftAndRightFollowTheDirectionOfTravel()
    {
        var path = new PathItem { Name = "line", Fill = FillSpec.None };
        SubPath sub = path.AddSubPath(closed: false);
        sub.Nodes.Add(new PathNode(new Point2D(400, 200)));
        sub.Nodes.Add(new PathNode(new Point2D(100, 200)));

        WidthProfileHandle handle = WidthProfileAnnotators.Handles(path, Even(20, 20))[0];

        // Right to left, so left of travel is below the line.
        Assert.Equal(210.0, handle.Left.Point.Y, 6);
        Assert.Equal(190.0, handle.Right.Point.Y, 6);
    }
}
