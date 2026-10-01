using System.Diagnostics;
using VCCad.Core.Samples;
using System.Text;
using VCCad.Core.Model;
using VCCad.Geometry;
using VCCad.Pdf.Parsing;
using Xunit;

namespace VCCad.Pdf.Tests;

/// <summary>
/// Validates our own exports with the real veraPDF engine (the same one the
/// corpus shipments are built against). These tests run only when a veraPDF
/// installation is discoverable — set <c>VCCAD_VERAPDF</c> to the <c>verapdf</c>
/// launcher — otherwise they are no-ops, so CI without Java/veraPDF is unaffected.
/// </summary>
public class PdfAValidationTests
{
    private static string? VeraPdf()
    {
        string? env = Environment.GetEnvironmentVariable("VCCAD_VERAPDF");
        if (!string.IsNullOrEmpty(env) && File.Exists(env))
        {
            return env;
        }

        foreach (string candidate in new[]
                 {
                     "/tmp/opencode/vpdf/install/verapdf",
                     "/opt/verapdf/verapdf",
                     "/usr/local/bin/verapdf",
                 })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static CadDocument RichFixture()
    {
        CadDocument doc = CadDocument.CreateDefault("PDFA");
        Layer layer = doc.Artboards[0].Layers[0];

        PathItem rect = PathFactory.CreateRectangle("rect", new Rect2D(20, 30, 120, 80));
        rect.Fill = FillSpec.Solid(ColorRgb.FromBytes(220, 30, 30));
        rect.Stroke = new StrokeSpec(true, ColorRgb.Black, 2.5, StrokeCap.Round, StrokeJoin.Bevel, 3.0);
        layer.AddItem(rect);

        var text = new TextItem { Name = "label", Origin = new Point2D(50, 200) };
        text.Runs.Add(new TextRun { Text = "PDF/A-2b conformance", FontFamily = "Helvetica", FontSize = 18 });
        layer.AddItem(text);

        return doc;
    }

    private static (int Code, string Output) RunVeraPdf(string exe, string pdfPath, string flavour)
    {
        var psi = new ProcessStartInfo(exe, $"--format text --flavour {flavour} \"{pdfPath}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(180000);
        return (p.ExitCode, output);
    }

    private static void Validate(string flavour)
    {
        string? exe = VeraPdf();
        if (exe is null)
        {
            return; // external validator unavailable
        }

        byte[] pdf = PdfDocumentExporter.Export(RichFixture());
        string path = Path.Combine(Path.GetTempPath(), $"vccad-pdfa-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, pdf);
        try
        {
            (int code, string output) = RunVeraPdf(exe, path, flavour);
            Assert.True(code == 0, $"veraPDF {flavour} rejected our export:\n{output}");
            Assert.Contains("PASS", output);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ExportPassesPdfA2b_VeraPdf()
    {
        if (!StandardFontFixture.Available) { return; }

        Validate("2b");
    }

    [Fact]
    public void SamplePatternReexportPassesPdfA2b_VeraPdf()
    {
        if (!StandardFontFixture.Available) { return; }

        string? sample = SamplePath();
        if (sample is null || VeraPdf() is null)
        {
            return;
        }

        CadDocument doc = PdfImporter.Import(File.ReadAllBytes(sample));
        byte[] pdf = PdfDocumentExporter.Export(doc);
        string path = Path.Combine(Path.GetTempPath(), $"vccad-pdfa-sample-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, pdf);
        try
        {
            (int code, string output) = RunVeraPdf(VeraPdf()!, path, "2b");
            Assert.True(code == 0, $"veraPDF 2b rejected the sample re-export:\n{output}");
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---------------------------------------------------------- writer-side structures

    [Fact]
    public void SidecarIsACatalogStreamNotAnEmbeddedFile_6_8()
    {
        if (!StandardFontFixture.Available) { return; }

        var file = new PdfFile(PdfDocumentExporter.Export(RichFixture()));
        var catalog = file.ResolveDict(file.GetObject(file.FindCatalog() ?? 0));
        Assert.NotNull(catalog);

        // PDF/A 6.8 forbids non-PDF/A /EmbeddedFiles; we expose the model through
        // a plain catalog stream instead.
        Assert.Null(catalog!.GetValueOrDefault("Names"));
        Assert.True(file.Resolve(catalog.GetValueOrDefault("VCCadDocument")) is PdfStream);

        string text = Encoding.Latin1.GetString(PdfDocumentExporter.Export(RichFixture()));
        Assert.DoesNotContain("/EmbeddedFiles", text);
        Assert.DoesNotContain("/Filespec", text);
    }

    [Fact]
    public void CatalogHasRgbOutputIntent_6_2_4()
    {
        if (!StandardFontFixture.Available) { return; }

        var file = new PdfFile(PdfDocumentExporter.Export(RichFixture()));
        var catalog = file.ResolveDict(file.GetObject(file.FindCatalog() ?? 0));
        Assert.NotNull(catalog);

        var intents = file.Resolve(catalog!.GetValueOrDefault("OutputIntents")) as List<object?>;
        Assert.NotNull(intents);
        var intent = file.ResolveDict(intents![0]);
        Assert.NotNull(intent);
        Assert.Equal("GTS_PDFA1", (intent!.GetValueOrDefault("S") as PdfName)?.Value);

        var profile = file.Resolve(intent.GetValueOrDefault("DestOutputProfile")) as PdfStream;
        Assert.NotNull(profile);
        byte[] icc = file.GetStreamData(profile!);
        Assert.Equal("acsp", Encoding.Latin1.GetString(icc, 36, 4));
        Assert.Equal("mntr", Encoding.Latin1.GetString(icc, 12, 4));
    }

    [Fact]
    public void XmpDeclaresPdfA2b_6_6_4()
    {
        if (!StandardFontFixture.Available) { return; }

        var file = new PdfFile(PdfDocumentExporter.Export(RichFixture()));
        var catalog = file.ResolveDict(file.GetObject(file.FindCatalog() ?? 0));
        var metadata = (PdfStream)file.Resolve(catalog!.GetValueOrDefault("Metadata"))!;
        string xmp = Encoding.UTF8.GetString(file.GetStreamData(metadata));

        Assert.Contains("pdfaid:part>2<", xmp);
        Assert.Contains("pdfaid:conformance>B<", xmp);
    }

    [Fact]
    public void HeaderHasBinaryMarker_6_1_2()
    {
        if (!StandardFontFixture.Available) { return; }

        byte[] pdf = PdfDocumentExporter.Export(RichFixture());
        // "%PDF-1.7\n%" followed by at least four bytes > 127.
        Assert.Equal((byte)'%', pdf[9]);
        for (int i = 10; i < 14; i++)
        {
            Assert.True(pdf[i] > 127, $"byte {i} of the binary marker is {pdf[i]}");
        }
    }

    private static string? SamplePath()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string? candidate = SampleLibrary.Find("A0-Temi-Bow-Bustier-sewing-pattern.pdf");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }
}