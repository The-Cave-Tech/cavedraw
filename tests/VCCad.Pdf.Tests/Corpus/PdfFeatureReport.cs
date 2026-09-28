using System.Text;

namespace VCCad.Pdf.Tests.Corpus;

/// <summary>
/// The immutable result of <see cref="PdfFeatureProbe"/>: a printable, name →
/// flag/count record of which PDF document features a file uses.
///
/// The probe never throws. A file whose object structure cannot be parsed is
/// reported with <see cref="ParseFailed"/> set and
/// <see cref="PdfFeature.DocumentUnparseable"/> true, so callers can distinguish
/// "this PDF has no images" from "we could not read this PDF at all".
/// </summary>
public sealed class PdfFeatureReport
{
    private readonly SortedDictionary<string, bool> _flags;
    private readonly SortedDictionary<string, int> _counts;

    internal PdfFeatureReport(string name, bool parseFailed, string? parseError,
        IDictionary<string, bool> flags, IDictionary<string, int> counts)
    {
        Name = name;
        ParseFailed = parseFailed;
        ParseError = parseError;
        _flags = new SortedDictionary<string, bool>(flags, StringComparer.Ordinal);
        _counts = new SortedDictionary<string, int>(counts, StringComparer.Ordinal);
    }

    /// <summary>File name (or empty when the probe was handed anonymous bytes).</summary>
    public string Name { get; }

    /// <summary>True when the PDF object structure could not be classified.</summary>
    public bool ParseFailed { get; }

    /// <summary>Why parsing failed, when it did.</summary>
    public string? ParseError { get; }

    /// <summary>Every known feature name mapped to whether it was observed.</summary>
    public IReadOnlyDictionary<string, bool> Flags => _flags;

    /// <summary>Numeric observations (page count, per-operator tallies, …).</summary>
    public IReadOnlyDictionary<string, int> Counts => _counts;

    /// <summary>Whether <paramref name="feature"/> was observed.</summary>
    public bool Has(string feature) => _flags.TryGetValue(feature, out bool value) && value;

    /// <summary>The observed value of <paramref name="count"/>, or zero.</summary>
    public int Count(string feature) => _counts.TryGetValue(feature, out int value) ? value : 0;

    /// <summary>Feature names that were observed, in ordinal order.</summary>
    public IEnumerable<string> ObservedFlags() => _flags.Where(pair => pair.Value).Select(pair => pair.Key);

    /// <summary>Counters that were observed with a non-zero value, ordinal order.</summary>
    public IEnumerable<KeyValuePair<string, int>> ObservedCounts()
        => _counts.Where(pair => pair.Value != 0);

    /// <inheritdoc/>
    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append(Name.Length == 0 ? "<pdf>" : Name);
        sb.Append(ParseFailed ? " [unparseable] " : " ");
        sb.Append(string.Join(", ", ObservedFlags()));
        return sb.ToString();
    }
}
