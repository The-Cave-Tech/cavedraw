using VCCad.Core.Model;
using VCCad.Core.Serialization;
using VCCad.Core.Svg;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// **A path records which marker it uses** (issue #202), from the file through the model to the sidecar and back.
///
/// The reader has always materialised a marker's artwork at its vertices, which is enough to draw the arrowhead and
/// not enough to say *which* arrowhead it is. These assert the reference itself: the id the file named, on the slot
/// it named it in, kept when the reference is dangling, and surviving the sidecar round trip - because a picker
/// that cannot read the marker a path uses has nothing to show, and an export that loses the reference is not a round
/// trip.
/// </summary>
public class MarkerReferenceTests
{
    private const string Document =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"200\" height=\"100\">" +
        "<defs><marker id=\"arrow\" markerWidth=\"10\" markerHeight=\"10\" refX=\"5\" refY=\"5\" orient=\"auto\">" +
        "<path d=\"M 0 0 L 10 5 L 0 10 Z\"/></marker></defs>" +
        "<path d=\"M 10 50 L 90 50\" fill=\"none\" stroke=\"black\" marker-start=\"url(#arrow)\" " +
        "marker-end=\"url(#arrow)\"/>" +
        "<path d=\"M 10 70 L 90 70\" fill=\"none\" stroke=\"black\" marker-end=\"url(#nowhere)\"/>" +
        "</svg>";

    private static PathItem[] Paths(SvgImportResult result)
        => result.Document.AllItems().OfType<PathItem>().Where(p => p.Strokes.Count > 0).ToArray();

    [Fact]
    public void TheReaderRecordsTheMarkerEachSlotNames()
    {
        SvgImportResult result = SvgReader.Read(Document);

        // The two authored paths, in document order; the marker's own `d` travels as artwork beside them, which is
        // why the filter is on having a stroke rather than on being first.
        PathItem[] paths = result.Document.AllItems().OfType<PathItem>()
            .Where(p => p.Strokes.Count > 0 && p.SubPaths.Count == 1 && p.SubPaths[0].Nodes.Count == 2)
            .ToArray();
        Assert.Equal(2, paths.Length);

        Assert.Equal("arrow", paths[0].MarkerStart);
        Assert.Equal("arrow", paths[0].MarkerEnd);
        Assert.Null(paths[0].MarkerMid);

        // **A dangling reference is still recorded**, and the warning says nothing was drawn for it: the file named
        // a marker, and a picker has to be able to say so.
        Assert.Equal("nowhere", paths[1].MarkerEnd);
        Assert.Contains(result.Warnings, warning => warning.Contains("nowhere", StringComparison.Ordinal));
    }

    [Fact]
    public void TheReferenceSurvivesTheSidecarRoundTrip()
    {
        SvgImportResult result = SvgReader.Read(Document);
        result.Document.AllItems().OfType<PathItem>().First(p => p.MarkerEnd == "arrow").MarkerMid = "arrow";

        string json = VccadDocumentSerializer.Serialize(result.Document);
        CadDocument reopened = VccadDocumentSerializer.Deserialize(json);

        PathItem arrow = reopened.AllItems().OfType<PathItem>().First(p => p.MarkerEnd == "arrow");
        Assert.Equal("arrow", arrow.MarkerStart);
        Assert.Equal("arrow", arrow.MarkerMid);
        Assert.Equal("arrow", arrow.MarkerEnd);

        PathItem dangling = reopened.AllItems().OfType<PathItem>().First(p => p.MarkerEnd == "nowhere");
        Assert.Null(dangling.MarkerStart);
        Assert.Null(dangling.MarkerMid);

        // And a path that names nothing writes no member at all, which is what keeps every earlier document's bytes
        // unchanged.
        PathItem markerArt = reopened.AllItems().OfType<PathItem>().First(p => p.MarkerEnd is null && p.MarkerStart is null);
        Assert.NotNull(markerArt);
    }

    [Fact]
    public void ACloneKeepsTheReference()
    {
        var path = new PathItem { MarkerStart = "a", MarkerMid = "b", MarkerEnd = "c" };
        var copy = (PathItem)path.Clone();

        Assert.Equal("a", copy.MarkerStart);
        Assert.Equal("b", copy.MarkerMid);
        Assert.Equal("c", copy.MarkerEnd);
    }

    [Fact]
    public void AMarkersContentIsKeptInTheDocumentsLibrary()
    {
        SvgImportResult result = SvgReader.Read(Document);

        // The `<marker>` is a definition now, not only a registry inside the import: a picker has to be able to list
        // the markers a document defines, and a renderer has to be able to place one. Its content is the marker's own
        // drawing, read through the ordinary element walk.
        ArtGroup arrow = Assert.IsType<ArtGroup>(result.Document.FindDefinition("arrow"));
        Assert.Single(arrow.Children);

        PathItem content = arrow.Children.OfType<PathItem>().Single();
        Assert.NotEmpty(content.SubPaths);

        // The definition is in the library, not on an artboard: nothing draws it as artwork of its own.
        Assert.Contains(arrow, result.Document.Definitions.Children);
        Assert.DoesNotContain(arrow, result.Document.Artboards[0].Layers[0].Children);
    }
}
