using System.Globalization;

namespace VCCad.Pdf.Ai;

/// <summary>
/// Tokenizer for a decoded Illustrator payload: <c>%</c>-directives, DSC comments,
/// <c>%%BeginData</c> blocks and layer nesting.
///
/// The parse is lossless by construction: the input is cut into lines that keep
/// their terminators, and every element stores the exact slice it covers. The
/// structured members (directive key/value, layer name and flags, decoded data
/// bytes) are <em>views</em> over those slices, so no numeric or string
/// reformatting can lose information on the way back out.
/// </summary>
public static class AiPayloadParser
{
    private const string BeginLayerMarker = "%AI5_BeginLayer";
    private const string EndLayerMarker = "%AI5_EndLayer";
    private const string BeginDataMarker = "%%BeginData";
    private const string BeginDataMarkerShort = "%BeginData";
    private const string EndDataMarker = "%%EndData";
    private const string EndDataMarkerShort = "%EndData";

    /// <summary>
    /// Cuts a payload into ordered elements. Line terminators are preserved verbatim,
    /// and the concatenation of every element's <see cref="AiPayloadElement.Raw"/> is
    /// exactly <paramref name="text"/>.
    /// </summary>
    public static IReadOnlyList<AiPayloadElement> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        List<string> lines = SplitLines(text);
        var elements = new List<AiPayloadElement>(Math.Max(4, lines.Count));
        int depth = 0;
        int index = 0;

        while (index < lines.Count)
        {
            string line = lines[index];
            string body = TrimEol(line);
            int lineNumber = index;

            if (body.StartsWith(BeginLayerMarker, StringComparison.Ordinal))
            {
                (AiLayerBegin begin, int consumed) = ParseLayerBegin(lines, index, depth, body);
                elements.Add(begin);
                depth++;
                index += consumed;
                continue;
            }

            if (body.StartsWith(EndLayerMarker, StringComparison.Ordinal))
            {
                // The element records the depth of the layer it closes (before the
                // decrement) so the tree projection can pair it with its BeginLayer.
                elements.Add(new AiLayerEnd(line) { LineNumber = lineNumber, Depth = depth });
                depth = Math.Max(0, depth - 1);
                index++;
                continue;
            }

            if (IsBeginData(body) && TryParseDataBlock(lines, index, depth, out AiDataBlock? block, out int blockLines))
            {
                elements.Add(block!);
                index += blockLines;
                continue;
            }

            elements.Add(body.StartsWith('%')
                ? ParseDirective(line, body, depth, lineNumber)
                : new AiRawLine(line) { LineNumber = lineNumber, Depth = depth });
            index++;
        }

