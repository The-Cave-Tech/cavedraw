using System.Globalization;
using System.Text;

namespace VCCad.Pdf.Ai;

/// <summary>
/// One ordered piece of a decoded Illustrator payload.
///
/// The parse is deliberately <b>flat</b>: every element knows the exact source text
/// it covers (including line terminators) and the layer nesting depth it sits at.
/// A flat list with verbatim slices is what makes the writer byte-exact — there is
/// no round-tripping of numbers, names or binary data to lose information — while
/// the nested views (<see cref="AiPrivateDataDocument.Layers"/>) are projections
/// rebuilt from the same list.
/// </summary>
public abstract record AiPayloadElement
{
    /// <summary>Exact source text this element covers, line terminators included.</summary>
    public abstract string Raw { get; }

    /// <summary>Zero-based line index of the element's first line in the payload.</summary>
    public int LineNumber { get; init; }

    /// <summary>
    /// Layer nesting depth: 0 outside every <c>%AI5_BeginLayer</c>/<c>%AI5_EndLayer--</c>
    /// pair, 1 inside the outermost layer, and so on.
    /// </summary>
    public int Depth { get; init; }
}

/// <summary>A payload line the tokenizer did not classify; preserved verbatim.</summary>
public sealed record AiRawLine : AiPayloadElement
{
    /// <summary>Creates a raw line from its exact source text.</summary>
    public AiRawLine(string text) => Text = text;

    /// <summary>Exact source text, line terminator included.</summary>
    public string Text { get; }

    /// <inheritdoc />
    public override string Raw => Text;
}

/// <summary>What a directive line is, which determines how its key/value were split.</summary>
public enum AiDirectiveKind
{
    /// <summary>PostScript DSC comment (<c>%%Creator: …</c>, <c>%%BeginProlog</c>, …).</summary>
    Dsc,

    /// <summary>Illustrator document header (<c>%AI5_FileFormat 14.0</c>, <c>%AI9_OpenToView: …</c>).</summary>
    AiHeader,

    /// <summary>Commented-out drawing operator (<c>%_0 D</c>, <c>%_/ArtDictionary :</c>).</summary>
    ArtData,

    /// <summary><c>%</c>-prefixed line that is none of the above (calls, comments, palettes).</summary>
    Comment,
}

/// <summary>
/// A <c>%</c>-prefixed header/directive line, split into its leading token and the
/// remainder. The payload text is never reconstructed from these parts: they are a
/// convenience view over <see cref="Line"/>, which the writer emits verbatim.
/// </summary>
public sealed record AiDirective : AiPayloadElement
{
    /// <summary>Creates a directive from its line, key, value and classification.</summary>
    public AiDirective(string line, string key, string? value, AiDirectiveKind kind)
    {
        Line = line;
        Key = key;
        Value = value;
        Kind = kind;
    }

    /// <summary>Exact source line, line terminator included.</summary>
    public string Line { get; }

    /// <summary>Leading directive token, e.g. <c>%AI5_FileFormat</c> or <c>%%Creator</c>.</summary>
    public string Key { get; }

    /// <summary>Text after the key/colon, or <c>null</c> when the line is key-only.</summary>
    public string? Value { get; }

    /// <summary>Classification of the line.</summary>
    public AiDirectiveKind Kind { get; }

    /// <inheritdoc />
    public override string Raw => Line;
}

/// <summary>How the bytes of an <see cref="AiDataBlock"/> are encoded inside the text.</summary>
public enum AiDataEncoding
{
    /// <summary>Encoding not recognised (block preserved as text only).</summary>
    Unknown,

    /// <summary><c>%%BeginData: N Hex Bytes</c> — each data line is <c>%</c> + hex digits.</summary>
    Hex,

    /// <summary><c>%%BeginData: N ASCII Bytes</c> — literal text.</summary>
    Ascii,

    /// <summary><c>%%BeginData: N Binary Bytes</c> — raw bytes between the markers.</summary>
    Binary,
}

/// <summary>
/// A <c>%%BeginData … %%EndData</c> block — the carrier for thumbnails and raster
/// image data. It is kept whole (marker lines, content lines and terminator) so the
/// writer can reproduce it byte-for-byte, and its hex/ASCII content is additionally
/// decoded into <see cref="Data"/> for callers that want the pixels.
/// </summary>
public sealed record AiDataBlock : AiPayloadElement
{
    /// <summary>The <c>%%BeginData: …</c> line, terminator included.</summary>
    public required string BeginLine { get; init; }

    /// <summary>The content lines between the markers, terminators included.</summary>
    public required IReadOnlyList<string> ContentLines { get; init; }

    /// <summary>The <c>%%EndData</c> line, terminator included, or <c>null</c> if unterminated.</summary>
    public string? EndLine { get; init; }

    /// <summary>Declared encoding of the content.</summary>
    public AiDataEncoding Encoding { get; init; }

    /// <summary>Byte count declared on the <c>%%BeginData</c> line, or −1 when absent.</summary>
    public int DeclaredByteCount { get; init; } = -1;

    /// <summary>Decoded content bytes (empty when the encoding is unknown).</summary>
    public byte[] Data { get; init; } = Array.Empty<byte>();

    /// <inheritdoc />
    public override string Raw
    {
        get
        {
            var builder = new StringBuilder(BeginLine);
            foreach (string line in ContentLines)
            {
                builder.Append(line);
            }

            if (EndLine is not null)
            {
                builder.Append(EndLine);
            }

            return builder.ToString();
        }
    }
}

