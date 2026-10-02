using VCCad.Core.Model;
using VCCad.Pdf;
using VCCad.Pdf.Fonts;
using Xunit;

namespace VCCad.Pdf.Tests;

/// <summary>
/// **A run is drawn when this machine has a face that can draw it, and declared when it does not.**
///
/// The embedder resolves through the standard-font chain - the URW Core 35 faces, which cover Latin, Greek and
/// Cyrillic and nothing else - so a document in another script lost every character of its runs. `8274395` made that
/// loss **declared**; the lookup added with this test makes it **not happen** where a face on this machine covers the
/// text. Nothing is bundled: the face is read from where it already lives, the rule that applies to Helvetica too.
///
/// The assertion is written to be honest in **both** environments rather than to pass everywhere. A container with
/// only the URW set has no Arabic face, and there the correct behaviour is the declared loss; this machine has one,
/// and there the correct behaviour is drawn text. Which branch runs is decided by asking the same lookup the
/// exporter asks, so the test cannot pass by pretending - **if the wiring breaks, the branch that expects drawing
/// fails on the machine that has the face.**
/// </summary>
public class PdfMachineCoverageTests
{
    /// <summary>"salaam" - seen, lam, alef, meem.</summary>
    private static readonly int[] Arabic = { 0x0633, 0x0644, 0x0627, 0x0645 };

    private static IReadOnlyList<string> ExportNotes(string content)
    {
        CadDocument document = CadDocument.CreateDefault("Script");
        var text = new TextItem { Name = "Text", Origin = new VCCad.Geometry.Point2D(60, 80) };
        text.Runs.Add(new TextRun { Text = content, FontFamily = "Helvetica", FontSize = 48 });
        document.Artboards[0].Layers[0].AddItem(text);

        PdfDocumentExporter.Export(document, out IReadOnlyList<string> notes);
        return notes;
    }

    /// <summary>
    /// **The acceptance.** The run is drawn exactly when a face on this machine covers its characters, and otherwise
    /// the loss is declared - never silently dropped.
    /// </summary>
    [Fact]
    public void ARunIsDrawnWhenThisMachineCoversItAndDeclaredOtherwise()
    {
        string? covering = StandardFontFiles.TryFindCovering(Arabic);
        IReadOnlyList<string> notes = ExportNotes("\u0633\u0644\u0627\u0645");
        bool declared = notes.Any(note => note.Contains("have no glyph", StringComparison.Ordinal));

        Assert.Equal(covering is null, declared);
    }
}
