using Avalonia.Headless.XUnit;
using VCCad.App.Fonts;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// What the font chooser offers, and what it promises about it.
///
/// The list has to be a specimen sheet: every row is drawn in its own face, so every row has
/// to have a face that exists. The trap is that asking for a bold face the family does not
/// have succeeds — the platform hands back the regular outlines and simulates bold — so a
/// naive check reports four faces for every family on the machine and the list fills with
/// fakes.
/// </summary>
public class FontCatalogTests
{
    [AvaloniaFact]
    public void EveryFamilyOffersAtLeastOneFace()
    {
        IReadOnlyList<FontFamilyEntry> families = FontCatalog.Families();

        Assert.NotEmpty(families);
        Assert.All(families, f => Assert.True(f.FaceCount > 0,
            $"{f.Name} is listed with no face to draw it in"));
    }

    [AvaloniaFact]
    public void AListedFaceIsOneTheFontItselfProvides()
    {
        // The check behind every row, read from the font index rather than asked for: a face
        // appears only if a real face with that weight and slope is installed. Asking the
        // renderer instead would make it build a synthetic one, which is both a lie and — for
        // at least one font on this machine — a native crash.
        foreach (FontFamilyEntry family in FontCatalog.Families().Take(25))
        {
            foreach (FontFace face in family.Faces)
            {
                Assert.True(
                    IsInTheFontIndex(family.Name, face.Bold, face.Italic),
                    $"{family.Name} lists {face.Style}, which is not a face it installs");
            }
        }
    }

    /// <summary>Whether the font index reports a face of this weight and slope.</summary>
    private static bool IsInTheFontIndex(string family, bool bold, bool italic)
    {
        using var set = SkiaSharp.SKFontManager.Default.GetFontStyles(family);
        if (set is null)
        {
            return false;
        }

        for (int i = 0; i < set.Count; i++)
        {
            using SkiaSharp.SKTypeface? typeface = set.CreateTypeface(i);
            if (typeface is null)
            {
                continue;
            }

            bool b = typeface.FontStyle.Weight >= (int)SkiaSharp.SKFontStyleWeight.SemiBold;
            bool it = typeface.FontStyle.Slant != SkiaSharp.SKFontStyleSlant.Upright;
            if (b == bold && it == italic)
            {
                return true;
            }
        }

        return false;
    }

    [AvaloniaFact]
    public void TheCountMatchesTheFacesListed()
    {
        foreach (FontFamilyEntry family in FontCatalog.Families())
        {
            Assert.Equal(family.Faces.Count, family.FaceCount);
        }
    }

    [AvaloniaFact]
    public void AFamilyWithMoreThanOneFaceSaysSoInItsLabel()
    {
        var many = new FontFamilyEntry(
            "Adobe Arabic",
            new[]
            {
                new FontFace("Adobe Arabic", "Regular", false, false),
                new FontFace("Adobe Arabic", "Bold", true, false),
                new FontFace("Adobe Arabic", "Italic", false, true),
                new FontFace("Adobe Arabic", "Bold Italic", true, true),
            },
            IsStandard: false);

        Assert.Equal("Adobe Arabic (4)", many.Label);
    }

    [AvaloniaFact]
    public void AFamilyWithOneFaceIsJustItsName()
    {
        var one = new FontFamilyEntry(
            "Agency FB", new[] { new FontFace("Agency FB", "Regular", false, false) },
            IsStandard: false);

        Assert.Equal("Agency FB", one.Label);
    }

    [AvaloniaFact]
    public void EveryFamilyHasAFaceOfItsOwnToDrawItsRowWith()
    {
        // The row for a family is drawn in that family, so the face it picks has to be one
        // the family really has. It is not always a regular face: a family like "Berlin Sans
        // FB Demi" has only a demi one, and drawing its row in something else would be the
        // substitution this list exists to avoid.
        foreach (FontFamilyEntry family in FontCatalog.Families())
        {
            FontFace face = family.RegularFace;
            Assert.Equal(family.Name, face.Family);
            Assert.Contains(face, family.Faces);
        }
    }

    [AvaloniaFact]
    public void ARegularFaceIsUsedWhenTheFamilyHasOne()
    {
        foreach (FontFamilyEntry family in FontCatalog.Families())
        {
            FontFace? regular = family.Faces.FirstOrDefault(f => !f.Bold && !f.Italic);
            if (regular is not null)
            {
                Assert.Equal("Regular", family.RegularFace.Style);
            }
        }
    }

    [AvaloniaFact]
    public void TheRegularFaceIsPreferredOverWhicheverComesFirst()
    {
        var entry = new FontFamilyEntry(
            "F",
            new[]
            {
                new FontFace("F", "Bold", true, false),
                new FontFace("F", "Regular", false, false),
            },
            IsStandard: false);

        Assert.Equal("Regular", entry.RegularFace.Style);
    }

    [AvaloniaFact]
    public void TheRowsHaveFacesThatCanActuallyBeResolved()
    {
        // Not just "HasRealFace says yes": the typeface a row would be rendered with has to
        // resolve. This is what stops a row being drawn in the default instead of its own
        // face without anyone noticing.
        foreach (FontFamilyEntry family in FontCatalog.Families().Take(25))
        {
            FontFace face = family.RegularFace;
            Assert.True(face.Typeface.FontFamily.Name.Length > 0);
            Assert.True(
                Avalonia.Media.FontManager.Current.TryGetGlyphTypeface(face.Typeface, out _),
                $"{family.Name} cannot be rendered to draw its own row");
        }
    }

    [AvaloniaFact]
    public void TheStandardFacesAreOfferedAndMarkedApart()
    {
        IReadOnlyList<FontFamilyEntry> families = FontCatalog.Families();
        IReadOnlyList<string> standard = StandardFontResolver.StandardFamilyNames();

        // Only what can actually be drawn is marked standard; a standard name with no
        // installed programme is reported as missing elsewhere, not offered here.
        foreach (FontFamilyEntry family in families.Where(f => f.IsStandard))
        {
            Assert.Contains(family.Name, standard, StringComparer.OrdinalIgnoreCase);
            Assert.True(family.FaceCount > 0);
        }

        // And the other direction: a standard family that IS resolvable is offered.
        foreach (string name in standard)
        {
            if (FontCatalog.FacesOf(name).Count > 0)
            {
                Assert.Contains(families, f => f.IsStandard && f.Name == name);
            }
        }
    }

    [AvaloniaFact]
    public void NoFamilyIsListedTwice()
    {
        IReadOnlyList<FontFamilyEntry> families = FontCatalog.Families();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (FontFamilyEntry family in families)
        {
            Assert.True(seen.Add(family.Name), $"{family.Name} appears more than once");
        }
    }

    [AvaloniaFact]
    public void AFamilyThatCannotBeDrawnIsNotOffered()
    {
        Assert.Empty(FontCatalog.FacesOf("No Such Family Exists 12345"));
        Assert.Null(FontCatalog.Find("No Such Family Exists 12345"));
    }
}
