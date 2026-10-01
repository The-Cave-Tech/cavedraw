using VCCad.Core.Model;
using VCCad.Core.Svg;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// SVG filters as a **directed graph**.
///
/// `feComposite` appears 155 times across Inkscape's own test files, more than any other element, so the wiring is
/// not a corner of the format - it is how filters are written. These assert the graph rather than the parameters
/// where the two differ, because a reader that flattened the primitives into a chain would get every one of those
/// files wrong while looking plausible.
/// </summary>
public class SvgFilterTests
{
    private const string Head =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"200\" height=\"200\" viewBox=\"0 0 200 200\">";

    private static SvgImportResult Read(string body) => SvgReader.Read(Head + body + "</svg>");

    private static FilterSpec Filter(SvgImportResult result, string name)
        => result.Document.AllPaths().FirstOrDefault(p => p.FilterId == name) is not null
            ? result.Document.FindFilter(name)!
            : result.Document.FindFilter(name)!;

    // ---------------------------------------------------------------- the graph

    [Fact]
    public void ThePrimitivesAreReadInOrderWithTheirParameters()
    {
        SvgImportResult result = Read(
            "<defs><filter id=\"f\">" +
            "<feGaussianBlur in=\"SourceAlpha\" stdDeviation=\"3\" result=\"blur\"/>" +
            "<feOffset in=\"blur\" dx=\"4\" dy=\"5\" result=\"shadow\"/>" +
            "<feFlood flood-color=\"#ff0000\" flood-opacity=\"0.5\" result=\"ink\"/>" +
            "<feComposite in=\"ink\" in2=\"shadow\" operator=\"in\" result=\"shaded\"/>" +
            "<feBlend in=\"SourceGraphic\" in2=\"shaded\" mode=\"multiply\"/>" +
            "</filter></defs>" +
            "<rect width=\"10\" height=\"10\" filter=\"url(#f)\"/>");

        FilterSpec filter = result.Document.FindFilter("f")!;

        Assert.Equal(5, filter.Primitives.Count);
        Assert.Equal(FilterPrimitiveKind.GaussianBlur, filter.Primitives[0].Kind);
        Assert.Equal(3.0, filter.Primitives[0].Radius, 6);
        Assert.Equal("SourceAlpha", filter.Primitives[0].Input);
        Assert.Equal("blur", filter.Primitives[0].Result);

        Assert.Equal(4.0, filter.Primitives[1].Dx, 6);
        Assert.Equal(5.0, filter.Primitives[1].Dy, 6);
        Assert.Equal("blur", filter.Primitives[1].Input);

        Assert.Equal(1.0, filter.Primitives[2].FloodColor!.Value.R, 6);
        Assert.Equal(0.5, filter.Primitives[2].FloodOpacity, 6);

        Assert.Equal("in", filter.Primitives[3].Operator);
        Assert.Equal("ink", filter.Primitives[3].Input);
        Assert.Equal("shadow", filter.Primitives[3].Input2);

        Assert.Equal("multiply", filter.Primitives[4].Mode);
        Assert.Equal("shaded", filter.Primitives[4].Input2);
    }

    /// <summary>
    /// **One result consumed twice**, which is the test that tells a graph from a pipeline. A design that chained
    /// the primitives would give the second consumer the first's output rather than the named buffer, and the
    /// picture would be wrong in a way no parameter test could see.
    /// </summary>
    [Fact]
    public void OneResultCanFeedTwoConsumers()
    {
        SvgImportResult result = Read(
            "<defs><filter id=\"f\">" +
            "<feGaussianBlur in=\"SourceGraphic\" stdDeviation=\"2\" result=\"soft\"/>" +
            "<feComposite in=\"soft\" in2=\"SourceAlpha\" operator=\"in\" result=\"clipped\"/>" +
            "<feBlend in=\"soft\" in2=\"clipped\" mode=\"screen\"/>" +
            "</filter></defs>" +
            "<rect width=\"10\" height=\"10\" filter=\"url(#f)\"/>");

        FilterSpec filter = result.Document.FindFilter("f")!;

        // Two primitives read `soft`, and they are not adjacent - which is only expressible by name.
        Assert.Equal(2, filter.Primitives.Count(p => p.Input == "soft" || p.Input2 == "soft"));

        // And the graph can be walked: `soft` has one producer, and it is the first primitive.
        FilterPrimitive producer = filter.ProducerOf("soft")!;
        Assert.Equal(FilterPrimitiveKind.GaussianBlur, producer.Kind);

        // The roots are the inputs nothing produces.
        List<string> roots = filter.Inputs.Where(i => filter.ProducerOf(i) is null).ToList();
        Assert.Contains("SourceGraphic", roots);
        Assert.Contains("SourceAlpha", roots);
        Assert.DoesNotContain("soft", roots);
    }

