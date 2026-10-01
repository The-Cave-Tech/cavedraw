using VCCad.App.Views.Panes;
using VCCad.Core.Samples;
using VCCad.Core.Model;
using VCCad.Pdf;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// The Objects panel on the real twelve-page pattern.
///
/// This is the document the problem was reported on: a tiled sewing pattern whose pieces are
/// drawn at full size on every sheet and left for the page edge to cut. Its 3,195 objects
/// live on twelve artboards laid out in a grid, so every page except the one at the origin
/// has a large positive offset — which is exactly what the panel ignored.
///
/// The synthetic tests in <see cref="ObjectsPanePlacementTests"/> cover the rule; this one
/// covers the file, because a rule that passes on made-up geometry and fails on the real
/// thing is not fixed.
///
/// Skips cleanly when the sample is not checked out.
/// </summary>
public class ObjectsPaneSampleTests
{
    private static string? SamplePath()
    {
        var candidate = new DirectoryInfo(AppContext.BaseDirectory);
        while (candidate is not null)
        {
            string? path = SampleLibrary.Find("3464_LILLIE_View_A_Sides_color.pdf");
            if (File.Exists(path))
            {
                return path;
            }

            candidate = candidate.Parent;
        }

        return null;
    }

    /// <summary>Yields the sample path, or the empty skip sentinel when it is absent.</summary>
    public static IEnumerable<object[]> Samples()
    {
        string? path = SamplePath();
        if (path is null)
        {
            yield return new object[] { string.Empty }; // skip cleanly when absent
            yield break;
        }

        yield return new object[] { path };
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void EveryPageOfThePatternHoldsItsOwnObjects(string path)
    {
        if (path.Length == 0)
        {
            return;
        }

        CadDocument document = PdfImporter.Import(File.ReadAllBytes(path));
        Assert.Equal(12, document.Artboards.Count);

        (Dictionary<Layer, List<LayerItem>> byLayer, List<LayerItem> pasteboard) =
            ObjectsPane.Classify(document);

        // Not one page with everything else on the pasteboard: every page carries objects.
        foreach (Artboard artboard in document.Artboards)
        {
            int held = artboard.Layers.Sum(l =>
                byLayer.TryGetValue(l, out List<LayerItem>? mine) ? mine.Count : 0);

            Assert.True(held > 0,
                $"{artboard.Name} (at {artboard.X},{artboard.Y}) holds nothing; "
                + "its objects have been filed elsewhere");
        }

        int total = document.Artboards.Sum(a => a.Layers.Sum(l => l.Children.Count));

        // The overwhelming majority belong to a page. A little overflow is expected and
        // correct - a piece drawn past its sheet's edge belongs to no page - but not half
        // the document, which is what a space mix-up looks like.
        Assert.True(pasteboard.Count < total * 0.25,
            $"{pasteboard.Count} of {total} objects are on the pasteboard, which is too many "
            + "for a document whose pages are laid out in a grid");
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void ThePagesAreActuallySpreadOutSoTheTestMeansSomething(string path)
    {
        if (path.Length == 0)
        {
            return;
        }

        CadDocument document = PdfImporter.Import(File.ReadAllBytes(path));

        // If every artboard sat at the origin, ignoring the offset would be harmless and
        // this whole class of test would prove nothing. It does not.
        Assert.True(document.Artboards.Select(a => a.X).Distinct().Count() > 1
                    || document.Artboards.Select(a => a.Y).Distinct().Count() > 1,
            "the sample's pages should be laid out in a grid, not stacked at the origin");
    }
}