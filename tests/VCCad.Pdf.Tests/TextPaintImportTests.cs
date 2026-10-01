using System.Text;
using VCCad.Core.Model;
using VCCad.Pdf;
using Xunit;
using Xunit.Abstractions;

namespace VCCad.Pdf.Tests;

/// <summary>
/// **How a PDF says text is painted, when the statement is a pattern or a fill alpha.**
///
/// Two ways the file states the text's paint were dropped on import and replaced by a plausible colour:
///
/// <list type="number">
/// <item>A <c>/Pattern</c> fill space (text painted with a tiling pattern, or with a shading pattern held
/// on a PatternType 2 dictionary) resolved through <see cref="PdfContentImporter"/>'s colour resolver to
/// <see cref="ColorRgb.Black"/>, so the text arrived solid black with nothing said.</item>
/// <item>The <c>ca</c> fill alpha an ExtGState states never reached the text colour at all, so text the
/// file draws semi-transparent arrived opaque.</item>
/// </list>
///
/// Both are the silent-fallback family (#140, #143, #144, #150, #151, #152, #155, #158, #160, #162, #166,
/// #175): a stated value replaced by a default with nothing reported. The project rule is that a value the
/// importer cannot honour is **reported**, never substituted, and for paint it matters more than for
/// geometry - black text on a black background cannot be told from text that is not there.
///
/// Built the way the other content-importer tests build a file (see <see cref="TextRunColourImportTests"/>):
/// a small hand-written one-page PDF, so the document under test is exactly the construct.
/// </summary>
public class TextPaintImportTests
{
    private readonly ITestOutputHelper _out;

    public TextPaintImportTests(ITestOutputHelper output) => _out = output;

    /// <summary>
    /// **Text painted with a tiling pattern is reported, not silently black.**
    ///
    /// The pattern is an *uncoloured* tiling pattern - the file names the underlying DeviceRGB space and
    /// then gives components that paint the tile - which is the shape that reaches
    /// <c>ResolveColor</c>'s <c>case "Pattern"</c> and comes back <see cref="ColorRgb.Black"/> whatever the
    /// components say. The model's text colour is a single <see cref="ColorRgb"/> and has nowhere to put a
    /// pattern tile, so the honest outcome is that the substitution is stated.
    ///
    /// Against the importer before the fix this fails on an empty note list: the text simply arrives black
    /// and nothing mentions the pattern.
    /// </summary>
    [Fact]
    public void TextPaintedWithATilingPatternIsReportedRatherThanSilentlyBlack()
    {
        // [/Pattern /DeviceRGB] cs 0.2 0.6 0.9 /P1 scn - an uncoloured tiling pattern whose tile is painted
        // in the given DeviceRGB components. The text is then filled with whatever that pattern draws.
        CadDocument document = PdfImporter.Import(
            Pdf(
                "[/Pattern /DeviceRGB] cs 0.2 0.6 0.9 /P1 scn " +
                "BT /F1 12 Tf 72 400 Td (tiled) Tj ET",
                "/Pattern << /P1 6 0 R >>",
                TilingPattern),
            out IReadOnlyList<string> notes);

        TextItem text = Assert.Single(TextItems(document));
        Assert.Equal("tiled", Assert.Single(text.Runs).Text);

        // The pattern tile is not in the model, so the text is a solid colour - and the note is the whole
        // difference between an approximation and a silent one.
        Assert.Contains(notes, note =>
            note.Contains("pattern /P1", StringComparison.Ordinal) &&
            note.Contains("solid", StringComparison.OrdinalIgnoreCase));
        _out.WriteLine($"note: {Assert.Single(notes)}");
    }

    /// <summary>
    /// **Text painted with an axial (shading) pattern is reported too.**
    ///
    /// A PatternType 2 pattern holds a shading dictionary - here <c>/ShadingType 2</c>, an axial ramp - and
    /// fills with it. <see cref="GradientSpec"/> exists on <see cref="PathItem.Fill"/>, but a
    /// <see cref="TextItem"/>'s paint is a <see cref="ColorRgb"/>, so the ramp cannot be carried on the
    /// text. It must be named in the report rather than becoming black.
    ///
    /// Before the fix the note list is empty.
    /// </summary>
    [Fact]
    public void TextPaintedWithAnAxialShadingPatternIsReported()
    {
        CadDocument document = PdfImporter.Import(
            Pdf(
                "/Pattern cs /P1 scn BT /F1 12 Tf 72 400 Td (shaded) Tj ET",
                "/Pattern << /P1 6 0 R >>",
                ShadingPattern,
                AxialShading,
                IdentityFunction),
            out IReadOnlyList<string> notes);

        TextItem text = Assert.Single(TextItems(document));
        Assert.Equal("shaded", Assert.Single(text.Runs).Text);

        Assert.Contains(notes, note =>
            note.Contains("pattern /P1", StringComparison.Ordinal) &&
            note.Contains("axial", StringComparison.OrdinalIgnoreCase));
        _out.WriteLine($"note: {Assert.Single(notes)}");
    }

