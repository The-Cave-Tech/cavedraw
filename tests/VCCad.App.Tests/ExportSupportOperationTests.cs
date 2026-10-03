using System.Text.Json;
using VCCad.App.Automation;
using VCCad.App.ViewModels;
using VCCad.Pdf;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// `document.exportSupport` - the list a warning is drawn from, reachable by a driver as well as by a panel.
/// </summary>
public class ExportSupportOperationTests
{
    [Fact]
    public void TheOperationReportsWhatTheExportCarries()
    {
        var context = new AutomationContext { ViewModel = new EditorViewModel() };

        JsonElement reported = JsonSerializer.SerializeToElement(
            EditorOperations.Invoke(context, "document.exportSupport", default));

        Assert.Equal(PdfExportSupport.All.Count, reported.GetProperty("carries").GetArrayLength());

        // And it is the same list, not a copy that could drift from it.
        string[] lossy = reported.GetProperty("lossy").EnumerateArray()
            .Select(e => e.GetString()!).OrderBy(n => n).ToArray();

        Assert.Equal(PdfExportSupport.Lossy.Select(f => f.Name).OrderBy(n => n), lossy);
    }

    /// <summary>The honest case: a **group's** blend is still not carried, because PDF composites it with an
    /// isolated transparency group this exporter cannot emit. An outline effect, a filter, a stroke's raster effect
    /// and a leaf item's or a stroke's blend are all written now.</summary>
    /// <summary>
    /// **Nothing is declared unwritten any more, and the operation says so.** The last entry was a group's blend,
    /// which needed an isolated transparency group; the exporter writes one now, so the list a person reads is empty.
    /// The assertions are the individual features rather than only the count, so a feature that quietly stops being
    /// written shows up here as itself instead of as a list that grew by one.
    /// </summary>
    [Fact]
    public void TheLossyListIsEmptyBecauseEveryDeclaredFeatureIsWritten()
    {
        var context = new AutomationContext { ViewModel = new EditorViewModel() };

        JsonElement reported = JsonSerializer.SerializeToElement(
            EditorOperations.Invoke(context, "document.exportSupport", default));

        string[] lossy = reported.GetProperty("lossy").EnumerateArray()
            .Select(e => e.GetString()!).ToArray();

        Assert.Empty(lossy);
        Assert.DoesNotContain("blendMode", lossy);
        Assert.DoesNotContain("blendModeGroup", lossy);
        Assert.DoesNotContain("filter", lossy);
        Assert.DoesNotContain("rasterEffect", lossy);
        Assert.DoesNotContain("outlineEffect", lossy);

        // And the declaration behind the list agrees with it, so the two cannot drift apart.
        Assert.Empty(PdfExportSupport.Lossy);
    }
}
