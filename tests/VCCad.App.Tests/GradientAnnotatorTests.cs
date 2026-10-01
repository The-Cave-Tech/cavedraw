using Avalonia;
using VCCad.App.Controls;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;
using GradientStop = VCCad.Core.Model.GradientStop;

namespace VCCad.App.Tests;

/// <summary>
/// A gradient's on-canvas annotators: where the handles are, which one a pointer is on, and what
/// dragging it does.
///
/// These are the arithmetic behind the drawing and the gesture, in world coordinates, so they can
/// be pinned without a window. The canvas is a thin layer on top: it converts to screen and calls
/// this.
/// </summary>
public class GradientAnnotatorTests
{
    private static readonly Rect Box = new(0, 0, 200, 100);

    private static GradientSpec Linear(double startX = 0.0, double startY = 0.5,
        double endX = 1.0, double endY = 0.5)
        => new()
        {
            Kind = GradientKind.Linear,
            Start = new Point2D(startX, startY),
            End = new Point2D(endX, endY),
            Stops = new[] { new GradientStop(0.0, ColorRgb.White), new GradientStop(1.0, ColorRgb.Black) },
        };

    private static GradientSpec Radial(double radiusX, double radiusY, double rotation = 0.0)
        => new()
        {
            Kind = GradientKind.Radial,
            Center = new Point2D(0.5, 0.5),
            RadiusX = radiusX,
            RadiusY = radiusY,
            Rotation = rotation,
            Stops = new[] { new GradientStop(0.0, ColorRgb.White), new GradientStop(1.0, ColorRgb.Black) },
        };

    [Fact]
    public void LinearHandlesSitAtTheRampEnds()
    {
        IReadOnlyList<(GradientHandle Handle, Point2D Point)> handles =
            GradientAnnotators.Handles(Linear(), Box);

        Assert.Equal(2, handles.Count);
        Assert.Equal(GradientHandle.LinearStart, handles[0].Handle);
        Assert.Equal(new Point2D(0, 50), handles[0].Point);
        Assert.Equal(GradientHandle.LinearEnd, handles[1].Handle);
        Assert.Equal(new Point2D(200, 50), handles[1].Point);
    }

    [Fact]
    public void RadialHandlesSitOnTheEllipsesAxes()
    {
        // Half the width and half the height: an ellipse 100 wide and 50 tall, centred.
        IReadOnlyList<(GradientHandle Handle, Point2D Point)> handles =
            GradientAnnotators.Handles(Radial(0.5, 0.5), Box);

        Assert.Equal(3, handles.Count);
        Assert.Equal(new Point2D(100, 50), handles[0].Point);   // centre
        Assert.Equal(new Point2D(200, 50), handles[1].Point);   // horizontal radius
        Assert.Equal(new Point2D(100, 100), handles[2].Point);  // vertical radius
    }

    [Fact]
    public void ACircularRadialOffersOneRadiusHandle()
    {
        // Both axes the same length would put one handle under the other, where no pointer could
        // pick it.
        IReadOnlyList<(GradientHandle Handle, Point2D Point)> handles =
            GradientAnnotators.Handles(Radial(0.4, 0.8), new Rect(0, 0, 100, 50));

        Assert.Equal(2, handles.Count);
        Assert.Equal(GradientHandle.RadialCentre, handles[0].Handle);
        Assert.Equal(GradientHandle.RadialRadiusX, handles[1].Handle);
    }

    [Fact]
    public void TheNearestHandleWithinReachWins()
    {
        GradientSpec spec = Linear();

        Assert.Equal(GradientHandle.LinearEnd,
            GradientAnnotators.HitTest(spec, Box, new Point2D(203, 52), tolerance: 6));
        Assert.Equal(GradientHandle.LinearStart,
            GradientAnnotators.HitTest(spec, Box, new Point2D(2, 49), tolerance: 6));

        // Away from every handle: no grab, so the press is an ordinary selection.
        Assert.Equal(GradientHandle.None,
            GradientAnnotators.HitTest(spec, Box, new Point2D(100, 50), tolerance: 6));
    }

    [Fact]
    public void DraggingALinearEndTurnsAndStretchesTheRamp()
    {
        GradientSpec spec = Linear();

        GradientSpec start = GradientAnnotators.Drag(
            spec, Box, GradientHandle.LinearStart, new Point2D(40, 20), shift: false);
        Assert.Equal(new Point2D(0.2, 0.2), start.Start);

        GradientSpec end = GradientAnnotators.Drag(
            spec, Box, GradientHandle.LinearEnd, new Point2D(160, 30), shift: false);
        Assert.Equal(new Point2D(0.8, 0.3), end.End);

        // The other end did not move.
        Assert.Equal(spec.Start, end.Start);
    }

