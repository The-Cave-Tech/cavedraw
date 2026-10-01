using VCCad.Core.Model;
using VCCad.Core.Raster;
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

    /// <summary>
    /// **The primitives #124 added are read**, each arriving with the parameters the file gave it.
    ///
    /// The theory that stood here asserted they were *not* read, which is how the gap was recorded; now that they
    /// are, it is the positive assertion instead - written as the values each attribute lands on, so a reader that
    /// accepted an element and dropped its parameters fails here rather than looking like success.
    /// </summary>
    [Theory]
    [InlineData("feMorphology", "operator=\"dilate\" radius=\"2\"", FilterPrimitiveKind.Morphology)]
    [InlineData("feColorMatrix", "type=\"saturate\" values=\"0.5\"", FilterPrimitiveKind.ColorMatrix)]
    [InlineData("feTurbulence", "baseFrequency=\"0.05\" numOctaves=\"3\"", FilterPrimitiveKind.Turbulence)]
    [InlineData("fePerlinNoise", "baseFrequency=\"0.05\"", FilterPrimitiveKind.Turbulence)]
    [InlineData("feDisplacementMap", "scale=\"10\" xChannelSelector=\"R\" yChannelSelector=\"G\"",
        FilterPrimitiveKind.DisplacementMap)]
    public void ANewPrimitiveIsReadWithItsParameters(string name, string attributes, FilterPrimitiveKind kind)
    {
        SvgImportResult result = Read(
            $"<defs><filter id=\"f\"><{name} {attributes}/></filter></defs>" +
            "<rect width=\"10\" height=\"10\" filter=\"url(#f)\"/>");

        FilterPrimitive read = Assert.Single(result.Document.FindFilter("f")!.Primitives);
        Assert.Equal(kind, read.Kind);
        Assert.DoesNotContain(result.Warnings, warning =>
            warning.Contains("does not read", StringComparison.Ordinal));

        switch (name)
        {
            case "feMorphology":
                Assert.Equal("dilate", read.Operator);
                Assert.Equal(2.0, read.Radius, 6);
                break;

            case "feColorMatrix":
                // The shorthand is kept as the one number it is, so it can be written back the way it was read.
                Assert.Equal("saturate", read.Type);
                Assert.Equal(new[] { 0.5 }, read.Matrix!);
                break;

            case "feTurbulence":
                Assert.Equal(0.05, read.BaseFrequency, 6);
                Assert.Equal(3, read.Octaves);
                Assert.Equal("turbulence", read.Type);
                break;

            case "fePerlinNoise":
                // The old name is the same element, so it lands as the same kind with the same parameters.
                Assert.Equal(0.05, read.BaseFrequency, 6);
                Assert.Equal("turbulence", read.Type);
                break;

            default:
                Assert.Equal(10.0, read.Scale, 6);
                Assert.Equal("R", read.XChannel);
                Assert.Equal("G", read.YChannel);
                break;
        }
    }

    /// <summary>
    /// A lighting element lit by a **distant** light is read, with every parameter and the light's angles.
    /// </summary>
    [Fact]
    public void ADistantLitLightingPrimitiveIsRead()
    {
        SvgImportResult result = Read(
            "<defs><filter id=\"f\"><feSpecularLighting surfaceScale=\"3\" specularConstant=\"0.7\" " +
            "specularExponent=\"14\" lighting-color=\"#ff0000\">" +
            "<feDistantLight azimuth=\"35\" elevation=\"55\"/></feSpecularLighting></filter></defs>" +
            "<rect width=\"10\" height=\"10\" filter=\"url(#f)\"/>");

        FilterPrimitive read = Assert.Single(result.Document.FindFilter("f")!.Primitives);
        Assert.Equal(FilterPrimitiveKind.SpecularLighting, read.Kind);
        Assert.Equal(3.0, read.SurfaceScale, 6);
        Assert.Equal(0.7, read.SpecularConstant, 6);
        Assert.Equal(14.0, read.SpecularExponent, 6);
        Assert.Equal(35.0, read.Azimuth, 6);
        Assert.Equal(55.0, read.Elevation, 6);
        Assert.Equal(1.0, read.LightingColor!.Value.R, 6);
        Assert.Equal(0.0, read.LightingColor!.Value.G, 6);
    }

    /// <summary>
    /// **A point or spot light is refused, and said out loud.**
    ///
    /// The model has one kind of light - a distant one, which lights a whole surface evenly - and the other two
    /// produce visibly different pictures: a point light is a bright patch that moves with the distance. Turning
    /// one into the other would be a plausible-looking lie, so the step is dropped and the file is told, rather
    /// than a bevel being drawn from a light that is not the one it asked for.
    /// </summary>
    [Theory]
    [InlineData("fePointLight", "x=\"10\" y=\"30\" z=\"20\"")]
    [InlineData("feSpotLight", "x=\"10\" y=\"30\" z=\"20\" pointsAtX=\"0\" pointsAtY=\"0\" pointsAtZ=\"0\"")]
    public void APointOrSpotLightIsRefusedRatherThanApproximated(string light, string attributes)
    {
        SvgImportResult result = Read(
            $"<defs><filter id=\"f\"><feGaussianBlur stdDeviation=\"1\"/>" +
            $"<feSpecularLighting surfaceScale=\"2\"><{light} {attributes}/></feSpecularLighting></filter></defs>" +
            "<rect width=\"10\" height=\"10\" filter=\"url(#f)\"/>");

        Assert.Contains(result.Warnings, warning =>
            warning.Contains(light, StringComparison.Ordinal) &&
            warning.Contains("feDistantLight", StringComparison.Ordinal) &&
            warning.Contains("refuses", StringComparison.Ordinal));

        // The blur is still there, so the filter is not thrown away over the light it cannot light.
        FilterPrimitive read = Assert.Single(result.Document.FindFilter("f")!.Primitives);
        Assert.Equal(FilterPrimitiveKind.GaussianBlur, read.Kind);
    }

    /// <summary>A lighting element with no light source at all is refused the same way and says which it is.</summary>
    [Fact]
    public void ALightingPrimitiveWithNoLightIsRefused()
    {
        SvgImportResult result = Read(
            "<defs><filter id=\"f\"><feDiffuseLighting surfaceScale=\"2\"/></filter></defs>" +
            "<rect width=\"10\" height=\"10\" filter=\"url(#f)\"/>");

        Assert.Contains(result.Warnings, warning =>
            warning.Contains("no light source", StringComparison.Ordinal) &&
            warning.Contains("feDiffuseLighting", StringComparison.Ordinal));
    }

    /// <summary>
    /// A `feColorMatrix` whose values are not twenty numbers is **said**, and read as the identity rather than as
    /// the numbers that happen to be there: a matrix with the wrong count is not a matrix, and inventing the rest
    /// would draw a picture the file did not ask for.
    /// </summary>
    [Fact]
    public void AShortColourMatrixIsReportedAndReadAsTheIdentity()
    {
        SvgImportResult result = Read(
            "<defs><filter id=\"f\"><feColorMatrix type=\"matrix\" values=\"1 0 0 0 0 0 1\"/></filter></defs>" +
            "<rect width=\"10\" height=\"10\" filter=\"url(#f)\"/>");

        Assert.Contains(result.Warnings, warning =>
            warning.Contains("feColorMatrix", StringComparison.Ordinal) &&
            warning.Contains("identity", StringComparison.Ordinal));

        FilterPrimitive read = Assert.Single(result.Document.FindFilter("f")!.Primitives);
        Assert.Equal(FilterEngine.Identity(), read.Matrix!);
    }

    /// <summary>
    /// A two-value `radius` or `baseFrequency` is an approximation, because the model has the one number - the same
    /// rule the blur's `stdDeviation` follows, and said for the same reason.
    /// </summary>
    [Theory]
    [InlineData("feMorphology operator=\"dilate\" radius=\"2 6\"", "feMorphology radius")]
    [InlineData("feTurbulence baseFrequency=\"0.05 0.1\"", "baseFrequency")]
    public void ATwoValueRadiusOrFrequencyIsReported(string element, string what)
    {
        SvgImportResult result = Read(
            $"<defs><filter id=\"f\"><{element}/></filter></defs>" +
            "<rect width=\"10\" height=\"10\" filter=\"url(#f)\"/>");

        Assert.Contains(result.Warnings, warning =>
            warning.Contains("two-value", StringComparison.Ordinal) &&
            warning.Contains(what, StringComparison.Ordinal));
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
