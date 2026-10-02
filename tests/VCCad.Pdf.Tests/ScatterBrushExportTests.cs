using System.Text.RegularExpressions;
using VCCad.Core.Model;
using VCCad.Geometry;
using VCCad.Pdf;
using Xunit;

namespace VCCad.Pdf.Tests;

/// <summary>
/// **The export half of the honouring step of a scatter brush: the copies reach the file** (issue #102).
///
/// The issue names the fidelity rule directly - anything that changes what a stroke looks like must also be written
/// to the PDF - and a scatter brush changed what a stroke looks like the moment it was applied. The geometry can be
/// computed, tested and round-tripped while the exporter writes the stroke's own width and no copies, and a
/// round-trip test cannot see that: every round trip is correct.
///
/// The assertions are on the **written coordinates**, not on the parameters: the expected points are the asset's own
/// corners taken through `ScatterBrushPath.Placements`, which is the answer `brush.scatter` hands a driver. A
/// renderer that ignored the copies, or the per-copy turn and offset inside them, cannot produce them. And the
/// range is asserted **against a pinned brush of the same document**: a copy that the range moved is written here
/// and is not written when the range is zero, which is what tells "the randomness reached the file" apart from
/// "something was written".
/// </summary>
public class ScatterBrushExportTests
{
    /// <summary>A ten by ten solid square on the pasteboard, which is what the brush repeats.</summary>
    private static PathItem Square(CadDocument document)
    {
        var asset = new PathItem { Name = "copy", Fill = FillSpec.Solid(ColorRgb.Black) };
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

    /// <summary>A copy of the ten by ten asset as a twenty-across brush, its pitch and its offset stated.</summary>
    private static BrushSpec Spray(Guid asset, double spacing, ScatterParameter? offset = null, ScatterParameter? opacity = null)
        => BrushSpec.Scatter("Spray", asset, size: 20, spacing: new ScatterParameter(spacing), offset: offset, opacity: opacity);

    /// <summary>
    /// **Every copy is in the file**, at the four corners of the asset's own box taken through the transform the
    /// placement states - including each copy's own offset, which puts them off the line the stroke draws.
    /// </summary>
    [Fact]
    public void AScatterBrushWritesItsCopiesIntoTheExport()
    {
        CadDocument document = CadDocument.CreateDefault();
        PathItem asset = Square(document);
        BrushSpec brush = Spray(asset.Id, spacing: 60, offset: new ScatterParameter(30, 8));
        PathItem path = Line(document, new Point2D(40, 400), new Point2D(300, 400), brush);

        string content = Inflate(PdfDocumentExporter.Export(document));
        var written = WrittenPoints(content).ToList();

        IReadOnlyList<ScatterBrushPlacement> copies =
            ScatterBrushPath.Placements(path, brush, _ => ItemBounds.Of(asset));

        // A 260pt line at a 60pt pitch is five copies: 0, 60, 120, 180 and 240.
        Assert.Equal(5, copies.Count);

        var corners = new[]
        {
            new Point2D(0, 0), new Point2D(10, 0), new Point2D(10, 10), new Point2D(0, 10),
        };

        foreach (ScatterBrushPlacement copy in copies)
        {
            foreach (Point2D corner in corners)
            {
                Point2D expected = copy.Transform.Transform(corner);
                Assert.Contains(written, p =>
                    Math.Abs(p.X - expected.X) < 1e-4 && Math.Abs(p.Y - expected.Y) < 1e-4);
            }
        }

        // Every copy really is off the line: the range moved them, rather than the export writing them centred.
        Assert.All(copies, c => Assert.InRange(c.Offset, 22.0, 38.0));
    }

    /// <summary>
    /// And without the brush the same document writes none of them: the copies are in the file **because the stroke
    /// carries the brush**, not because the piece of artwork happens to be somewhere in the document.
    /// </summary>
    [Fact]
    public void AStrokeWithNoScatterBrushWritesNoCopies()
    {
        CadDocument document = CadDocument.CreateDefault();
        PathItem asset = Square(document);
        BrushSpec brush = Spray(asset.Id, spacing: 60, offset: new ScatterParameter(30));
        Line(document, new Point2D(40, 400), new Point2D(300, 400), brush: null);

        string content = Inflate(PdfDocumentExporter.Export(document));

        ScatterBrushPlacement first =
            ScatterBrushPath.Placements(PathOf(new Point2D(40, 400), new Point2D(300, 400)), brush, _ => ItemBounds.Of(asset))[0];
        Point2D where = first.Transform.Transform(new Point2D(0, 0));

        Assert.DoesNotContain(WrittenPoints(content), p =>
            Math.Abs(p.X - where.X) < 1e-4 && Math.Abs(p.Y - where.Y) < 1e-4);
    }

    /// <summary>
    /// **The range reaches the file.** Two documents identical but for one control's randomness: with the range at
    /// zero every copy is written on the line, and with the range stated a copy that the draw moved off it is
    /// written where the pinned brush writes nothing. The pair is what tells "the randomness is in the export" from
    /// "a copy was written somewhere".
    /// </summary>
    [Fact]
    public void TheRangeReachesTheFileAndNotJustTheFixedValue()
    {
        ScatterParameter ranged = new(0, 25);

        (CadDocument rangedDocument, PathItem rangedAsset, PathItem rangedPath, BrushSpec rangedBrush) =
            Document(ranged);
        (CadDocument pinnedDocument, PathItem pinnedAsset, _, BrushSpec pinnedBrush) =
            Document(new ScatterParameter(0, 0));

        var rangedWritten = WrittenPoints(Inflate(PdfDocumentExporter.Export(rangedDocument))).ToList();
        var pinnedWritten = WrittenPoints(Inflate(PdfDocumentExporter.Export(pinnedDocument))).ToList();

        // A copy the draw moved clear of the line the stroke draws along.
        ScatterBrushPlacement moved = ScatterBrushPath
            .Placements(rangedPath, rangedBrush, _ => ItemBounds.Of(rangedAsset))
            .First(c => Math.Abs(c.Offset) > 12.0);

        // The copy that stays on the line in the pinned document, at the same arc length.
        ScatterBrushPlacement held = ScatterBrushPath
            .Placements(PathOf(new Point2D(40, 400), new Point2D(300, 400)), pinnedBrush, _ => ItemBounds.Of(pinnedAsset))
            .First(c => Math.Abs(c.Position - moved.Position) < 1e-6);

        Point2D movedCorner = moved.Transform.Transform(new Point2D(0, 0));
        Point2D heldCorner = held.Transform.Transform(new Point2D(0, 0));

        Assert.Contains(rangedWritten, p => Near(p, movedCorner));
        Assert.DoesNotContain(pinnedWritten, p => Near(p, movedCorner));

        // And the pinned document did write the copy, on the line - so "not written" above means "moved", not
        // "the whole document failed to export".
        Assert.Contains(pinnedWritten, p => Near(p, heldCorner));
    }

    /// <summary>
    /// **A copy's own opacity reaches the file as the state the copy is painted under** - not merely as a state that
    /// exists somewhere.
    ///
    /// That distinction is the whole test. The exporter scans the document for every alpha it will use before it
    /// paints anything, so <c>/ca 0.5</c> is written into the file's graphics states **whether or not the copy is
    /// painted under it**: an assertion that the number appears would pass while the copy was drawn fully opaque,
    /// which is exactly the "stored and never honoured" defect this suite exists to catch. So the state is followed
    /// from the copy to the stroke: the ExtGState object carrying <c>/ca 0.5</c> is found, the resource name bound to
    /// that object is read off, and the **content stream** is required to switch to it. A copy painted at full
    /// strength switches to the opaque state instead and this fails.
    /// </summary>
    [Fact]
    public void ACopyOpacityIsWrittenAsTheConstantAlphaTheCopyIsPaintedUnder()
    {
        (CadDocument faded, _, PathItem fadedPath, BrushSpec fadedBrush) =
            Document(new ScatterParameter(0), opacity: new ScatterParameter(0.5));
        (CadDocument opaque, _, _, _) = Document(new ScatterParameter(0), opacity: new ScatterParameter(1.0));

        byte[] fadedPdf = PdfDocumentExporter.Export(faded);
        byte[] opaquePdf = PdfDocumentExporter.Export(opaque);

        string fadedBody = System.Text.Encoding.Latin1.GetString(fadedPdf);
        string opaqueBody = System.Text.Encoding.Latin1.GetString(opaquePdf);

        Assert.Contains("/ca 0.5 ", fadedBody, StringComparison.Ordinal);
        Assert.DoesNotContain("/ca 0.5 ", opaqueBody, StringComparison.Ordinal);

        // The half-opacity graphics state, its object number, and the resource name the page binds to it.
        Match half = Regex.Match(fadedBody, @"(?<obj>\d+) 0 obj\s*<< /Type /ExtGState /ca 0\.5 ");
        Assert.True(half.Success, "the fading copy should have put a half-opacity graphics state in the file");

        string halfObject = half.Groups["obj"].Value;
        Match bound = Regex.Match(fadedBody, $@"/(?<name>GS\d+) {halfObject} 0 R");
        Assert.True(bound.Success, $"the half-opacity state {halfObject} should be named in the page resources");

        string halfName = "/" + bound.Groups["name"].Value;
        string fadedContent = Inflate(fadedPdf);

        Assert.Contains($"{halfName} gs", fadedContent, StringComparison.Ordinal);

        // And the copy is the thing painted under it: its own first corner is drawn in the same stream, **after**
        // the switch. Without this the assertion above could be satisfied by some other translucent item.
        ScatterBrushPlacement copy =
            ScatterBrushPath.Placements(fadedPath, fadedBrush, _ => new Rect2D(0, 0, 10, 10))[0];
        Point2D corner = copy.Transform.Transform(new Point2D(0, 0));

        int switchAt = fadedContent.IndexOf($"{halfName} gs", StringComparison.Ordinal);
        Match drawn = Regex.Match(fadedContent, $@"{Regex.Escape(Num(corner.X))} {Regex.Escape(Num(corner.Y))} m");
        Assert.True(drawn.Success, "the copy's own corner should be drawn in the content stream");
        Assert.True(drawn.Index > switchAt, "the copy should be drawn after the half-opacity state is set");
    }

    /// <summary>
    /// The exporter's own number formatting, so a coordinate is searched for exactly as the file writes it.
    /// Mirrored rather than reached for, because the exporter's is internal to its assembly.
    /// </summary>
    private static string Num(double value)
        => value.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);

    private static (CadDocument Document, PathItem Asset, PathItem Path, BrushSpec Brush) Document(
        ScatterParameter offset, ScatterParameter? opacity = null)
    {
        CadDocument document = CadDocument.CreateDefault();
        PathItem asset = Square(document);
        BrushSpec brush = Spray(asset.Id, spacing: 60, offset: offset, opacity: opacity);
        PathItem path = Line(document, new Point2D(40, 400), new Point2D(300, 400), brush);
        return (document, asset, path, brush);
    }

    private static PathItem PathOf(Point2D from, Point2D to)
    {
        var path = new PathItem { Name = "probe" };
        SubPath sub = path.AddSubPath(closed: false);
        sub.Nodes.Add(new PathNode(from));
        sub.Nodes.Add(new PathNode(to));
        return path;
    }

    private static bool Near((double X, double Y) p, Point2D expected)
        => Math.Abs(p.X - expected.X) < 1e-4 && Math.Abs(p.Y - expected.Y) < 1e-4;

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
