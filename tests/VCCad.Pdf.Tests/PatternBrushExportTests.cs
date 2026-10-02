using System.Text.RegularExpressions;
using VCCad.Core.Model;
using VCCad.Geometry;
using VCCad.Pdf;
using Xunit;

namespace VCCad.Pdf.Tests;

/// <summary>
/// **The export half of the honouring step of a pattern brush: the tiles reach the file** (issue #101).
///
/// The issue names the fidelity rule directly - anything that changes what a stroke looks like must also be
/// written to the PDF - and a pattern brush changes what a stroke looks like the moment it is applied. The tile
/// set, the geometry and the round trip can all be correct while the exporter writes the stroke's own width and
/// no tiles, and a round-trip test cannot see that, because every round trip is correct.
///
/// The assertions are on the **written coordinates**: the expected points are a tile's own corners taken through
/// the placement `PatternBrushPath` states, which is the answer `brush.tiles` hands a driver. A writer that
/// ignored the tile set, or that drew the tiles without turning them, cannot produce them.
/// </summary>
public class PatternBrushExportTests
{
    /// <summary>A ten by ten solid square on the pasteboard, which is what a tile is drawn with.</summary>
    private static PathItem Square(CadDocument document, string name)
    {
        var tile = new PathItem { Name = name, Fill = FillSpec.Solid(ColorRgb.Black) };
        SubPath sub = tile.AddSubPath(closed: true);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(10, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(10, 10)));
        sub.Nodes.Add(new PathNode(new Point2D(0, 10)));
        document.Orphans.AddItem(tile);
        return tile;
    }

    private static PathItem Path(CadDocument document, BrushSpec? brush, params Point2D[] points)
    {
        var path = new PathItem { Name = "line", Fill = FillSpec.None };
        SubPath sub = path.AddSubPath(closed: false);
        foreach (Point2D point in points)
        {
            sub.Nodes.Add(new PathNode(point));
        }

        path.Strokes.Clear();
        path.Strokes.Add(new StrokeSpec(true, ColorRgb.Black, 4, StrokeCap.Butt, StrokeJoin.Miter, 4)
        {
            Brush = brush,
        });
        document.Artboards[0].Layers[0].AddItem(path);
        return path;
    }

    /// <summary>
    /// **The side tiles are in the file.** Every tile's four corners - its own box taken through the transform the
    /// placement states - is written as a path point, exactly where `brush.tiles` says the tile sits.
    /// </summary>
    [Fact]
    public void APatternBrushWritesItsSideTilesIntoTheExport()
    {
        CadDocument document = CadDocument.CreateDefault();
        PathItem side = Square(document, "side");
        BrushSpec brush = BrushSpec.Pattern("Rail", 20, side: new PatternTileSpec(side.Id));
        PathItem path = Path(document, brush, new Point2D(40, 400), new Point2D(300, 400));

        string content = Inflate(PdfDocumentExporter.Export(document));
        var written = WrittenPoints(content).ToList();

        IReadOnlyList<PatternTilePlacement> tiles = PatternBrushPath.Placements(
            path, brush, id => id == side.Id ? ItemBounds.Of(side) : null);

        // A 260pt line with a 20pt foot is thirteen whole tiles.
        Assert.Equal(13, tiles.Count);
        Assert.All(tiles, tile => Assert.Equal(PatternTileKind.Side, tile.Slot));

        var corners = new[]
        {
            new Point2D(0, 0), new Point2D(10, 0), new Point2D(10, 10), new Point2D(0, 10),
        };

        foreach (PatternTilePlacement tile in tiles)
        {
            foreach (Point2D corner in corners)
            {
                Point2D expected = tile.Transform.Transform(corner);
                Assert.Contains(written, p =>
                    Math.Abs(p.X - expected.X) < 1e-4 && Math.Abs(p.Y - expected.Y) < 1e-4);
            }
        }
    }

    /// <summary>
    /// **The corner tile is written at the turn, and the side tiles between the turns.**
    ///
    /// The corner tile is three times the set's size, so its corners are 30 points from the turn where a side
    /// tile's are 10 - a difference no writer that ignored the corner slot could produce.
    /// </summary>
    [Fact]
    public void APatternBrushWritesItsCornerTileAtTheTurn()
    {
        CadDocument document = CadDocument.CreateDefault();
        PathItem side = Square(document, "side");
        PathItem corner = Square(document, "corner");

        BrushSpec brush = BrushSpec.Pattern(
            "Rail", 20,
            side: new PatternTileSpec(side.Id),
            outerCorner: new PatternTileSpec(corner.Id, Scale: 3.0));

        PathItem path = Path(
            document, brush, new Point2D(40, 400), new Point2D(200, 400), new Point2D(200, 560));

        string content = Inflate(PdfDocumentExporter.Export(document));
        var written = WrittenPoints(content).ToList();

        Rect2D? Bounds(Guid id)
            => id == side.Id ? ItemBounds.Of(side) : id == corner.Id ? ItemBounds.Of(corner) : null;

        PatternTilePlacement turn = PatternBrushPath
            .Placements(path, brush, Bounds)
            .Single(t => t.Slot == PatternTileKind.OuterCorner);

        Assert.Equal(60.0, turn.Length, 6);
        Assert.Equal(new Point2D(200, 400), turn.Point);

        var corners = new[]
        {
            new Point2D(0, 0), new Point2D(10, 0), new Point2D(10, 10), new Point2D(0, 10),
        };

        foreach (Point2D own in corners)
        {
            Point2D expected = turn.Transform.Transform(own);
            Assert.Contains(written, p =>
                Math.Abs(p.X - expected.X) < 1e-4 && Math.Abs(p.Y - expected.Y) < 1e-4);
        }
    }

    /// <summary>
    /// And without the brush the same document writes none of it: the tiles are in the file **because the stroke
    /// carries the brush**, not because the piece of artwork happens to be somewhere in the document.
    /// </summary>
    [Fact]
    public void AStrokeWithNoPatternBrushWritesNoTiles()
    {
        CadDocument document = CadDocument.CreateDefault();
        PathItem side = Square(document, "side");
        BrushSpec brush = BrushSpec.Pattern("Rail", 20, side: new PatternTileSpec(side.Id));
        Path(document, brush: null, new Point2D(40, 400), new Point2D(300, 400));

        string content = Inflate(PdfDocumentExporter.Export(document));

        // Where the first tile would go had the stroke carried the brush - which the brushed export writes.
        var probe = new PathItem { Name = "probe" };
        SubPath sub = probe.AddSubPath(closed: false);
        sub.Nodes.Add(new PathNode(new Point2D(40, 400)));
        sub.Nodes.Add(new PathNode(new Point2D(300, 400)));
        Point2D first = PatternBrushPath.Placements(probe, brush, _ => ItemBounds.Of(side))[0].Transform
            .Transform(new Point2D(0, 0));

        Assert.DoesNotContain(WrittenPoints(content), p =>
            Math.Abs(p.X - first.X) < 1e-4 && Math.Abs(p.Y - first.Y) < 1e-4);
    }

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
