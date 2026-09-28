using System.Text;
using System.Text.Json;
using VCCad.Core.Model;
using VCCad.Pdf.Tests.Corpus;
using Xunit;
using Xunit.Abstractions;

namespace VCCad.Pdf.Tests;

/// <summary>
/// Corpus-driven feature harness over the Ghostscript/MuPDF public test corpus
/// (see <see cref="PdfCorpus"/>). Unlike the veraPDF sweep — which mainly asks
/// "can we read this at all?" — these tests also answer "which document features
/// does the corpus exercise, and does the importer still survive all of them?".
///
/// Every theory emits a single empty-string sentinel when the corpus is absent,
/// so the suite is green without it; the aggregate tests report one summary
/// failure rather than thousands of individual ones.
/// </summary>
public class GhostscriptCorpusTests
{
    private readonly ITestOutputHelper _output;

    public GhostscriptCorpusTests(ITestOutputHelper output) => _output = output;

    /// <summary>Theory rows: one PDF path per corpus file (or one sentinel row).</summary>
    public static IEnumerable<object[]> CorpusFiles() => PdfCorpus.PdfTheoryData();

    // ------------------------------------------------------------------
    // per-file theories
    // ------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(CorpusFiles))]
    [Trait("Category", "Corpus")]
    public void CorpusPdfImportReturnsUsableDocument(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return; // corpus not present
        }

        string name = PdfCorpus.RelativePath(path);
        byte[] bytes = File.ReadAllBytes(path);

        CadDocument document;
        try
        {
            document = PdfImporter.Import(bytes);
        }
        catch (Exception ex)
        {
            Assert.Fail($"Import threw on {name}: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        DocumentSanity.AssertSane(document, name);

        // The public entry point must always produce a document; when the real
        // vector path succeeds the geometry must also be finite.
        if (PdfImporter.TryImportVector(bytes, out CadDocument? vector) && vector is not null)
        {
            DocumentSanity.AssertSane(vector, name);
        }
    }

    [Theory]
    [MemberData(nameof(CorpusFiles))]
    [Trait("Category", "Corpus")]
    public void FeatureProbeClassifiesEveryFile(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return; // corpus not present
        }

        string name = PdfCorpus.RelativePath(path);
        PdfFeatureReport report = PdfFeatureProbe.Probe(File.ReadAllBytes(path), name);

        Assert.NotNull(report);
        Assert.Equal(name, report.Name);

        // An unparseable file must be reported as such rather than silently
        // classified as featureless.
        Assert.Equal(report.ParseFailed, report.Has(PdfFeature.DocumentUnparseable));

        // A file that parsed must not claim to be unparseable.
        if (!report.ParseFailed)
        {
            Assert.False(report.Has(PdfFeature.DocumentUnparseable), $"{name}: contradictory unparseable flag");

            // Every file in this corpus is a real PDF with a document catalog
            // (measured 208/208), so a probe that finds none has a bug.
            Assert.True(report.Has(PdfFeature.ProbeCatalog), $"{name}: probe found no document catalog");
            Assert.True(report.Count("count.pages") >= 1, $"{name}: probe found no pages");
        }
    }

    // ------------------------------------------------------------------
    // aggregate inventory
    // ------------------------------------------------------------------

    /// <summary>
    /// Feature floors measured over the Ghostscript corpus on the day this
    /// harness landed (208 PDFs; measured value in the trailing comment). They
    /// are deliberately a little below the measurement: the point is to fail
    /// loudly if the corpus or the probe stops covering a feature class, not to
    /// forbid the numbers from growing. Raise them when the corpus grows.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, int> FeatureFloors =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [PdfFeature.ProbeStructuredParsed] = 200, // 208
            [PdfFeature.ProbeCatalog] = 200,          // 208

            // fonts
            [PdfFeature.FontAny] = 130,               // 137
            [PdfFeature.FontType1] = 100,             // 105
            [PdfFeature.FontTrueType] = 18,           // 21
            [PdfFeature.FontType0] = 45,              // 49
            [PdfFeature.FontType3] = 7,               // 7
            [PdfFeature.FontCff] = 90,                // 96
            [PdfFeature.FontOpenType] = 2,            // 2
            [PdfFeature.FontEmbedded] = 110,          // 115
            [PdfFeature.FontNonEmbedded] = 100,       // 106
            [PdfFeature.FontToUnicode] = 80,          // 86
            [PdfFeature.FontEncodingDifferences] = 55, // 58
            [PdfFeature.FontIdentityH] = 45,          // 49
            [PdfFeature.FontCidType0] = 33,           // 36
            [PdfFeature.FontCidType2] = 13,           // 15

