using System.Buffers.Binary;
using VCCad.Pdf.Parsing;

namespace VCCad.Pdf.Encryption;

/// <summary>
/// The standard security handler (ISO 32000-1, 7.6.3), which is what `qpdf --decrypt` implements and
/// what almost every protected PDF uses.
///
/// Encryption is applied to *streams and strings*, not to the file's structure: the object graph, the
/// cross-reference and this very dictionary are readable without a password, and only the content is
/// scrambled. That is why a protected file opens **empty** rather than failing - the structure parses
/// perfectly and every stream is ciphertext - and it is why decryption belongs at the point where
/// stream and string bytes are read, so nothing above this ever learns the file was encrypted.
///
/// Four generations of the handler have to be understood, because files from all four are still in
/// circulation:
///
/// | /V | /R | key | per object |
/// |----|----|-----|-----------|
/// | 1  | 2  | RC4 40-bit, MD5 of the padded password and /O | MD5(key+obj+gen), 10 bytes |
/// | 2  | 3  | RC4 of /Length, 50 MD5 rounds | as above, key length + 5 |
/// | 4  | 4  | as R3, or AES-128 when /CFM is /AESV2 | AES appends "sAlT" before the MD5 |
/// | 5  | 5/6| AES-256, the key wrapped in /UE, unwrapped by SHA-2 over the password | none |
/// </summary>
internal sealed class PdfStandardSecurity
{
    private readonly byte[] _key;
    private readonly bool _aes;
    private readonly bool _encryptMetadata;
    private readonly int _version;
    private readonly int _revision;

    private PdfStandardSecurity(
        byte[] key, bool aes, bool encryptMetadata, int version, int revision,
        int permissions, bool ownerPassword, string cipher)
    {
        _key = key;
        _aes = aes;
        _encryptMetadata = encryptMetadata;
        _version = version;
        _revision = revision;
        Permissions = permissions;
        OpenedWithOwnerPassword = ownerPassword;
        Cipher = cipher;
    }

    /// <summary>The /P permission bits, as the file wrote them (a signed 32-bit value).</summary>
    public int Permissions { get; }

    /// <summary>True when the file opened with its owner password rather than its user password.</summary>
    public bool OpenedWithOwnerPassword { get; }

    /// <summary>The cipher in use, for reporting: RC4-40, RC4-128, AES-128 or AES-256.</summary>
    public string Cipher { get; }

    public int Version => _version;

    public int Revision => _revision;

    /// <summary>
    /// Builds a handler for a protected file, or returns null when the password does not open it.
    ///
    /// The empty password is tried first because a file carrying only an owner password - "do not
    /// edit this drawing" with nothing to type - is the common case, and it is the one that has to
    /// work before any dialog exists to ask for anything else.
    /// </summary>
    public static PdfStandardSecurity? TryCreate(
        Dictionary<string, object?> encrypt, byte[] id0, string password)
    {
        if (encrypt.GetValueOrDefault("Filter") is not PdfName { Value: "Standard" })
        {
            return null;
        }

        int version = (int)(ToDouble(encrypt.GetValueOrDefault("V")) ?? 1);
        int revision = (int)(ToDouble(encrypt.GetValueOrDefault("R")) ?? 2);
        byte[] o = Bytes(encrypt.GetValueOrDefault("O"));
        byte[] u = Bytes(encrypt.GetValueOrDefault("U"));
        int permissions = (int)(ToDouble(encrypt.GetValueOrDefault("P")) ?? 0);
        bool encryptMetadata = encrypt.GetValueOrDefault("EncryptMetadata") is not bool b || b;

        if (o.Length < 32 || u.Length < 32)
        {
            return null;
        }

        if (version >= 5)
        {
            return Aes256(encrypt, o, u, permissions, version, revision, encryptMetadata, password);
        }

        int bits = (int)(ToDouble(encrypt.GetValueOrDefault("Length")) ?? 40);
        bool aes = false;

        if (version == 4)
        {
            // The crypt filter decides the cipher and, for AES-128, the key length is stated in
            // BYTES here rather than in bits as /Length does.
            if (CryptFilter(encrypt) is { } filter)
            {
                aes = string.Equals(filter.Method, "AESV2", StringComparison.Ordinal);
                if (filter.Length > 0)
                {
                    bits = filter.Length * 8;
                }
            }
        }

        int length = Math.Clamp(bits / 8, 5, 16);

        foreach (bool owner in new[] { false, true })
        {
            byte[] candidate = owner ? OwnerPassword(password, o, revision, length) : PdfCrypto.Pad(password);

            byte[] key = FileKey(candidate, o, permissions, id0, revision, length, encryptMetadata);
            if (key.Length == 0 || !Validates(key, u, id0, revision))
            {
                continue;
            }

            string cipher = aes ? "AES-128" : length == 5 ? "RC4-40" : "RC4-128";
            return new PdfStandardSecurity(key, aes, encryptMetadata, version, revision, permissions, owner, cipher);
        }

        return null;
    }

