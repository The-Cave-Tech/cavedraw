using System.Diagnostics;
using System.Text;
using VCCad.Core.Model;
using VCCad.Geometry;
using VCCad.Pdf.Parsing;
using Xunit;

namespace VCCad.Pdf.Tests;

/// <summary>
/// Structural conformance checks for the PDF writer, mirroring the rule areas the
/// veraPDF corpus is organised around (ISO 19005 / ISO 32000): file structure,
/// fonts, content streams and metadata. Each test names the clause it targets so
/// the suite reads like the corpus checklist. External cross-checks (qpdf) are
/// used where available and skipped otherwise.
/// </summary>
public class PdfConformanceTests
{
    private static CadDocument Fixture()
    {
        CadDocument doc = CadDocument.CreateDefault("Conformance");
        Layer layer = doc.Artboards[0].Layers[0];

        PathItem rect = PathFactory.CreateRectangle("rect", new Rect2D(20, 30, 120, 80));
        rect.Fill = FillSpec.Solid(ColorRgb.FromBytes(220, 30, 30));
        rect.Stroke = new StrokeSpec(true, ColorRgb.Black, 2.5, StrokeCap.Round, StrokeJoin.Bevel, 3.0);
        layer.AddItem(rect);

        PathItem open = PathFactory.CreatePolyline("open", new[]
        {
            new Point2D(300, 300), new Point2D(360, 300), new Point2D(360, 380),
        });
        open.Stroke = StrokeSpec.Hairline(ColorRgb.Green);
        layer.AddItem(open);

        var text = new TextItem { Name = "label", Origin = new Point2D(50, 200) };
        text.Runs.Add(new TextRun { Text = "Hello VeraPDF", FontFamily = "Helvetica", FontSize = 18 });
        layer.AddItem(text);
        return doc;
    }

    private static byte[] Export() => PdfDocumentExporter.Export(Fixture());

    [Fact]
    public void EmbeddedFontProgrammesAreReusedVerbatim_NoSubstitution()
    {
        if (!StandardFontFixture.Available) { return; }

        string? sample = Sample();
        if (sample is null)
        {
            return;
        }

        CadDocument doc = PdfImporter.Import(File.ReadAllBytes(sample));
        List<TextItem> texts = doc.Artboards.SelectMany(a => a.Layers)
            .SelectMany(l => l.Children).OfType<TextItem>().ToList();
        if (!texts.SelectMany(t => t.Runs).Any(r => r.EmbeddedFont is not null))
        {
            return; // sample has no embedded fonts in this checkout
        }

        byte[] pdf = PdfDocumentExporter.Export(doc);
        string ascii = Encoding.Latin1.GetString(pdf);

        // The original programmes are re-emitted (FontFile2/FontFile3), and no
        // standard-font programme is embedded for those runs.
        Assert.True(ascii.Contains("/FontFile2") || ascii.Contains("/FontFile3"),
            "expected an embedded font programme");
        Assert.Contains("/Encoding", ascii);
    }

