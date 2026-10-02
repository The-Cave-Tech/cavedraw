using VCCad.Core.Model;
using VCCad.Core.Svg;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// The SVG reader: shapes, path data, transforms, the view box and inherited paint.
///
/// Every assertion is on geometry or on a model value, for the reason the repo gives everywhere: "it parsed" proves
/// nothing, and a reader that dropped every transform would parse perfectly and draw the whole file in the corner.
/// </summary>
public class SvgReaderTests
{
    private static readonly string Header =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"200\" height=\"100\" viewBox=\"0 0 200 100\">";

    private static SvgImportResult Read(string body) => SvgReader.Read(Header + body + "</svg>");

    private static PathItem FirstPath(SvgImportResult result)
        => result.Document.AllPaths().First();

    private static Point2D FirstAnchor(SvgImportResult result) => FirstPath(result).SubPaths[0].Nodes[0].Anchor;

    // ---------------------------------------------------------------- shapes

    [Fact]
    public void ARectangleBecomesFourCorners()
    {
        SvgImportResult result = Read("<rect x=\"10\" y=\"20\" width=\"30\" height=\"40\"/>");

        PathItem path = FirstPath(result);
        SubPath sub = Assert.Single(path.SubPaths);
        Assert.True(sub.IsClosed);
        Assert.Equal(4, sub.Nodes.Count);
        Assert.Equal(new Point2D(10, 20), sub.Nodes[0].Anchor);
        Assert.Equal(new Point2D(40, 60), sub.Nodes[2].Anchor);
        Assert.Equal(1, result.ByElement["rect"]);
    }

    [Fact]
    public void ACircleIsFourBezierArcs()
    {
        SvgImportResult result = Read("<circle cx=\"50\" cy=\"50\" r=\"20\"/>");

        SubPath sub = Assert.Single(FirstPath(result).SubPaths);
        Assert.True(sub.IsClosed);
        Assert.Equal(4, sub.Nodes.Count);

        // The top of the circle, with handles pushed out by the Bezier constant rather than left on the anchor.
        Assert.Equal(new Point2D(50, 30), sub.Nodes[0].Anchor);
        Assert.True(Math.Abs(sub.Nodes[0].OutHandle.X - (50 + (20 * 0.5522847498307936))) < 1e-9);
    }

    [Fact]
    public void AnEllipseKeepsItsTwoRadii()
    {
        SvgImportResult result = Read("<ellipse cx=\"0\" cy=\"0\" rx=\"30\" ry=\"10\"/>");

        SubPath sub = Assert.Single(FirstPath(result).SubPaths);
        Assert.Equal(new Point2D(0, -10), sub.Nodes[0].Anchor);
        Assert.Equal(new Point2D(30, 0), sub.Nodes[1].Anchor);
    }

    [Fact]
    public void ALineIsTwoPointsAndAPolygonIsClosed()
    {
        SvgImportResult result = Read(
            "<line x1=\"1\" y1=\"2\" x2=\"3\" y2=\"4\"/>" +
            "<polygon points=\"0,0 10,0 10,10\"/>");

        Assert.False(result.Document.AllPaths().First().SubPaths[0].IsClosed);
        Assert.True(result.Document.AllPaths().Last().SubPaths[0].IsClosed);
        Assert.Equal(3, result.Document.AllPaths().Last().SubPaths[0].Nodes.Count);
    }

    // ---------------------------------------------------------------- path data

    [Fact]
    public void PathCommandsBecomeNodes()
    {
        SvgImportResult result = Read("<path d=\"M10 10 L50 10 L50 40 Z\"/>");

        SubPath sub = Assert.Single(FirstPath(result).SubPaths);
        Assert.True(sub.IsClosed);
        Assert.Equal(3, sub.Nodes.Count);
        Assert.Equal(new Point2D(50, 40), sub.Nodes[2].Anchor);
    }