        return elements;
    }

    /// <summary>
    /// Splits text into lines that each retain their terminator. Only <c>\n</c> is
    /// treated as a terminator, so a lone <c>\r</c> stays inside its line and the
    /// concatenation is always byte-identical to the input.
    /// </summary>
    public static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                lines.Add(text[start..(i + 1)]);
                start = i + 1;
            }
        }

        if (start < text.Length)
        {
            lines.Add(text[start..]);
        }

        return lines;
    }

    /// <summary>Removes at most one CRLF/CR/LF terminator from a line.</summary>
    internal static string TrimEol(string line)
    {
        int end = line.Length;
        if (end > 0 && line[end - 1] == '\n')
        {
            end--;
        }

        if (end > 0 && line[end - 1] == '\r')
        {
            end--;
        }

        return line[..end];
    }

    private static bool IsBeginData(string body)
        => body.StartsWith(BeginDataMarker, StringComparison.Ordinal)
           || body.StartsWith(BeginDataMarkerShort, StringComparison.Ordinal);

    private static bool IsEndData(string body)
        => body is EndDataMarker || body is EndDataMarkerShort;

    // ------------------------------------------------------------------
    // Layers
    // ------------------------------------------------------------------

    private static (AiLayerBegin Element, int Consumed) ParseLayerBegin(
        List<string> lines, int index, int depth, string body)
    {
        string beginLine = lines[index];
        int consumed = 1;

        string? lbLine = null;
        IReadOnlyList<double> lbParameters = Array.Empty<double>();
        if (index + consumed < lines.Count &&
            TryParseLb(TrimEol(lines[index + consumed]), out double[]? parameters))
        {
            lbLine = lines[index + consumed];
            lbParameters = parameters!;
            consumed++;
        }

        string? nameLine = null;
        string name = string.Empty;
        if (index + consumed < lines.Count &&
            TryParseLn(TrimEol(lines[index + consumed]), out string? parsedName))
        {
            nameLine = lines[index + consumed];
            name = parsedName!;
            consumed++;
        }

        string? expandLine = null;
        bool expanded = false;
        if (index + consumed < lines.Count &&
            TryParseExpand(TrimEol(lines[index + consumed]), out bool isExpanded))
        {
            expandLine = lines[index + consumed];
            expanded = isExpanded;
            consumed++;
        }

        LayerFlags flags = LayerFlags.From(lbParameters);
        var element = new AiLayerBegin
        {
            BeginLine = beginLine,
            LbLine = lbLine,
            NameLine = nameLine,
            ExpandLine = expandLine,
            LbParameters = lbParameters,
            Name = name,
            IsVisible = flags.IsVisible,
            IsVisibleFlag = flags.IsVisibleFlag,
            ColorIndex = flags.ColorIndex,
            Red = flags.Red,
            Green = flags.Green,
            Blue = flags.Blue,
            IsExpanded = expanded,
            LineNumber = index,
            Depth = depth,
        };

        return (element, consumed);
    }

    /// <summary>
    /// The <c>Lb</c> operand list is <c>visible preview enabled printing dimmed
    /// hasMultiLayerMasks [visible?] colorIndex red green blue [0 50 0]</c>.
    ///
    /// The bracketed members arrived over the product's life — <c>visible?</c> with
    /// AI8, the trailing trio no later than CS2 — so the layout is selected by
    /// length rather than assumed: 10 operands is the pre-AI8 form (colourIndex at 6,
    /// colour at 7..9) and 11 or more is the extended form (visible? at 6, colourIndex
    /// at 7, colour at 8..10). The fixture corpus exercises exactly the 10- and
    /// 14-operand forms.
    /// </summary>
    private readonly record struct LayerFlags(
        bool IsVisible, bool IsVisibleFlag, int ColorIndex, int Red, int Green, int Blue)
    {
        public static LayerFlags From(IReadOnlyList<double> parameters)
        {
            if (parameters.Count == 0)
            {
                return new LayerFlags(true, true, -1, -1, -1, -1);
            }

            bool visible = parameters[0] != 0;
            bool extended = parameters.Count >= 11;
            bool visibleFlag = extended ? parameters[6] != 0 : visible;
            int colorIndex = extended ? 7 : 6;
            int colorBase = extended ? 8 : 7;

            if (colorBase + 2 >= parameters.Count)
            {
                return new LayerFlags(visible, visibleFlag, -1, -1, -1, -1);
            }

            return new LayerFlags(
                visible,
                visibleFlag,
                (int)parameters[colorIndex],
                (int)parameters[colorBase],
                (int)parameters[colorBase + 1],
                (int)parameters[colorBase + 2]);
        }
    }

    /// <summary>Matches <c>&lt;numbers…&gt; Lb</c>.</summary>
    private static bool TryParseLb(string body, out double[]? parameters)
    {
        parameters = null;
        if (!body.EndsWith(" Lb", StringComparison.Ordinal))
        {
            return false;
        }

        string[] tokens = body[..^3].Split(
            new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 6)
        {
            return false;
        }

        var values = new double[tokens.Length];
        for (int i = 0; i < tokens.Length; i++)
        {
            if (!double.TryParse(tokens[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
            {
                return false;
            }
        }

        parameters = values;
        return true;
    }

    /// <summary>Matches <c>(name) Ln</c>, unescaping PostScript string escapes.</summary>
    private static bool TryParseLn(string body, out string? name)
    {
        name = null;
        if (!body.StartsWith('(') || !body.EndsWith(") Ln", StringComparison.Ordinal))
        {
            return false;
        }

        name = Unescape(body[1..^4]);
        return true;
    }

    /// <summary>Matches <c>0|1 AE</c> (Layers-panel expanded state, added in CC Legacy).</summary>
    private static bool TryParseExpand(string body, out bool expanded)
    {
        expanded = false;
        if (!body.EndsWith(" AE", StringComparison.Ordinal))
        {
            return false;
        }

        string head = body[..^3].Trim();
        if (head is not ("0" or "1"))
        {
            return false;
        }

        expanded = head == "1";
        return true;
    }

    /// <summary>
    /// Reverses the PostScript string escapes Illustrator uses inside <c>Ln</c>
    /// names: <c>\\(</c>, <c>\\)</c>, <c>\\\\</c> and octal <c>\\ddd</c>.
    /// </summary>
    internal static string Unescape(string value)
    {
        if (!value.Contains('\\', StringComparison.Ordinal))
        {
            return value;
        }

        var builder = new System.Text.StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c != '\\' || i + 1 >= value.Length)
            {
                builder.Append(c);
                continue;
            }

            char next = value[++i];
            switch (next)
            {
                case 'n':
                    builder.Append('\n');
                    break;
                case 'r':
                    builder.Append('\r');
                    break;
                case 't':
                    builder.Append('\t');
                    break;
                case 'b':
                    builder.Append('\b');
                    break;
                case 'f':
                    builder.Append('\f');
                    break;
                default:
                    if (next is >= '0' and <= '7')
                    {
                        int octal = next - '0';
                        int digits = 1;
                        while (digits < 3 && i + 1 < value.Length && value[i + 1] is >= '0' and <= '7')
                        {
                            octal = (octal * 8) + (value[++i] - '0');
                            digits++;
                        }

                        builder.Append((char)octal);
                    }
                    else
                    {
                        builder.Append(next);
                    }

                    break;
            }
        }

        return builder.ToString();
    }

    // ------------------------------------------------------------------
    // %%BeginData blocks
    // ------------------------------------------------------------------

    private static bool TryParseDataBlock(
        List<string> lines, int index, int depth, out AiDataBlock? block, out int consumed)
    {
        block = null;
        consumed = 0;

        int end = index + 1;
        while (end < lines.Count && !IsEndData(TrimEol(lines[end])))
        {
            end++;
        }

        if (end >= lines.Count)
        {
            // Unterminated: leave the line to the ordinary directive path so nothing
            // is swallowed.
            return false;
        }

        string beginBody = TrimEol(lines[index]);
        (AiDataEncoding encoding, int declared) = ParseDataHeader(beginBody);
        var content = new List<string>(end - index - 1);
        for (int i = index + 1; i < end; i++)
        {
            content.Add(lines[i]);
        }

        if (encoding == AiDataEncoding.Unknown && content.Count > 0 && LooksLikeHexContent(content[0]))
        {
            encoding = AiDataEncoding.Hex;
        }

        block = new AiDataBlock
        {
            BeginLine = lines[index],
            ContentLines = content,
            EndLine = lines[end],
            Encoding = encoding,
            DeclaredByteCount = declared,
            Data = DecodeData(content, encoding, declared),
            LineNumber = index,
            Depth = depth,
        };
        consumed = end - index + 1;
        return true;
    }

    /// <summary>Parses <c>%%BeginData: 6490 Hex Bytes</c> into its encoding and byte count.</summary>
    private static (AiDataEncoding Encoding, int DeclaredBytes) ParseDataHeader(string body)
    {
        int colon = body.IndexOf(':');
        if (colon < 0)
        {
            return (AiDataEncoding.Unknown, -1);
        }

        string[] tokens = body[(colon + 1)..].Split(
            new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0 || !int.TryParse(tokens[0], NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int count))
        {
            return (AiDataEncoding.Unknown, -1);
        }

        AiDataEncoding encoding = tokens.Length >= 2
            ? tokens[1].ToLowerInvariant() switch
            {
                "hex" => AiDataEncoding.Hex,
                "ascii" => AiDataEncoding.Ascii,
                "binary" => AiDataEncoding.Binary,
                _ => AiDataEncoding.Unknown,
            }
            : AiDataEncoding.Unknown;

        return (encoding, count);
    }

    private static bool LooksLikeHexContent(string line)
        => line.StartsWith('%') && line.Length > 1 && IsHexDigit(line[1]);

    private static bool IsHexDigit(char c)
        => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

    /// <summary>
    /// Decodes the payload of a data block. Hex blocks store <c>%</c> plus hex digit
    /// pairs per line (the <c>%</c> keeps them inside a PostScript comment); ASCII
    /// blocks carry the bytes literally; binary blocks are already raw bytes and are
    /// returned as the Latin-1 projection of the text.
    /// </summary>
    private static byte[] DecodeData(IReadOnlyList<string> content, AiDataEncoding encoding, int declared)
    {
        switch (encoding)
        {
            case AiDataEncoding.Hex:
            {
                var bytes = new List<byte>(Math.Max(0, declared));
                foreach (string line in content)
                {
                    string body = TrimEol(line);
                    int i = body.StartsWith('%') ? 1 : 0;
                    for (; i + 1 < body.Length; i += 2)
                    {
                        if (!IsHexDigit(body[i]) || !IsHexDigit(body[i + 1]))
                        {
                            break;
                        }

                        bytes.Add(Convert.ToByte(body.Substring(i, 2), 16));
                    }
                }

                return bytes.ToArray();
            }

            case AiDataEncoding.Ascii:
            case AiDataEncoding.Binary:
            case AiDataEncoding.Unknown:
            default:
            {
                var builder = new System.Text.StringBuilder();
                foreach (string line in content)
                {
                    builder.Append(TrimEol(line));
                }

                return System.Text.Encoding.Latin1.GetBytes(builder.ToString());
            }
        }
    }

    // ------------------------------------------------------------------
    // Directives
    // ------------------------------------------------------------------

    private static AiDirective ParseDirective(string line, string body, int depth, int lineNumber)
    {
        AiDirectiveKind kind = body switch
        {
            _ when body.StartsWith("%_", StringComparison.Ordinal) => AiDirectiveKind.ArtData,
            _ when body.StartsWith("%%", StringComparison.Ordinal) => AiDirectiveKind.Dsc,
            _ when body.StartsWith("%AI", StringComparison.Ordinal) => AiDirectiveKind.AiHeader,
            _ => AiDirectiveKind.Comment,
        };

        int keyEnd = 0;
        while (keyEnd < body.Length && !char.IsWhiteSpace(body[keyEnd]) && body[keyEnd] != ':')
        {
            keyEnd++;
        }

        string key = body[..keyEnd];
        string rest = body[keyEnd..].TrimStart();
        if (rest.StartsWith(':'))
        {
            rest = rest[1..].TrimStart();
        }

        return new AiDirective(line, key, rest.Length == 0 ? null : rest, kind)
        {
            LineNumber = lineNumber,
            Depth = depth,
        };
    }
}
