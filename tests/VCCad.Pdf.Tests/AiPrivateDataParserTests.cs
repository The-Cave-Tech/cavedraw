using System.Text;
using VCCad.Pdf.Ai;
using Xunit;

namespace VCCad.Pdf.Tests;

/// <summary>
/// Parser/writer tests: the payload view must be structured (directives, data
/// blocks, nested layers) and at the same time completely lossless, so that
/// <c>Write(Parse(text)) == text</c> byte-for-byte and parsing written text again
/// yields the same structure.
/// </summary>
public class AiPrivateDataParserTests
{
    private const string TwoLayerPayload =
        "%!PS-Adobe-3.0\r\n" +
        "%%Creator: Adobe Illustrator(R) 24.0\r\n" +
        "%AI5_FileFormat 14.0\r\n" +
        "%AI5_NumLayers: 2\r\n" +
        "%%EndComments\r\n" +
        "%AI5_BeginLayer\r\n" +
        "1 1 1 1 0 0 1 0 79 128 255 0 50 0 Lb\r\n" +
        "(Layer 1) Ln\r\n" +
        "1 AE\r\n" +
        "0 A\r\n" +
        "%AI5_BeginLayer\r\n" +
        "0 1 1 1 0 0 0 2 255 79 79 0 50 0 Lb\r\n" +
        "(Sublayer 1_1) Ln\r\n" +
        "0 AE\r\n" +
        "LB\r\n" +
        "%AI5_EndLayer--\r\n" +
        "LB\r\n" +
        "%AI5_EndLayer--\r\n" +
        "%AI5_BeginLayer\r\n" +
        "1 1 0 1 0 0 1 1 255 79 79 0 50 0 Lb\r\n" +
        "(Layer 2) Ln\r\n" +
        "1 AE\r\n" +
        "LB\r\n" +
        "%AI5_EndLayer--\r\n" +
        "%%EOF\r\n";

    public static IEnumerable<object[]> Payloads()
    {
        var payloads = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["empty"] = string.Empty,
            ["lf"] = "%!PS-Adobe-3.0\n%%EOF\n",
            ["crlf"] = "%!PS-Adobe-3.0\r\n%%EOF\r\n",
            ["cr-only"] = "%!PS-Adobe-3.0\r%%EOF\r",
            ["no-trailing-terminator"] = "%!PS-Adobe-3.0\n%%EOF",
            ["mixed-endings"] = "a\nb\r\nc\rd",
            ["layers"] = TwoLayerPayload,
            ["data-block"] = "%%BeginData: 4 Hex Bytes\r\n%00FF10AB\r\n%%EndData\r\n%AI5_FileFormat 14.0\r\n",
            ["unterminated-data"] = "%%BeginData: 100 Hex Bytes\r\n%0011\r\n%!PS-Adobe-3.0\r\n",
            ["binary"] = string.Concat(Enumerable.Range(0x01, 0xFF).Select(i => (char)i)) + "\r\n",
            ["dsc-only"] = "%%!PS-Adobe-3.0\r\n%%%%BoundingBox: 0 0 1 1\r\n",
        };