    /// <summary>
    /// **Numbers are not separated by commas.** `10-5` is two numbers, and a parser that split on separators would
    /// read one malformed token and lose the rest of the path.
    /// </summary>
    [Fact]
    public void NumbersRunTogetherAreStillTwoNumbers()
    {
        SvgImportResult result = Read("<path d=\"M0 0L10-5L1.5.5\"/>");

        SubPath sub = Assert.Single(FirstPath(result).SubPaths);
        Assert.Equal(3, sub.Nodes.Count);
        Assert.Equal(new Point2D(10, -5), sub.Nodes[1].Anchor);
        Assert.Equal(new Point2D(1.5, 0.5), sub.Nodes[2].Anchor);
    }

    [Fact]
    public void AnImplicitLineAfterAMoveIsALine()
    {
        SvgImportResult result = Read("<path d=\"M0 0 10 10 20 20\"/>");

        SubPath sub = Assert.Single(FirstPath(result).SubPaths);
        Assert.Equal(3, sub.Nodes.Count);
        Assert.Equal(new Point2D(20, 20), sub.Nodes[2].Anchor);
    }

    [Fact]
    public void RelativeCommandsAccumulate()
    {
        SvgImportResult result = Read("<path d=\"m10 10 l5 0 l0 5\"/>");

        SubPath sub = Assert.Single(FirstPath(result).SubPaths);
        Assert.Equal(new Point2D(15, 10), sub.Nodes[1].Anchor);
        Assert.Equal(new Point2D(15, 15), sub.Nodes[2].Anchor);
    }

    /// <summary>A quadratic becomes the cubic with the same shape, not an approximation of it.</summary>
    [Fact]
    public void AQuadraticBecomesACubic()
    {
        SvgImportResult result = Read("<path d=\"M0 0 Q10 20 20 0\"/>");

        SubPath sub = Assert.Single(FirstPath(result).SubPaths);
        PathNode first = sub.Nodes[0];

        // The control points sit two thirds of the way from each end towards the quadratic's control point.
        Assert.Equal(20.0 / 3.0, first.OutHandle.X, 9);
        Assert.Equal(40.0 / 3.0, first.OutHandle.Y, 9);
        Assert.Equal(20.0, sub.Nodes[1].Anchor.X, 9);
    }

    /// <summary>An arc becomes cubics, and its endpoints are exact even though the middle is approximate.</summary>
    [Fact]
    public void AnArcBecomesCubics()
    {
        SvgImportResult result = Read("<path d=\"M0 0 A10 10 0 0 1 20 0\"/>");

        SubPath sub = Assert.Single(FirstPath(result).SubPaths);
        Assert.True(sub.Nodes.Count > 2, "an arc is a run of cubics, not one straight line");

        // A half circle from (0,0) to (20,0) with radius 10 bows through the midpoint, which is at (10,-10) and
        // not (10,10): the sweep flag is a **positive-angle** direction, and in SVG's y-down space that is
        // clockwise on screen - so from the nine o'clock position the arc rises to twelve o'clock, which is up.
        // The end is within a rounding error of exact, because the arc is built from cubics.
        Assert.Equal(20.0, sub.Nodes[^1].Anchor.X, 9);
        Assert.Equal(0.0, sub.Nodes[^1].Anchor.Y, 9);
        Assert.Contains(sub.Nodes, n => Math.Abs(n.Anchor.X - 10) < 0.2 && Math.Abs(n.Anchor.Y + 10) < 0.2);
    }

    // ---------------------------------------------------------------- transforms

    [Fact]
    public void ATranslationMovesThePoints()
    {
        SvgImportResult result = Read("<rect x=\"0\" y=\"0\" width=\"10\" height=\"10\" transform=\"translate(5,7)\"/>");

        Assert.Equal(new Point2D(5, 7), FirstAnchor(result));
    }

