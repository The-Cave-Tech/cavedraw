using VCCad.Core.Model;
using VCCad.Pdf;
using VCCad.Pdf.Fonts;
using Xunit;

namespace VCCad.Pdf.Tests;

/// <summary>
/// **Text the resolved face cannot draw is declared, not silently dropped.**
///
/// `WriteText` skips a character whose glyph id is 0 (`if (gid == 0) continue;`). For text the chosen face cannot
/// draw, every character is skipped, no show operation is emitted, and the page comes out **blank** unless the loss
/// is declared. The canvas draws the same document, because Skia shapes and falls back; the page drew nothing, and
/// nothing said so. That is the rule this repository already states for fonts: a font that cannot be supplied "is
/// reported, never silent".
///
/// **The assertion is environment-aware on purpose, and that is not a weakening.** Two earlier versions named a
/// script as "uncoverable" and were wrong on a machine that covered it - Devanagari is covered on Windows (Nirmala
/// UI), and a font here maps even the Private Use Area. What is true everywhere is the **relationship**: the loss is
/// declared exactly when no face on this machine can draw the text. So the test asks the same lookup the exporter
/// asks and asserts the two agree - which still fails if the declaration stops happening on a machine that needs it,
/// and cannot pass by pretending.
/// </summary>
public class PdfUndrawnCharacterTests
{
    /// <summary>Unicode noncharacters: the least likely thing for any face to map.</summary>
    private const string Undrawable = "\uFDD0\uFDD1\uFDD2\uFDD3";

    private static List<int> CodePointsOf(string content) => content.Select(c => (int)c).ToList();

    private static IReadOnlyList<string> NotesFor(string family, string content)
    {
        CadDocument document = CadDocument.CreateDefault("Text");
        var text = new TextItem { Name = "Text", Origin = new VCCad.Geometry.Point2D(60, 80) };
        text.Runs.Add(new TextRun { Text = content, FontFamily = family, FontSize = 48 });
        document.Artboards[0].Layers[0].AddItem(text);

        PdfDocumentExporter.Export(document, out IReadOnlyList<string> notes);
        return notes;
    }

    private static bool Declared(IReadOnlyList<string> notes)
        => notes.Any(note => note.Contains("have no glyph", StringComparison.Ordinal));

    /// <summary>
    /// **The acceptance.** Text no face can draw is declared, and text no face drew is named - decided by asking the
    /// machine, so the same assertion holds on a developer's laptop and on a bare container.
    /// </summary>
    [Fact]
    public void TextNoFaceCanDrawIsDeclared()
    {
        bool coverable = StandardFontFiles.TryFindCovering(CodePointsOf(Undrawable)) is not null;
        IReadOnlyList<string> notes = NotesFor("Arial", Undrawable);

        Assert.Equal(!coverable, Declared(notes));

        string? declared = notes.FirstOrDefault(n => n.Contains("have no glyph", StringComparison.Ordinal));
        if (declared is not null)
        {
            Assert.Contains("Arial", declared, StringComparison.Ordinal);
        }
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
