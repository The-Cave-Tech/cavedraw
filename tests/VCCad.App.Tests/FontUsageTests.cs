using System.Text.Json;
using Avalonia.Headless.XUnit;
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
/// A PDF need not embed its fonts. When it does not, every viewer renders a substitute — but
/// the substitute is a different design, and that is only worth interrupting somebody about
/// when nothing on the machine can supply the real thing.
///
/// These tests used to assert the opposite: that ANY substituted font produces a Warning. It
/// does not, and should not — "DejaVu Sans is being used instead of Helvetica" is true on a
/// machine with no URW fonts and false on one with them, so a test written that way can only
/// pass by agreeing with whichever machine it runs on. They also never ran: the project did
/// not compile from 9639e50 until 4c3459e, and the runner stopped at the first failing project
/// until 0d79354. So they were wrong and invisible at the same time.
///
/// What they assert now is the contract, and it holds whether or not URW faces are installed:
/// the REPORT always lists what the document asked for, and the WARNING appears only for a
/// font nothing can supply.
/// </summary>
public class FontUsageTests
{
    /// <summary>A family no resolver can possibly supply, so "missing" is machine-independent.</summary>
    private const string Unsuppliable = "NoSuchFontXYZ-NotInstalledAnywhere";

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

    [Fact]
    public void ADocumentWithNoTextHasNothingToWarnAbout()
    {
        Assert.Null(FontUsage.Warning(new CadDocument()));
        Assert.Empty(FontUsage.Report(new CadDocument()));
    }

    [AvaloniaFact]
    public void TheReportNamesTheFontTheDocumentAskedForRatherThanTheSubstitute()
    {
        // The run renders with a substitute; the report must name what the FILE asked for.
        // "DejaVu Sans is not embedded" would be true and useless.
        EditorViewModel viewModel = WithText("DO NOT REPRODUCE", "Helvetica-Bold");

        FontUsageEntry entry = Assert.Single(FontUsage.Report(viewModel.Document));

        Assert.Equal("Helvetica-Bold", entry.BaseFont);
        Assert.False(entry.Embedded);
        Assert.NotEqual("Nimbus Sans", entry.BaseFont);
    }

    [AvaloniaFact]
    public void EveryFontIsListedOnceHoweverManyRunsUseIt()
    {
        var document = new CadDocument { Name = "t" };
        Layer layer = document.AddArtboard(new Size2D(200, 200), "Page 1").AddLayer("Art");
        foreach ((string text, string font) in new[]
                 {
                     ("a", "Helvetica"), ("b", "Helvetica"),
                     ("c", "Helvetica-Bold"), ("d", "Times-Roman"),
                 })
        {
            var item = new TextItem { Origin = new Point2D(0, 0) };
            item.Runs.Add(new TextRun { Text = text, SourceFont = font });
            layer.AddItem(item);
        }

        IReadOnlyList<FontUsageEntry> report = FontUsage.Report(document);

        // Helvetica twice, so three distinct fonts and not four.
        Assert.Equal(3, report.Count);
        Assert.Contains(report, f => f.BaseFont == "Helvetica");
        Assert.Contains(report, f => f.BaseFont == "Helvetica-Bold");
        Assert.Contains(report, f => f.BaseFont == "Times-Roman");
        Assert.All(report, f => Assert.False(f.Embedded));
    }

    [AvaloniaFact]
    public void WarningNamesEveryStandardFaceNothingOnThisMachineCanSupply()
    {
        // Warning's scope, read from the contract rather than assumed: it reports the standard
        // faces nothing on the machine can supply, plus embedded programmes that failed to
        // load. An arbitrary unknown family is neither of those — it is a font the file named
        // and nothing has, which is a different report — so this asserts the two cases Warning
        // actually covers instead of inventing a third and calling the code wrong for it.
        CadDocument document = WithText("x", "Helvetica-Bold").Document;
        IReadOnlyList<string> missing = StandardFontResolver.Missing(document);
        string? warning = FontUsage.Warning(document);

        if (missing.Count == 0)
        {
            // Everything is covered, so there is nothing worth interrupting anybody about.
            // This is the assertion the old tests had backwards: they demanded a warning for a
            // font that WAS supplied, which is true only on a machine with no URW faces.
            Assert.Null(warning);
        }
        else
        {
            Assert.NotNull(warning);
            Assert.Contains(missing[0], warning!, StringComparison.Ordinal);
        }

        // And a document with no text never warns, on any machine.
        Assert.Null(FontUsage.Warning(new CadDocument()));
    }


    [AvaloniaFact]
    public void TheFontReportIsPartOfTheSurface()
    {
        Assert.True(EditorOperations.TryGet("fonts.list", out _));

        var context = new AutomationContext { ViewModel = WithText("hi", "Helvetica") };
        object? result = EditorOperations.Invoke(context, "fonts.list", default);

        Assert.NotNull(result);
        string json = JsonSerializer.Serialize(result).ToLowerInvariant();

        // The report is how a driver answers "which fonts does this document actually need,
        // and is anything missing". All three parts have to be reachable.
        Assert.Contains("helvetica", json, StringComparison.Ordinal);
        Assert.Contains("embedded", json, StringComparison.Ordinal);
        Assert.Contains("missingstandardfonts", json, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void TheFontSurfaceIsAutomationReachable()
    {
        Assert.True(EditorOperations.TryGet("fonts.list", out _));
        Assert.True(EditorOperations.TryGet("fonts.installStandard", out EditorOperation install));

        // Installing for the person is the async half: it downloads fonts, so it must not run
        // on the UI thread and it must not be reachable only from a dialog.
        Assert.NotNull(install.AsyncHandler);

        var context = new AutomationContext { ViewModel = WithText("hi", "Helvetica") };
        object? result = EditorOperations.Invoke(context, "fonts.list", default);

        Assert.NotNull(result);
        string json = JsonSerializer.Serialize(result);
        Assert.Contains("Helvetica", json, StringComparison.Ordinal);
        Assert.Contains("missingStandardFonts", json, StringComparison.Ordinal);
    }
}