    /// <summary>The AES-256 handler (/V 5), whose key comes out of /UE rather than a hash chain.</summary>
    private static PdfStandardSecurity? Aes256(
        Dictionary<string, object?> encrypt, byte[] o, byte[] u, int permissions,
        int version, int revision, bool encryptMetadata, string password)
    {
        byte[]? ue = OptionalBytes(encrypt.GetValueOrDefault("UE"));
        byte[]? oe = OptionalBytes(encrypt.GetValueOrDefault("OE"));
        if (ue is null || oe is null || ue.Length < 32)
        {
            return null;
        }

        byte[] userKey =
        {
            (byte)(permissions & 0xFF), (byte)((permissions >> 8) & 0xFF),
            (byte)((permissions >> 16) & 0xFF), (byte)((permissions >> 24) & 0xFF),
            0xFF, 0xFF, 0xFF, 0xFF, 0x54, 0x61, 0x64, 0x62, 0x00, 0x00, 0x00, 0x00,
        };

        foreach (bool owner in new[] { false, true })
        {
            // The first 32 bytes of /U and /O are the hash of a candidate password; the next eight are
            // the VALIDATION salt and the eight after that the KEY salt. They are different salts for
            // different jobs, and using the validation one to unwrap the key produces a file key that
            // is simply wrong - which looks like a bad password rather than a mistake.
            byte[] hash = owner ? o : u;
            byte[] validationSalt = hash[32..40];
            byte[] keySalt = hash[40..48];
            byte[] extra = owner ? u : Array.Empty<byte>();

            // Algorithm 2.A step (a): the validation hash says whether this password is the one.
            byte[]? check = Hash(revision, password, validationSalt, extra);
            if (check is null || !check.SequenceEqual(hash[..32]))
            {
                continue;
            }

            byte[]? intermediate = Hash(revision, password, keySalt, extra);
            if (intermediate is null)
            {
                continue;
            }

            // And step (b): the file key is wrapped in /UE (or /OE).
            byte[] fileKey = PdfCrypto.AesCbcDecrypt(intermediate, new byte[16], (owner ? oe : ue)[..32]);
            if (fileKey.Length < 32)
            {
                continue;
            }

            // /Perms is the key's own check value: decrypting it and finding the permission bytes says
            // the key is right far more convincingly than a hash comparison alone.
            if (!PermissionsConfirm(encrypt, fileKey, permissions))
            {
                continue;
            }

            return new PdfStandardSecurity(
                fileKey[..32], aes: true, encryptMetadata, version, revision, permissions, owner,
                "AES-256");
        }

        return null;
    }

    /// <summary>Whether /Perms agrees with the permissions and the key.</summary>
    private static bool PermissionsConfirm(Dictionary<string, object?> encrypt, byte[] fileKey, int permissions)
    {
        if (OptionalBytes(encrypt.GetValueOrDefault("Perms")) is not { Length: >= 16 } perms)
        {
            return true; // absent is not a failure; older writers leave it out
        }

        byte[] plain = PdfCrypto.AesCbcDecrypt(fileKey, new byte[16], perms[..16]);
        if (plain.Length < 16)
        {
            return false;
        }

        return plain[0] == (byte)(permissions & 0xFF)
            && plain[4] == 0xFF && plain[5] == 0xFF && plain[6] == 0xFF && plain[7] == 0xFF
            && (plain[8] == (byte)'T' || plain[8] == (byte)'F');
    }

