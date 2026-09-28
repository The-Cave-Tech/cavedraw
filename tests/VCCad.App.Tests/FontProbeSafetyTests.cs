using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// Which style probes are safe to make across every installed font.
///
/// Asking Avalonia for a face a family does not have makes it build a synthetic one, and on
/// this machine that call crashes natively for some installed fonts — an
/// AccessViolationException, which no catch block can see and which takes the process with
/// it. The chooser has to know which questions it may ask, so this asks them one at a time
/// and the run itself is the answer: if a probe is fatal, this test file aborts and the
/// stacks say which one.
/// </summary>
public class FontProbeSafetyTests
{
    /// <summary>The system families, or an empty list when the platform will not say.</summary>
    private static IReadOnlyList<FontFamily> SystemFamilies()
    {
        try
        {
            return FontManager.Current.SystemFonts.ToList();
        }
        catch (Exception)
        {
            return Array.Empty<FontFamily>();
        }
    }

    [AvaloniaFact]
    public void ResolvingEveryFamilyInItsOwnRegularStyleIsSafe()
    {
        // The one probe the picker cannot do without: a row has to be drawn in its family.
        int resolved = 0;

        foreach (FontFamily family in SystemFamilies())
        {
            if (string.IsNullOrWhiteSpace(family.Name))
            {
                continue;
            }

            if (FontManager.Current.TryGetGlyphTypeface(new Typeface(family), out _))
            {
                resolved++;
            }
        }

        Assert.True(resolved > 0, "no system family resolved in its own regular style");
    }

    [AvaloniaFact]
    public void AskingForABoldFaceThatMayNotExistIsSafe()
    {
        // The probe that produced the crash when it was made for every family at once.
        foreach (FontFamily family in SystemFamilies())
        {
            if (string.IsNullOrWhiteSpace(family.Name))
            {
                continue;
            }

            try
            {
                FontManager.Current.TryGetGlyphTypeface(
                    new Typeface(family, FontStyle.Normal, FontWeight.Bold), out _);
            }
            catch (Exception)
            {
                // A managed failure is fine to swallow; a native one is what this test is
                // looking for, and it cannot be caught.
            }
        }
    }

    [AvaloniaFact]
    public void AskingForAnItalicFaceThatMayNotExistIsSafe()
    {
        foreach (FontFamily family in SystemFamilies())
        {
            if (string.IsNullOrWhiteSpace(family.Name))
            {
                continue;
            }

            try
            {
                FontManager.Current.TryGetGlyphTypeface(
                    new Typeface(family, FontStyle.Italic, FontWeight.Normal), out _);
            }
            catch (Exception)
            {
            }
        }
    }

    [AvaloniaFact]
    public void AskingForABoldItalicFaceThatMayNotExistIsSafe()
    {
        foreach (FontFamily family in SystemFamilies())
        {
            if (string.IsNullOrWhiteSpace(family.Name))
            {
                continue;
            }

            try
            {
                FontManager.Current.TryGetGlyphTypeface(
                    new Typeface(family, FontStyle.Italic, FontWeight.Bold), out _);
            }
            catch (Exception)
            {
            }
        }
    }

    [AvaloniaFact]
    public void ReadingTheMembersOfAResolvedFaceIsSafe()
    {
        // TryGetGlyphTypeface hands back a wrapper even when the family does not have the
        // style asked for, and reading anything from that wrapper is what the catalogue
        // needs. It is also the step the four probes above do not take: they ask and walk
        // away, so a wrapper that is not safe to use would not have shown up.
        foreach (FontFamily family in SystemFamilies())
        {
            if (string.IsNullOrWhiteSpace(family.Name))
            {
                continue;
            }

            foreach (FontWeight weight in new[] { FontWeight.Normal, FontWeight.Bold })
            {
                foreach (FontStyle style in new[] { FontStyle.Normal, FontStyle.Italic })
                {
                    try
                    {
                        if (FontManager.Current.TryGetGlyphTypeface(
                                new Typeface(family, style, weight), out IGlyphTypeface glyph))
                        {
                            _ = glyph.FamilyName;
                            _ = glyph.FontSimulations;
                            _ = glyph.Weight;
                            _ = glyph.Style;
                        }
                    }
                    catch (Exception)
                    {
                    }
                }
            }
        }
    }

    [AvaloniaFact]
    public void ReadingTheMembersOfAFaceThatDoesNotExistIsSafe()
    {
        // Every family in SystemFonts is real, so probing them proves nothing about a name
        // the machine has never heard of — which is what the picker is asked for whenever a
        // document names a font nobody installed.
        foreach (string name in new[]
                 {
                     "No Such Family Exists 12345",
                     "Helvetica",
                     "URW Nimbus Sans",
                     "Times-Roman",
                 })
        {
            foreach (FontWeight weight in new[] { FontWeight.Normal, FontWeight.Bold })
            {
                foreach (FontStyle style in new[] { FontStyle.Normal, FontStyle.Italic })
                {
                    try
                    {
                        if (FontManager.Current.TryGetGlyphTypeface(
                                new Typeface(new FontFamily(name), style, weight),
                                out IGlyphTypeface glyph))
                        {
                            _ = glyph.FamilyName;
                            _ = glyph.FontSimulations;
                        }
                    }
                    catch (Exception)
                    {
                    }
                }
            }
        }
    }
}