        foreach (KeyValuePair<string, string> entry in payloads.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            yield return new object[] { entry.Key, entry.Value };
        }
    }

    [Theory]
    [MemberData(nameof(Payloads))]
    public void WriterReproducesTheInputByteForByte(string name, string text)
    {
        AiPrivateDataDocument document = AiPrivateDataDocument.Parse(text);
        string written = document.Write();

        Assert.True(text.Length == written.Length, $"{name}: writer changed the payload length");
        Assert.Equal(text, written);
        Assert.True(
            Encoding.Latin1.GetBytes(text).SequenceEqual(AiPayloadWriter.WriteBytes(document)),
            $"{name}: the byte projection of the payload changed");
    }

    [Theory]
    [MemberData(nameof(Payloads))]
    public void ParsingWrittenTextIsStructurallyIdentical(string name, string text)
    {
        AiPrivateDataDocument first = AiPrivateDataDocument.Parse(text);
        AiPrivateDataDocument second = AiPrivateDataDocument.Parse(first.Write());

        Assert.True(first.StructurallyEquals(second), $"{name}: round-trip changed the structure");
        Assert.Equal(first.Elements.Count, second.Elements.Count);
    }

    [Theory]
    [MemberData(nameof(Payloads))]
    public void EveryElementCoversItsExactSourceSlice(string name, string text)
    {
        AiPrivateDataDocument document = AiPrivateDataDocument.Parse(text);
        int offset = 0;

        foreach (AiPayloadElement element in document.Elements)
        {
            Assert.NotEqual(string.Empty, element.Raw);
            Assert.Equal(text.Substring(offset, element.Raw.Length), element.Raw);
            offset += element.Raw.Length;
        }

        Assert.True(text.Length == offset, $"{name}: elements cover {offset} of {text.Length} characters");
    }

    [Fact]
    public void DirectivesExposeTheirKeyAndValue()
    {
        AiPrivateDataDocument document = AiPrivateDataDocument.Parse(TwoLayerPayload);

        AiDirective format = Assert.Single(document.Directives, d => d.Key == "%AI5_FileFormat");
        Assert.Equal("14.0", format.Value);
        Assert.Equal(AiDirectiveKind.AiHeader, format.Kind);

        AiDirective creator = Assert.Single(document.Directives, d => d.Key == "%%Creator");
        Assert.Equal("Adobe Illustrator(R) 24.0", creator.Value);
        Assert.Equal(AiDirectiveKind.Dsc, creator.Kind);

        Assert.Contains(document.Directives, d => d.Key == "%%EndComments" && d.Value is null);
        Assert.Contains(document.Elements, e => e is AiRawLine { Text: "0 A\r\n" });
    }

    [Fact]
    public void DataBlocksAreDecodedButKeptVerbatim()
    {
        const string text = "%!PS-Adobe-3.0\r\n%%BeginData: 4 Hex Bytes\r\n%00FF10AB\r\n%%EndData\r\n%%EOF\r\n";
        AiPrivateDataDocument document = AiPrivateDataDocument.Parse(text);

        AiDataBlock block = Assert.IsType<AiDataBlock>(
            Assert.Single(document.Elements, e => e is AiDataBlock));
        Assert.Equal(AiDataEncoding.Hex, block.Encoding);
        Assert.Equal(4, block.DeclaredByteCount);
        Assert.Equal(new byte[] { 0x00, 0xFF, 0x10, 0xAB }, block.Data);
        Assert.Equal("%%BeginData: 4 Hex Bytes\r\n%00FF10AB\r\n%%EndData\r\n", block.Raw);
        Assert.Equal(text, document.Write());
    }

    [Fact]
    public void UnterminatedDataBlocksFallBackToOrdinaryLines()
    {
        const string text = "%%BeginData: 100 Hex Bytes\r\n%0011\r\n%!PS-Adobe-3.0\r\n";
        AiPrivateDataDocument document = AiPrivateDataDocument.Parse(text);

        Assert.DoesNotContain(document.Elements, e => e is AiDataBlock);
        Assert.Equal(text, document.Write());
    }

    [Fact]
    public void LayersNestWithNamesFlagsAndColours()
    {
        AiPrivateDataDocument document = AiPrivateDataDocument.Parse(TwoLayerPayload);

        Assert.Equal(2, document.Layers.Count);

        AiLayer first = document.Layers[0];
        Assert.Equal("Layer 1", first.Name);
        Assert.True(first.IsVisible);
        Assert.True(first.IsVisibleFlag);
        Assert.True(first.IsExpanded);
        Assert.Equal(0, first.ColorIndex);
        Assert.Equal(new AiLayerColor(79, 128, 255), first.Color);
        Assert.Equal(0, first.Depth);
        Assert.True(first.IsEffectivelyVisible);
        Assert.Null(first.Parent);

        AiLayer sublayer = Assert.Single(first.Children);
        Assert.Equal("Sublayer 1_1", sublayer.Name);
        Assert.False(sublayer.IsVisible);
        Assert.False(sublayer.IsVisibleFlag);
        Assert.False(sublayer.IsExpanded);
        Assert.Equal(new AiLayerColor(255, 79, 79), sublayer.Color);
        Assert.Equal(1, sublayer.Depth);
        Assert.Same(first, sublayer.Parent);

        // A hidden parent hides its sublayers regardless of their own flags.
        Assert.False(sublayer.IsEffectivelyVisible);

        AiLayer second = document.Layers[1];
        Assert.Equal("Layer 2", second.Name);
        Assert.True(second.IsEffectivelyVisible);
        Assert.Empty(second.Children);

        Assert.Equal(3, document.Layers.SelectMany(l => l.DescendantsAndSelf()).Count());
    }

    [Fact]
    public void HiddenParentHidesAVisibleSublayer()
    {
        const string text =
            "%!PS-Adobe-3.0\r\n" +
            "%AI5_BeginLayer\r\n" +
            "0 1 1 1 0 0 0 0 79 128 255 0 50 0 Lb\r\n" +
            "(Hidden) Ln\r\n" +
            "0 AE\r\n" +
            "%AI5_BeginLayer\r\n" +
            "1 1 1 1 0 0 1 1 255 79 79 0 50 0 Lb\r\n" +
            "(Visible child) Ln\r\n" +
            "1 AE\r\n" +
            "LB\r\n" +
            "%AI5_EndLayer--\r\n" +
            "LB\r\n" +
            "%AI5_EndLayer--\r\n";

        AiPrivateDataDocument document = AiPrivateDataDocument.Parse(text);
        AiLayer child = Assert.Single(Assert.Single(document.Layers).Children);

        Assert.True(child.IsVisible);
        Assert.False(child.IsEffectivelyVisible);
    }

    [Fact]
    public void LegacyLbWithoutTheVisibleFlagFieldStillParses()
    {
        // Pre-AI8 layers have 10 operands (no `visible?`, no trailing trio).
        const string text =
            "%!PS-Adobe-3.0\r\n" +
            "%AI5_BeginLayer\r\n" +
            "1 1 1 1 0 0 0 12 34 56 Lb\r\n" +
            "(Old) Ln\r\n" +
            "LB\r\n" +
            "%AI5_EndLayer--\r\n";

        AiPrivateDataDocument document = AiPrivateDataDocument.Parse(text);
        AiLayer layer = Assert.Single(document.Layers);

        Assert.Equal("Old", layer.Name);
        Assert.True(layer.IsVisible);
        Assert.True(layer.IsVisibleFlag);
        Assert.Equal(0, layer.ColorIndex);
        Assert.Equal(new AiLayerColor(12, 34, 56), layer.Color);
        Assert.Equal(text, document.Write());
    }

    [Fact]
    public void RenamingALayerRewritesOnlyTheLnStatement()
    {
        AiPrivateDataDocument document = AiPrivateDataDocument.Parse(TwoLayerPayload);
        var elements = document.Elements.ToList();
        int index = elements.FindIndex(e => e is AiLayerBegin { Name: "Layer 1" });

        var begin = (AiLayerBegin)elements[index];
        elements[index] = begin.WithName("Renamed (layer)");

        string written = AiPayloadWriter.Write(elements);
        Assert.Contains("(Renamed \\(layer\\)) Ln\r\n", written, StringComparison.Ordinal);
        Assert.DoesNotContain("(Layer 1) Ln", written, StringComparison.Ordinal);

        // Everything else is untouched, and the result still parses into two layers.
        AiPrivateDataDocument reparsed = AiPrivateDataDocument.Parse(written);
        Assert.Equal("Renamed (layer)", reparsed.Layers[0].Name);
        Assert.Equal("Sublayer 1_1", Assert.Single(reparsed.Layers[0].Children).Name);
    }

    [Fact]
    public void HidingALayerClearsBothVisibilityOperands()
    {
        AiPrivateDataDocument document = AiPrivateDataDocument.Parse(TwoLayerPayload);
        var elements = document.Elements.ToList();
        int index = elements.FindIndex(e => e is AiLayerBegin { Name: "Layer 2" });

        var begin = (AiLayerBegin)elements[index];
        elements[index] = begin.WithVisibility(false);

        string written = AiPayloadWriter.Write(elements);
        AiPrivateDataDocument reparsed = AiPrivateDataDocument.Parse(written);
        AiLayer layer = reparsed.Layers[1];

        Assert.False(layer.IsVisible);
        Assert.False(layer.IsVisibleFlag);
        Assert.Equal("0 1 0 1 0 0 0 1 255 79 79 0 50 0 Lb", Assert.IsType<AiLayerBegin>(
            reparsed.Elements[index]).LbLine!.TrimEnd('\r', '\n'));
        Assert.Equal(
            "1 1 0 1 0 0 1 1 255 79 79 0 50 0 Lb",
            begin.LbLine!.TrimEnd('\r', '\n'));
    }

    [Fact]
    public void UnrecognisedLinesArePreservedInOrder()
    {
        const string text =
            "0 A\r\n" +
            "0 Xw\r\n" +
            "LB\r\n" +
            "gsave annotatepage grestore showpage\r\n" +
            "%%Trailer\r\n";

        AiPrivateDataDocument document = AiPrivateDataDocument.Parse(text);
        string[] raw = document.Elements.OfType<AiRawLine>().Select(e => e.Text).ToArray();

        Assert.Equal(new[] { "0 A\r\n", "0 Xw\r\n", "LB\r\n", "gsave annotatepage grestore showpage\r\n" }, raw);
        Assert.Equal(text, document.Write());
    }
}
