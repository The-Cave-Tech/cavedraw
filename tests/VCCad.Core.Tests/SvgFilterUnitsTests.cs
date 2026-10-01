using VCCad.Core.Svg;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// A filter region written as a **percentage**, which is a fraction of whichever box `filterUnits` names.
///
/// `x="10%"` is neither a tenth of a user unit nor the region's default: under the default `objectBoundingBox`
/// units it is a tenth of the object's own box, and under `userSpaceOnUse` a tenth of the **viewport**. Reading it
/// as the default - which is what `SvgFilters.Fraction` did - imported a region the file never wrote, with nothing
/// reported (#144). A percentage with no reference box to be a fraction of is the one case that cannot be resolved,
/// and it is **said** rather than quietly given a plausible number.
/// </summary>
public class SvgFilterUnitsTests
{
    private const string Head =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"200\" height=\"200\" viewBox=\"0 0 200 200\">";

    /// <summary>A document that states no size at all: no width, no height and no view box.</summary>
    private const string Sizeless = "<svg xmlns=\"http://www.w3.org/2000/svg\">";

    private static SvgImportResult Read(string body) => SvgReader.Read(Head + body + "</svg>");

    private static SvgImportResult ReadSizeless(string body) => SvgReader.Read(Sizeless + body + "</svg>");

    private static double Region(SvgImportResult result, string name)
    {
        var filter = result.Document.FindFilter("f")!;
        return name switch
        {
            "x" => filter.X,
            "y" => filter.Y,
            "width" => filter.Width,
            _ => filter.Height,
        };
    }

    /// <summary>
    /// **A user-space percentage is a fraction of the viewport.** It was reported as an approximation and read as
    /// the region's default; the document's own viewport is what SVG measures it against, and the viewport is
    /// stated by the root the reader is already holding.
    /// </summary>
    [Fact]
    public void APercentageRegionInUserSpaceResolvesAgainstTheViewport()
    {
        SvgImportResult result = Read(
            "<defs><filter id=\"f\" filterUnits=\"userSpaceOnUse\" x=\"10%\" y=\"10%\" width=\"90%\" height=\"80%\">" +
            "<feGaussianBlur stdDeviation=\"2\"/></filter></defs>" +
            "<rect width=\"10\" height=\"10\" filter=\"url(#f)\"/>");

        Assert.Equal(20.0, Region(result, "x"), 6);
        Assert.Equal(20.0, Region(result, "y"), 6);
        Assert.Equal(180.0, Region(result, "width"), 6);
        Assert.Equal(160.0, Region(result, "height"), 6);

        Assert.DoesNotContain(result.Warnings, warning => warning.Contains("viewport", StringComparison.Ordinal));
    }

    /// <summary>
    /// **The same percentage under the default units is a fraction of the object's box** - which the model already
    /// stores, so the two unit modes give four different numbers from one spelling of the region, and each is the
    /// one SVG defines.
    /// </summary>
    [Fact]
    public void TheSamePercentageUnderObjectBoundingBoxIsAFractionOfTheBox()
    {
        SvgImportResult result = Read(
            "<defs><filter id=\"f\" x=\"10%\" y=\"10%\" width=\"90%\" height=\"80%\">" +
            "<feGaussianBlur stdDeviation=\"2\"/></filter></defs>" +
            "<rect width=\"10\" height=\"10\" filter=\"url(#f)\"/>");

        Assert.Equal(0.10, Region(result, "x"), 6);
        Assert.Equal(0.10, Region(result, "y"), 6);
        Assert.Equal(0.90, Region(result, "width"), 6);
        Assert.Equal(0.80, Region(result, "height"), 6);

        Assert.DoesNotContain(result.Warnings, warning => warning.Contains("viewport", StringComparison.Ordinal));
    }

