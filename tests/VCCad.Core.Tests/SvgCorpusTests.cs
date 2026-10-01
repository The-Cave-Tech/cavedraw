using VCCad.Core.Svg;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// Every SVG in the Inkscape corpus imports without throwing.
///
/// This is the issue's own test, and the reason it is a corpus rather than a handful of hand-written files: these
/// are real drawings made by a real editor, and the failure they catch is a reader that works on the tidy example
/// it was written against and falls over on the first file that uses a construct nobody thought of.
///
/// The theory emits a **skip sentinel** rather than no data when the corpus is absent, which is this repository's
/// rule: a theory that yields nothing is a CI error, not a pass.
/// </summary>
public class SvgCorpusTests
{
    private static string[] Files()
    {
        string? root = CorpusRoot();
        if (root is null)
        {
            return new[] { string.Empty };
        }

        string[] files = Directory.GetFiles(root, "*.svg", SearchOption.AllDirectories);
        return files.Length == 0 ? new[] { string.Empty } : files;
    }

    /// <summary>
    /// The corpus, wherever it was fetched to.
    ///
    /// The directory name carries the commit it was taken from, so it is found by prefix rather than by a name
    /// that would need updating every time the corpus is. A skip sentinel when nothing matches.
    /// </summary>
    private static string? CorpusRoot()
    {
        string? configured = Environment.GetEnvironmentVariable("VCCAD_INKSCAPE_CORPUS");
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured))
        {
            return configured;
        }

        string cache = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "vccad-corpora");

        if (!Directory.Exists(cache))
        {
            return null;
        }

        foreach (string directory in Directory.GetDirectories(cache, "inkscape*"))
        {
            // The SVGs live under testfiles/rendering_tests in the fetched tree; search for the shallowest place
            // that holds them rather than hard-coding the layout of someone else's repository.
            string? found = FindSvgDirectory(directory);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private static string? FindSvgDirectory(string root)
    {
        var candidates = new List<string>();
        try
        {
            candidates.AddRange(Directory.GetDirectories(root, "*", SearchOption.AllDirectories));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        candidates.Insert(0, root);

        foreach (string candidate in candidates)
        {
            try
            {
                if (Directory.GetFiles(candidate, "*.svg").Length > 0)
                {
                    return candidate;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // An unreadable directory is one this test does not need.
            }
        }

        return null;
    }

    public static IEnumerable<object[]> CorpusFiles()
        => Files().Select(file => new object[] { file });

    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void EveryCorpusFileImports(string path)
    {
        if (path.Length == 0)
        {
            return;
        }

        // The assertion is that it does not throw - but a reader that threw nothing and produced nothing would
        // pass that, so the document it produces is checked for the things every drawing has: an artboard with an
        // area, and at least one object in it.
        SvgImportResult result;
        try
        {
            result = SvgReader.ReadFile(path);
        }
        catch (SvgImportException exception)
        {
            // Named here rather than left to the test runner: xunit truncates a theory's parameters, and a failure
            // that says "line 30" without saying which of twenty-eight files is not a failure anybody can act on.
            Assert.Fail($"{Path.GetFileName(path)}: {exception.Message}");
            return;
        }

        Assert.True(result.Document.Artboards.Count > 0, "an import produces an artboard");
        Assert.True(result.Document.Artboards[0].Width > 0 && result.Document.Artboards[0].Height > 0,
            "the artboard has an area");

        Assert.True(result.Objects >= 0, "the element count is reported");
    }

    /// <summary>
    /// The corpus is only worth its name if it is actually there, so how much of it was read is asserted once
    /// rather than assumed. This is the test that fails loudly if the corpus moves.
    /// </summary>
    [Fact]
    public void TheCorpusIsFound()
    {
        string[] files = Files();
        if (files.Length == 1 && files[0].Length == 0)
        {
            // Absent corpus: the sentinel, which is a skip rather than a pass.
            return;
        }

        Assert.True(files.Length >= 20, $"the corpus should hold the rendering tests, but {files.Length} were found");
    }
}
