using VCCad.Core.Model;
using VCCad.Core.Svg;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// SVG paint servers read into the model's gradients.
///
/// The model stores a gradient's geometry **normalised to the shape's box**, which is SVG's default
/// (`objectBoundingBox`) - so the common case is a direct translation, and `userSpaceOnUse` is the one that has to
/// be converted. Assertions are on the gradient's geometry and stops, because a gradient that imports as "a
/// gradient" but points the wrong way is the failure that looks like a broken transform.
/// </summary>
public class SvgGradientTests
{
    private const string Head =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" " +
        "width=\"200\" height=\"200\" viewBox=\"0 0 200 200\">";

    private static SvgImportResult Read(string body) => SvgReader.Read(Head + body + "</svg>");

    private static GradientSpec Gradient(SvgImportResult result, string pathId = "shape")
        => result.Document.AllPaths().Single(p => p.Name == pathId).Fill.Gradient!;

    /// <summary>
    /// The same gradient, compared field by field at the precision the writer keeps.
    ///
    /// The record's own equality would compare the stop list by reference, so it would report two identical
    /// gradients as different - the trap this codebase avoids in four other places.
    /// </summary>
    private static void AssertSameGradient(GradientSpec expected, GradientSpec actual)
    {
        Assert.Equal(expected.Kind, actual.Kind);
        Assert.Equal(expected.Spread, actual.Spread);
        Assert.Equal(expected.Center.X, actual.Center.X, 5);
        Assert.Equal(expected.Center.Y, actual.Center.Y, 5);
        Assert.Equal(expected.RadiusX, actual.RadiusX, 5);
        Assert.Equal(expected.RadiusY, actual.RadiusY, 5);

        if (expected.FocalPoint is { } wanted)
        {
            Assert.NotNull(actual.FocalPoint);
            Assert.Equal(wanted.X, actual.FocalPoint!.Value.X, 5);
            Assert.Equal(wanted.Y, actual.FocalPoint!.Value.Y, 5);
        }
        else
        {
            Assert.Null(actual.FocalPoint);
        }
    }

    private const string TwoStops =
        "<stop offset=\"0\" stop-color=\"#ff0000\"/><stop offset=\"1\" stop-color=\"#0000ff\"/>";

    // ---------------------------------------------------------------- the basics

    [Fact]
    public void ALinearGradientBecomesTheShapesFill()
    {
        SvgImportResult result = Read(
            "<defs><linearGradient id=\"g\">" + TwoStops + "</linearGradient></defs>" +
            "<rect id=\"shape\" x=\"0\" y=\"0\" width=\"100\" height=\"50\" fill=\"url(#g)\"/>");

        GradientSpec gradient = Gradient(result);

        Assert.Equal(GradientKind.Linear, gradient.Kind);
        Assert.Equal(2, gradient.Stops.Count);
        Assert.Equal(1.0, gradient.Stops[0].Color.R, 6);
        Assert.Equal(1.0, gradient.Stops[1].Color.B, 6);

        // The default direction is left to right across the box, which is x1=0 x2=1.
        Assert.Equal(0.0, gradient.Start.X, 6);
        Assert.Equal(1.0, gradient.End.X, 6);
    }

    [Fact]
    public void ARadialGradientKeepsItsCentreAndRadius()
    {
        SvgImportResult result = Read(
            "<defs><radialGradient id=\"g\" cx=\"0.25\" cy=\"0.75\" r=\"0.5\">" + TwoStops + "</radialGradient></defs>" +
            "<rect id=\"shape\" width=\"100\" height=\"50\" fill=\"url(#g)\"/>");

        GradientSpec gradient = Gradient(result);

        Assert.Equal(GradientKind.Radial, gradient.Kind);
        Assert.Equal(0.25, gradient.Center.X, 6);
        Assert.Equal(0.75, gradient.Center.Y, 6);
        Assert.Equal(0.5, gradient.RadiusX, 6);
    }

