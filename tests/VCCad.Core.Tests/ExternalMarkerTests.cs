using VCCad.Core.Model;
using VCCad.Core.Svg;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// **A marker defined in a referenced document joins the library** (issue #202).
///
/// Its gradient, its pattern and its filter are used through the `use` that named it; a marker is the same kind of
/// asset. Leaving it behind is why a reference could not be written for `test-use.svg`: the arrowhead travelled into
/// the model while the `&lt;marker&gt;` stayed in the file that defined it, so the export had to keep the materialised
/// art instead of writing a reference.
///
/// The collision case is asserted too: two documents may each define a marker of the same name, and choosing between
/// them silently is a picture nobody can explain - so the second is **named** in a warning.
/// </summary>
public class ExternalMarkerTests
{
    private const string Library =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"100\" height=\"100\">" +
        "<defs><marker id=\"arrow\" markerWidth=\"10\" markerHeight=\"10\" refX=\"5\" refY=\"5\" orient=\"auto\">" +
        "<path d=\"M 0 0 L 10 5 L 0 10 Z\"/></marker></defs>" +
        "<g id=\"label\"><path d=\"M 0 0 L 40 0\" fill=\"none\" stroke=\"black\" marker-end=\"url(#arrow)\"/></g>" +
        "</svg>";

    private const string Main =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"200\" height=\"200\">" +
        "<use href=\"lib.svg#label\" x=\"10\" y=\"10\"/>" +
        "</svg>";

    private static SvgImportResult Import(string main, string? library = null)
    {
        string directory = Path.Combine(Path.GetTempPath(), "vccad-external-marker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "lib.svg"), library ?? Library);
            string path = Path.Combine(directory, "main.svg");
            File.WriteAllText(path, main);
            return SvgReader.ReadFile(path);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AReferencedDocumentsMarkerReachesTheLibrary()
    {
        SvgImportResult result = Import(Main);

        ArtGroup arrow = Assert.IsType<ArtGroup>(result.Document.FindDefinition("arrow"));
        Assert.Single(arrow.Children);

        // Its placement attributes came across too, which is what a renderer placing it needs.
        Assert.Equal("5", arrow.ForeignAttributes["refX"]);
        Assert.Equal("auto", arrow.ForeignAttributes["orient"]);

        // And the path that names it is in the model, from the referenced document's own content.
        PathItem path = Assert.Single(result.Document.AllPaths().Where(p => p.MarkerEnd == "arrow"));
        Assert.Equal("arrow", path.MarkerEnd);
    }

    /// <summary>
    /// **And the export can now write the reference**, which is what the library entry is for: before this, a marker
    /// from a referenced document was not resolvable in the output, so the writer had to keep the materialised art.
    /// </summary>
    [Fact]
    public void TheExportWritesTheReferenceAndTheDefinition()
    {
        SvgImportResult result = Import(Main);

        string svg = SvgWriter.Write(result.Document);
        System.Xml.Linq.XDocument xml = System.Xml.Linq.XDocument.Parse(svg);

        System.Xml.Linq.XElement path = xml.Descendants()
            .Single(e => e.Name.LocalName == "path" && e.Attribute("marker-end") is not null);
        Assert.Equal("url(#arrow)", path.Attribute("marker-end")!.Value);

        System.Xml.Linq.XElement marker = xml.Descendants().Single(e => e.Name.LocalName == "marker");
        Assert.Equal("arrow", marker.Attribute("id")!.Value);
        Assert.Equal("5", marker.Attribute("refX")!.Value);
    }

    [Fact]
    public void ANameTheDocumentAlreadyHoldsIsKeptAndTheCollisionIsReported()
    {
        // The importing document defines its own `arrow` first, so the referenced one must not replace it.
        string main =
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"200\" height=\"200\">" +
            "<defs><marker id=\"arrow\" refX=\"1\" refY=\"1\" orient=\"auto\">" +
            "<rect width=\"4\" height=\"4\"/></marker></defs>" +
            "<use href=\"lib.svg#label\" x=\"10\" y=\"10\"/>" +
            "</svg>";

        SvgImportResult result = Import(main);

        // The document's own marker is the one in the library: `refX` 1, not the referenced file's 5.
        ArtGroup arrow = Assert.IsType<ArtGroup>(result.Document.FindDefinition("arrow"));
        Assert.Equal("1", arrow.ForeignAttributes["refX"]);

        Assert.Contains(
            result.Warnings,
            warning => warning.Contains("lib.svg", StringComparison.Ordinal) &&
                warning.Contains("arrow", StringComparison.Ordinal));
    }
}
