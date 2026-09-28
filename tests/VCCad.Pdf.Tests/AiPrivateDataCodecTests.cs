using System.Text;
using VCCad.Core.Model;
using VCCad.Pdf.Ai;
using Xunit;
using Xunit.Abstractions;
using ZstdSharp;

namespace VCCad.Pdf.Tests;

/// <summary>
/// Codec-level tests for the Illustrator private-data formats, driven by the
/// hand-authored fixtures under <c>tests/VCCad.Pdf.Tests/Fixtures/ai</c> plus
/// synthetic containers built in memory for the edge cases (marker offset,
/// multi-block splits, PDF-level filters).
/// </summary>
public class AiPrivateDataCodecTests
{
    private const string Prolog = "%!PS-Adobe-3.0\r\n%%Creator: Adobe Illustrator(R) 24.0\r\n";

    private readonly ITestOutputHelper _output;

    public AiPrivateDataCodecTests(ITestOutputHelper output) => _output = output;

    /// <summary>Every authored <c>.aifix</c> fixture must decode to its recorded payload.</summary>
    [Fact]
    public void SyntheticFixturesDecodeToTheirRecordedPayload()
    {
        List<AiPrivateDataFixtures.Fixture> fixtures = AiPrivateDataFixtures.LoadFixtures();
        Assert.NotEmpty(fixtures);

        foreach (AiPrivateDataFixtures.Fixture fixture in fixtures)
        {
            byte[] container = fixture.Container ?? fixture.Payload;
            AiPrivateDataDocument? document = AiPrivateDataExtractor.Extract(container);
            Assert.True(document is not null, $"{fixture.Name}: nothing extracted");
            Assert.Equal(
                Enum.Parse<AiPrivateDataFormat>(fixture.FormatName),
                document!.Format);
            Assert.Equal(Encoding.Latin1.GetString(fixture.Payload), document.Text);
            _output.WriteLine($"{fixture.Name}: {document.Format}, {document.Text.Length} chars");
        }
    }

    [Fact]
    public void ZlibHeaderSnifferAcceptsEveryWindowIllustratorWrites()
    {
        // 78 9C (32 KB) and 48 89 (16 KB) both occur in the wild; the second is the
        // one AI 9–CS writes, and a naive magic-pair check would discard those blocks.
        Assert.True(AiPrivateDataCodec.LooksLikeZlib(new byte[] { 0x78, 0x9C, 0x01 }));
        Assert.True(AiPrivateDataCodec.LooksLikeZlib(new byte[] { 0x48, 0x89, 0x01 }));
        Assert.True(AiPrivateDataCodec.LooksLikeZlib(new byte[] { 0x78, 0xDA, 0x01 }));
        Assert.True(AiPrivateDataCodec.LooksLikeZlib(new byte[] { 0xFF, 0x78, 0x9C }, 1));

        Assert.False(AiPrivateDataCodec.LooksLikeZlib(new byte[] { 0x00, 0x01 }));
        Assert.False(AiPrivateDataCodec.LooksLikeZlib(new byte[] { 0x78 })); // too short
        Assert.False(AiPrivateDataCodec.LooksLikeZlib(new byte[] { 0x28, 0xB5 })); // zstd, not zlib
    }

    [Fact]
    public void ZstdMagicSnifferMatchesTheFrameHeader()
    {
        Assert.True(AiPrivateDataCodec.LooksLikeZstd(new byte[] { 0x28, 0xB5, 0x2F, 0xFD, 0x00 }));
        Assert.True(AiPrivateDataCodec.LooksLikeZstd(new byte[] { 0xAA, 0x28, 0xB5, 0x2F, 0xFD }, 1));
        Assert.False(AiPrivateDataCodec.LooksLikeZstd(new byte[] { 0x28, 0xB5, 0x2F }));
        Assert.False(AiPrivateDataCodec.LooksLikeZstd(new byte[] { 0x78, 0x9C }));
    }

