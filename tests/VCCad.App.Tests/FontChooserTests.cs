using VCCad.App.Fonts;
using VCCad.Core.Model;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// What the font chooser shows, and the one place it is decided.
///
/// The control and fonts.families both come through here. If they could disagree, an
/// operation reporting what the chooser "would" show would be worth nothing, and the chooser
/// is the thing that cannot be read without pixels.
/// </summary>
public class FontChooserTests
{
    [Fact]
    public void TheCategoryNamesRoundTrip()
    {
        foreach (FontCategory category in Enum.GetValues<FontCategory>())
        {
            Assert.Equal(category, FontChooser.Parse(FontChooser.NameOf(category)));
        }
    }

    [Fact]
    public void AnUnknownCategoryIsAll()
    {
        Assert.Equal(FontCategory.All, FontChooser.Parse("nonsense"));
        Assert.Equal(FontCategory.All, FontChooser.Parse(null));
    }

    [Fact]
    public void TheAmericanSpellingIsTheSameCategory()
        => Assert.Equal(FontCategory.Favourites, FontChooser.Parse("favorites"));

    [Fact]
    public void AllIsEveryFamilyTheMachineHas()
    {
        IReadOnlyList<FontFamilyEntry> all = FontChooser.Select(null, FontCategory.All);

        Assert.Equal(FontCatalog.Families().Count, all.Count);
        Assert.NotEmpty(all);
    }

    [Fact]
    public void TheCategoriesAreSubsetsOfAll()
    {
        // A row can never be in a category and not in the list it came from.
        var document = new CadDocument();
        IReadOnlyList<FontFamilyEntry> all = FontChooser.Select(document, FontCategory.All);
        var names = all.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (FontCategory category in Enum.GetValues<FontCategory>())
        {
            foreach (FontFamilyEntry family in FontChooser.Select(document, category))
            {
                Assert.Contains(family.Name, names);
            }
        }
    }

    [Fact]
    public void UsedIsEmptyWhenNothingIsOpen()
        => Assert.Empty(FontChooser.Select(null, FontCategory.Used));

    [Fact]
    public void UsedIsEmptyForADocumentThatNamesNoFont()
        => Assert.Empty(FontChooser.Select(new CadDocument(), FontCategory.Used));

    [Fact]
    public void FavouritesOnlyHoldsWhatIsStarred()
    {
        // The shared list is process-wide, so this leaves it as it found it.
        IReadOnlyList<FontFamilyEntry> all = FontChooser.Select(null, FontCategory.All);
        if (all.Count == 0)
        {
            return;
        }

        string family = all[0].Name;
        bool wasStarred = FontFavourites.Shared.IsFavourite(family);

        try
        {
            FontFavourites.Shared.Set(family, true);
            IReadOnlyList<FontFamilyEntry> starred =
                FontChooser.Select(null, FontCategory.Favourites);

            Assert.Contains(starred, f => f.Name == family);
            Assert.All(starred, f => Assert.True(FontFavourites.Shared.IsFavourite(f.Name)));

            FontFavourites.Shared.Set(family, false);
            Assert.DoesNotContain(
                FontChooser.Select(null, FontCategory.Favourites), f => f.Name == family);
        }
        finally
        {
            FontFavourites.Shared.Set(family, wasStarred);
        }
    }