/// <summary>
/// A <c>%AI5_BeginLayer</c> header, carrying the parsed <c>Lb</c> (layer attributes)
/// and <c>Ln</c> (layer name) statements.
///
/// <para>
/// The <c>Lb</c> operand list is, per the AI specification as amended by the
/// Inkscape <c>extension-ai</c> project and confirmed against the fixture corpus:
/// <c>visible preview enabled printing dimmed hasMultiLayerMasks [visible?]
/// colorIndex red green blue [0 50 0] Lb</c>. The bracketed members were added over
/// time — <c>visible?</c> from AI8, the trailing trio no later than CS2 — so the
/// colour fields are located relative to the end of the operand list rather than at
/// fixed indices. Hiding a layer clears both <c>visible</c> and <c>visible?</c>.
/// </para>
/// </summary>
public sealed record AiLayerBegin : AiPayloadElement
{
    /// <summary>The <c>%AI5_BeginLayer</c> line, terminator included.</summary>
    public required string BeginLine { get; init; }

    /// <summary>The <c>… Lb</c> attributes line, terminator included, or <c>null</c>.</summary>
    public string? LbLine { get; init; }

    /// <summary>The <c>(name) Ln</c> statement line, terminator included, or <c>null</c>.</summary>
    public string? NameLine { get; init; }

    /// <summary>The <c>0|1 AE</c> expanded-state line, terminator included, or <c>null</c>.</summary>
    public string? ExpandLine { get; init; }

    /// <summary>All numeric <c>Lb</c> operands, in source order.</summary>
    public IReadOnlyList<double> LbParameters { get; init; } = Array.Empty<double>();

    /// <summary>Layer name from the <c>Ln</c> statement (PostScript string escapes removed).</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Value of the leading <c>visible</c> flag; false for a hidden layer.</summary>
    public bool IsVisible { get; init; } = true;

    /// <summary>
    /// Value of the later <c>visible?</c> flag (present from AI8). Illustrator clears
    /// it together with <see cref="IsVisible"/> when a layer is hidden; it stays 1
    /// for template layers, which are visible but not printed.
    /// </summary>
    public bool IsVisibleFlag { get; init; } = true;

    /// <summary>Layer colour index from the <c>Lb</c> operands, or −1 when not present.</summary>
    public int ColorIndex { get; init; } = -1;

    /// <summary>Red component of the layer's selection colour (0–255), or −1.</summary>
    public int Red { get; init; } = -1;

    /// <summary>Green component of the layer's selection colour (0–255), or −1.</summary>
    public int Green { get; init; } = -1;

    /// <summary>Blue component of the layer's selection colour (0–255), or −1.</summary>
    public int Blue { get; init; } = -1;

    /// <summary>True when the later <c>0|1 AE</c> statement marks the layer expanded.</summary>
    public bool IsExpanded { get; init; }

    /// <inheritdoc />
    public override string Raw
        => BeginLine + (LbLine ?? string.Empty) + (NameLine ?? string.Empty) + (ExpandLine ?? string.Empty);

    /// <summary>
    /// Returns a copy whose <c>Ln</c> statement carries <paramref name="name"/>.
    /// The name is PostScript-escaped and the original line ending is preserved, so
    /// the rewritten element still writes back byte-stable text.
    /// </summary>
    public AiLayerBegin WithName(string name)
    {
        string terminator = TerminatorOf(NameLine ?? LbLine ?? BeginLine);
        string escaped = name
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("(", "\\(", StringComparison.Ordinal)
            .Replace(")", "\\)", StringComparison.Ordinal);
        return this with
        {
            Name = name,
            NameLine = $"({escaped}) Ln{terminator}",
        };
    }

    /// <summary>
    /// Returns a copy whose <c>Lb</c> visibility flags are set to
    /// <paramref name="visible"/> (both the leading <c>visible</c> operand and, when
    /// the operand list is long enough, the later <c>visible?</c> one).
    /// </summary>
    public AiLayerBegin WithVisibility(bool visible)
    {
        if (LbParameters.Count == 0 || LbLine is null)
        {
            return this with { IsVisible = visible, IsVisibleFlag = visible };
        }

        double[] parameters = LbParameters.ToArray();
        parameters[0] = visible ? 1.0 : 0.0;
        if (parameters.Length >= 11)
        {
            // Extended (AI8+) layout: `visible?` sits at index 6 and must follow the
            // leading `visible` operand.
            parameters[6] = visible ? 1.0 : 0.0;
        }

        string terminator = TerminatorOf(LbLine);
        string operands = string.Join(' ', parameters.Select(FormatNumber));
        return this with
        {
            IsVisible = visible,
            IsVisibleFlag = visible,
            LbParameters = parameters,
            LbLine = $"{operands} Lb{terminator}",
        };
    }

    /// <summary>Line terminator sequence at the end of <paramref name="line"/> (defaults to "\n").</summary>
    internal static string TerminatorOf(string? line)
    {
        if (line is null || line.Length == 0)
        {
            return "\n";
        }

        if (line.EndsWith("\r\n", StringComparison.Ordinal))
        {
            return "\r\n";
        }

        return line.EndsWith('\n') || line.EndsWith('\r') ? line[^1..] : "\n";
    }

    private static string FormatNumber(double value)
        => value == Math.Floor(value) && Math.Abs(value) < 1e15
            ? ((long)value).ToString(CultureInfo.InvariantCulture)
            : value.ToString("R", CultureInfo.InvariantCulture);
}

/// <summary>
/// The <c>%AI5_EndLayer--</c> terminator of a layer. The <c>LB</c> operator that
/// precedes it is an ordinary payload line and is preserved as such.
/// </summary>
public sealed record AiLayerEnd : AiPayloadElement
{
    /// <summary>Creates the terminator element from its exact source line.</summary>
    public AiLayerEnd(string line) => Line = line;

    /// <summary>Exact source line, terminator included.</summary>
    public string Line { get; }

    /// <inheritdoc />
    public override string Raw => Line;
}
