using System.Text;
using System.Text.Json;
using Avalonia.Threading;
using VCCad.App.Automation;

namespace VCCad.App.Ai;

/// <summary>One entry in the chat transcript.</summary>
/// <param name="Role">user | assistant | system | action.</param>
/// <param name="Text">Rendered text.</param>
/// <param name="ImagePng">Optional attached image (screenshots).</param>
public sealed record ChatEntry(string Role, string Text, byte[]? ImagePng = null);

/// <summary>The outcome of one user turn.</summary>
/// <param name="Reply">The assistant's final message.</param>
/// <param name="Actions">Operations performed during the turn.</param>
/// <param name="Steps">How many model round-trips it took.</param>
public sealed record AgentTurnResult(string Reply, IReadOnlyList<ApiCallRecord> Actions, int Steps);

/// <summary>
/// The in-app assistant: a tool-use loop over <see cref="EditorOperations"/>.
///
/// The model is offered a single tool, <c>vccad_operation</c>, whose description
/// carries the live operation catalog. Every call it makes goes through the same
/// registry the UI and the HTTP endpoint use, so the assistant can do exactly what
/// a person can — no more — and every action is written to the diagnostics log for
/// the person to inspect.
///
/// Vision works on request: when the model calls <c>capture.screenshot</c> the
/// workspace PNG is attached to the next message, so a text-only driver can see
/// the canvas through the model.
///
/// A JSON-in-content protocol is also accepted ({ "action": ... } / { "final": ... })
/// because not every endpoint enables native tool calling.
/// </summary>
public sealed class EditorAgent
{
    private const int MaxSteps = 24;

    /// <summary>
    /// Longest tool observation handed back to the model. A corpus-sized document
    /// (thousands of objects) otherwise produces a single observation large enough
    /// to exceed the model's context window.
    /// </summary>
    private const int MaxObservationChars = 3000;

    /// <summary>Diary entry name for a turn the person typed.</summary>
    public const string UserEntryName = "chat.user";

    /// <summary>Diary entry name for a completed assistant reply.</summary>
    public const string AssistantEntryName = "chat.assistant";

    /// <summary>Diary entry name for "start a new conversation".</summary>
    public const string ResetEntryName = "chat.reset";

    /// <summary>Sentinel meaning "the turn ended without a closing message".</summary>
    private const string NoAnswer = "\u0000no-answer";

    private readonly AutomationContext _context;
    private readonly VllmClient _client;
    private readonly InteractionLog? _diary;
    private readonly List<LlmMessage> _history = new();
    private readonly List<ChatEntry> _transcript = new();
    private readonly ContextCompressor _compressor;

    public EditorAgent(AutomationContext context, VllmClient client, InteractionLog? diary = null)
    {
        _context = context;
        _client = client;
        _diary = diary ?? context.History;
        _compressor = new ContextCompressor(_client.HistoryChars);
        _history.Add(new LlmMessage("system", BuildSystemPrompt()));

        // The conversation is continuous: what was said in an earlier run of the
        // editor is in the diary, so it is restored rather than lost with the process.
        RestoreFromDiary();
    }

    /// <summary>The conversation so far, oldest first.</summary>
    public IReadOnlyList<ChatEntry> Transcript => _transcript;

    /// <summary>The accumulated summary of everything compressed out of the context.</summary>
    public string CompressedSummary => _compressor.Summary;

    /// <summary>How many times the conversation has been compressed.</summary>
    public int CompressionCount => _compressor.CompressionCount;

    /// <summary>Raised whenever the transcript changes (any thread).</summary>
    public event EventHandler? TranscriptChanged;

    /// <summary>
    /// Raised with a short human-readable progress note (which step, which
    /// operation) so the person can watch the turn advance instead of staring at a
    /// frozen panel.
    /// </summary>
    public event EventHandler<string>? Progress;

