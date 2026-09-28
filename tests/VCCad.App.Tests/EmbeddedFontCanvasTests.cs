using Avalonia.Headless.XUnit;
using Avalonia.Media;
using VCCad.App.Fonts;
using VCCad.Core.Model;
using VCCad.Pdf;
using Xunit;

namespace VCCad.App.Tests;

public class EmbeddedFontCanvasTests
{
    private static string? Sample()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "samples", "A0-Temi-Bow-Bustier-sewing-pattern.pdf");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    [AvaloniaFact]
    public void EmbeddedFontsResolveToGlyphTypefaces()
    {
        string? path = Sample();
        if (path is null) return;

        CadDocument doc = PdfImporter.Import(File.ReadAllBytes(path));
        List<EmbeddedFont> fonts = doc.Artboards
            .SelectMany(a => a.Layers).SelectMany(l => l.Children).OfType<TextItem>()
            .SelectMany(t => t.Runs).Select(r => r.EmbeddedFont)
            .Where(f => f is not null).Select(f => f!).Distinct().ToList();

        Assert.NotEmpty(fonts);
        EmbeddedFontManager.Register(fonts);

        int resolved = 0;
        foreach (EmbeddedFont font in fonts)
        {
            if (FontManager.Current.TryGetGlyphTypeface(
                    new Typeface(new FontFamily(font.FamilyName)), out IGlyphTypeface gtf) && gtf.GlyphCount > 0)
            {
                resolved++;
            }
        }

        Assert.Equal(fonts.Count, resolved);

        // Every computed glyph id must be a valid index in the loaded typeface,
        // and the drawn runs must be non-empty.
        foreach (TextItem text in doc.Artboards.SelectMany(a => a.Layers)
                     .SelectMany(l => l.Children).OfType<TextItem>())
        {
            foreach (TextRun run in text.Runs)
            {
                if (run.EmbeddedFont is null || run.GlyphIds is not { Length: > 0 } ids)
                {
                    continue;
                }

                Assert.True(FontManager.Current.TryGetGlyphTypeface(
                    new Typeface(new FontFamily(run.EmbeddedFont.FamilyName)), out IGlyphTypeface gtf));
                Assert.All(ids, id => Assert.True(id < gtf.GlyphCount,
                    $"glyph {id} out of range for {run.EmbeddedFont.FamilyName} ({gtf.GlyphCount})"));
            }
        }
    }
}
