using Avalonia.Headless.XUnit;
using VCCad.Core.Samples;
using Avalonia.Media;
using VCCad.App.Fonts;
using VCCad.Core.Model;
using VCCad.Pdf;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// Embedded fonts must be *rendered* with, not merely attached to the model.
/// "When a font is embedded we must not substitute it" is the project rule, and no
/// other viewer has trouble with this document, so neither should we.
/// </summary>
public class EmbeddedFontUsageTests
{
    private static string? SamplePath(string fileName)
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string? candidate = SampleLibrary.Find(fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    [AvaloniaFact]
    public void EmbeddedProgrammesResolveToGlyphTypefacesWithEnoughGlyphs()
    {
        string? path = SamplePath("3464_LILLIE_View_A_Sides_color.pdf");
        if (path is null)
        {
            return; // sample not present in this checkout: skip cleanly
        }

        CadDocument document = PdfImporter.Import(File.ReadAllBytes(path));
        List<TextRun> runs = document.Artboards
            .SelectMany(a => a.Layers)
            .SelectMany(l => Walk(l.Children))
            .OfType<TextItem>()
            .SelectMany(t => t.Runs)
            .Where(r => r.EmbeddedFont is not null && r.GlyphIds is { Length: > 0 })
            .ToList();
        Assert.NotEmpty(runs);

        EmbeddedFontManager.Reset(); // the registry is process-wide
        EmbeddedFontManager.Register(runs.Select(r => r.EmbeddedFont!).Distinct());

        var problems = new List<string>();
        foreach (TextRun run in runs)
        {
            EmbeddedFont font = run.EmbeddedFont!;
            string label = $"{font.BaseFont} ('{run.Text.Trim()}')";

            if (!EmbeddedFontManager.IsRegistered(font.FamilyName))
            {
                problems.Add($"{label}: programme not registered");
            }
            else if (!EmbeddedFontManager.TryGetEmbeddedGlyphTypeface(font.FamilyName, out IGlyphTypeface typeface))
            {
                problems.Add($"{label}: registered but no typeface could be created");
            }
            else if (run.GlyphIds!.Max() >= typeface.GlyphCount)
            {
                // The canvas bails out and substitutes when a glyph id is out of range.
                problems.Add(
                    $"{label}: typeface has {typeface.GlyphCount} glyphs but the run needs id {run.GlyphIds.Max()}");
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems.Distinct().Take(8)));
    }

    [AvaloniaFact]
    public void TheDocumentNamesItsSourceFontsEvenWhenTheyAreEmbedded()
    {
        // SourceFont is what lets a substitution be reported in terms the person
        // recognises ("Helvetica-Bold is not embedded") rather than the substitute's name.
        string? path = SamplePath("3464_LILLIE_View_A_Sides_color.pdf");
        if (path is null)
        {
            return;
        }

        CadDocument document = PdfImporter.Import(File.ReadAllBytes(path));
        List<TextRun> runs = document.Artboards
            .SelectMany(a => a.Layers)
            .SelectMany(l => Walk(l.Children))
            .OfType<TextItem>()
            .SelectMany(t => t.Runs)
            .ToList();

        Assert.All(runs, r => Assert.False(string.IsNullOrWhiteSpace(r.SourceFont), "a run lost its source font"));
        Assert.Contains(runs, r => r.SourceFont == "Helvetica-Bold");
        Assert.Contains(runs, r => r.SourceFont!.Contains("CenturyGothic", StringComparison.Ordinal));
    }
    /// <summary>
    /// Every item under these, groups included.
    ///
    /// The imported tree is nested - a page's content is a group from the file's own form XObjects, with
    /// optional-content groups inside it - so a walk that looks only at a layer's direct children no longer
    /// finds the artwork. These tests are about fonts and images, not structure, so they walk.
    /// </summary>
    private static IEnumerable<LayerItem> Walk(IEnumerable<LayerItem> items)
    {
        foreach (LayerItem item in items)
        {
            yield return item;
            if (item is ArtGroup group)
            {
                foreach (LayerItem nested in Walk(group.Children))
                {
                    yield return nested;
                }
            }
        }
    }
}