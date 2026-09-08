using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

public class ModelInvariantTests
{
    [Fact]
    public void ReHomingItemRemovesItFromPreviousContainer()
    {
        Layer layerA = CadDocument.CreateDefault().Artboards[0].AddLayer("A");
        Layer layerB = CadDocument.CreateDefault().Artboards[0].AddLayer("B");

        var path = PathFactory.CreateRectangle("p", new Rect2D(0, 0, 5, 5));
        layerA.AddItem(path);
        Assert.Single(layerA.Children);
        Assert.Equal(layerA, path.Container);

        layerB.AddItem(path); // moving, not copying
        Assert.Empty(layerA.Children);
        Assert.Single(layerB.Children);
        Assert.Equal(layerB, path.Container);
    }

    [Fact]
    public void OwningLayerWalksGroupNesting()
    {
        CadDocument doc = CadDocument.CreateDefault();
        Layer layer = doc.Artboards[0].Layers[0];

        var group = new ArtGroup { Name = "g" };
        var nested = new ArtGroup { Name = "g2" };
        var path = PathFactory.CreateRectangle("p", new Rect2D(0, 0, 5, 5));

        layer.AddItem(group);
        group.AddItem(nested);
        nested.AddItem(path);

        Assert.Equal(layer, path.OwningLayer());
        Assert.Equal(layer, nested.OwningLayer());
    }

    [Fact]
    public void GroupCannotContainItsOwnAncestor()
    {
        CadDocument doc = CadDocument.CreateDefault();
        Layer layer = doc.Artboards[0].Layers[0];

        var outer = new ArtGroup { Name = "outer" };
        var inner = new ArtGroup { Name = "inner" };
        layer.AddItem(outer);
        outer.AddItem(inner);

        // Re-parenting `outer` beneath its own descendant would form a cycle.
        Assert.Throws<InvalidOperationException>(() => inner.AddItem(outer));
    }

    [Fact]
    public void ItemCannotBeAddedToItself()
    {
        var group = new ArtGroup { Name = "self" };
        Assert.Throws<ArgumentException>(() => group.AddItem(group));
    }

    [Fact]
    public void LayerVisibilityPropagatesThroughGroups()
    {
        CadDocument doc = CadDocument.CreateDefault();
        Layer layer = doc.Artboards[0].Layers[0];

        var group = new ArtGroup();
        var path = PathFactory.CreateRectangle("p", new Rect2D(0, 0, 5, 5));
        layer.AddItem(group);
        group.AddItem(path);

        Assert.True(path.IsEffectivelyVisible());
        group.IsVisible = false;
        Assert.False(path.IsEffectivelyVisible());
        layer.IsVisible = false;
        Assert.False(path.IsEffectivelyVisible());
    }

    [Fact]
    public void GroupTransformComposesDownTheNesting()
    {
        var doc = CadDocument.CreateDefault();
        Layer layer = doc.Artboards[0].Layers[0];

        var outer = new ArtGroup { Name = "outer", Transform = AffineTransform.CreateTranslation(10, 0) };
        var inner = new ArtGroup { Name = "inner", Transform = AffineTransform.CreateScale(2, 2) };
        PathItem rect = PathFactory.CreateRectangle("p", new Rect2D(0, 0, 10, 10));

        layer.AddItem(outer);
        outer.AddItem(inner);
        inner.AddItem(rect);

        // World = outer ∘ inner: 10x10 rect → scaled to 20x20, shifted to x∈[10,30].
        AffineTransform world = AffineTransform.Identity
            .Compose(outer.Transform)
            .Compose(inner.Transform);

        Rect2D worldBox = world.Transform(rect.BoundingBox());
        Assert.Equal(10.0, worldBox.Left, 9);
        Assert.Equal(30.0, worldBox.Right, 9);
        Assert.Equal(20.0, worldBox.Height, 9);
    }
}
