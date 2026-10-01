using VCCad.Core.Model;
using VCCad.Core.Svg;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// The small SVG 1.2 pieces Inkscape still writes, and what happens to elements the reader does not know.
///
/// `solidColor` is a paint server whose colour arrives from a property rather than from the reference, so reading
/// the reference alone renders **black** - which is why the corpus has a file dedicated to it. The second half is
/// the reporting rule: an unrecognised element is listed rather than silently skipped, because artwork that
/// quietly went missing looks deliberate.
/// </summary>
public class SvgSolidColorTests
{
    private const string Head =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"200\" height=\"100\" viewBox=\"0 0 200 100\">";

    private static SvgImportResult Read(string body) => SvgReader.Read(Head + body + "</svg>");

    private static PathItem ById(SvgImportResult result, string id)
        => result.Document.AllPaths().Single(p => p.Name == id);

    [Fact]
    public void ASolidColorFromAttributesBecomesASolidFill()
    {
        SvgImportResult result = Read(
            "<defs><solidcolor id=\"col1\" solid-color=\"#ff0000\" solid-opacity=\"0.2\"/></defs>" +
            "<rect id=\"shape\" width=\"10\" height=\"10\" fill=\"url(#col1)\"/>");

        PathItem path = ById(result, "shape");

        Assert.Null(path.Fill.Gradient);
        Assert.Equal(1.0, path.Fill.Color.R, 6);
        Assert.Equal(0.2, path.Fill.Color.A, 6);
    }

    /// <summary>**The property may arrive from an inline style instead of an attribute**, which the corpus uses
    /// for its second swatch - so a reader that only reads attributes gets one of the two right.</summary>
    [Fact]
    public void ASolidColorFromAnInlineStyleWorks()
    {
        SvgImportResult result = Read(
            "<defs><solidcolor id=\"col2\" style=\"solid-color:#0000ff;solid-opacity:0.2\"/></defs>" +
            "<rect id=\"shape\" width=\"10\" height=\"10\" style=\"fill:url(#col2)\"/>");

        PathItem path = ById(result, "shape");

        Assert.Equal(1.0, path.Fill.Color.B, 6);
        Assert.Equal(0.2, path.Fill.Color.A, 6);
    }

    /// <summary>And from a stylesheet rule, which is the third way the property can arrive.</summary>
    [Fact]
    public void ASolidColorFromAStylesheetWorks()
    {
        SvgImportResult result = Read(
            "<style>#col { solid-color: #00ff00; solid-opacity: 0.5; }</style>" +
            "<defs><solidcolor id=\"col\"/></defs>" +
            "<rect id=\"shape\" width=\"10\" height=\"10\" fill=\"url(#col)\"/>");

        PathItem path = ById(result, "shape");

        Assert.Equal(1.0, path.Fill.Color.G, 6);
        Assert.Equal(0.5, path.Fill.Color.A, 6);
    }

    /// <summary>The element is written **lowercase** in the corpus and XML is case-sensitive, so the name is
    /// matched ignoring case - reading only the camel-case spelling misses the one file that uses it.</summary>
    [Fact]
    public void TheElementNameIsMatchedIgnoringCase()
    {
        SvgImportResult lower = Read(
            "<defs><solidcolor id=\"c\" solid-color=\"#ff0000\"/></defs>" +
            "<rect id=\"shape\" width=\"1\" height=\"1\" fill=\"url(#c)\"/>");
        SvgImportResult camel = Read(
            "<defs><solidColor id=\"c\" solid-color=\"#ff0000\"/></defs>" +
            "<rect id=\"shape\" width=\"1\" height=\"1\" fill=\"url(#c)\"/>");

        Assert.Equal(1.0, lower.Document.AllPaths().Single().Fill.Color.R, 6);
        Assert.Equal(1.0, camel.Document.AllPaths().Single().Fill.Color.R, 6);
    }

    // ---------------------------------------------------------------- the corpus file

    /// <summary>
    /// **`solid-color-test.svg`, whose whole purpose is that getting this wrong renders black.** Its two
    /// rectangles are red at a fifth and blue at a fifth - one from attributes, one from an inline style.
    /// </summary>
    [Fact]
    public void TheSolidColorCorpusFileHasTheColoursItPromises()
    {
        string? path = CorpusFile("solid-color-test.svg");
        if (path is null)
        {
            return;
        }

        SvgImportResult result = SvgReader.ReadFile(path);
        List<PathItem> paths = result.Document.AllPaths().ToList();

        Assert.Equal(2, paths.Count);

        // The file states no size, only a 200x100 view box, so the view box is the page - and the page is points.
        Assert.Equal(150.0, result.Document.Artboards[0].Width, 6);
        Assert.Equal(75.0, result.Document.Artboards[0].Height, 6);

        // Neither is black, which is exactly what the file exists to catch.
        Assert.All(paths, p => Assert.True(
            p.Fill.Color.R + p.Fill.Color.G + p.Fill.Color.B > 0.5,
            $"a solid colour from the file came out {p.Fill.Color}"));

        Assert.Equal(1.0, paths[0].Fill.Color.R, 6);
        Assert.Equal(0.2, paths[0].Fill.Color.A, 6);
        Assert.Equal(1.0, paths[1].Fill.Color.B, 6);
        Assert.Equal(0.2, paths[1].Fill.Color.A, 6);
    }

    // ---------------------------------------------------------------- reporting

    /// <summary>**An element the reader does not know is reported by name**, not silently dropped.</summary>
    [Fact]
    public void AnUnknownElementIsReported()
    {
        SvgImportResult result = Read(
            "<rect id=\"shape\" width=\"1\" height=\"1\"/>" +
            "<someFutureThing width=\"1\"/>" +
            "<anotherUnknown/>");

        Assert.Contains("someFutureThing", result.Warnings);
        Assert.Contains("anotherUnknown", result.Warnings);

        // And it is reported once each, and the shape that is understood still imported.
        Assert.Equal(2, result.Warnings.Count);
        Assert.Single(result.Document.AllPaths());
    }

    /// <summary>The things a reader deliberately does not draw are not warnings: they are understood and skipped,
    /// and listing them would drown the real gaps in noise.</summary>
    [Fact]
    public void DeliberatelySkippedElementsAreNotWarnings()
    {
        SvgImportResult result = Read(
            "<title>a title</title><desc>a description</desc><metadata>data</metadata>" +
            "<defs><symbol id=\"s\"><rect width=\"1\" height=\"1\"/></symbol></defs>" +
            "<rect id=\"shape\" width=\"1\" height=\"1\"/>");

        Assert.Empty(result.Warnings);
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