    /// <summary>
    /// A coordinate is a fraction of the viewport's **width** and a vertical one of its **height**, so the same
    /// percentage is a different length on each axis of a viewport that is not square. Resolving both against one
    /// dimension would put a region in the right place on a square page and the wrong one everywhere else.
    /// </summary>
    [Fact]
    public void EachAxisResolvesAgainstItsOwnViewportDimension()
    {
        SvgImportResult result = SvgReader.Read(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"200\" height=\"100\" viewBox=\"0 0 200 100\">" +
            "<defs><filter id=\"f\" filterUnits=\"userSpaceOnUse\" x=\"10%\" y=\"10%\" width=\"50%\" height=\"50%\">" +
            "<feGaussianBlur stdDeviation=\"2\"/></filter></defs>" +
            "<rect width=\"10\" height=\"10\" filter=\"url(#f)\"/></svg>");

        Assert.Equal(20.0, Region(result, "x"), 6);
        Assert.Equal(10.0, Region(result, "y"), 6);
        Assert.Equal(100.0, Region(result, "width"), 6);
        Assert.Equal(50.0, Region(result, "height"), 6);
    }

    /// <summary>The same units with **absolute** values are exact, so nothing is said about them.</summary>
    [Fact]
    public void AUserSpaceRegionWithNumbersIsNotReported()
    {
        SvgImportResult result = Read(
            "<defs><filter id=\"f\" filterUnits=\"userSpaceOnUse\" x=\"0\" y=\"0\" width=\"200\" height=\"200\">" +
            "<feGaussianBlur stdDeviation=\"2\"/></filter></defs>" +
            "<rect width=\"10\" height=\"10\" filter=\"url(#f)\"/>");

        Assert.DoesNotContain(result.Warnings, warning => warning.Contains("userSpaceOnUse", StringComparison.Ordinal));
        Assert.Equal(200.0, result.Document.FindFilter("f")!.Width, 6);
    }

    /// <summary>
    /// **SVG's initial values are percentages, so an omitted attribute is resolved like a written one.** A
    /// `userSpaceOnUse` filter that names no region is ten per cent of margin around the viewport and 120% of it -
    /// not `-0.1` of a user unit, which is a region narrower than a hair and was stored for every such file.
    /// </summary>
    [Fact]
    public void AUserSpaceRegionTheFileOmitsIsStillTheViewportsPercentages()
    {
        SvgImportResult result = Read(
            "<defs><filter id=\"f\" filterUnits=\"userSpaceOnUse\"><feGaussianBlur stdDeviation=\"2\"/></filter></defs>" +
            "<rect width=\"10\" height=\"10\" filter=\"url(#f)\"/>");

        Assert.Equal(-20.0, Region(result, "x"), 6);
        Assert.Equal(-20.0, Region(result, "y"), 6);
        Assert.Equal(240.0, Region(result, "width"), 6);
        Assert.Equal(240.0, Region(result, "height"), 6);
    }

    /// <summary>
    /// **A percentage that cannot be resolved is reported, never substituted quietly.** The document states no
    /// viewport for a user-space percentage to be a fraction of, so there is no honest number - the region's
    /// default stands and the file is told, because a resolved-looking default is indistinguishable from a region
    /// the file actually wrote.
    /// </summary>
    [Fact]
    public void APercentageWithNoViewportToResolveAgainstIsReported()
    {
        SvgImportResult result = ReadSizeless(
            "<defs><filter id=\"f\" filterUnits=\"userSpaceOnUse\" x=\"10%\" y=\"10%\" width=\"90%\" height=\"80%\">" +
            "<feGaussianBlur stdDeviation=\"2\"/></filter></defs>" +
            "<rect width=\"10\" height=\"10\" filter=\"url(#f)\"/>");

        Assert.Contains(result.Warnings, warning =>
            warning.Contains("userSpaceOnUse", StringComparison.Ordinal) &&
            warning.Contains("states no viewport", StringComparison.Ordinal));

        // What stands is SVG's own initial value for the attribute, not the percentage read as a user unit.
        Assert.Equal(-0.1, Region(result, "x"), 6);
        Assert.Equal(1.2, Region(result, "width"), 6);
    }
}