    [Fact]
    public void ShiftSnapsALinearEndToFortyFiveDegrees()
    {
        GradientSpec spec = Linear(startX: 0.0, startY: 0.5, endX: 1.0, endY: 0.5);

        // The start is at (0,50). Aim at (100,140): about 42 degrees, which snaps to 45.
        GradientSpec dragged = GradientAnnotators.Drag(
            spec, Box, GradientHandle.LinearEnd, new Point2D(100, 140), shift: true);

        GradientGeometry geometry = GradientGeometry.For(dragged, Box);
        double degrees = Math.Atan2(geometry.End.Y - geometry.Start.Y, geometry.End.X - geometry.Start.X) * 180.0 / Math.PI;

        Assert.Equal(45.0, degrees, 4);
    }

    [Fact]
    public void DraggingARadiusHandleScalesThatAxis()
    {
        GradientSpec spec = Radial(0.25, 0.5);

        // The horizontal handle is at (150,50); drag it to (175,50) - half as far again.
        GradientSpec dragged = GradientAnnotators.Drag(
            spec, Box, GradientHandle.RadialRadiusX, new Point2D(175, 50), shift: false);

        Assert.Equal(0.375, dragged.RadiusX, 6);
        Assert.Equal(0.5, dragged.RadiusY, 6);
    }

    [Fact]
    public void ShiftKeepsTheAspectWhenScalingARadius()
    {
        GradientSpec spec = Radial(0.25, 0.5);

        GradientSpec dragged = GradientAnnotators.Drag(
            spec, Box, GradientHandle.RadialRadiusX, new Point2D(175, 50), shift: true);

        // RadiusX went from 0.25 to 0.375 - half as far again - so RadiusY follows.
        Assert.Equal(0.375, dragged.RadiusX, 6);
        Assert.Equal(0.75, dragged.RadiusY, 6);
    }

    [Fact]
    public void DraggingTheRadialCentreMovesTheWholeEllipse()
    {
        GradientSpec spec = Radial(0.25, 0.5);

        GradientSpec dragged = GradientAnnotators.Drag(
            spec, Box, GradientHandle.RadialCentre, new Point2D(40, 20), shift: false);

        Assert.Equal(new Point2D(0.2, 0.2), dragged.Center);
        Assert.Equal(spec.RadiusX, dragged.RadiusX);
        Assert.Equal(spec.RadiusY, dragged.RadiusY);
    }

    /// <summary>
    /// A radial whose focus is its centre has no focus handle: the model holds null for exactly
    /// that picture, and a second handle on the centre would sit under the centre's own where no
    /// pointer could pick it.
    /// </summary>
    [Fact]
    public void ARadialWithNoFocusOffersNoFocusHandle()
    {
        IReadOnlyList<(GradientHandle Handle, Point2D Point)> handles =
            GradientAnnotators.Handles(Radial(0.5, 0.5), Box);

        Assert.DoesNotContain(handles, h => h.Handle == GradientHandle.RadialFocus);
    }

    /// <summary>
    /// A focused radial offers a handle AT the focus, in world coordinates, so a pointer can grab
    /// the highlight and move it - the person's half of the same capability the operation gives a
    /// driver.
    /// </summary>
    [Fact]
    public void AFocusedRadialOffersAHandleAtItsFocus()
    {
        GradientSpec spec = Radial(0.5, 0.5) with { FocalPoint = new Point2D(0.25, 0.5) };

        IReadOnlyList<(GradientHandle Handle, Point2D Point)> handles =
            GradientAnnotators.Handles(spec, Box);

        (GradientHandle handle, Point2D point) = handles.First(h => h.Handle == GradientHandle.RadialFocus);
        Assert.Equal(new Point2D(50, 50), point);   // 0.25 of 200 across, half of 100 down

        Assert.Equal(GradientHandle.RadialFocus, GradientAnnotators.HitTest(spec, Box, point, tolerance: 4));
    }

    /// <summary>
    /// Dragging the focus moves the highlight, and a drag past the ellipse lands on its EDGE
    /// along the ray from the centre - the rule the SVG reader applies to a file that names one
    /// outside. Dragging it onto the centre removes it: null and "the centre" are one picture,
    /// and only one of them is a coordinate the model needs to keep.
    /// </summary>
    [Fact]
    public void DraggingTheFocusMovesItAndClampsItToTheEdge()
    {
        GradientSpec spec = Radial(0.5, 0.5);

        GradientSpec moved = GradientAnnotators.Drag(
            spec, Box, GradientHandle.RadialFocus, new Point2D(50, 50), shift: false);
        Assert.Equal(new Point2D(0.25, 0.5), moved.FocalPoint);

        // Ten radii to the right: clamped to one radius, on the ellipse's edge, not recentred.
        GradientSpec outside = GradientAnnotators.Drag(
            moved, Box, GradientHandle.RadialFocus, new Point2D(1000, 50), shift: false);
        Assert.Equal(new Point2D(1.0, 0.5), outside.FocalPoint);

        GradientSpec back = GradientAnnotators.Drag(
            outside, Box, GradientHandle.RadialFocus, new Point2D(100, 50), shift: false);
        Assert.Null(back.FocalPoint);
    }
}