    /// <summary>
    /// **Transforms apply in the order written.** `translate(10,0) scale(2,2)` moves the space and then scales it,
    /// so the translation is *not* multiplied by two - composing the other way gives (20,0) and is the classic
    /// symptom of getting the order backwards.
    /// </summary>
    [Fact]
    public void TransformListAppliesInOrder()
    {
        SvgImportResult result = Read(
            "<rect x=\"0\" y=\"0\" width=\"10\" height=\"10\" transform=\"translate(10,0) scale(2,2)\"/>");

        Assert.Equal(10.0, FirstAnchor(result).X, 9);
        Assert.Equal(0.0, FirstAnchor(result).Y, 9);

        // And the scaling still reached the geometry: the far corner is at 10 + 10*2.
        Assert.Equal(30.0, FirstPath(result).SubPaths[0].Nodes[2].Anchor.X, 9);
    }

    /// <summary>A group keeps its transform and its children keep theirs, so the two compose.</summary>
    [Fact]
    public void NestedGroupTransformsCompose()
    {
        SvgImportResult result = Read(
            "<g transform=\"translate(100,0)\"><g transform=\"scale(2,2)\">" +
            "<rect x=\"1\" y=\"1\" width=\"2\" height=\"2\"/></g></g>");

        // The rectangle's own geometry is untouched: the transforms live on the groups.
        PathItem path = FirstPath(result);
        Assert.Equal(new Point2D(1, 1), path.SubPaths[0].Nodes[0].Anchor);
        Assert.Equal(2, result.ByElement["g"]);
    }

    [Fact]
    public void ARotationTurnsThePoints()
    {
        SvgImportResult result = Read(
            "<rect x=\"10\" y=\"0\" width=\"1\" height=\"1\" transform=\"rotate(90)\"/>");

        // (10,0) turned a quarter turn about the origin is (0,10).
        Assert.Equal(0.0, FirstAnchor(result).X, 6);
        Assert.Equal(10.0, FirstAnchor(result).Y, 6);
    }

    // ---------------------------------------------------------------- the view box

    /// <summary>A view box with a larger width than the port scales the content down, which is what makes a
    /// view-box-sized file appear in an artboard-sized window.</summary>
    [Fact]
    public void TheViewBoxScalesTheContent()
    {
        SvgImportResult result = SvgReader.Read(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"100\" height=\"100\" viewBox=\"0 0 200 200\">" +
            "<rect x=\"0\" y=\"0\" width=\"200\" height=\"200\"/></svg>");

        // A hundred CSS pixels of page is seventy-five points of paper.
        Assert.Equal(75.0, result.Document.Artboards[0].Width, 6);

        // The content is still two hundred of the file's units wide and the group above it carries them into the
        // model: 200 units fitted into a 100-unit port, and the port itself three quarters of its number in points.
        ArtGroup group = result.Document.Artboards[0].Layers[0].Children.OfType<ArtGroup>().Single();
        Point2D corner = group.Transform.Transform(new Point2D(200, 200));
        Assert.Equal(75.0, corner.X, 6);
        Assert.Equal(75.0, corner.Y, 6);
    }

    /// <summary>A file with no view box has nothing to map: the declared size is the page, in points.</summary>
    [Fact]
    public void ADocumentWithNoViewBoxUsesItsDeclaredSize()
    {
        SvgImportResult result = SvgReader.Read(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"40\" height=\"30\">" +
            "<rect x=\"1\" y=\"2\" width=\"3\" height=\"4\"/></svg>");

        Assert.Equal(30.0, result.Document.Artboards[0].Width, 6);
        Assert.Equal(22.5, result.Document.Artboards[0].Height, 6);

        // The file's own numbers survive on the content, and the one group above it is what makes them points.
        ArtGroup group = result.Document.Artboards[0].Layers[0].Children.OfType<ArtGroup>().Single();
        Assert.Equal(new Point2D(0.75, 1.5), group.Transform.Transform(FirstAnchor(result)));
    }

