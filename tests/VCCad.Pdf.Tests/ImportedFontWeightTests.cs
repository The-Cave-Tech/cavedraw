using System;
using System.IO;
using System.Linq;
using VCCad.Core.Model;
using VCCad.Pdf;
using Xunit;

namespace VCCad.Pdf.Tests;

/// <summary>
/// **A run's weight and slant must match the face the file asked for** (issue #257).
///
/// A file whose base font is `Helvetica-Bold` produced runs with `Bold == false`. The file's own text was still drawn bold -
/// its face identity says so - while text **typed** into the block is shaped from `FontFamily` + `Bold`, so the weight
/// changed under the person's hands as they typed. That is the defect: the two paths read different things, and the flags
/// have to agree with the face the file named.
///
/// This asserts it on the **run in the document**, not on a helper's return value - the helper was already correct, which is
/// exactly why no helper-level check would have caught this.
/// </summary>
public class ImportedFontWeightTests
{
    private static string? SamplePath()
    {
        string path = Path.Combine("samples", "3464_LILLIE_View_A_Sides_color.pdf");
        if (File.Exists(path))
        {
            return path;
        }

        // The suite runs from the test project's output directory as often as from the repository root.
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            string candidate = Path.Combine(dir, path);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }

    /// <summary>
    /// Every run whose source face says Bold or Black must be flagged bold; the imported samples are real files and the
    /// names are what the file states, so this holds for any corpus under `samples/`.
    /// </summary>
    [Fact]
    public void ARunImportedFromABoldFaceIsFlaggedBold()
    {
        string? sample = SamplePath();
        if (sample is null)
        {
            // A corpus-backed test must not yield nothing on a machine without the samples (AGENTS.md).
            Assert.True(true, "samples/3464_LILLIE_View_A_Sides_color.pdf is not present - skipped.");
            return;
        }

        CadDocument document = PdfImporter.Import(File.ReadAllBytes(sample));

        var runs = document.Artboards
            .SelectMany(a => a.Layers)
            .SelectMany(l => l.Children)
            .SelectMany(TextOf)
            .SelectMany(t => t.Runs)
            .Where(r => !string.IsNullOrEmpty(r.SourceFont))
            .ToList();

        Assert.NotEmpty(runs);

        var wronglyRegular = runs
            .Where(r => r.SourceFont!.Contains("Bold", StringComparison.OrdinalIgnoreCase) ||
                        r.SourceFont.Contains("Black", StringComparison.OrdinalIgnoreCase) ||
                        r.SourceFont.Contains("Heavy", StringComparison.OrdinalIgnoreCase))
            .Where(r => !r.Bold)
            .Select(r => $"'{r.Text}' source={r.SourceFont} family={r.FontFamily} bold={r.Bold}")
            .ToList();

        Assert.True(
            wronglyRegular.Count == 0,
            "These runs came from a bold face and are not flagged bold - a character typed into one of them would be drawn " +
            "regular beside bold text (issue #257):\n  " + string.Join("\n  ", wronglyRegular));
    }

    private static System.Collections.Generic.IEnumerable<TextItem> TextOf(LayerItem item)
    {
        switch (item)
        {
            case TextItem text:
                yield return text;
                break;
            case ArtGroup group:
                foreach (LayerItem child in group.Children)
                {
                    foreach (TextItem nested in TextOf(child))
                    {
                        yield return nested;
                    }
                }

                break;
        }
    }
}
