using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;
using Xunit.Abstractions;

namespace VCCad.Pdf.Tests;

/// <summary>
/// Runs the real vector importer over the veraPDF corpus
/// (https://github.com/veraPDF/veraPDF-corpus) to measure how much of it we can
/// actually read. The corpus is not vendored — set <c>VCCAD_VERAPDF_CORPUS</c> to
/// its checkout, or keep it at <c>/tmp/opencode/veraPDF-corpus</c>. When neither
/// exists every test here is a no-op (empty data), like the bundled-sample test.
///
/// The corpus files are named "…-pass-…" / "…-fail-…" with the leading numbers
/// identifying the specification clause (PDF/A, PDF/UA, ISO 32000). A "-pass-"
/// file is a valid document, so we expect the vector importer to read it; a
/// "-fail-" file is deliberately malformed, so we only require that we do not
/// throw or produce non-finite geometry.
/// </summary>
public class VeraPdfCorpusTests
{
    private readonly ITestOutputHelper _output;

    public VeraPdfCorpusTests(ITestOutputHelper output) => _output = output;

    internal static string? CorpusRoot()
    {
        string? env = Environment.GetEnvironmentVariable("VCCAD_VERAPDF_CORPUS");
        if (!string.IsNullOrEmpty(env) && Directory.Exists(env))
        {
            return env;
        }

        foreach (string candidate in new[]
                 {
                     "/tmp/opencode/veraPDF-corpus",
                     "/home/darren/development/veraPDF-corpus",
                 })
        {
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    public static IEnumerable<object[]> CorpusFiles()
    {
        string? root = CorpusRoot();
        if (root is null)
        {
            yield break;
        }

        // Deterministic order so failures are reproducible.
        List<string> files = Directory.EnumerateFiles(root, "*.pdf", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal).ToList();
        if (files.Count == 0)
        {
            // A theory with no data is an xunit error; emit a sentinel the test skips.
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
    public void CorpusPdfVectorImportIsRobust(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return; // corpus not present
        }

        byte[] bytes = File.ReadAllBytes(path);
        bool ok;
        CadDocument? doc;
        try
        {
            ok = PdfImporter.TryImportVector(bytes, out doc);
        }
        catch (Exception ex)
        {
            Assert.Fail($"Importer threw on {Path.GetFileName(path)}: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        if (!ok || doc is null)
        {
            // Reading is allowed to fail, but the public API must then still
            // return a usable document rather than throwing.
            CadDocument fallback = PdfImporter.Import(bytes);
            Assert.NotNull(fallback);
            return;
        }

        AssertSaneDocument(doc, Path.GetFileName(path));
    }

    /// <summary>Documents the overall hit-rate over the whole corpus; kept as a
    /// single test so a regression shows as one failure with a summary rather
    /// than thousands of assertion messages.</summary>
    [Fact]
    [Trait("Category", "Corpus")]
    public void CorpusVectorImportCoverageMeetsFloor()
    {
        string? root = CorpusRoot();
        if (root is null)
        {
            return;
        }

        int passTotal = 0, passOk = 0, failTotal = 0, failOk = 0;
        var failures = new List<string>();

        foreach (string path in Directory.EnumerateFiles(root, "*.pdf", SearchOption.AllDirectories))
        {
            string name = Path.GetFileName(path);
            bool isPass = name.Contains("-pass-", StringComparison.OrdinalIgnoreCase);
            bool isFail = name.Contains("-fail-", StringComparison.OrdinalIgnoreCase);
            if (!isPass && !isFail)
            {
                continue;
            }

            bool ok;
            try
            {
                ok = PdfImporter.TryImportVector(File.ReadAllBytes(path), out _);
            }
            catch
            {
                ok = false;
            }

            if (isPass)
            {
                passTotal++;
                if (ok)
                {
                    passOk++;
                }
                else
                {
                    failures.Add("PASS " + name);
                }
            }
            else
            {
                failTotal++;
                if (ok)
                {
                    failOk++;
                }
            }
        }

        _output.WriteLine($"corpus: pass {passOk}/{passTotal}, fail {failOk}/{failTotal}");

        // We do not yet claim full coverage; this only guards against regressions
        // from the current baseline. Raise it as the importer grows.
        Assert.True(passTotal == 0 || passOk > passTotal * 0.5,
            $"vector import covered {passOk}/{passTotal} valid corpus files; first misses: " +
            string.Join(", ", failures.Take(10)));
    }

    private static void AssertSaneDocument(CadDocument doc, string name)
    {
        Assert.NotEmpty(doc.Artboards);
        foreach (Artboard artboard in doc.Artboards)
        {
            Assert.True(double.IsFinite(artboard.Width) && double.IsFinite(artboard.Height),
                $"{name}: non-finite artboard size");
            Assert.True(artboard.Width > 0 && artboard.Height > 0, $"{name}: empty artboard");

            foreach (LayerItem item in artboard.Layers.SelectMany(l => l.Children))
            {
                AssertFinite(item, name);
            }
        }
    }

    private static void AssertFinite(LayerItem item, string name)
    {
        switch (item)
        {
            case PathItem path:
                foreach (SubPath sp in path.SubPaths)
                {
                    foreach (PathNode node in sp.Nodes)
                    {
                        Assert.True(IsFinite(node.Anchor), $"{name}: non-finite anchor");
                        Assert.True(IsFinite(node.InHandle) && IsFinite(node.OutHandle),
                            $"{name}: non-finite handle");
                    }
                }

                break;

            case TextItem text:
                Assert.True(IsFinite(text.Origin), $"{name}: non-finite text origin");
                break;

            case ArtGroup group:
                foreach (LayerItem child in group.Children)
                {
                    AssertFinite(child, name);
                }

                break;
        }
    }

    private static bool IsFinite(Point2D p) => double.IsFinite(p.X) && double.IsFinite(p.Y);
}
