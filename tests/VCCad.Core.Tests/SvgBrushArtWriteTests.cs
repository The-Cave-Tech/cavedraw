using System.Globalization;
using System.Xml.Linq;
using VCCad.Core.Model;
using VCCad.Core.Svg;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// **The SVG half of the honouring step of a brush: the placed artwork reaches the file** (issue #186).
///
/// The canvas draws the artwork a stroke's brush maps and the PDF exporter writes it; `SvgWriter` called
/// `StrokeOutlineBuilder.Plan(path, stroke)` alone and emitted the stroke's own outline and nothing else. A
/// round-trip test cannot see that, because every round trip was correct: the brush is a stroke property that no
/// dump compares, so the file came back the document it went in while losing the picture.
///
/// The three brush kinds answer one seam - `<see cref="PlacedArt.Resolve"/>` - so they are asserted here through
/// one loop, exactly as the canvas and the exporter consume them. The assertion is on the **written bytes**: the
/// `transform` the file states is the placement transform `PlacedArt` gives, and the element carries the asset's
/// own path data, so a writer that drew the artwork anywhere else, or drew something else, cannot satisfy it.
/// </summary>
public class SvgBrushArtWriteTests
{
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";

    /// <summary>A ten by ten solid square of artwork on the pasteboard, which is what a brush maps.</summary>
    private static PathItem Square(CadDocument document, string name = "tile")
    {
        var asset = new PathItem { Name = name, Fill = FillSpec.Solid(ColorRgb.Black) };
        SubPath sub = asset.AddSubPath(closed: true);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(10, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(10, 10)));
        sub.Nodes.Add(new PathNode(new Point2D(0, 10)));
        document.Orphans.AddItem(asset);
        return asset;
    }

    /// <summary>The square's own path data, which is what a copy of the asset must carry in the file.</summary>
    private const string SquareData = "M 0 0 L 10 0 L 10 10 L 0 10 L 0 0 Z";

    private static PathItem Line(CadDocument document, Point2D from, Point2D to, BrushSpec? brush)
    {
        var path = new PathItem { Name = "line", Fill = FillSpec.None };
        SubPath sub = path.AddSubPath(closed: false);
        sub.Nodes.Add(new PathNode(from));
        sub.Nodes.Add(new PathNode(to));
        path.Strokes.Clear();
        path.Strokes.Add(new StrokeSpec(true, ColorRgb.Black, 4, StrokeCap.Butt, StrokeJoin.Miter, 4)
        {
            Brush = brush,
        });
        document.Artboards[0].Layers[0].AddItem(path);
        return path;
    }

    /// <summary>Every element of the file that states a transform, which is how a placement is written.</summary>
    private static List<XElement> Transformed(XDocument svg)
        => svg.Descendants().Where(e => e.Attribute("transform") is not null).ToList();

    /// <summary>The affine transform a `matrix(...)` attribute states.</summary>
    private static AffineTransform Matrix(XElement element)
    {
        string value = element.Attribute("transform")!.Value;
        Assert.StartsWith("matrix(", value, StringComparison.Ordinal);

        double[] numbers = value[7..^1]
            .Split(',')
            .Select(part => double.Parse(part, CultureInfo.InvariantCulture))
            .ToArray();

        Assert.Equal(6, numbers.Length);
        return new AffineTransform(numbers[0], numbers[1], numbers[2], numbers[3], numbers[4], numbers[5]);
    }

    private static bool Same(AffineTransform a, AffineTransform b)
        => Math.Abs(a.A - b.A) < 1e-9 && Math.Abs(a.B - b.B) < 1e-9 &&
           Math.Abs(a.C - b.C) < 1e-9 && Math.Abs(a.D - b.D) < 1e-9 &&
           Math.Abs(a.E - b.E) < 1e-9 && Math.Abs(a.F - b.F) < 1e-9;

    /// <summary>
    /// **Every piece the seam answers with is an element at that piece's transform, carrying the asset.**
    ///
    /// The expected transforms are `PlacedArt.Resolve`'s own answer - the same call the canvas and the PDF writer
    /// make - so a writer that ignored the brush, placed the art at the wrong point, or turned it wrongly cannot
    /// pass. The asset's path data is asserted beside the transform so that "a group exists here" is separated
    /// from "the right artwork is in it".
    /// </summary>
    private static void AssertEveryPieceIsWritten(CadDocument document, PathItem path, BrushSpec brush, int expected)
    {
        SvgWriteResult result = SvgWriter.WriteResult(document);
        Assert.Empty(result.Missing);

        IReadOnlyList<PlacedArt> art = PlacedArt.Resolve(document, path, brush);
        Assert.Equal(expected, art.Count);

        List<XElement> placed = Transformed(XDocument.Parse(result.Svg));

        foreach (PlacedArt piece in art)
        {
            Assert.Contains(placed, element =>
                Same(Matrix(element), piece.Placement.Transform) &&
                element.Descendants(Svg + "path").Any(p => (string?)p.Attribute("d") == SquareData));
        }

        // And reading the file back yields them: the pieces are artwork the reader can see, not decoration.
        CadDocument again = SvgReader.Read(result.Svg).Document;
        foreach (PlacedArt piece in art)
        {
            Assert.Contains(again.AllGroups(), group =>
                Same(group.Transform, piece.Placement.Transform) &&
                group.Children.OfType<PathItem>().Any(child =>
                    Math.Abs(child.BoundingBox().X) < 1e-9 &&
                    Math.Abs(child.BoundingBox().Y) < 1e-9 &&
                    Math.Abs(child.BoundingBox().Width - 10) < 1e-9 &&
                    Math.Abs(child.BoundingBox().Height - 10) < 1e-9));
        }
    }

    /// <summary>An art brush maps one asset along the whole path: thirteen 20pt pieces on a 260pt line.</summary>
    [Fact]
    public void AnArtBrushWritesItsAssetIntoTheSvg()
    {
        CadDocument document = CadDocument.CreateDefault();
        PathItem asset = Square(document);
        BrushSpec brush = BrushSpec.Art("Vine", asset.Id, size: 20, ArtStretch.Repeat);
        PathItem path = Line(document, new Point2D(40, 400), new Point2D(300, 400), brush);

        AssertEveryPieceIsWritten(document, path, brush, expected: 13);
    }

    /// <summary>A pattern brush lays a tile set along the path, through the same seam and the same entry point.</summary>
    [Fact]
    public void APatternBrushWritesItsSideTilesIntoTheSvg()
    {
        CadDocument document = CadDocument.CreateDefault();
        PathItem side = Square(document, "side");
        BrushSpec brush = BrushSpec.Pattern("Rail", 20, side: new PatternTileSpec(side.Id));
        PathItem path = Line(document, new Point2D(40, 400), new Point2D(300, 400), brush);

        AssertEveryPieceIsWritten(document, path, brush, expected: 13);
    }

    /// <summary>A scatter brush repeats one asset with its own turn, size and offset - five copies here.</summary>
    [Fact]
    public void AScatterBrushWritesItsCopiesIntoTheSvg()
    {
        CadDocument document = CadDocument.CreateDefault();
        PathItem asset = Square(document, "copy");
        BrushSpec brush = BrushSpec.Scatter(
            "Spray", asset.Id, size: 20, spacing: new ScatterParameter(60), offset: new ScatterParameter(30, 8));
        PathItem path = Line(document, new Point2D(40, 400), new Point2D(300, 400), brush);

        AssertEveryPieceIsWritten(document, path, brush, expected: 5);
    }

    /// <summary>
    /// **Without the brush the same document writes none of it.** The artwork is in the file because the stroke
    /// carries the brush, not because the asset happens to be somewhere in the document - the pasteboard copy is
    /// written untransformed, and the placement transform is nowhere.
    /// </summary>
    [Fact]
    public void AStrokeWithNoBrushWritesNoArtwork()
    {
        CadDocument document = CadDocument.CreateDefault();
        PathItem asset = Square(document);
        BrushSpec brush = BrushSpec.Art("Vine", asset.Id, size: 20, ArtStretch.Repeat);
        PathItem path = Line(document, new Point2D(40, 400), new Point2D(300, 400), brush: null);

        IReadOnlyList<PlacedArt> wouldBe = PlacedArt.Resolve(document, path, brush);
        Assert.NotEmpty(wouldBe);

        SvgWriteResult result = SvgWriter.WriteResult(document);
        Assert.Empty(result.Missing);

        List<XElement> placed = Transformed(XDocument.Parse(result.Svg));

        Assert.DoesNotContain(placed, element =>
            Same(Matrix(element), wouldBe[0].Placement.Transform) &&
            element.Descendants(Svg + "path").Any(p => (string?)p.Attribute("d") == SquareData));
    }

    /// <summary>
    /// **A brush whose asset the document does not have places no art**, and the file must not invent a copy of
    /// the asset that is still in the document - <c>PlacedArt.Resolve</c> answers with nothing, and so does the file.
    /// </summary>
    [Fact]
    public void ABrushNamingAMissingAssetWritesNothing()
    {
        CadDocument document = CadDocument.CreateDefault();
        Square(document);
        BrushSpec brush = BrushSpec.Art("Vine", Guid.NewGuid(), size: 20, ArtStretch.Repeat);
        PathItem path = Line(document, new Point2D(40, 400), new Point2D(300, 400), brush);

        Assert.Empty(PlacedArt.Resolve(document, path, brush));

        SvgWriteResult result = SvgWriter.WriteResult(document);

        XDocument svg = XDocument.Parse(result.Svg);
        Assert.DoesNotContain(Transformed(svg), element =>
            element.Descendants(Svg + "path").Any(p => (string?)p.Attribute("d") == SquareData));
    }
}
