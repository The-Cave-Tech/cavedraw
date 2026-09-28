using System.Text;
using VCCad.Core.Model;
using VCCad.Core.Serialization;
using VCCad.Pdf;
using VCCad.Pdf.Ai;
using Xunit;

namespace VCCad.Pdf.Tests;

/// <summary>
/// Export-side behaviour of the Illustrator private data: the <c>/PieceInfo</c>
/// chain must appear only when a payload exists (the existing PDF/A and
/// conformance tests pin the export structure), the payload must survive the
/// lossless sidecar, and import → export → extraction must return the same text.
/// </summary>
public class AiPrivateDataExportTests
{
    private const string Payload =
        "%!PS-Adobe-3.0\r\n" +
        "%%Creator: Adobe Illustrator(R) 24.0\r\n" +
        "%AI5_FileFormat 14.0\r\n" +
        "%AI5_BeginLayer\r\n" +
        "1 1 1 1 0 0 1 0 79 128 255 0 50 0 Lb\r\n" +
        "(Layer 1) Ln\r\n" +
        "1 AE\r\n" +
        "LB\r\n" +
        "%AI5_EndLayer--\r\n" +
        "%%EOF\r\n";

    [Fact]
    public void DocumentsWithoutPayloadExportAsBefore()
    {
        CadDocument document = CadDocument.CreateDefault("No AI data");

        byte[] pdf = PdfDocumentExporter.Export(document);
        string text = Encoding.Latin1.GetString(pdf);

        // Strict no-op: neither the per-page chain nor the catalog marker is written.
        Assert.DoesNotContain("/PieceInfo", text, StringComparison.Ordinal);
        Assert.DoesNotContain("/AIPrivateData", text, StringComparison.Ordinal);
        Assert.DoesNotContain("/CreatorInfo", text, StringComparison.Ordinal);

        // And the sidecar stays byte-for-byte the pre-feature format: no new member.
        string json = VccadDocumentSerializer.Serialize(document);
        Assert.DoesNotContain("AiPrivateData", json, StringComparison.Ordinal);
    }

    [Fact]
    public void DocumentsWithPayloadGetTheIllustratorPieceInfoChain()
    {
        CadDocument document = CadDocument.CreateDefault("With AI data");
        document.AiPrivateData = new AiPrivateData(Payload, AiPrivateDataFormat.ZstdAi24);

        byte[] pdf = PdfDocumentExporter.Export(document);
        string text = Encoding.Latin1.GetString(pdf);

        Assert.Contains("/PieceInfo << /Illustrator << /Subtype /Artwork", text, StringComparison.Ordinal);
        Assert.Contains("/CreatorInfo << /Creator (VCCad) /Subtype /Artwork >>", text, StringComparison.Ordinal);
        Assert.Contains("/Private << /NumBlock 1 /AIPrivateData1 ", text, StringComparison.Ordinal);
        Assert.Contains("/Type /Catalog", text, StringComparison.Ordinal);

        // The catalog carries /CreatorInfo too.
        int catalog = text.IndexOf("/Type /Catalog", StringComparison.Ordinal);
        Assert.Contains("/CreatorInfo", text[catalog..(catalog + 400)], StringComparison.Ordinal);
    }

    [Fact]
    public void ExportedPayloadIsReadBackByteForByte()
    {
        CadDocument document = CadDocument.CreateDefault("With AI data");
        document.AiPrivateData = new AiPrivateData(Payload, AiPrivateDataFormat.ZlibAi12Cc);

        byte[] pdf = PdfDocumentExporter.Export(document);
        AiPrivateDataDocument? extracted = AiPrivateDataExtractor.Extract(pdf);

        Assert.NotNull(extracted);
        Assert.Equal(Payload, extracted!.Text);

        // VCCad writes plain (AI8-style) private data; the source format is metadata
        // that describes where the text came from, not what we emit.
        Assert.Equal(AiPrivateDataFormat.ZlibAi9Cs, extracted.Format);
    }

    [Fact]
    public void SharedStreamObjectContributesOnceForMultiPageDocuments()
    {
        var document = new CadDocument { Name = "Two pages" };
        document.AddArtboard(PageSizes.A4Landscape, "Page 1").AddLayer("Layer 1");
        document.AddArtboard(PageSizes.A4Landscape, "Page 2").AddLayer("Layer 1");
        document.AiPrivateData = new AiPrivateData(Payload, AiPrivateDataFormat.PostScriptAi8);

        byte[] pdf = PdfDocumentExporter.Export(document);
        AiPrivateDataDocument? extracted = AiPrivateDataExtractor.Extract(pdf);

        Assert.NotNull(extracted);
        Assert.Equal(Payload, extracted!.Text);
    }

    [Fact]
    public void ExportingTwiceProducesTheSamePayload()
    {
        CadDocument document = CadDocument.CreateDefault("Determinism");
        document.AiPrivateData = new AiPrivateData(Payload, AiPrivateDataFormat.PostScriptAi8);

        AiPrivateDataDocument? first = AiPrivateDataExtractor.Extract(PdfDocumentExporter.Export(document));
        AiPrivateDataDocument? second = AiPrivateDataExtractor.Extract(PdfDocumentExporter.Export(document));

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first!.Text, second!.Text);
        Assert.Equal(first.Format, second.Format);
    }

    [Fact]
    public void SidecarPersistsTheDecodedTextAndFormat()
    {
        CadDocument document = CadDocument.CreateDefault("Sidecar");
        document.AiPrivateData = new AiPrivateData(Payload, AiPrivateDataFormat.ZstdAi24);

        CadDocument revived = VccadDocumentSerializer.Deserialize(
            VccadDocumentSerializer.Serialize(document));

        Assert.NotNull(revived.AiPrivateData);
        Assert.Equal(Payload, revived.AiPrivateData!.Text);
        Assert.Equal(AiPrivateDataFormat.ZstdAi24, revived.AiPrivateData.Format);

        // Deterministic: the second serialization is identical to the first.
        Assert.Equal(
            VccadDocumentSerializer.Serialize(revived),
            VccadDocumentSerializer.Serialize(document));
    }

    [Fact]
    public void PayloadSurvivesPdfImportOfOurOwnExport()
    {
        CadDocument document = CadDocument.CreateDefault("Round trip");
        document.AiPrivateData = new AiPrivateData(Payload, AiPrivateDataFormat.PostScriptAi8);

        byte[] pdf = PdfDocumentExporter.Export(document);
        CadDocument imported = PdfImporter.Import(pdf);

        // The importer takes the payload from the sidecar (authoritative) rather
        // than re-deriving it, but the result must be the same text either way.
        Assert.NotNull(imported.AiPrivateData);
        Assert.Equal(Payload, imported.AiPrivateData!.Text);
    }

    [Fact]
    public void EmptyPayloadIsTreatedAsNoPayload()
    {
        CadDocument document = CadDocument.CreateDefault("Empty");
        document.AiPrivateData = new AiPrivateData(string.Empty, AiPrivateDataFormat.Unknown);

        byte[] pdf = PdfDocumentExporter.Export(document);
        Assert.DoesNotContain("/PieceInfo", Encoding.Latin1.GetString(pdf), StringComparison.Ordinal);
    }
}
