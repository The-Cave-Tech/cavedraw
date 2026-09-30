using VCCad.Core.Model;
using VCCad.Geometry;
using VCCad.Pdf;
using Xunit;

namespace VCCad.Pdf.Tests;

/// <summary>
/// The shipped real-world fixtures in <c>samples/</c>.
///
/// These are the documents a person actually opens, so they are the strongest
/// regression guard for import behaviour.
///
/// The A4 pattern is a *tiled* export: the artwork is drawn at full size on every
/// sheet it touches and the page edge does the cutting. Import must store that
/// faithfully — overflow included — because a PDF viewer clips it at the MediaBox.
/// An earlier attempt cut the geometry and trimmed the text runs to fit; it butchered
/// the labels, so these tests now pin the opposite: the importer must not rewrite
/// what the file draws.
/// </summary>
public class SamplePatternTests
{
    private const string A4 = "A4 Temi Bow Bustier sewing pattern .pdf";
    private const string A0 = "A0-Temi-Bow-Bustier-sewing-pattern.pdf";

    private static string? SamplePath(string fileName)
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "samples", fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static CadDocument? ImportOnce(string fileName)
    {
        string? path = SamplePath(fileName);
        return path is null ? null : PdfImporter.Import(File.ReadAllBytes(path));
    }

    private static IEnumerable<TextItem> Texts(CadDocument document)
        => document.Artboards
            .SelectMany(a => a.Layers)
            .SelectMany(l => Walk(l.Children))
            .OfType<TextItem>();

    private static IEnumerable<PathItem> Paths(CadDocument document)
        => document.Artboards
            .SelectMany(a => a.Layers)
            .SelectMany(l => Walk(l.Children))
            .OfType<PathItem>();

    [Fact]
    public void TiledA4PatternImportsAsEightPagesInAGrid()
    {
        CadDocument? document = ImportOnce(A4);
        if (document is null)
        {
            return; // sample not present in this checkout: skip cleanly
        }

        Assert.Equal(8, document.Artboards.Count);

        // Not one row of pages: a 4837x814 strip is unreadable when fitted.
        double sheetWidth = document.Artboards.Max(a => a.X + a.Width);
        double sheetHeight = document.Artboards.Max(a => a.Y + a.Height);
        Assert.True(sheetWidth / sheetHeight < 3.0,
            $"pages are laid out as a strip: {sheetWidth:F0}x{sheetHeight:F0}");
    }

    [Fact]
    public void TiledA4PatternKeepsEveryLabelExactlyAsAuthored()
    {
        // The guard for a real regression: clipping must not rewrite text. Every run
        // keeps its full string, its advance width and — where the font is embedded —
        // its per-character glyph mapping, because the renderer and the exporter both
        // rely on them.
        CadDocument? document = ImportOnce(A4);
        if (document is null)
        {
            return;
        }

        List<TextItem> texts = Texts(document).ToList();
        Assert.NotEmpty(texts);

        // Labels carry the trailing spaces the file writes, so compare trimmed.
        string[] labels = texts.Select(t => t.PlainText.Trim()).ToArray();
        Assert.Contains("TEMI BOW BUSTIER", labels);
        Assert.Contains("CENTER BACK", labels);
        Assert.Contains(labels, l => l is "UPPER CUP" or "LOWER CUP");

        foreach (TextItem text in texts)
        {
            Assert.False(string.IsNullOrWhiteSpace(text.PlainText), "a run was emptied");

            foreach (TextRun run in text.Runs)
            {
                Assert.NotEmpty(run.Text);

                // A trimmed run would have had its raw codes cleared.
                if (run.EmbeddedFont is not null)
                {
                    Assert.NotNull(run.RawCodes);
                    Assert.NotNull(run.GlyphIds);
                }

                if (run.GlyphIds is { } glyphIds)
                {
                    Assert.Equal(run.Text.Length, glyphIds.Length);
                }
            }
        }
    }

    [Fact]
    public void TiledA4PatternKeepsPathGeometryUncut()
    {
        // Overflow past the page edge is preserved: the page box clips it at render
        // time, so cutting it here would only lose the neighbouring sheet's half.
        CadDocument? document = ImportOnce(A4);
        if (document is null)
        {
            return;
        }

        List<PathItem> paths = Paths(document).ToList();
        Assert.NotEmpty(paths);

        // At least one path reaches beyond its page — that is the nature of a tiled
        // export, and it is why the view must clip.
        bool anyOverflow = document.Artboards.Any(page =>
            page.Layers.SelectMany(l => Walk(l.Children)).OfType<PathItem>()
                .Any(p => !page.Bounds.Contains(p.BoundingBox())));

        Assert.True(anyOverflow, "expected the tiled export to carry artwork past the page edge");

        // …and the curves are intact rather than flattened into polylines.
        Assert.Contains(paths, p => p.SubPaths.Any(sp => sp.Nodes.Any(n => !n.HasStraightOutgoing)));
    }

    [Fact]
    public void TiledA4PatternKeepsEverySizeLayerOnEveryPage()
    {
        CadDocument? document = ImportOnce(A4);
        if (document is null)
        {
            return;
        }

        int uk6 = document.Artboards.Count(a => a.Layers.Any(l => l.Name == "UK 6"));
        Assert.Equal(8, uk6);
    }

    [Fact]
    public void EveryTextRunKeepsTheFontTheFileSpecifies()
    {
        // "PDF import fidelity: when a font is embedded we must not substitute it."
        // A substituted face is a different design — a substitute face has a much
        // smaller counter than Helvetica Bold's and closes up when zoomed out — so the
        // substitution has to be a deliberate fallback, never an accident.
        CadDocument? doc = ImportOnce("3464_LILLIE_View_A_Sides_color.pdf");
        if (doc is null)
        {
            return;
        }

        List<TextRun> runs = Texts(doc)
            .SelectMany(t => t.Runs)
            .Where(r => r.Text.Trim().Length > 0)
            .ToList();
        Assert.NotEmpty(runs);

        // The pattern's own faces (Century Gothic for the labels and size numbers,
        // Arial for the page furniture) are embedded, so they must be carried through.
        Assert.Contains(runs, r => r.EmbeddedFont?.BaseFont.Contains("CenturyGothic", StringComparison.Ordinal) == true);

        // Only the footer lines, which are set in a non-embedded Helvetica, may fall
        // back to a substitute. Anything else being substituted would mean an embedded
        // programme was dropped on the floor.
        string[] substituted = runs.Where(r => r.EmbeddedFont is null)
            .Select(r => r.Text.Trim())
            .Distinct()
            .ToArray();

        Assert.Equal(3, substituted.Length);
        Assert.All(substituted, t => Assert.True(
            t.Contains("DO NOT REPRODUCE", StringComparison.Ordinal) ||
            t.Contains("Darren Starr", StringComparison.Ordinal) ||
            t.Contains("Munkerudstubben", StringComparison.Ordinal),
            $"'{t}' was substituted, but its font is embedded in the file"));
    }

    [Fact]
    public void SinglePageA0PatternStillImportsAsOneSheet()
    {
        CadDocument? document = ImportOnce(A0);
        if (document is null)
        {
            return;
        }

        Assert.Single(document.Artboards);
        Assert.True(document.Artboards[0].Width > 2000, "A0 is a large sheet");
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