using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VCCad.App.Automation;

/// <summary>Who or what produced an entry in the diary.</summary>
public enum InteractionKind
{
    /// <summary>A person, through the window (pointer, keyboard, drag and drop).</summary>
    Ui,

    /// <summary>An external automation client over the HTTP endpoint.</summary>
    Api,

    /// <summary>The built-in assistant, including its model requests.</summary>
    Llm,

    /// <summary>The application itself (startup, sessions, errors).</summary>
    System,

    /// <summary>A learned skill: a distilled record of work that was completed.</summary>
    Skill,
}

/// <summary>Finer classification used for filtering and retrieval.</summary>
public enum InteractionCategory
{
    Session,
    Pointer,
    Hover,
    DragDrop,
    Key,
    Operation,
    Model,
    Skill,
    Note,
}

/// <summary>
/// One entry in the application diary. Deliberately flat and self-describing so a
/// log line stands alone when retrieved by a search: who did what, to which
/// control or object, with what parameters and result.
/// </summary>
/// <param name="Sequence">Monotonic id within the whole application history.</param>
/// <param name="TimestampUtc">When it happened (UTC).</param>
/// <param name="SessionId">The application session (one run of the editor).</param>
/// <param name="Kind">UI, Api, Llm, System or Skill.</param>
/// <param name="Category">Pointer, Key, Operation, Model, …</param>
/// <param name="Name">Short verb/name: <c>pointer.press</c>, <c>object.create</c>, <c>llm.chat</c>.</param>
/// <param name="Target">What it acted on: a control description, item id, layer, …</param>
/// <param name="Details">Extra context (parameters, results, error text).</param>
/// <param name="Success">False for failures and cancellations.</param>
/// <param name="DurationMs">How long it took, when meaningful.</param>
/// <param name="Tags">Free-form labels used for retrieval.</param>
public sealed record InteractionRecord(
    long Sequence,
    DateTimeOffset TimestampUtc,
    string SessionId,
    InteractionKind Kind,
    InteractionCategory Category,
    string Name,
    string? Target,
    string? Details,
    bool Success,
    double DurationMs,
    string[] Tags)
{
    /// <summary>The text a search reads: everything meaningful, lower-cased on demand.</summary>
    public string SearchText()
        => string.Join(' ', Name, Target ?? string.Empty, Details ?? string.Empty, string.Join(' ', Tags));

    /// <summary>One-line rendering for the diagnostics view.</summary>
    public string Describe()
    {
        string kind = Kind switch
        {
            InteractionKind.Ui => "ui",
            InteractionKind.Api => "api",
            InteractionKind.Llm => "llm",
            InteractionKind.System => "sys",
            _ => "skill",
        };
        string target = Target is null ? string.Empty : $" → {Target}";
        string status = Success ? string.Empty : "  [FAILED]";
        return $"{TimestampUtc.ToLocalTime():HH:mm:ss.fff} {kind,-5} {Name}{target}{status}";
    }
}

/// <summary>
/// The application diary: an append-only, searchable record of everything that
/// happens in VCCad — the person's pointer and keyboard activity, the assistant's
/// model calls and operations, and external automation.
///
/// Why a diary and not just the command queue: the queue is *how to redo work*,
/// the diary is *what was actually done*. It is what lets the assistant recognise
/// a task it has performed before ("we designed a US size 10 bodice block") and
/// reuse the approach, and it lets a person audit any session after the fact.
///
/// Storage is newline-delimited JSON, one file per day, under the application data
/// directory. No database engine is needed, entries are greppable by hand, and the
/// in-memory index is rebuilt by streaming the files. Searches are token-overlap
/// scored over the entry text, which is enough for retrieval-augmented prompting
/// and keeps the whole thing dependency-free (it must also work in the browser
/// build, where there is no SQLite).
/// </summary>
public sealed class InteractionLog
{
    /// <summary>Rows kept in memory for the diagnostics view and quick searches.</summary>
    public const int MaxInMemoryRecords = 20000;

