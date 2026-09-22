using System.Globalization;
using System.Text;

namespace VCCad.Pdf.Parsing;

/// <summary>
/// Tokenizer/object reader over a PDF byte buffer. Reads PDF objects (numbers,
/// names, strings, arrays, dictionaries, indirect references) and, when asked,
/// stream bodies. Used both for the file structure and (via its operand reading)
/// for content streams.
/// </summary>
internal ref struct PdfReader
{
    private readonly byte[] _data;
    private int _pos;

    public PdfReader(byte[] data, int position)
    {
        _data = data;
        _pos = position;
    }

    public int Position
    {
        get => _pos;
        set => _pos = value;
    }

    /// <summary>Advances the read position by one byte.</summary>
    public void Advance() => _pos++;

    public int Peek() => _pos < _data.Length ? _data[_pos] : -1;

    public void SkipWhitespace()
    {
        while (_pos < _data.Length)
        {
            byte c = _data[_pos];
            if (c is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)'\f' or 0)
            {
                _pos++;
            }
            else if (c == (byte)'%')
            {
                while (_pos < _data.Length && _data[_pos] != (byte)'\n')
                {
                    _pos++;
                }
            }
            else
            {
                break;
            }
        }
    }

    public void SkipLine()
    {
        while (_pos < _data.Length && _data[_pos] != (byte)'\n')
        {
            _pos++;
        }

        if (_pos < _data.Length)
        {
            _pos++;
        }
    }

    public bool TryReadKeyword(string keyword)
    {
        SkipWhitespace();
        if (_pos + keyword.Length > _data.Length)
        {
            return false;
        }

        for (int i = 0; i < keyword.Length; i++)
        {
            if (_data[_pos + i] != keyword[i])
            {
                return false;
            }
        }

        _pos += keyword.Length;
        return true;
    }

    public long? ReadInteger()
    {
        SkipWhitespace();
        int start = _pos;
        if (_pos < _data.Length && (_data[_pos] == (byte)'-' || _data[_pos] == (byte)'+'))
        {
            _pos++;
        }

        while (_pos < _data.Length && char.IsDigit((char)_data[_pos]))
        {
            _pos++;
        }

        if (_pos == start)
        {
            return null;
        }

        return long.TryParse(Encoding.ASCII.GetString(_data, start, _pos - start),
            NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) ? value : null;
    }

    /// <summary>Reads one PDF object.</summary>
    public object? ReadObject(PdfFile file, bool parseStream = false)
    {
        SkipWhitespace();
        if (_pos >= _data.Length)
        {
            return null;
        }

        byte c = _data[_pos];

        if (c == (byte)'/')
        {
            return ReadName();
        }

        if (c == (byte)'(')
        {
            return ReadLiteralString();
        }

        if (c == (byte)'<')
        {
            if (_pos + 1 < _data.Length && _data[_pos + 1] == (byte)'<')
            {
                Dictionary<string, object?> dict = ReadDictionary(file);
                if (parseStream)
                {
                    PdfStream? stream = TryReadStream(file, dict);
                    if (stream is not null)
                    {
                        return stream;
                    }
                }

                return dict;
            }

            return ReadHexString();
        }

        if (c == (byte)'[')
        {
            return ReadArray(file);
        }

        if (c is (byte)'+' or (byte)'-' or (byte)'.' || char.IsDigit((char)c))
        {
            return ReadNumberOrRef(file);
        }

        if (TryReadKeyword("true"))
        {
            return true;
        }

        if (TryReadKeyword("false"))
        {
            return false;
        }

        if (TryReadKeyword("null"))
        {
            return null;
        }

        _pos++; // unknown token: skip a byte to guarantee progress
        return null;
    }

    public PdfName ReadName()
    {
        SkipWhitespace();
        if (_pos < _data.Length && _data[_pos] == (byte)'/')
        {
            _pos++;
        }

        var sb = new StringBuilder();
        while (_pos < _data.Length)
        {
            byte c = _data[_pos];
            if (c is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)'/' or (byte)'(' or (byte)')'
                or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'%')
            {
                break;
            }

            if (c == (byte)'#')
            {
                if (_pos + 2 < _data.Length &&
                    int.TryParse(Encoding.ASCII.GetString(_data, _pos + 1, 2), NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture, out int hex))
                {
                    sb.Append((char)hex);
                    _pos += 3;
                    continue;
                }
            }

            sb.Append((char)c);
            _pos++;
        }

        return new PdfName(sb.ToString());
    }

    private object? ReadNumberOrRef(PdfFile file)
    {
        int saved = _pos;
        double number = ReadNumber();

        // Indirect reference? "int int R"
        if (number == Math.Floor(number) && Math.Abs(number) < int.MaxValue)
        {
            int afterFirst = _pos;
            SkipWhitespace();
            int digitStart = _pos;
            if (_pos < _data.Length && char.IsDigit((char)_data[_pos]))
            {
                long? gen = ReadInteger();
                SkipWhitespace();
                if (gen is not null && _pos < _data.Length && _data[_pos] == (byte)'R')
                {
                    _pos++;
                    return new PdfRef((int)number, (int)gen.Value);
                }
            }

            _pos = afterFirst;
        }

        return number;
    }

    public double ReadNumber()
    {
        SkipWhitespace();
        int start = _pos;
        if (_pos < _data.Length && (_data[_pos] == (byte)'-' || _data[_pos] == (byte)'+'))
        {
            _pos++;
        }

        while (_pos < _data.Length && (char.IsDigit((char)_data[_pos]) || _data[_pos] == (byte)'.'))
        {
            _pos++;
        }

        string text = Encoding.ASCII.GetString(_data, start, _pos - start);
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : 0.0;
    }

    private object? ReadLiteralString()
    {
        _pos++; // (
        var sb = new StringBuilder();
        int depth = 1;
        while (_pos < _data.Length && depth > 0)
        {
            byte c = _data[_pos++];
            if (c == (byte)'\\')
            {
                if (_pos >= _data.Length)
                {
                    break;
                }

                byte esc = _data[_pos++];
                sb.Append(esc switch
                {
                    (byte)'n' => '\n',
                    (byte)'r' => '\r',
                    (byte)'t' => '\t',
                    (byte)'b' => '\b',
                    (byte)'f' => '\f',
                    (byte)'(' => '(',
                    (byte)')' => ')',
                    (byte)'\\' => '\\',
                    _ => (char)esc,
                });
            }
            else if (c == (byte)'(')
            {
                depth++;
                sb.Append('(');
            }
            else if (c == (byte)')')
            {
                depth--;
                if (depth > 0)
                {
                    sb.Append(')');
                }
            }
            else
            {
                sb.Append((char)c);
            }
        }

        return sb.ToString();
    }

    private object? ReadHexString()
    {
        _pos++; // <
        var sb = new StringBuilder();
        while (_pos < _data.Length && _data[_pos] != (byte)'>')
        {
            sb.Append((char)_data[_pos++]);
        }

        if (_pos < _data.Length)
        {
            _pos++; // >
        }

        return sb.ToString();
    }

    private List<object?> ReadArray(PdfFile file)
    {
        _pos++; // [
        var list = new List<object?>();
        while (true)
        {
            SkipWhitespace();
            if (_pos >= _data.Length || _data[_pos] == (byte)']')
            {
                if (_pos < _data.Length)
                {
                    _pos++;
                }

                break;
            }

            list.Add(ReadObject(file));
        }

        return list;
    }

    private Dictionary<string, object?> ReadDictionary(PdfFile file)
    {
        _pos += 2; // <<
        var dict = new Dictionary<string, object?>();
        while (true)
        {
            SkipWhitespace();
            if (_pos >= _data.Length)
            {
                break;
            }

            if (_data[_pos] == (byte)'>' && _pos + 1 < _data.Length && _data[_pos + 1] == (byte)'>')
            {
                _pos += 2;
                break;
            }

            if (_data[_pos] != (byte)'/')
            {
                _pos++; // malformed; skip
                continue;
            }

            PdfName key = ReadName();
            object? value = ReadObject(file);
            dict[key.Value] = value;
        }

        return dict;
    }

    private PdfStream? TryReadStream(PdfFile file, Dictionary<string, object?> dict)
    {
        SkipWhitespace();
        if (!TryReadKeyword("stream"))
        {
            return null;
        }

        // Consume the EOL after "stream".
        if (_pos < _data.Length && _data[_pos] == (byte)'\r')
        {
            _pos++;
        }

        if (_pos < _data.Length && _data[_pos] == (byte)'\n')
        {
            _pos++;
        }

        int dataStart = _pos;
        double? length = file.ResolveNumber(dict.GetValueOrDefault("Length"));
        int dataLength;

        if (length is not null && length.Value >= 0 && dataStart + length.Value <= _data.Length)
        {
            dataLength = (int)length.Value;
        }
        else
        {
            int end = FindKeyword("endstream", dataStart);
            dataLength = (end < 0 ? _data.Length : end) - dataStart;
        }

        var raw = new byte[dataLength];
        Array.Copy(_data, dataStart, raw, 0, dataLength);
        _pos = dataStart + dataLength;

        int endStream = FindKeyword("endstream", _pos);
        if (endStream >= 0)
        {
            _pos = endStream + "endstream".Length;
        }

        return new PdfStream { Dict = dict, Raw = raw };
    }

    private int FindKeyword(string keyword, int from)
    {
        byte[] pattern = Encoding.ASCII.GetBytes(keyword);
        for (int i = from; i <= _data.Length - pattern.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < pattern.Length; j++)
            {
                if (_data[i + j] != pattern[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return i;
            }
        }

        return -1;
    }
}
