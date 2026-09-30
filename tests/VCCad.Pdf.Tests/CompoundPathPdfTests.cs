using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using VCCad.Core.Model;
using VCCad.Geometry;
using VCCad.Pdf;
using Xunit;

namespace VCCad.Pdf.Tests;

/// <summary>
/// A compound path in a PDF: one path with several closed subpaths, which is exactly what Illustrator
/// writes and reads.
///
/// PDF has **no compound-path object**, so the compatibility claim is precisely this: the subpaths go
/// into ONE path - not one path per outline, which would fill the holes solid - and the model's fill
/// rule becomes `/f` or `/f*`. Nothing about the object may live in the lossless sidecar, because a
/// foreign reader cannot see it.
/// </summary>
public class CompoundPathPdfTests
{
    private static CadDocument DocumentWithARing(FillRule rule = FillRule.NonZero)
    {
        CadDocument document = CadDocument.CreateDefault("compound");
        Layer layer = document.Artboards[0].Layers[0];

        PathItem face = PathFactory.CreateEllipse("face", new Point2D(200, 200), 100, 100);
        face.Fill = FillSpec.Solid(new ColorRgb(0.9, 0.7, 0.2), rule);

        PathItem eye = PathFactory.CreateEllipse("eye", new Point2D(200, 200), 40, 40);
        eye.Fill = FillSpec.Solid(new ColorRgb(0.1, 0.1, 0.1), rule);

        PathItem? compound = PathBoolean.Combine(new[] { face, eye }, BooleanOp.Subtract);
        Assert.NotNull(compound);

        compound!.Fill = FillSpec.Solid(new ColorRgb(0.9, 0.7, 0.2), rule);
        layer.AddItem(compound);
        return document;
    }

    /// <summary>The page's content stream, inflated - the PDF compresses it, so it is not readable
    /// as it stands.</summary>
    private static string PageContent(byte[] pdf)
    {
        MatchCollection streams = Regex.Matches(
            Encoding.Latin1.GetString(pdf), "stream\r?\n(.*?)endstream", RegexOptions.Singleline);

        foreach (Match match in streams)
        {
            try
            {
                byte[] raw = Encoding.Latin1.GetBytes(match.Groups[1].Value);
                using var input = new MemoryStream(raw);
                using var zlib = new ZLibStream(input, CompressionMode.Decompress);
                using var reader = new StreamReader(zlib, Encoding.UTF8);
                string text = reader.ReadToEnd();
                if (text.Contains(" m", StringComparison.Ordinal))
                {
                    return text;
                }
            }
            catch (InvalidDataException)
            {
                // Not a deflated stream.
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// The whole point: both outlines are in **one** filled path. A second fill operator would mean one
    /// path per outline, and a reader would paint the hole solid.
    ///
    /// The movetos are counted up to the fill operator rather than through the whole stream, because the
    /// object has a stroke as well as a fill and a PDF draws it twice - once to fill, once to stroke.
    /// Counting the whole stream finds four movetos and concludes, wrongly, that there are four outlines.
    /// </summary>
    [Fact]
    public void BothOutlinesGoIntoOnePath()
    {
        string content = PageContent(PdfDocumentExporter.Export(DocumentWithARing()));

        Assert.False(string.IsNullOrEmpty(content), "no page content stream was found");

        List<string> lines = content.Split('\n').Select(line => line.Trim()).ToList();
        int fillAt = lines.IndexOf("f");
        Assert.True(fillAt > 0, "the page has no fill operator");

        Assert.Equal(2, lines.Take(fillAt).Count(line => line.EndsWith(" m", StringComparison.Ordinal)));
        Assert.Single(lines.Where(line => line == "f"));
        Assert.DoesNotContain("f*", lines);
    }

    /// <summary>The model's fill rule becomes the operator, because that is what decides the holes.</summary>
    [Fact]
    public void TheFillRuleBecomesTheFillOperator()
    {
        Assert.Contains("f", PageContent(PdfDocumentExporter.Export(DocumentWithARing(FillRule.NonZero))));

        string evenOdd = PageContent(PdfDocumentExporter.Export(DocumentWithARing(FillRule.EvenOdd)));
        Assert.Contains("f*", evenOdd);
    }

    /// <summary>
    /// And it comes back as one object with both outlines - not as two objects, which would be a
    /// compound path silently taken apart by a round trip through its own format.
    /// </summary>
    [Fact]
    public void ACompoundPathSurvivesTheRoundTrip()
    {
        byte[] pdf = PdfDocumentExporter.Export(DocumentWithARing());

        CadDocument imported = PdfImporter.Import(pdf);
        List<PathItem> paths = imported.Artboards
            .SelectMany(a => a.Layers)
            .SelectMany(l => l.Children)
            .OfType<PathItem>()
            .Where(p => p.SubPaths.Count > 0)
            .ToList();

        PathItem path = Assert.Single(paths);
        Assert.Equal(2, path.SubPaths.Count);
        Assert.All(path.SubPaths, sub => Assert.True(sub.IsClosed));

        // And it still paints as a ring: the middle is not covered.
        IReadOnlyList<FlattenedOutline> outlines = PathFlattener.Flatten(path);
        Assert.False(PathFlattener.IsFilled(outlines, path.Fill.Rule, new Point2D(200, 200)));
        Assert.True(PathFlattener.IsFilled(outlines, path.Fill.Rule, new Point2D(280, 200)));
    }

    /// <summary>The fill rule makes the round trip too, since it decides what the subpaths mean.</summary>
    [Fact]
    public void TheFillRuleSurvivesTheRoundTrip()
    {
        byte[] pdf = PdfDocumentExporter.Export(DocumentWithARing(FillRule.EvenOdd));

        CadDocument imported = PdfImporter.Import(pdf);

        PathItem path = imported.Artboards
            .SelectMany(a => a.Layers)
            .SelectMany(l => l.Children)
            .OfType<PathItem>()
            .First(p => p.SubPaths.Count > 1);

        Assert.Equal(FillRule.EvenOdd, path.Fill.Rule);
    }
}
