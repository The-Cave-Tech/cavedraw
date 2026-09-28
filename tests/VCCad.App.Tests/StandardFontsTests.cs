using Avalonia.Headless.XUnit;
using System.Text.Json;
using VCCad.App.Automation;
using VCCad.App.Fonts;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Geometry;
using VCCad.Pdf;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// Classifying the standard PDF fonts.
///
/// A PDF may name any of the fourteen standard faces and embed nothing; the viewer is
/// expected to supply them. Classifying the name is pure logic and is tested directly,
/// because that is the part that must never be wrong — everything after it is either a
/// file on the machine or a report that it is missing.
/// </summary>
public class StandardFontsTests
{
    [Theory]
    [InlineData("Helvetica", StandardFontKind.Sans, false, false)]
    [InlineData("Helvetica-Bold", StandardFontKind.Sans, true, false)]
    [InlineData("Helvetica-Oblique", StandardFontKind.Sans, false, true)]
    [InlineData("Helvetica-BoldOblique", StandardFontKind.Sans, true, true)]
    [InlineData("Times-Roman", StandardFontKind.Serif, false, false)]
    [InlineData("Times-BoldItalic", StandardFontKind.Serif, true, true)]
    [InlineData("Courier", StandardFontKind.Mono, false, false)]
    [InlineData("Courier-BoldOblique", StandardFontKind.Mono, true, true)]
    [InlineData("Symbol", StandardFontKind.Symbol, false, false)]
    [InlineData("ZapfDingbats", StandardFontKind.Dingbats, false, false)]
    public void TheFourteenClassifyCorrectly(string name, StandardFontKind kind, bool bold, bool italic)
    {
        Assert.True(StandardFonts.TryResolve(name, bold: false, italic: false, out StandardFace face));
        Assert.Equal(kind, face.Kind);
        Assert.Equal(bold, face.Bold);
        Assert.Equal(italic, face.Italic);
        Assert.True(StandardFonts.IsBase14(name));
    }

    [Theory]
    [InlineData("ArialMT", StandardFontKind.Sans)]
    [InlineData("Arial-BoldMT", StandardFontKind.Sans)]
    [InlineData("ABCDEF+Helvetica-Bold", StandardFontKind.Sans)]
    [InlineData("TimesNewRomanPSMT", StandardFontKind.Serif)]
    [InlineData("TimesNewRomanPS-BoldItalicMT", StandardFontKind.Serif)]
    [InlineData("CourierNewPSMT", StandardFontKind.Mono)]
    [InlineData("CourierStd-Bold", StandardFontKind.Mono)]
    [InlineData("SymbolMT", StandardFontKind.Symbol)]
    public void AliasesClassifyByTheirFamilyWord(string name, StandardFontKind kind)
    {
        // The alias space is enormous; every alias contains the word that identifies its
        // family, which is why one rule covers all of them.
        Assert.True(StandardFonts.TryResolve(name, bold: false, italic: false, out StandardFace face));
        Assert.Equal(kind, face.Kind);
    }

    [Fact]
    public void ABoldSubsetPrefixIsStrippedBeforeClassification()
    {
        Assert.True(StandardFonts.TryResolve("NPFRLV+CenturyGothic-Bold", bold: false, italic: false,
            out StandardFace face));

        // Century Gothic is a geometric sans that is not one of the fourteen, so it is
        // classified for substitution but is not a base-14 name.
        Assert.Equal(StandardFontKind.Sans, face.Kind);
        Assert.True(face.Bold);
        Assert.False(StandardFonts.IsBase14("NPFRLV+CenturyGothic-Bold"));
    }

    [Theory]
    [InlineData(StandardFontKind.Sans, false, false, "NimbusSans-Regular.otf")]
    [InlineData(StandardFontKind.Sans, true, true, "NimbusSans-BoldItalic.otf")]
    [InlineData(StandardFontKind.Serif, true, false, "NimbusRoman-Bold.otf")]
    [InlineData(StandardFontKind.Mono, false, true, "NimbusMonoPS-Italic.otf")]
    [InlineData(StandardFontKind.Symbol, false, false, "StandardSymbolsPS.otf")]
    [InlineData(StandardFontKind.Dingbats, false, false, "D050000L.otf")]
    public void TheUrwFaceForEachFamilyIsNamedCorrectly(StandardFontKind kind, bool bold, bool italic, string file)
    {
        Assert.Equal(file, StandardFonts.UrwFileName(new StandardFace(kind, bold, italic)));
    }