    /// <summary>
    /// **Both unit modes put the same colours on the same shape.** `objectBoundingBox` is already what the model
    /// stores; `userSpaceOnUse` is absolute and has to be converted against the shape's box. A 100x50 rectangle
    /// occupying (0,0)-(100,50) is the case where the two are told apart: a fraction-versus-absolute mix-up puts
    /// the gradient a hundred times too far out.
    /// </summary>
    [Fact]
    public void BothUnitModesGiveTheSameGeometryForTheSameShape()
    {
        SvgImportResult box = Read(
            "<defs><linearGradient id=\"g\" x1=\"0\" y1=\"0\" x2=\"1\" y2=\"0\">" + TwoStops + "</linearGradient></defs>" +
            "<rect id=\"shape\" x=\"0\" y=\"0\" width=\"100\" height=\"50\" fill=\"url(#g)\"/>");

        SvgImportResult user = Read(
            "<defs><linearGradient id=\"g\" gradientUnits=\"userSpaceOnUse\" x1=\"0\" y1=\"0\" x2=\"100\" y2=\"0\">" +
            TwoStops + "</linearGradient></defs>" +
            "<rect id=\"shape\" x=\"0\" y=\"0\" width=\"100\" height=\"50\" fill=\"url(#g)\"/>");

        GradientSpec a = Gradient(box);
        GradientSpec b = Gradient(user, "shape");

        Assert.Equal(a.Start.X, b.Start.X, 6);
        Assert.Equal(a.Start.Y, b.Start.Y, 6);
        Assert.Equal(a.End.X, b.End.X, 6);
        Assert.Equal(a.End.Y, b.End.Y, 6);
    }

    /// <summary>A user-space gradient on a shape that is not at the origin is offset by the shape's own position.</summary>
    [Fact]
    public void UserSpaceIsRelativeToTheShapePosition()
    {
        SvgImportResult result = Read(
            "<defs><linearGradient id=\"g\" gradientUnits=\"userSpaceOnUse\" x1=\"50\" y1=\"25\" x2=\"150\" y2=\"25\">" +
            TwoStops + "</linearGradient></defs>" +
            "<rect id=\"shape\" x=\"50\" y=\"25\" width=\"100\" height=\"50\" fill=\"url(#g)\"/>");

        GradientSpec gradient = Gradient(result);

        // The gradient starts at the shape's top-left corner and ends one width along, which is the whole box.
        Assert.Equal(0.0, gradient.Start.X, 6);
        Assert.Equal(100.0 / 100.0, gradient.End.X - gradient.Start.X, 6);
    }

    /// <summary>**A gradient written in two pieces.** One element carries the stops, another the geometry, and the
    /// second refers to the first by `href` - the shape Inkscape actually writes.</summary>
    [Fact]
    public void AGradientInheritsThroughHref()
    {
        SvgImportResult result = Read(
            "<defs>" +
            "<linearGradient id=\"base\">" + TwoStops + "</linearGradient>" +
            "<linearGradient id=\"derived\" xlink:href=\"#base\" x1=\"0\" y1=\"0\" x2=\"0\" y2=\"1\"/>" +
            "</defs>" +
            "<rect id=\"shape\" width=\"100\" height=\"50\" fill=\"url(#derived)\"/>");

        GradientSpec gradient = Gradient(result);

        // The stops came from the referenced gradient, the direction from this one.
        Assert.Equal(2, gradient.Stops.Count);
        Assert.Equal(1.0, gradient.Stops[0].Color.R, 6);
        Assert.Equal(0.0, gradient.Start.Y, 6);
        Assert.Equal(1.0, gradient.End.Y, 6);
    }

    [Fact]
    public void SpreadMethodMapsToTheModelsSpread()
    {
        SvgImportResult result = Read(
            "<defs><linearGradient id=\"g\" spreadMethod=\"reflect\">" + TwoStops + "</linearGradient></defs>" +
            "<rect id=\"shape\" width=\"10\" height=\"10\" fill=\"url(#g)\"/>" +
            "<defs><linearGradient id=\"h\" spreadMethod=\"repeat\">" + TwoStops + "</linearGradient></defs>" +
            "<rect id=\"other\" width=\"10\" height=\"10\" fill=\"url(#h)\"/>");

        Assert.Equal(GradientSpread.Reflect, Gradient(result).Spread);
        Assert.Equal(GradientSpread.Repeat, Gradient(result, "other").Spread);
    }

