using System.Text;

namespace VCCad.App.Ai;

/// <summary>
/// Turns the middle of a long conversation into an accumulated summary instead of
/// deleting it.
///
/// Trimming used to replace the dropped region with the literal string
/// "(earlier steps omitted to stay within the context window)". That region is the
/// middle of the work in progress — exactly where the reasoning about the current
/// job lives — so the model re-decided work it had already done and forgot facts
/// the user had given it. Two rules replace that:
///
/// <list type="bullet">
///   <item>the dropped region becomes a compact record of what was attempted, what
///   worked, what failed and what was decided;</item>
///   <item>that record is kept and folded into the next compression, so the summary
///   accumulates over a long session instead of being thrown away each time.</item>
/// </list>
///
/// This class makes no model calls: it decides what to drop and renders a
/// deterministic digest of it. The agent may add a model-written summary on top, but
/// the digest is the floor — so a value the user asked to be remembered (an id, a
/// codeword, a measurement) cannot be lost because a summariser forgot it.
/// </summary>
public sealed class ContextCompressor
{
    /// <summary>Heading of the message that carries the accumulated summary.</summary>
    public const string SummaryHeading =
        "[compressed history — what happened earlier in this conversation]";

    /// <summary>
    /// A conversation is never compressed below this: the summary itself, the system
    /// prompt and the newest turn have to fit somewhere.
    /// </summary>
    public const int MinBudgetChars = 2000;

    /// <summary>Longest the accumulated summary is allowed to grow to.</summary>
    public const int MaxSummaryChars = 6000;

    /// <summary>Creates a compressor with the given conversation budget.</summary>
    public ContextCompressor(int budgetChars) => BudgetChars = Math.Max(MinBudgetChars, budgetChars);

    /// <summary>Characters the conversation may occupy before it is compressed.</summary>
    public int BudgetChars { get; }

    /// <summary>Everything compressed so far, oldest first.</summary>
    public string Summary { get; private set; } = string.Empty;

    /// <summary>How many times the conversation has been compressed.</summary>
    public int CompressionCount { get; private set; }

    /// <summary>What one message costs: text plus a quarter of an image's bytes.</summary>
    public static int Cost(LlmMessage message) => (message.Text?.Length ?? 0) + ((message.ImagePng?.Length ?? 0) / 4);

    /// <summary>What a conversation costs.</summary>
    public static int Cost(IEnumerable<LlmMessage> messages) => messages.Sum(Cost);

    /// <summary>True when the conversation has outgrown its budget.</summary>
    public bool IsOverBudget(IReadOnlyList<LlmMessage> history) => Cost(history) > BudgetChars;

