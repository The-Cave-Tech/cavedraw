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

    /// <summary>The honest cases: a blur is not written and an outline effect is, which is what the warning says.</summary>
    [Fact]
    public void TheLossyListNamesTheEffectsThatAreNotWritten()
    {
        var context = new AutomationContext { ViewModel = new EditorViewModel() };

        JsonElement reported = JsonSerializer.SerializeToElement(
            EditorOperations.Invoke(context, "document.exportSupport", default));

        string[] lossy = reported.GetProperty("lossy").EnumerateArray()
            .Select(e => e.GetString()!).ToArray();

        Assert.Contains("rasterEffect", lossy);
        Assert.Contains("filter", lossy);
        Assert.DoesNotContain("outlineEffect", lossy);
    }
}
