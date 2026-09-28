using System.IO.Compression;
using ZstdSharp;

namespace VCCad.Pdf.Ai;

/// <summary>
/// Low-level byte codec for Illustrator private data: the format sniffers and the
/// zlib/zstd decompressors the container rules are built on.
///
/// Nothing here knows about PDF; it operates on the concatenated block bytes that
/// <see cref="AiPrivateDataExtractor"/> hands over. Every helper is deliberately
/// tolerant: Illustrator truncates payloads, pads them and appends unrelated
/// blocks, and a reader that throws on the first stray byte cannot read real files.
/// </summary>
public static class AiPrivateDataCodec
{
    /// <summary>Marker that precedes the single zlib stream of an AI12–CC file.</summary>
    public const string Ai12Marker = "%AI12_CompressedData";

    /// <summary>Marker that precedes the single zstd stream of an AI24 file.</summary>
    public const string Ai24Marker = "%AI24_ZStandard_Data";

    private static readonly byte[] ZstdMagic = { 0x28, 0xB5, 0x2F, 0xFD };

    /// <summary>
    /// True when <paramref name="data"/> begins with a valid zlib (RFC 1950) header
    /// at <paramref name="offset"/>.
    ///
    /// Illustrator has used several window sizes over the years, so the check is the
    /// real zlib framing test rather than the two magic pairs (<c>78 9C</c>,
    /// <c>78 DA</c>) people usually hard-code: the compression method nibble must be
    /// 8 (deflate), the window exponent must be ≤ 7, and the 16-bit header must be a
    /// multiple of 31. That is what makes the <c>48 89</c> header AI 9–CS writes
    /// (4 KB window) decode instead of being discarded.
    /// </summary>
    public static bool LooksLikeZlib(ReadOnlySpan<byte> data, int offset = 0)
    {
        if (offset < 0 || offset + 2 > data.Length)
        {
            return false;
        }

        int cmf = data[offset];
        int flg = data[offset + 1];
        return (cmf & 0x0F) == 8 && (cmf >> 4) <= 7 && (((cmf << 8) | flg) % 31) == 0;
    }

    /// <summary>True when <paramref name="data"/> begins with the zstd frame magic.</summary>
    public static bool LooksLikeZstd(ReadOnlySpan<byte> data, int offset = 0)
    {
        if (offset < 0 || offset + ZstdMagic.Length > data.Length)
        {
            return false;
        }

        for (int i = 0; i < ZstdMagic.Length; i++)
        {
            if (data[offset + i] != ZstdMagic[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Inflates a zlib stream, returning whatever could be recovered.
    ///
    /// AI9–CS stores one complete zlib stream per block, but AI12–CC splits a single
    /// stream across 64 KB blocks and the last block may be short. A truncated tail
    /// therefore ends decompression without failing the whole payload — the caller
    /// decides whether the partial result is usable.
    /// </summary>
    public static byte[] Inflate(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return Array.Empty<byte>();
        }

        using var input = new MemoryStream(data.ToArray(), writable: false);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        return Drain(zlib);
    }

    /// <summary>Inflates a zlib stream, reporting whether any output was produced.</summary>
    public static bool TryInflate(ReadOnlySpan<byte> data, out byte[] result)
    {
        try
        {
            result = Inflate(data);
            return result.Length > 0;
        }
        catch (InvalidDataException)
        {
            result = Array.Empty<byte>();
            return false;
        }
    }

    /// <summary>
    /// Decompresses a zstd stream (Illustrator 2020+). Streaming is used rather than
    /// the one-shot <c>Unwrap</c> because AI24 frames omit the content-size field, so
    /// the output length is not known up front.
    /// </summary>
    public static byte[] DecompressZstd(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return Array.Empty<byte>();
        }

        using var input = new MemoryStream(data.ToArray(), writable: false);
        using var zstd = new DecompressionStream(input);
        return Drain(zstd);
    }

    /// <summary>Decompresses a zstd stream, returning false instead of throwing on garbage.</summary>
    public static bool TryDecompressZstd(ReadOnlySpan<byte> data, out byte[] result)
    {
        try
        {
            result = DecompressZstd(data);
            return result.Length > 0;
        }
        catch (Exception)
        {
            // ZstdSharp surfaces corrupt frames as a mix of ZstdException and
            // EndOfStreamException; private data is best-effort, so treat them all
            // as "not a zstd stream".
            result = Array.Empty<byte>();
            return false;
        }
    }

    /// <summary>Index of <paramref name="needle"/> in <paramref name="haystack"/>, or −1.</summary>
    public static int IndexOf(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
        => haystack.IndexOf(needle);

    private static byte[] Drain(Stream stream)
    {
        using var output = new MemoryStream();
        byte[] buffer = new byte[81920];
        try
        {
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                output.Write(buffer, 0, read);
            }
        }
        catch (InvalidDataException)
        {
            // Keep the bytes produced before the corrupt tail (see Inflate).
        }
        catch (EndOfStreamException)
        {
            // Same: a short zstd frame is still worth returning.
        }
        catch (IOException)
        {
            // A truncated deflate stream surfaces as a plain IOException on some
            // runtimes; the readable prefix is still the best answer available.
        }
        catch (ZstdException)
        {
            // Several corpus fixtures ship a truncated zstd frame (the tail was cut
            // when the file was produced). Recovering the prefix matches what the
            // reference zstd CLI does, and the payload's own %%EOF tells callers
            // whether it is complete.
        }

        return output.ToArray();
    }
}
