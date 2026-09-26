using VCCad.Core.Model;
using Xunit;

namespace VCCad.Pdf.Tests;

/// <summary>Imports the bundled real-world Illustrator sewing pattern to guard the
/// PDF object/content parser against regressions.</summary>
public class PdfImportSampleTests
{
    private static string? SamplePath()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "samples", "A0-Temi-Bow-Bustier-sewing-pattern.pdf");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }

    [Fact]
    public void ImportsIllustratorA0PatternWithVectorContent()
    {
        string? path = SamplePath();
        if (path is null)
        {
            return; // sample not present in this checkout
        }

        CadDocument doc = PdfImporter.Import(File.ReadAllBytes(path));

        Assert.Single(doc.Artboards);
        Artboard page = doc.Artboards[0];
        Assert.True(page.Width > 3000, "A0 width expected");
        Assert.True(page.Height > 2000, "A0 height expected");

        int paths = page.Layers.SelectMany(l => l.Children).OfType<PathItem>().Count();
        Assert.True(paths > 500, $"expected many imported paths, got {paths}");

        int nodes = page.Layers.SelectMany(l => l.Children).OfType<PathItem>()
            .Sum(p => p.SubPaths.Sum(s => s.Nodes.Count));
        Assert.True(nodes > 2000, $"expected many nodes, got {nodes}");

        // Optional-content groups must become real layers (not one merged layer).
        Assert.True(page.Layers.Count > 1, $"expected PDF layers, got {page.Layers.Count}");

        List<PathItem> allPaths = page.Layers.SelectMany(l => l.Children).OfType<PathItem>().ToList();

        // Stroke types: this pattern uses round/square caps and dashed lines.
        Assert.Contains(allPaths, p => p.Stroke.Cap is StrokeCap.Round or StrokeCap.Square);
        Assert.Contains(allPaths, p => !p.Stroke.Dash.IsEmpty);

        // Text: the Tf size is 1 and the real size lives in the text matrix, so a
        // naive import would produce 1pt (invisible) runs.
        List<TextItem> texts = page.Layers.SelectMany(l => l.Children).OfType<TextItem>().ToList();
        Assert.NotEmpty(texts);
        Assert.All(texts.SelectMany(t => t.Runs), r => Assert.True(r.FontSize > 1.0, $"text size {r.FontSize} looks unscaled"));

        // Original advance widths are captured so the substituted font can be
        // scaled to the source layout (avoids reflow/overlap).
        Assert.All(texts.SelectMany(t => t.Runs), r => Assert.True(r.AdvanceWidth is > 0, "advance width missing"));

        // The pattern has vertical (rotated) piece labels.
        Assert.Contains(texts, t => Math.Abs(t.RotationRadians) > 0.1);

        // Embedded fonts are captured for pass-through, so text renders with the
        // original face instead of a bundled substitute.
        Assert.Contains(texts.SelectMany(t => t.Runs), r => r.EmbeddedFont is not null && r.RawCodes is { Length: > 0 });
    }
}
