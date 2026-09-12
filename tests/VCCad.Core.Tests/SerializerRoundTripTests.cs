using VCCad.Core.Model;
using VCCad.Core.Serialization;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

public class SerializerRoundTripTests
{
    private static CadDocument BuildFixture()
    {
        CadDocument doc = CadDocument.CreateDefault("Fixture");
        Artboard artboard = doc.Artboards[0];
        Layer layer = artboard.Layers[0];

        // Closed rectangle, filled red with a visible stroke.
        PathItem rect = PathFactory.CreateRectangle("swatch", new Rect2D(20, 30, 120, 80));
        rect.Fill = FillSpec.Solid(ColorRgb.FromBytes(220, 30, 30));
        rect.Stroke = new StrokeSpec(true, ColorRgb.Black, 2.5, StrokeCap.Round, StrokeJoin.Bevel, 3.0);
        layer.AddItem(rect);

        // Open curved polyline with real handles.
        PathItem wave = PathFactory.CreateLine("curve", new Point2D(10, 10), new Point2D(200, 10));
        wave.SubPaths[0].Nodes[0].OutHandle = new Point2D(70, 90);
        wave.SubPaths[0].Nodes[1].InHandle = new Point2D(140, -70);
        wave.Stroke = StrokeSpec.Hairline(ColorRgb.Blue);
        layer.AddItem(wave);

        // Nested group with a transform.
        var inner = new ArtGroup { Name = "nested" };
        inner.AddItem(PathFactory.CreateEllipse("orb", new Point2D(60, 60), 25, 40));
        var outer = new ArtGroup { Name = "outer", Transform = AffineTransform.CreateRotation(0.3) };
        outer.AddItem(inner);
        layer.AddItem(outer);

        Artboard second = doc.AddArtboard(PageSizes.A4Landscape, "Artboard 2", new Point2D(1000, 0));
        second.AddLayer("backdrop");
        return doc;
    }

    [Fact]
    public void RoundTripIsByteIdenticalForSerialization()
    {
        CadDocument doc = BuildFixture();
        string once = VccadDocumentSerializer.Serialize(doc);

        CadDocument revived = VccadDocumentSerializer.Deserialize(once);
        string twice = VccadDocumentSerializer.Serialize(revived);

        Assert.Equal(once, twice); // deterministic, lossless text round-trip
    }

    [Fact]
    public void StructureSurvivesRoundTrip()
    {
        CadDocument doc = BuildFixture();
        CadDocument revived = VccadDocumentSerializer.Deserialize(VccadDocumentSerializer.Serialize(doc));

        Assert.Equal(doc.Name, revived.Name);
        Assert.Equal(2, revived.Artboards.Count);

        Artboard original = doc.Artboards[0];
        Artboard copy = revived.Artboards[0];
        Assert.Equal(original.Id, copy.Id);
        Assert.Equal(original.X, copy.X, 12);
        Assert.Equal(original.Width, copy.Width, 12);
        Assert.Equal(original.Height, copy.Height, 12);
        Assert.Equal(original.Name, copy.Name);

        Assert.Equal(original.Layers.Count, copy.Layers.Count);
        Assert.Equal(original.Layers[0].Id, copy.Layers[0].Id);

        PathItem origRect = (PathItem)original.Layers[0].Children[0];
        PathItem copyRect = (PathItem)copy.Layers[0].Children[0];

        Assert.Equal(origRect.Id, copyRect.Id);
        Assert.Equal(origRect.SubPaths.Count, copyRect.SubPaths.Count);
        Assert.Equal(origRect.Fill.Color.R, copyRect.Fill.Color.R, 12);
        Assert.Equal(origRect.Stroke.Width, copyRect.Stroke.Width, 12);
        Assert.Equal(origRect.Stroke.Join, copyRect.Stroke.Join);
    }

    [Fact]
    public void HandlesAndTransformsArePreserved()
    {
        CadDocument doc = BuildFixture();
        CadDocument revived = VccadDocumentSerializer.Deserialize(VccadDocumentSerializer.Serialize(doc));

        Artboard copy = revived.Artboards[0];
        PathItem origWave = (PathItem)BuildFixture().Artboards[0].Layers[0].Children[1];
        PathItem copyWave = (PathItem)copy.Layers[0].Children[1];

        Assert.Equal(origWave.SubPaths[0].Nodes[0].OutHandle.X, copyWave.SubPaths[0].Nodes[0].OutHandle.X, 12);
        Assert.Equal(origWave.SubPaths[0].Nodes[1].InHandle.Y, copyWave.SubPaths[0].Nodes[1].InHandle.Y, 12);

        ArtGroup copyOuter = (ArtGroup)copy.Layers[0].Children[2];
        AffineTransform expected = AffineTransform.CreateRotation(0.3);
        Assert.Equal(expected.A, copyOuter.Transform.A, 12);
        Assert.Equal(expected.B, copyOuter.Transform.B, 12);
    }

    [Fact]
    public void OrphanObjectsRoundTrip()
    {
        var doc = new CadDocument { Name = "with-orphans" };
        doc.Orphans.AddItem(PathFactory.CreateRectangle("pasteboard-rect", new Rect2D(5, 7, 30, 20)));

        CadDocument revived = VccadDocumentSerializer.Deserialize(VccadDocumentSerializer.Serialize(doc));

        Assert.Single(revived.Orphans.Children);
        Assert.Equal("pasteboard-rect", revived.Orphans.Children[0].Name);
    }

    [Fact]
    public void EmptyDocumentRoundTrips()
    {
        var doc = new CadDocument { Name = "blank" };
        CadDocument revived = VccadDocumentSerializer.Deserialize(VccadDocumentSerializer.Serialize(doc));
        Assert.Empty(revived.Artboards);
    }

    [Fact]
    public void NewerFormatVersionIsRejected()
    {
        string payload = VccadDocumentSerializer.Serialize(new CadDocument());
        // Simulate a file written by a future build.
        string future = payload.Replace("\"Version\":1", "\"Version\":999", StringComparison.Ordinal);
        Assert.Throws<NotSupportedException>(() => VccadDocumentSerializer.Deserialize(future));
    }
}