    /// <summary>Resets the conversation, keeping the system prompt.</summary>
    public void Reset()
    {
        // A cleared conversation must stay cleared across a restart, so the reset is
        // written to the diary: restoration stops at the most recent reset marker.
        if (_diary is not null && _transcript.Count > 0)
        {
            Remember(ResetEntryName, $"cleared {_transcript.Count} transcript entries");
        }

        _compressor.Reset();
        _history.Clear();
        _history.Add(new LlmMessage("system", BuildSystemPrompt()));
        _transcript.Clear();
        TranscriptChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Restores the conversation recorded in the diary, oldest first, skipping
    /// anything cleared by a later reset. Only the person's turns and the
    /// assistant's replies are restored: the intermediate operation chatter stays in
    /// the History tab, so the Assistant tab reads as a conversation.
    /// </summary>
    /// <param name="maxEntries">Most recent entries to restore.</param>
    /// <param name="maxChars">Ceiling on the restored text, so a long history does not
    /// blow the context budget before the first turn.</param>
    public static IReadOnlyList<ChatEntry> RestoreTranscript(
        InteractionLog diary, int maxEntries = 40, int maxChars = 24000)
    {
        IReadOnlyList<InteractionRecord> tail = diary.Tail(2000);

        int start = 0;
        for (int i = tail.Count - 1; i >= 0; i--)
        {
            if (tail[i].Name == ResetEntryName)
            {
                start = i + 1;
                break;
            }
        }

        var spoken = new List<ChatEntry>();
        for (int i = start; i < tail.Count; i++)
        {
            InteractionRecord record = tail[i];
            if (record.Details is not { Length: > 0 })
            {
                continue;
            }

            if (record.Name == UserEntryName)
            {
                spoken.Add(new ChatEntry("user", record.Details));
            }
            else if (record.Name == AssistantEntryName)
            {
                spoken.Add(new ChatEntry("assistant", record.Details));
            }
        }

        var kept = new List<ChatEntry>();
        int chars = 0;
        for (int i = spoken.Count - 1; i >= 0; i--)
        {
            if (kept.Count >= maxEntries || chars + spoken[i].Text.Length > maxChars)
            {
                break;
            }

            chars += spoken[i].Text.Length;
            kept.Add(spoken[i]);
        }

        kept.Reverse();
        return kept;
    }

    private void RestoreFromDiary()
    {
        if (_diary is null)
        {
            return;
        }

        IReadOnlyList<ChatEntry> restored;
        try
        {
            restored = RestoreTranscript(_diary);
        }
        catch (Exception)
        {
            return; // an unreadable diary must not stop the assistant starting
        }

        foreach (ChatEntry entry in restored)
        {
            _transcript.Add(entry);
            _history.Add(new LlmMessage(entry.Role, entry.Text));
        }

        if (restored.Count > 0)
        {
            _transcript.Add(new ChatEntry(
                "action", $"restored {restored.Count} turn(s) from the diary — this conversation continues an earlier session"));
        }
    }

    /// <summary>Records one conversation turn in the diary.</summary>
    private void Remember(string name, string text)
    {
        if (_diary is null || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        try
        {
            _diary.Record(
                InteractionKind.Llm,
                InteractionCategory.Note,
                name,
                details: text,
                success: true,
                durationMs: 0,
                tags: new[] { "chat" });
        }
        catch (Exception)
        {
            // Persistence is valuable but never worth failing a turn for.
        }
    }

    /// <summary>Runs one user turn to completion.</summary>
    public async Task<AgentTurnResult> SendAsync(
        string prompt, bool withScreenshot = false, CancellationToken cancellationToken = default)
    {
        Append(new ChatEntry("user", prompt));
        Remember(UserEntryName, prompt);

        byte[]? startImage = withScreenshot ? _context.Screenshot?.Invoke() : null;
        string recall = RecallFor(prompt);
        if (recall.Length > 0)
        {
            Append(new ChatEntry("action", $"recalled {recall.Split('\n').Count(l => l.StartsWith("- ", StringComparison.Ordinal))} past entr(ies) from the diary"));
        }

        _history.Add(new LlmMessage("user", recall.Length == 0 ? prompt : prompt + "\n\n" + recall, startImage));

        var actions = new List<ApiCallRecord>();
        string reply = NoAnswer;
        int steps = 0;

        for (int step = 1; step <= MaxSteps; step++)
        {
            steps = step;
            cancellationToken.ThrowIfCancellationRequested();
            await CompressHistoryAsync(cancellationToken).ConfigureAwait(false);
            Report($"step {step}: asking {_client.Model}…");

            LlmReply completion = await _client.CompleteAsync(_history, VccadTools, cancellationToken)
                .ConfigureAwait(false);

            if (completion.ToolCalls.Count > 0)
            {
                _history.Add(new LlmMessage("assistant", completion.Text, ToolCalls: completion.ToolCalls));
                byte[]? screenshot = null;

                foreach (LlmToolCall call in completion.ToolCalls)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    (string op, JsonElement parameters) = ParseArguments(call.Arguments);
                    Report($"{op}…");
                    (string observation, ApiCallRecord? record) = RunOperation(op, parameters, call.Arguments);
                    if (record is not null)
                    {
                        actions.Add(record);
                    }

                    _history.Add(new LlmMessage("tool", observation, ToolCallId: call.Id));

                    if (op == "capture.screenshot" && screenshot is null)
                    {
                        screenshot = _context.Screenshot?.Invoke();
                    }
                }

                if (screenshot is { Length: > 0 })
                {
                    _history.Add(new LlmMessage("user", "Here is the current workspace after those changes.", screenshot));
                }

                continue;
            }

            string text = completion.Text?.Trim() ?? string.Empty;
            if (text.Length == 0)
            {
                break; // nothing actionable and nothing to say
            }

            // Fallback: a JSON directive in the content.
            if (TryParseDirective(text, out string? final, out string? action, out JsonElement directiveParams))
            {
                if (final is not null)
                {
                    reply = final;
                    _history.Add(new LlmMessage("assistant", final));
                    break;
                }

                (string observation, ApiCallRecord? record) = RunOperation(action!, directiveParams, text);
                if (record is not null)
                {
                    actions.Add(record);
                }

                _history.Add(new LlmMessage("assistant", text));
                _history.Add(new LlmMessage("user", "OBSERVATION " + observation));
                continue;
            }

            reply = text;
            _history.Add(new LlmMessage("assistant", reply));
            break;
        }

        // A turn that runs out of steps must still say what it did: "no answer" is
        // useless to the person watching, even when the work itself succeeded.
        if (reply == NoAnswer)
        {
            reply = await SummariseAsync(actions, steps, cancellationToken).ConfigureAwait(false);
        }

        Append(new ChatEntry("assistant", reply));
        Remember(AssistantEntryName, reply);
        return new AgentTurnResult(reply, actions, steps);
    }

    /// <summary>Asks for a closing summary; falls back to a deterministic one.</summary>
    private async Task<string> SummariseAsync(
        IReadOnlyList<ApiCallRecord> actions, int steps, CancellationToken cancellationToken)
    {
        try
        {
            _history.Add(new LlmMessage("user",
                "You have reached the step limit for this turn. Summarise what you changed, in one or two " +
                "sentences, with no tool calls."));
            LlmReply reply = await _client.CompleteAsync(_history, null, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(reply.Text))
            {
                return reply.Text.Trim();
            }
        }
        catch (Exception)
        {
            // Fall through to the deterministic summary below.
        }

        if (actions.Count == 0)
        {
            return $"I ran out of steps after {steps} model turns without changing the document.";
        }

        string operations = string.Join(", ", actions.Select(a => a.Operation).Distinct());
        string failures = actions.Where(a => !a.Success).Select(a => a.Operation).Distinct().ToArray() is { Length: > 0 } failed
            ? $" Failures: {string.Join(", ", failed)}."
            : string.Empty;
        return $"I ran {actions.Count} operations over {steps} steps ({operations}).{failures}";
    }

    /// <summary>Executes one operation and renders a model-facing observation.</summary>
    private (string Observation, ApiCallRecord? Record) RunOperation(
        string op, JsonElement parameters, string rawArguments)
    {
        try
        {
            object? result = Dispatcher.UIThread.CheckAccess()
                ? EditorOperations.Invoke(_context, op, parameters, ApiCallSource.Llm)
                : Dispatcher.UIThread.Invoke(() => EditorOperations.Invoke(_context, op, parameters, ApiCallSource.Llm));

            Append(new ChatEntry("action", $"{op} → ok"));

            if (op == "capture.screenshot")
            {
                return ("screenshot captured; the image is attached to the next message", DiagnosticsLog.Recent(1).FirstOrDefault());
            }

            string json = JsonSerializer.Serialize(new { ok = true, result });
            return (Truncate(json), DiagnosticsLog.Recent(1).FirstOrDefault());
        }
        catch (Exception ex)
        {
            Append(new ChatEntry("action", $"{op} → {ex.Message}"));
            return (JsonSerializer.Serialize(new { ok = false, error = ex.Message }),
                DiagnosticsLog.Recent(1).FirstOrDefault());
        }
    }

    /// <summary>Keeps an observation inside the model's context budget.</summary>
    private static string Truncate(string text)
        => text.Length <= MaxObservationChars
            ? text
            : text[..MaxObservationChars] + "…(truncated; narrow the query with object.find or max/offset)";

    /// <summary>
    /// Retrieval-augmented recall: looks through the whole diary — every earlier
    /// session, not just this one — for skills and operations related to the request,
    /// and hands them to the model as context.
    ///
    /// This is what lets "we designed a US size 10 bodice block, learn this as a
    /// skill" pay off later: the learned skill is retrieved on a similar request and
    /// the model can follow the recorded approach instead of rediscovering it.
    /// </summary>
    private string RecallFor(string prompt)
    {
        if (_diary is null)
        {
            return string.Empty;
        }

        var lines = new List<string>();

        foreach (InteractionRecord skill in _diary.Skills(prompt, limit: 3))
        {
            lines.Add($"- SKILL \"{skill.Target}\" (learned {skill.TimestampUtc.ToLocalTime():yyyy-MM-dd}): " +
                      Compact(skill.Details, 700));
        }

        foreach (InteractionRecord entry in _diary.Search(prompt, limit: 12)
                     .Where(e => e.Kind != InteractionKind.Skill)
                     .Where(e => !e.Name.StartsWith("chat.", StringComparison.Ordinal))
                     .Take(5))
        {
            string target = string.IsNullOrWhiteSpace(entry.Target) ? string.Empty : $" {Compact(entry.Target, 80)}";
            lines.Add($"- {entry.Kind.ToString().ToLowerInvariant()} {entry.Name}{target}: {Compact(entry.Details, 200)}");
        }

        if (lines.Count == 0)
        {
            return string.Empty;
        }

        return "RELEVANT PAST WORK from the application diary (earlier sessions; reuse it when it applies, " +
               "and use history.search for more):\n" + string.Join('\n', lines);
    }

    private static string Compact(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "(no details)";
        }

        string flattened = string.Join(' ', text.Split(
            new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return flattened.Length <= max ? flattened : flattened[..max] + "…";
    }

    /// <summary>
    /// Compresses the middle of the conversation once it outgrows the endpoint's
    /// budget, instead of deleting it.
    ///
    /// Two things must not happen: the endpoint rejects a conversation with no user
    /// query ("No user query found in messages"), and a <c>tool</c> result must never
    /// lose the assistant message that requested it — hence cutting only at a user
    /// turn boundary. What is cut is turned into a summary that is carried forward,
    /// so a long session accumulates what happened rather than forgetting it.
    /// </summary>
    private async Task CompressHistoryAsync(CancellationToken cancellationToken)
    {
        // The accumulated summary already holds the earlier material, so the
        // compressor hands back only the messages that are genuinely new;
        // re-summarising the summary would duplicate it.
        IReadOnlyList<LlmMessage>? region = _compressor.SelectRegion(_history);
        if (region is null)
        {
            return;
        }

        string digest = ContextCompressor.Digest(region);
        string written = await SummariseRegionAsync(region, cancellationToken).ConfigureAwait(false);

        string combined = string.IsNullOrWhiteSpace(written)
            ? digest
            : digest.Length == 0 ? written : written + Environment.NewLine + digest;

        if (string.IsNullOrWhiteSpace(combined))
        {
            return;
        }

        List<LlmMessage> rebuilt = _compressor.Apply(_history, combined);
        _history.Clear();
        _history.AddRange(rebuilt);

        Append(new ChatEntry("action",
            $"compressed {region.Count} earlier message(s) into a summary ({_compressor.Summary.Length} chars kept)"));
        Report($"compressed earlier context into a summary ({_compressor.Summary.Length} chars)");
    }

    /// <summary>
    /// Asks the model for a compact record of the dropped region. Best effort: the
    /// deterministic digest is always kept alongside it, so a failed or forgetful
    /// summariser cannot lose a fact the user asked to be remembered.
    /// </summary>
    private async Task<string> SummariseRegionAsync(
        IReadOnlyList<LlmMessage> region, CancellationToken cancellationToken)
    {
        try
        {
            var transcript = new StringBuilder();
            foreach (LlmMessage message in region)
            {
                if (string.IsNullOrWhiteSpace(message.Text))
                {
                    continue;
                }

                transcript.AppendLine($"{message.Role}: {message.Text}");
                if (transcript.Length > 60000)
                {
                    transcript.AppendLine("…(the rest is in the deterministic notes)");
                    break;
                }
            }

            var messages = new List<LlmMessage>
            {
                new("system",
                    "You compress the earlier part of a working conversation so it can be carried " +
                    "forward without its full text. Write a compact record, at most 1200 characters, " +
                    "with these headings: DECIDED, DONE, FAILED, STILL TO DO. Keep every concrete fact " +
                    "the user stated or asked to be remembered, verbatim — names, ids, numbers, paths, " +
                    "codewords, sizes — and keep the names of operations that worked. Do not invent " +
                    "anything and do not repeat the notes already carried forward. Reply with the " +
                    "record only."),
                new("user", "EARLIER PART OF THE CONVERSATION:\n" + transcript),
            };

            LlmReply reply = await _client.CompleteAsync(messages, null, cancellationToken).ConfigureAwait(false);
            return reply.Text?.Trim() ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty; // the digest carries the facts
        }
    }

    private static (string Op, JsonElement Parameters) ParseArguments(string arguments)
    {
        try
        {
            JsonElement root = JsonSerializer.Deserialize<JsonElement>(arguments);
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (arguments.Trim(), default);
            }

            string op = root.TryGetProperty("op", out JsonElement opElement) && opElement.ValueKind == JsonValueKind.String
                ? opElement.GetString() ?? string.Empty
                : string.Empty;

            JsonElement parameters = root.TryGetProperty("params", out JsonElement p) ? p : default;
            return (op, parameters);
        }
        catch (JsonException)
        {
            return (arguments.Trim(), default);
        }
    }

    private void Append(ChatEntry entry)
    {
        _transcript.Add(entry);
        TranscriptChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Report(string message) => Progress?.Invoke(this, message);

    /// <summary>
    /// Reads a JSON directive from message content. Tolerates code fences and
    /// surrounding prose by locating the outermost JSON object.
    /// </summary>
    private static bool TryParseDirective(string text, out string? final, out string? action, out JsonElement parameters)
    {
        final = null;
        action = null;
        parameters = default;

        int start = text.IndexOf('{');
        int end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return false;
        }

        JsonElement root;
        try
        {
            root = JsonSerializer.Deserialize<JsonElement>(text[start..(end + 1)]);
        }
        catch (JsonException)
        {
            return false;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        final = root.TryGetProperty("final", out JsonElement f) && f.ValueKind == JsonValueKind.String
            ? f.GetString()
            : null;
        action = root.TryGetProperty("action", out JsonElement a) && a.ValueKind == JsonValueKind.String
            ? a.GetString()
            : null;
        parameters = root.TryGetProperty("params", out JsonElement p) ? p : default;

        return final is not null || action is not null;
    }

    /// <summary>The single generic tool offered to the model, carrying the catalog.</summary>
    private static IReadOnlyList<LlmTool> VccadTools => new[]
    {
        new LlmTool(
            "vccad_operation",
            "Run ONE VCCad editor operation against the live document the user is looking at. " +
            "The change appears on their canvas immediately.\n\nAvailable operations (use exactly these names):\n" +
            EditorOperations.Catalog(),
            """
            {
              "type": "object",
              "properties": {
                "op": { "type": "string", "description": "The operation name, e.g. object.create" },
                "params": { "type": "object", "description": "The operation's parameters as documented in the tool description" }
              },
              "required": ["op"]
            }
            """),
    };

    /// <summary>
    /// The system prompt: what the assistant IS, then what it is for, then how it
    /// works, then the operational rules learned from real failures.
    ///
    /// The order matters. A model handed tool descriptions and operational advice
    /// with no statement of purpose waits to be told what it is allowed to do — it
    /// does not paginate unless someone says pagination is in scope. Public so a test
    /// can pin that the scope is stated, since the prompt is the behaviour.
    /// </summary>
    public static string BuildSystemPrompt()
    {
        var sb = new StringBuilder();
        sb.AppendLine("WHO YOU ARE");
        sb.AppendLine("You are the assistant built into VCCad. VCCad is a vector graphics application");
        sb.AppendLine("(Illustrator-style) that stores documents as PDF, and you are its automation layer:");
        sb.AppendLine("the way the whole program is driven by intent instead of by hand. That is your");
        sb.AppendLine("purpose, not an extra feature — the user is talking to the application through you.");
        sb.AppendLine();
        sb.AppendLine("WHAT YOU ARE FOR");
        sb.AppendLine("Your scope is the WHOLE application, not just drawing. Anything a person can do in");
        sb.AppendLine("VCCad, you can do, through the same operation registry the interface and the HTTP");
        sb.AppendLine("endpoint use. That includes, explicitly:");
        sb.AppendLine("- drawing and editing: shapes, paths and nodes, fills, strokes, groups, transforms,");
        sb.AppendLine("  alignment and arrangement;");
        sb.AppendLine("- text editing and typography: creating, updating, selecting, aligning and styling");
        sb.AppendLine("  text runs, fonts, sizes and spacing;");
        sb.AppendLine("- pagination and imposition: artboards and pages, page bounds and sizes, arranging,");
        sb.AppendLine("  repeating and imposing work across pages;");
        sb.AppendLine("- document structure and layers: artboards, layers, groups, visibility, ordering and");
        sb.AppendLine("  document properties;");
        sb.AppendLine("- colour and styles: RGB and CMYK fills and strokes, dash patterns;");
        sb.AppendLine("- import and export: opening and importing real-world PDFs, exporting and saving;");
        sb.AppendLine("- inspection: listing and finding objects, dumping the screen, describing what is");
        sb.AppendLine("  visible, and searching the application diary.");
        sb.AppendLine("If you can picture a person doing it in this program, assume you can do it too. Look");
        sb.AppendLine("for the operation that does it; do not wait to be told that you are allowed.");
        sb.AppendLine();
        sb.AppendLine("HOW YOU WORK");
        sb.AppendLine("Act. When the user asks for something, call operations and make it happen. Do not");
        sb.AppendLine("answer by reciting what the program can do, and do not ask the person to restate the");
        sb.AppendLine("program's capabilities to you. Ask a question only when the request is genuinely");
        sb.AppendLine("ambiguous in a way that changes the work.");
        sb.AppendLine("Never invent an operation: the catalog in the tool description is the truth. If");
        sb.AppendLine("nothing in it fits, say so plainly and describe what is missing rather than calling");
        sb.AppendLine("something that does not exist.");
        sb.AppendLine("You work inside the running application: the user watches your edits appear on their");
        sb.AppendLine("canvas in real time. Call the vccad_operation tool to do anything. You may call it");
        sb.AppendLine("repeatedly, one operation at a time, until the task is done. When you are finished (or");
        sb.AppendLine("need to ask the user something), reply with ordinary text and no tool call.");
        sb.AppendLine();
        sb.AppendLine("Operational rules learned from real failures:");
        sb.AppendLine("- Coordinates are PDF points (72 per inch), origin at the top-left of the document.");
        sb.AppendLine("- Most operations act on the current SELECTION. Call object.list or object.find to get ids,");
        sb.AppendLine("  then selection.set before styling or transforming existing objects.");
        sb.AppendLine("- object.list pages (default 200 rows) because real documents hold thousands of objects.");
        sb.AppendLine("  Prefer object.find with a name/text/layer filter over listing everything.");
        sb.AppendLine("- Case conversions on an ALL-CAPS label keep the wording and spacing and only change the");
        sb.AppendLine("  letter case: \"UPPER CUP\" becomes \"Upper Cup\", not \"UpperCup\".");
        sb.AppendLine("- To centre text, call text.centerIn — do not hunt for a containing rectangle by");
        sb.AppendLine("  repeated searching. Use target=artboard, target=item with targetItemId, or target=rect.");
        sb.AppendLine("- If a search returns nothing, do not repeat it: the object may have been renamed or the");
        sb.AppendLine("  filter may be wrong. Try a different field or read the layer with object.list.");
        sb.AppendLine("- Call capture.screenshot to see the document. The image is attached to your next");
        sb.AppendLine("  message, so you can check colours, overlap and layout yourself before declaring done.");
        sb.AppendLine("- Verify meaningful work with object.find or document.summary before you finish.");
        sb.AppendLine("- If an operation fails, read the error and adjust; do not repeat a failing call.");
        sb.AppendLine("- Do not delete or overwrite the user's work unless you were asked to.");
        sb.AppendLine("- ALWAYS finish the turn with a plain-text answer describing what you changed, even if");
        sb.AppendLine("  you could not complete everything.");
        sb.AppendLine();
        sb.AppendLine("The application diary records everything: every pointer and keyboard action the person");
        sb.AppendLine("takes, every operation (yours, theirs and external automation's) and every model request,");
        sb.AppendLine("across all previous sessions and stored on disk.");
        sb.AppendLine("- history.search {query}      — recall how something was done before. Use it when the");
        sb.AppendLine("  request resembles earlier work, or when the user refers to what \"we did\" previously.");
        sb.AppendLine("- history.skills {query}      — the distilled skills learned from completed work.");
        sb.AppendLine("- history.session {sessionId} — the full ordered record of one session.");
        sb.AppendLine("- history.learn {title, description, tags} — when the user says \"learn this as a skill\"");
        sb.AppendLine("  (or the task was clearly a reusable recipe), store it. Include what inputs it needs.");
        sb.AppendLine("- history.note {text}         — record a decision or caveat worth remembering.");
        sb.AppendLine("Relevant skills and past steps are recalled for you automatically at the start of a turn;");
        sb.AppendLine("follow a recalled approach when it fits rather than working it out again. This conversation");
        sb.AppendLine("also continues across restarts, so earlier turns may already be in your context.");
        sb.AppendLine();
        sb.AppendLine("Worked example — \"open the sample, hide all size layers except UK 6, rename the text");
        sb.AppendLine("UPPER CUP to Upper Cup and centre it in its rectangle\":");
        sb.AppendLine("  1. {\"action\":\"document.openFile\",\"params\":{\"path\":\"A0 Temi Bow Bustier sewing pattern.pdf\"}}");
        sb.AppendLine("  2. {\"action\":\"layer.onlyVisible\",\"params\":{\"keep\":[\"UK 6\"],\"match\":\"UK\"}}");
        sb.AppendLine("  3. {\"action\":\"object.find\",\"params\":{\"text\":\"UPPER CUP\"}}        ← gives the itemId");
        sb.AppendLine("  4. {\"action\":\"selection.set\",\"params\":{\"itemIds\":[\"<id>\"]}}");
        sb.AppendLine("  5. {\"action\":\"text.update\",\"params\":{\"text\":\"Upper Cup\"}}");
        sb.AppendLine("  6. {\"action\":\"text.centerIn\",\"params\":{\"itemId\":\"<id>\"}}          ← centres it; nothing else needed");
        sb.AppendLine("  7. {\"final\":\"...\"}");
        sb.AppendLine("Note step 6: do NOT search for a rectangle first — text.centerIn finds it.");
        return sb.ToString();
    }
}