    /// <summary>
    /// **The <c>ca</c> in an ExtGState is the text's fill alpha, and it reaches the text paint.**
    ///
    /// <c>ca</c> is the non-stroking alpha of the graphics state, and text is filled, so it applies to the
    /// text's fill exactly as it does to a path's. The importer read it into <c>fillAlpha</c> and then used
    /// it only when building a <see cref="PathItem"/>, so the text arrived opaque.
    ///
    /// <see cref="ColorRgb"/> carries the alpha and the exporter already writes it back as <c>ca</c>
    /// (<c>PdfDocumentExporter</c> writes <c>colour.A</c> before each run), so holding it on the text
    /// colour is the whole round trip. Before the fix this fails with 1 against 0.5.
    /// </summary>
    [Fact]
    public void TextFillAlphaReachesTheTextPaint()
    {
        CadDocument document = PdfImporter.Import(
            Pdf(
                "/GS1 gs BT /F1 12 Tf 1 0 0 rg 72 400 Td (semi) Tj ET",
                "/ExtGState << /GS1 6 0 R >>",
                "<< /Type /ExtGState /ca 0.5 >>"),
            out _);

        TextItem text = Assert.Single(TextItems(document));
        ColorRgb paint = text.ColourOf(Assert.Single(text.Runs));

        Assert.Equal(0.5, paint.A, 3);
        Assert.Equal(1.0, paint.R, 3);
        Assert.Equal(0.0, paint.G, 3);
        Assert.Equal(0.0, paint.B, 3);
    }

    /// <summary>
    /// **A full fill alpha changes nothing and reports nothing.** The guard on the fix: only a stated
    /// transparency moves the paint, so an ordinary opaque document keeps the colour it had and grows no
    /// run-level colour and no note.
    /// </summary>
    [Fact]
    public void TextWithFullFillAlphaStaysOpaqueAndIsNotReported()
    {
        CadDocument document = PdfImporter.Import(
            Pdf(
                "/GS1 gs BT /F1 12 Tf 1 0 0 rg 72 400 Td (opaque) Tj ET",
                "/ExtGState << /GS1 6 0 R >>",
                "<< /Type /ExtGState /ca 1 >>"),
            out IReadOnlyList<string> notes);

        TextItem text = Assert.Single(TextItems(document));
        Assert.Equal(1.0, text.ColourOf(Assert.Single(text.Runs)).A, 3);
        Assert.Empty(notes);
    }

    // ------------------------------------------------------------------
    // helpers
    // ------------------------------------------------------------------

    /// <summary>The text blocks of a page, wherever the importer's grouping put them.</summary>
    private static List<TextItem> TextItems(CadDocument document)
        => Imported.Page(document).OfType<TextItem>().ToList();

    /// <summary>The tile's own content stream: fill the cell in whatever colour the base space gave.</summary>
    private const string TilingTile = "0 0 8 8 re f";

    private static readonly string TilingPattern =
        "<< /Type /Pattern /PatternType 1 /PaintType 2 /TilingType 1 /BBox [0 0 8 8] " +
        "/XStep 8 /YStep 8 /Resources << >> /Length " + TilingTile.Length +
        " >>\nstream\n" + TilingTile + "\nendstream";

    private const string ShadingPattern =
        "<< /Type /Pattern /PatternType 2 /Shading 7 0 R /Matrix [1 0 0 1 0 0] >>";

    private const string AxialShading =
        "<< /ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 0 200 0] " +
        "/Function 8 0 R /Extend [true true] >>";

    private const string IdentityFunction =
        "<< /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [0 0 1] /N 1 >>";

    /// <summary>
    /// A one-page PDF whose only content is <paramref name="content"/>. Object 5 is a Helvetica font and
    /// <paramref name="extraObjects"/> follow it from object 6, so a resource string can name them - and
    /// <paramref name="resources"/> is spliced into the page's resource dictionary beside <c>/Font</c>.
    /// </summary>
    private static byte[] Pdf(string content, string resources, params string[] extraObjects)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
            $"/Resources << /Font << /F1 5 0 R >> {resources} >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /FirstChar 32 /LastChar 122 " +
            "/Widths [" + string.Join(" ", Enumerable.Repeat("556", 91)) + "] >>",
        };
        objects.AddRange(extraObjects);
        return Assemble(objects);
    }

    private static byte[] Assemble(List<string> objects)
    {
        var builder = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(builder.Length);
            builder.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        int xref = builder.Length;
        builder.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets)
        {
            builder.Append($"{offset:D10} 00000 n \n");
        }

        builder.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(builder.ToString());
    }
}