    /// <summary>
    /// **A file with neither a size nor a view box gets the default viewport**, not a refusal.
    ///
    /// This test used to assert the opposite, and the corpus changed the answer. Inkscape's own test files include
    /// glyph fragments - an `svg` element with no size and nothing drawable but `<glyph>` definitions - and refusing
    /// them made four real files fail to import. The default object size CSS gives a replaced element with no
    /// intrinsic dimensions is what a viewer uses, so that is what this does - and it is measured in the same unit
    /// as any other size the file might have written, so 300 CSS pixels is 225 points.
    /// </summary>
    [Fact]
    public void ADocumentWithNoSizeGetsTheDefaultViewport()
    {
        SvgImportResult result = SvgReader.Read(
            "<svg xmlns=\"http://www.w3.org/2000/svg\"><rect width=\"1\" height=\"1\"/></svg>");

        Assert.Equal(225.0, result.Document.Artboards[0].Width, 6);
        Assert.Equal(225.0, result.Document.Artboards[0].Height, 6);
        Assert.Single(result.Document.AllPaths());
    }

    /// <summary>A file with no width still uses the width it does declare, rather than defaulting both.</summary>
    [Fact]
    public void AHalfSizedFileDefaultsOnlyTheMissingDimension()
    {
        SvgImportResult result = SvgReader.Read(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"120\"><rect width=\"1\" height=\"1\"/></svg>");

        Assert.Equal(90.0, result.Document.Artboards[0].Width, 6);
        Assert.Equal(225.0, result.Document.Artboards[0].Height, 6);
    }

    // ---------------------------------------------------------------- paint

    [Fact]
    public void FillAndStrokeAreRead()
    {
        SvgImportResult result = Read(
            "<rect x=\"0\" y=\"0\" width=\"10\" height=\"10\" fill=\"#ff0000\" stroke=\"blue\" stroke-width=\"3\"/>");

        PathItem path = FirstPath(result);
        Assert.Equal(1.0, path.Fill.Color.R, 6);
        Assert.Equal(0.0, path.Fill.Color.G, 6);
        Assert.True(path.Stroke.HasVisibleOutline);
        Assert.Equal(3.0, path.Stroke.Width, 6);
        Assert.Equal(1.0, path.Stroke.Color.B, 6);
    }

    [Fact]
    public void FillNoneIsNotAFill()
    {
        SvgImportResult result = Read("<rect x=\"0\" y=\"0\" width=\"10\" height=\"10\" fill=\"none\"/>");

        Assert.False(FirstPath(result).Fill.IsVisible);
    }

    /// <summary>**Paint is inherited**, so a group's stroke reaches the children that do not name one.</summary>
    [Fact]
    public void PaintIsInheritedFromAGroup()
    {
        SvgImportResult result = Read(
            "<g stroke=\"#00ff00\" stroke-width=\"4\">" +
            "<rect x=\"0\" y=\"0\" width=\"10\" height=\"10\"/>" +
            "<rect x=\"20\" y=\"0\" width=\"10\" height=\"10\" stroke=\"#0000ff\"/></g>");

        List<PathItem> paths = result.Document.AllPaths().ToList();
        Assert.Equal(1.0, paths[0].Stroke.Color.G, 6);
        Assert.Equal(4.0, paths[0].Stroke.Width, 6);

        // And the child that named its own stroke keeps it.
        Assert.Equal(1.0, paths[1].Stroke.Color.B, 6);
    }

    [Fact]
    public void AStyleAttributeWinsOverAnAttribute()
    {
        SvgImportResult result = Read(
            "<rect x=\"0\" y=\"0\" width=\"10\" height=\"10\" fill=\"#ff0000\" style=\"fill:#0000ff\"/>");

        Assert.Equal(1.0, FirstPath(result).Fill.Color.B, 6);
        Assert.Equal(0.0, FirstPath(result).Fill.Color.R, 6);
    }

    [Fact]
    public void DisplayNoneIsNotDrawn()
    {
        SvgImportResult result = Read(
            "<rect x=\"0\" y=\"0\" width=\"10\" height=\"10\"/>" +
            "<rect x=\"0\" y=\"0\" width=\"10\" height=\"10\" display=\"none\"/>");

        Assert.Single(result.Document.AllPaths());
    }

