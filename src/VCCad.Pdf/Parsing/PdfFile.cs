using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using VCCad.Pdf.Encryption;

namespace VCCad.Pdf.Parsing;

/// <summary>A PDF name (written <c>/Foo</c>).</summary>
internal sealed record PdfName(string Value);

/// <summary>An indirect reference <c>N G R</c>.</summary>
internal sealed record PdfRef(int Number, int Generation);

/// <summary>A PDF stream: its dictionary plus the raw (still filtered) bytes.</summary>
internal sealed class PdfStream
{
    public required Dictionary<string, object?> Dict { get; init; }

    public required byte[] Raw { get; init; }

    /// <summary>
    /// The object this stream was parsed as. The standard security handler mixes it into the key,
    /// which is what stops a stream being lifted out of one object into another, so a stream that
    /// does not know its own number cannot be decrypted.
    /// </summary>
    public int Number { get; set; }

    /// <summary>The object's generation, the other half of that key material.</summary>
    public int Generation { get; set; }
}

/// <summary>
/// A pragmatic PDF reader: object/xref-stream parsing, object streams, FlateDecode
/// with PNG predictors, and lazy object resolution. Sufficient to read real-world
/// Illustrator/InDesign PDFs for vector import; not a full PDF implementation.
/// </summary>
internal sealed class PdfFile
{
    private readonly byte[] _data;
    private readonly Dictionary<int, long> _offsets = new();
    private readonly Dictionary<int, (int StreamObj, int Index)> _inObjectStream = new();
    private readonly Dictionary<int, object?> _cache = new();
    private readonly Dictionary<int, object?[]> _objectStreams = new();
    private Dictionary<string, object?>? _trailer;
    private bool _securityResolved;
    private PdfStandardSecurity? _security;

    private readonly string? _password;

    /// <param name="password">
    /// The password to open the file with, when it needs one. Tried before the empty password, and
    /// falling back to it, so supplying a password for a file that never needed one still opens.
    /// </param>
    public PdfFile(byte[] data, string? password = null)
    {
        _data = data;
        _password = string.IsNullOrEmpty(password) ? null : password;
        ReadXref();
    }

    /// <summary>
    /// The standard security handler in force, or null when the file is not protected.
    ///
    /// Resolved lazily, because the trailer it needs is only found once the cross-reference has been
    /// read - and a file whose xref has to be found by brute-force scanning gets its trailer late.
    /// The empty password is the one tried: a file carrying only an owner password has nothing for a
    /// person to type, and it is the common case for the artwork this program exists to open.
    /// </summary>
    public PdfStandardSecurity? Security
    {
        get
        {
            if (!_securityResolved)
            {
                _securityResolved = true;

                // The password that was supplied, then the empty one: a file carrying only an owner
                // password has nothing to type, and a password supplied for a file that never needed one
                // must not stop it opening.
                _security = (_password is null ? null : Unlock(_password)) ?? Unlock(string.Empty);
            }

            return _security;
        }
    }

    /// <summary>Whether the file is protected at all, whatever the password situation.</summary>
    public bool IsEncrypted => EncryptDictionary() is not null;

    /// <summary>
    /// The /P permission bits, as the file wrote them.
    ///
    /// Read straight from the /Encrypt dictionary, which is **not itself encrypted** - so the
    /// permissions a file claims can be reported even when it cannot be opened, which is exactly when
    /// a person most needs telling.
    /// </summary>
    public int Permissions
        => (int)(ResolveNumber(EncryptDictionary()?.GetValueOrDefault("P")) ?? 0);

    /// <summary>
    /// Unlocks the file with a password, or returns null when it does not open it. An unprotected
    /// file returns null as well: there is nothing to unlock and nothing to decrypt.
    /// </summary>
    public PdfStandardSecurity? Unlock(string password)
    {
        if (_trailer is null || EncryptDictionary() is not { } encrypt)
        {
            return null;
        }

        return PdfStandardSecurity.TryCreate(encrypt, FirstFileId(), password);
    }

    private Dictionary<string, object?>? EncryptDictionary()
        => _trailer is null ? null : ResolveDict(_trailer.GetValueOrDefault("Encrypt"));

    /// <summary>
    /// The first file identifier, which the key derivation is salted with. A file that declares no
    /// /ID gets none rather than a failure: the hash simply has nothing appended.
    /// </summary>
    private byte[] FirstFileId()
    {
        if (_trailer?.GetValueOrDefault("ID") is not List<object?> ids || ids.Count == 0)
        {
            return Array.Empty<byte>();
        }

        return Resolve(ids[0]) switch
        {
            string text => Encoding.Latin1.GetBytes(text),
            byte[] raw => raw,
            _ => Array.Empty<byte>(),
        };
    }

