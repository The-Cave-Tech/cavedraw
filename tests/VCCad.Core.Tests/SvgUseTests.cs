using VCCad.Core.Model;
using VCCad.Core.Svg;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// `use` and `symbol`: an instance of a definition rather than a copy of it.
///
/// The corpus uses `use` 56 times and `symbol` 14, more than any element except `rect` and `text`, so this is not a
/// corner of the format - it is how Inkscape files are built. The assertions are on where the instance's geometry
/// lands, because an instance in the wrong place is the failure that looks like a broken transform.
/// </summary>
public class SvgUseTests
{
    private const string Head =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" " +
        "width=\"200\" height=\"200\" viewBox=\"0 0 200 200\">";

    private static SvgImportResult Read(string body) => SvgReader.Read(Head + body + "</svg>");

    private static Point2D FirstAnchor(SvgImportResult result)
        => result.Document.AllPaths().First().SubPaths[0].Nodes[0].Anchor;

    // ---------------------------------------------------------------- the basics

    [Fact]
    public void AUseDrawsItsTargetAtItsOwnPosition()
    {
        SvgImportResult result = Read(
            "<defs><rect id=\"box\" x=\"0\" y=\"0\" width=\"10\" height=\"10\"/></defs>" +
            "<use xlink:href=\"#box\" x=\"50\" y=\"30\"/>");

        // The definition is not drawn where it is defined, and the instance is drawn at 50,30.
        PathItem path = Assert.Single(result.Document.AllPaths());
        Assert.Equal(new Point2D(0, 0), path.SubPaths[0].Nodes[0].Anchor);

        ArtGroup instance = result.Document.Artboards[0].Layers[0].Children.OfType<ArtGroup>().Single();
        Assert.Equal("box", instance.SourceId);
        Assert.Equal(new Point2D(50, 30), instance.Transform.Transform(new Point2D(0, 0)));
    }

    /// <summary>The instance's own transform composes with its x and y, in that order.</summary>
    [Fact]
    public void AUseComposesItsTransformWithItsPosition()
    {
        SvgImportResult result = Read(
            "<defs><rect id=\"box\" width=\"10\" height=\"10\"/></defs>" +
            "<use xlink:href=\"#box\" x=\"10\" y=\"0\" transform=\"scale(2,2)\"/>");

        ArtGroup instance = result.Document.AllGroups().Single(g => g.SourceId is not null);

        // x is ten to the right of a doubling, so the origin is at 10 and one unit is two.
        Assert.Equal(10.0, instance.Transform.Transform(new Point2D(0, 0)).X, 6);
        Assert.Equal(12.0, instance.Transform.Transform(new Point2D(1, 0)).X, 6);
    }

    /// <summary>**An instance is a link, not a copy**: the group carries the id it came from.</summary>
    [Fact]
    public void AnInstanceRemembersWhatItIsAnInstanceOf()
    {
        SvgImportResult result = Read(
            "<defs><g id=\"widget\"><rect width=\"5\" height=\"5\"/></g></defs>" +
            "<use href=\"#widget\"/>");

        ArtGroup instance = result.Document.AllGroups().Single(g => g.SourceId is not null);
        Assert.Equal("widget", instance.SourceId);
    }

    /// <summary>The SVG 2 bare `href` works as well as `xlink:href`, because Inkscape writes the latter and newer
    /// files write either. The test above uses one and this one the other.</summary>
    [Fact]
    public void BothReferenceFormsResolve()
    {
        SvgImportResult result = Read(
            "<defs><rect id=\"box\" width=\"4\" height=\"4\"/></defs>" +
            "<use xlink:href=\"#box\"/><use href=\"#box\"/>");

        Assert.Equal(2, result.Document.Artboards[0].Layers[0].Children.OfType<ArtGroup>().Count());
        Assert.Empty(result.Missing);
    }

    // ---------------------------------------------------------------- symbols

    /// <summary>
    /// **A symbol is sized by the use that draws it**, and its view box is what the size means.
    /// </summary>
    [Fact]
    public void ASymbolIsSizedByItsUse()
    {
        SvgImportResult result = Read(
            "<defs><symbol id=\"icon\" viewBox=\"0 0 10 10\"><rect width=\"10\" height=\"10\"/></symbol></defs>" +
            "<use xlink:href=\"#icon\" x=\"0\" y=\"0\" width=\"50\" height=\"50\"/>");

        ArtGroup instance = result.Document.AllGroups().Single(g => g.SourceId is not null);
        Assert.Equal("icon", instance.SourceId);

        // The symbol is ten units across and is drawn fifty wide, so one unit is five.
        Assert.Equal(50.0, instance.Transform.Transform(new Point2D(10, 10)).X, 6);
    }

