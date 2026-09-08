using VCCad.Core.Viewport;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

public class PasteboardLayoutTests
{
    // An A4 landscape artboard plus some artwork spilling off it.
    private static Rect2D FixtureExtent()
        => new Rect2D(0, 0, 1000, 600).Union(new Rect2D(700, 400, 800, 300));

    [Fact]
    public void DefaultZoomLeavesFullViewportRoomOnEverySide()
    {
        var layout = new PasteboardLayout(FixtureExtent());
        var viewport = new Size2D(1280, 800);

        // Artwork top-left at its left-most legal point: the artwork's right/bottom
        // edge sits at the viewport's left/top edge → one blank viewport right/below.
        Vector2D leftMost = layout.ClampTopLeft(layout.MinTopLeft(viewport), viewport);
        Assert.Equal(-layout.Extent.Width, leftMost.X, 9);
        Assert.Equal(-layout.Extent.Height, leftMost.Y, 9);

        // …and at its right-most legal point the artboard leaves a full blank
        // viewport on the left/top.
        Vector2D rightMost = layout.ClampTopLeft(layout.MaxTopLeft(viewport), viewport);
        Assert.Equal(viewport.Width, rightMost.X, 9);
        Assert.Equal(viewport.Height, rightMost.Y, 9);

        // Travel between the two extremes therefore exceeds the viewport size.
        double travelX = rightMost.X - leftMost.X;
        double travelY = rightMost.Y - leftMost.Y;
        Assert.True(travelX >= viewport.Width, "Horizontal scroll must cover at least one viewport.");
        Assert.True(travelY >= viewport.Height, "Vertical scroll must cover at least one viewport.");
    }

    [Fact]
    public void OutOfRangePansAreClampedIntoTheLegalRange()
    {
        var layout = new PasteboardLayout(new Rect2D(0, 0, 400, 300));
        var viewport = new Size2D(800, 600);

        Vector2D huge = layout.ClampTopLeft(new Vector2D(10_000, 10_000), viewport);
        Assert.Equal(viewport.Width, huge.X, 9);
        Assert.Equal(viewport.Height, huge.Y, 9);

        Vector2D far = layout.ClampTopLeft(new Vector2D(-10_000, -10_000), viewport);
        Assert.Equal(-layout.Extent.Width, far.X, 9);
        Assert.Equal(-layout.Extent.Height, far.Y, 9);
    }

    [Fact]
    public void ZoomAffectsScreenExtentAndClamping()
    {
        var layout = new PasteboardLayout(new Rect2D(0, 0, 100, 100));
        layout.Zoom = 2.0;
        Assert.Equal(200.0, layout.ExtentOnScreen.Width, 9);

        var viewport = new Size2D(500, 500);
        Vector2D min = layout.MinTopLeft(viewport);
        Assert.Equal(-200.0, min.X, 9);
    }

    [Fact]
    public void ZoomToFitContainsArtworkWithMargin()
    {
        var layout = new PasteboardLayout(new Rect2D(0, 0, 1000, 800));
        var viewport = new Size2D(1000, 800);
        layout.Zoom = layout.ZoomToFit(viewport);

        Size2D onScreen = layout.ExtentOnScreen;
        Assert.True(onScreen.Width < viewport.Width, "Fitted artwork must be narrower than the viewport.");
        Assert.True(onScreen.Height < viewport.Height, "Fitted artwork must be shorter than the viewport.");
    }

    [Fact]
    public void CenterInViewportBalancesMargins()
    {
        var layout = new PasteboardLayout(new Rect2D(0, 0, 400, 200));
        layout.Zoom = 1.0;
        Vector2D topLeft = layout.CenterInViewport(new Size2D(1000, 600));
        Assert.Equal((1000 - 400) / 2.0, topLeft.X, 9);
        Assert.Equal((600 - 200) / 2.0, topLeft.Y, 9);
    }

    [Fact]
    public void EmptyArtworkDoesNotProduceNonsense()
    {
        var layout = new PasteboardLayout(Rect2D.Empty);
        var viewport = new Size2D(800, 600);
        Vector2D clamped = layout.ClampTopLeft(new Vector2D(400, 300), viewport);
        // Both extremes are 0 for empty art; the clamp collapses gracefully.
        Assert.True(double.IsFinite(clamped.X) && double.IsFinite(clamped.Y));
        Assert.Equal(1.0, layout.ZoomToFit(viewport), 9);
    }
}
