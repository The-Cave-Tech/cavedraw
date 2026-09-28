using VCCad.App.Ai;
using VCCad.App.ViewModels;

namespace VCCad.App.Automation;

/// <summary>
/// Owns the application's automation and assistant services: the operation
/// registry's context, the HTTP endpoint, and the chat agent.
///
/// One host exists per running editor (see <see cref="Current"/>), so an external
/// client, the diagnostics window and the model all drive the same live document.
/// </summary>
public sealed class AutomationHost
{
    private AutomationHost(
        EditorViewModel viewModel,
        Func<byte[]?> screenshot,
        Func<Avalonia.Visual?> inputRoot,
        Func<int, string>? uiTreeDump,
        Func<Avalonia.Controls.Control?>? uiRoot,
        ViewportActions? viewport,
        HostActions? hostActions,
        InteractionLog? history,
        LlmOptions options)
    {
        Llm = options;
        Client = new VllmClient(options);
        Viewport = viewport;
        History = history;
        Context = new AutomationContext
        {
            ViewModel = viewModel,
            Screenshot = screenshot,
            InputRoot = inputRoot,
            UiTreeDump = uiTreeDump,
            UiRoot = uiRoot,
            Viewport = viewport,
            Host = hostActions,
            History = history,
            Describe = DescribeAsync,
            Chat = async (prompt, withScreenshot) => Summarise(await ChatAsync(prompt, withScreenshot)),
            Cancellation = CurrentToken,
            Cancel = CancelCurrent,
        };
        Agent = new EditorAgent(Context, Client, history);

        if (history is not null)
        {
            // Everything that runs through the operation registry is already in the
            // audit trail; mirror it into the diary so one search covers UI events,
            // automation and model work alike.
            DiagnosticsLog.Recorded += OnAuditRecorded;
        }
    }

    /// <summary>Mirrors an audit-trail record into the persistent diary.</summary>
    private void OnAuditRecorded(object? sender, ApiCallRecord record)
    {
        if (History is null || record.Operation == "history.record")
        {
            return;
        }

        InteractionKind kind = record.Source switch
        {
            ApiCallSource.Ui => InteractionKind.Ui,
            ApiCallSource.Api => InteractionKind.Api,
            ApiCallSource.Llm => InteractionKind.Llm,
            _ => InteractionKind.System,
        };

        InteractionCategory category = record.Operation.StartsWith("llm.", StringComparison.Ordinal)
            ? InteractionCategory.Model
            : InteractionCategory.Operation;

        History.Record(
            kind,
            category,
            record.Operation,
            target: null,
            details: record.Success ? record.Parameters : $"{record.Parameters} → ERROR {record.Error}",
            success: record.Success,
            durationMs: record.DurationMs,
            tags: new[] { record.Source.ToString().ToLowerInvariant() });
    }

    /// <summary>The running host, or null before the editor is up.</summary>
    public static AutomationHost? Current { get; private set; }

    /// <summary>Model/endpoint settings.</summary>
    public LlmOptions Llm { get; }

    /// <summary>The model client.</summary>
    public VllmClient Client { get; }

    /// <summary>The assistant.</summary>
    public EditorAgent Agent { get; }

    /// <summary>Viewport controls (fit/zoom), when a window is available.</summary>
    public ViewportActions? Viewport { get; }

    /// <summary>The application diary, when one is running.</summary>
    public InteractionLog? History { get; }

    /// <summary>The HTTP automation endpoint (null when disabled).</summary>
    public AutomationServer? Server { get; private set; }

    /// <summary>The operation context shared by the HTTP endpoint and the agent.</summary>
    public AutomationContext Context { get; }

    /// <summary>The bound automation port, or 0 when the server is disabled.</summary>
    public int Port => Server?.Port ?? 0;

    /// <summary>Creates and (optionally) starts the services for a running editor.</summary>
    public static AutomationHost Create(
        EditorViewModel viewModel,
        Func<byte[]?> screenshot,
        Func<Avalonia.Visual?> inputRoot,
        LlmOptions? options = null,
        int port = 5099,
        bool startServer = true,
        Func<int, string>? uiTreeDump = null,
        Func<Avalonia.Controls.Control?>? uiRoot = null,
        ViewportActions? viewport = null,
        HostActions? host = null,
        InteractionLog? history = null)
    {
        options ??= new LlmOptions();
        var created = new AutomationHost(
            viewModel, screenshot, inputRoot, uiTreeDump, uiRoot, viewport, host, history, options);

        if (startServer)
        {
            // A port clash must not prevent the editor from starting: fall back to
            // an ephemeral port so automation is still available.
            try
            {
                created.Server = AutomationServer.Start(created.Context, port);
            }
            catch (System.Net.Sockets.SocketException)
            {
                created.Server = AutomationServer.Start(created.Context, 0);
            }
        }

        Current = created;
        DiagnosticsLog.Add(ApiCallSource.System, "host.ready",
            $"{{\"port\":{created.Port},\"model\":\"{options.Model}\"}}", null, true, 0);
        return created;
    }

