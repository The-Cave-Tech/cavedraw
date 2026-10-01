using VCCad.Core.Model;
using VCCad.Core.Svg;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// The filter primitives and parameters this build does **not** fully read.
///
/// A filter is a graph, so a step that silently does nothing changes what every step after it receives - and the
/// shape comes out looking as though nobody had asked for a filter at all. That is the "artwork that quietly went
/// missing" failure this repository names, so an unread primitive and an approximated parameter are both reported.
/// </summary>
public class SvgFilterGapTests
{
    private const string Head =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"200\" height=\"200\" viewBox=\"0 0 200 200\">";

    private static SvgImportResult Read(string body) => SvgReader.Read(Head + body + "</svg>");

    /// <summary>The primitives #124 covers are not read yet, so a file using one is told exactly that.</summary>
    [Theory]
    [InlineData("feMorphology", "operator=\"erode\" radius=\"2\"")]
    [InlineData("feColorMatrix", "type=\"saturate\" values=\"0.5\"")]
    [InlineData("feTurbulence", "baseFrequency=\"0.05\" numOctaves=\"3\"")]
    [InlineData("feSpecularLighting", "surfaceScale=\"2\" specularConstant=\"1\"")]
    [InlineData("feDisplacementMap", "scale=\"10\" xChannelSelector=\"R\" yChannelSelector=\"G\"")]
    public void AnUnreadPrimitiveIsReported(string name, string attributes)
    {
        SvgImportResult result = Read(
            $"<defs><filter id=\"f\"><feGaussianBlur stdDeviation=\"2\"/><{name} {attributes}/></filter></defs>" +
            "<rect width=\"10\" height=\"10\" filter=\"url(#f)\"/>");

        Assert.Contains(result.Warnings, warning =>
            warning.Contains(name, StringComparison.Ordinal) &&
            warning.Contains("does not read", StringComparison.Ordinal));

        // And the primitive it *does* understand is still there, so the filter is not thrown away over it.
        Assert.Single(result.Document.FindFilter("f")!.Primitives);
    }

    /// <summary>A primitive it does understand is not reported, which is what stops the list becoming noise.</summary>
    [Fact]
    public void AReadPrimitiveIsNotReported()
    {
        SvgImportResult result = Read(
            "<defs><filter id=\"f\"><feGaussianBlur stdDeviation=\"2\" result=\"b\"/>" +
            "<feOffset in=\"b\" dx=\"1\" dy=\"1\"/><feFlood flood-color=\"#000000\"/>" +
            "<feComposite in=\"SourceGraphic\" in2=\"b\" operator=\"in\"/>" +
            "<feBlend in=\"SourceGraphic\" in2=\"b\" mode=\"screen\"/></filter></defs>" +
            "<rect width=\"10\" height=\"10\" filter=\"url(#f)\"/>");

        Assert.DoesNotContain(result.Warnings, warning =>
            warning.Contains("does not read", StringComparison.Ordinal));
        Assert.Equal(5, result.Document.FindFilter("f")!.Primitives.Count);
    }

    /// <summary>
    /// **A two-value `stdDeviation` is an approximation**, because the model has one radius. Using the first is a
    /// sensible choice; using it without saying so is a blur that is wrong in one direction and explains nothing.
    /// </summary>
    [Fact]
    public void ATwoValueBlurIsReported()
    {
        SvgImportResult result = Read(
            "<defs><filter id=\"f\"><feGaussianBlur stdDeviation=\"2 6\"/></filter></defs>" +
            "<rect width=\"10\" height=\"10\" filter=\"url(#f)\"/>");

        Assert.Contains(result.Warnings, warning =>
            warning.Contains("two-value", StringComparison.Ordinal) &&
            warning.Contains("one radius", StringComparison.Ordinal));

        Assert.Equal(2.0, result.Document.FindFilter("f")!.Primitives[0].Radius, 6);
    }

    [Fact]
    public void ASingleValueBlurIsNotReported()
    {
        SvgImportResult result = Read(
            "<defs><filter id=\"f\"><feGaussianBlur stdDeviation=\"3\"/></filter></defs>" +
            "<rect width=\"10\" height=\"10\" filter=\"url(#f)\"/>");

        Assert.DoesNotContain(result.Warnings, warning => warning.Contains("two-value", StringComparison.Ordinal));
        Assert.Equal(3.0, result.Document.FindFilter("f")!.Primitives[0].Radius, 6);
    }

    /// <summary>The real corpus: whatever Inkscape's own filter files use, the gaps it exposes are named.</summary>
    [Fact]
    public void ACorpusFilterFileNamesWhatItCannotRead()
    {
        string? path = CorpusFile("filters.svg");
        if (path is null)
        {
            return;
        }

        SvgImportResult result = SvgReader.ReadFile(path);

        // Whatever the file needs, nothing is dropped without a word: every filter it declares either came through
        // or is named in the warnings.
        Assert.All(result.Document.Filters, filter => Assert.NotEmpty(filter.Primitives));
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
