using VCCad.Core.Model;
using VCCad.Core.Serialization;
using VCCad.Core.Svg;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// Everything an Inkscape file carries that is not artwork.
///
/// Inkscape's own test files use `inkscape:` 226 times and `sodipodi:` 94, and this repository's rule is *reflect
/// the file, never invent*: an importer that drops the namespaces silently rewrites every document it touches, and
/// the person who opens the result finds their layer names gone. Two of the attributes are not decoration -
/// `inkscape:label` **is** the layer name, and `sodipodi:insensitive` is what locks a layer.
/// </summary>
public class SvgMetadataTests
{
    private const string Head =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" " +
        "xmlns:inkscape=\"http://www.inkscape.org/namespaces/inkscape\" " +
        "xmlns:sodipodi=\"http://sodipodi.sourceforge.net/DTD/sodipodi-0.0.dtd\" " +
        "xmlns:dc=\"http://purl.org/dc/elements/1.1/\" " +
        "width=\"200\" height=\"100\" viewBox=\"0 0 200 100\">";

    private static SvgImportResult Read(string body) => SvgReader.Read(Head + body + "</svg>");

    /// <summary>Import, export, import - the only test that says the file means what it meant.</summary>
    private static SvgImportResult RoundTrip(string body) => SvgReader.Read(SvgWriter.Write(Read(body).Document));

    /// <summary>
    /// The group the **file** wrote, which is the innermost one in tree order.
    ///
    /// Every file the reader takes is wrapped in one group carrying the file's own units into the model's points, so
    /// a document that the file gave one group has two in the model; these tests are about the file's own data, so
    /// they mean the inner one.
    /// </summary>
    private static ArtGroup FileGroup(CadDocument document) => document.AllGroups().Last();

    // ---------------------------------------------------------------- the label IS the name

    [Fact]
    public void AnInkscapeLabelBecomesTheName()
    {
        SvgImportResult result = Read(
            "<g id=\"layer1\" inkscape:label=\"UK 14\">" +
            "<rect inkscape:label=\"bodice\" width=\"10\" height=\"10\"/></g>");

        ArtGroup group = FileGroup(result.Document);
        PathItem rect = result.Document.AllPaths().Single();

        Assert.Equal("UK 14", group.Name);
        Assert.Equal("bodice", rect.Name);
    }

    /// <summary>`sodipodi:insensitive` is how Inkscape locks a layer, so losing it unlocks the person's work.</summary>
    [Fact]
    public void AnInsensitiveFlagLocksTheItem()
    {
        SvgImportResult result = Read("<g id=\"layer1\" sodipodi:insensitive=\"true\"><rect width=\"1\" height=\"1\"/></g>");

        Assert.True(FileGroup(result.Document).IsLocked);
    }

    /// <summary>And the name survives the round trip, which is the point of reading the label as a name.</summary>
    [Fact]
    public void TheLabelSurvivesTheRoundTrip()
    {
        SvgImportResult back = RoundTrip("<g id=\"layer1\" inkscape:label=\"UK 14\"><rect width=\"10\" height=\"10\"/></g>");

        Assert.Equal("UK 14", FileGroup(back.Document).Name);
    }

    // ---------------------------------------------------------------- unknown attributes

    /// <summary>**An attribute the model has no meaning for is carried verbatim**, not dropped.</summary>
    [Fact]
    public void UnknownNamespacedAttributesSurvive()
    {
        SvgImportResult result = Read(
            "<rect width=\"10\" height=\"10\" inkscape:connector-curvature=\"0\" " +
            "sodipodi:nodetypes=\"cc\" inkscape:someFutureThing=\"42\"/>");

        Dictionary<string, string> foreign = result.Document.AllPaths().Single().ForeignAttributes;

        Assert.Equal("0", foreign["inkscape:connector-curvature"]);
        Assert.Equal("cc", foreign["sodipodi:nodetypes"]);
        Assert.Equal("42", foreign["inkscape:someFutureThing"]);

        // And the exporter puts them back with the same prefixes rather than generated ones.
        string svg = SvgWriter.Write(result.Document);
        Assert.Contains("inkscape:someFutureThing=\"42\"", svg, StringComparison.Ordinal);
        Assert.Contains("sodipodi:nodetypes=\"cc\"", svg, StringComparison.Ordinal);
        Assert.Contains("xmlns:inkscape=\"http://www.inkscape.org/namespaces/inkscape\"", svg, StringComparison.Ordinal);

        // And they survive being read again.
        SvgImportResult again = SvgReader.Read(svg);
        Assert.Equal("42", again.Document.AllPaths().Single().ForeignAttributes["inkscape:someFutureThing"]);
    }

