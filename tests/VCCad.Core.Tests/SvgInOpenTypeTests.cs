using VCCad.Core.Model;
using VCCad.Core.Svg;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// **A file that supplies its own font is drawn with that font's glyphs, not with a substitute.**
///
/// `text-svg-glyph-custom.svg` is the corpus's SVG-in-OpenType case: an `@font-face` naming
/// `./svginotf/svginotf_testfont1.otf`, and `abcd` written in it. The programme beside the file carries one SVG
/// drawing per glyph, in font units with Y up, so the picture the file means is those drawings placed along the
/// line - and until this landed the reader warned that it does not load fonts from the document and drew a
/// substituted face instead.
///
/// The assertions are the model and the geometry: no text remains, four glyph shapes are placed along the run at
/// the size the file states, and the reader says out loud that the text became outlines. A control file whose font
/// is not there keeps its text and keeps the old warning, so the new path cannot swallow a face it did not load.
/// </summary>
public class SvgInOpenTypeTests
{
    private static string? RenderingTests()
    {
        string cache = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "vccad-corpora");
        if (!Directory.Exists(cache))
        {
            return null;
        }

        foreach (string root in Directory.GetDirectories(cache, "inkscape*"))
        {
            try
            {
                string? font = Directory
                    .GetFiles(root, "svginotf_testfont1.otf", SearchOption.AllDirectories)
                    .FirstOrDefault();
                if (font is not null)
                {
                    return Path.GetDirectoryName(Path.GetDirectoryName(font)!)!;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        return null;
    }

    [Fact]
    public void TheDocumentsOwnFontDrawsItsTextAsItsOwnGlyphs()
    {
        string? root = RenderingTests();
        string file = Path.Combine(root ?? string.Empty, "text-svg-glyph-custom.svg");
        if (root is null || !File.Exists(file))
        {
            return;
        }

        SvgImportResult result = SvgReader.ReadFile(file);

        // The words are gone, because the picture is the file's glyphs rather than a text run this machine cannot
        // set - and that is said rather than left to be discovered.
        Assert.Empty(result.Document.AllItems().OfType<TextItem>());
        Assert.Contains(
            result.Warnings, w => w.Contains("drawn as outlines", StringComparison.Ordinal));
        Assert.Contains(
            result.Warnings, w => w.Contains("SVGinOTF testfont1", StringComparison.Ordinal));

        PathItem[] glyphs = result.Document.AllItems().OfType<PathItem>().ToArray();
        Assert.True(glyphs.Length >= 4, $"the four glyphs' shapes should be placed, and {glyphs.Length} were");

        // Placed along the run at the size the file states: font-size 59.9972 against a 1000-unit em is 0.06, so
        // each 500-unit glyph is 30 points wide and the four of them span more than a hundred - where a reader that
        // dropped the placement would put every shape on one spot.
        Rect2D placed = Union(glyphs.Select(g => g.BoundingBox()));
        Assert.True(placed.Width > 100, $"the run should span its glyphs, and it spans {placed.Width}");

        // The glyph drawings are font units with Y up, so below the baseline is *down* the page in the model: the
        // shapes sit at the run's baseline and below it, not above it.
        Assert.True(placed.Bottom >= placed.Top + 20, $"the glyphs should have height, and they span {placed.Height}");
    }

    /// <summary>**The control.** A `@font-face` whose file is not there keeps the text and keeps the warning.</summary>
    [Fact]
    public void AFaceThatCannotBeLoadedIsStillReportedAndTheTextKept()
    {
        string directory = Path.Combine(Path.GetTempPath(), "vccad-font-face-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string file = Path.Combine(directory, "missing-face.svg");
            File.WriteAllText(file,
                "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"200\" height=\"100\">" +
                "<style>@font-face { font-family: \"Nowhere\"; src: url(\"nowhere.otf\"); }" +
                "text { font-family: \"Nowhere\"; font-size: 20px; }</style>" +
                "<text x=\"10\" y=\"50\">abc</text></svg>");

            SvgImportResult result = SvgReader.ReadFile(file);

            Assert.Single(result.Document.AllItems().OfType<TextItem>());
            Assert.Contains(
                result.Warnings,
                w => w.Contains("does not load fonts from the document", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static Rect2D Union(IEnumerable<Rect2D> boxes)
    {
        Rect2D[] all = boxes.ToArray();
        double left = all.Min(b => b.Left);
        double top = all.Min(b => b.Top);
        double right = all.Max(b => b.Right);
        double bottom = all.Max(b => b.Bottom);
        return new Rect2D(left, top, right - left, bottom - top);
    }
}
