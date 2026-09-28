using System.Collections.Concurrent;

namespace VCCad.Pdf.Tests.Corpus;

/// <summary>
/// Shared access to the Ghostscript/MuPDF public test corpus
/// (<see href="https://github.com/ArtifexSoftware/tests"/>, AGPL-3.0). The corpus
/// is never vendored into this repository: point <c>VCCAD_GS_CORPUS</c> at a
/// checkout, or keep one at a default probe path. When none exists every corpus
/// theory yields the empty-string sentinel and the test body returns early, so
/// the suite stays green without the corpus (xUnit treats a theory with no data
/// as an error).
///
/// Deterministic file ordering and a process-wide probe cache are provided here
/// so several test classes can share one sweep; the AI-corpus agent can use this
/// helper too, but nothing here depends on the AI agent's files.
/// </summary>
public static class PdfCorpus
{
    /// <summary>Environment variable naming the corpus checkout.</summary>
    public const string EnvVar = "VCCAD_GS_CORPUS";

    /// <summary>Default locations probed when <see cref="EnvVar"/> is unset.</summary>
    public static readonly IReadOnlyList<string> DefaultRoots = new[]
    {
        "/home/darren/.cache/vccad-corpora/ghostscript",
        "/tmp/opencode/gs-tests",
        "/home/darren/development/Artifex-tests",
    };

    /// <summary>The Ghent PDF Output Suite directories (valid, well-formed files).</summary>
    public static readonly IReadOnlyList<string> GhentDirectories = new[] { "Ghent_V3.0", "Ghent_V5.0" };

    private static readonly object Gate = new();
    private static IReadOnlyList<string>? _files;
    private static string? _resolvedRoot;
    private static bool _rootResolved;

    private static readonly ConcurrentDictionary<string, PdfFeatureReport> ProbeCache =
        new(StringComparer.Ordinal);

    /// <summary>
    /// The corpus root, honouring <c>VCCAD_GS_CORPUS</c> first and then the
    /// default probe paths. <c>null</c> when no corpus is available.
    /// </summary>
    internal static string? CorpusRoot()
    {
        if (_rootResolved)
        {
            return _resolvedRoot;
        }

        lock (Gate)
        {
            if (_rootResolved)
            {
                return _resolvedRoot;
            }

            string? env = Environment.GetEnvironmentVariable(EnvVar);
            if (!string.IsNullOrEmpty(env) && Directory.Exists(env))
            {
                _resolvedRoot = env;
            }
            else
            {
                foreach (string candidate in DefaultRoots)
                {
                    if (Directory.Exists(candidate))
                    {
                        _resolvedRoot = candidate;
                        break;
                    }
                }
            }

            _rootResolved = true;
            return _resolvedRoot;
        }
    }

    /// <summary>Every corpus PDF, ordered ordinally for reproducible failures.</summary>
    public static IReadOnlyList<string> PdfFiles()
    {
        if (_files is not null)
        {
            return _files;
        }

        lock (Gate)
        {
            if (_files is not null)
            {
                return _files;
            }

            string? root = CorpusRoot();
            if (root is null)
            {
                return _files = Array.Empty<string>();
            }

            try
            {
                _files = Directory.EnumerateFiles(root, "*.pdf", SearchOption.AllDirectories)
                    .OrderBy(path => path, StringComparer.Ordinal)
                    .ToArray();
            }
            catch (IOException)
            {
                _files = Array.Empty<string>();
            }

            return _files;
        }
    }

    /// <summary>Whether a usable corpus is present.</summary>
    public static bool Available => PdfFiles().Count > 0;

    /// <summary>
    /// Theory data for the corpus: one row per PDF, or a single empty-string
    /// sentinel when the corpus is absent so the theory never yields no data.
    /// </summary>
    public static IEnumerable<object[]> PdfTheoryData()
    {
        IReadOnlyList<string> files = PdfFiles();
        if (files.Count == 0)
        {
            yield return new object[] { string.Empty };
            yield break;
        }

        foreach (string path in files)
        {
            yield return new object[] { path };
        }
    }

    /// <summary>Corpus-relative path, used in reports so output is machine-stable.</summary>
    public static string RelativePath(string path)
    {
        string? root = CorpusRoot();
        if (root is null)
        {
            return path;
        }

        string prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.Ordinal) ? path[prefix.Length..] : path;
    }

    /// <summary>Probes one corpus file, memoised across the whole test run.</summary>
    public static PdfFeatureReport Probe(string path)
        => ProbeCache.GetOrAdd(path, static p => PdfFeatureProbe.Probe(File.ReadAllBytes(p), RelativePath(p)));

    /// <summary>Every corpus file paired with its cached feature report.</summary>
    public static IEnumerable<(string Path, PdfFeatureReport Report)> Probed()
    {
        foreach (string path in PdfFiles())
        {
            PdfFeatureReport report;
            try
            {
                report = Probe(path);
            }
            catch (IOException)
            {
                continue;
            }

            yield return (path, report);
        }
    }

    /// <summary>Corpus-relative paths of the files that exercise <paramref name="feature"/>.</summary>
    public static IReadOnlyList<string> FilesWith(string feature)
        => Probed().Where(entry => entry.Report.Has(feature))
            .Select(entry => RelativePath(entry.Path))
            .ToArray();

    /// <summary>The first corpus file (ordinal order) that exercises <paramref name="feature"/>.</summary>
    public static string? FirstFileWith(string feature)
        => Probed().FirstOrDefault(entry => entry.Report.Has(feature)).Path;

    /// <summary>The first corpus file (ordinal order) whose report satisfies <paramref name="predicate"/>.</summary>
    public static string? FirstFileWhere(Func<PdfFeatureReport, bool> predicate)
        => Probed().FirstOrDefault(entry => predicate(entry.Report)).Path;

    /// <summary>Corpus-relative paths under any of <paramref name="directories"/>.</summary>
    public static IReadOnlyList<string> FilesUnder(params string[] directories)
    {
        string? root = CorpusRoot();
        if (root is null)
        {
            return Array.Empty<string>();
        }

        return PdfFiles()
            .Where(path =>
            {
                string relative = RelativePath(path);
                return directories.Any(dir =>
                    relative.StartsWith(dir + "/", StringComparison.Ordinal));
            })
            .ToArray();
    }
}
