using VCCad.Core.Model;
using VCCad.Pdf;
using Xunit;

namespace VCCad.Pdf.Tests;

/// <summary>
/// Type 3 and type 4 PDF functions, used for spot-colour tint transforms and shadings.
///
/// A tint transform that is not evaluated does not give a slightly wrong colour: the raw
/// tint is handed to a four-component alternate space, so a spot colour becomes whatever
/// one number means to CMYK. On the Ghent Output Suite's default-colour-space sheet, that
/// is black across swatches that should be pink. Both types appear in real files — type 4
/// in about one in eight, type 3 in about one in twelve, across the Ghostscript corpus.
/// </summary>
public class PdfFunctionTests
{
    /// <summary>A PDF whose content paints one swatch with the given Separation.</summary>
    private static byte[] SeparationPdf(string separation, string tint = "1.0")
    {
        string content = $"q /CS1 cs {tint} scn 60 600 120 120 re f Q";
        string resources = $"/ColorSpace << /CS1 {separation} >>";

        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << {resources} >> "
                + "/Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
        };

        var builder = new System.Text.StringBuilder("%PDF-1.7\n");
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
            builder.Append($"{offset:0000000000} 00000 n \n");
        }

        builder.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\n");
        builder.Append($"startxref\n{xref}\n%%EOF\n");
        return System.Text.Encoding.Latin1.GetBytes(builder.ToString());
    }

    private static ColorRgb Painted(string separation, string tint = "1.0")
    {
        CadDocument document = PdfImporter.Import(SeparationPdf(separation, tint));
        PathItem path = document.Artboards[0].Layers[0].Children.OfType<PathItem>().Single();
        return path.Fill.Color;
    }

    [Fact]
    public void AType4CalculatorRuns()
    {
        // A type 4 function is a stream, so it is exercised directly. The program takes
        // one input and leaves four outputs: the input scaled by 0.5, then the input
        // three times over.
        double[]? result = PdfFunctions.Type4("{ dup 0.5 mul exch dup dup }",
            new[] { 0.8 }, outputCount: 4);

        Assert.NotNull(result);
        Assert.Equal(4, result!.Length);
        Assert.Equal(0.4, result[0], 6);
        Assert.Equal(0.8, result[1], 6);
    }

    [Fact]
    public void AType4CalculatorBranches()
    {
        // ifelse picks the branch: inputs below a half come out 0.1, above come out 0.9.
        double[]? low = PdfFunctions.Type4(
            "{ dup 0.5 lt { pop 0.1 } { pop 0.9 } ifelse }", new[] { 0.2 }, 1);
        double[]? high = PdfFunctions.Type4(
            "{ dup 0.5 lt { pop 0.1 } { pop 0.9 } ifelse }", new[] { 0.7 }, 1);

        Assert.Equal(0.1, low![0], 6);
        Assert.Equal(0.9, high![0], 6);
    }

    [Fact]
    public void AnUnsupportedOperatorIsRefusedRatherThanGuessed()
    {
        // A number that looks plausible and is wrong is worse than no answer: the caller
        // keeps the input instead. "foo" is not a PostScript operator.
        Assert.Null(PdfFunctions.Type4("{ foo }", new[] { 0.5 }, 1));

        // As is a program that underflows the stack.
        Assert.Null(PdfFunctions.Type4("{ add }", new[] { 0.5 }, 1));
    }

    [Fact]
    public void AType3StitchSelectsTheRightPart()
    {
        // Two parts, split at 0.5: below it the first sub-function, above the second.
        // Each is a constant type 2 function, so the result says which part ran.
        const string separation =
            "[/Separation /Spot /DeviceCMYK << /FunctionType 3 /Domain [0 1] /Bounds [0.5] "
            + "/Encode [0 1 0 1] /Functions ["
            + "<< /FunctionType 2 /Domain [0 1] /C0 [1 1 1 0] /C1 [1 1 1 0] /N 1 >> "
            + "<< /FunctionType 2 /Domain [0 1] /C0 [0 0 0 0] /C1 [0 0 0 0] /N 1 >> "
            + "] >>]";

        // Below the boundary: the first sub-function, which is full CMYK black.
        ColorRgb low = Painted(separation, "0.2");
        Assert.Equal(0.0, low.R, 3);
        Assert.Equal(0.0, low.G, 3);

        // Above it: the second, which is white in CMYK and so stays white on the page.
        ColorRgb high = Painted(separation, "0.8");
        Assert.Equal(1.0, high.R, 3);
        Assert.Equal(1.0, high.G, 3);
    }
}
