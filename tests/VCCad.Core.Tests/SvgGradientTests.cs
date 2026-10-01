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
}