    /// <summary>
    /// **The region is part of the filter.** A blur near an edge either grows into the margin or is clipped off,
    /// and that is decided by these four numbers rather than by the blur.
    /// </summary>
    [Fact]
    public void TheFilterRegionIsRead()
    {
        SvgImportResult result = Read(
            "<defs><filter id=\"f\" x=\"-0.25\" y=\"-0.25\" width=\"1.5\" height=\"1.5\">" +
            "<feGaussianBlur stdDeviation=\"4\"/></filter></defs>" +
            "<rect width=\"10\" height=\"10\" filter=\"url(#f)\"/>");

        FilterSpec filter = result.Document.FindFilter("f")!;

        Assert.Equal(-0.25, filter.X, 6);
        Assert.Equal(-0.25, filter.Y, 6);
        Assert.Equal(1.5, filter.Width, 6);
        Assert.Equal(1.5, filter.Height, 6);
        Assert.True(filter.ObjectBoundingBox);
    }

    /// <summary>And the defaults are SVG's own, including the ten per cent of margin that clips a large blur.</summary>
    [Fact]
    public void TheDefaultRegionIsTheDocumentedOne()
    {
        SvgImportResult result = Read(
            "<defs><filter id=\"f\"><feGaussianBlur stdDeviation=\"1\"/></filter></defs>" +
            "<rect width=\"10\" height=\"10\" filter=\"url(#f)\"/>" +
            "<rect width=\"10\" height=\"10\" filter=\"url(#f)\" x=\"20\"/>");

        FilterSpec filter = result.Document.FindFilter("f")!;

        Assert.Equal(-0.1, filter.X, 6);
        Assert.Equal(1.2, filter.Width, 6);
    }

    [Fact]
    public void AFilterInUserSpaceSaysSo()
    {
        SvgImportResult result = Read(
            "<defs><filter id=\"f\" filterUnits=\"userSpaceOnUse\" x=\"0\" y=\"0\" width=\"200\" height=\"200\">" +
            "<feGaussianBlur stdDeviation=\"1\"/></filter></defs>" +
            "<rect width=\"10\" height=\"10\" filter=\"url(#f)\"/>");

        FilterSpec filter = result.Document.FindFilter("f")!;

        Assert.False(filter.ObjectBoundingBox);
        Assert.Equal(200.0, filter.Width, 6);
    }

    // ---------------------------------------------------------------- the reference

    [Fact]
    public void AShapeRefersToItsFilterByName()
    {
        SvgImportResult result = Read(
            "<defs><filter id=\"soft\"><feGaussianBlur stdDeviation=\"2\"/></filter></defs>" +
            "<rect width=\"10\" height=\"10\" filter=\"url(#soft)\"/>" +
            "<rect width=\"10\" height=\"10\" x=\"20\"/>");

        List<PathItem> paths = result.Document.AllPaths().ToList();

        Assert.Equal("soft", paths[0].FilterId);
        Assert.Null(paths[1].FilterId);
    }

    /// <summary>**A reference to a filter the document does not have is reported**, not silently ignored.</summary>
    [Fact]
    public void AMissingFilterIsReported()
    {
        SvgImportResult result = Read("<rect width=\"10\" height=\"10\" filter=\"url(#nothing)\"/>");

        var missing = result.Document.MissingFilters().ToList();

        Assert.Equal("nothing", Assert.Single(missing).Name);
    }

    [Fact]
    public void AFilterWithNoPrimitivesIsNotStored()
    {
        SvgImportResult result = Read(
            "<defs><filter id=\"f\"/></defs>" +
            "<rect width=\"10\" height=\"10\" filter=\"url(#f)\"/>");

        Assert.Null(result.Document.FindFilter("f"));
    }

    // ---------------------------------------------------------------- the round trip

    /// <summary>
    /// **The graph survives export and re-import**, wiring and region together - which is the test that says the
    /// exporter did not flatten it into something a viewer would draw differently.
    /// </summary>
    [Fact]
    public void AFilterSurvivesTheRoundTrip()
    {
        SvgImportResult first = Read(
            "<defs><filter id=\"f\" x=\"-0.2\" y=\"-0.2\" width=\"1.4\" height=\"1.4\">" +
            "<feGaussianBlur in=\"SourceAlpha\" stdDeviation=\"3\" result=\"soft\"/>" +
            "<feOffset in=\"soft\" dx=\"2\" dy=\"3\" result=\"moved\"/>" +
            "<feFlood flood-color=\"#336699\" flood-opacity=\"0.75\" result=\"ink\"/>" +
            "<feComposite in=\"ink\" in2=\"moved\" operator=\"in\" result=\"shadow\"/>" +
            "<feBlend in=\"SourceGraphic\" in2=\"shadow\" mode=\"multiply\"/>" +
            "</filter></defs>" +
            "<rect width=\"10\" height=\"10\" filter=\"url(#f)\"/>");

        SvgImportResult second = SvgReader.Read(SvgWriter.Write(first.Document));

        FilterSpec back = second.Document.FindFilter("f")!;

        Assert.Equal(first.Document.FindFilter("f")!, back);
        Assert.Equal(5, back.Primitives.Count);
        Assert.Equal(-0.2, back.X, 6);
    }
}