    [Fact]
    public void TheCloneFamiliesAreTheMetricCompatibleOnes()
    {
        // Helvetica/Arial, Times/Times New Roman, Courier/Courier New share metrics —
        // which is why every viewer substitutes one for the other.
        Assert.Equal("Arial", StandardFonts.CloneFamily(new StandardFace(StandardFontKind.Sans, false, false)));
        Assert.Equal("Times New Roman", StandardFonts.CloneFamily(new StandardFace(StandardFontKind.Serif, false, false)));
        Assert.Equal("Courier New", StandardFonts.CloneFamily(new StandardFace(StandardFontKind.Mono, false, false)));
        Assert.Null(StandardFonts.CloneFamily(new StandardFace(StandardFontKind.Symbol, false, false)));
    }

    [Fact]
    public void EveryFaceHasAFileAndAFamily()
    {
        foreach (StandardFace face in StandardFontResolver.AllFaces())
        {
            Assert.EndsWith(".otf", StandardFonts.UrwFileName(face), StringComparison.Ordinal);
            Assert.NotEmpty(StandardFonts.UrwFamily(face));
        }
    }
}

/// <summary>
/// Reporting which font a document's text is actually drawn with.
///
/// A PDF that does not embed Helvetica is not a problem — every viewer supplies it —
/// so the person is only interrupted when nothing on the machine can.
/// </summary>
public class FontUsageTests
{
    private static EditorViewModel WithText(string text, string? sourceFont)
    {
        var viewModel = new EditorViewModel();
        Layer layer = viewModel.Document.Artboards[0].Layers[0];
        var item = new TextItem { Origin = new Point2D(10, 10) };
        item.Runs.Add(new TextRun
        {
            Text = text,
            FontSize = 12,
            FontFamily = "Nimbus Sans",
            SourceFont = sourceFont,
        });
        layer.AddItem(item);
        return viewModel;
    }

    [AvaloniaFact]
    public void ADocumentWithNoTextHasNothingToWarnAbout()
    {
        Assert.Null(FontUsage.Warning(new CadDocument()));
    }

    [AvaloniaFact]
    public void TheReportNamesTheFontTheDocumentAskedFor()
    {
        EditorViewModel viewModel = WithText("DO NOT REPRODUCE", "Helvetica-Bold");

        IReadOnlyList<FontUsageEntry> report = FontUsage.Report(viewModel.Document);

        FontUsageEntry entry = Assert.Single(report);
        Assert.Equal("Helvetica-Bold", entry.BaseFont);
        Assert.False(entry.Embedded);
    }

    [AvaloniaFact]
    public void AStandardFontIsDescribedAsComingFromThisMachineOrAsMissing()
    {
        TextRun run = WithText("x", "Helvetica-Bold").Document.Artboards[0].Layers[0]
            .Children.OfType<TextItem>().First().Runs[0];

        string described = StandardFontResolver.Describe(run);

        // Either a real face was found (URW or a metric-compatible clone), or the report
        // says plainly that nothing on this machine can supply it. It must never pretend
        // to a face it does not have.
        bool found = described.Contains("this machine", StringComparison.Ordinal);
        bool missing = described.Contains("unavailable", StringComparison.Ordinal);
        Assert.True(found || missing, $"unexpected description: {described}");
    }

    [AvaloniaFact]
    public void TheFontSurfaceIsAutomationReachable()
    {
        Assert.True(EditorOperations.TryGet("fonts.list", out _));
        Assert.True(EditorOperations.TryGet("fonts.installStandard", out EditorOperation install));
        Assert.NotNull(install.AsyncHandler);

        var context = new AutomationContext { ViewModel = WithText("hi", "Helvetica") };
        object? result = EditorOperations.Invoke(context, "fonts.list", default);

        Assert.NotNull(result);
        string json = JsonSerializer.Serialize(result);
        Assert.Contains("Helvetica", json, StringComparison.Ordinal);
        Assert.Contains("missingStandardFonts", json, StringComparison.Ordinal);
    }
}