            // graphics operators
            [PdfFeature.OpAny] = 190,                 // 195
            [PdfFeature.OpMove] = 104,                // 108
            [PdfFeature.OpLine] = 98,                 // 102
            [PdfFeature.OpCurve] = 33,                // 35
            [PdfFeature.OpRect] = 105,                // 109
            [PdfFeature.OpFill] = 118,                // 123
            [PdfFeature.OpStroke] = 98,               // 102
            [PdfFeature.OpFillStroke] = 21,           // 23
            [PdfFeature.OpEvenOdd] = 22,              // 24
            [PdfFeature.OpClip] = 115,                // 120
            [PdfFeature.OpClipEvenOdd] = 14,          // 15
            [PdfFeature.OpGs] = 112,                  // 118
            [PdfFeature.OpDo] = 115,                  // 121
            [PdfFeature.OpShading] = 20,              // 21
            [PdfFeature.OpText] = 122,                // 127
            [PdfFeature.OpInlineImage] = 5,           // 6

            // transparency
            [PdfFeature.GsExtGState] = 125,           // 132
            [PdfFeature.GsSoftMask] = 60,             // 64
            [PdfFeature.GsFillAlpha] = 58,            // 63
            [PdfFeature.GsStrokeAlpha] = 53,          // 57
            [PdfFeature.GsFillAlphaTranslucent] = 27, // 30
            [PdfFeature.GsStrokeAlphaTranslucent] = 19, // 21
            [PdfFeature.GsBlendMode] = 65,            // 69
            [PdfFeature.GsBlendModeNonNormal] = 16,   // 18
            [PdfFeature.TransparencyGroup] = 27,      // 29
            [PdfFeature.TransparencySoftMaskImage] = 3, // 3

            // patterns and shadings
            [PdfFeature.PatternTiling] = 19,          // 20
            [PdfFeature.PatternShading] = 2,          // 2
            [PdfFeature.ShadingAny] = 21,             // 22
            [PdfFeature.ResShading] = 21,             // 22
            [PdfFeature.ResPattern] = 21,             // 22

            // images and XObjects
            [PdfFeature.ImageAny] = 105,              // 112
            [PdfFeature.XObjectImage] = 105,          // 112
            [PdfFeature.XObjectForm] = 34,            // 36
            [PdfFeature.ImageDct] = 28,               // 31
            [PdfFeature.ImageJbig2] = 26,             // 28
            [PdfFeature.ImageJpx] = 12,               // 13
            [PdfFeature.ImageCcitt] = 5,              // 6
            [PdfFeature.ImageFlate] = 43,             // 47
            [PdfFeature.ImageMask] = 6,               // 7

            // resources and colour spaces
            [PdfFeature.ResResources] = 200,          // 208
            [PdfFeature.ResFont] = 132,               // 138
            [PdfFeature.ResXObject] = 120,            // 126
            [PdfFeature.ResColorSpace] = 150,         // 157
            [PdfFeature.ResIccBased] = 30,            // 32
            [PdfFeature.ResSeparation] = 82,          // 87
            [PdfFeature.ResDeviceN] = 22,             // 24
            [PdfFeature.ResIndexed] = 26,             // 28
            [PdfFeature.ResOutputIntents] = 75,       // 79
            [PdfFeature.ResExtGState] = 125,          // 132