    /// <summary>All object numbers known from the xref.</summary>
    public IEnumerable<int> ObjectNumbers
        => _offsets.Keys.Concat(_inObjectStream.Keys).Distinct().OrderBy(n => n);

    /// <summary>Object number of the document catalog (/Type /Catalog), if found.</summary>
    public int? FindCatalog()
    {
        foreach (int number in ObjectNumbers)
        {
            if (GetObject(number) is Dictionary<string, object?> dict &&
                dict.GetValueOrDefault("Type") is PdfName { Value: "Catalog" })
            {
                return number;
            }
        }

        return null;
    }

    /// <summary>Resolves indirect references recursively (one level).</summary>
    public object? Resolve(object? value)
        => value is PdfRef r ? GetObject(r.Number) : value;

    public double? ResolveNumber(object? value)
    {
        object? v = Resolve(value);
        return v switch
        {
            double d => d,
            long l => l,
            _ => null,
        };
    }

    public Dictionary<string, object?>? ResolveDict(object? value)
        => Resolve(value) as Dictionary<string, object?>;

    public object? GetObject(int number)
    {
        if (_cache.TryGetValue(number, out object? cached))
        {
            return cached;
        }

        object? result = null;
        if (_offsets.TryGetValue(number, out long offset))
        {
            result = ParseObjectAt(offset, number);
        }
        else if (_inObjectStream.TryGetValue(number, out (int StreamObj, int Index) location))
        {
            object?[] objects = LoadObjectStream(location.StreamObj);
            result = location.Index < objects.Length ? objects[location.Index] : null;
        }

        _cache[number] = result;
        return result;
    }

    /// <summary>Decoded stream bytes (filters applied).</summary>
    public byte[] GetStreamData(PdfStream stream)
    {
        byte[] data = stream.Raw;

        // Decrypt BEFORE the filters, never after: the filters describe the plaintext, and what was
        // encrypted is the filtered bytes. This one place is the whole of reading a protected file -
        // the importer, the renderer and the model above it never learn that it was encrypted, which
        // is why they needed no changes at all.
        string? type = stream.Dict.GetValueOrDefault("Type") is PdfName { Value: var t } ? t : null;

        // An XRef stream is never encrypted (ISO 32000-1, 7.5.8.2), and a metadata stream is only
        // encrypted when the handler says so.
        if (type != "XRef" && Security is { } security)
        {
            data = security.Decrypt(stream.Number, stream.Generation, data, type == "Metadata");
        }
        object? filter = Resolve(stream.Dict.GetValueOrDefault("Filter"));

        bool flate = filter is PdfName { Value: "FlateDecode" }
                     || (filter is List<object?> list && list.OfType<PdfName>().Any(n => n.Value == "FlateDecode"));

        if (flate)
        {
            try
            {
                using var input = new MemoryStream(data);
                using var zlib = new ZLibStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream();
                zlib.CopyTo(output);
                data = output.ToArray();
            }
            catch (InvalidDataException)
            {
                return Array.Empty<byte>();
            }

            data = ApplyPredictor(stream, data);
        }

        return data;
    }

    // ------------------------------------------------------------------
    // xref / object streams
    // ------------------------------------------------------------------

    private void ReadXref()
    {
        int startxref = LastIndexOf("%%EOF");
        string tail = Encoding.Latin1.GetString(_data, Math.Max(0, startxref - 200), Math.Min(200, _data.Length - Math.Max(0, startxref - 200)));
        int idx = tail.LastIndexOf("startxref", StringComparison.Ordinal);
        if (idx < 0)
        {
            BruteForceScan();
            return;
        }

        string num = tail[(idx + 9)..].Trim().Split('\n', '\r', ' ')[0];
        if (!long.TryParse(num, NumberStyles.Integer, CultureInfo.InvariantCulture, out long xrefOffset))
        {
            BruteForceScan();
            return;
        }

        // Walk the /Prev chain newest-first so an incremental update's entries
        // win over the older sections it supersedes. (Anything not redefined in
        // the update is only present in the previous section.)
        long current = xrefOffset;
        var visited = new HashSet<long>();
        while (current >= 0 && current < _data.Length && visited.Add(current))
        {
            object? xrefObj = ParseObjectAt(current, -1);
            if (xrefObj is PdfStream xrefStream && ResolveDict(xrefStream.Dict) is { } dict &&
                dict.GetValueOrDefault("Type") is PdfName { Value: "XRef" })
            {
                ReadXrefStream(xrefStream);
                current = ResolveNumber(dict.GetValueOrDefault("Prev")) is double prev ? (long)prev : -1;
            }
            else
            {
                current = ReadXrefTable(current) ?? -1;
            }
        }

        if (_offsets.Count == 0)
        {
            BruteForceScan();
        }
    }

