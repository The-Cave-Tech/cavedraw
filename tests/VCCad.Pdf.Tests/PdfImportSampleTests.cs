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
    }
}
