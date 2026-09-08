using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

public class DocumentModelTests
{
    [Fact]
    public void DefaultDocumentIsA4LandscapeWithOneLayer()
    {
        CadDocument doc = CadDocument.CreateDefault();

        Assert.Single(doc.Artboards);
        Artboard artboard = doc.Artboards[0];
        Assert.Equal("Artboard 1", artboard.Name);
        Assert.Single(artboard.Layers);

        // A4 landscape: 297 × 210 mm → 841.89 × 595.28 pt.
        Assert.Equal(Measurement.MmToPoints(297.0), artboard.Width, 6);
        Assert.Equal(Measurement.MmToPoints(210.0), artboard.Height, 6);
    }

    [Fact]
    public void ArtboardBoundsMatchItsGeometry()
    {
        var artboard = new Artboard(PageSizes.A4Portrait, new Point2D(120, 50));
        Assert.Equal(new Rect2D(120, 50, artboard.Width, artboard.Height), artboard.Bounds);
    }

    [Fact]
    public void LayerAddsNotifyStructureChanged()
    {
        CadDocument doc = CadDocument.CreateDefault();
        Artboard artboard = doc.Artboards[0];
        int fired = 0;
        artboard.StructureChanged += (_, _) => fired++;

        Layer a = artboard.AddLayer("A");
        Layer b = artboard.AddLayer("B");
        Assert.Equal(2, fired);
        Assert.Equal(3, artboard.Layers.Count);
        Assert.Equal(a, artboard.Layers[1]);
        Assert.Equal(b, artboard.Layers[2]);

        Assert.True(artboard.RemoveLayer(a));
        Assert.False(artboard.RemoveLayer(a)); // already gone → no event
        Assert.Equal(3, fired);
        Assert.Equal(2, artboard.Layers.Count); // default layer remains plus B
        Assert.Equal(b, artboard.Layers[1]);
    }

    [Fact]
    public void PathItemCanHaveMultipleSubPaths()
    {
        var path = new PathItem();
        SubPath outer = path.AddSubPath(closed: true);
        outer.AppendNode(new Point2D(0, 0));
        outer.AppendNode(new Point2D(10, 0));
        outer.AppendNode(new Point2D(10, 10));

        SubPath inner = path.AddSubPath(closed: true);
        inner.AppendNode(new Point2D(3, 3));
        inner.AppendNode(new Point2D(7, 3));

        Assert.Equal(2, path.SubPaths.Count);
        Assert.True(path.IsFullyClosed);
    }

    [Fact]
    public void NodeWithCollapsedHandlesIsStraight()
    {
        var node = new PathNode(new Point2D(5, 5));
        Assert.True(node.HasStraightIncoming);
        Assert.True(node.HasStraightOutgoing);
        Assert.True(node.IsSmooth());

        node.OutHandle = new Point2D(8, 5); // horizontal handle, still "smooth-ish" line
        Assert.False(node.IsSmooth());      // in-collapsed but out not → not mirrored
    }

    [Fact]
    public void SmoothNodeRequiresAntiParallelHandles()
    {
        var node = new PathNode(
            new Point2D(0, 0),
            new Point2D(-10, 0),  // incoming from the left
            new Point2D(10, 0));  // outgoing to the right — mirror images
        Assert.True(node.IsSmooth());
    }
}
