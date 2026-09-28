using System.Text;
using VCCad.Core.Model;

namespace VCCad.Pdf.Ai;

/// <summary>
/// Emits a payload back out of its parsed model.
///
/// The writer is intentionally dumb: it concatenates each element's verbatim
/// <see cref="AiPayloadElement.Raw"/> slice in order. That is what makes
/// <c>Write(Parse(text)) == text</c> hold byte-for-byte for every payload,
/// including ones with binary raster data, mixed line endings and constructs the
/// tokenizer does not understand. Structured editing happens by replacing an
/// element with a regenerated one (see <see cref="AiLayerBegin.WithName"/>), which
/// then contributes its new text to the same concatenation.
///
/// VCCad's own exports are plain-text (AI8-style) payloads: Illustrator opens
/// uncompressed private data directly, and the compressed containers exist only to
/// shrink the file. The codec still round-trips every historical encoding on the
/// read side.
/// </summary>
public static class AiPayloadWriter
{
    /// <summary>Writes the document's element list back to its payload text.</summary>
    public static string Write(AiPrivateDataDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return Write(document.Elements);
    }

    /// <summary>Writes elements back to text by concatenating their verbatim slices.</summary>
    public static string Write(IReadOnlyList<AiPayloadElement> elements)
    {
        ArgumentNullException.ThrowIfNull(elements);

        var builder = new StringBuilder();
        foreach (AiPayloadElement element in elements)
        {
            builder.Append(element.Raw);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Encodes payload text as the byte stream a PDF <c>/AIPrivateData</c> stream
    /// carries.
    ///
    /// Latin-1 is deliberate and load-bearing: the decoded text is a one-character-
    /// per-byte projection of the payload (private data embeds binary rasters), so
    /// only a byte-preserving codec makes import → export → import produce the same
    /// text. UTF-8 would expand every byte ≥ 0x80 and break the round-trip.
    /// </summary>
    public static byte[] WriteBytes(AiPrivateDataDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return Encoding.Latin1.GetBytes(document.Text);
    }

    /// <summary>Wraps decoded payload text in the model-level payload object.</summary>
    public static AiPrivateData ToPrivateData(AiPrivateDataDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return new AiPrivateData(document.Text, document.Format);
    }
}
