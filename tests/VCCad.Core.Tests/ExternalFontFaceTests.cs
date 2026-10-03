using VCCad.Core.Model;
using VCCad.Core.Svg;
using Xunit;

namespace VCCad.Core.Tests;

/// <summary>
/// **A referenced document's own font belongs to it** (issue #129's unverified half).
///
/// `url(...)` in a referenced file is relative to *that* file, and the element read through `use` is that document's,
/// so it is that document's face it means. Before this, the referencing document's faces were used instead - which
/// happens to be nothing for a referenced file that declares one, so its text was drawn with a substituted face and
/// the file's own glyphs were ignored.
///
/// The fixture is inline `<font>` rather than an OpenType programme: the point under test is *which document's*
/// faces are loaded, not how a face is read, and an inline font needs no binary. The assertion is the model: the
/// external document's text becomes its own glyph outlines instead of staying a `TextItem`.
/// </summary>
public class ExternalFontFaceTests
{
    private const string Library =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"100\" height=\"100\">" +
        "<defs><font id=\"inline\" horiz-adv-x=\"1000\">" +
        "<font-face font-family=\"Remote\" units-per-em=\"1000\"/>" +
        "<glyph unicode=\"a\" horiz-adv-x=\"600\" d=\"M 0 0 L 0 500 L 500 500 L 500 0 Z\"/>" +
        "</font></defs>" +
        "<g id=\"label\"><text x=\"0\" y=\"20\" font-size=\"10\" font-family=\"Remote\">a</text></g>" +
        "</svg>";

    private const string Main =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"200\" height=\"200\">" +
        "<use href=\"lib.svg#label\" x=\"10\" y=\"10\"/>" +
        "</svg>";

    [Fact]
    public void AnExternalDocumentsOwnFontDrawsItsText()
    {
        string directory = Path.Combine(Path.GetTempPath(), "vccad-external-font-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "lib.svg"), Library);
            string main = Path.Combine(directory, "main.svg");
            File.WriteAllText(main, Main);

            SvgImportResult result = SvgReader.ReadFile(main);

            // The referenced document's face was loaded and used: its text is its own glyph artwork, not a run this
            // machine would have to substitute a face for.
            Assert.Empty(result.Document.AllItems().OfType<TextItem>());
            Assert.Contains(
                result.Document.AllItems().OfType<PathItem>(),
                path => path.BoundingBox().Width > 0);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
