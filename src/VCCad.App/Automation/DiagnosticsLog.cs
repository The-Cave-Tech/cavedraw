using System.Collections.Concurrent;
using System.Text.Json;

namespace VCCad.App.Automation;

/// <summary>Where a recorded operation came from.</summary>
public enum ApiCallSource
{
    /// <summary>A person operating the UI.</summary>
    Ui,

    /// <summary>An external automation client (HTTP/JSON-RPC).</summary>
    Api,

    /// <summary>The in-app chatbot's agent loop.</summary>
    Llm,

    /// <summary>The application itself (startup, background work).</summary>
    System,
}

/// <summary>One recorded operation, as shown in the diagnostics window.</summary>
/// <param name="Sequence">Monotonic id; also the poll cursor for clients.</param>
/// <param name="Timestamp">When the call was recorded.</param>
/// <param name="Source">Who initiated it.</param>
/// <param name="Operation">Operation name, e.g. <c>object.create</c>.</param>
/// <param name="Parameters">Request parameters as compact JSON, if any.</param>
/// <param name="Result">Result as compact JSON, or null on failure.</param>
/// <param name="Success">Whether the operation completed without error.</param>
/// <param name="DurationMs">Wall-clock duration in milliseconds.</param>
/// <param name="Error">Error message when <paramref name="Success"/> is false.</param>
public sealed record ApiCallRecord(
    long Sequence,
    DateTimeOffset Timestamp,
    ApiCallSource Source,
    string Operation,
    string? Parameters,
    string? Result,
    bool Success,
    double DurationMs,
    string? Error)
{
    /// <summary>Short single-line form used in logs and chat transcripts.</summary>
    public string Describe()
        => $"[{Sequence}] {Source.ToString().ToLowerInvariant()} {Operation} " +
           $"{(Success ? "ok" : "FAIL")} {DurationMs:F1}ms{(Error is null ? string.Empty : ": " + Error)}";
}

/// <summary>
/// The application's audit trail: every operation that runs through the
/// automation layer, whoever initiated it, is recorded here.
///
/// This is deliberately the single funnel for UI, HTTP and chatbot actions. The
/// requirement is that the person and the model can always see what the other is
/// doing, so nothing may mutate the document without passing through
/// <see cref="EditorOperations"/>, which records here.
/// </summary>
public static class DiagnosticsLog
{
    /// <summary>How many records are retained in memory.</summary>
    public const int Capacity = 2000;

    private static readonly object Gate = new();
    private static readonly Queue<ApiCallRecord> Records = new();
    private static long _sequence;

    /// <summary>Raised after a record is added (any thread).</summary>
    public static event EventHandler<ApiCallRecord>? Recorded;

    /// <summary>Highest sequence number issued so far.</summary>
    public static long Sequence
    {
        get
        {
            lock (Gate)
            {
                return _sequence;
            }
        }
    }

    /// <summary>Records one operation and returns the stored record.</summary>
    public static ApiCallRecord Add(
        ApiCallSource source, string operation, string? parameters, object? result,
        bool success, double durationMs, string? error = null)
    {
        ApiCallRecord record;
        lock (Gate)
        {
            record = new ApiCallRecord(
                ++_sequence,
                DateTimeOffset.Now,
                source,
                operation,
                Truncate(parameters),
                success ? Truncate(ToJson(result)) : null,
                success,
                durationMs,
                error);
            Records.Enqueue(record);
            while (Records.Count > Capacity)
            {
                Records.Dequeue();
            }
        }

        Recorded?.Invoke(null, record);
        return record;
    }

    /// <summary>Records a call that threw.</summary>
    public static ApiCallRecord AddFailure(
        ApiCallSource source, string operation, string? parameters, double durationMs, Exception error)
        => Add(source, operation, parameters, null, success: false, durationMs, error.Message);

    /// <summary>Records that a person used the UI (so the model can see it too).</summary>
    public static ApiCallRecord Ui(string operation, string? parameters = null, object? result = null)
        => Add(ApiCallSource.Ui, operation, parameters, result, success: true, durationMs: 0);

    /// <summary>Records after <paramref name="since"/> (exclusive); 0 returns everything retained.</summary>
    public static IReadOnlyList<ApiCallRecord> Since(long since)
    {
        lock (Gate)
        {
            return Records.Where(r => r.Sequence > since).ToArray();
        }
    }

    /// <summary>The most recent records, oldest first.</summary>
    public static IReadOnlyList<ApiCallRecord> Recent(int max = 200)
    {
        lock (Gate)
        {
            return Records.Reverse().Take(max).Reverse().ToArray();
        }
    }

    /// <summary>Drops all retained records (used by tests).</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            Records.Clear();
        }
    }

    /// <summary>Compact JSON, safe for display and transport.</summary>
    public static string ToJson(object? value)
    {
        if (value is null)
        {
            return "null";
        }

        if (value is string s)
        {
            return s;
        }

        try
        {
            return JsonSerializer.Serialize(value, JsonOptions);
        }
        catch (Exception)
        {
            return value.ToString() ?? string.Empty;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static string? Truncate(string? text, int max = 4000)
        => text is null || text.Length <= max ? text : text[..max] + "…";
}
