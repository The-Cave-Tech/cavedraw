using System.Text.Json;
using VCCad.App.Automation;
using VCCad.App.Fonts;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// Telling the person when a font had to be substituted.
///
/// A PDF need not embed its fonts. When it does not, every viewer renders a
/// substitute — but the substitute is a different design, so the person has to be
/// told rather than left to notice the letterforms are wrong.
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
            FontFamily = "DejaVu Sans",
            SourceFont = sourceFont,
        });
        layer.AddItem(item);
        return viewModel;
    }

    [Fact]
    public void ADocumentWithNoTextHasNothingToWarnAbout()
    {
        Assert.Null(FontUsage.Warning(new CadDocument()));
    }

    [Fact]
    public void ASubstitutedFontIsNamedAsTheDocumentAskedForIt()
    {
        // The run carries the substitute's family for rendering, but the warning must
        // name the font the file asked for — "DejaVu Sans is not embedded" would be
        // true and useless.
        EditorViewModel viewModel = WithText("DO NOT REPRODUCE", "Helvetica-Bold");

        string? warning = FontUsage.Warning(viewModel.Document);

        Assert.NotNull(warning);
        Assert.Contains("Helvetica-Bold", warning!, StringComparison.Ordinal);
        Assert.DoesNotContain("DejaVu", warning!, StringComparison.Ordinal);
    }

    [Fact]
    public void EverySubstitutedFontIsListedOnce()
    {
        var document = new CadDocument { Name = "t" };
        Layer layer = document.AddArtboard(new Size2D(200, 200), "Page 1").AddLayer("Art");
        foreach ((string text, string font) in new[]
                 {
                     ("a", "Helvetica"), ("b", "Helvetica"), ("c", "Helvetica-Bold"), ("d", "Times-Roman"),
                 })
        {
            var item = new TextItem { Origin = new Point2D(0, 0) };
            item.Runs.Add(new TextRun { Text = text, SourceFont = font });
            layer.AddItem(item);
        }

        string? warning = FontUsage.Warning(document);

        Assert.NotNull(warning);
        Assert.Contains("3 fonts", warning!, StringComparison.Ordinal);
        Assert.Contains("Helvetica", warning!, StringComparison.Ordinal);
        Assert.Contains("Times-Roman", warning!, StringComparison.Ordinal);

        IReadOnlyList<FontUsageEntry> report = FontUsage.Report(document);
        Assert.Equal(3, report.Count);
        Assert.All(report, f => Assert.False(f.Embedded));
    }

    [Fact]
    public void TheFontReportIsPartOfTheSurface()
    {
        Assert.True(EditorOperations.TryGet("fonts.list", out _));

        var context = new AutomationContext { ViewModel = WithText("hi", "Helvetica") };
        object? result = EditorOperations.Invoke(context, "fonts.list", default);

        Assert.NotNull(result);
        string json = JsonSerializer.Serialize(result);
        Assert.Contains("Helvetica", json, StringComparison.Ordinal);
        Assert.Contains("\"embedded\":false", json, StringComparison.Ordinal);
    }

    [Fact]
    public void TheReportNamesTheFontTheDocumentAskedFor()
    {
        EditorViewModel viewModel = WithText("DO NOT REPRODUCE", "Helvetica-Bold");

        IReadOnlyList<FontUsageEntry> report = FontUsage.Report(viewModel.Document);

        FontUsageEntry entry = Assert.Single(report);
        Assert.Equal("Helvetica-Bold", entry.BaseFont);
        Assert.False(entry.Embedded);
    }

    [Fact]
    public void AStandardFontIsDescribedAsComingFromThisMachineOrAsMissing()
    {
        TextRun run = WithText("x", "Helvetica-Bold").Document.Artboards[0].Layers[0]
            .Children.OfType<TextItem>().First().Runs[0];

        string described = StandardFontResolver.Describe(run);

        // Either a real face was found (URW or a metric-compatible clone), or the report says
        // plainly that nothing on this machine can supply it. It must never pretend to a face
        // it does not have.
        bool found = described.Contains("this machine", StringComparison.Ordinal);
        bool missing = described.Contains("unavailable", StringComparison.Ordinal);
        Assert.True(found || missing, $"unexpected description: {described}");
    }

    [Fact]
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
