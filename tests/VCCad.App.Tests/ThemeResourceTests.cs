using System.Text.RegularExpressions;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// Every theme resource the interface asks for is one that exists.
///
/// This is not hypothetical. The Layers panel's thumbnail was bound to
/// <c>{DynamicResource VcForeground}</c>, and the theme defines <c>VcText</c> and
/// <c>VcTextDim</c> but no <c>VcForeground</c>. A missing DynamicResource resolves to null
/// rather than failing, so the Path drew nothing, the row showed an empty box, and no test,
/// build or log said a word. It was found by comparing two screenshots byte for byte after a
/// change that ought to have made a visible difference and made none.
///
/// Reading the XAML is enough to catch it: the keys are all declared in one dictionary.
/// </summary>
public class ThemeResourceTests
{
    /// <summary>The repository root, found by walking up to the solution file.</summary>
    private static string? Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "VCCad.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }

    /// <summary>Keys declared with x:Key in the app's own resource dictionaries.</summary>
    private static HashSet<string> DeclaredKeys(string appDir)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);

        foreach (string file in Directory.EnumerateFiles(appDir, "*.axaml", SearchOption.AllDirectories))
        {
            string text = File.ReadAllText(file);
            foreach (Match m in Regex.Matches(text, @"x:Key=""([^""]+)"""))
            {
                keys.Add(m.Groups[1].Value);
            }
        }

        return keys;
    }

    /// <summary>The keys the interface asks for, and the file that asks.</summary>
    private static List<(string Key, string File)> ReferencedKeys(string appDir)
    {
        var wanted = new List<(string, string)>();

        foreach (string file in Directory.EnumerateFiles(appDir, "*.axaml", SearchOption.AllDirectories))
        {
            string text = File.ReadAllText(file);

            foreach (Match m in Regex.Matches(
                         text, @"\{DynamicResource\s+([A-Za-z_][\w.]*)\}"))
            {
                wanted.Add((m.Groups[1].Value, Path.GetFileName(file)));
            }
        }

        return wanted;
    }

    [Fact]
    public void EveryDynamicResourceTheInterfaceAsksForIsDeclared()
    {
        string? root = Root();
        if (root is null)
        {
            return;
        }

        string appDir = Path.Combine(root, "src", "VCCad.App");
        if (!Directory.Exists(appDir))
        {
            return;
        }

        HashSet<string> declared = DeclaredKeys(appDir);
        Assert.NotEmpty(declared);

        var missing = ReferencedKeys(appDir)
            .Where(r => !declared.Contains(r.Key))
            .Select(r => $"{r.Key} (asked for in {r.File})")
            .Distinct()
            .OrderBy(s => s)
            .ToList();

        Assert.True(missing.Count == 0,
            "these resources resolve to null and draw nothing:\n  " + string.Join("\n  ", missing));
    }

    [Fact]
    public void TheCheckerWouldNoticeAMissingResource()
    {
        // A guard on the guard. If the patterns stopped matching, the test above would pass
        // by finding nothing to complain about, which is the failure mode it exists to stop.
        string? root = Root();
        if (root is null)
        {
            return;
        }

        string appDir = Path.Combine(root, "src", "VCCad.App");
        if (!Directory.Exists(appDir))
        {
            return;
        }

        List<(string Key, string File)> referenced = ReferencedKeys(appDir);

        Assert.True(referenced.Count > 5,
            $"only {referenced.Count} resource references were found; the scan is not reading the XAML");

        Assert.Contains(referenced, r => r.Key is "VcBackground" or "VcBorder" or "VcText");

        // And a key nobody declares is recognised as missing rather than ignored.
        HashSet<string> declared = DeclaredKeys(appDir);
        Assert.DoesNotContain("VcForeground", declared);
    }
}
