using VCCad.Core.Model;
using VCCad.Core.Svg;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// SVG lengths: the unit suffix, and percentages.
///
/// **Every assertion is on the model.** A reader that read `1in` as one and a reader that read it as ninety-six
/// both "parse" a file, so what is checked here is the coordinate the file's own geometry landed on - which is the
/// only thing that says the unit was understood rather than skipped.
///
/// The lengths resolve into the document's own coordinate system, SVG's user unit, which is a CSS pixel and is the
/// same unit the page's own width and height are measured in. That is what makes one number the right answer for a
/// length wherever it appears in the file.
///
/// That space is **not** the model's: the model stores points, so what the reader hands to it is the file's space
/// carried across by one factor of 0.75 - applied once, at the root, and asserted in the second half of this file.
/// The assertions that read a path's own nodes therefore see the file's numbers, and the ones that ask where the
/// geometry lands see points; <see cref="InArtboard"/> is how a test says which it means.
/// </summary>
public class SvgUnitTests
{
    private static readonly string Header =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"96\" height=\"96\" viewBox=\"0 0 96 96\">";

    private static SvgImportResult Read(string body) => SvgReader.Read(Header + body + "</svg>");

    private static SvgImportResult ReadRoot(string svg) => SvgReader.Read(svg);

    private static PathItem FirstPath(SvgImportResult result) => result.Document.AllPaths().First();

    private static double X(SvgImportResult result) => FirstPath(result).SubPaths[0].Nodes[0].Anchor.X;

    // ---------------------------------------------------------------- the unit table

    /// <summary>
    /// **1in = 96px = 72pt = 25.4mm = 2.54cm.** They are the same length, and the reader has to say so: while the
    /// suffix was dropped, `1in` imported as one unit and `72pt` as seventy-two, so a file written in millimetres
    /// or points was the wrong size and two lengths SVG calls equal came out different.
    /// </summary>
    [Theory]
    [InlineData("1in", 96.0)]
    [InlineData("96px", 96.0)]
    [InlineData("72pt", 96.0)]
    [InlineData("25.4mm", 96.0)]
    [InlineData("2.54cm", 96.0)]
    [InlineData("6pc", 96.0)]
    [InlineData("96", 96.0)]
    public void ALengthResolvesThroughItsUnit(string written, double expected)
    {
        SvgImportResult result = Read($"<rect x=\"{written}\" y=\"0\" width=\"10\" height=\"10\"/>");

        Assert.Empty(result.Warnings);
        Assert.Equal(expected, X(result), 9);
    }

    /// <summary>The five spellings of one inch, side by side in one document, must land on the same coordinate.</summary>
    [Fact]
    public void EverySpellingOfAnInchIsTheSameLength()
    {
        SvgImportResult result = Read(
            "<rect x=\"1in\" y=\"0\" width=\"1\" height=\"1\"/>" +
            "<rect x=\"96px\" y=\"0\" width=\"1\" height=\"1\"/>" +
            "<rect x=\"72pt\" y=\"0\" width=\"1\" height=\"1\"/>" +
            "<rect x=\"25.4mm\" y=\"0\" width=\"1\" height=\"1\"/>" +
            "<rect x=\"2.54cm\" y=\"0\" width=\"1\" height=\"1\"/>");

        List<double> positions = result.Document.AllPaths()
            .Select(p => p.SubPaths[0].Nodes[0].Anchor.X)
            .ToList();

        Assert.Equal(5, positions.Count);
        Assert.All(positions, x => Assert.Equal(96.0, x, 9));
        Assert.Equal(0.0, positions.Max() - positions.Min(), 9);
    }

    /// <summary>
    /// **A non-pixel width and height establish the user-unit scale.** The view box is in user units and the view
    /// port is a physical size, so a page declared an inch wide holding a 48-unit view box draws that view box at
    /// twice its size - which is the relationship a reader that ignored the unit cannot express at all. An inch of
    /// page is 72 points, and the 48 units are drawn across it.
    /// </summary>
    [Fact]
    public void ANonPixelWidthEstablishesTheUserUnitScale()
    {
        SvgImportResult result = ReadRoot(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"1in\" height=\"1in\" viewBox=\"0 0 48 48\">" +
            "<rect x=\"0\" y=\"0\" width=\"48\" height=\"48\"/></svg>");

        Assert.Equal(72.0, result.Document.Artboards[0].Width, 9);

        ArtGroup group = result.Document.Artboards[0].Layers[0].Children.OfType<ArtGroup>().Single();
        Point2D corner = group.Transform.Transform(new Point2D(48, 48));
        Assert.Equal(72.0, corner.X, 9);
        Assert.Equal(72.0, corner.Y, 9);
    }