    [Fact]
    public void ZlibRoundTripsThroughTheCodec()
    {
        byte[] payload = Encoding.Latin1.GetBytes(SamplePayload());
        byte[] compressed = AiPrivateDataFixtures.Zlib(payload);

        Assert.True(AiPrivateDataCodec.LooksLikeZlib(compressed));
        Assert.Equal(payload, AiPrivateDataCodec.Inflate(compressed));
    }

    [Fact]
    public void TruncatedZlibStreamsStillYieldTheirReadablePrefix()
    {
        var source = new byte[200_000];
        for (int i = 0; i < source.Length; i++)
        {
            source[i] = (byte)(i % 251);
        }

        byte[] compressed = AiPrivateDataFixtures.Zlib(source);

        // Cut the tail: it cannot be reassembled, but everything already produced
        // must survive, because Illustrator truncates blocks at block boundaries.
        byte[] recovered = AiPrivateDataCodec.Inflate(compressed[..^16]);
        Assert.True(recovered.Length > 0, "no output recovered from a truncated stream");
        Assert.True(recovered.Length <= source.Length);
        Assert.Equal(source.Take(recovered.Length).ToArray(), recovered);
    }

    [Fact]
    public void ZstdRoundTripsThroughTheCodec()
    {
        byte[] payload = Encoding.Latin1.GetBytes(SamplePayload());
        using var compressor = new Compressor();
        byte[] compressed = compressor.Wrap(payload).ToArray();

        Assert.True(AiPrivateDataCodec.LooksLikeZstd(compressed));
        Assert.Equal(payload, AiPrivateDataCodec.DecompressZstd(compressed));
    }

    [Fact]
    public void Ai24MarkerOffsetIsDiscoveredInsideTheConcatenation()
    {
        // A short plaintext prolog precedes the marker; the decoder must never assume
        // the marker sits at offset 0.
        byte[] payload = Encoding.Latin1.GetBytes(SamplePayload());
        using var compressor = new Compressor();
        byte[] block = Encoding.ASCII.GetBytes(Prolog)
            .Concat(Encoding.ASCII.GetBytes(AiPrivateDataCodec.Ai24Marker))
            .Concat(compressor.Wrap(payload).ToArray())
            .ToArray();

        AiPrivateDataDocument? document = AiPrivateDataExtractor.Extract(
            AiPrivateDataFixtures.BuildPdf(new[] { new AiPrivateDataFixtures.PdfBlock(1, block, false) }, 1));

        Assert.NotNull(document);
        Assert.Equal(AiPrivateDataFormat.ZstdAi24, document!.Format);
        Assert.Equal(Encoding.Latin1.GetString(payload), document.Text);
    }

    [Fact]
    public void Ai12MarkerAndZlibStreamSurviveBeingSplitAcrossBlocks()
    {
        byte[] payload = Encoding.Latin1.GetBytes(SamplePayload());
        byte[] stream = Encoding.ASCII.GetBytes(Prolog)
            .Concat(Encoding.ASCII.GetBytes(AiPrivateDataCodec.Ai12Marker))
            .Concat(AiPrivateDataFixtures.Zlib(payload))
            .ToArray();

        // Split into three uneven chunks; the marker itself straddles the first two,
        // exactly as Illustrator's fixed-size blocks would.
        int first = 40;
        int second = stream.Length / 2;
        var blocks = new[]
        {
            new AiPrivateDataFixtures.PdfBlock(1, stream[..first], false),
            new AiPrivateDataFixtures.PdfBlock(2, stream[first..second], false),
            new AiPrivateDataFixtures.PdfBlock(3, stream[second..], false),
        };

        AiPrivateDataDocument? document = AiPrivateDataExtractor.Extract(
            AiPrivateDataFixtures.BuildPdf(blocks, 3));

        Assert.NotNull(document);
        Assert.Equal(AiPrivateDataFormat.ZlibAi12Cc, document!.Format);
        Assert.Equal(Encoding.Latin1.GetString(payload), document.Text);
    }