    private long? ReadXrefTable(long offset)
    {
        var reader = new PdfReader(_data, (int)offset);
        reader.SkipWhitespace();
        if (!reader.TryReadKeyword("xref"))
        {
            return null;
        }

        // The declared /Count is a claim, not a promise. Every iteration must
        // consume bytes, and the moment the bytes run out (no offset, or EOF) the
        // section is over however large the count says it is. Without this a
        // 130-byte file declaring 2e9 entries loops 2e9 times, and a 13-digit
        // count runs for about an hour: work proportional to a number in the
        // file rather than to the file.
        while (true)
        {
            reader.SkipWhitespace();
            int peeked = reader.Peek();
            if (peeked < 0) // EOF
            {
                break;
            }

            if (peeked == 't') // trailer
            {
                break;
            }

            long? start = reader.ReadInteger();
            long? count = reader.ReadInteger();
            if (start is null || count is null)
            {
                break;
            }

            bool exhausted = false;
            for (long i = 0; i < count.Value; i++)
            {
                long? entryOffset = reader.ReadInteger();
                long? gen = reader.ReadInteger();
                if (entryOffset is null || gen is null)
                {
                    exhausted = true;
                    break;
                }

                reader.SkipWhitespace();
                int typePeek = reader.Peek();
                if (typePeek < 0)
                {
                    exhausted = true;
                    break;
                }

                char type = (char)typePeek;
                reader.SkipLine();
                if (type != 'n')
                {
                    continue;
                }

                int number = (int)(start.Value + i);
                if (!_offsets.ContainsKey(number))
                {
                    _offsets[number] = entryOffset.Value;
                }
            }

            if (exhausted)
            {
                break;
            }
        }

        if (reader.TryReadKeyword("trailer") &&
            reader.ReadObject(this) is Dictionary<string, object?> trailer)
        {
            // The trailer carries /Encrypt and /ID, which is everything the security handler needs.
            _trailer ??= trailer;

            if (ResolveNumber(trailer.GetValueOrDefault("Prev")) is double prev)
            {
                return (long)prev;
            }
        }

        return null;
    }

    private void ReadXrefStream(PdfStream xref)
    {
        // An xref STREAM carries the trailer's entries in its own dictionary, so an incremental
        // update that uses one brings /Encrypt and /ID with it.
        _trailer ??= xref.Dict;
        if (Resolve(xref.Dict.GetValueOrDefault("W")) is not List<object?> widths || widths.Count < 3)
        {
            BruteForceScan();
            return;
        }

        int[] w = widths.Select(x => (int)(Convert.ToDouble(Resolve(x) ?? 0.0))).ToArray();
        byte[] data = GetStreamData(xref);

        object? indexObj = Resolve(xref.Dict.GetValueOrDefault("Index"));
        var index = new List<long>();
        if (indexObj is List<object?> indexList)
        {
            index.AddRange(indexList.Select(x => (long)Convert.ToDouble(Resolve(x) ?? 0.0)));
        }
        else
        {
            index.Add(0);
            index.Add((long)(ResolveNumber(xref.Dict.GetValueOrDefault("Size")) ?? 0));
        }

        int pos = 0;
        for (int s = 0; s + 1 < index.Count; s += 2)
        {
            long start = index[s];
            long count = index[s + 1];
            for (long i = 0; i < count && pos + w[0] + w[1] + w[2] <= data.Length; i++)
            {
                long type = w[0] == 0 ? 1 : ReadField(data, ref pos, w[0]);
                long field2 = ReadField(data, ref pos, w[1]);
                long field3 = ReadField(data, ref pos, w[2]);
                int number = (int)(start + i);

                if (type == 1)
                {
                    if (!_offsets.ContainsKey(number))
                    {
                        _offsets[number] = field2;
                    }
                }
                else if (type == 2)
                {
                    if (!_inObjectStream.ContainsKey(number))
                    {
                        _inObjectStream[number] = ((int)field2, (int)field3);
                    }
                }
            }
        }
    }

    private static long ReadField(byte[] data, ref int pos, int width)
    {
        long value = 0;
        for (int i = 0; i < width; i++)
        {
            value = (value << 8) | data[pos++];
        }

        return value;
    }

    private object?[] LoadObjectStream(int streamNumber)
    {
        if (_objectStreams.TryGetValue(streamNumber, out object?[]? cached))
        {
            return cached;
        }

