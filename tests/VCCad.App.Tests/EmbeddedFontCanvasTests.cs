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

        Assert.True(EmbeddedFontManager.IsInitialized,
            "Avalonia never initialised the embedded font collection, so no imported " +
            "programme can resolve and the canvas would draw through a fallback face.");
        // The registry is process-wide, so the meaningful property is that this
        // document's programmes are all present — not an exact global count.
        Assert.True(EmbeddedFontManager.RegisteredCount >= fonts.Count,
            $"registered {EmbeddedFontManager.RegisteredCount} programme(s), expected at least {fonts.Count}");

        int resolved = 0;
        var unresolved = new List<string>();
        foreach (EmbeddedFont font in fonts)
        {
            if (EmbeddedFontManager.TryGetEmbeddedGlyphTypeface(font.FamilyName, out IGlyphTypeface gtf) &&
                gtf.GlyphCount > 0)
            {
                resolved++;
            }
            else
            {
                unresolved.Add($"{font.FamilyName} ({Convert.ToHexString(font.Program.Take(4).ToArray())})");
            }
        }

        // Every programme the platform *can* load must resolve, and every glyph id
        // must be valid for it. Programmes it cannot load must be reported as
        // unresolved so the canvas substitutes readable text — never silently
        // resolved to a fallback face, which is what garbled the canvas.
        Assert.True(resolved >= 1, "no embedded programme resolved at all");
        Assert.True(resolved == fonts.Count,
            $"{fonts.Count - resolved} embedded programme(s) did not resolve: {string.Join(", ", unresolved)}");
        Assert.DoesNotContain(fonts, f => !EmbeddedFontManager.IsRegistered(f.FamilyName));

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

                if (!EmbeddedFontManager.TryGetEmbeddedGlyphTypeface(
                        run.EmbeddedFont.FamilyName, out IGlyphTypeface gtf))
                {
                    continue; // canvas substitutes the decoded text for this run
                }

                Assert.All(ids, id => Assert.True(id < gtf.GlyphCount,
                    $"glyph {id} out of range for {run.EmbeddedFont.FamilyName} ({gtf.GlyphCount})"));
            }
        }

        if (unresolved.Count > 0)
        {
            // Documented gap: bare CFF programmes are not an sfnt container, so the
            // platform font manager rejects them. They currently render substituted.
            Assert.True(
                unresolved.All(u => u.Contains("01000402")),
                "unexpected unresolved programmes (not bare CFF): " + string.Join(", ", unresolved));
        }
    }

    /// <summary>
    /// The canvas draws imported text by glyph id, so it must resolve the
    /// programme it imported rather than let the global font manager answer with
    /// a fallback face. Avalonia's global lookup reports success for an unknown
    /// family (returning the default typeface), which indexed the imported glyph
    /// ids into an unrelated font and painted garbage on the Win32 backend.
    /// </summary>
    [AvaloniaFact]
    public void UnknownFamilyIsRejectedRatherThanFallingBack()
    {
        Assert.False(EmbeddedFontManager.TryGetEmbeddedGlyphTypeface(
            "VCCadEmbDefinitelyNotRegistered", out _));

        // The global manager is happy to answer with something else — that is the
        // hazard the strict lookup exists to avoid.
        bool globalFallback = FontManager.Current.TryGetGlyphTypeface(
            new Typeface(new FontFamily("VCCadEmbDefinitelyNotRegistered")), out IGlyphTypeface? fallback);

        if (globalFallback)
        {
            Assert.NotNull(fallback);
        }
    }
}