    /// <summary>Daily files older than this are pruned on startup.</summary>
    public const int RetentionDays = 90;

    private readonly object _gate = new();
    private readonly List<InteractionRecord> _recent = new();
    private readonly string _directory;
    private StreamWriter? _writer;
    private DateOnly _writerDay;
    private long _sequence;

    /// <summary>Where the diary lives by default: the user's application data.</summary>
    public static string DefaultDirectory()
    {
        string? custom = Environment.GetEnvironmentVariable("VCCAD_HISTORY_DIR");
        if (!string.IsNullOrWhiteSpace(custom))
        {
            return custom;
        }

        string root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(root))
        {
            root = Path.Combine(Path.GetTempPath(), "vccad");
        }

        return Path.Combine(root, "VCCad", "history");
    }

    /// <summary>Creates a diary in <paramref name="directory"/> (created if missing).</summary>
    public InteractionLog(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(_directory);
        LoadRecent();
        _sequence = _recent.Count > 0 ? _recent[^1].Sequence : 0;
        Prune();
    }

    /// <summary>Where the diary is stored.</summary>
    public string Location => _directory;

    /// <summary>The current session id; every entry is tagged with it.</summary>
    public string SessionId { get; private set; } = "unknown";

    /// <summary>Raised after every appended entry (any thread).</summary>
    public event EventHandler<InteractionRecord>? Recorded;

    /// <summary>Starts a new session (one run of the editor) and records it.</summary>
    public InteractionRecord StartSession(string reason = "startup")
    {
        SessionId = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}";
        return Record(InteractionKind.System, InteractionCategory.Session, "session.start",
            target: null, details: reason, success: true, durationMs: 0, tags: new[] { "session" });
    }

    /// <summary>Appends an entry and returns it.</summary>
    public InteractionRecord Record(
        InteractionKind kind,
        InteractionCategory category,
        string name,
        string? target = null,
        string? details = null,
        bool success = true,
        double durationMs = 0,
        IEnumerable<string>? tags = null)
    {
        InteractionRecord record;
        lock (_gate)
        {
            record = new InteractionRecord(
                ++_sequence,
                DateTimeOffset.UtcNow,
                SessionId,
                kind,
                category,
                name,
                Truncate(target, 400),
                Truncate(details, 4000),
                success,
                durationMs,
                tags?.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                    ?? Array.Empty<string>());

            _recent.Add(record);
            if (_recent.Count > MaxInMemoryRecords)
            {
                _recent.RemoveRange(0, _recent.Count - MaxInMemoryRecords);
            }

            Append(record);
        }

        Recorded?.Invoke(null, record);
        return record;
    }

    /// <summary>Convenience for recording a free-form note (used by automation clients).</summary>
    public InteractionRecord Note(string text, string? target = null, IEnumerable<string>? tags = null)
        => Record(InteractionKind.System, InteractionCategory.Note, "note", target, text, true, 0, tags);

    /// <summary>The most recent entries, oldest first.</summary>
    public IReadOnlyList<InteractionRecord> Tail(int count = 200, string? sessionId = null)
    {
        lock (_gate)
        {
            IEnumerable<InteractionRecord> source = _recent;
            if (sessionId is not null)
            {
                source = source.Where(r => r.SessionId == sessionId);
            }

            return source.Reverse().Take(Math.Clamp(count, 1, MaxInMemoryRecords)).Reverse().ToArray();
        }
    }

    /// <summary>Entries recorded in a session, oldest first.</summary>
    public IReadOnlyList<InteractionRecord> Session(string sessionId, int max = 2000)
        => SessionRecords(sessionId, max);

    /// <summary>All known session ids, most recent first.</summary>
    public IReadOnlyList<SessionSummary> Sessions()
    {
        lock (_gate)
        {
            return _recent
                .GroupBy(r => r.SessionId)
                .Select(g => new SessionSummary(
                    g.Key,
                    g.Min(r => r.TimestampUtc),
                    g.Max(r => r.TimestampUtc),
                    g.Count(),
                    g.Count(r => r.Kind == InteractionKind.Skill)))
                .OrderByDescending(s => s.EndedUtc)
                .ToArray();
        }
    }

    /// <summary>
    /// Token-overlap retrieval over the diary — the RAG query. Returns the entries
    /// that share the most words with <paramref name="query"/>, newest first within
    /// equal scores, so a skill recorded months ago can still surface.
    /// </summary>
    public IReadOnlyList<InteractionRecord> Search(
        string query, int limit = 20, InteractionKind? kind = null, string? sessionId = null)
    {
        string[] terms = Terms(query);
        lock (_gate)
        {
            IEnumerable<InteractionRecord> source = _recent;
            if (kind is not null)
            {
                source = source.Where(r => r.Kind == kind);
            }

            if (sessionId is not null)
            {
                source = source.Where(r => r.SessionId == sessionId);
            }

            if (terms.Length == 0)
            {
                return source.Reverse().Take(Math.Clamp(limit, 1, 200)).Reverse().ToArray();
            }

            return source
                .Select(r => (Record: r, Score: Score(r, terms)))
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.Record.TimestampUtc)
                .Take(Math.Clamp(limit, 1, 200))
                .Select(x => x.Record)
                .ToArray();
        }
    }

    /// <summary>All learned skills, newest first, optionally filtered by a query.</summary>
    public IReadOnlyList<InteractionRecord> Skills(string? query = null, int limit = 50)
        => string.IsNullOrWhiteSpace(query)
            ? Search(string.Empty, limit, InteractionKind.Skill)
            : Search(query, limit, InteractionKind.Skill);

    /// <summary>
    /// Records a skill: a distilled, reusable description of work that was completed,
    /// including the operations that achieved it. This is what makes "learn this as
    /// a skill" persist for later retrieval.
    /// </summary>
    /// <param name="title">Short name, e.g. "US size 10 bodice block".</param>
    /// <param name="description">What the task achieves and any inputs it needs.</param>
    /// <param name="sessionId">Session to distil; defaults to the current one.</param>
    /// <param name="extraTags">Additional retrieval tags.</param>
    public InteractionRecord Learn(
        string title, string? description = null, string? sessionId = null, IEnumerable<string>? extraTags = null)
    {
        string source = sessionId ?? SessionId;
        IReadOnlyList<InteractionRecord> operations = SessionRecords(source, 5000)
            .Where(r => r.Category == InteractionCategory.Operation)
            .ToArray();

        var details = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(description))
        {
            details.AppendLine(description.Trim()).AppendLine();
        }

        details.AppendLine($"Achieved in session {source} with {operations.Count} recorded step(s).");
        if (operations.Count > 0)
        {
            details.AppendLine("Steps:");
            foreach (InteractionRecord step in operations)
            {
                string target = step.Target is null ? string.Empty : $" {step.Target}";
                string parameters = string.IsNullOrWhiteSpace(step.Details) ? string.Empty : $" {step.Details}";
                details.AppendLine($"- {step.Name}{target}{parameters}");
            }
        }

        string[] derived = Terms(title).Concat(Terms(description ?? string.Empty))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(24)
            .ToArray();

        return Record(
            InteractionKind.Skill,
            InteractionCategory.Skill,
            "skill.learn",
            target: title,
            details: details.ToString(),
            success: true,
            durationMs: 0,
            tags: derived.Concat(extraTags ?? Array.Empty<string>()));
    }

    /// <summary>Counts by kind and the storage footprint — used by the diagnostics view.</summary>
    public object Stats()
    {
        lock (_gate)
        {
            return new
            {
                directory = _directory,
                session = SessionId,
                inMemory = _recent.Count,
                sequence = _sequence,
                sessions = Sessions().Count,
                skills = _recent.Count(r => r.Kind == InteractionKind.Skill),
                byKind = _recent.GroupBy(r => r.Kind.ToString())
                    .ToDictionary(g => g.Key, g => g.Count()),
                files = Directory.Exists(_directory) ? Directory.GetFiles(_directory, "*.jsonl").Length : 0,
            };
        }
    }

    /// <summary>Streams every stored entry (whole application history), oldest first.</summary>
    public IEnumerable<InteractionRecord> All()
    {
        if (!Directory.Exists(_directory))
        {
            yield break;
        }

        foreach (string file in Directory.GetFiles(_directory, "*.jsonl").OrderBy(f => f, StringComparer.Ordinal))
        {
            foreach (string line in ReadLinesShared(file))
            {
                InteractionRecord? record = Deserialize(line);
                if (record is not null)
                {
                    yield return record;
                }
            }
        }
    }

    /// <summary>Copies the whole diary into a single file (export for analysis).</summary>
    public int Export(string path)
    {
        int count = 0;
        using var writer = new StreamWriter(path, append: false);
        foreach (InteractionRecord record in All())
        {
            writer.WriteLine(Serialize(record));
            count++;
        }

        return count;
    }

    private IReadOnlyList<InteractionRecord> SessionRecords(string sessionId, int max)
    {
        lock (_gate)
        {
            return _recent.Where(r => r.SessionId == sessionId).Take(Math.Clamp(max, 1, MaxInMemoryRecords)).ToArray();
        }
    }

    // ------------------------------------------------------------------
    // Persistence
    // ------------------------------------------------------------------

    private void Append(InteractionRecord record)
    {
        try
        {
            DateOnly today = DateOnly.FromDateTime(record.TimestampUtc.UtcDateTime);
            if (_writer is null || _writerDay != today)
            {
                _writer?.Dispose();
                string file = Path.Combine(_directory, $"history-{today:yyyyMMdd}.jsonl");

                // Share the file: the diary is meant to be readable while the editor is
                // running — by a second process, a tail, or the next test.
                var stream = new FileStream(
                    file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                _writer = new StreamWriter(stream) { AutoFlush = true };
                _writerDay = today;
            }

            _writer.WriteLine(Serialize(record));
        }
        catch (IOException)
        {
            // A diary must never take the editor down; the entry stays in memory.
        }
        catch (UnauthorizedAccessException)
        {
            // Same: read-only home directories are survivable.
        }
    }

    private void LoadRecent()
    {
        if (!Directory.Exists(_directory))
        {
            return;
        }

        foreach (string file in Directory.GetFiles(_directory, "*.jsonl")
                     .OrderByDescending(f => f, StringComparer.Ordinal)
                     .Take(3))
        {
            foreach (string line in ReadLinesShared(file))
            {
                InteractionRecord? record = Deserialize(line);
                if (record is not null)
                {
                    _recent.Add(record);
                }
            }

            if (_recent.Count >= MaxInMemoryRecords)
            {
                break;
            }
        }

        if (_recent.Count > MaxInMemoryRecords)
        {
            _recent.RemoveRange(0, _recent.Count - MaxInMemoryRecords);
        }

        _recent.Sort((a, b) => a.Sequence.CompareTo(b.Sequence));
    }

    private void Prune()
    {
        try
        {
            DateOnly cutoff = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-RetentionDays));
            foreach (string file in Directory.GetFiles(_directory, "history-*.jsonl"))
            {
                string stem = Path.GetFileNameWithoutExtension(file).Replace("history-", string.Empty);
                if (DateOnly.TryParseExact(stem, "yyyyMMdd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out DateOnly day) && day < cutoff)
                {
                    File.Delete(file);
                }
            }
        }
        catch (IOException)
        {
            // Pruning is best effort.
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private static string Serialize(InteractionRecord record)
    {
        // A compact wire form: short property names keep month-long diaries small.
        var dto = new Dictionary<string, object?>
        {
            ["seq"] = record.Sequence,
            ["at"] = record.TimestampUtc.ToString("O", CultureInfo.InvariantCulture),
            ["s"] = record.SessionId,
            ["k"] = record.Kind.ToString().ToLowerInvariant(),
            ["c"] = record.Category.ToString().ToLowerInvariant(),
            ["n"] = record.Name,
            ["t"] = record.Target,
            ["d"] = record.Details,
            ["ok"] = record.Success,
            ["ms"] = Math.Round(record.DurationMs, 2),
            ["tags"] = record.Tags.Length == 0 ? null : record.Tags,
        };
        return JsonSerializer.Serialize(dto, JsonOptions);
    }

    private static InteractionRecord? Deserialize(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            return new InteractionRecord(
                root.TryGetProperty("seq", out JsonElement seq) ? seq.GetInt64() : 0,
                root.TryGetProperty("at", out JsonElement at) && at.ValueKind == JsonValueKind.String
                    ? DateTimeOffset.Parse(at.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
                    : DateTimeOffset.UtcNow,
                root.TryGetProperty("s", out JsonElement s) ? s.GetString() ?? "unknown" : "unknown",
                ParseKind(root.TryGetProperty("k", out JsonElement k) ? k.GetString() : null),
                ParseCategory(root.TryGetProperty("c", out JsonElement c) ? c.GetString() : null),
                root.TryGetProperty("n", out JsonElement n) ? n.GetString() ?? "?" : "?",
                root.TryGetProperty("t", out JsonElement t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null,
                root.TryGetProperty("d", out JsonElement d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null,
                !root.TryGetProperty("ok", out JsonElement ok) || ok.ValueKind != JsonValueKind.False,
                root.TryGetProperty("ms", out JsonElement ms) && ms.ValueKind == JsonValueKind.Number ? ms.GetDouble() : 0,
                root.TryGetProperty("tags", out JsonElement tags) && tags.ValueKind == JsonValueKind.Array
                    ? tags.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String)
                        .Select(e => e.GetString()!).ToArray()
                    : Array.Empty<string>());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static InteractionKind ParseKind(string? value)
        => Enum.TryParse(value, ignoreCase: true, out InteractionKind kind) ? kind : InteractionKind.System;

    private static InteractionCategory ParseCategory(string? value)
        => Enum.TryParse(value, ignoreCase: true, out InteractionCategory category)
            ? category
            : InteractionCategory.Note;

    // ------------------------------------------------------------------
    // Retrieval scoring
    // ------------------------------------------------------------------

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "and", "or", "to", "of", "in", "on", "for", "with", "is", "it", "this", "that",
        "be", "as", "at", "by", "from", "into", "please", "i", "we", "you", "my", "our", "me", "then",
    };

    /// <summary>Splits text into meaningful lowercase terms.</summary>
    public static string[] Terms(string text)
        => text.ToLowerInvariant()
            .Split(new[] { ' ', '\t', '\r', '\n', ',', '.', ';', ':', '/', '\\', '(', ')', '[', ']', '"', '\'', '-', '_', '=', '<', '>', '|' },
                StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length > 1 && !StopWords.Contains(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static int Score(InteractionRecord record, string[] terms)
    {
        string haystack = record.SearchText().ToLowerInvariant();
        int score = 0;
        foreach (string term in terms)
        {
            if (haystack.Contains(term, StringComparison.Ordinal))
            {
                // A skill matching the query is worth more than a passing event.
                score += record.Kind == InteractionKind.Skill ? 4 : 1;
            }
        }

        return score;
    }

    private static string? Truncate(string? text, int max)
        => text is null || text.Length <= max ? text : text[..max] + "…";

    /// <summary>
    /// Reads a diary file even while the editor (or another reader) has it open.
    /// A plain <see cref="File.ReadLines(string)"/> takes a share mode that conflicts
    /// with the running writer.
    /// </summary>
    private static IEnumerable<string> ReadLinesShared(string path)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }
}

/// <summary>Summary of one recorded session.</summary>
/// <param name="SessionId">Session identifier.</param>
/// <param name="StartedUtc">First entry.</param>
/// <param name="EndedUtc">Last entry.</param>
/// <param name="Entries">Number of entries.</param>
/// <param name="Skills">Skills learned during it.</param>
public sealed record SessionSummary(
    string SessionId, DateTimeOffset StartedUtc, DateTimeOffset EndedUtc, int Entries, int Skills);
