namespace VCCad.App.Fonts;

/// <summary>
/// The font families somebody has starred, and the ones they used last.
///
/// A machine with two hundred families is a list nobody reads to the end, and the ones a
/// person actually uses are a handful. Starring them is what turns the list into something
/// usable, so the marks have to last between sessions - a favourite that forgets is worse
/// than none, because it teaches the person not to bother.
///
/// Both lists are the same store with different uses: one is a set, the other is ordered by
/// when each family was last chosen.
/// </summary>
public sealed class FontFavourites
{
    /// <summary>How many recently used families to keep.</summary>
    private const int RecentLimit = 10;

    private readonly FontNameList _starred;

    /// <summary>Creates a set backed by <paramref name="storePath"/>, loading what is there.</summary>
    public FontFavourites(string storePath)
    {
        _starred = new FontNameList(storePath);

        string directory = Path.GetDirectoryName(storePath) ?? string.Empty;
        Recents = new FontNameList(
            Path.Combine(directory, $"recent-{Path.GetFileName(storePath)}"),
            RecentLimit);
    }

    /// <summary>Where the machine keeps its own list.</summary>
    public static string DefaultStorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VCCad",
        "font-favourites.json");

    /// <summary>The set the application uses.</summary>
    public static FontFavourites Shared { get; } = new(DefaultStorePath);

    /// <summary>The families used most recently, newest first.</summary>
    public FontNameList Recents { get; }

    /// <summary>Every starred family, in the order they were starred.</summary>
    public IReadOnlyList<string> All => _starred.All;

    public int Count => _starred.Count;

    /// <summary>Whether a family is starred. Family names are not case sensitive.</summary>
    public bool IsFavourite(string family) => _starred.Contains(family);

    /// <summary>Stars a family that was not starred, or unstars one that was.</summary>
    /// <returns>The state afterwards.</returns>
    public bool Toggle(string family) => _starred.Toggle(family);

    /// <summary>Sets a family's state directly, which is what a checkbox wants.</summary>
    public bool Set(string family, bool favourite) => _starred.Set(family, favourite);

    /// <summary>Records that a family was just chosen for some text.</summary>
    public void Used(string family) => Recents.Touch(family);
}
