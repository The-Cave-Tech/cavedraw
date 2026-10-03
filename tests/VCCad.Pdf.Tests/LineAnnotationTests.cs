using System.Text;
using VCCad.Core.Model;
using Xunit;

namespace VCCad.Pdf.Tests;

/// <summary>
/// **A PDF line annotation carries arrowheads, and the import does not read them** (issue #202).
///
/// This is a **sentinel**, the kind this repository uses to record a known gap: it asserts what happens today, not
/// what should. When the annotation is read, this test fails - and it must then be turned into a positive assertion,
/// not deleted.
///
/// **The gap.** SVG gives a path three marker slots and the reader fills them; PDF has no equivalent on a path, and
/// states an arrowhead as a **line ending** on a Line annotation (`/LE`, ISO 32000-1 §12.5.6.7: `/OpenArrow`,
/// `/ClosedArrow`, `/Diamond`, `/Square`, `/Circle`, `/None`). The vector import walks page content and never looks
/// at `/Annots`, so the line is either absent or a plain path and no arrowhead is drawn - while every viewer draws
/// one, because the file says so.
///
/// **What "resolved" means.** Each Line annotation becomes a path with `MarkerStart`/`MarkerEnd` naming a library
/// definition (the marker model is already in place, so this is the reading that is missing), with the styles the
/// file names and `/None` leaving a slot empty; the definition draws an arrowhead of that style; and a file with no
/// annotations imports exactly as it does today.
/// </summary>
public class LineAnnotationTests
{
    /// <summary>
    /// A one-page file whose only content is a line, plus a Line annotation across the same two points carrying
    /// `/LE [/OpenArrow /OpenArrow]`. Written by hand rather than exported, because the exporter does not write
    /// annotations either - which is the other half of this gap.
    /// </summary>
    private static byte[] Pdf()
    {
        var text = new StringBuilder();
        var offsets = new List<int>();
        text.Append("%PDF-1.7\n");

        void Object(string body)
        {
            offsets.Add(Encoding.Latin1.GetByteCount(text.ToString()));
            text.Append(body).Append('\n');
        }

        const string content = "0 0 0 RG 2 w\n20 40 m 120 40 l S\n";

        Object("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj");
        Object("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj");
        Object("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 200] /Contents 4 0 R " +
               "/Annots [5 0 R] >>\nendobj");
        Object($"4 0 obj\n<< /Length {Encoding.Latin1.GetByteCount(content)} >>\nstream\n{content}endstream\nendobj");
        Object("5 0 obj\n<< /Type /Annot /Subtype /Line /Rect [20 40 280 160] /L [20 40 280 160] " +
               "/LE [/OpenArrow /OpenArrow] /C [0 0 0] /Border [0 0 0] /F 4 >>\nendobj");

        int xref = Encoding.Latin1.GetByteCount(text.ToString());
        text.Append("xref\n0 6\n0000000000 65535 f \n");
        foreach (int offset in offsets)
        {
            text.Append(offset.ToString("D10")).Append(" 00000 n \n");
        }

        text.Append("trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(text.ToString());
    }

    [Fact]
    public void TheFixtureIsAValidFileOurReaderCanOpen()
    {
        CadDocument document = PdfImporter.Import(Pdf());

        // The page and its content are read: whatever is missing is the annotation, not the file.
        Assert.NotEmpty(document.Artboards);
        Assert.NotEmpty(document.AllPaths());
    }

    /// <summary>
    /// Writes the fixture and our re-export of it, so both can be rendered **by the same engine** and compared: the
    /// file's arrows against the import's silence. Runs only when <c>VCCAD_CAPTURE</c> names a directory.
    /// </summary>
    [Fact]
    public void CaptureTheFileAndOurExport()
    {
        if (Environment.GetEnvironmentVariable("VCCAD_CAPTURE") is not { Length: > 0 } directory)
        {
            return;
        }

        Directory.CreateDirectory(directory);
        byte[] source = Pdf();
        File.WriteAllBytes(Path.Combine(directory, "source.pdf"), source);
        File.WriteAllBytes(
            Path.Combine(directory, "roundtrip.pdf"),
            PdfDocumentExporter.Export(PdfImporter.Import(source)));
    }

    /// <summary>
    /// **The gap, pinned.** The annotation states two open arrowheads; nothing in the document names them. When this
    /// starts failing, the annotation is being read - turn the assertions over.
    /// </summary>
    [Fact]
    public void TheAnnotationsArrowheadsAreNotReadYet()
    {
        CadDocument document = PdfImporter.Import(Pdf());

        Assert.DoesNotContain(
            document.AllPaths(),
            path => path.MarkerStart is not null || path.MarkerEnd is not null);

        Assert.DoesNotContain(
            document.Definitions.Children.OfType<ArtGroup>(),
            definition => definition.Name.Contains("Arrow", StringComparison.OrdinalIgnoreCase));
    }
}