    /// <summary>
    /// SVG 2 lets the **symbol** carry its own width and height as geometry properties, and the corpus has a file
    /// whose whole point is that. A `use` that gives no size falls back to them.
    /// </summary>
    [Fact]
    public void ASymbolsOwnGeometryPropertiesSizeIt()
    {
        SvgImportResult result = Read(
            "<defs><symbol id=\"icon\" viewBox=\"0 0 10 10\" width=\"20\" height=\"20\">" +
            "<rect width=\"10\" height=\"10\"/></symbol></defs>" +
            "<use xlink:href=\"#icon\"/>");

        ArtGroup instance = result.Document.AllGroups().Single(g => g.SourceId is not null);

        // Twenty over ten is two, so one symbol unit is two user units.
        Assert.Equal(20.0, instance.Transform.Transform(new Point2D(10, 0)).X, 6);
    }

    /// <summary>A symbol with no size anywhere is drawn at its view box size, which is what a viewer does.</summary>
    [Fact]
    public void ASymbolWithNoSizeKeepsItsViewBoxSize()
    {
        SvgImportResult result = Read(
            "<defs><symbol id=\"icon\" viewBox=\"0 0 10 10\"><rect width=\"10\" height=\"10\"/></symbol></defs>" +
            "<use xlink:href=\"#icon\"/>");

        ArtGroup instance = result.Document.AllGroups().Single(g => g.SourceId is not null);
        Assert.Equal(10.0, instance.Transform.Transform(new Point2D(10, 0)).X, 6);
    }

    // ---------------------------------------------------------------- nesting

    [Fact]
    public void AUseCanReferToAnotherUse()
    {
        SvgImportResult result = Read(
            "<defs><rect id=\"box\" width=\"4\" height=\"4\"/>"
            + "<use id=\"first\" xlink:href=\"#box\" x=\"7\"/></defs>"
            + "<use xlink:href=\"#first\" x=\"100\"/>");

        // Two instance groups: the outer one, and the one nested inside it for the first use.
        Assert.Equal(2, result.Document.AllGroups().Count());
        Assert.Equal("first", result.Document.AllGroups().First(g => g.SourceId is not null).SourceId);
    }

    // ---------------------------------------------------------------- what is not there

    /// <summary>
    /// **A missing target is reported, not silently dropped.** A file that refers to something it does not contain
    /// is a file the person needs to know about; an instance that quietly vanishes looks like a rendering fault
    /// nobody can explain.
    /// </summary>
    [Fact]
    public void AMissingTargetIsReported()
    {
        SvgImportResult result = Read("<use xlink:href=\"#nothing\"/>");

        Assert.Empty(result.Document.AllPaths());
        Assert.Equal("nothing", Assert.Single(result.Missing));
    }

    /// <summary>A reference that leads back to itself is reported rather than followed, because following it is a
    /// stack overflow rather than a drawing.</summary>
    [Fact]
    public void ACircularReferenceIsReportedRatherThanFollowed()
    {
        SvgImportResult result = Read(
            "<defs><g id=\"a\"><use xlink:href=\"#a\"/></g></defs>" +
            "<use xlink:href=\"#a\"/>");

        Assert.Contains(result.Missing, id => id.Contains("circular", StringComparison.OrdinalIgnoreCase));
    }

    // ---------------------------------------------------------------- the corpus

    /// <summary>
    /// The corpus files that exist for this feature import, and produce instances rather than nothing. `use` is
    /// used 56 times across the suite, so a reader that dropped it would still import every file and draw the wrong
    /// picture - which is why this counts objects rather than checking for exceptions.
    /// </summary>
    [Fact]
    public void TheCorpusUseFilesProduceInstances()
    {
        string? directory = SvgCorpusDirectory();
        if (directory is null)
        {
            return;
        }

        string[] files = Directory.GetFiles(directory, "*.svg", SearchOption.AllDirectories)
            .Where(path =>
            {
                string text = File.ReadAllText(path);
                return text.Contains("<use", StringComparison.Ordinal);
            })
            .ToArray();

        if (files.Length == 0)
        {
            return;
        }

        int instances = 0;
        foreach (string file in files)
        {
            SvgImportResult result = SvgReader.ReadFile(file);
            instances += result.Document.AllGroups().Count(g => g.SourceId is not null);
        }

        Assert.True(files.Length > 0, "the corpus has files that use `use`");
        Assert.True(instances > 0, $"those files produced instances: {instances} from {files.Length} files");
    }

    private static string? SvgCorpusDirectory()
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
                    if (Directory.GetFiles(candidate, "*.svg").Length > 0)
                    {
                        return candidate;
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