    [Fact]
    public void SearchingNarrowsWithoutChangingTheOrder()
    {
        IReadOnlyList<FontFamilyEntry> all = FontChooser.Select(null, FontCategory.All);
        if (all.Count == 0)
        {
            return;
        }

        string needle = all[0].Name[..Math.Min(3, all[0].Name.Length)];
        IReadOnlyList<FontFamilyEntry> found = FontChooser.Select(null, FontCategory.All, needle);

        Assert.NotEmpty(found);
        Assert.All(found, f => Assert.Contains(needle, f.Name, StringComparison.OrdinalIgnoreCase));

        // Same order as the full list, with the others removed.
        Assert.Equal(
            all.Where(f => f.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)).Select(f => f.Name),
            found.Select(f => f.Name));
    }

    [Fact]
    public void ASearchThatMatchesNothingIsAnEmptyListRatherThanEverything()
        => Assert.Empty(FontChooser.Select(
            null, FontCategory.All, "No Such Family Exists 12345"));

    [Fact]
    public void EveryRowHasAFaceToBeDrawnIn()
    {
        // B.1: the row is set in its own face, so every row needs one. A row with no face
        // would be drawn in the default and the list would stop being a specimen sheet.
        foreach (FontFamilyEntry family in FontChooser.Select(null, FontCategory.All).Take(25))
        {
            FontFace? face = FontChooser.RowFace(family);

            Assert.NotNull(face);
            Assert.Equal(family.Name, face!.Family);
        }
    }

    [Fact]
    public void TheSampleLineIsWorthSetting()
    {
        // It has to contain lower case, upper case and spaces, or a face with a broken
        // ascender or a missing space looks fine in the preview and wrong on the page.
        Assert.Contains(" ", FontChooser.PreviewText);
        Assert.Contains(FontChooser.PreviewText, c => char.IsLower(c));
        Assert.Contains(FontChooser.PreviewText, c => char.IsUpper(c));
        Assert.True(FontChooser.PreviewText.Length >= 20);
    }

    [Fact]
    public void ClosedTheListIsOneRowPerFamily()
    {
        IReadOnlyList<FontRow> rows = FontChooser.Rows(null, FontCategory.All);

        Assert.Equal(FontCatalog.Families().Count, rows.Count);
        Assert.All(rows, r => Assert.Equal(FontRowKind.Family, r.Kind));
        Assert.All(rows, r => Assert.False(r.Expanded));
    }

    [Fact]
    public void OpeningAFamilyPutsItsFacesUnderIt()
    {
        IReadOnlyList<FontRow> closed = FontChooser.Rows(null, FontCategory.All);

        // Something with more than one face, which is what can be opened.
        FontRow? family = closed.FirstOrDefault(r => r.FaceCount > 1);
        if (family is null)
        {
            return;
        }

        var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { family.Family };
        IReadOnlyList<FontRow> open = FontChooser.Rows(null, FontCategory.All, null, expanded);

        int at = open.ToList().FindIndex(r => r.Family == family.Family);

        Assert.True(open[at].Expanded);
        Assert.Equal(closed.Count + family.FaceCount, open.Count);

        // Its faces follow immediately, one level in, and each carries its own style.
        for (int i = 1; i <= family.FaceCount; i++)
        {
            Assert.Equal(FontRowKind.Face, open[at + i].Kind);
            Assert.Equal(1, open[at + i].Depth);
            Assert.Equal(family.Family, open[at + i].Family);
            Assert.NotEmpty(open[at + i].Style);
        }

        // And the next family still follows them.
        Assert.Equal(FontRowKind.Family, open[at + family.FaceCount + 1].Kind);
    }

    [Fact]
    public void EveryFaceRowIsDrawnInThatFace()
    {
        IReadOnlyList<FontRow> closed = FontChooser.Rows(null, FontCategory.All);
        FontRow? family = closed.FirstOrDefault(r => r.FaceCount > 1);
        if (family is null)
        {
            return;
        }

        var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { family.Family };
        IReadOnlyList<FontRow> open = FontChooser.Rows(null, FontCategory.All, null, expanded);

        foreach (FontRow face in open.Where(r => r.Kind == FontRowKind.Face))
        {
            Assert.NotNull(face.Face);
            Assert.Equal(family.Family, face.Face!.Name);

            // The row carries the style's own weight and slope, so it is drawn as the face it
            // names rather than as the regular one with a label saying otherwise.
            Assert.Equal(face.Style == "Bold Italic" || face.Style is "Bold" or "Regular",
                face.Weight != Avalonia.Media.FontWeight.Normal || face.Style == "Regular");
            Assert.Equal(face.Style.Contains("Italic"), face.Slant == Avalonia.Media.FontStyle.Italic);
            Assert.Equal(1, face.Depth);
        }
    }

    [Fact]
    public void AFamilyWithOneFaceCannotBeOpened()
    {
        IReadOnlyList<FontRow> rows = FontChooser.Rows(null, FontCategory.All);

        Assert.All(rows.Where(r => r.FaceCount == 1), r =>
        {
            Assert.False(r.Expandable);

            // And it stays closed even if something asks for it to be open, because opening
            // it would add a row identical to itself.
            var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { r.Family };
            IReadOnlyList<FontRow> open = FontChooser.Rows(null, FontCategory.All, null, expanded);
            Assert.Equal(rows.Count, open.Count);
        });
    }

    [Fact]
    public void TheCategoriesNarrowTheRowsTheSameWayTheyNarrowTheFamilies()
    {
        // The first three families of each category, opened, so the face rows are counted
        // against what was actually opened rather than against nothing.
        foreach (FontCategory category in Enum.GetValues<FontCategory>())
        {
            IReadOnlyList<FontFamilyEntry> families = FontChooser.Select(null, category);
            var expanded = new HashSet<string>(
                families.Take(3).Select(f => f.Name), StringComparer.OrdinalIgnoreCase);

            IReadOnlyList<FontRow> rows = FontChooser.Rows(null, category, null, expanded);

            Assert.Equal(families.Count, rows.Count(r => r.Kind == FontRowKind.Family));

            // Exactly one face row per face of the families that were opened, and none for
            // the ones that were not.
            int expected = families.Take(3).Where(f => f.FaceCount > 1).Sum(f => f.FaceCount);
            Assert.Equal(expected, rows.Count(r => r.Kind == FontRowKind.Face));

            // Every row, family or face, belongs to a family in this category.
            var names = families.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.All(rows, r => Assert.Contains(r.Family, names));
        }
    }

    [Fact]
    public void SearchingStillNarrowsWithFamiliesOpen()
    {
        var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (FontFamilyEntry family in FontChooser.Select(null, FontCategory.All).Take(5))
        {
            expanded.Add(family.Name);
        }

        IReadOnlyList<FontRow> all = FontChooser.Rows(null, FontCategory.All, null, expanded);
        IReadOnlyList<FontRow> narrowed = FontChooser.Rows(null, FontCategory.All, "Arial", expanded);

        Assert.True(narrowed.Count <= all.Count);
        Assert.All(narrowed, r => Assert.Contains("Arial", r.Family, StringComparison.OrdinalIgnoreCase));
    }
}