    /// <summary>Stops are ordered by offset whatever order they are written in, and keep their own opacity.</summary>
    [Fact]
    public void StopsAreOrderedAndKeepTheirOpacity()
    {
        SvgImportResult result = Read(
            "<defs><linearGradient id=\"g\">" +
            "<stop offset=\"1\" stop-color=\"#0000ff\"/>" +
            "<stop offset=\"0\" stop-color=\"#ff0000\" stop-opacity=\"0.25\"/>" +
            "</linearGradient></defs>" +
            "<rect id=\"shape\" width=\"10\" height=\"10\" fill=\"url(#g)\"/>");

        GradientSpec gradient = Gradient(result);

        Assert.Equal(0.0, gradient.Stops[0].Position, 6);
        Assert.Equal(1.0, gradient.Stops[0].Color.R, 6);
        Assert.Equal(0.25, gradient.Stops[0].Opacity, 6);
        Assert.Equal(1.0, gradient.Stops[1].Position, 6);
    }

    /// <summary>A gradient paints through a stylesheet as readily as through an attribute.</summary>
    [Fact]
    public void AGradientCanBeAppliedFromAStylesheet()
    {
        SvgImportResult result = Read(
            "<style>#shape { fill: url(#g); }</style>" +
            "<defs><linearGradient id=\"g\">" + TwoStops + "</linearGradient></defs>" +
            "<rect id=\"shape\" width=\"10\" height=\"10\" fill=\"#00ff00\"/>");

        Assert.Equal(GradientKind.Linear, Gradient(result).Kind);
    }

    /// <summary>
    /// **A gradient on a rotated shape is not rotated with it.** `objectBoundingBox` coordinates are the shape's own
    /// box, which is what the model stores - so the gradient geometry is the same whether the shape is turned or
    /// not, and a reader that resolved the gradient after baking in the transform would turn it twice.
    /// </summary>
    [Fact]
    public void ARotatedShapeKeepsItsGradientGeometry()
    {
        SvgImportResult straight = Read(
            "<defs><linearGradient id=\"g\" x1=\"0\" y1=\"0\" x2=\"1\" y2=\"0\">" + TwoStops + "</linearGradient></defs>" +
            "<rect id=\"shape\" width=\"100\" height=\"50\" fill=\"url(#g)\"/>");

        SvgImportResult rotated = Read(
            "<defs><linearGradient id=\"g\" x1=\"0\" y1=\"0\" x2=\"1\" y2=\"0\">" + TwoStops + "</linearGradient></defs>" +
            "<rect id=\"shape\" width=\"100\" height=\"50\" transform=\"rotate(30)\" fill=\"url(#g)\"/>");

        GradientSpec a = Gradient(straight);
        GradientSpec b = Gradient(rotated);

        Assert.Equal(a.Start.X, b.Start.X, 6);
        Assert.Equal(a.End.X, b.End.X, 6);
        Assert.Equal(a.Start.Y, b.Start.Y, 6);
        Assert.Equal(a.End.Y, b.End.Y, 6);
    }

    /// <summary>A `gradientTransform` moves the gradient without moving the shape, which is how a gradient is
    /// aimed at an angle without rotating what it paints.</summary>
    [Fact]
    public void AGradientTransformMovesTheGradient()
    {
        SvgImportResult result = Read(
            "<defs><linearGradient id=\"g\" x1=\"0\" y1=\"0\" x2=\"1\" y2=\"0\" gradientTransform=\"translate(0,0.5)\">" +
            TwoStops + "</linearGradient></defs>" +
            "<rect id=\"shape\" width=\"100\" height=\"50\" fill=\"url(#g)\"/>");

        GradientSpec gradient = Gradient(result);

        // The gradient is unchanged in direction and shifted half a box down.
        Assert.Equal(0.0, gradient.Start.X, 6);
        Assert.Equal(0.5, gradient.Start.Y, 6);
        Assert.Equal(1.0, gradient.End.X, 6);
        Assert.Equal(0.5, gradient.End.Y, 6);
    }

