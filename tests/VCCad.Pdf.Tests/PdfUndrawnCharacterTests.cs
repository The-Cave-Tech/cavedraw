using VCCad.Core.Model;
using VCCad.Pdf;
using Xunit;

namespace VCCad.Pdf.Tests;

/// <summary>
/// **Text the resolved face cannot draw is declared, not silently dropped.**
///
/// `WriteText` skips a character whose glyph id is 0 (`if (gid == 0) continue;`). For a script the chosen face does
/// not cover - Arabic through a metric-compatible Latin clone, for instance - every character is skipped, no show
/// operation is emitted, and the page comes out **blank**. The canvas draws the same document fine, because Skia
/// shapes and falls back; the page draws nothing, and until now nothing said so.
///
/// That is the rule this repository already states for fonts: a font that cannot be supplied "is reported, never
/// silent". A face that resolves but cannot draw the characters is the same kind of loss.
///
/// The assertion is a **pair** - the loss is named for the run that has it, and no such note appears for a run that
/// draws completely.
/// </summary>
public class PdfUndrawnCharacterTests
{
    private static IReadOnlyList<string> NotesFor(string family, string content)
    {
        CadDocument document = CadDocument.CreateDefault("Text");
        var text = new TextItem { Name = "Text", Origin = new VCCad.Geometry.Point2D(60, 80) };
        text.Runs.Add(new TextRun { Text = content, FontFamily = family, FontSize = 48 });
        document.Artboards[0].Layers[0].AddItem(text);

        PdfDocumentExporter.Export(document, out IReadOnlyList<string> notes);
        return notes;
    }

    /// <summary>The acceptance: the characters that vanish are counted and named, with the face that could not draw them.</summary>
    [Fact]
    public void ARunTheFaceCannotDrawIsReported()
    {
        // Devanagari - a script no face the project can supply covers, so the loss is genuinely unrepairable.
        IReadOnlyList<string> notes = NotesFor("Arial", "\u0928\u092e\u0938\u094d\u0924\u0947");

        string? declared = notes.FirstOrDefault(
            note => note.Contains("have no glyph", StringComparison.Ordinal));

        Assert.True(declared is not null,
            $"the loss must be declared; the notes were: [{string.Join(" | ", notes)}]");

        Assert.Contains("6 of 6", declared, StringComparison.Ordinal);
        Assert.Contains("Arial", declared!, StringComparison.Ordinal);
    }

    /// <summary>
    /// **The control.** A run that draws completely produces no such note, so the declaration marks a real loss
    /// rather than firing for every document.
    /// </summary>
    [Fact]
    public void ARunThatDrawsCompletelyIsNotReported()
    {
        IReadOnlyList<string> notes = NotesFor("Arial", "SALAAM");

        Assert.DoesNotContain(notes, note => note.Contains("have no glyph", StringComparison.Ordinal));
    }
}
