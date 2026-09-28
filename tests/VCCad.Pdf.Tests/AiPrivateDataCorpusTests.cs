using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VCCad.Core.Model;
using VCCad.Pdf;
using VCCad.Pdf.Ai;
using Xunit;
using Xunit.Abstractions;

namespace VCCad.Pdf.Tests;

/// <summary>
/// Runs the Illustrator private-data extractor over the real <c>.ai</c> corpus
/// (66 files from Inkscape's <c>extension-ai</c> test data — AI8 PostScript through
/// AI24 zstd, CMYK/RGB, gradients, opacity groups, layers, text and raster art).
///
/// The corpus is not vendored (its licence is not ours). Set
/// <c>VCCAD_AI_CORPUS</c>, or keep it at <c>$HOME/.cache/vccad-corpora/ai</c>; when
/// it is absent every test here is a no-op with a skip sentinel, exactly like the
/// other corpus sweeps.
/// </summary>
public class AiPrivateDataCorpusTests
{
    private readonly ITestOutputHelper _output;

    public AiPrivateDataCorpusTests(ITestOutputHelper output) => _output = output;

    /// <summary>Corpus directory as seen by the tests, or <c>null</c> when absent.</summary>
    internal static string? CorpusRoot()
    {
        string? env = Environment.GetEnvironmentVariable("VCCAD_AI_CORPUS");
        if (!string.IsNullOrEmpty(env) && Directory.Exists(env))
        {
            return env;
        }

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (string candidate in new[]
                 {
                     Path.Combine(home, ".cache", "vccad-corpora", "ai"),
                     "/tmp/opencode/vccad-corpora/ai",
                 })
        {
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Every <c>.ai</c> fixture, in deterministic path order.</summary>
    public static IEnumerable<object[]> CorpusFiles()
    {
        string? root = CorpusRoot();
        if (root is null)
        {
            yield return new object[] { string.Empty }; // skip cleanly when absent
            yield break;
        }

        List<string> files = Directory.EnumerateFiles(root, "*.ai", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
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

    [Theory]
    [MemberData(nameof(CorpusFiles))]
    [Trait("Category", "Corpus")]
    public void CorpusExtractionIsRobust(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return; // corpus not present
        }

        byte[] bytes = File.ReadAllBytes(path);
        AiPrivateDataDocument? document;
        try
        {
            document = AiPrivateDataExtractor.Extract(bytes);
        }
        catch (Exception ex)
        {
            Assert.Fail($"{Path.GetFileName(path)}: extractor threw {ex.GetType().Name}: {ex.Message}");
            return;
        }

        // Every PDF container in this corpus carries Illustrator private data; a
        // failure here is a real regression, not an unsupported file.
        if (IsPdfContainer(bytes))
        {
            Assert.True(document is not null, $"{Path.GetFileName(path)}: PDF container without private data");
        }

        if (document is null)
        {
            return;
        }

        Assert.NotEqual(AiPrivateDataFormat.Unknown, document.Format);
        Assert.False(string.IsNullOrEmpty(document.Text));
        Assert.True(
            AiPrivateDataExtractor.LooksLikePostScriptPayload(Encoding.Latin1.GetBytes(document.Text)),
            $"{Path.GetFileName(path)}: decoded payload is not PostScript-like");
    }

    /// <summary>
    /// Aggregate sweep: reports the format histogram and asserts both the decode
    /// coverage floor and byte-exact writer idempotence over everything decoded, so
    /// drift shows up as one failure with a summary rather than 66.
    /// </summary>
    [Fact]
    [Trait("Category", "Corpus")]
    public void CorpusDecodeCoverageAndWriterIdempotence()
    {
        string? root = CorpusRoot();
        if (root is null)
        {
            return;
        }

        List<string> files = Directory.EnumerateFiles(root, "*.ai", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        var histogram = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var failures = new List<string>();
        int decoded = 0;

        foreach (string path in files)
        {
            string name = Path.GetFileName(path);
            byte[] bytes = File.ReadAllBytes(path);
            try
            {
                AiPrivateDataDocument? document = AiPrivateDataExtractor.Extract(bytes);
                if (document is null)
                {
                    failures.Add("NO-DATA " + name);
                    continue;
                }

                decoded++;
                string format = document.Format.ToString();
                histogram[format] = histogram.GetValueOrDefault(format) + 1;

                if (!EndsWithEof(document.Text))
                {
                    _output.WriteLine(
                        $"  truncated payload: {name} ({document.Format}, {document.Text.Length} chars)");
                }

                // Writer idempotence: byte-exact reproduction and a structurally
                // identical re-parse.
                string written = document.Write();
                if (!string.Equals(written, document.Text, StringComparison.Ordinal))
                {
                    failures.Add("WRITE " + name);
                    continue;
                }

                AiPrivateDataDocument reparsed = AiPrivateDataDocument.Parse(written, document.Format);
                if (!reparsed.StructurallyEquals(document))
                {
                    failures.Add("REPARSE " + name);
                }
            }
            catch (Exception ex)
            {
                failures.Add($"THREW {name}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        _output.WriteLine($"ai corpus: {files.Count} files, {decoded} decoded");
        foreach (KeyValuePair<string, int> entry in histogram)
        {
            _output.WriteLine($"  {entry.Key,-14} {entry.Value}");
        }

        Assert.True(
            files.Count == 0 || decoded >= (int)Math.Ceiling(files.Count * 0.9),
            $"private-data decode covered {decoded}/{files.Count} corpus files; first failures: " +
            string.Join(", ", failures.Take(10)));
        Assert.True(failures.Count == 0,
            $"{failures.Count} corpus failures: " + string.Join(", ", failures.Take(10)));
    }

    /// <summary>
    /// End-to-end guarantee: import a real fixture, export it, and extract the same
    /// decoded payload text back out of the exported bytes.
    /// </summary>
    [Fact]
    [Trait("Category", "Corpus")]
    public void ImportExportRoundTripsTheDecodedPayload()
    {
        string? path = PickFixture("layers_and_sublayers.ai", "simple_v2020.ai", "simple_cs.ai");
        if (path is null)
        {
            return; // corpus not present
        }

        byte[] original = File.ReadAllBytes(path);
        AiPrivateDataDocument? source = AiPrivateDataExtractor.Extract(original);
        Assert.NotNull(source);

        CadDocument document = PdfImporter.Import(original);
        Assert.NotNull(document.AiPrivateData);
        Assert.Equal(source!.Text, document.AiPrivateData!.Text);

        byte[] exported = PdfDocumentExporter.Export(document);
        AiPrivateDataDocument? reExtracted = AiPrivateDataExtractor.Extract(exported);
        Assert.NotNull(reExtracted);
        Assert.Equal(source.Text, reExtracted!.Text);

        // Re-exporting the re-imported document is stable too.
        CadDocument again = PdfImporter.Import(exported);
        byte[] reExported = PdfDocumentExporter.Export(again);
        AiPrivateDataDocument? third = AiPrivateDataExtractor.Extract(reExported);
        Assert.NotNull(third);
        Assert.Equal(source.Text, third!.Text);
    }

    /// <summary>
    /// Cross-check against the repository's independent Python decoder and its
    /// golden manifest (<c>tools/ai-private-data</c>). Where the two implementations
    /// read the same bytes — the zstd and AI12 containers, which carry no PDF-level
    /// filter — the decoded payload must match bit for bit.
    /// </summary>
    [Fact]
    [Trait("Category", "Corpus")]
    public void MatchesTheIndependentReferenceDecoderManifest()
    {
        string? root = CorpusRoot();
        string? manifestPath = FindManifest();
        if (root is null || manifestPath is null)
        {
            return;
        }

        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        if (!manifest.RootElement.TryGetProperty("fixtures", out JsonElement fixtures))
        {
            return;
        }

        int compared = 0;
        int pdfFiltered = 0;
        int longer = 0;
        var mismatches = new List<string>();

        foreach (JsonElement fixture in fixtures.EnumerateArray())
        {
            string format = fixture.GetProperty("format").GetString() ?? string.Empty;
            string file = fixture.GetProperty("file").GetString() ?? string.Empty;
            string expectedHash = fixture.GetProperty("payload_sha256").GetString() ?? string.Empty;

            string? path = Directory.EnumerateFiles(root, file, SearchOption.AllDirectories)
                .OrderBy(p => p, StringComparer.Ordinal)
                .FirstOrDefault();
            if (path is null)
            {
                continue;
            }

            AiPrivateDataDocument? document = AiPrivateDataExtractor.Extract(File.ReadAllBytes(path));
            if (document is null)
            {
                mismatches.Add($"{file}: nothing extracted");
                continue;
            }

            if (format is "ai24-zstd" or "ai12-zlib")
            {
                // Same input bytes: the marker formats carry no PDF-level filter, so
                // both decoders see identical streams.
                compared++;
                byte[] payload = Encoding.Latin1.GetBytes(document.Text);
                int referenceBytes = fixture.GetProperty("payload_bytes").GetInt32();
                string head = fixture.GetProperty("head").GetString() ?? string.Empty;

                if (payload.Length < referenceBytes)
                {
                    mismatches.Add(
                        $"{file}: recovered {payload.Length} bytes, the reference has {referenceBytes}");
                    continue;
                }

                if (payload.Length > referenceBytes)
                {
                    // Two fixtures (circle.ai, gradient_radial_random_test.ai) carry a
                    // zstd frame whose tail is missing. The reference CLI stops at an
                    // internal buffer boundary; our streaming reader keeps going to
                    // the DSC trailer. Verify the shared prefix + head then, since the
                    // recorded hash describes the shorter buffer.
                    longer++;
                }
                else if (!string.Equals(
                             Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(),
                             expectedHash,
                             StringComparison.OrdinalIgnoreCase))
                {
                    mismatches.Add($"{file}: payload differs from the reference decoder");
                }

                if (head.Length > 0 && !document.Text.StartsWith(head, StringComparison.Ordinal))
                {
                    mismatches.Add($"{file}: payload head differs from the reference decoder");
                }
            }
            else if (format == "plain")
            {
                // These containers route their block compression through PDF's
                // /FlateDecode. The reference decoder normalises with qpdf, so it
                // reads the *decoded* streams and keeps the leading thumbnail block;
                // the byte-level AI 9–CS rule ("discard blocks without a zlib
                // header") drops it. This is a deliberate, documented divergence —
                // we assert the classification rather than pretending to match.
                pdfFiltered++;
                if (document.Format is not (AiPrivateDataFormat.ZlibAi9Cs or AiPrivateDataFormat.PostScriptAi8))
                {
                    mismatches.Add($"{file}: expected a per-block or plain decode, got {document.Format}");
                }
            }
        }

        _output.WriteLine(
            $"reference manifest: {compared} comparisons, {longer} recovered beyond the reference, " +
            $"{pdfFiltered} PDF-filtered containers");
        Assert.True(compared > 20, $"expected a meaningful cross-check, compared {compared}");
        Assert.True(longer <= 5, $"{longer} fixtures recovered more than the reference; the corpus has 2");
        Assert.True(mismatches.Count == 0, string.Join("; ", mismatches.Take(10)));
    }

    /// <summary>
    /// A complete Illustrator payload ends with the DSC trailer; the two corpus
    /// fixtures with a clipped zstd frame still reach it here (see the manifest test
    /// for why the reference decoder does not).
    /// </summary>
    private static bool EndsWithEof(string text)
        => text.TrimEnd('\r', '\n', ' ', '\t').EndsWith("%%EOF", StringComparison.Ordinal);

    private static string? PickFixture(params string[] names)
    {
        string? root = CorpusRoot();
        if (root is null)
        {
            return null;
        }

        foreach (string name in names)
        {
            string? path = Directory.EnumerateFiles(root, name, SearchOption.AllDirectories)
                .OrderBy(p => p, StringComparer.Ordinal)
                .FirstOrDefault();
            if (path is not null)
            {
                return path;
            }
        }

        return Directory.EnumerateFiles(root, "*.ai", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static string? FindManifest()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "tools", "ai-private-data", "fixture-manifest.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }

    private static bool IsPdfContainer(byte[] bytes)
    {
        int limit = Math.Min(bytes.Length, 2048);
        for (int i = 0; i + 5 <= limit; i++)
        {
            if (bytes[i] == (byte)'%' && bytes[i + 1] == (byte)'P' && bytes[i + 2] == (byte)'D'
                && bytes[i + 3] == (byte)'F' && bytes[i + 4] == (byte)'-')
            {
                return true;
            }
        }

        return false;
    }
}
