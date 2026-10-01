using System.Text.RegularExpressions;
using VCCad.Core.Model;
using VCCad.Geometry;
using VCCad.Pdf;
using VCCad.Pdf.Fonts;
using Xunit;

namespace VCCad.Pdf.Tests;

/// <summary>
/// A block with more than one colour of run has to reach the page as more than one colour.
///
/// The model holds <see cref="TextRun.Color"/> and answers <see cref="TextItem.ColourOf"/>, but the exporter set one
/// colour per block - before the run loop - so a two-coloured line exported as two runs in the block's single
/// colour. The canvas had the same defect, and the two are asserted together because the invariant this repository
/// keeps breaking is that the canvas and the export agree about the same text; one without the other recreates it.
///
/// Asserted on the operators, not on the bytes: the export embeds the document's own JSON as a sidecar, so every
/// model value changes the file whether or not it draws anything. <see cref="PdfDrawing.Of"/> reads the streams that
/// draw, through the page tree.
///
/// Both of the exporter's text paths are covered. A substituted face writes one text object per run, and each has to
/// carry its own colour. A block whose runs all carry their original programme is written as a single text object -
/// so the colour change has to be written *inside* it, between the runs, rather than once for the block.
/// </summary>
public class TextColourExportTests
{
    private static readonly ColorRgb Red = new(1.0, 0.0, 0.0);

    private static readonly ColorRgb Blue = new(0.0, 0.0, 1.0);

    private const string RedOperator = "1 0 0 rg";

    private const string BlueOperator = "0 0 1 rg";

    /// <summary>A block of two runs: the first the block's red, the second its own blue.</summary>
    private static CadDocument TwoColourBlock(string family)
    {
        CadDocument document = CadDocument.CreateDefault("Two colours");
        var text = new TextItem { Name = "two-colour", Origin = new Point2D(100, 200), Color = Red };
        text.Runs.Add(new TextRun { Text = "HH", FontFamily = family, FontSize = 24, AdvanceWidth = 36 });
        text.Runs.Add(new TextRun { Text = "HH", FontFamily = family, FontSize = 24, Color = Blue });
        document.Artboards[0].Layers[0].AddItem(text);
        return document;
    }

    /// <summary>
    /// **Each run is written in its own colour.** Against the exporter this replaces, the whole block is set once in
    /// red: there is one colour operator per run and it is the block's for both - so the second text object's colour
    /// is red and the assertion below fails, which is the shape of the defect rather than a missing operator.
    /// </summary>
    [Fact]
    public void EachRunOfASubstitutedFaceIsWrittenInItsOwnColour()
    {
        if (!StandardFontFixture.Available) { return; }

        string content = PdfDrawing.Of(PdfDocumentExporter.Export(TwoColourBlock("Helvetica")));
        List<TextObject> objects = TextObjects(content);

        Assert.Equal(2, objects.Count);
        Assert.Equal(RedOperator, objects[0].Colour);
        Assert.Equal(BlueOperator, objects[1].Colour);
    }

    /// <summary>
    /// **The colour change is written inside the single text object the pass-through path builds.** That path exists
    /// so a letter-spaced heading is one text object and reads as one word, so the colour cannot be set once for the
    /// block: the change has to land between the two runs, after the first `TJ` and before the second.
    /// </summary>
    [Fact]
    public void TheColourChangeIsWrittenInsideASingleTextObject()
    {
        if (!StandardFontFixture.Available) { return; }

        byte[]? program = StandardFontFiles.TryReadProgram(new StandardFace(StandardFontKind.Sans, false, false));
        if (program is null) { return; }

        var font = new EmbeddedFont
        {
            Format = EmbeddedFontFormat.TrueType,
            Program = program,
            BaseFont = "VCCadTestSans",
            FamilyName = "VCCadTestSans",
            FirstChar = 32,
            Widths = Enumerable.Repeat(600.0, 95).ToArray(),
            Ascent = 800,
        };

        CadDocument document = TwoColourBlock("VCCadTestSans");
        TextItem text = document.Artboards[0].Layers[0].Children.OfType<TextItem>().Single();
        foreach (TextRun run in text.Runs)
        {
            run.EmbeddedFont = font;

            // A simple font's codes are one byte each, so one code per character - which is what the
            // pass-through emits verbatim.
            run.RawCodes = string.Concat(run.Text.Select(c => (char)(byte)c));
        }

        string content = PdfDrawing.Of(PdfDocumentExporter.Export(document));
        List<TextObject> objects = TextObjects(content);

        Assert.Single(objects);
        TextObject only = objects[0];
        Assert.Equal(RedOperator, only.Colour);

        int first = only.Body.IndexOf("] TJ", StringComparison.Ordinal);
        int second = only.Body.LastIndexOf("] TJ", StringComparison.Ordinal);
        int blue = only.Body.IndexOf(BlueOperator, StringComparison.Ordinal);

        Assert.True(first >= 0 && second > first, $"expected two runs in one text object.\n  {only.Body}");
        Assert.True(blue > first && blue < second,
            $"the second run's colour has to be set between the two runs.\n  {only.Body}");
    }

    /// <summary>One text object: the fill colour in force when it starts, and the operators inside it.</summary>
    private sealed record TextObject(string Colour, string Body);

    /// <summary>
    /// The text objects of a content stream, in order.
    ///
    /// The colour in force when an object starts is the **last colour operator written before its `BT`**, because a
    /// real stream sets the fill and then opens the text object. Pairing each object with its own colour is what
    /// makes this answer a fidelity question: a colour written once per block and one written per run are the same
    /// bytes unless the objects are looked at separately.
    /// </summary>
    private static List<TextObject> TextObjects(string content)
    {
        var objects = new List<TextObject>();

        foreach (Match match in Regex.Matches(content, @"BT(.*?)ET", RegexOptions.Singleline))
        {
            Match? colour = Regex
                .Matches(content[..match.Index], @"[-\d.]+ [-\d.]+ [-\d.]+ (?:rg|k)")
                .Cast<Match>()
                .LastOrDefault();

            objects.Add(new TextObject(colour?.Value ?? "(no colour operator)", match.Groups[1].Value));
        }

        return objects;
    }
}