            // page / document
            [PdfFeature.DocumentAnnotations] = 8,     // 9
            [PdfFeature.DocumentOutlines] = 28,       // 30
            [PdfFeature.DocumentMetadataXmp] = 105,   // 113
            [PdfFeature.DocumentCropBox] = 105,       // 113
            [PdfFeature.DocumentEncrypted] = 9,       // 10
            [PdfFeature.DocumentPdfA] = 2,            // 2
            [PdfFeature.DocumentOptionalContent] = 5, // 6
            [PdfFeature.DocumentOcg] = 5,             // 6
            [PdfFeature.DocumentObjectStreams] = 18,  // 20
            [PdfFeature.DocumentXrefStream] = 18,     // 20
            [PdfFeature.DocumentLinearized] = 40,     // 44
        };

    [Fact]
    [Trait("Category", "Corpus")]
    public void FeatureInventoryMeetsFloors()
    {
        if (!PdfCorpus.Available)
        {
            return; // corpus not present
        }

        var observed = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var examples = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        var counts = new SortedDictionary<string, long>(StringComparer.Ordinal);
        var areas = new SortedDictionary<string, int>(StringComparer.Ordinal);

        int files = 0;
        int unparseable = 0;

        foreach ((string path, PdfFeatureReport report) in PdfCorpus.Probed())
        {
            files++;
            string relative = PdfCorpus.RelativePath(path);
            string area = relative.Contains('/') ? relative[..relative.IndexOf('/')] : ".";
            areas[area] = areas.GetValueOrDefault(area) + 1;

            if (report.ParseFailed)
            {
                unparseable++;
            }

            foreach (string feature in report.ObservedFlags())
            {
                observed[feature] = observed.GetValueOrDefault(feature) + 1;
                if (!examples.TryGetValue(feature, out List<string>? list))
                {
                    list = new List<string>();
                    examples[feature] = list;
                }

                if (list.Count < 5)
                {
                    list.Add(relative);
                }
            }

            foreach (KeyValuePair<string, int> pair in report.ObservedCounts())
            {
                counts[pair.Key] = counts.GetValueOrDefault(pair.Key) + pair.Value;
            }
        }

        var payload = new Dictionary<string, object>
        {
            ["corpusRoot"] = PdfCorpus.CorpusRoot() ?? string.Empty,
            ["files"] = files,
            ["unparseable"] = unparseable,
            ["areas"] = areas,
            ["featureFileCounts"] = observed,
            ["featureExamples"] = examples,
            ["featureTotals"] = counts,
        };

        string json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        string artifact = Path.Combine(AppContext.BaseDirectory, "feature-inventory.json");
        try
        {
            File.WriteAllText(artifact, json);
        }
        catch (IOException)
        {
            // Writing the artifact is a convenience; the report below is canonical.
        }

        var summary = new StringBuilder();
        summary.AppendLine(FormattableString.Invariant($"corpus: {files} PDFs ({unparseable} unparseable)"));
        summary.AppendLine(FormattableString.Invariant($"root:   {PdfCorpus.CorpusRoot()}"));
        summary.AppendLine(FormattableString.Invariant($"artifact: {artifact}"));
        summary.AppendLine("areas:  " + string.Join(", ", areas.Select(pair => $"{pair.Key}={pair.Value}")));
        summary.AppendLine("features (files containing the feature):");
        foreach (KeyValuePair<string, int> pair in observed)
        {
            summary.AppendLine(FormattableString.Invariant(
                $"  {pair.Key,-34} {pair.Value,4}   e.g. {string.Join(", ", examples[pair.Key].Take(2))}"));
        }

        summary.AppendLine("totals:");
        foreach (KeyValuePair<string, long> pair in counts)
        {
            summary.AppendLine(FormattableString.Invariant($"  {pair.Key,-34} {pair.Value,8}"));
        }

        _output.WriteLine(summary.ToString());

        var failures = new List<string>();
        if (files < 200)
        {
            failures.Add($"corpus has {files} PDFs; the Ghostscript corpus has 208");
        }

        foreach (KeyValuePair<string, int> floor in FeatureFloors)
        {
            int actual = observed.GetValueOrDefault(floor.Key);
            if (actual < floor.Value)
            {
                failures.Add($"{floor.Key}: {actual} files, floor {floor.Value}");
            }
        }

        Assert.True(failures.Count == 0,
            "corpus feature inventory fell below its measured floors:\n  " + string.Join("\n  ", failures));
    }

    // ------------------------------------------------------------------
    // Ghent import coverage
    // ------------------------------------------------------------------

    [Fact]
    [Trait("Category", "Corpus")]
    public void GhentImportCoverageMeetsFloor()
    {
        IReadOnlyList<string> ghent = PdfCorpus.FilesUnder(PdfCorpus.GhentDirectories.ToArray());
        if (ghent.Count == 0)
        {
            return; // corpus not present
        }

        int ok = 0;
        var misses = new List<string>();
        foreach (string path in ghent)
        {
            string relative = PdfCorpus.RelativePath(path);
            bool imported;
            string? failure = null;
            try
            {
                imported = PdfImporter.TryImportVector(File.ReadAllBytes(path), out CadDocument? document)
                           && document is not null;
                if (imported)
                {
                    DocumentSanity.AssertSane(document!, relative);
                }
            }
            catch (Exception ex)
            {
                imported = false;
                failure = ex.GetType().Name;
            }

            if (imported)
            {
                ok++;
            }
            else
            {
                misses.Add(failure is null ? relative : $"{relative} ({failure})");
            }
        }

        _output.WriteLine($"Ghent block: vector import {ok}/{ghent.Count}");

        // Measured baseline: 79/79 (Ghent_V3.0 = 28, Ghent_V5.0 = 51). Every one
        // of these files is a valid, well-formed PDF, so this is the strongest
        // "must import" set in the corpus and the floor is full coverage.
        if (ghent.Count >= 70)
        {
            Assert.True(ok >= ghent.Count,
                $"Ghent vector import covered {ok}/{ghent.Count}; misses: {string.Join(", ", misses.Take(10))}");
        }
    }

    // ------------------------------------------------------------------
    // Illustrator private data
    // ------------------------------------------------------------------

    [Fact]
    [Trait("Category", "Corpus")]
    public void ProbeReportsIllustratorPrivateData()
    {
        if (!PdfCorpus.Available)
        {
            return; // corpus not present
        }

        IReadOnlyList<string> pieceInfo = PdfCorpus.FilesWith(PdfFeature.DocumentPieceInfo);
        IReadOnlyList<string> aiPrivate = PdfCorpus.FilesWith(PdfFeature.DocumentAiPrivateData);

        _output.WriteLine($"files with /PieceInfo: {pieceInfo.Count}; with /AIPrivateData: {aiPrivate.Count}");
        foreach (string file in pieceInfo.Take(10))
        {
            _output.WriteLine("  " + file);
        }

        // The corpus may contain no Illustrator files at all — that is fine. What
        // must hold is consistency: AIPrivateData lives inside PieceInfo, so a
        // file reporting private data must also report PieceInfo.
        foreach (string file in aiPrivate)
        {
            Assert.Contains(file, pieceInfo);
        }
    }
}
