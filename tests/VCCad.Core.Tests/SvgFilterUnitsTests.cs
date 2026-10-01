using VCCad.Core.Svg;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// A filter region given as a percentage in **user-space** units.
///
/// A percentage there is a percentage of the **viewport**, which is a property of the document rather than of the
/// filter, so the reader cannot resolve it. Reading `-10%` quietly as a tenth of a user unit is a wrong region that
/// nothing reports; reading it as a fraction of the object is the closest honest approximation - and the point of
/// this test is that the approximation is **said**, so a person sees an approximation rather than trusting an exact
/// number.
/// </summary>
public class SvgFilterUnitsTests
{
    private const string Head =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"200\" height=\"200\" viewBox=\"0 0 200 200\">";

    private static SvgImportResult Read(string body) => SvgReader.Read(Head + body + "</svg>");

    [Fact]
    public void APercentageRegionInUserSpaceIsReported()
    {
        SvgImportResult result = Read(
            "<defs><filter id=\"f\" filterUnits=\"userSpaceOnUse\" x=\"-10%\" y=\"-10%\" width=\"120%\" height=\"120%\">" +
            "<feGaussianBlur stdDeviation=\"2\"/></filter></defs>" +
            "<rect width=\"10\" height=\"10\" filter=\"url(#f)\"/>");

        Assert.Contains(result.Warnings, warning =>
            warning.Contains("userSpaceOnUse", StringComparison.Ordinal) &&
            warning.Contains("viewport", StringComparison.Ordinal));
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

    /// <summary>And the object-bounding-box default, where percentages are exact, says nothing either.</summary>
    [Fact]
    public void AnObjectBoundingBoxRegionIsNotReported()
    {
        SvgImportResult result = Read(
            "<defs><filter id=\"f\" x=\"-10%\" y=\"-10%\" width=\"120%\" height=\"120%\">" +
            "<feGaussianBlur stdDeviation=\"2\"/></filter></defs>" +
            "<rect width=\"10\" height=\"10\" filter=\"url(#f)\"/>");

        Assert.DoesNotContain(result.Warnings, warning => warning.Contains("viewport", StringComparison.Ordinal));
        Assert.Equal(-0.1, result.Document.FindFilter("f")!.X, 6);
    }
}
