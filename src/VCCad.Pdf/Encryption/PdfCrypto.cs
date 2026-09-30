using System.Security.Cryptography;

namespace VCCad.Pdf.Encryption;

/// <summary>
/// The primitives the standard security handler is built from.
///
/// Everything here is either in the framework or short enough to keep beside its use: MD5, SHA-2 and
/// AES come from <see cref="System.Security.Cryptography"/>; RC4 is a twenty-line cipher that the
/// framework deliberately does not provide, and it is here rather than in a dependency because a
/// PDF from 1996 is still a PDF we have to read.
/// </summary>
internal static class PdfCrypto
{
    /// <summary>
    /// The 32 bytes every candidate password is padded with before it is hashed (ISO 32000-1,
    /// 7.6.3.3). Padding rather than appending a terminator is what makes "no password" and a
    /// password that begins with the same bytes hash differently.
    /// </summary>
    public static readonly byte[] PasswordPadding =
    {
        0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41, 0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
        0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80, 0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A,
    };

    /// <summary>A password padded to the 32 bytes the handler hashes.</summary>
    public static byte[] Pad(string password)
    {
        // The password is a byte string, not text: taking it as Latin-1 keeps each byte it was given,
        // which is what an operator typing a password from the file's own documentation needs.
        byte[] given = new byte[password.Length];
        for (int i = 0; i < password.Length; i++)
        {
            given[i] = (byte)(password[i] & 0xFF);
        }

        var padded = new byte[32];
        int take = Math.Min(given.Length, 32);
        Array.Copy(given, padded, take);
        Array.Copy(PasswordPadding, 0, padded, take, 32 - take);
        return padded;
    }

    public static byte[] Md5(params byte[][] parts)
    {
        using var md5 = MD5.Create();
        int total = parts.Sum(p => p.Length);
        var buffer = new byte[total];
        int at = 0;
        foreach (byte[] part in parts)
        {
            Array.Copy(part, 0, buffer, at, part.Length);
            at += part.Length;
        }

        return md5.ComputeHash(buffer);
    }

    public static byte[] Sha256(byte[] data) => SHA256.HashData(data);

    public static byte[] Sha384(byte[] data) => SHA384.HashData(data);

    public static byte[] Sha512(byte[] data) => SHA512.HashData(data);

    /// <summary>RC4, in place by construction: a key of 5 to 16 bytes and the data it scrambles.</summary>
    public static byte[] Rc4(byte[] key, byte[] data)
    {
        var s = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            s[i] = (byte)i;
        }

        int j = 0;
        for (int i = 0; i < 256; i++)
        {
            j = (j + s[i] + key[i % key.Length]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
        }

        var output = new byte[data.Length];
        int x = 0;
        int y = 0;
        for (int i = 0; i < data.Length; i++)
        {
            x = (x + 1) & 0xFF;
            y = (y + s[x]) & 0xFF;
            (s[x], s[y]) = (s[y], s[x]);
            output[i] = (byte)(data[i] ^ s[(s[x] + s[y]) & 0xFF]);
        }

        return output;
    }

    /// <summary>
    /// AES-256 in CBC with no padding - the mode PDF uses for everything in the AES handler.
    ///
    /// "No padding" is the spec's word, not a shortcut: the encrypted data is always a whole number
    /// of blocks, and a handler that added PKCS#7 stripping here would quietly swallow valid bytes.
    /// </summary>
    public static byte[] AesCbcDecrypt(byte[] key, byte[] iv, byte[] data)
    {
        using var aes = Aes.Create();

        // The key size has to be stated before the key: Aes.Create() defaults to 256 bits, and handing
        // a 16-byte key to a 256-bit instance is refused. The revision 6 hash needs AES-128.
        aes.KeySize = key.Length * 8;
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;

        using ICryptoTransform decryptor = aes.CreateDecryptor();
        int blocks = data.Length / 16 * 16;
        if (blocks == 0)
        {
            return Array.Empty<byte>();
        }

        return decryptor.TransformFinalBlock(data, 0, blocks);
    }

    /// <summary>AES-CBC where the first 16 bytes of <paramref name="data"/> are the IV.</summary>
    public static byte[] AesCbcDecryptWithIvPrefix(byte[] key, byte[] data)
    {
        if (data.Length <= 16)
        {
            return Array.Empty<byte>();
        }

        var iv = new byte[16];
        Array.Copy(data, iv, 16);

        var ciphertext = new byte[data.Length - 16];
        Array.Copy(data, 16, ciphertext, 0, ciphertext.Length);

        return AesCbcDecrypt(key, iv, ciphertext);
    }

    /// <summary>A 128-bit key split as two 64-bit halves, used by the AES-256 revision-6 hash.</summary>
    public static byte[] AesCbcEncryptNoPadding(byte[] key, byte[] iv, byte[] data)
    {
        using var aes = Aes.Create();
        aes.KeySize = key.Length * 8;
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;

        using ICryptoTransform encryptor = aes.CreateEncryptor();
        return encryptor.TransformFinalBlock(data, 0, data.Length / 16 * 16);
    }
}
