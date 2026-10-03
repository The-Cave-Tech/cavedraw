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


        // **A joining script is drawn in its contextual forms** (issue #197): `سلام` is four characters and three
        // glyphs once lam-alef has formed its ligature, where picking one glyph per character gave four. The count is
        // the assertion because it is what "shaped" means here, and a per-character path cannot reach it.
        if (text == "\u0633\u0644\u0627\u0645")
        {
            Assert.Equal(3, show.Groups[1].Value.Length / 4);
        }

        // **A script that joins is declared**, because this export cannot shape it (issue #197): the characters are
        // drawn, and nothing says they are wrong. That is the difference a person has to be told about.
        // **Arabic and Hebrew are shaped now**, so nothing is declared for them; a script this export still cannot
        // shape is declared, and Syriac is one.
        bool shaped = text.Any(c => c is >= '\u0590' and <= '\u06FF');
        Assert.Equal(shaped, !notes.Any(note => note.Contains("does not shape", StringComparison.Ordinal)));

        // And the export does not claim to have lost the run it just drew.
        Assert.DoesNotContain(notes, note =>
            note.Contains(text, StringComparison.Ordinal) &&
            note.Contains("could not", StringComparison.OrdinalIgnoreCase));
    }
}