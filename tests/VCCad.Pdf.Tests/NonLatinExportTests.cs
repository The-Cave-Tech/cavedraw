using System.Text.RegularExpressions;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.Pdf.Tests;

/// <summary>
/// **A non-Latin run exports with its characters in the file** (issue #197).
///
/// The acceptance this issue names: the exported content stream carries a show operation whose encoded length is
/// non-zero, for a run in a script the URW standard chain cannot draw. Asserted on the **bytes**, not on a note
/// saying something was written.
/// </summary>
public class NonLatinExportTests
{
    private static CadDocument Document(string text)
    {
        CadDocument document = CadDocument.CreateDefault("Script");
        document.Artboards[0].Width = 300;
        document.Artboards[0].Height = 150;

        var item = new TextItem { Name = "t", Origin = new Point2D(20, 60) };
        item.Runs.Add(new TextRun { Text = text, FontSize = 24, FontFamily = "Arial" });
        document.Artboards[0].Layers[0].AddItem(item);
        return document;
    }

    [Theory]
    [InlineData("سلام")]          // Arabic
    [InlineData("שלום")]          // Hebrew
    [InlineData("Привет")]        // Cyrillic
    [InlineData("你好")]          // CJK
    public void ANonLatinRunReachesTheContentStream(string text)
    {
        byte[] pdf = PdfDocumentExporter.Export(Document(text), out IReadOnlyList<string> notes);

        string content = PdfDrawing.Of(pdf);

        // A show operation with a non-empty hex string, which is what "the characters are in the file" means.
        Match show = Regex.Match(content, @"<([0-9A-Fa-f]{4,})>\s*Tj");
        Assert.True(show.Success, $"no show operation with glyphs was written for '{text}'. Notes: " +
            string.Join(" | ", notes));

        // Two bytes per glyph in an Identity-H string, so the length is the glyph count.
        Assert.True(show.Groups[1].Value.Length / 4 >= 1, $"an empty show operation for '{text}'");

        // A capture, when one is wanted: the exported page, for a renderer to draw.
        if (Environment.GetEnvironmentVariable("VCCAD_CAPTURE") is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
            string name = string.Join("-", text.Select(c => ((int)c).ToString("X4")));
            File.WriteAllBytes(Path.Combine(directory, name + ".pdf"), pdf);
        }


        // **A script that joins is declared**, because this export cannot shape it (issue #197): the characters are
        // drawn, and nothing says they are wrong. That is the difference a person has to be told about.
        bool joins = text.Length > 0 && (text[0] >= '\u0590' && text[0] <= '\u07FF' ||
            text[0] >= '\uFB50' && text[0] <= '\uFEFF');

        if (joins)
        {
            Assert.Contains(notes, note => note.Contains("joined forms", StringComparison.Ordinal));
        }

        // And the export does not claim to have lost the run it just drew.
        Assert.DoesNotContain(notes, note =>
            note.Contains(text, StringComparison.Ordinal) &&
            note.Contains("could not", StringComparison.OrdinalIgnoreCase));
    }
}