    /// <summary>A unit in the stroke properties is read the same way, and reaches the model as the same length.</summary>
    [Fact]
    public void AStrokeWidthInPointsIsItsPixelLength()
    {
        SvgImportResult result = Read(
            "<rect x=\"0\" y=\"0\" width=\"10\" height=\"10\" stroke=\"#000000\" stroke-width=\"72pt\"/>");

        Assert.Equal(96.0, FirstPath(result).Stroke.Width, 9);
    }

    // ---------------------------------------------------------------- percentages

    /// <summary>A percentage is a fraction of the viewport, on the axis the attribute belongs to.</summary>
    [Fact]
    public void APercentageResolvesAgainstTheViewport()
    {
        SvgImportResult result = Read(
            "<rect x=\"25%\" y=\"10%\" width=\"50%\" height=\"50%\"/>");

        SubPath sub = Assert.Single(FirstPath(result).SubPaths);
        Assert.Equal(24.0, sub.Nodes[0].Anchor.X, 9);
        Assert.Equal(9.6, sub.Nodes[0].Anchor.Y, 9);
        Assert.Equal(72.0, sub.Nodes[2].Anchor.X, 9);
        Assert.Equal(57.6, sub.Nodes[2].Anchor.Y, 9);
    }

    /// <summary>
    /// A radius is a fraction of the viewport's **normalized diagonal**, which is what keeps a circle circular in a
    /// viewport that is not square. Resolving it against the width instead would draw an oval - so this uses a
    /// viewport whose diagonal is neither of its sides.
    /// </summary>
    [Fact]
    public void ARadiusPercentageResolvesAgainstTheDiagonal()
    {
        SvgImportResult result = ReadRoot(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"200\" height=\"100\" viewBox=\"0 0 200 100\">" +
            "<circle cx=\"50%\" cy=\"50%\" r=\"50%\"/></svg>");

        double radius = Math.Sqrt(((200.0 * 200.0) + (100.0 * 100.0)) / 2.0) / 2.0;
        SubPath sub = Assert.Single(FirstPath(result).SubPaths);

        // 79.06 from the diagonal; from the width it would be 100, and the top of the circle would be at -50.
        Assert.Equal(50.0 - radius, sub.Nodes[0].Anchor.Y, 9);
        Assert.NotEqual(-50.0, sub.Nodes[0].Anchor.Y, 6);
    }

    /// <summary>
    /// **A percentage with no viewport to resolve against is reported.** The outer `svg` element's own width and
    /// height ARE the viewport - there is no containing block for a percentage to be a percentage of - so the
    /// reader's fallback is a value the file did not write, and it says so rather than quietly using it.
    /// </summary>
    [Fact]
    public void APercentageWithNoViewportIsReported()
    {
        SvgImportResult result = ReadRoot(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"50%\" height=\"100%\" viewBox=\"0 0 96 48\">" +
            "<rect x=\"0\" y=\"0\" width=\"10\" height=\"10\"/></svg>");

        // The fallback is the view box, which is the best available answer - but it is not the file's, and the
        // warning is what makes the difference visible. The view box is in user units, so the page it becomes is
        // three quarters of it in points.
        Assert.Equal(72.0, result.Document.Artboards[0].Width, 9);
        Assert.Equal(36.0, result.Document.Artboards[0].Height, 9);
        Assert.Contains(result.Warnings, w =>
            w.Contains("width=\"50%\"", StringComparison.Ordinal) &&
            w.Contains("containing block", StringComparison.Ordinal));
    }

    /// <summary>A percentage is resolved, so nothing about it is reported.</summary>
    [Fact]
    public void AResolvedPercentageIsNotReported()
    {
        SvgImportResult result = Read("<rect x=\"25%\" y=\"25%\" width=\"50%\" height=\"50%\"/>");

        Assert.Single(result.Document.AllPaths());
        Assert.DoesNotContain(result.Warnings, w => w.Contains('%'));
    }