    /// <summary>
    /// The most recent user turn a resume can start from, or -1. Cutting at a user
    /// boundary is what keeps an assistant tool call next to the tool result that
    /// answers it — the endpoint rejects a tool message whose request is missing.
    /// </summary>
    public static int ResumeIndex(IReadOnlyList<LlmMessage> history)
    {
        for (int i = history.Count - 1; i >= 2; i--)
        {
            if (history[i].Role == "user")
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// The messages that should be replaced by a summary, or null when the
    /// conversation fits or nothing can be dropped safely.
    /// </summary>
    public IReadOnlyList<LlmMessage>? SelectRegion(IReadOnlyList<LlmMessage> history)
    {
        if (history.Count <= 4 || !IsOverBudget(history))
        {
            return null;
        }

        if (!TrySelectRegion(history, out int resume, out int from, out _))
        {
            return null;
        }

        LlmMessage[] region = history
            .Skip(from)
            .Take(resume - from)
            .Where(m => !IsSummaryMessage(m)) // already carried in Summary
            .ToArray();

        return region.Length == 0 ? null : region;
    }

    /// <summary>
    /// Folds <paramref name="addition"/> into the accumulated summary and rebuilds the
    /// conversation around it. This is the whole transform, so a test can run several
    /// compressions in a row and check that the summary accumulates.
    /// </summary>
    public List<LlmMessage> Apply(IReadOnlyList<LlmMessage> history, string addition)
    {
        if (!TrySelectRegion(history, out int resume, out _, out _))
        {
            return history.ToList();
        }

        Accumulate(addition);
        return Rebuild(history, resume, Summary);
    }

    /// <summary>
    /// Chooses the region to compress: everything after the system prompt and the
    /// original task, up to <paramref name="resume"/>. Returns false when there is
    /// nothing that can be dropped without breaking the conversation.
    /// </summary>
    public static bool TrySelectRegion(
        IReadOnlyList<LlmMessage> history, out int resume, out int from, out int toExclusive)
    {
        resume = ResumeIndex(history);
        from = RegionStart(history);
        toExclusive = resume;
        return resume > from;
    }

    /// <summary>
    /// Where the droppable region begins: just past the original task, which is kept
    /// for the whole session. Normally that is index 2 (system prompt, task); if a
    /// rebuild ever left the summary at index 1 there is no separate original task, so
    /// the region starts there and the summary is replaced rather than duplicated.
    /// </summary>
    private static int RegionStart(IReadOnlyList<LlmMessage> history)
        => history.Count > 1 && history[1].Role == "user" && !IsSummaryMessage(history[1]) ? 2 : 1;

    /// <summary>
    /// A deterministic digest of the dropped region: what the user asked for, what
    /// the assistant said, which operations ran and which failed. Written so the
    /// person's own words survive verbatim (truncated), because those carry the
    /// facts the model must not lose.
    /// </summary>
    public static string Digest(IEnumerable<LlmMessage> region)
    {
        var text = new StringBuilder();
        foreach (LlmMessage message in region)
        {
            if (!string.IsNullOrWhiteSpace(message.Text))
            {
                string flat = Flatten(message.Text!);
                if (message.ToolCallId is not null)
                {
                    bool failed = flat.Contains("\"ok\":false", StringComparison.OrdinalIgnoreCase)
                                  || flat.Contains("\"success\":false", StringComparison.OrdinalIgnoreCase);
                    text.AppendLine($"- operation result{(failed ? " FAILED" : string.Empty)}: {Clip(flat, 160)}");
                }
                else if (message.Role == "user")
                {
                    text.AppendLine($"- user asked: {Clip(flat, 400)}");
                }
                else if (message.Role == "assistant")
                {
                    text.AppendLine($"- assistant said: {Clip(flat, 240)}");
                }
                else
                {
                    text.AppendLine($"- {message.Role}: {Clip(flat, 160)}");
                }
            }

            foreach (LlmToolCall call in message.ToolCalls ?? Array.Empty<LlmToolCall>())
            {
                text.AppendLine($"- called {OperationName(call.Arguments)}");
            }
        }

        return text.ToString().Trim();
    }

    /// <summary>
    /// Folds a new digest (and/or a model-written summary) into the accumulated
    /// summary. This is what makes compression cumulative: the next compression sees
    /// the previous summary as part of the region, so nothing is lost by degrees.
    /// </summary>
    public string Accumulate(string addition)
    {
        string merged = string.IsNullOrWhiteSpace(Summary)
            ? addition.Trim()
            : Summary.Trim() + Environment.NewLine + addition.Trim();

        if (merged.Length > MaxSummaryChars)
        {
            // Older material is already reflected in the original task and the
            // newest notes matter most for the job in flight, so keep the tail.
            merged = "…(older compressed notes elided)" + Environment.NewLine +
                     merged[^MaxSummaryChars..];
        }

        Summary = merged;
        CompressionCount++;
        return Summary;
    }

    /// <summary>Forgets everything: used by the Clear affordance.</summary>
    public void Reset()
    {
        Summary = string.Empty;
        CompressionCount = 0;
    }

    /// <summary>The summary as a message the model reads.</summary>
    public static string Format(string summary) => $"{SummaryHeading}{Environment.NewLine}{summary}";

    /// <summary>True when this message is the accumulator rather than a real turn.</summary>
    public static bool IsSummaryMessage(LlmMessage message)
        => message.Role == "user" && (message.Text?.StartsWith(SummaryHeading, StringComparison.Ordinal) ?? false);

    /// <summary>
    /// Rebuilds the conversation as: system prompt, original task, accumulated
    /// summary, then the recent messages from <paramref name="resume"/> onwards.
    /// </summary>
    public List<LlmMessage> Rebuild(
        IReadOnlyList<LlmMessage> history, int resume, string summary)
    {
        var rebuilt = new List<LlmMessage> { history[0] };
        if (history.Count > 1 && history[1].Role == "user" && !IsSummaryMessage(history[1]))
        {
            rebuilt.Add(history[1]); // the original task, kept for the whole session
        }

        rebuilt.Add(new LlmMessage("user", Format(summary)));
        rebuilt.AddRange(history.Skip(resume));
        return rebuilt;
    }

    private static string OperationName(string arguments)
    {
        try
        {
            using System.Text.Json.JsonDocument json = System.Text.Json.JsonDocument.Parse(arguments);
            if (json.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object &&
                json.RootElement.TryGetProperty("op", out System.Text.Json.JsonElement op) &&
                op.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                return op.GetString() ?? "an operation";
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Not JSON; fall through to the raw text.
        }

        return Clip(Flatten(arguments), 60);
    }

    private static string Flatten(string text)
        => string.Join(' ', text.Split(
            new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string Clip(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";
}