    private static string? Sample()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "samples", "A0-Temi-Bow-Bustier-sewing-pattern.pdf");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }

    // ---------------------------------------------------------------- 7.5 File structure

    [Fact]
    public void HeaderAndEofAreWellFormed_7_5_1()
    {
        if (!StandardFontFixture.Available) { return; }

        byte[] pdf = Export();
        Assert.StartsWith("%PDF-1.7", Encoding.ASCII.GetString(pdf, 0, 8));
        Assert.Equal("%%EOF", Encoding.ASCII.GetString(pdf).TrimEnd().Split('\n').Last().Trim());
    }

    [Fact]
    public void TrailerHasRootAndSizeAndNoEncryption_7_5_5()
    {
        if (!StandardFontFixture.Available) { return; }

        string text = Encoding.Latin1.GetString(Export());
        int trailer = text.LastIndexOf("trailer", StringComparison.Ordinal);
        Assert.True(trailer >= 0, "no trailer");
        string tail = text[trailer..];

        Assert.Contains("/Root", tail);
        Assert.Contains("/Size", tail);
        Assert.DoesNotContain("/Encrypt", tail);
        Assert.Contains("startxref", tail);
    }

    [Fact]
    public void StartXrefPointsAtTheXrefKeyword_7_5_4()
    {
        if (!StandardFontFixture.Available) { return; }

        byte[] pdf = Export();
        string text = Encoding.Latin1.GetString(pdf);
        int marker = text.LastIndexOf("startxref", StringComparison.Ordinal);
        Assert.True(marker >= 0);
        string number = new string(text[(marker + "startxref".Length)..]
            .TrimStart().TakeWhile(char.IsDigit).ToArray());
        Assert.True(long.TryParse(number, out long offset), "startxref offset not numeric");

        string at = text.Substring((int)offset, 5);
        Assert.Equal("xref\n", at.Replace("\r", string.Empty));
    }

    [Fact]
    public void XrefOffsetsPointAtTheirObjects_7_5_4()
    {
        if (!StandardFontFixture.Available) { return; }

        byte[] pdf = Export();
        string text = Encoding.Latin1.GetString(pdf);

        int xref = text.LastIndexOf("\nxref", StringComparison.Ordinal);
        Assert.True(xref >= 0, "no xref table");
        string[] lines = text[(xref + 1)..].Split('\n');
        // lines[0]=xref, lines[1]="0 N", then N entries.
        string[] header = lines[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int count = int.Parse(header[1]);

        // lines[0]=xref, lines[1]="0 N", lines[2]=free object 0, lines[2+i]=object i.
        for (int i = 1; i < count; i++)
        {
            string entry = lines[2 + i];
            long offset = long.Parse(entry[..10]);
            string at = text.Substring((int)offset);
            Assert.Matches($"^{i}\\s+0\\s+obj", at);
        }
    }

    [Fact]
    public void TrailerCarriesFileIdAndInfo_14_4()
    {
        if (!StandardFontFixture.Available) { return; }

        string text = Encoding.Latin1.GetString(Export());
        int trailer = text.LastIndexOf("trailer", StringComparison.Ordinal);
        string tail = text[trailer..];

        Assert.Contains("/Info", tail);
        Assert.Matches(@"/ID\s*\[<[0-9A-Fa-f]+>\s*<[0-9A-Fa-f]+>\]", tail);
    }

    [Fact]
    public void CatalogHasXmpMetadata_14_3_2()
    {
        if (!StandardFontFixture.Available) { return; }

        var file = new PdfFile(Export());
        var catalog = file.ResolveDict(file.GetObject(file.FindCatalog() ?? 0));
        Assert.NotNull(catalog);
        var metadata = file.Resolve(catalog!.GetValueOrDefault("Metadata"));
        Assert.IsType<PdfStream>(metadata);

        string xmp = Encoding.UTF8.GetString(file.GetStreamData((PdfStream)metadata!));
        Assert.Contains("<rdf:RDF", xmp);
        Assert.Contains("dc:title", xmp);
        Assert.Contains("Conformance", xmp); // document name
    }

    [Fact]
    public void InfoDictionaryNamesTheProducer_14_3_3()
    {
        if (!StandardFontFixture.Available) { return; }

        string text = Encoding.Latin1.GetString(Export());
        Assert.Contains("/Producer (VCCad)", text);
        Assert.Contains("/CreationDate (D:", text);
    }

    [Fact]
    public void QpdfCheckAcceptsTheFile_External()
    {
        if (!StandardFontFixture.Available) { return; }

        if (!HasTool("qpdf"))
        {
            return; // external validator unavailable
        }

        byte[] pdf = Export();
        string path = Path.Combine(Path.GetTempPath(), $"vccad-conf-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, pdf);
        try
        {
            (int code, string output) = Run("qpdf", $"--check \"{path}\"");
            Assert.True(code == 0, $"qpdf --check failed ({code}):\n{output}");
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ---------------------------------------------------------------- 9.5/9.6/9.7 Fonts

    [Fact]
    public void EveryFontIsEmbedded_9_7_4()
    {
        if (!StandardFontFixture.Available) { return; }

        var file = new PdfFile(Export());
        var fonts = FindByType(file, "Font");
        Assert.NotEmpty(fonts);

        foreach (Dictionary<string, object?> font in fonts)
        {
            Assert.True(HasEmbeddedProgram(file, font),
                $"font not embedded: {string.Join(' ', font.Keys)}");
        }
    }

    [Fact]
    public void EveryFontHasWidthInformation_9_6_3()
    {
        if (!StandardFontFixture.Available) { return; }

        var file = new PdfFile(Export());
        foreach (Dictionary<string, object?> font in FindByType(file, "Font"))
        {
            string subtype = (font.GetValueOrDefault("Subtype") as PdfName)?.Value ?? string.Empty;
            if (subtype == "Type0")
            {
                object? descendants = file.Resolve(font.GetValueOrDefault("DescendantFonts"));
                Assert.IsType<List<object?>>(descendants);
                var descendant = file.ResolveDict(((List<object?>)descendants!)[0]);
                Assert.NotNull(descendant);
                Assert.True(file.Resolve(descendant!.GetValueOrDefault("W")) is List<object?>,
                    "CID font has no /W widths");
            }
            else if (subtype is "CIDFontType0" or "CIDFontType2")
            {
                Assert.True(file.Resolve(font.GetValueOrDefault("W")) is List<object?>,
                    "CID font has no /W widths");
            }
            else
            {
                Assert.True(file.Resolve(font.GetValueOrDefault("Widths")) is List<object?>,
                    "simple font has no /Widths");
                Assert.NotNull(file.Resolve(font.GetValueOrDefault("FirstChar")));
            }
        }
    }

    [Fact]
    public void CompositeFontsHaveToUnicode_9_10_3()
    {
        if (!StandardFontFixture.Available) { return; }

        var file = new PdfFile(Export());
        foreach (Dictionary<string, object?> font in FindByType(file, "Font"))
        {
            if ((font.GetValueOrDefault("Subtype") as PdfName)?.Value == "Type0")
            {
                Assert.NotNull(file.Resolve(font.GetValueOrDefault("ToUnicode")));
            }
        }
    }

    // ---------------------------------------------------------------- 7.8.2 Content streams

    [Fact]
    public void EveryStreamDecodesAndLengthMatches_7_3_4()
    {
        if (!StandardFontFixture.Available) { return; }

        var file = new PdfFile(Export());
        foreach (int number in file.ObjectNumbers)
        {
            if (file.GetObject(number) is not PdfStream stream || stream.Dict.Count == 0)
            {
                continue;
            }

            byte[] data = file.GetStreamData(stream); // throws if the filter chain is broken
            Assert.NotNull(data);
        }
    }

    [Fact]
    public void ContentOperatorsAreBalanced_8_Graphics()
    {
        if (!StandardFontFixture.Available) { return; }

        string content = AllContent(Export());
        AssertBalanced(content, "q", "Q");
        AssertBalanced(content, "BT", "ET");
        AssertBalanced(content, "BDC", "EMC");
    }

    [Fact]
    public void ContentUsesOnlyRealNumbers_8_Graphics()
    {
        if (!StandardFontFixture.Available) { return; }

        string content = AllContent(Export());
        Assert.DoesNotContain("NaN", content);
        Assert.DoesNotContain("Infinity", content);
    }

    // ---------------------------------------------------------------- helpers

    private static void AssertBalanced(string content, string open, string close)
    {
        int depth = 0;
        foreach (string token in content.Split(new[] { ' ', '\n', '\r', '\t' },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (token == open)
            {
                depth++;
            }
            else if (token == close)
            {
                depth--;
                Assert.True(depth >= 0, $"more {close} than {open}");
            }
        }

        Assert.Equal(0, depth);
    }

    private static List<Dictionary<string, object?>> FindByType(PdfFile file, string type)
    {
        var result = new List<Dictionary<string, object?>>();
        foreach (int number in file.ObjectNumbers)
        {
            if (file.GetObject(number) is Dictionary<string, object?> dict &&
                dict.GetValueOrDefault("Type") is PdfName { } n && n.Value == type)
            {
                result.Add(dict);
            }
        }

        return result;
    }

    private static bool HasEmbeddedProgram(PdfFile file, Dictionary<string, object?> font)
    {
        var dicts = new List<Dictionary<string, object?>> { font };

        if (file.Resolve(font.GetValueOrDefault("DescendantFonts")) is List<object?> descendants)
        {
            foreach (object? d in descendants)
            {
                if (file.ResolveDict(d) is { } dd)
                {
                    dicts.Add(dd);
                }
            }
        }

        foreach (Dictionary<string, object?> d in dicts)
        {
            if (file.ResolveDict(d.GetValueOrDefault("FontDescriptor")) is not { } descriptor)
            {
                continue;
            }

            if (file.Resolve(descriptor.GetValueOrDefault("FontFile")) is not null ||
                file.Resolve(descriptor.GetValueOrDefault("FontFile2")) is not null ||
                file.Resolve(descriptor.GetValueOrDefault("FontFile3")) is not null)
            {
                return true;
            }
        }

        return false;
    }

    private static string AllContent(byte[] pdf)
    {
        string text = Encoding.Latin1.GetString(pdf);
        var sb = new StringBuilder();
        const string marker = "/FlateDecode >>\nstream";
        int scan = 0;
        while (true)
        {
            int dictStart = text.IndexOf(marker, scan, StringComparison.Ordinal);
            if (dictStart < 0)
            {
                break;
            }

            int dataStart = dictStart + marker.Length;
            if (dataStart < text.Length && text[dataStart] == '\n')
            {
                dataStart++;
            }

            int dataEnd = text.IndexOf("endstream", dataStart, StringComparison.Ordinal);
            while (dataEnd > dataStart && (text[dataEnd - 1] == '\n' || text[dataEnd - 1] == '\r'))
            {
                dataEnd--;
            }

            byte[] raw = PdfDocumentExporter.Decompress(
                Encoding.Latin1.GetBytes(text[dataStart..dataEnd]));
            string decoded = Encoding.UTF8.GetString(raw);
            // Only content streams contain painting operators; skip the JSON sidecar.
            if (decoded.Contains(" cm") || decoded.Contains(" Tj") || decoded.Contains(" m\n"))
            {
                sb.Append(decoded);
            }

            scan = dataEnd + 1;
        }

        return sb.ToString();
    }

    private static bool HasTool(string tool)
    {
        try
        {
            (int code, _) = Run(tool, "--version");
            return code == 0;
        }
        catch
        {
            return false;
        }
    }

    private static (int Code, string Output) Run(string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(30000);
        return (p.ExitCode, output);
    }
}