    /// <summary>A reference to a gradient the document does not contain leaves the shape fillable rather than
    /// invisible, and does not throw.</summary>
    [Fact]
    public void AMissingGradientDoesNotBreakTheShape()
    {
        SvgImportResult result = Read("<rect id=\"shape\" width=\"10\" height=\"10\" fill=\"url(#nothing)\"/>");

        Assert.True(result.Document.AllPaths().Count() == 1);
    }

    [Fact]
    public void AGradientWithNoStopsIsNotAGradient()
    {
        SvgImportResult result = Read(
            "<defs><linearGradient id=\"g\"/></defs>" +
            "<rect id=\"shape\" width=\"10\" height=\"10\" fill=\"url(#g)\"/>");

        Assert.Null(Gradient(result));
    }

    // ---------------------------------------------------------------- the radial focal point

    /// <summary>
    /// **`fx`/`fy` move the highlight, and a gradient that reads without them is a different picture.**
    /// A radial's focus defaults to its centre, so dropping it on the way in recentres a spot the file put
    /// deliberately off to one side - which is the failure that looks like the shape's fill is wrong.
    /// </summary>
    [Fact]
    public void ARadialGradientKeepsItsFocalPoint()
    {
        SvgImportResult result = Read(
            "<defs><radialGradient id=\"g\" cx=\"0.5\" cy=\"0.5\" r=\"0.5\" fx=\"0.2\" fy=\"0.3\">" + TwoStops +
            "</radialGradient></defs>" +
            "<rect id=\"shape\" width=\"100\" height=\"50\" fill=\"url(#g)\"/>");

        GradientSpec gradient = Gradient(result);

        Assert.Equal(0.5, gradient.Center.X, 6);
        Assert.Equal(0.5, gradient.Center.Y, 6);
        Assert.NotNull(gradient.FocalPoint);
        Assert.Equal(0.2, gradient.FocalPoint!.Value.X, 6);
        Assert.Equal(0.3, gradient.FocalPoint!.Value.Y, 6);
    }

    /// <summary>A focus given as percentages or in user space lands where the identical focus in the default
    /// units does - the same two modes, and the same conversion, as the centre and radius already use.</summary>
    [Fact]
    public void TheFocalPointUnderstandsPercentagesAndUserSpace()
    {
        SvgImportResult percent = Read(
            "<defs><radialGradient id=\"g\" cx=\"50%\" cy=\"50%\" r=\"50%\" fx=\"35%\" fy=\"30%\">" + TwoStops +
            "</radialGradient></defs>" +
            "<rect id=\"shape\" x=\"0\" y=\"0\" width=\"100\" height=\"100\" fill=\"url(#g)\"/>");

        // The same point on the same square, written absolutely instead of as a fraction of it.
        SvgImportResult user = Read(
            "<defs><radialGradient id=\"g\" gradientUnits=\"userSpaceOnUse\" cx=\"50\" cy=\"50\" r=\"50\" " +
            "fx=\"35\" fy=\"30\">" + TwoStops + "</radialGradient></defs>" +
            "<rect id=\"shape\" x=\"0\" y=\"0\" width=\"100\" height=\"100\" fill=\"url(#g)\"/>");

        Assert.Equal(0.35, Gradient(percent).FocalPoint!.Value.X, 6);
        Assert.Equal(0.30, Gradient(percent).FocalPoint!.Value.Y, 6);
        AssertSameGradient(Gradient(percent), Gradient(user));
    }

    /// <summary>
    /// **A focus outside the circle is clamped to its edge, along the line from the centre.**
    ///
    /// The check is the diagonal case on purpose: clamping each axis on its own would give (0.75, 0.75),
    /// which is a different direction from the one the file asked for. The answer has to be the point where
    /// the centre-to-focus line meets the circle, so the highlight still points where the file pointed it.
    /// </summary>
    [Fact]
    public void AFocalPointOutsideTheCircleIsClampedToItsEdge()
    {
        SvgImportResult result = Read(
            "<defs><radialGradient id=\"g\" cx=\"0.5\" cy=\"0.5\" r=\"0.25\" fx=\"0.8\" fy=\"0.9\">" + TwoStops +
            "</radialGradient></defs>" +
            "<rect id=\"shape\" width=\"100\" height=\"50\" fill=\"url(#g)\"/>");

        // The focus sits 1.2 radii right and 1.6 radii down, so its length is 2 radii and it lands 0.6 of
        // the way along that direction: 0.5 + 0.25*0.6, 0.5 + 0.25*0.8.
        Point2D focus = Gradient(result).FocalPoint!.Value;
        Assert.Equal(0.65, focus.X, 6);
        Assert.Equal(0.70, focus.Y, 6);
    }