    /// <summary>A namespace the reader does not know is skipped rather than guessed at.</summary>
    [Fact]
    public void ForeignElementsAreSkipped()
    {
        SvgImportResult result = SvgReader.Read(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:sodipodi=\"http://sodipodi.sourceforge.net/DTD/sodipodi-0.0.dtd\" " +
            "width=\"10\" height=\"10\">" +
            "<sodipodi:namedview id=\"x\"/><rect width=\"5\" height=\"5\"/></svg>");

        Assert.Single(result.Document.AllPaths());
    }

    [Fact]
    public void SomethingThatIsNotSvgIsRefused()
    {
        Assert.Throws<SvgImportException>(() => SvgReader.Read("<html><body/></html>"));
        Assert.Throws<SvgImportException>(() => SvgReader.Read("<svg"));
    }
}

/// <summary>
/// **Gzipped SVG is SVG.** A `.svgz` is the same document with its bytes gzip-compressed, and both SVG 1.1 and
/// SVG 2 expect a viewer to decompress it on the way in. Reading it as text hands the parser binary, so the
/// document does not merely lose detail - it fails to parse entirely.
///
/// The assertion is the **model**: the compressed and plain forms must place the same anchor at the same point.
/// "It opened" would pass for a reader that decompressed the bytes and then dropped the geometry.
/// </summary>
public class SvgGzipTests
{
    private const string Document =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"200\" height=\"100\" viewBox=\"0 0 200 100\">" +
        "<path d=\"M10 20 L30 40\"/>" +
        "</svg>";

    private static string WriteTemp(byte[] bytes, string extension)
    {
        string path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"vccad-svgz-{Guid.NewGuid():N}{extension}");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] Gzipped(string text)
    {
        byte[] raw = System.Text.Encoding.UTF8.GetBytes(text);
        using var output = new MemoryStream();
        using (var gzip = new System.IO.Compression.GZipStream(
                   output, System.IO.Compression.CompressionMode.Compress, leaveOpen: true))
        {
            gzip.Write(raw, 0, raw.Length);
        }

        return output.ToArray();
    }

    private static Point2D FirstAnchor(SvgImportResult result)
        => result.Document.AllPaths().First().SubPaths[0].Nodes[0].Anchor;

    [Fact]
    public void AGzippedDocumentPlacesTheSameAnchorAsThePlainOne()
    {
        string plainPath = WriteTemp(System.Text.Encoding.UTF8.GetBytes(Document), ".svg");
        string gzippedPath = WriteTemp(Gzipped(Document), ".svgz");

        try
        {
            Point2D plain = FirstAnchor(SvgReader.ReadFile(plainPath));
            Point2D gzipped = FirstAnchor(SvgReader.ReadFile(gzippedPath));

            Assert.Equal(plain.X, gzipped.X);
            Assert.Equal(plain.Y, gzipped.Y);

            // And the shared value is the file's own, so two equally broken parses cannot agree with each other.
            Assert.Equal(10.0, gzipped.X);
            Assert.Equal(20.0, gzipped.Y);
        }
        finally
        {
            File.Delete(plainPath);
            File.Delete(gzippedPath);
        }
    }

    /// <summary>
    /// A stream that carries the gzip header but holds no usable content is a **parse failure naming the file**,
    /// not an empty document. `GZipStream` does not throw on a truncated stream - it yields nothing - so this
    /// cannot be caught; the empty output has to be checked for.
    /// </summary>
    [Fact]
    public void ATruncatedGzipStreamIsReportedByName()
    {
        byte[] truncated = { 0x1F, 0x8B, 0x08, 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06 };
        string path = WriteTemp(truncated, ".svgz");

        try
        {
            SvgImportException error = Assert.Throws<SvgImportException>(() => SvgReader.ReadFile(path));
            Assert.Contains(path, error.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
