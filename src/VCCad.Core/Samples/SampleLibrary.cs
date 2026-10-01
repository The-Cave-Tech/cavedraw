namespace VCCad.Core.Samples;

/// <summary>
/// Where the copyrighted sample PDFs are checked out.
///
/// They are third-party commercial sewing patterns, used as the strongest real-world import fixtures the
/// project has. They are **not** in this repository: it is public, and redistributing them was not permitted.
/// They live in a private one, checked out beside this one.
///
/// Everything that needs them asks here, so there is one answer to "where are the samples" rather than three
/// that can disagree. And nothing that needs them is *required* to find them - absent samples mean the tests
/// that use them skip, not that the suite fails.
/// </summary>
public static class SampleLibrary
{
    /// <summary>
    /// The environment variable that names the directory, when it is somewhere else again.
    ///
    /// A build machine that checks the private repository out anywhere can point at it directly rather than
    /// having to match the layout.
    /// </summary>
    public const string EnvironmentVariable = "VCCAD_SAMPLES";

    /// <summary>The directory holding the samples, or null when this checkout does not have them.</summary>
    public static string? Location
    {
        get
        {
            string? configured = Environment.GetEnvironmentVariable(EnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(configured) && Exists(configured))
            {
                return configured;
            }

            // Walking up from the binary finds them inside the repository for a checkout that still has them,
            // and beside it - `…/vccad/samples` next to `…/vccad/main` - for the private checkout, which is the
            // layout the README describes. One rule covers both.
            for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, "samples");
                if (Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }
    }

    /// <summary>Whether the samples are available at all, for a test that wants to say so.</summary>
    public static bool Available => Location is not null;

    /// <summary>A sample by exact file name, or null when it - or the whole directory - is absent.</summary>
    public static string? Find(string fileName)
    {
        string? directory = Location;
        if (directory is null)
        {
            return null;
        }

        string candidate = Path.Combine(directory, fileName);
        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>
    /// A sample by the loose name a person would use - "the sample A0 Temi Bow …pdf" - which is how the
    /// automation endpoint lets a caller name a file without an absolute path. Exact match first, then a
    /// containment match, both on letters and digits only.
    /// </summary>
    public static string? FindLoose(string name)
    {
        string? directory = Location;
        if (directory is null)
        {
            return null;
        }

        string wanted = Normalize(Path.GetFileNameWithoutExtension(name));
        if (wanted.Length == 0)
        {
            return null;
        }

        try
        {
            var files = Directory.EnumerateFiles(directory, "*.pdf").ToList();
            return files.FirstOrDefault(f => Normalize(Path.GetFileNameWithoutExtension(f)) == wanted)
                   ?? files.FirstOrDefault(f => Normalize(Path.GetFileNameWithoutExtension(f))
                       .Contains(wanted, StringComparison.Ordinal));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Lower-cases and strips separators so "A0 Temi Bow.pdf" matches the file name.
    ///
    /// Also what makes this safe to call from the browser build, where the filesystem may not answer at all:
    /// every probe is defensive and a failure means "not here", never an exception in the middle of rendering.
    /// </summary>
    private static string Normalize(string value)
        => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static bool Exists(string directory)
    {
        try
        {
            return Directory.Exists(directory);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
