using VCCad.Core.Model;
using VCCad.Core.Svg;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// The gradient features and references this build cannot honour exactly.
///
/// Both are silent-wrong-picture cases: a radial gradient's **focal point** moved the highlight off centre and the
/// model cannot hold one, and a reference to a paint server that is not in the file leaves the shape filled with
/// something the file never asked for. Neither raises an error, which is exactly why they have to be said.
/// </summary>
public class SvgGradientGapTests
{
    private const string Head =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"200\" height=\"100\" viewBox=\"0 0 200 100\">";

    private static SvgImportResult Read(string body) => SvgReader.Read(Head + body + "</svg>");

    private const string Stops =
        "<stop offset=\"0\" stop-color=\"#ff0000\"/><stop offset=\"1\" stop-color=\"#0000ff\"/>";

    [Fact]
    public void ARadialFocalPointIsReported()
    {
        SvgImportResult result = Read(
            "<defs><radialGradient id=\"g\" cx=\"0.5\" cy=\"0.5\" r=\"0.5\" fx=\"0.2\" fy=\"0.3\">" + Stops +
            "</radialGradient></defs>" +
            "<rect id=\"shape\" width=\"10\" height=\"10\" fill=\"url(#g)\"/>");

        Assert.Contains(result.Warnings, warning =>
            warning.Contains("focal point", StringComparison.Ordinal) &&
            warning.Contains("cx/cy", StringComparison.Ordinal));

        // The gradient is still read, centred where its centre says, so the picture is the closest it can be.
        GradientSpec gradient = result.Document.AllPaths().Single().Fill.Gradient!;
        Assert.Equal(0.5, gradient.Center.X, 6);
        Assert.Equal(0.5, gradient.Center.Y, 6);
    }

    /// <summary>A focal point that **is** the centre loses nothing, so nothing is said.</summary>
    [Fact]
    public void ACentredFocalPointIsNotReported()
    {
        SvgImportResult result = Read(
            "<defs><radialGradient id=\"g\" cx=\"0.5\" cy=\"0.5\" r=\"0.5\" fx=\"0.5\" fy=\"0.5\">" + Stops +
            "</radialGradient></defs>" +
            "<rect width=\"10\" height=\"10\" fill=\"url(#g)\"/>");

        Assert.DoesNotContain(result.Warnings, warning => warning.Contains("focal point", StringComparison.Ordinal));
    }

    /// <summary>An ordinary radial gradient, which is most of them, says nothing at all.</summary>
    [Fact]
    public void AnOrdinaryRadialGradientIsNotReported()
    {
        SvgImportResult result = Read(
            "<defs><radialGradient id=\"g\">" + Stops + "</radialGradient></defs>" +
            "<rect width=\"10\" height=\"10\" fill=\"url(#g)\"/>");

        Assert.Empty(result.Warnings);
    }

    /// <summary>A fill referring to a paint server the file does not contain is named, not left to be discovered.</summary>
    [Fact]
    public void AMissingPaintServerIsReported()
    {
        SvgImportResult result = Read("<rect width=\"10\" height=\"10\" fill=\"url(#nothing)\"/>");

        Assert.Contains(result.Warnings, warning =>
            warning.Contains("no paint server", StringComparison.Ordinal) &&
            warning.Contains("nothing", StringComparison.Ordinal));

        // And the shape is still there, because a missing fill is not a reason to lose the geometry.
        Assert.Single(result.Document.AllPaths());
    }

    /// <summary>A reference that **does** resolve says nothing.</summary>
    [Fact]
    public void AResolvedPaintServerIsNotReported()
    {
        SvgImportResult result = Read(
            "<defs><linearGradient id=\"g\">" + Stops + "</linearGradient></defs>" +
            "<rect width=\"10\" height=\"10\" fill=\"url(#g)\"/>");

        Assert.DoesNotContain(result.Warnings, warning => warning.Contains("paint server", StringComparison.Ordinal));
    }
}