    /// <summary>`xlink:href` is the parser's business, not the file's baggage: it must not be copied as if it were
    /// an unknown attribute, or the exporter would write it twice.</summary>
    [Fact]
    public void MeaningfulNamespacedAttributesAreNotTreatedAsBaggage()
    {
        SvgImportResult result = Read(
            "<defs><rect id=\"r\" width=\"4\" height=\"4\"/></defs>" +
            "<use xlink:href=\"#r\" xmlns:xlink=\"http://www.w3.org/1999/xlink\"/>");

        foreach (PathItem path in result.Document.AllPaths())
        {
            Assert.DoesNotContain("xlink", string.Join(",", path.ForeignAttributes.Keys), StringComparison.Ordinal);
        }
    }

    // ---------------------------------------------------------------- the root-level extras

    /// <summary>**`namedview` round-trips.** It holds the grid, the zoom and the page settings, and a file that
    /// comes back without it resets the document's own settings.</summary>
    [Fact]
    public void TheNamedViewRoundTrips()
    {
        SvgImportResult result = Read(
            "<sodipodi:namedview id=\"namedview1\" pagecolor=\"#ffffff\" inkscape:zoom=\"3.5\" " +
            "inkscape:current-layer=\"layer1\"/>" +
            "<rect width=\"10\" height=\"10\"/>");

        Assert.Single(result.Document.SvgExtras);
        Assert.Contains("namedview1", result.Document.SvgExtras[0], StringComparison.Ordinal);

        string svg = SvgWriter.Write(result.Document);
        Assert.Contains("namedview1", svg, StringComparison.Ordinal);
        Assert.Contains("inkscape:zoom=\"3.5\"", svg, StringComparison.Ordinal);

        // And it is still there after a second trip.
        Assert.Single(SvgReader.Read(svg).Document.SvgExtras);
    }

