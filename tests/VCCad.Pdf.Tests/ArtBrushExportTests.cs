using System.Text.RegularExpressions;
using VCCad.Core.Model;
using VCCad.Geometry;
using VCCad.Pdf;
using Xunit;

namespace VCCad.Pdf.Tests;

/// <summary>
/// **The export half of the honouring step of an art brush: the placed artwork reaches the file** (issue #100).
///
/// The issue names the fidelity rule directly - anything that changes what a stroke looks like must also be
/// written to the PDF - and the art brush changed what a stroke looks like the moment it was applied. The
/// geometry was computed, tested and round-tripped while the exporter wrote the stroke's own width and no art,
/// and a round-trip test cannot see that: every round trip was correct.
///
/// The assertions are on the **written coordinates**, not on the parameters: the expected points are the asset's
/// own corners taken through `ArtBrushPath.Placements`, which is the answer `brush.placements` hands a driver.
/// A renderer that ignored the placements, or the turn inside them, cannot produce them.
/// </summary>
public class ArtBrushExportTests
{
    /// <summary>A ten by ten solid square of artwork on the pasteboard, which is what the brush maps.</summary>
    private static PathItem Square(CadDocument document)
    {
        var asset = new PathItem { Name = "tile", Fill = FillSpec.Solid(ColorRgb.Black) };
        SubPath sub = asset.AddSubPath(closed: true);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(10, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(10, 10)));
        sub.Nodes.Add(new PathNode(new Point2D(0, 10)));
        document.Orphans.AddItem(asset);
        return asset;
    }

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

    /// <summary>
    /// **The placed art is in the file.** Every placement's four corners - the asset's own box taken through the
    /// transform the placement states - is written as a path point, exactly where `brush.placements` says.
    /// </summary>
    [Fact]
    public void AnArtBrushWritesItsAssetIntoTheExport()
    {
        CadDocument document = CadDocument.CreateDefault();
        PathItem asset = Square(document);
        BrushSpec brush = BrushSpec.Art("Vine", asset.Id, size: 20, ArtStretch.Repeat);
        PathItem path = Line(document, new Point2D(40, 400), new Point2D(300, 400), brush);

        string content = Inflate(PdfDocumentExporter.Export(document));
        var written = WrittenPoints(content).ToList();

        IReadOnlyList<ArtBrushPlacement> placements =
            ArtBrushPath.Placements(path, brush, ItemBounds.Of(asset));

        // A 260pt line with a 20pt repeat is thirteen whole pieces.
        Assert.Equal(13, placements.Count);

        var corners = new[]
        {
            new Point2D(0, 0), new Point2D(10, 0), new Point2D(10, 10), new Point2D(0, 10),
        };

        foreach (ArtBrushPlacement placement in placements)
        {
            foreach (Point2D corner in corners)
            {
                Point2D expected = placement.Transform.Transform(corner);
                Assert.Contains(written, p =>
                    Math.Abs(p.X - expected.X) < 1e-4 && Math.Abs(p.Y - expected.Y) < 1e-4);
            }
        }
    }

    /// <summary>
    /// And without the brush the same document writes none of it: the art is in the file **because the stroke
    /// carries the brush**, not because the piece of artwork happens to be somewhere in the document.
    /// </summary>
    [Fact]
    public void AStrokeWithNoArtBrushWritesNoArtwork()
    {
        CadDocument document = CadDocument.CreateDefault();
        PathItem asset = Square(document);
        BrushSpec brush = BrushSpec.Art("Vine", asset.Id, size: 20, ArtStretch.Repeat);
        Line(document, new Point2D(40, 400), new Point2D(300, 400), brush: null);

        string content = Inflate(PdfDocumentExporter.Export(document));

        // Where the first piece would go had the stroke carried the brush - which the brushed export writes.
        var probe = new PathItem { Name = "probe" };
        SubPath sub = probe.AddSubPath(closed: false);
        sub.Nodes.Add(new PathNode(new Point2D(40, 400)));
        sub.Nodes.Add(new PathNode(new Point2D(300, 400)));
        Point2D first = ArtBrushPath.Placements(probe, brush, ItemBounds.Of(asset))[0].Transform
            .Transform(new Point2D(0, 0));

        Assert.DoesNotContain(WrittenPoints(content), p =>
            Math.Abs(p.X - first.X) < 1e-4 && Math.Abs(p.Y - first.Y) < 1e-4);
    }

    /// <summary>
    /// **A turned raster placement is written turned.** An <see cref="ImageItem"/> holds an axis-aligned
    /// placement and no rotation, so the export has to apply the placement's own transform - and it does, as
    /// two composed matrices whose product is the turned image. Asserted as the matrix, because that is what
    /// the file says the picture is drawn as.
    /// </summary>
    [Fact]
    public void ATurnedRasterPlacementIsWrittenTurned()
    {
        CadDocument document = CadDocument.CreateDefault();

        var image = new ImageItem
        {
            Name = "scan",
            Placement = new Rect2D(0, 0, 40, 20),
            PixelWidth = 2,
            PixelHeight = 2,
            ColorSpace = ImageColorSpace.Rgb,
            Samples = new byte[2 * 2 * 3],
        };
        document.Orphans.AddItem(image);

        BrushSpec brush = BrushSpec.Art("Scan", image.Id, size: 40, ArtStretch.ScaleProportionally);
        PathItem path = Line(document, new Point2D(100, 100), new Point2D(140, 140), brush);

        string content = Inflate(PdfDocumentExporter.Export(document));

        Match draw = Regex.Match(
            content,
            @"([-\d.]+) ([-\d.]+) ([-\d.]+) ([-\d.]+) ([-\d.]+) ([-\d.]+) cm\s+" +
            @"/(Im\d+) Do");

        Assert.True(draw.Success, $"the placed image should be drawn; the stream was:\n{content}");

        AffineTransform written = Matrix(draw, 1);

        ArtBrushPlacement placement = Assert.Single(
            ArtBrushPath.Placements(path, brush, ItemBounds.Of(image)));
        Assert.Equal(Math.Atan2(40, 40), placement.TangentRadians, 9);

        // The unit square goes to the image's own box, and the placement carries that onto the path turned.
        AffineTransform expected = placement.Transform.Compose(
            AffineTransform.CreateScale(image.Placement.Width, image.Placement.Height));

        Assert.Equal(expected.A, written.A, 4);
        Assert.Equal(expected.B, written.B, 4);
        Assert.Equal(expected.C, written.C, 4);
        Assert.Equal(expected.D, written.D, 4);

        // Non-zero off-diagonal terms are the turn itself: axis-aligned would write 0 and 0.
        Assert.NotEqual(0.0, written.B, 6);
        Assert.NotEqual(0.0, written.C, 6);
    }

    private static AffineTransform Matrix(Match match, int group)
        => new(
            double.Parse(match.Groups[group].Value, System.Globalization.CultureInfo.InvariantCulture),
            double.Parse(match.Groups[group + 1].Value, System.Globalization.CultureInfo.InvariantCulture),
            double.Parse(match.Groups[group + 2].Value, System.Globalization.CultureInfo.InvariantCulture),
            double.Parse(match.Groups[group + 3].Value, System.Globalization.CultureInfo.InvariantCulture),
            double.Parse(match.Groups[group + 4].Value, System.Globalization.CultureInfo.InvariantCulture),
            double.Parse(match.Groups[group + 5].Value, System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>The points a content stream draws, in order, as (x, y).</summary>
    private static IEnumerable<(double X, double Y)> WrittenPoints(string content)
        => Regex.Matches(content, @"([-\d.]+) ([-\d.]+) [ml]")
            .Select(m => (
                double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
                double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture)));

    private static string Inflate(byte[] pdf)
    {
        string latin = System.Text.Encoding.Latin1.GetString(pdf);
        var builder = new System.Text.StringBuilder();

        foreach (Match m in Regex.Matches(latin, @"(?<!end)stream\r?\n"))
        {
            int start = m.Index + m.Length;
            int end = latin.IndexOf("endstream", start, StringComparison.Ordinal);
            if (end < 0)
            {
                break;
            }

            try
            {
                using var input = new MemoryStream(pdf, start, end - start);
                using var zlib = new System.IO.Compression.ZLibStream(
                    input, System.IO.Compression.CompressionMode.Decompress);
                using var reader = new StreamReader(zlib, System.Text.Encoding.UTF8);
                builder.Append(reader.ReadToEnd());
            }
            catch (Exception)
            {
                // Not a Flate stream.
            }
        }

        return builder.ToString();
    }
}
