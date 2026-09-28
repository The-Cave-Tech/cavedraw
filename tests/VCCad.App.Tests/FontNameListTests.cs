using VCCad.App.Fonts;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// The recently used font families, and the store both lists share.
///
/// The recent list is ordered, and the order is the whole point of it: a family chosen again
/// moves to the front rather than appearing twice. A list that grows a duplicate every time
/// somebody picks the same font is a list that fills with one name.
/// </summary>
public class FontNameListTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"vccad-names-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        foreach (string file in Directory.Exists(Path.GetDirectoryName(_path))
                     ? Directory.GetFiles(Path.GetDirectoryName(_path)!, $"*{Path.GetFileName(_path)}")
                     : Array.Empty<string>())
        {
            File.Delete(file);
        }

        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }

    [Fact]
    public void TheNewestUseComesFirst()
    {
        var list = new FontNameList(_path);
        list.Touch("Bahnschrift");
        list.Touch("Arial");
        list.Touch("Adobe Arabic");

        Assert.Equal(new[] { "Adobe Arabic", "Arial", "Bahnschrift" }, list.All);
    }

    [Fact]
    public void UsingAFamilyAgainMovesItRatherThanAddingIt()
    {
        var list = new FontNameList(_path);
        list.Touch("Arial");
        list.Touch("Bahnschrift");
        list.Touch("Arial");

        Assert.Equal(new[] { "Arial", "Bahnschrift" }, list.All);
        Assert.Equal(2, list.Count);
    }

    [Fact]
    public void RecentsSurviveARestart()
    {
        new FontNameList(_path).Touch("Arial");
        new FontNameList(_path).Touch("Bahnschrift");

        // A second reader sees the same file, which is what the next launch does.
        Assert.Equal(new[] { "Bahnschrift", "Arial" }, new FontNameList(_path).All);
    }

    [Fact]
    public void TheListIsBoundedAndDropsTheOldest()
    {
        var list = new FontNameList(_path, limit: 3);
        foreach (string name in new[] { "one", "two", "three", "four" })
        {
            list.Touch(name);
        }

        // Newest first, and "one" has fallen off the end.
        Assert.Equal(new[] { "four", "three", "two" }, list.All);
    }

    [Fact]
    public void AnEmptyNameIsNotAFamily()
    {
        var list = new FontNameList(_path);
        list.Touch(string.Empty);
        list.Touch("   ");

        Assert.Empty(list.All);
    }

    [Fact]
    public void MembershipIsNotCaseSensitive()
    {
        var list = new FontNameList(_path);
        list.Toggle("Adobe Arabic");

        Assert.True(list.Contains("adobe arabic"));

        // Removing by a different spelling removes the one that is there rather than adding
        // a second spelling of it.
        Assert.False(list.Toggle("ADOBE ARABIC"));
        Assert.Empty(list.All);
    }

    [Fact]
    public void SetDoesNotToggleWhenItIsAlreadyRight()
    {
        var list = new FontNameList(_path);

        Assert.True(list.Set("Arial", true));
        Assert.True(list.Set("Arial", true));
        Assert.True(list.Contains("Arial"));

        Assert.False(list.Set("Arial", false));
        Assert.False(list.Contains("Arial"));
    }

    [Fact]
    public void TheFavouritesAndRecentsLiveInSeparateFiles()
    {
        // Two lists in one class must not share a file, or starring a font would make it
        // look recently used and vice versa.
        var favourites = new FontFavourites(_path);

        favourites.Toggle("Adobe Arabic");
        Assert.Empty(favourites.Recents.All);

        favourites.Used("Bahnschrift");
        Assert.False(favourites.IsFavourite("Bahnschrift"));

        // And both survive a restart independently.
        var reopened = new FontFavourites(_path);
        Assert.True(reopened.IsFavourite("Adobe Arabic"));
        Assert.Equal(new[] { "Bahnschrift" }, reopened.Recents.All);
    }
}