    /// <summary>
    /// An elliptical radial is clamped against its **own** ellipse, not against a circle one of its axes
    /// happens to describe.
    ///
    /// `r` is a single length, so on a 100x50 box it is half the width and a whole height. A focus 25 units
    /// right of the centre is exactly on that edge and is left alone; one 50 units right is two radii out and
    /// lands on the edge at 25 units, which is 0.75 of the box. Read as though `r` were a circle in the
    /// model's normalised units, the second would still look inside and would be kept at 1.0.
    /// </summary>
    [Fact]
    public void AnEllipticalFocalPointIsClampedAgainstTheEllipse()
    {
        SvgImportResult result = Read(
            "<defs>" +
            "<radialGradient id=\"g\" gradientUnits=\"userSpaceOnUse\" cx=\"50\" cy=\"25\" r=\"25\" " +
            "fx=\"100\" fy=\"25\">" + TwoStops + "</radialGradient>" +
            "<radialGradient id=\"h\" gradientUnits=\"userSpaceOnUse\" cx=\"50\" cy=\"25\" r=\"25\" " +
            "fx=\"50\" fy=\"50\">" + TwoStops + "</radialGradient>" +
            "</defs>" +
            "<rect id=\"shape\" width=\"100\" height=\"50\" fill=\"url(#g)\"/>" +
            "<rect id=\"other\" width=\"100\" height=\"50\" fill=\"url(#h)\"/>");

        // Two radii right of the centre, so the edge is one radius right: 25 units, or three quarters across.
        Point2D clamped = Gradient(result).FocalPoint!.Value;
        Assert.Equal(0.75, clamped.X, 6);
        Assert.Equal(0.5, clamped.Y, 6);

        // One radius below the centre, which is on the edge and therefore already where it belongs.
        Point2D onEdge = Gradient(result, "other").FocalPoint!.Value;
        Assert.Equal(0.5, onEdge.X, 6);
        Assert.Equal(1.0, onEdge.Y, 6);
    }

    /// <summary>
    /// **A percentage is a coordinate, not a reason to use the default.**
    ///
    /// The centre and radius are read through the same helper as the focus, and that helper asked the length
    /// reader for a number - which answers in user units and refuses a percentage. Every `cx="25%"` therefore
    /// fell back to the default `0.5`, silently recentring the gradient; the focal point is only the case where
    /// the fallback is visibly a different picture rather than a plausible one.
    /// </summary>
    [Fact]
    public void PercentageCoordinatesAreReadRatherThanFallenBackToTheDefault()
    {
        SvgImportResult result = Read(
            "<defs><radialGradient id=\"g\" cx=\"25%\" cy=\"75%\" r=\"20%\" fx=\"30%\" fy=\"70%\">" + TwoStops +
            "</radialGradient></defs>" +
            "<rect id=\"shape\" width=\"100\" height=\"100\" fill=\"url(#g)\"/>");

        GradientSpec gradient = Gradient(result);

        Assert.Equal(0.25, gradient.Center.X, 6);
        Assert.Equal(0.75, gradient.Center.Y, 6);
        Assert.Equal(0.20, gradient.RadiusX, 6);
        Assert.NotNull(gradient.FocalPoint);
        Assert.Equal(0.30, gradient.FocalPoint!.Value.X, 6);
        Assert.Equal(0.70, gradient.FocalPoint!.Value.Y, 6);
    }

