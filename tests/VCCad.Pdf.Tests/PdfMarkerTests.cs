using System.Text;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Pdf.Tests;

/// <summary>
/// **The page writes a marker from its definition** (issue #202).
///
/// Byte assertions rather than a picture, because what is being asked is whether the exporter *wrote* the arrowhead
/// at all and whether it wrote it once: the definition's own far corner appears in the content stream, the corner of
/// a deliberately larger materialised copy does not, and clearing the reference takes the geometry away.
/// </summary>
public class PdfMarkerTests
{
    private static CadDocument Document(string? markerEnd, double? shadowSize = null)
    {
        CadDocument document = CadDocument.CreateDefault("Markers");
        document.Artboards[0].Width = 300;
        document.Artboards[0].Height = 300;

        // The library definition: a filled box `size` units across, its own origin at its top-left.
        ArtGroup definition = document.AddDefinition("arrow");
        definition.ForeignAttributes["markerWidth"] = "20";
        definition.ForeignAttributes["markerHeight"] = "20";
        definition.ForeignAttributes["markerUnits"] = "userSpaceOnUse";
        definition.AddItem(Box(20));

        var line = new PathItem
        {
            Name = "line",
            Stroke = StrokeSpec.Hairline(ColorRgb.Black),
            MarkerEnd = markerEnd,
        };

        SubPath sub = line.AddSubPath(closed: false);
        sub.Nodes.Add(new PathNode(new Point2D(20, 50)));
        sub.Nodes.Add(new PathNode(new Point2D(120, 50)));
        document.Artboards[0].Layers[0].AddItem(line);

        if (shadowSize is { } size)
        {
            var shadow = new ArtGroup { Name = "arrow" };
            shadow.AddItem(Box(size));
            shadow.ForeignAttributes[VCCad.Core.Svg.SvgWriter.MarkerArtTag] = "End arrow";
            document.Artboards[0].Layers[0].AddItem(shadow);
        }

        return document;
    }

    private static PathItem Box(double size)
    {
        var path = new PathItem { Name = "box", Fill = FillSpec.Solid(ColorRgb.Black) };
        SubPath sub = path.AddSubPath(closed: true);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(size, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(size, size)));
        sub.Nodes.Add(new PathNode(new Point2D(0, size)));
        return path;
    }

    /// <summary>
    /// The page's own drawing operators, **inflated**: the exporter compresses its content streams
    /// (`/Filter /FlateDecode`), so a search of the raw bytes for a coordinate finds nothing at all - which is what
    /// the first version of this test did, and why it failed on every assertion. `PdfDrawing.Of` is the helper the
    /// other content-level tests already use.
    /// </summary>
    private static string Content(CadDocument document) => PdfDrawing.Of(PdfDocumentExporter.Export(document));

    [Fact]
    public void ThePageWritesTheMarkerWhereThePathSays()
    {
        string withMarker = Content(Document("arrow"));
        string without = Content(Document(null));

        // The definition's far corner is 20 units past the end vertex at x=120, so the page writes x=140 for the
        // marker; the same document without the reference has no such geometry. (The line itself stops at 120.)
        Assert.Contains("140", withMarker, StringComparison.Ordinal);
        Assert.DoesNotContain("140", without, StringComparison.Ordinal);
    }

    [Fact]
    public void TheMaterialisedArtworkIsNotWrittenAsWell()
    {
        // An 80-unit tagged copy beside the path: if it were written as well as the placement, the page would carry
        // both arrowheads - and its far corner at x=200 is what says which one is there.
        string content = Content(Document("arrow", shadowSize: 80));

        Assert.Contains("140", content, StringComparison.Ordinal);
        Assert.DoesNotContain("200", content, StringComparison.Ordinal);
    }

    [Fact]
    public void ChangingTheReferenceChangesThePage()
    {
        string small = Content(Document("arrow"));

        // The same document with a bigger marker in the library: the page must follow it.
        CadDocument other = CadDocument.CreateDefault("Markers");
        other.Artboards[0].Width = 300;
        other.Artboards[0].Height = 300;

        ArtGroup bigger = other.AddDefinition("arrow");
        bigger.ForeignAttributes["markerWidth"] = "60";
        bigger.ForeignAttributes["markerHeight"] = "60";
        bigger.ForeignAttributes["markerUnits"] = "userSpaceOnUse";
        bigger.AddItem(Box(60));

        var line = new PathItem { Name = "line", Stroke = StrokeSpec.Hairline(ColorRgb.Black), MarkerEnd = "arrow" };
        SubPath sub = line.AddSubPath(closed: false);
        sub.Nodes.Add(new PathNode(new Point2D(20, 50)));
        sub.Nodes.Add(new PathNode(new Point2D(120, 50)));
        other.Artboards[0].Layers[0].AddItem(line);

        string large = Content(other);

        Assert.NotEqual(small, large);
        Assert.Contains("180", large, StringComparison.Ordinal);
    }
}