    /// <summary>
    /// The revision 5/6 password hash, which for revision 6 is the hardened one (ISO 32000-1,
    /// algorithm 2.B): a great deal of SHA-2 and AES for no purpose other than making the hash
    /// expensive to attack.
    /// </summary>
    private static byte[]? Hash(int revision, string password, byte[] salt, byte[] extra)
    {
        // Truncated to 127 bytes, as qpdf does (password.substr(0, 127)) and as the specification
        // says: a longer password is not an error, it is simply cut.
        int take = Math.Min(password.Length, 127);
        var given = new byte[take];
        for (int i = 0; i < take; i++)
        {
            given[i] = (byte)(password[i] & 0xFF);
        }

        if (revision == 5)
        {
            return PdfCrypto.Sha256(Join(given, salt));
        }

        byte[] k = PdfCrypto.Sha256(Join(given, salt, extra));

        // Algorithm 2.B: at least 64 rounds, and then as many more as it takes. The loop ends when the
        // last byte of E is no greater than (rounds - 32), so how many rounds a given password needs
        // is a property of the password, not a constant.
        //
        // Stopping at exactly 64 - which is what this did - gets the right answer only when the 64th
        // round happens to satisfy the test, about one password in eight. That is why one fixture
        // reproduced its /U exactly while others reproduced nothing at all: it was never the hash, the
        // password or the file key, it was the loop ending one round too early on most inputs.
        int round = 0;
        while (true)
        {
            round++;

            // (password || K || udata) repeated 64 times, and the length follows K - which is 32, 48
            // or 64 bytes depending on the previous round's hash. Padding the block out with zeros to
            // some fixed size would encrypt those zeros too and hash them into the result.
            int stride = given.Length + k.Length + extra.Length;
            var block = new byte[stride * 64];

            for (int repeat = 0; repeat < 64; repeat++)
            {
                int at = repeat * stride;
                Array.Copy(given, 0, block, at, given.Length);
                at += given.Length;
                Array.Copy(k, 0, block, at, k.Length);
                at += k.Length;
                Array.Copy(extra, 0, block, at, extra.Length);
            }

            byte[] encrypted = PdfCrypto.AesCbcEncryptNoPadding(k[..16], k[16..32], block);
            int sum = 0;
            for (int i = 0; i < 16; i++)
            {
                sum += encrypted[i];
            }

            k = (sum % 3) switch
            {
                0 => PdfCrypto.Sha256(encrypted),
                1 => PdfCrypto.Sha384(encrypted),
                _ => PdfCrypto.Sha512(encrypted),
            };

            // The last byte of E, against the round number (qpdf: `ch <= round_number - 32`). The
            // termination is reached with probability 1/8 a round, so this is bounded in practice;
            // the cap is only so a corrupt or hostile file cannot spin forever.
            if (round >= 64 && encrypted[^1] <= round - 32)
            {
                break;
            }

            if (round >= 1000)
            {
                break;
            }
        }

        return k[..32];
    }

    /// <summary>
    /// The file encryption key for /V 1, 2 and 4 (algorithm 2): MD5 over the padded password, /O, /P
    /// little-endian and the first file identifier, stretched by 50 rounds from revision 3.
    /// </summary>
    private static byte[] FileKey(
        byte[] paddedPassword, byte[] o, int permissions, byte[] id0, int revision, int length, bool encryptMetadata)
    {
        var p = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(p, permissions);

        byte[] input = encryptMetadata || revision < 4
            ? Join(paddedPassword, o, p, id0)
            : Join(paddedPassword, o, p, id0, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF });

        byte[] hash = PdfCrypto.Md5(input);
        if (revision >= 3)
        {
            for (int i = 0; i < 50; i++)
            {
                hash = PdfCrypto.Md5(hash[..length]);
            }
        }

