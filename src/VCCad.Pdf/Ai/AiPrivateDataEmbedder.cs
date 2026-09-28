using System.Text;

namespace VCCad.Pdf.Ai;

/// <summary>
/// The export half of the private-data subsystem: the PDF fragments that carry a
/// decoded payload back into a page.
///
/// The shape written is the one Illustrator itself uses, so the result is a file an
/// Illustrator user can open and VCCad can re-import:
/// <code>
/// /PieceInfo &lt;&lt; /Illustrator &lt;&lt; /Subtype /Artwork
///                                /CreatorInfo &lt;&lt; /Creator (VCCad) /Subtype /Artwork &gt;&gt;
///                                /Private &lt;&lt; /NumBlock 1 /AIPrivateData1 N 0 R &gt;&gt; &gt;&gt; &gt;&gt;
/// </code>
///
/// <para>
/// The object number is injected by <see cref="VCCad.Pdf.PdfDocumentExporter"/>,
/// which owns the assembler and therefore object numbering. Everything here is a
/// pure string/byte transformation so it stays testable without a PDF in flight.
/// </para>
/// </summary>
public static class AiPrivateDataEmbedder
{
    /// <summary>/Subtype value Illustrator uses for the artwork private data.</summary>
    public const string ArtworkSubtype = "Artwork";

    /// <summary>/Creator value VCCad writes into the Illustrator /CreatorInfo dicts.</summary>
    public const string CreatorName = "VCCad";

    /// <summary>
    /// Builds the <c>/PieceInfo</c> entry that attaches the private-data stream
    /// <paramref name="privateDataObjectNumber"/> to a page.
    /// </summary>
    public static string PieceInfo(int privateDataObjectNumber)
    {
        if (privateDataObjectNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(privateDataObjectNumber), "Private-data stream object number must be positive.");
        }

        return $"/PieceInfo << /Illustrator << /Subtype /{ArtworkSubtype} " +
               $"/CreatorInfo {CreatorInfo()} " +
               $"/Private << /NumBlock 1 /AIPrivateData1 {privateDataObjectNumber} 0 R >> >> >>";
    }

    /// <summary>
    /// Builds the catalog-level <c>/CreatorInfo</c> entry. Illustrator writes this
    /// alongside the per-page <c>/PieceInfo</c>; readers use it to recognise the file
    /// as Illustrator artwork before touching any page.
    /// </summary>
    public static string CatalogCreatorInfo() => $"/CreatorInfo {CreatorInfo()}";

    /// <summary>Encodes payload text for storage in a PDF stream (see the class remarks).</summary>
    public static byte[] EncodePayload(string payloadText)
    {
        ArgumentNullException.ThrowIfNull(payloadText);
        return Encoding.Latin1.GetBytes(payloadText);
    }

    private static string CreatorInfo()
        => $"<< /Creator ({CreatorName}) /Subtype /{ArtworkSubtype} >>";
}
