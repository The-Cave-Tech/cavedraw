using VCCad.Core.Model;
using VCCad.Pdf;
using Xunit;

namespace VCCad.Pdf.Tests;

/// <summary>
/// **A run is drawn when a face in the project can draw it.**
///
/// The embedder resolved every run through the standard-font chain — the URW Core 35 faces, which cover Latin, Greek
/// and Cyrillic and nothing else. A document in any other script therefore lost every character of its runs, silently,
/// into a blank page; `8274395` made that loss *declared*, and this makes it *not happen* where the project already
/// bundles a face with the coverage.
///
/// Measured before this: exporting `سلام` through seven families reported the loss for all seven, and
/// `BundledFonts.Resolve("DejaVu Sans")` maps all four letters (`seen=1377 lam=1389 alef=1365 meem=1390`) while
/// DejaVu **Serif** maps none. So the pair below is the real test: a family with coverage is drawn, one without is
/// still declared lost.
/// </summary>
public class PdfNonLatinCoverageTests
{
    private static IReadOnlyList<string> ExportNotes(string content)
    {
        CadDocument document = CadDocument.CreateDefault("Script");
        var text = new TextItem { Name = "Text", Origin = new VCCad.Geometry.Point2D(60, 80) };
        text.Runs.Add(new TextRun { Text = content, FontFamily = "DejaVu Sans", FontSize = 48 });
        document.Artboards[0].Layers[0].AddItem(text);

        PdfDocumentExporter.Export(document, out IReadOnlyList<string> notes);
        return notes;
    }

    /// <summary>The acceptance: an Arabic run is not reported as lost, because a face that draws it was found.</summary>
    [Fact]
    public void ArabicIsDrawnThroughABundledFace()
    {
        IReadOnlyList<string> notes = ExportNotes("\u0633\u0644\u0627\u0645");

        Assert.DoesNotContain(notes, note => note.Contains("have no glyph", StringComparison.Ordinal));
    }

    /// <summary>
    /// **The control.** A script no bundled face covers is still declared, so the fallback marks a real capability
    /// rather than reporting success for anything.
    /// </summary>
    [Fact]
    public void AScriptNoBundledFaceCoversIsStillDeclared()
    {
        // Devanagari - DejaVu has no coverage for it here.
        IReadOnlyList<string> notes = ExportNotes("\u0928\u092e\u0938\u094d\u0924\u0947");

        Assert.Contains(notes, note => note.Contains("have no glyph", StringComparison.Ordinal));
    }
}