        return hash[..length];
    }

    /// <summary>
    /// Whether the key is the user password's, by reproducing /U (algorithms 4 and 5).
    ///
    /// This is the only honest test available: the file does not store the password, it stores
    /// something that the right password turns into the right bytes.
    /// </summary>
    private static bool Validates(byte[] key, byte[] u, byte[] id0, int revision)
    {
        if (revision == 2)
        {
            byte[] expected = PdfCrypto.Rc4(key, PdfCrypto.PasswordPadding);
            return expected.SequenceEqual(u[..32]);
        }

        byte[] hash = PdfCrypto.Md5(PdfCrypto.PasswordPadding, id0);
        byte[] test = PdfCrypto.Rc4(key, hash);
        for (int i = 1; i <= 19; i++)
        {
            test = PdfCrypto.Rc4(XorKey(key, i), test);
        }

        return test[..16].SequenceEqual(u[..16]);
    }

    /// <summary>
    /// The user password implied by an owner password (algorithm 3): /O is the user password
    /// encrypted under a key built from the owner's, so decrypting it is how a shop that has the
    /// owner password gets in.
    /// </summary>
    private static byte[] OwnerPassword(string password, byte[] o, int revision, int length)
    {
        byte[] hash = PdfCrypto.Md5(PdfCrypto.Pad(password));
        if (revision >= 3)
        {
            for (int i = 0; i < 50; i++)
            {
                hash = PdfCrypto.Md5(hash[..length]);
            }
        }

        byte[] key = hash[..length];
        byte[] data = (byte[])o.Clone();

        if (revision == 2)
        {
            return PdfCrypto.Rc4(key, data);
        }

        for (int i = 19; i >= 0; i--)
        {
            data = PdfCrypto.Rc4(XorKey(key, i), data);
        }

        return data;
    }

    private static byte[] XorKey(byte[] key, int value)
    {
        var mixed = new byte[key.Length];
        for (int i = 0; i < key.Length; i++)
        {
            mixed[i] = (byte)(key[i] ^ value);
        }

        return mixed;
    }

    /// <summary>
    /// Decrypts one object's bytes.
    ///
    /// Object and generation numbers go into the key for /V 1, 2 and 4 (algorithm 1), which is what
    /// stops a stream being lifted from one object into another; the AES-256 handler derives no
    /// per-object key and instead carries a random initialisation vector in front of the data.
    /// </summary>
    public byte[] Decrypt(int number, int generation, byte[] data, bool isMetadata = false)
    {
        if (data.Length == 0)
        {
            return data;
        }

        if (isMetadata && !_encryptMetadata)
        {
            return data;
        }

        if (_version >= 5)
        {
            return PdfCrypto.AesCbcDecryptWithIvPrefix(_key, data);
        }

        // Three bytes of object number then two of generation, both little-endian (algorithm 1).
        var extra = new byte[5];
        extra[0] = (byte)(number & 0xFF);
        extra[1] = (byte)((number >> 8) & 0xFF);
        extra[2] = (byte)((number >> 16) & 0xFF);
        extra[3] = (byte)(generation & 0xFF);
        extra[4] = (byte)((generation >> 8) & 0xFF);

        byte[] material = _aes
            ? Join(_key, extra[..5], new byte[] { 0x73, 0x41, 0x6C, 0x54 })
            : Join(_key, extra[..5]);

        byte[] objectKey = PdfCrypto.Md5(material)[..Math.Min(_key.Length + 5, 16)];

        if (_aes)
        {
            return PdfCrypto.AesCbcDecryptWithIvPrefix(objectKey, data);
        }

        return PdfCrypto.Rc4(objectKey, data);
    }

    private static byte[] Join(params byte[][] parts)
    {
        int total = parts.Sum(p => p.Length);
        var joined = new byte[total];
        int at = 0;
        foreach (byte[] part in parts)
        {
            Array.Copy(part, 0, joined, at, part.Length);
            at += part.Length;
        }

        return joined;
    }

    private static byte[] Bytes(object? value) => OptionalBytes(value) ?? Array.Empty<byte>();

    private static byte[]? OptionalBytes(object? value) => value switch
    {
        PdfStream => null,
        string text => System.Text.Encoding.Latin1.GetBytes(text),
        byte[] raw => raw,
        _ => null,
    };

    private static double? ToDouble(object? value) => value switch
    {
        double d => d,
        long l => l,
        int i => i,
        _ => null,
    };

    private static (string Method, int Length)? CryptFilter(Dictionary<string, object?> encrypt)
    {
        if (encrypt.GetValueOrDefault("CF") is not Dictionary<string, object?> cf)
        {
            return null;
        }

        string name = encrypt.GetValueOrDefault("StmF") is PdfName { Value: var f } ? f : "Identity";
        if (cf.GetValueOrDefault(name) is not Dictionary<string, object?> filter)
        {
            return null;
        }

        string method = filter.GetValueOrDefault("CFM") is PdfName { Value: var m } ? m : "None";
        int length = (int)(ToDouble(filter.GetValueOrDefault("Length")) ?? 0);
        return (method, length);
    }
}
