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
    [Fact]
    public void TheLossyListNamesTheEffectsThatAreNotWritten()
    {
        var context = new AutomationContext { ViewModel = new EditorViewModel() };

        JsonElement reported = JsonSerializer.SerializeToElement(
            EditorOperations.Invoke(context, "document.exportSupport", default));

        string[] lossy = reported.GetProperty("lossy").EnumerateArray()
            .Select(e => e.GetString()!).ToArray();

        Assert.DoesNotContain("blendMode", lossy);
        Assert.Contains("blendModeGroup", lossy);
        // The PDF writes a filter now, as an image XObject, so it is no longer in the lossy list...
        Assert.DoesNotContain("filter", lossy);
        // ...and a stroke's raster effect is rasterised and placed the same way.
        Assert.DoesNotContain("rasterEffect", lossy);
        Assert.DoesNotContain("outlineEffect", lossy);
    }
}
