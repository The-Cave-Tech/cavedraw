using VCCad.Core.Model;
using VCCad.Core.Svg;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// **One rule for a focus that does not fit, stated once and shared.** SVG's own rule is that a
/// focal point outside the radial is moved onto its edge, along the line from the centre; the model
/// states it as <see cref="GradientSpec.ClampedFocalPoint"/>, and the reader reaches the same
/// answer. They are pinned against each other here because the two were once written out
/// separately - and the PDF importer had a third, unstated behaviour - which is how the same
/// picture, imported twice, becomes two documents.
///
/// The reader's answer is the one that counts: the model's rule is only worth having if a file and
/// an operation that name the same outside point land on the same coordinate.
/// </summary>
public class GradientFocalPointRuleTests
{
    private static GradientSpec Read(string focusX, string focusY)
    {
        SvgImportResult result = SvgReader.Read(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"200\" height=\"200\" viewBox=\"0 0 200 200\">" +
            "<defs><radialGradient id=\"g\" cx=\"50%\" cy=\"50%\" r=\"25%\" " +
            $"fx=\"{focusX}\" fy=\"{focusY}\">" +
            "<stop offset=\"0\" stop-color=\"#ffffff\"/><stop offset=\"1\" stop-color=\"#000000\"/>" +
            "</radialGradient></defs>" +
            "<rect id=\"shape\" width=\"100\" height=\"50\" fill=\"url(#g)\"/></svg>");

        return result.Document.AllPaths().Single().Fill.Gradient!;
    }

    /// <summary>
    /// Every direction past the edge - along an axis, on the diagonal, and out the other way -
    /// because a clamp that only knows one quadrant is a clamp that works on the sample that
    /// prompted it.
    /// </summary>
    [Theory]
    [InlineData("80%", "90%")]
    [InlineData("200%", "50%")]
    [InlineData("50%", "-300%")]
    [InlineData("-100%", "-100%")]
    public void AnOutsideFocusIsClampedTheSameWayByTheReaderAndByTheModel(string focusX, string focusY)
    {
        GradientSpec gradient = Read(focusX, focusY);

        Assert.NotNull(gradient.FocalPoint);
        Assert.Equal(gradient.ClampedFocalPoint(), gradient.FocalPoint);

        // And it is ON the edge rather than merely somewhere inside: the point of the rule is that
        // the highlight keeps pointing where the file pointed it, as far out as it can go.
        Point2D focus = gradient.FocalPoint!.Value;
        double dx = (focus.X - gradient.Center.X) / gradient.RadiusX;
        double dy = (focus.Y - gradient.Center.Y) / gradient.RadiusY;
        Assert.Equal(1.0, Math.Sqrt((dx * dx) + (dy * dy)), 6);
    }

    /// <summary>
    /// A focus already inside is left exactly where it was: clamping is a rule for the points that
    /// do not fit, and a "clamp" that moved a point that fits would be a recentring by stealth.
    /// </summary>
    [Fact]
    public void AFocusInsideTheEllipseIsLeftAlone()
    {
        GradientSpec gradient = Read("30%", "40%");

        Assert.NotNull(gradient.FocalPoint);
        Assert.Equal(0.30, gradient.FocalPoint!.Value.X, 6);
        Assert.Equal(0.40, gradient.FocalPoint.Value.Y, 6);
        Assert.Equal(gradient.FocalPoint, gradient.ClampedFocalPoint());
    }

    /// <summary>
    /// A focus the file names ON the centre is no focus at all - the model keeps null for the
    /// picture a concentric gradient paints - and the model's own rule agrees rather than inventing
    /// a coordinate the file never wrote.
    /// </summary>
    [Fact]
    public void AFocusOnTheCentreIsNoFocusInEitherPlace()
    {
        GradientSpec gradient = Read("50%", "50%");

        Assert.Null(gradient.FocalPoint);
        Assert.Null(gradient.ClampedFocalPoint());
    }
}
