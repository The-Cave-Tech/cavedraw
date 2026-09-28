using System.Text.Json;

namespace VCCad.App.Views;

/// <summary>
/// The list of recently opened files, kept beside the diary and the font directory.
///
/// Opening a document is the most common thing anyone does, and the native file picker is
/// the one control automation genuinely cannot drive — so remembering where files were is
/// both a convenience and the only way a driver can get back to one without being told
/// the path again.
/// </summary>
internal static class RecentFiles
{
    private const int Limit = 12;

    private static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VCCad",
        "recent.json");

    /// <summary>The remembered paths, most recent first. Never throws.</summary>
    public static IReadOnlyList<string> Load()
    {
        try
        {
            if (!File.Exists(StorePath))
            {
                return Array.Empty<string>();
            }

            string[]? paths = JsonSerializer.Deserialize<string[]>(File.ReadAllText(StorePath));
            return paths ?? Array.Empty<string>();
        }
        catch (Exception)
        {
            // A corrupt or unreadable list is not worth failing a launch over.
            return Array.Empty<string>();
        }
    }

    /// <summary>Records a path as most recently opened.</summary>
    public static IReadOnlyList<string> Add(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Load();
        }

        var paths = new List<string> { path };
        foreach (string existing in Load())
        {
            if (!string.Equals(existing, path, StringComparison.OrdinalIgnoreCase) &&
                paths.Count < Limit)
            {
                paths.Add(existing);
            }
        }

        Save(paths);
        return paths;
    }

    /// <summary>Drops a path that can no longer be opened.</summary>
    public static IReadOnlyList<string> Remove(string path)
    {
        List<string> remaining = Load()
            .Where(p => !string.Equals(p, path, StringComparison.OrdinalIgnoreCase))
            .ToList();

        Save(remaining);
        return remaining;
    }

    private static void Save(IReadOnlyList<string> paths)
    {
        try
        {
            string? directory = Path.GetDirectoryName(StorePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(StorePath, JsonSerializer.Serialize(paths));
        }
        catch (Exception)
        {
            // Failing to remember is not worth interrupting an open.
        }
    }
}