        if (GetObject(streamNumber) is not PdfStream stream)
        {
            return _objectStreams[streamNumber] = Array.Empty<object?>();
        }

        int n = (int)(ResolveNumber(stream.Dict.GetValueOrDefault("N")) ?? 0);
        int first = (int)(ResolveNumber(stream.Dict.GetValueOrDefault("First")) ?? 0);
        byte[] data = GetStreamData(stream);

        // Header: N pairs of "objnum offset".
        var reader = new PdfReader(data, 0);
        var numbers = new int[n];
        var offsets = new int[n];
        for (int i = 0; i < n; i++)
        {
            numbers[i] = (int)(reader.ReadInteger() ?? 0);
            offsets[i] = (int)(reader.ReadInteger() ?? 0);
        }

        var objects = new object?[n];
        for (int i = 0; i < n; i++)
        {
            var objReader = new PdfReader(data, first + offsets[i]);
            objects[i] = objReader.ReadObject(this);
        }

        return _objectStreams[streamNumber] = objects;
    }

    private void BruteForceScan()
    {
        // Fallback: scan for "N 0 obj" markers anywhere in the file.
        string text = Encoding.Latin1.GetString(_data);
        int index = 0;
        while (true)
        {
            int obj = text.IndexOf(" 0 obj", index, StringComparison.Ordinal);
            if (obj < 0)
            {
                break;
            }

            int start = obj - 1;
            while (start >= 0 && char.IsDigit(text[start]))
            {
                start--;
            }

            string numText = text[(start + 1)..obj];
            if (int.TryParse(numText, out int number) && !_offsets.ContainsKey(number))
            {
                _offsets[number] = start + 1;
            }

            index = obj + 6;
        }
    }

    private object? ParseObjectAt(long offset, int expectedNumber)
    {
        var reader = new PdfReader(_data, (int)offset);

        // Skip the "N G obj" header before the object body.
        long? number = reader.ReadInteger();
        long? generation = reader.ReadInteger();
        reader.TryReadKeyword("obj");
        object? result = reader.ReadObject(this, parseStream: true);

        if (result is PdfStream stream)
        {
            stream.Number = (int)(number ?? expectedNumber);
            stream.Generation = (int)(generation ?? 0);
        }

        return result;
    }

    private int LastIndexOf(string token)
    {
        string text = Encoding.Latin1.GetString(_data);
        return text.LastIndexOf(token, StringComparison.Ordinal);
    }

    private byte[] ApplyPredictor(PdfStream stream, byte[] data)
    {
        object? parms = Resolve(stream.Dict.GetValueOrDefault("DecodeParms"));
        if (parms is List<object?> list)
        {
            parms = list.FirstOrDefault();
        }

        if (ResolveDict(parms) is not { } dict)
        {
            return data;
        }

        int predictor = (int)(ResolveNumber(dict.GetValueOrDefault("Predictor")) ?? 1);
        if (predictor < 10)
        {
            return data;
        }

        int colors = (int)(ResolveNumber(dict.GetValueOrDefault("Colors")) ?? 1);
        int bpc = (int)(ResolveNumber(dict.GetValueOrDefault("BitsPerComponent")) ?? 8);
        int columns = (int)(ResolveNumber(dict.GetValueOrDefault("Columns")) ?? 1);

        int bytesPerPixel = Math.Max(1, colors * bpc / 8);
        int rowLength = (colors * bpc * columns + 7) / 8;
        int outLength = rowLength * (data.Length / (rowLength + 1));

        var output = new byte[outLength];
        byte[]? prior = null;
        int inPos = 0;
        int outPos = 0;

        while (inPos + 1 <= data.Length && outPos + rowLength <= output.Length)
        {
            int filterType = data[inPos++];
            byte[] row = new byte[rowLength];
            Array.Copy(data, inPos, row, 0, Math.Min(rowLength, data.Length - inPos));
            inPos += rowLength;

            for (int i = 0; i < rowLength; i++)
            {
                int left = i >= bytesPerPixel ? row[i - bytesPerPixel] : 0;
                int up = prior?[i] ?? 0;
                int upLeft = prior is not null && i >= bytesPerPixel ? prior[i - bytesPerPixel] : 0;
                row[i] = filterType switch
                {
                    0 => row[i],
                    1 => (byte)(row[i] + left),
                    2 => (byte)(row[i] + up),
                    3 => (byte)(row[i] + (left + up) / 2),
                    4 => (byte)(row[i] + Paeth(left, up, upLeft)),
                    _ => row[i],
                };
            }

            Array.Copy(row, 0, output, outPos, rowLength);
            outPos += rowLength;
            prior = row;
        }

        return output;
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }
}
