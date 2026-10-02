using System.Text.RegularExpressions;
using VCCad.Core.Model;
using VCCad.Geometry;
using VCCad.Pdf;
using Xunit;

namespace VCCad.Pdf.Tests;

/// <summary>
/// **The export half of the recorded pen: what a tablet stroke looks like reaches the file** (issue #107).
///
/// The fidelity rule is blunt - anything that changes what a stroke looks like must also be written to the PDF - and
/// a recorded pen changes what a scatter brush looks like the moment a copy's size follows it. The geometry, the
/// model and the operations can all be right while the exporter writes the copies at a pressure nobody recorded,
/// and a round-trip test cannot see that: every round trip is correct.
///
/// The assertion is on the **written coordinates**, and it is paired: the copy's corners in the file are the ones
/// the recorded pen's placement states, and they are **not** the ones a fully pressed pen states - which the control
/// document, identical but for the record, does write.
/// </summary>
public class RecordedPenExportTests
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

    private static readonly Point2D[] Corners =
    {
        new(0, 0), new(10, 0), new(10, 10), new(0, 10),
    };

    /// <summary>
    /// A line with a scatter brush whose copies follow the pen's pressure, carrying whatever pen was recorded. The
    /// two documents this returns differ in **nothing but the record**, which is what makes the pair a test of it.
    /// </summary>
    private static (CadDocument Document, PathItem Path, PathItem Asset, BrushSpec Brush) Document(PenProfile? pen)
    {
        CadDocument document = CadDocument.CreateDefault();
        PathItem asset = Square(document);

        BrushSpec brush = BrushSpec.Scatter(
            "Spray", asset.Id, size: 20.0,
            spacing: new ScatterParameter(60.0),
            dynamics: DynamicsSpec.RespondingTo(DynamicsTarget.ScatterScale));

        var path = new PathItem { Name = "line", Fill = FillSpec.None };
        SubPath sub = path.AddSubPath(closed: false);
        sub.Nodes.Add(new PathNode(new Point2D(40, 400)));
        sub.Nodes.Add(new PathNode(new Point2D(300, 400)));
        path.Strokes.Clear();
        path.Strokes.Add(new StrokeSpec(true, ColorRgb.Black, 4, StrokeCap.Butt, StrokeJoin.Miter, 4)
        {
            Brush = brush,
            Pen = pen,
        });

        document.Artboards[0].Layers[0].AddItem(path);
        return (document, path, asset, brush);
    }

    /// <summary>
    /// **A copy is written at the size the recorded pen gives it.** The file's coordinates are the corners the
    /// recorded placement states - a copy at a quarter pressure is a quarter the size - and the corners a fully
    /// pressed pen would give are absent. The control document, whose only difference is that it records no pen,
    /// writes exactly those full-size corners.
    /// </summary>
    [Fact]
    public void ARecordedPenChangesTheCopyTheFileWrites()
    {
        PenProfile quarter = PenProfile.Constant(0.25);

        (CadDocument recorded, PathItem path, PathItem asset, BrushSpec brush) = Document(quarter);
        (CadDocument pressed, _, _, _) = Document(pen: null);

        var recordedWritten = WrittenPoints(Inflate(PdfDocumentExporter.Export(recorded))).ToList();
        var pressedWritten = WrittenPoints(Inflate(PdfDocumentExporter.Export(pressed))).ToList();

        ScatterBrushPlacement atQuarter =
            ScatterBrushPath.Placements(path, brush, _ => ItemBounds.Of(asset), 1.0, quarter)[0];
        ScatterBrushPlacement atFull =
            ScatterBrushPath.Placements(path, brush, _ => ItemBounds.Of(asset), 1.0, pen: null)[0];

        // The copy really is drawn a different size, so the two coordinate sets cannot coincide.
        Assert.Equal(0.25, atQuarter.Scale, 9);
        Assert.Equal(1.0, atFull.Scale, 9);
        Assert.NotEqual(
            atQuarter.Transform.Transform(Corners[0]).X,
            atFull.Transform.Transform(Corners[0]).X);

        foreach (Point2D corner in Corners)
        {
            Assert.Contains(recordedWritten, p => Near(p, atQuarter.Transform.Transform(corner)));
        }

        // And the full-pressure copy is not in the recorded file - which is the whole claim, since it is in the
        // control file at the same place.
        Point2D fullCorner = atFull.Transform.Transform(Corners[0]);
        Assert.DoesNotContain(recordedWritten, p => Near(p, fullCorner));
        Assert.Contains(pressedWritten, p => Near(p, fullCorner));
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
