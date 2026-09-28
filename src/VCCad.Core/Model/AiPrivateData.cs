namespace VCCad.Core.Model;

/// <summary>
/// Storage format of an Illustrator private-data payload (<c>/AIPrivateData</c>).
///
/// Illustrator changed the container encoding several times; the value records
/// which one a payload was decoded from so it can be reported, audited and — if
/// a caller ever needs it — re-encoded in the same shape. VCCad itself always
/// <em>writes</em> plain text (<see cref="PostScriptAi8"/>), which Illustrator
/// opens directly, but it must round-trip every historical encoding losslessly,
/// hence the full enumeration.
/// </summary>
public enum AiPrivateDataFormat
{
    /// <summary>The payload could not be classified (e.g. an already-extracted text blob).</summary>
    Unknown = 0,

    /// <summary>
    /// Illustrator 8 and earlier (or an already-decoded payload): the file <em>is</em>
    /// the PostScript programme — there is no PDF container and no compression.
    /// </summary>
    PostScriptAi8,

    /// <summary>
    /// Illustrator 9 – CS (file format 9.0–11.0): every <c>/AIPrivateData&lt;n&gt;</c>
    /// block is an independent zlib (RFC 1950) stream; the inflated blocks are
    /// concatenated in numeric order. Blocks without a zlib header (typically the
    /// page thumbnail) are discarded.
    /// </summary>
    ZlibAi9Cs,

    /// <summary>
    /// Illustrator CS2 – CC Legacy (file format 12.0–17.0): all blocks are
    /// concatenated first, the leading <c>%AI12_CompressedData</c> marker is
    /// stripped, and the remainder is one zlib stream.
    /// </summary>
    ZlibAi12Cc,

    /// <summary>
    /// Illustrator 2020 and later (file format 24.0): all blocks are concatenated,
    /// the leading <c>%AI24_ZStandard_Data</c> marker is stripped, and the
    /// remainder is one zstd stream.
    /// </summary>
    ZstdAi24,
}

/// <summary>
/// The decoded Illustrator private-data payload carried by a document.
///
/// Only the <em>decoded</em> text is stored, together with the format it came
/// from: the compressed bytes are an implementation detail of the container and
/// are regenerated from the text on export. That keeps the lossless sidecar JSON
/// human-readable and keeps the document model independent of the AI codec.
///
/// <para>
/// The text is a Latin-1 (ISO-8859-1) projection of the payload byte stream — one
/// character per source byte. That is what makes the round-trip byte-exact even
/// for payloads that embed binary raster data; see
/// <c>VCCad.Pdf.Ai.AiPayloadWriter</c>.
/// </para>
/// </summary>
public sealed class AiPrivateData
{
    /// <summary>Creates a payload from its decoded text and source format.</summary>
    /// <param name="text">Decoded payload text (Latin-1 projection of the bytes).</param>
    /// <param name="format">Container format the text was decoded from.</param>
    public AiPrivateData(string text, AiPrivateDataFormat format = AiPrivateDataFormat.PostScriptAi8)
    {
        Text = text ?? throw new ArgumentNullException(nameof(text));
        Format = format;
    }

    /// <summary>Decoded payload text; the empty string means "no payload".</summary>
    public string Text { get; }

    /// <summary>Container format the payload was decoded from.</summary>
    public AiPrivateDataFormat Format { get; }

    /// <summary>True when this payload carries no text at all.</summary>
    public bool IsEmpty => Text.Length == 0;

    /// <summary>Short diagnostic form, e.g. <c>ZstdAi24 (2271798 chars)</c>.</summary>
    public override string ToString() => $"{Format} ({Text.Length} chars)";
}