    /// <summary>Runs one chat turn.</summary>
    public async Task<AgentTurnResult> ChatAsync(string prompt, bool withScreenshot)
    {
        (CancellationToken token, bool owner) = EnterOperation();
        try
        {
            return await Agent.SendAsync(prompt, withScreenshot, token).ConfigureAwait(false);
        }
        finally
        {
            ExitOperation(owner);
        }
    }

    // ------------------------------------------------------------------
    // Busy / cancellation
    // ------------------------------------------------------------------

    private readonly object _gate = new();
    private CancellationTokenSource? _current;

    /// <summary>True while a long-running operation (chat or vision) is in flight.</summary>
    public bool IsBusy
    {
        get
        {
            lock (_gate)
            {
                return _current is not null;
            }
        }
    }

    /// <summary>Raised when <see cref="IsBusy"/> changes (any thread).</summary>
    public event EventHandler? BusyChanged;

    /// <summary>The token for the current operation, or a token that never cancels.</summary>
    public CancellationToken CurrentToken()
    {
        lock (_gate)
        {
            return _current?.Token ?? CancellationToken.None;
        }
    }

    /// <summary>
    /// Asks the in-flight operation to stop. The cancellation surfaces as a
    /// cancelled observation, not an error, and the interface is re-enabled.
    /// </summary>
    public void CancelCurrent()
    {
        lock (_gate)
        {
            _current?.Cancel();
        }
    }

    /// <summary>
    /// Marks a long-running operation as started. Nested calls (a vision request
    /// inside a chat turn) reuse the outer token so cancelling stops the whole turn.
    /// </summary>
    private (CancellationToken Token, bool Owner) EnterOperation()
    {
        lock (_gate)
        {
            if (_current is not null)
            {
                return (_current.Token, false);
            }

            _current = new CancellationTokenSource();
            _ = Task.Run(() => BusyChanged?.Invoke(this, EventArgs.Empty));
            return (_current.Token, true);
        }
    }

    private void ExitOperation(bool owner)
    {
        if (!owner)
        {
            return;
        }

        lock (_gate)
        {
            _current?.Dispose();
            _current = null;
        }

        _ = Task.Run(() => BusyChanged?.Invoke(this, EventArgs.Empty));
    }

    /// <summary>
    /// Vision helper used by the <c>ui.describe</c> operation: the model is asked
    /// to describe a view or region, optionally with the workspace image attached.
    /// </summary>
    private async Task<string> DescribeAsync(string prompt, byte[]? image, CancellationToken cancellationToken)
    {
        var messages = new List<LlmMessage>
        {
            new("system",
                "You are a vision assistant embedded in VCCad, a vector graphics editor. You are shown " +
                "either a screenshot of the running application or a structured text dump of it, and you " +
                "describe precisely what is visible. Be concrete: name the panels, controls, artboards, " +
                "objects, text, colours and coordinates you can actually see or read. If something is " +
                "ambiguous or not visible, say so rather than guessing. Keep the answer focused and " +
                "ordered by importance."),
            new("user", prompt, image),
        };

        LlmReply reply = await Client.CompleteAsync(messages, null, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(reply.Text) ? "(the model returned no description)" : reply.Text;
    }

    /// <summary>Transport-friendly projection of a turn (for the HTTP route).</summary>
    private static object Summarise(AgentTurnResult result) => new
    {
        reply = result.Reply,
        steps = result.Steps,
        actions = result.Actions.Select(a => new
        {
            op = a.Operation,
            success = a.Success,
            durationMs = Math.Round(a.DurationMs, 2),
            error = a.Error,
        }).ToArray(),
    };
}

/// <summary>Options parsed from the desktop host's command line.</summary>
public sealed class DesktopStartupOptions
{
    /// <summary>Automation port (0 = ephemeral).</summary>
    public int Port { get; set; } = 5099;

