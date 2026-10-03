using System.Xml.Linq;
using VCCad.Core.Model;
using VCCad.Core.Svg;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// **The writer writes the marker reference instead of the artwork it stands for** (issue #202's writer half).
///
/// The reader materialises each marker as art beside its path. If the writer then wrote the reference *and* that
/// copy, every save would add an arrowhead: the re-import would materialise one from the reference and keep the one
/// that was written. So the two halves are asserted together - the bytes carry `marker-end="url(#arrow)"` and a
/// `<marker>` definition, and the round trip comes back with **one** arrowhead, not two.
/// </summary>
public class MarkerWriterTests
{
    private const string Document =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"200\" height=\"100\">" +
        "<defs><marker id=\"arrow\" markerWidth=\"10\" markerHeight=\"10\" refX=\"5\" refY=\"5\" orient=\"auto\">" +
        "<path d=\"M 0 0 L 10 5 L 0 10 Z\"/></marker></defs>" +
        "<path d=\"M 10 50 L 90 50\" fill=\"none\" stroke=\"black\" marker-end=\"url(#arrow)\"/>" +
        "</svg>";

    private static SvgImportResult Import() => SvgReader.Read(Document);

    private static int ArrowheadGroups(CadDocument document)
        => document.AllGroups().Count(g => g.ForeignAttributes.ContainsKey(SvgWriter.MarkerArtTag));

    [Fact]
    public void TheReaderMarksTheArtworkItMaterialised()
    {
        SvgImportResult result = Import();

        // The tag is what makes the writer's job possible: it names the path the artwork belongs to, the slot, and
        // the marker - and it is carried as a plain foreign attribute, so it is never written back out.
        ArtGroup art = Assert.Single(result.Document.AllGroups().Where(g => g.ForeignAttributes.ContainsKey(SvgWriter.MarkerArtTag)));

        // The tag names the **slot and the marker**, never the path's id: an id is generated afresh on every import,
        // so a tag carrying one could never match across a round trip - which the corpus round trip caught.
        Assert.Equal("End arrow", art.ForeignAttributes[SvgWriter.MarkerArtTag]);
    }

    [Fact]
    public void TheWrittenBytesCarryTheReferenceAndTheDefinition()
    {
        SvgImportResult result = Import();

        string svg = SvgWriter.Write(result.Document);
        XDocument xml = XDocument.Parse(svg);

        XElement path = xml.Descendants().Single(e => e.Name.LocalName == "path" &&
            e.Attribute("marker-end") is not null);
        Assert.Equal("url(#arrow)", path.Attribute("marker-end")!.Value);

        // The definition the reference points at is written, with the placement the file stated.
        XElement marker = xml.Descendants().Single(e => e.Name.LocalName == "marker");
        Assert.Equal("arrow", marker.Attribute("id")!.Value);
        Assert.Equal("5", marker.Attribute("refX")!.Value);
        Assert.Equal("auto", marker.Attribute("orient")!.Value);

        // And the materialised copy is **not** written as a second path: exactly two paths exist, the decorated one
        // and the one inside the definition.
        Assert.Equal(2, xml.Descendants().Count(e => e.Name.LocalName == "path"));
    }

    [Fact]
    public void ARoundTripComesBackWithOneArrowheadNotTwo()
    {
        SvgImportResult first = Import();
        Assert.Equal(1, ArrowheadGroups(first.Document));

        // Save and reopen: the picture must be the same, which is the check that catches the double-draw.
        string svg = SvgWriter.Write(first.Document);
        SvgImportResult second = SvgReader.Read(svg);

        PathItem reopened = second.Document.AllItems().OfType<PathItem>().First(p => p.MarkerEnd == "arrow");
        Assert.Equal("arrow", reopened.MarkerEnd);

        // **One arrowhead.** The reference was written, so reading it back materialises exactly one - the one that
        // was written was skipped, and had it not been there would now be two.
        Assert.Equal(1, ArrowheadGroups(second.Document));

        // And the definition came back into the library, so the reference still resolves.
        Assert.NotNull(second.Document.FindDefinition("arrow"));
    }
}
