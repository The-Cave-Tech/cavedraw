using VCCad.Core.Model;
using VCCad.Core.Svg;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// **One placement formula, and it agrees with what the reader materialises** (issue #202).
///
/// The reader places an arrowhead at import and the canvas and the exporter have to place the same one from the
/// document's library. Two copies of SVG 1.1 §11.6.4 would be two chances to disagree about which way an arrowhead
/// points, so the arithmetic lives in <see cref="MarkerSpec"/> and the reader's own definition delegates to it.
///
/// The agreement is asserted by **comparing transforms**: the matrix the reader materialised for a file's arrowhead
/// against the one the library definition's attributes produce for the same vertex, heading and stroke width. That
/// is the test that keeps the two from drifting, and it is why the attributes are kept on the definition at all.
/// </summary>
public class MarkerSpecTests
{
    private static (CadDocument Document, ArtGroup Arrowhead, ArtGroup Definition) Imported(string markerAttributes)
    {
        SvgImportResult result = SvgReader.Read(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"200\" height=\"100\">" +
            $"<defs><marker id=\"arrow\" {markerAttributes}>" +
            "<path d=\"M 0 0 L 10 5 L 0 10 Z\"/></marker></defs>" +
            "<path d=\"M 10 50 L 90 50\" fill=\"none\" stroke=\"black\" stroke-width=\"2\" " +
            "marker-end=\"url(#arrow)\"/></svg>");

        ArtGroup arrowhead = result.Document.AllGroups()
            .Single(group => group.ForeignAttributes.ContainsKey(SvgWriter.MarkerArtTag));
        ArtGroup definition = result.Document.FindDefinition("arrow")!;
        return (result.Document, arrowhead, definition);
    }

    private static void AssertSameTransform(AffineTransform expected, AffineTransform actual)
    {
        Assert.Equal(expected.A, actual.A, 6);
        Assert.Equal(expected.B, actual.B, 6);
        Assert.Equal(expected.C, actual.C, 6);
        Assert.Equal(expected.D, actual.D, 6);
        Assert.Equal(expected.E, actual.E, 6);
        Assert.Equal(expected.F, actual.F, 6);
    }

    [Fact]
    public void TheLibraryDefinitionPlacesTheMarkerWhereTheReaderDid()
    {
        (_, ArtGroup arrowhead, ArtGroup definition) = Imported(
            "markerWidth=\"10\" markerHeight=\"10\" refX=\"5\" refY=\"5\" orient=\"auto\"");

        // The file's own numbers: a horizontal line ending at (90, 50), heading 0, stroke width 2.
        MarkerSpec spec = MarkerSpec.From(definition.ForeignAttributes);
        AssertSameTransform(
            arrowhead.Transform, spec.Placement(new Point2D(90, 50), headingRadians: 0.0, strokeWidth: 2.0));
    }

    [Fact]
    public void AViewBoxMarkerAgreesToo()
    {
        (_, ArtGroup arrowhead, ArtGroup definition) = Imported(
            "markerWidth=\"10\" markerHeight=\"10\" refX=\"5\" refY=\"5\" orient=\"auto\" " +
            "viewBox=\"0 0 20 20\" preserveAspectRatio=\"xMidYMid meet\"");

        MarkerSpec spec = MarkerSpec.From(definition.ForeignAttributes);
        AssertSameTransform(
            arrowhead.Transform, spec.Placement(new Point2D(90, 50), headingRadians: 0.0, strokeWidth: 2.0));
    }

    [Fact]
    public void AUserSpaceMarkerIsNotScaledByTheStroke()
    {
        (_, ArtGroup arrowhead, ArtGroup definition) = Imported(
            "markerWidth=\"10\" markerHeight=\"10\" refX=\"5\" refY=\"5\" orient=\"auto\" " +
            "markerUnits=\"userSpaceOnUse\"");

        MarkerSpec spec = MarkerSpec.From(definition.ForeignAttributes);

        // One marker unit is one user unit here, whatever the stroke's width: the same transform at width 2 and 8.
        AssertSameTransform(
            arrowhead.Transform, spec.Placement(new Point2D(90, 50), headingRadians: 0.0, strokeWidth: 8.0));
        AssertSameTransform(spec.Placement(new Point2D(90, 50), 0.0, 2.0), spec.Placement(new Point2D(90, 50), 0.0, 8.0));
    }

    [Fact]
    public void AStatedAngleTurnsTheMarkerByIt()
    {
        MarkerSpec right = MarkerSpec.From(new Dictionary<string, string> { ["orient"] = "90" });
        MarkerSpec marked = MarkerSpec.From(new Dictionary<string, string> { ["orient"] = "90deg" });

        // The bare number and the unit are the same angle, which the specification allows and files use.
        Assert.Equal(MarkerOrient.Angle, right.Orient);
        Assert.Equal(90.0, right.AngleDegrees, 6);
        Assert.Equal(MarkerOrient.Angle, marked.Orient);
        Assert.Equal(90.0, marked.AngleDegrees, 6);
    }

    [Fact]
    public void SomethingItCannotReadIsReportedRatherThanDefaulted()
    {
        var warnings = new List<string>();
        MarkerSpec spec = MarkerSpec.From(
            new Dictionary<string, string> { ["orient"] = "sideways", ["markerUnits"] = "furlongs" },
            warnings.Add);

        // The defaults stand - there is nothing better to do - but the caller is told, because an arrowhead placed
        // by a value nobody understood looks exactly as deliberate as one placed correctly.
        Assert.Equal(MarkerOrient.Auto, spec.Orient);
        Assert.Equal(2, warnings.Count);
        Assert.Contains(warnings, warning => warning.Contains("sideways", StringComparison.Ordinal));
        Assert.Contains(warnings, warning => warning.Contains("furlongs", StringComparison.Ordinal));
    }
}
