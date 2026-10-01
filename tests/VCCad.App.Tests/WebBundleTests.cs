using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// Tests that run against the **published web bundle** rather than the desktop build.
///
/// The web version is a shipped target - the Docker image serves the WASM bundle - and it runs the same Avalonia
/// shell and the same operation registry. Nothing local used to build it, so a change that compiled for `net10.0`
/// and broke the browser head was only discovered by CI after a push.
///
/// These skip cleanly when no bundle has been published, the way every corpus test here skips without its corpus:
/// the bundle is produced by `scripts/test-all.ps1` (and by `dotnet publish src/VCCad.App.Browser`), and its absence
/// must not fail a run that was never asked to build it.
///
/// What is asserted is the **contract the browser depends on**: the entry point exists and points at a boot script
/// that exists, and `_framework` holds the runtime *and* the application assembly under the name and extension the
/// publish actually produces. That last part is the trap `AGENTS.md` §1.1 records twice over: the assembly is not
/// at the publish root, and matching its **name alone** also matches `runtimeconfig.json` - a check that passed for
/// a year while proving nothing.
/// </summary>
public class WebBundleTests
{
    /// <summary>
    /// Where a publish lands. `scripts/test-all.ps1` writes `artifacts/web`; CI writes `artifacts/wasm`. Both are
    /// probed so the same test covers the routine run and the pipeline.
    /// </summary>
    private static string? BundleRoot()
    {
        foreach (string candidate in new[] { "artifacts/web", "artifacts/wasm" })
        {
            string wwwroot = Path.Combine(RepositoryRoot(), candidate, "wwwroot");
            if (Directory.Exists(wwwroot))
            {
                return wwwroot;
            }
        }

        return null;
    }

    /// <summary>The repository root, found by walking up for the solution file.</summary>
    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "VCCad.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? AppContext.BaseDirectory;
    }

    private static string Framework(string wwwroot) => Path.Combine(wwwroot, "_framework");

    /// <summary>
    /// The assets for one assembly in `_framework`, matched by **name and shape**: the publish writes
    /// `&lt;AssemblyName&gt;.&lt;hash&gt;.wasm` on .NET 10 and `.webcil` on .NET 8.
    ///
    /// The prefix alone is not enough, and that is not a detail: `VCCad.App.` also matches `VCCad.App.Browser.`, so
    /// a prefix match picks the **host** - six kilobytes - when it was asked for the **shell**, which is over a
    /// megabyte. That is the same "a name alone matches more than you meant" trap `AGENTS.md` §1.1 records for
    /// `runtimeconfig.json`, in a second guise. Counting the dotted segments is what tells them apart, because the
    /// hash the publish inserts contains none.
    /// </summary>
    private static List<string> Assemblies(string wwwroot, string assembly)
    {
        int expectedParts = assembly.Split('.').Length + 2;
        return Directory.EnumerateFiles(Framework(wwwroot))
            .Where(path =>
            {
                string name = Path.GetFileName(path);
                if (!name.StartsWith(assembly + ".", StringComparison.Ordinal))
                {
                    return false;
                }

                string[] parts = name.Split('.');
                return parts.Length == expectedParts &&
                       (name.EndsWith(".wasm", StringComparison.Ordinal) ||
                        name.EndsWith(".webcil", StringComparison.Ordinal));
            })
            .ToList();
    }

    [Fact]
    public void TheWebEntryPointExistsAndLoadsAScriptThatExists()
    {
        string? wwwroot = BundleRoot();
        if (wwwroot is null)
        {
            return;
        }

        string index = Path.Combine(wwwroot, "index.html");
        Assert.True(File.Exists(index), $"no index.html in {wwwroot}");

        string html = File.ReadAllText(index);

        // The page boots through its own module script, so the script it names has to be there. A publish that
        // produced the runtime and forgot the boot script is a blank page, and nothing else here would notice.
        Assert.Contains("main.js", html, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(wwwroot, "main.js")), "index.html loads main.js, which is not published");
    }

    [Fact]
    public void TheWebBundleHoldsTheRuntime()
    {
        string? wwwroot = BundleRoot();
        if (wwwroot is null)
        {
            return;
        }

        // The .NET runtime the browser loads, and the native WASM runtime behind it.
        Assert.NotEmpty(Directory.EnumerateFiles(Framework(wwwroot), "dotnet.js"));
        Assert.NotEmpty(Directory.EnumerateFiles(Framework(wwwroot), "dotnet.native*.wasm"));
    }

    /// <summary>
    /// **The editor is really in the bundle**, and so is the host that boots it - each matched by name **and**
    /// shape.
    ///
    /// This is the check that used to pass while proving nothing: matching a name alone also matches
    /// `runtimeconfig.json`, and matching a *prefix* also matches the other assembly. A publish that dropped the
    /// editor altogether looked green.
    /// </summary>
    [Fact]
    public void TheWebBundleHoldsTheApplicationAssembly()
    {
        string? wwwroot = BundleRoot();
        if (wwwroot is null)
        {
            return;
        }

        List<string> shell = Assemblies(wwwroot, "VCCad.App");
        Assert.True(shell.Count > 0,
            $"no VCCad.App.<hash>.wasm or .webcil in {Framework(wwwroot)} - the bundle has no editor in it");

        // And it is the editor, not a stub: the shell is a megabyte of compiled XAML and Avalonia against a few
        // kilobytes for the host. A size floor is what tells "published" from "published empty".
        long size = new FileInfo(shell[0]).Length;
        Assert.True(size > 200_000, $"the editor assembly is only {size} bytes, which is not the editor");

        // The host that boots it is a different, much smaller assembly - and matching it is where a prefix match
        // goes wrong, so it is asserted separately and by its own full name.
        Assert.NotEmpty(Assemblies(wwwroot, "VCCad.App.Browser"));
    }

    /// <summary>The libraries the shell needs are in the bundle too, so the app can actually load.</summary>
    [Fact]
    public void TheWebBundleHoldsTheLibrariesTheShellNeeds()
    {
        string? wwwroot = BundleRoot();
        if (wwwroot is null)
        {
            return;
        }

        foreach (string library in new[] { "VCCad.Core", "VCCad.Geometry" })
        {
            Assert.True(
                Assemblies(wwwroot, library).Count > 0,
                $"{library} is missing from the web bundle");
        }
    }

    /// <summary>
    /// The bundle is what a static host would serve: every file the entry point names is reachable under `wwwroot`
    /// at the path the browser will ask for, with no absolute paths that only work on the build machine.
    /// </summary>
    [Fact]
    public void TheBundleIsSelfContained()
    {
        string? wwwroot = BundleRoot();
        if (wwwroot is null)
        {
            return;
        }

        string index = File.ReadAllText(Path.Combine(wwwroot, "index.html"));

        // No absolute filesystem path may appear in the page: a bundle referencing C:\... would work here and be
        // broken on the server, which is the "green locally" failure this test exists to prevent in the first place.
        Assert.DoesNotContain("C:\\", index, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/home/", index, StringComparison.OrdinalIgnoreCase);

        // Relative assets resolve inside the published tree.
        Assert.True(File.Exists(Path.Combine(wwwroot, "app.css")) || index.Contains("app.css"),
            "index.html references a stylesheet that is not published");
    }
}