    /// <summary>RDF metadata is kept the same way.</summary>
    [Fact]
    public void RdfMetadataRoundTrips()
    {
        SvgImportResult result = Read(
            "<metadata id=\"metadata7\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
            "<dc:title>a title</dc:title></rdf:RDF></metadata>" +
            "<rect width=\"10\" height=\"10\"/>");

        string svg = SvgWriter.Write(result.Document);

        Assert.Contains("a title", svg, StringComparison.Ordinal);
        Assert.Contains("metadata7", svg, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- the adversarial case

    /// <summary>
    /// **An element with an unknown namespaced attribute _and_ an unknown element must not lose either.** Reading
    /// the attributes but not the children is the same silent rewrite one level down, and it looks like
    /// preservation.
    /// </summary>
    [Fact]
    public void AnUnknownAttributeAndAnUnknownChildBothSurvive()
    {
        SvgImportResult result = Read(
            "<g inkscape:label=\"Group\" inkscape:groupmode=\"layer\" " +
            "sodipodi:someData=\"keep me\">" +
            "<inkscape:someChild xmlns:inkscape=\"http://www.inkscape.org/namespaces/inkscape\" " +
            "payload=\"preserved\"/>" +
            "<rect width=\"10\" height=\"10\"/></g>");

        ArtGroup group = FileGroup(result.Document);

        Assert.Equal("keep me", group.ForeignAttributes["sodipodi:someData"]);
        Assert.Equal("layer", group.ForeignAttributes["inkscape:groupmode"]);
        Assert.Single(group.ForeignElements);
        Assert.Contains("preserved", group.ForeignElements[0], StringComparison.Ordinal);

        string svg = SvgWriter.Write(result.Document);

        Assert.Contains("sodipodi:someData=\"keep me\"", svg, StringComparison.Ordinal);
        Assert.Contains("preserved", svg, StringComparison.Ordinal);
        Assert.Contains("inkscape:label=\"Group\"", svg, StringComparison.Ordinal);

        // Two round trips, so a loss that only shows up on the second trip is caught too.
        ArtGroup twice = FileGroup(SvgReader.Read(SvgWriter.Write(SvgReader.Read(svg).Document)).Document);

        Assert.Equal("keep me", twice.ForeignAttributes["sodipodi:someData"]);
        Assert.Contains("preserved", twice.ForeignElements[0], StringComparison.Ordinal);
        Assert.Equal("Group", twice.Name);
    }

    // ---------------------------------------------------------------- the real corpus

    /// <summary>One of Inkscape's own files: whatever namespaced data it carries survives the trip.</summary>
    [Fact]
    public void ACorpusFileKeepsItsInkscapeData()
    {
        string? path = CorpusFile("style-parsing.svg");
        if (path is null)
        {
            return;
        }

        SvgImportResult first = SvgReader.ReadFile(path);
        string svg = SvgWriter.Write(first.Document);
        SvgImportResult second = SvgReader.Read(svg);

        int before = first.Document.AllItems().Sum(item => item.ForeignAttributes.Count);
        int after = second.Document.AllItems().Sum(item => item.ForeignAttributes.Count);

        Assert.Equal(before, after);
        Assert.Equal(first.Document.SvgExtras.Count, second.Document.SvgExtras.Count);
    }

    // ---------------------------------------------------------------- the sidecar

    /// <summary>
    /// The baggage survives this repository's **own** format too, or a document saved and reopened would lose the
    /// names it was imported with - and "preserved on the document" would only be true until the first save.
    /// </summary>
    [Fact]
    public void TheMetadataSurvivesTheSidecar()
    {
        SvgImportResult first = Read(
            "<sodipodi:namedview id=\"nv\" inkscape:zoom=\"2\"/>" +
            "<g inkscape:label=\"Group\" sodipodi:someData=\"keep me\">" +
            "<inkscape:someChild xmlns:inkscape=\"http://www.inkscape.org/namespaces/inkscape\" payload=\"preserved\"/>" +
            "<rect width=\"10\" height=\"10\"/></g>");

        CadDocument reloaded = VccadDocumentSerializer.Deserialize(
            VccadDocumentSerializer.SerializeToBytes(first.Document));

        ArtGroup group = FileGroup(reloaded);
        Assert.Equal("Group", group.Name);
        Assert.Equal("keep me", group.ForeignAttributes["sodipodi:someData"]);
        Assert.Contains("preserved", group.ForeignElements[0], StringComparison.Ordinal);
        Assert.Single(reloaded.SvgExtras);
        Assert.Contains("inkscape", reloaded.SvgNamespaces.Keys);

        // And a document with none of it is written exactly as it was before any of this existed.
        string plain = System.Text.Encoding.UTF8.GetString(
            VccadDocumentSerializer.SerializeToBytes(CadDocument.CreateDefault()));
        Assert.DoesNotContain("Foreign", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("SvgExtras", plain, StringComparison.Ordinal);
    }

    private static string? CorpusFile(string name)
    {
        string cache = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "vccad-corpora");
        if (!Directory.Exists(cache))
        {
            return null;
        }

        foreach (string directory in Directory.GetDirectories(cache, "inkscape*"))
        {
            foreach (string candidate in Directory.GetDirectories(directory, "*", SearchOption.AllDirectories))
            {
                try
                {
                    string? found = Directory.GetFiles(candidate, name).FirstOrDefault();
                    if (found is not null)
                    {
                        return found;
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Unreadable, and not needed.
                }
            }
        }

        return null;
    }
}