    /// <summary>
    /// **The round trip the suite asks for: text in, `SvgWriter` out, text back in, models compared.**
    /// The focal point is written because it is not the centre, and the second read returns it unchanged.
    /// </summary>
    [Fact]
    public void TheFocalPointSurvivesTheRoundTrip()
    {
        SvgImportResult first = Read(
            "<defs><radialGradient id=\"g\" cx=\"0.4\" cy=\"0.6\" r=\"0.25\" fx=\"0.2\" fy=\"0.45\">" + TwoStops +
            "</radialGradient></defs>" +
            "<rect id=\"shape\" width=\"100\" height=\"50\" fill=\"url(#g)\"/>");

        string svg = SvgWriter.Write(first.Document);
        GradientSpec written = Gradient(SvgReader.Read(svg));

        Assert.Contains("fx=\"0.2\"", svg, StringComparison.Ordinal);
        Assert.Contains("fy=\"0.45\"", svg, StringComparison.Ordinal);
        AssertSameGradient(Gradient(first), written);
    }

    /// <summary>
    /// **A gradient with no focus keeps none, in the model and in the file.**
    ///
    /// A radial that names no `fx`/`fy` means the centre, so writing the defaults explicitly would be
    /// equivalent - but it would also be a coordinate the file never wrote, and every such round trip would
    /// grow attributes nobody asked for. The model stays null and the export stays silent.
    /// </summary>
    [Fact]
    public void AGradientWithNoFocalPointStaysUnfocused()
    {
        SvgImportResult first = Read(
            "<defs><radialGradient id=\"g\" cx=\"0.4\" cy=\"0.6\" r=\"0.25\">" + TwoStops +
            "</radialGradient></defs>" +
            "<rect id=\"shape\" width=\"100\" height=\"50\" fill=\"url(#g)\"/>");

        Assert.Null(Gradient(first).FocalPoint);

        string svg = SvgWriter.Write(first.Document);
        Assert.DoesNotContain("fx=", svg, StringComparison.Ordinal);
        Assert.DoesNotContain("fy=", svg, StringComparison.Ordinal);

        SvgImportResult second = SvgReader.Read(svg);
        Assert.Null(Gradient(second).FocalPoint);
        AssertSameGradient(Gradient(first), Gradient(second));
    }

    /// <summary>A focus that **is** the centre is the same picture as none, so it is not kept as a
    /// coordinate the writer would then have to emit.</summary>
    [Fact]
    public void ACentredFocalPointIsTheSameAsNone()
    {
        SvgImportResult result = Read(
            "<defs><radialGradient id=\"g\" cx=\"0.5\" cy=\"0.5\" r=\"0.5\" fx=\"0.5\" fy=\"0.5\">" + TwoStops +
            "</radialGradient></defs>" +
            "<rect id=\"shape\" width=\"100\" height=\"50\" fill=\"url(#g)\"/>");

        Assert.Null(Gradient(result).FocalPoint);
        Assert.DoesNotContain("fx=", SvgWriter.Write(result.Document), StringComparison.Ordinal);
    }

    /// <summary>Two gradients that differ **only** in their focal point are two gradients: sharing one
    /// definition between them would paint both with whichever was written first.</summary>
    [Fact]
    public void GradientsWithDifferentFocalPointsAreNotShared()
    {
        SvgImportResult result = Read(
            "<defs><radialGradient id=\"g\" cx=\"0.5\" cy=\"0.5\" r=\"0.5\" fx=\"0.2\" fy=\"0.5\">" + TwoStops +
            "</radialGradient>" +
            "<radialGradient id=\"h\" cx=\"0.5\" cy=\"0.5\" r=\"0.5\" fx=\"0.8\" fy=\"0.5\">" + TwoStops +
            "</radialGradient></defs>" +
            "<rect id=\"shape\" width=\"100\" height=\"50\" fill=\"url(#g)\"/>" +
            "<rect id=\"other\" width=\"100\" height=\"50\" fill=\"url(#h)\"/>");

        string svg = SvgWriter.Write(result.Document);

        Assert.Equal(2, svg.Split("<radialGradient", StringSplitOptions.None).Length - 1);
        Assert.Contains("fx=\"0.2\"", svg, StringComparison.Ordinal);
        Assert.Contains("fx=\"0.8\"", svg, StringComparison.Ordinal);
    }
}
