using VCCad.App.Fonts;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// Starred font families, and whether they stay starred.
///
/// A favourite that forgets between sessions is worse than no favourite at all: it teaches
/// the person not to bother. So these check the round trip through the file, not just the
/// in-memory set - a store that works until the process ends is the failure this is here to
/// catch.
/// </summary>
public class FontFavouritesTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"vccad-fav-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }

    private FontFavourites Fresh() => new(_path);

    [Fact]
    public void NothingIsStarredToBeginWith()
        => Assert.Empty(Fresh().All);

    [Fact]
    public void StarringAFamilyKeepsIt()
    {
        FontFavourites favourites = Fresh();

        Assert.True(favourites.Toggle("Adobe Arabic"));
        Assert.True(favourites.IsFavourite("Adobe Arabic"));
        Assert.Equal(new[] { "Adobe Arabic" }, favourites.All);
    }

    [Fact]
    public void FavouritesSurviveARestart()
    {
        Fresh().Toggle("Adobe Arabic");
        Fresh().Toggle("Bahnschrift");

        // A second instance reads the file, which is what the next launch does.
        FontFavourites reopened = Fresh();

        Assert.True(reopened.IsFavourite("Adobe Arabic"));
        Assert.True(reopened.IsFavourite("Bahnschrift"));
        Assert.Equal(2, reopened.Count);
    }

    [Fact]
    public void StarringTwiceUnstars()
    {
        FontFavourites favourites = Fresh();

        Assert.True(favourites.Toggle("Arial"));
        Assert.False(favourites.Toggle("Arial"));
        Assert.False(favourites.IsFavourite("Arial"));
        Assert.Empty(Fresh().All);
    }

    [Fact]
    public void TheFamilyNameIsNotCaseSensitive()
    {
        FontFavourites favourites = Fresh();
        favourites.Toggle("Adobe Arabic");

        Assert.True(favourites.IsFavourite("adobe arabic"));
        Assert.True(favourites.IsFavourite("ADOBE ARABIC"));

        // And toggling with different casing removes the one that is there rather than
        // adding a second spelling of it.
        Assert.False(favourites.Toggle("adobe arabic"));
        Assert.Empty(favourites.All);
    }

    [Fact]
    public void TheOrderIsTheOrderTheyWereStarred()
    {
        FontFavourites favourites = Fresh();
        favourites.Toggle("Bahnschrift");
        favourites.Toggle("Adobe Arabic");
        favourites.Toggle("Arial");

        Assert.Equal(new[] { "Bahnschrift", "Adobe Arabic", "Arial" }, Fresh().All);
    }

    [Fact]
    public void AnEmptyNameIsNotAFamily()
    {
        FontFavourites favourites = Fresh();

        Assert.False(favourites.Toggle(string.Empty));
        Assert.False(favourites.Toggle("   "));
        Assert.Empty(favourites.All);
    }

    [Fact]
    public void SetPutsItWhereItIsAsked()
    {
        FontFavourites favourites = Fresh();

        Assert.True(favourites.Set("Arial", true));
        Assert.True(favourites.IsFavourite("Arial"));

        // Setting the state it already has changes nothing and does not toggle it away,
        // which is what a checkbox bound to this would otherwise do on every refresh.
        Assert.True(favourites.Set("Arial", true));
        Assert.True(favourites.IsFavourite("Arial"));

        Assert.False(favourites.Set("Arial", false));
        Assert.False(favourites.IsFavourite("Arial"));
    }

    [Fact]
    public void ACorruptStoreIsAnEmptyListRatherThanAFailure()
    {
        File.WriteAllText(_path, "{ this is not json");

        FontFavourites favourites = Fresh();
        Assert.Empty(favourites.All);

        // And it can still be used, which matters more than recovering the old contents.
        Assert.True(favourites.Toggle("Arial"));
        Assert.True(Fresh().IsFavourite("Arial"));
    }

    [Fact]
    public void AStoreThatCannotBeWrittenStillWorksInMemory()
    {
        // The path is a directory, so writing fails. Starring should still take effect for
        // this session rather than throwing at the click that asked for it.
        string blocked = Path.Combine(Path.GetTempPath(), $"vccad-fav-dir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(blocked);
        try
        {
            var favourites = new FontFavourites(blocked);
            Assert.True(favourites.Toggle("Arial"));
            Assert.True(favourites.IsFavourite("Arial"));
        }
        finally
        {
            Directory.Delete(blocked, recursive: true);
        }
    }

    [Fact]
    public void TheDefaultStoreIsBesideTheRest()
    {
        string path = FontFavourites.DefaultStorePath;

        Assert.Contains("VCCad", path);
        Assert.EndsWith(".json", path);
    }
}
