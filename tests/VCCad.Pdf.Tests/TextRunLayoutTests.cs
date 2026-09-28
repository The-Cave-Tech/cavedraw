using VCCad.Core.Model;
using VCCad.Pdf;
using Xunit;

namespace VCCad.Pdf.Tests;

/// <summary>
/// A text block's runs must be laid out along the baseline, not stacked on one another.
///
/// Every run is emitted as its own <c>BT/Tf/Tm/Tj/ET</c> with an explicit matrix, and that
/// matrix used to be the block's origin for each of them — so a block with more than one
/// run drew every run at the same place, one on top of the other. The Transparency Guide
/// arrives with runs to merge, because it draws one glyph per show operation, so this is
/// not a rare shape: the doubled text on its page 6 was exactly it.
///
/// The fix moves the pen by what each run takes to set, along the text's own baseline, so
/// the second run starts where the first ended however the block is rotated.
/// </summary>
public class TextRunLayoutTests
{
    /// <summary>The <c>Tm</c> matrices the exporter emits for a block's runs, in order.</summary>
    private static List<double[]> RunMatrices(CadDocument document)
    {
        byte[] pdf = PdfDocumentExporter.Export(document);

        // Content streams are compressed, so they are inflated before being read.
        string latin = System.Text.Encoding.Latin1.GetString(pdf);
        var text = new System.Text.StringBuilder();
        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(latin, @"(?<!end)stream\r?\n"))
        {
            int start = m.Index + m.Length;
            int end = latin.IndexOf("endstream", start, StringComparison.Ordinal);
            if (end < 0)
            {
                continue;
            }

            try
            {
                using var input = new MemoryStream(pdf, start, end - start);
                using var zlib = new System.IO.Compression.ZLibStream(
                    input, System.IO.Compression.CompressionMode.Decompress);
                using var reader = new StreamReader(zlib, System.Text.Encoding.UTF8);
                text.Append(reader.ReadToEnd());
            }
            catch (Exception)
            {
                // Not a Flate stream.
            }
        }

        var matrices = new List<double[]>();
        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(
                     text.ToString(), @"([-\d.]+) ([-\d.]+) ([-\d.]+) ([-\d.]+) ([-\d.]+) ([-\d.]+) Tm"))
        {
            matrices.Add(new[]
            {
                double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
                double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture),
                double.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture),
                double.Parse(m.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture),
                double.Parse(m.Groups[5].Value, System.Globalization.CultureInfo.InvariantCulture),
                double.Parse(m.Groups[6].Value, System.Globalization.CultureInfo.InvariantCulture),
            });
        }

        return matrices;
    }

    private static CadDocument TwoRunBlock(double rotationDegrees = 0)
    {
        CadDocument document = CadDocument.CreateDefault("Runs");
        var item = new TextItem
        {
            Name = "Two runs",
            Origin = new VCCad.Geometry.Point2D(100, 200),
            RotationRadians = rotationDegrees * Math.PI / 180.0,
            Color = new ColorRgb(0, 0, 0),
        };

        item.Runs.Add(new TextRun
        {
            Text = "AB",
            FontFamily = "Nimbus Sans",
            FontSize = 20,
            AdvanceWidth = 30,
        });
        item.Runs.Add(new TextRun
        {
            Text = "CD",
            FontFamily = "Nimbus Sans",
            FontSize = 20,
            AdvanceWidth = 28,
        });

        document.Artboards[0].Layers[0].AddItem(item);
        return document;
    }

    [Fact]
    public void TheSecondRunDoesNotStartWhereTheFirstDid()
    {
        List<double[]> matrices = RunMatrices(TwoRunBlock());

        Assert.True(matrices.Count >= 2,
            $"expected a matrix per run, found {matrices.Count}");

        double[] first = matrices[0];
        double[] second = matrices[1];

        // The first run starts at the block's origin; the second is 30 points along the
        // baseline, which for unrotated text is 30 to the right.
        Assert.Equal(first[4] + 30.0, second[4], 2);
        Assert.Equal(first[5], second[5], 2);
    }

    [Fact]
    public void TheAdvanceFollowsTheBlocksRotation()
    {
        // Rotated a quarter turn, the pen moves down the page instead of right.
        List<double[]> matrices = RunMatrices(TwoRunBlock(rotationDegrees: 90));

        Assert.True(matrices.Count >= 2, "expected a matrix per run");

        double dx = matrices[1][4] - matrices[0][4];
        double dy = matrices[1][5] - matrices[0][5];

        Assert.Equal(30.0, Math.Sqrt((dx * dx) + (dy * dy)), 1);
        Assert.True(Math.Abs(dy) > Math.Abs(dx),
            $"a quarter turn should move the pen mostly vertically, but moved ({dx}, {dy})");
    }

    [Fact]
    public void ASingleRunBlockIsUnchanged()
    {
        CadDocument document = CadDocument.CreateDefault("One");
        var item = new TextItem
        {
            Name = "One run",
            Origin = new VCCad.Geometry.Point2D(100, 200),
            Color = new ColorRgb(0, 0, 0),
        };

        item.Runs.Add(new TextRun
        {
            Text = "AB",
            FontFamily = "Nimbus Sans",
            FontSize = 20,
            AdvanceWidth = 30,
        });

        document.Artboards[0].Layers[0].AddItem(item);

        // One run means one matrix, at the block's own origin.
        Assert.Single(RunMatrices(document));
    }
}