    /// <summary>Disable the HTTP automation endpoint.</summary>
    public bool NoServer { get; set; }

    /// <summary>Prompt to send to the assistant on startup.</summary>
    public string? ChatPrompt { get; set; }

    /// <summary>Attach a screenshot to the startup prompt.</summary>
    public bool ChatWithScreenshot { get; set; } = true;

    /// <summary>
    /// Open the diagnostics overlay on startup. It is on by default: development
    /// happens with the model working in the same window, and the panel is the only
    /// live view of what it is doing.
    /// </summary>
    public bool ShowDiagnostics { get; set; } = true;

    /// <summary>Start with the diagnostics overlay hidden.</summary>
    public bool NoDiagnostics { get; set; }

    /// <summary>Skip UI event recording (pointer, keyboard, drag and drop).</summary>
    public bool NoRecording { get; set; }

    /// <summary>Where the diary is stored; defaults to the per-user application data.</summary>
    public string? HistoryDirectory { get; set; }

    /// <summary>The command-line arguments, recorded with the session header.</summary>
    public string[]? OriginalArguments { get; set; }

    /// <summary>Do not dock the window to the right half of the screen.</summary>
    public bool NoDock { get; set; }

    /// <summary>Print usage and exit.</summary>
    public bool ShowHelp { get; set; }

    /// <summary>Model/endpoint settings.</summary>
    public LlmOptions Llm { get; } = new();

    /// <summary>Parses the host's arguments.</summary>
    public static DesktopStartupOptions Parse(string[] args)
    {
        var options = new DesktopStartupOptions { OriginalArguments = args };
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            string? Next() => i + 1 < args.Length ? args[++i] : null;

            switch (arg)
            {
                case "--chat":
                case "--prompt":
                    options.ChatPrompt = Next();
                    options.ShowDiagnostics = true;
                    break;
                case "--no-diagnostics":
                    options.NoDiagnostics = true;
                    options.ShowDiagnostics = false;
                    break;
                case "--no-recording":
                case "--no-diary":
                    options.NoRecording = true;
                    break;
                case "--history-dir":
                    options.HistoryDirectory = Next();
                    break;
                case "--port":
                    if (int.TryParse(Next(), out int port))
                    {
                        options.Port = port;
                    }

                    break;
                case "--no-server":
                    options.NoServer = true;
                    break;
                case "--no-screenshot":
                    options.ChatWithScreenshot = false;
                    break;
                case "--diagnostics":
                    options.ShowDiagnostics = true;
                    break;
                case "--no-dock":
                    options.NoDock = true;
                    break;
                case "--model":
                    options.Llm.Model = Next() ?? options.Llm.Model;
                    break;
                case "--llm-url":
                    options.Llm.BaseUrl = Next() ?? options.Llm.BaseUrl;
                    break;
                case "--api-key":
                    options.Llm.ApiKey = Next() ?? options.Llm.ApiKey;
                    break;
                case "--help":
                case "-h":
                    options.ShowHelp = true;
                    break;
            }
        }

        return options;
    }

    /// <summary>Command-line help text.</summary>
    public static string Usage =>
        """
        VCCad desktop host

          VCCad.App.Desktop [options]

          --chat "PROMPT"     send PROMPT to the built-in assistant on startup
                              (opens the diagnostics overlay)
          --diagnostics       open the diagnostics overlay
          --no-diagnostics    start with the diagnostics overlay hidden
          --no-recording      do not record pointer/keyboard/drag events in the diary
          --history-dir DIR   where the interaction diary is stored
                              (default %APPDATA%\VCCad\history, env VCCAD_HISTORY_DIR)
          --no-dock           do not dock the window to the right half of the screen
          --port N            automation endpoint port (default 5099, 0 = ephemeral)
          --no-server         do not start the automation endpoint
          --no-screenshot     do not attach a workspace screenshot to --chat
          --model NAME        model name (default qwen3.8-27b)
          --llm-url URL       OpenAI-compatible base URL
          --api-key KEY       bearer token for the model endpoint
          --help              show this help

        Environment: VCCAD_LLM_MODEL, VCCAD_LLM_BASE, VCCAD_LLM_KEY, VCCAD_HISTORY_DIR
        """;
}

/// <summary>
/// Startup options handed from the desktop entry point to the Avalonia
/// application, which creates the services once the main window exists.
/// </summary>
public static class DesktopStartup
{
    /// <summary>Options parsed from the process arguments.</summary>
    public static DesktopStartupOptions Options { get; set; } = new();
}
