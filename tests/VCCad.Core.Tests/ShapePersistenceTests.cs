using VCCad.Core.Model;
using VCCad.Core.Serialization;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// A shape stays a shape.
///
/// If a shape were only ever turned into a path, nothing downstream would have anything to work with:
/// no parameters for the handles to move, no symmetry for a segment edit to follow, and re-opening the
/// file would give back an anonymous outline. The definition therefore travels with the object through
/// the lossless sidecar, and the geometry is only ever the definition rendered.
/// </summary>
public class ShapePersistenceTests
{
    private static readonly ShapeParameters Base = new()
    {
        Centre = new Point2D(100, 120),
        Width = 80,
        Height = 60,
        Points = 6,
        InnerRatio = 0.4,
    };

    [Fact]
    public void AShapeKnowsWhatItIs()
    {
        PathItem star = ShapeLibrary.Create(ShapeKind.Star, Base);

        Assert.NotNull(star.Shape);
        Assert.Equal(ShapeKind.Star, star.Shape!.Kind);
        Assert.Equal(Base, star.Shape.Parameters);
    }

    [Fact]
    public void AnOrdinaryPathIsNotAShape()
    {
        PathItem plain = PathFactory.CreateRectangle("box", new Rect2D(0, 0, 10, 10));

        Assert.Null(plain.Shape);
        Assert.False(plain.RegenerateShape());
    }

    [Fact]
    public void TheDefinitionSurvivesTheLosslessSidecar()
    {
        CadDocument document = CadDocument.CreateDefault("shapes");
        document.Artboards[0].Layers[0].AddItem(ShapeLibrary.Create(ShapeKind.Star, Base));
        document.Artboards[0].Layers[0].AddItem(ShapeLibrary.Create(ShapeKind.Callout, Base with
        {
            HasTail = true,
            Tail = new Point2D(20, 300),
        }));

        CadDocument again = VccadDocumentSerializer.Deserialize(
            VccadDocumentSerializer.SerializeToBytes(document));

        PathItem[] paths = again.Artboards[0].Layers[0].Children.OfType<PathItem>().ToArray();
        Assert.Equal(2, paths.Length);

        Assert.Equal(ShapeKind.Star, paths[0].Shape!.Kind);
        Assert.Equal(Base, paths[0].Shape!.Parameters);
        Assert.True(paths[1].Shape!.Parameters.HasTail);
        Assert.Equal(new Point2D(20, 300), paths[1].Shape!.Parameters.Tail);

        // And the geometry came back with it, node for node.
        PathItem fresh = ShapeLibrary.Create(ShapeKind.Star, Base);
        Assert.Equal(fresh.SubPaths[0].Nodes.Count, paths[0].SubPaths[0].Nodes.Count);
        for (int i = 0; i < fresh.SubPaths[0].Nodes.Count; i++)
        {
            Assert.Equal(fresh.SubPaths[0].Nodes[i].Anchor, paths[0].SubPaths[0].Nodes[i].Anchor);
        }
    }

    /// <summary>Changing a parameter and regenerating is the one way geometry ever moves.</summary>
    [Fact]
    public void RegeneratingRebuildsTheGeometryFromTheDefinition()
    {
        PathItem star = ShapeLibrary.Create(ShapeKind.Star, Base);
        Rect2D before = star.BoundingBox();

        star.Shape = star.Shape! with { Parameters = Base with { Width = 160 } };
        Assert.True(star.RegenerateShape());

        Rect2D after = star.BoundingBox();

        // Not 160: a star is inscribed in its box, so its bounds are narrower than its width. The
        // honest assertion is that it matches the library's own geometry for those parameters.
        Assert.True(after.Width > before.Width, $"width went {before.Width} -> {after.Width}");
        Assert.Equal(
            ShapeLibrary.Create(ShapeKind.Star, Base with { Width = 160 }).BoundingBox().Width,
            after.Width,
            3);

        // Regenerating keeps the identity and the styling: it is the same object, restated.
        Assert.NotNull(star.Shape);
        Assert.Equal(ShapeKind.Star, star.Shape!.Kind);
    }

    /// <summary>Regenerating an already-correct shape changes nothing, which makes it safe to call.</summary>
    [Fact]
    public void RegeneratingIsIdempotent()
    {
        PathItem star = ShapeLibrary.Create(ShapeKind.Star, Base);
        PathItem once = star.GeometrySnapshot();

        Assert.True(star.RegenerateShape());

        Assert.Equal(once.BoundingBox(), star.BoundingBox());
        Assert.Equal(once.SubPaths[0].Nodes.Count, star.SubPaths[0].Nodes.Count);
    }

    /// <summary>Detaching keeps the outline and stops claiming to be regular.</summary>
    [Fact]
    public void DetachingKeepsTheGeometryAndForgetsTheDefinition()
    {
        PathItem star = ShapeLibrary.Create(ShapeKind.Star, Base);
        Rect2D before = star.BoundingBox();
        int nodes = star.SubPaths[0].Nodes.Count;

        ShapeDefinition? detached = star.DetachShape();

        Assert.NotNull(detached);
        Assert.Equal(ShapeKind.Star, detached!.Kind);
        Assert.Null(star.Shape);
        Assert.Equal(before, star.BoundingBox());
        Assert.Equal(nodes, star.SubPaths[0].Nodes.Count);
        Assert.False(star.RegenerateShape());
    }

    /// <summary>A copy of a shape is a shape - a duplicated star must stay editable.</summary>
    [Fact]
    public void CopyingAShapeCopiesWhatItIs()
    {
        PathItem star = ShapeLibrary.Create(ShapeKind.Star, Base);

        var copy = (PathItem)star.Clone();

        Assert.NotNull(copy.Shape);
        Assert.Equal(ShapeKind.Star, copy.Shape!.Kind);
        Assert.Equal(Base, copy.Shape!.Parameters);
    }

    /// <summary>
    /// A file written before shapes existed has no shape fields, and must still load - with ordinary
    /// paths and no definitions.
    /// </summary>
    [Fact]
    public void ADocumentWithoutShapeFieldsStillLoads()
    {
        CadDocument document = CadDocument.CreateDefault("old");
        document.Artboards[0].Layers[0].AddItem(PathFactory.CreateRectangle("box", new Rect2D(0, 0, 10, 10)));

        byte[] json = VccadDocumentSerializer.SerializeToBytes(document);

        CadDocument again = VccadDocumentSerializer.Deserialize(json);

        PathItem path = again.Artboards[0].Layers[0].Children.OfType<PathItem>().Single();
        Assert.Null(path.Shape);
    }
}