    [Fact]
    public void Ai9CsInflatesEachBlockAndDiscardsBlocksWithoutAZlibHeader()
    {
        byte[] first = Encoding.ASCII.GetBytes("%!PS-Adobe-3.0\r\n");
        byte[] second = Encoding.ASCII.GetBytes("%%EOF\r\n");
        byte[] thumbnail = Encoding.ASCII.GetBytes("%AI7_Thumbnail: 96 128 8\r");

        var blocks = new[]
        {
            // The thumbnail has neither a filter nor a zlib header: it is not payload.
            new AiPrivateDataFixtures.PdfBlock(1, thumbnail, false),
            new AiPrivateDataFixtures.PdfBlock(2, AiPrivateDataFixtures.Zlib(first), false),
            new AiPrivateDataFixtures.PdfBlock(3, AiPrivateDataFixtures.Zlib(second), false),
        };

        AiPrivateDataDocument? document = AiPrivateDataExtractor.Extract(
            AiPrivateDataFixtures.BuildPdf(blocks, 3));

        Assert.NotNull(document);
        Assert.Equal(AiPrivateDataFormat.ZlibAi9Cs, document!.Format);
        Assert.Equal(Encoding.Latin1.GetString(first.Concat(second).ToArray()), document.Text);
        Assert.DoesNotContain("Thumbnail", document.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void PdfLevelFlateDecodeHidesTheBlockZlibHeader()
    {
        // Documents the one place where the byte-level rules and a PDF library
        // disagree: when Illustrator routes the block compression through PDF's
        // /FlateDecode (as it does for real AI 9–CS files), a *decoded* block no
        // longer starts with a zlib header, so "discard headerless blocks" would keep
        // the thumbnail. The extractor reads the raw stream bytes first and only
        // falls back to decoded bytes when the raw ones are unusable.
        byte[] payload = Encoding.ASCII.GetBytes("%!PS-Adobe-3.0\r\n%%EOF\r\n");
        byte[] thumbnail = Encoding.ASCII.GetBytes("%AI7_Thumbnail: 96 128 8\r");

        var blocks = new[]
        {
            new AiPrivateDataFixtures.PdfBlock(1, thumbnail, false),
            new AiPrivateDataFixtures.PdfBlock(2, payload, true), // PDF-level FlateDecode
        };

        AiPrivateDataDocument? document = AiPrivateDataExtractor.Extract(
            AiPrivateDataFixtures.BuildPdf(blocks, 2));

        Assert.NotNull(document);
        Assert.Equal(AiPrivateDataFormat.ZlibAi9Cs, document!.Format);
        Assert.Equal(Encoding.Latin1.GetString(payload), document.Text);
    }

    [Fact]
    public void BlocksAreOrderedNumericallyNotLexicographically()
    {
        // /AIPrivateData10 must follow /AIPrivateData9; string ordering would put it
        // second. Every block is its own zlib stream (AI 9–CS).
        var parts = new (int Index, string Text)[]
        {
            (1, "%!PS-Adobe-3.0\r\n"),
            (2, "%AI5_Second\r\n"),
            (9, "%AI5_Ninth\r\n"),
            (10, "%AI5_Tenth\r\n"),
        };

        // Deliberately listed out of order in the dictionary: order must come from the
        // numeric /AIPrivateData<n> suffix.
        var blocks = new[] { parts[3], parts[0], parts[1], parts[2] }
            .Select(p => new AiPrivateDataFixtures.PdfBlock(
                p.Index, AiPrivateDataFixtures.Zlib(Encoding.ASCII.GetBytes(p.Text)), false))
            .ToList();

        AiPrivateDataDocument? document = AiPrivateDataExtractor.Extract(
            AiPrivateDataFixtures.BuildPdf(blocks, 4));

        Assert.NotNull(document);
        Assert.Equal(AiPrivateDataFormat.ZlibAi9Cs, document!.Format);
        Assert.Equal(
            "%!PS-Adobe-3.0\r\n%AI5_Second\r\n%AI5_Ninth\r\n%AI5_Tenth\r\n",
            document.Text);
    }

    [Fact]
    public void NumBlockIsOptional()
    {
        byte[] payload = Encoding.ASCII.GetBytes("%!PS-Adobe-3.0\r\n%%EOF\r\n");
        byte[] zip = AiPrivateDataFixtures.Zlib(payload);

        AiPrivateDataDocument? withCount = AiPrivateDataExtractor.Extract(
            AiPrivateDataFixtures.BuildPdf(new[] { new AiPrivateDataFixtures.PdfBlock(1, zip, false) }, 1));
        AiPrivateDataDocument? withoutCount = AiPrivateDataExtractor.Extract(
            AiPrivateDataFixtures.BuildPdf(new[] { new AiPrivateDataFixtures.PdfBlock(1, zip, false) }));

        Assert.NotNull(withCount);
        Assert.NotNull(withoutCount);
        Assert.Equal(1, withCount!.DeclaredBlockCount);
        Assert.Equal(0, withoutCount!.DeclaredBlockCount);
        Assert.Equal(withCount.Text, withoutCount.Text);
    }

    [Fact]
    public void PlainUncompressedBlocksAreAcceptedAsAnAi8Payload()
    {
        byte[] payload = Encoding.ASCII.GetBytes("%!PS-Adobe-3.0\r\n%AI5_FileFormat 7.0\r\n%%EOF\r\n");
        AiPrivateDataDocument? document = AiPrivateDataExtractor.Extract(
            AiPrivateDataFixtures.BuildPdf(new[] { new AiPrivateDataFixtures.PdfBlock(1, payload, false) }, 1));

        Assert.NotNull(document);
        Assert.Equal(AiPrivateDataFormat.PostScriptAi8, document!.Format);
        Assert.Equal(Encoding.Latin1.GetString(payload), document.Text);
    }

    [Fact]
    public void BarePostScriptFilesAreTheirOwnPayload()
    {
        byte[] payload = Encoding.ASCII.GetBytes("%!PS-Adobe-3.0\r\n%AI5_FileFormat 4.0\r\n%%EOF\r\n");
        AiPrivateDataDocument? document = AiPrivateDataExtractor.Extract(payload);

        Assert.NotNull(document);
        Assert.Equal(AiPrivateDataFormat.PostScriptAi8, document!.Format);
        Assert.Equal(Encoding.Latin1.GetString(payload), document.Text);
    }

    [Fact]
    public void FilesWithoutIllustratorDataExtractToNull()
    {
        Assert.Null(AiPrivateDataExtractor.Extract(Array.Empty<byte>()));
        Assert.Null(AiPrivateDataExtractor.Extract(Encoding.ASCII.GetBytes("not a pdf at all")));

        // Generic PostScript is not Illustrator artwork: the whole file only counts
        // as private data when it carries %AI directives.
        Assert.Null(AiPrivateDataExtractor.Extract(
            Encoding.ASCII.GetBytes("%!PS-Adobe-3.0\r\n%%BoundingBox: 0 0 10 10\r\n%%EOF\r\n")));

        byte[] pdf = AiPrivateDataFixtures.BuildPdf(Array.Empty<AiPrivateDataFixtures.PdfBlock>());
        Assert.Null(AiPrivateDataExtractor.Extract(pdf));
    }

    private static string SamplePayload()
        => "%!PS-Adobe-3.0\r\n" +
           "%%Creator: Adobe Illustrator(R) 24.0\r\n" +
           "%AI5_FileFormat 14.0\r\n" +
           "%AI5_BeginLayer\r\n" +
           "1 1 1 1 0 0 1 0 79 128 255 0 50 0 Lb\r\n" +
           "(Layer 1) Ln\r\n" +
           "1 AE\r\n" +
           "LB\r\n" +
           "%AI5_EndLayer--\r\n" +
           "%%EOF\r\n";
}
