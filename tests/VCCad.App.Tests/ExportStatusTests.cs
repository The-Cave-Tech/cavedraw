using VCCad.App.ViewModels;
using VCCad.Core.Model;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// **A declared loss reaches the person, or it is not declared.**
///
/// The exporter reports what it could not draw - text with no glyph in the face that resolved, for one - through
/// `Export(document, out notes)`. For a while nothing called that overload: `EditorViewModel.ExportPdf` used the
/// one without notes and set a status line saying the export succeeded, so a document could export a blank page and
/// a person would be told everything was fine. That is the defect shape this repository keeps naming - the data is
/// produced and the step that honours it never runs - sitting inside the fix for the same class of bug.
///
/// The assertion is a **pair**: a document that loses text says so, and one that draws completely says only that
/// it succeeded.
/// </summary>
public class ExportStatusTests
{
    /// <summary>The acceptance: an export that could not draw every character names the loss in the status.</summary>
    [Fact]
    public void AnExportThatCouldNotDrawEveryCharacterSaysSo()
    {
        var viewModel = new EditorViewModel();
        var text = new TextItem { Name = "Arabic", Origin = new VCCad.Geometry.Point2D(60, 80) };
        text.Runs.Add(new TextRun { Text = "\u0928\u092e\u0938\u094d\u0924\u0947", FontFamily = "Arial", FontSize = 48 });
        viewModel.Document.Artboards[0].Layers[0].AddItem(text);

        viewModel.ExportPdf();

        Assert.Contains("not drawn", viewModel.Status, StringComparison.Ordinal);
        Assert.Contains("no glyph", viewModel.Status, StringComparison.Ordinal);
    }

    /// <summary>
    /// **The control.** A document that draws completely reports only success, so the warning marks a real loss
    /// rather than appearing on every export.
    /// </summary>
    [Fact]
    public void AnExportThatDrewEverythingSaysOnlyThatItSucceeded()
    {
        var viewModel = new EditorViewModel();
        var text = new TextItem { Name = "Latin", Origin = new VCCad.Geometry.Point2D(60, 80) };
        text.Runs.Add(new TextRun { Text = "EXPORT", FontFamily = "Arial", FontSize = 48 });
        viewModel.Document.Artboards[0].Layers[0].AddItem(text);

        viewModel.ExportPdf();

        Assert.StartsWith("Exported", viewModel.Status, StringComparison.Ordinal);
        Assert.DoesNotContain("not drawn", viewModel.Status, StringComparison.Ordinal);
    }
}