    /// <summary>An element inside a symbol the file never sizes has no viewport, so its percentages are reported.</summary>
    [Fact]
    public void APercentageInsideAnUnsizedSymbolIsReported()
    {
        SvgImportResult result = Read(
            "<symbol id=\"s\"><rect x=\"10%\" y=\"0\" width=\"10\" height=\"10\"/></symbol>" +
            "<use href=\"#s\"/>");

        Assert.Contains(result.Warnings, w => w.Contains("percentage", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A relative unit has no font size to be relative to, so the assumption made is reported.</summary>
    [Fact]
    public void ARelativeUnitIsResolvedAndReported()
    {
        SvgImportResult result = Read("<rect x=\"1em\" y=\"0\" width=\"16\" height=\"16\"/>");

        Assert.Equal(16.0, X(result), 9);
        Assert.Contains(result.Warnings, w => w.Contains("1em", StringComparison.Ordinal));
    }

    /// <summary>A unit suffix that is not a unit is reported rather than read as a number.</summary>
    [Fact]
    public void AnUnknownUnitIsReported()
    {
        SvgImportResult result = Read("<rect x=\"10vw\" y=\"0\" width=\"16\" height=\"16\"/>");

        Assert.Contains(result.Warnings, w => w.Contains("vw", StringComparison.Ordinal));
    }

    /// <summary>Degenerate and signed lengths still read, because the unit table must not lose that grammar.</summary>
    [Fact]
    public void SignedAndExponentLengthsStillRead()
    {
        SvgImportResult result = Read(
            "<rect x=\"-10.5\" y=\"0\" width=\"1\" height=\"1\"/>" +
            "<rect x=\"1e2\" y=\"0\" width=\"1\" height=\"1\"/>");

        List<PathItem> paths = result.Document.AllPaths().ToList();
        Assert.Equal(-10.5, paths[0].SubPaths[0].Nodes[0].Anchor.X, 9);
        Assert.Equal(100.0, paths[1].SubPaths[0].Nodes[0].Anchor.X, 9);
    }

    // ---------------------------------------------------------------- the file's unit in the model's space

    /// <summary>
    /// A point's coordinate **as the artboard sees it**, with every enclosing group's transform composed in.
    ///
    /// A path's own nodes are in the space the file wrote them in - the view box's - and it is the groups above it
    /// that carry that space into the model's. A test that read the node alone would be measuring the file rather
    /// than the document, which is exactly the difference this section is about.
    /// </summary>
    private static Point2D InArtboard(PathItem path, int node = 0)
    {
        AffineTransform transform = AffineTransform.Identity;
        for (IItemContainer? container = path.Container;
             container is not null;
             container = (container as LayerItem)?.Container)
        {
            if (container is ArtGroup group)
            {
                transform = group.Transform.Compose(transform);
            }
        }

        return transform.Transform(path.SubPaths[0].Nodes[node].Anchor);
    }

    /// <summary>
    /// **A bare viewport is measured in CSS pixels and the model stores points.** A file that says 800x600 with no
    /// unit is 600x450 pt of paper — three quarters of the number it writes — and the geometry inside it is carried
    /// into that same space rather than left a third larger than its own page.
    /// </summary>
    [Fact]
    public void ABareViewportImportsAtItsPointSize()
    {
        SvgImportResult result = ReadRoot(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"800\" height=\"600\">" +
            "<rect x=\"100\" y=\"100\" width=\"100\" height=\"100\"/></svg>");

        Assert.Equal(600.0, result.Document.Artboards[0].Width, 9);
        Assert.Equal(450.0, result.Document.Artboards[0].Height, 9);

        PathItem rect = FirstPath(result);
        Point2D corner = InArtboard(rect);

        // 100 user units in from each edge is 75 pt, and the rect is 75 pt across.
        Assert.Equal(75.0, corner.X, 9);
        Assert.Equal(75.0, corner.Y, 9);
        Assert.Equal(75.0, InArtboard(rect, 2).X - corner.X, 9);
        Assert.Equal(75.0, InArtboard(rect, 2).Y - corner.Y, 9);
    }

    /// <summary>
    /// **A physical unit states the size it describes.** This is the number that does not move: 210mm is 595.28 pt
    /// whether the unit table is applied in pixel space or in the model's, which is what makes it the check that the
    /// conversion was added rather than moved.
    /// </summary>
    [Fact]
    public void AnExplicitPhysicalUnitStatesItsPointSize()
    {
        SvgImportResult result = ReadRoot(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"210mm\" height=\"297mm\" viewBox=\"0 0 210 297\">" +
            "<rect x=\"0\" y=\"0\" width=\"210\" height=\"297\"/></svg>");

        Assert.Equal(PageSizes.A4Portrait.Width, result.Document.Artboards[0].Width, 9);
        Assert.Equal(PageSizes.A4Portrait.Height, result.Document.Artboards[0].Height, 9);
    }

    /// <summary>
    /// Every spelling of one inch is still the same length, and that length is **72 pt** — an inch of the model's
    /// paper. `1in` used to import as 96 of the model's units, which is an inch of CSS pixels and a third more paper
    /// than the file asks for.
    /// </summary>
    [Theory]
    [InlineData("1in")]
    [InlineData("96px")]
    [InlineData("72pt")]
    [InlineData("25.4mm")]
    [InlineData("2.54cm")]
    [InlineData("6pc")]
    [InlineData("96")]
    public void AnInchIsSeventyTwoPointsInTheModel(string written)
    {
        SvgImportResult result = ReadRoot(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"96\" height=\"96\" viewBox=\"0 0 96 96\">" +
            $"<rect x=\"{written}\" y=\"0\" width=\"10\" height=\"10\"/></svg>");

        Assert.Empty(result.Warnings);
        Assert.Equal(72.0, InArtboard(FirstPath(result)).X, 9);
    }

    /// <summary>
    /// **A view box whose shape differs from the port's still scales the way SVG says**: uniformly to fit (`meet`),
    /// with the leftover space centred. The fit is measured against the port **in points**, so getting this wrong
    /// does not just move the drawing - it draws it at the wrong size on a page that is the right one.
    /// </summary>
    [Fact]
    public void AViewBoxWithADifferentAspectScalesAsSvgSays()
    {
        SvgImportResult result = ReadRoot(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"400\" height=\"200\" viewBox=\"0 0 100 100\">" +
            "<rect x=\"0\" y=\"0\" width=\"100\" height=\"100\"/></svg>");

        Assert.Equal(300.0, result.Document.Artboards[0].Width, 9);
        Assert.Equal(150.0, result.Document.Artboards[0].Height, 9);

        // 100 user units of view box fit 150 pt of port, so the square is 150 pt across and centred in 300 pt.
        PathItem rect = FirstPath(result);
        Assert.Equal(75.0, InArtboard(rect).X, 9);
        Assert.Equal(0.0, InArtboard(rect).Y, 9);
        Assert.Equal(225.0, InArtboard(rect, 2).X, 9);
        Assert.Equal(150.0, InArtboard(rect, 2).Y, 9);
    }

    /// <summary>
    /// **The conversion happens once, at the root.** A nested `svg` is followed in the file's own space, so nothing
    /// inside it is converted a second time - which would draw everything within it three quarters of the size the
    /// file asked for, and would look like a content bug rather than a unit one.
    /// </summary>
    [Fact]
    public void ANestedSvgDoesNotApplyTheConversionTwice()
    {
        SvgImportResult result = ReadRoot(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"800\" height=\"600\">" +
            "<svg x=\"0\" y=\"0\" width=\"200\" height=\"100\">" +
            "<rect x=\"0\" y=\"0\" width=\"100\" height=\"100\"/></svg></svg>");

        Assert.Equal(600.0, result.Document.Artboards[0].Width, 9);
        Assert.Equal(75.0, InArtboard(FirstPath(result), 2).X, 9);
        Assert.Equal(75.0, InArtboard(FirstPath(result), 2).Y, 9);
    }

    /// <summary>
    /// A percentage is a fraction of the viewport **in the file's own space**, and only then carried into the
    /// model's - so it is the same fraction of the page the file describes rather than a fraction of a page four
    /// thirds of the size.
    /// </summary>
    [Fact]
    public void APercentageIsAFractionOfTheViewportInTheFilesSpace()
    {
        SvgImportResult result = ReadRoot(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"800\" height=\"600\">" +
            "<rect x=\"50%\" y=\"50%\" width=\"50%\" height=\"50%\"/></svg>");

        // 50% of 800 user units is 400, which is 300 of the 600 points the page is; 50% of 600 is 225 of 450.
        PathItem rect = FirstPath(result);
        Assert.Equal(300.0, InArtboard(rect).X, 9);
        Assert.Equal(225.0, InArtboard(rect).Y, 9);
        Assert.Equal(600.0, InArtboard(rect, 2).X, 9);
        Assert.Equal(450.0, InArtboard(rect, 2).Y, 9);
    }
}
