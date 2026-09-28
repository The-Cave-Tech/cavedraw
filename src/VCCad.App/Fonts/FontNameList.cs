using System.Text.Json;

namespace VCCad.App.Fonts;

/// <summary>
/// A list of font family names kept between sessions.
///
/// Two things need one: the families somebody starred, and the families they used last. The
/// only differences are whether the order means "most recently used" and whether the list is
/// bounded, so the file handling, the case-insensitivity and the never-throw behaviour live
/// here once.
///
/// Nothing here throws. A preference that cannot be read is not worth failing a launch over,
/// and one that cannot be written is not worth interrupting the click that asked for it.
/// </summary>
public sealed class FontNameList
{
    private readonly string _path;
    private readonly int _limit;
    private readonly List<string> _names = new();

    /// <summary>Loads the list at <paramref name="path"/>.</summary>
    /// <param name="limit">Most names to keep, oldest dropped first. Unlimited by default.</param>
    public FontNameList(string path, int limit = int.MaxValue)
    {
        _path = path;
        _limit = Math.Max(1, limit);
        _names.AddRange(Read());
    }

    /// <summary>The names, in file order.</summary>
    public IReadOnlyList<string> All => _names;

    public int Count => _names.Count;

    /// <summary>Whether the list holds a name. Names are not case sensitive.</summary>
    public bool Contains(string name)
        => IndexOf(name) >= 0;

    /// <summary>Adds a name that was absent, or removes one that was present.</summary>
    /// <returns>Whether it is present afterwards.</returns>
    public bool Toggle(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        int at = IndexOf(name);
        bool present;

        if (at >= 0)
        {
            _names.RemoveAt(at);
            present = false;
        }
        else
        {
            _names.Add(name);
            present = true;
        }

        Save();
        return present;
    }

    /// <summary>Puts a name's state where it is asked, without toggling when already there.</summary>
    public bool Set(string name, bool present)
        => Contains(name) == present ? present : Toggle(name);

    /// <summary>
    /// Records that a name was used, most recent first.
    ///
    /// Used for the "recent" list, where the order is the whole point: a name already in the
    /// list moves to the front rather than being added again.
    /// </summary>
    public void Touch(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        int at = IndexOf(name);
        if (at >= 0)
        {
            string existing = _names[at];
            _names.RemoveAt(at);
            _names.Insert(0, existing);
        }
        else
        {
            _names.Insert(0, name);

            while (_names.Count > _limit)
            {
                _names.RemoveAt(_names.Count - 1);
            }
        }

        Save();
    }

    private int IndexOf(string name)
        => string.IsNullOrWhiteSpace(name)
            ? -1
            : _names.FindIndex(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));

    private string[] Read()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return Array.Empty<string>();
            }

            string[]? names = JsonSerializer.Deserialize<string[]>(File.ReadAllText(_path));
            return names ?? Array.Empty<string>();
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    private void Save()
    {
        try
        {
            string? directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(_path, JsonSerializer.Serialize(_names));
        }
        catch (Exception)
        {
            // Failing to remember is not worth interrupting what asked.
        }
    }
}
