using System.Text.RegularExpressions;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// Fails if a secret is committed, so the next person who pastes one in finds out from CI rather than from a
/// notification that their key was used.
///
/// **This test looks for shapes, not for values.** A guard that names the credential it guards is a second copy
/// of the credential - which is exactly what happened while this was being fixed: the issue describing the
/// leak quoted the key, then quoted a search command containing it. So there is no hostname and no key in this
/// file: a private-network address is recognised as an address, and a key is recognised as the shape of one.
///
/// The consequence, stated plainly rather than discovered later: a *hostname* leak would not be caught here,
/// because catching it would mean writing it down. That one stays a matter for review.
/// </summary>
public class NoSecretsInTheTreeTests
{
    private static readonly string[] Roots = { "src", "tests", "tools", "docs", "scripts", "docker", ".github" };

    /// <summary>An API token: the conventional prefix, then enough characters to be a token rather than a word.</summary>
    private static readonly Regex KeyShape = new(@"\bsk-[A-Za-z0-9_\-]{8,}", RegexOptions.Compiled);

    /// <summary>A private-network IPv4 address, which is an internal host by definition.</summary>
    private static readonly Regex PrivateAddress = new(
        @"\b(10\.\d{1,3}\.\d{1,3}\.\d{1,3}|192\.168\.\d{1,3}\.\d{1,3}|172\.(1[6-9]|2\d|3[01])\.\d{1,3}\.\d{1,3})\b",
        RegexOptions.Compiled);

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src")) &&
                File.Exists(Path.Combine(dir.FullName, "README.md")))
            {
                return dir.FullName;
            }
        }

        throw new Xunit.Sdk.XunitException("Could not find the repository root from " + AppContext.BaseDirectory);
    }

    private static IEnumerable<string> FilesToScan(string root)
    {
        foreach (string folder in Roots)
        {
            string path = Path.Combine(root, folder);
            if (!Directory.Exists(path))
            {
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                string normalised = file.Replace('\\', '/');

                // Build output and anything gitignored: the point is what would be committed.
                if (normalised.Contains("/bin/") || normalised.Contains("/obj/") ||
                    normalised.Contains("/node_modules/") || normalised.Contains("/artifacts/"))
                {
                    continue;
                }

                string extension = Path.GetExtension(file);
                if (extension is ".cs" or ".py" or ".md" or ".json" or ".yml" or ".yaml" or ".sh" or ".ps1" or ".axaml" or ".csproj")
                {
                    yield return file;
                }
            }
        }

        foreach (string file in Directory.EnumerateFiles(root, "*.md", SearchOption.TopDirectoryOnly))
        {
            yield return file;
        }

        foreach (string file in Directory.EnumerateFiles(root, "*.json", SearchOption.TopDirectoryOnly))
        {
            yield return file;
        }
    }

    [Fact]
    public void NoApiKeyShapeIsCommitted()
    {
        var offenders = new List<string>();

        foreach (string file in FilesToScan(RepositoryRoot()))
        {
            if (KeyShape.IsMatch(File.ReadAllText(file)))
            {
                offenders.Add(Path.GetRelativePath(RepositoryRoot(), file));
            }
        }

        // Naming the files and not the strings: the failure message goes into a CI log, which is as public as
        // the repository it belongs to.
        Assert.True(offenders.Count == 0,
            "an API key looks like it is committed in: " + string.Join(", ", offenders));
    }

    [Fact]
    public void NoPrivateNetworkAddressIsCommitted()
    {
        var offenders = new List<string>();

        foreach (string file in FilesToScan(RepositoryRoot()))
        {
            if (PrivateAddress.IsMatch(File.ReadAllText(file)))
            {
                offenders.Add(Path.GetRelativePath(RepositoryRoot(), file));
            }
        }

        Assert.True(offenders.Count == 0,
            "a private-network address is committed in: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// The settings file itself is never committed - only the example is. This is the rule the whole issue
    /// turns on, so it is asserted rather than trusted.
    /// </summary>
    [Fact]
    public void TheSettingsFileIsIgnoredAndOnlyTheExampleIsCommitted()
    {
        string root = RepositoryRoot();

        Assert.True(File.Exists(Path.Combine(root, "settings.example.json")),
            "the example is what tells a person where to put their key");

        string ignore = File.ReadAllText(Path.Combine(root, ".gitignore"));
        Assert.Contains("settings.json", ignore, StringComparison.Ordinal);

        Assert.False(File.Exists(Path.Combine(root, "settings.json")),
            "settings.json must not be committed - it holds the key");
    }
}
