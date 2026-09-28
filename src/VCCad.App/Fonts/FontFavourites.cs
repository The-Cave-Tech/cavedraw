using System.Text.Json;

namespace VCCad.App.Fonts;

/// <summary>
/// The font families somebody has starred.
///
/// A machine with two hundred families is a list nobody reads to the end, and the ones a
/// person actually uses are a handful. Starring them is what turns the list into something
/// usable, so the marks have to last between sessions - a favourite that forgets is worse
/// than none, because it teaches the person not to bother.
///
/// The store is a plain list of family names beside the diary and the recent files. Nothing
/// here throws: a preference that cannot be read is not worth failing a launch over, and a
/// preference that cannot be written is not worth interrupting a click over.
/// </summary>
public sealed class FontFavourites
{
    private readonly string _storePath;
    private readonly List<string> _families = new();

    /// <summary>Creates a set backed by <paramref name="storePath"/>, loading what is there.</summary>
    public FontFavourites(string storePath)
    {
        _storePath = storePath;
        _families.AddRange(Read());
    }

    /// <summary>Where the machine keeps its own list.</summary>
    public static string DefaultStorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VCCad",
        "font-favourites.json");

    /// <summary>The set the application uses.</summary>
    public static FontFavourites Shared { get; } = new(DefaultStorePath);

    /// <summary>Every starred family, in the order they were starred.</summary>
    public IReadOnlyList<string> All => _families;

    public int Count => _families.Count;

    /// <summary>Whether a family is starred. Family names are not case sensitive.</summary>
    public bool IsFavourite(string family)
        => _families.Any(f => string.Equals(f, family, StringComparison.OrdinalIgnoreCase));

    /// <summary>Stars a family that was not starred, or unstars one that was.</summary>
    /// <returns>The state afterwards.</returns>
    public bool Toggle(string family)
    {
        if (string.IsNullOrWhiteSpace(family))
        {
            return false;
        }

        int at = _families.FindIndex(f => string.Equals(f, family, StringComparison.OrdinalIgnoreCase));
        bool starred;

        if (at >= 0)
        {
            _families.RemoveAt(at);
            starred = false;
        }
        else
        {
            _families.Add(family);
            starred = true;
        }

        Write();
        return starred;
    }

    /// <summary>Sets a family's state directly, which is what a checkbox wants.</summary>
    public bool Set(string family, bool favourite)
    {
        if (IsFavourite(family) == favourite)
        {
            return favourite;
        }

        return Toggle(family);
    }

    private string[] Read()
    {
        try
        {
            if (!File.Exists(_storePath))
            {
                return Array.Empty<string>();
            }

            string[]? names = JsonSerializer.Deserialize<string[]>(File.ReadAllText(_storePath));
            return names ?? Array.Empty<string>();
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    private void Write()
    {
        try
        {
            string? directory = Path.GetDirectoryName(_storePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(_storePath, JsonSerializer.Serialize(_families));
        }
        catch (Exception)
        {
            // Failing to remember is not worth interrupting the click that asked.
        }
    }
}
