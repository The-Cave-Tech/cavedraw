using System.Net;
using System.Net.Sockets;
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
        InteractionLog? history = null,
        string? instanceName = null)
    {
        options ??= new LlmOptions();
        var created = new AutomationHost(
            viewModel, screenshot, inputRoot, uiTreeDump, uiRoot, viewport, host, history, options);

        if (startServer)
        {
            // An explicit port is a promise. The launcher has already reserved the
            // listener (PrepareAutomationPort), so adopt it; the port is held from
            // before the UI starts and cannot be taken in the meantime. Without a
            // reservation — tests, an embedding host — bind here, and let an
            // explicit port fail loudly rather than drifting to another one.
            TcpListener? reserved = TakeReservation(port);
            created.Server = reserved is null
                ? AutomationServer.Start(created.Context, port)
                : AutomationServer.Start(created.Context, reserved);

            // A named instance publishes where it actually ended up, so a driver
            // that launched it can find the port without having seen stdout.
            // The name normally comes from the launcher's claim; an embedding
            // caller can pass one explicitly instead of relying on that global.
            string? name = instanceName ?? AutomationInstanceRegistry.Current;
            if (name is not null)
            {
                AutomationInstanceRegistry.Publish(name, created.Server.Port);
                created.Server.InstanceName = name;
            }
        }

        Current = created;

        // The truth at startup, on every run: the port actually bound. On a WinExe
        // the console is not attached, so this is a convenience; the diary and the
        // named-instance file are the durable records.
        int bound = created.Port;
        Report(bound > 0
            ? $"automation endpoint listening on http://127.0.0.1:{bound}" +
              (port != 0 && bound != port ? $" (port {port} was not available)" : string.Empty) +
              (created.Server!.InstanceName is { } named
                  ? $" (instance '{named}', {AutomationInstanceRegistry.FileFor(named)})"
                  : string.Empty)
            : "automation endpoint disabled (--no-server)");

        DiagnosticsLog.Add(ApiCallSource.System, "host.ready",
            $"{{\"port\":{bound},\"requested\":{port},\"model\":\"{options.Model}\"}}", null, true, 0);
        return created;
    }

    /// <summary>
    /// Writes a startup fact where a launcher can see it. A WinExe has no console
    /// attached, so this can go nowhere; it must never be the only record and it
    /// must never stop the editor.
    /// </summary>
    private static void Report(string message)
    {
        try
        {
            Console.Out.WriteLine($"[vccad] {message}");
            Console.Out.Flush();
        }
        catch (Exception)
        {
            // No console, or a closed pipe. The diary and the instance file remain.
        }
    }

    // ------------------------------------------------------------------
    // The explicit port is a promise
    // ------------------------------------------------------------------

    private static readonly object ReserveGate = new();
    private static TcpListener? _reservedListener;
    private static int _reservedFor;

    /// <summary>
    /// Secures the port the caller asked for, before the UI starts, so a launcher
    /// gets a truthful failure and a non-zero exit code instead of a window that
    /// quietly answers on a different port.
    ///
    /// Returns <c>null</c> when the port is secured (or when an arbitrary port was
    /// requested), otherwise a message naming what failed. The reserved listener is
    /// adopted by <see cref="Create"/>.
    /// </summary>
    public static string? PrepareAutomationPort(DesktopStartupOptions options)
    {
        if (options.NoServer)
        {
            return string.IsNullOrWhiteSpace(options.Name)
                ? null
                : "--name publishes an automation endpoint, but --no-server disables it; " +
                  "drop --no-server or drop --name";
        }

        // Claim the name first: two instances must never share one, and the name
        // must be free before any port is bound for it.
        if (!string.IsNullOrWhiteSpace(options.Name))
        {
            string? claimError = AutomationInstanceRegistry.Claim(
                options.Name, options.OriginalArguments ?? Array.Empty<string>());
            if (claimError is not null)
            {
                return claimError;
            }
        }

        // 0 is the sanctioned opt-in: bind whatever is free and report it.
        if (options.Port == 0)
        {
            return null;
        }

        var listener = new TcpListener(IPAddress.Loopback, options.Port);
        try
        {
            listener.Start();
        }
        catch (SocketException ex)
        {
            listener.Dispose();
            AutomationInstanceRegistry.Release(options.Name);
            return new AutomationPortUnavailableException(options.Port, ex).Message +
                   "; pass --port 0 (or --name NAME) to accept an arbitrary port";
        }

        lock (ReserveGate)
        {
            _reservedListener?.Dispose();
            _reservedListener = listener;
            _reservedFor = options.Port;
        }

        return null;
    }

    /// <summary>The launcher's reservation for <paramref name="port"/>, if any.</summary>
    private static TcpListener? TakeReservation(int port)
    {
        lock (ReserveGate)
        {
            if (_reservedListener is null || _reservedFor != port)
            {
                return null;
            }

            TcpListener listener = _reservedListener;
            _reservedListener = null;
            return listener;
        }
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

/// <summary>
/// Named instances: a driver picks a name, the app publishes where it is, and two
/// live instances can never share one.
///
/// A name is a promise in the same way an explicit port is. The port is not the
/// thing a caller should have to negotiate — it invites the exact failure this
/// exists to remove, where a launch is followed by a call to a port that turns out
/// to be someone else's. Instead the app binds an arbitrary free port and writes
/// <c>%APPDATA%\VCCad\instances\&lt;name&gt;.json</c>, which the caller reads.
///
/// The file is created exclusively, so two simultaneous launches cannot both win
/// the name; it is deleted on a clean exit; and a file whose pid is no longer
/// running is treated as stale, so a crash does not hold the name for ever.
/// </summary>
public static class AutomationInstanceRegistry
{
    /// <summary>Overrides the instance directory (used by tests and CI).</summary>
    public const string DirectoryVariable = "VCCAD_INSTANCE_DIR";

    private static readonly object Gate = new();
    private static string? _claimed;
    private static string[] _claimedArguments = Array.Empty<string>();
    private static bool _exitHookInstalled;

    /// <summary>
    /// The last-resort release. The endpoint is normally disposed on shutdown, but
    /// a clean exit must free the name even if that wiring is bypassed, so the hook
    /// is installed when a name is claimed and releases whatever is still held.
    /// A process that is killed outright cannot run this — the pid check in
    /// <see cref="Claim"/> is what makes that case recoverable.
    /// </summary>
    private static void InstallExitHook()
    {
        if (_exitHookInstalled)
        {
            return;
        }

        _exitHookInstalled = true;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Release(Current);
    }

    /// <summary>Where instance files live: <c>%APPDATA%\VCCad\instances</c>.</summary>
    public static string Directory
    {
        get
        {
            string? custom = Environment.GetEnvironmentVariable(DirectoryVariable);
            if (!string.IsNullOrWhiteSpace(custom))
            {
                return custom;
            }

            string root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrEmpty(root))
            {
                root = Path.Combine(Path.GetTempPath(), "vccad");
            }

            return Path.Combine(root, "VCCad", "instances");
        }
    }

    /// <summary>The discovery file for <paramref name="name"/>.</summary>
    public static string FileFor(string name) => Path.Combine(Directory, name + ".json");

    /// <summary>The name this process has claimed, or null.</summary>
    public static string? Current
    {
        get
        {
            lock (Gate)
            {
                return _claimed;
            }
        }
    }

    /// <summary>
    /// Claims <paramref name="name"/> for this process. Returns <c>null</c> on
    /// success, otherwise a message explaining who holds it.
    /// </summary>
    public static string? Claim(string name, string[] arguments)
    {
        if (!IsValidName(name, out string? invalid))
        {
            return invalid;
        }

        lock (Gate)
        {
            string directory = Directory;
            try
            {
                System.IO.Directory.CreateDirectory(directory);
            }
            catch (Exception ex)
            {
                return $"cannot create the instance directory '{directory}': {ex.Message}";
            }

            string path = FileFor(name);
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        WriteEntry(stream, name, port: 0, arguments, claimOnly: true);
                    }

                    _claimed = name;
                    _claimedArguments = arguments;
                    InstallExitHook();
                    return null;
                }
                catch (IOException)
                {
                    // Either a live instance holds the name or a stale file is in
                    // the way; both are decided below.
                }
                catch (UnauthorizedAccessException ex)
                {
                    return $"cannot claim instance name '{name}': {ex.Message}";
                }

                InstanceEntry? existing = Read(path);
                if (existing is not null && IsAlive(existing.Pid))
                {
                    string where = existing.Port > 0 ? $" (http://127.0.0.1:{existing.Port})" : string.Empty;
                    return $"instance name '{name}' is already in use by pid {existing.Pid}{where}; " +
                           $"pick another --name or stop that process";
                }

                // Stale: the process that wrote it is gone. Reclaim the name.
                try
                {
                    File.Delete(path);
                }
                catch (Exception)
                {
                    return $"instance name '{name}' is held by a stale file that cannot be replaced: {path}";
                }
            }

            return $"instance name '{name}' is already in use, and the stale file could not be taken over: {path}";
        }
    }

    /// <summary>
    /// Rewrites the instance file now that the endpoint is up, so a caller polling
    /// it sees the port. Best effort: the app must start even if this fails.
    /// </summary>
    public static void Publish(string name, int port)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        lock (Gate)
        {
            // The launcher's claim carries the original command line; an explicit
            // name has no such record, so its file simply has no args.
            string[] arguments = string.Equals(name, _claimed, StringComparison.Ordinal)
                ? _claimedArguments
                : Array.Empty<string>();

            try
            {
                System.IO.Directory.CreateDirectory(Directory);
                using var stream = new FileStream(FileFor(name), FileMode.Create, FileAccess.Write, FileShare.None);
                WriteEntry(stream, name, port, arguments, claimOnly: false);
            }
            catch (Exception)
            {
                // Discovery is a convenience; never stop the editor for it.
            }
        }
    }

    /// <summary>Releases <paramref name="name"/> if this process holds it.</summary>
    public static void Release(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        lock (Gate)
        {
            if (string.Equals(name, _claimed, StringComparison.Ordinal))
            {
                _claimed = null;
            }

            try
            {
                InstanceEntry? existing = Read(FileFor(name));
                if (existing is not null && existing.Pid == Environment.ProcessId)
                {
                    File.Delete(FileFor(name));
                }
            }
            catch (Exception)
            {
                // A leftover file is reclaimable by pid; nothing to do here.
            }
        }
    }

    /// <summary>The entry as written to disk, when it can be read.</summary>
    public sealed record InstanceEntry(string Name, int Pid, int Port, string Started, string Args);

    /// <summary>Reads an instance file, or null when it is missing or unreadable.</summary>
    public static InstanceEntry? Read(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            using var json = System.Text.Json.JsonDocument.Parse(reader.ReadToEnd());
            System.Text.Json.JsonElement root = json.RootElement;

            return new InstanceEntry(
                root.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty,
                root.TryGetProperty("pid", out var p) ? p.GetInt32() : 0,
                root.TryGetProperty("port", out var o) ? o.GetInt32() : 0,
                root.TryGetProperty("started", out var s) ? s.GetString() ?? string.Empty : string.Empty,
                root.TryGetProperty("args", out var a) ? a.GetString() ?? string.Empty : string.Empty);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>True when <paramref name="pid"/> is a running process.</summary>
    public static bool IsAlive(int pid)
    {
        if (pid <= 0)
        {
            return false;
        }

        if (pid == Environment.ProcessId)
        {
            return true;
        }

        try
        {
            using System.Diagnostics.Process process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>A name must be one path-safe component, so it cannot escape the directory.</summary>
    public static bool IsValidName(string name, out string? error)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "--name needs a value, for example --name transform-panel";
            return false;
        }

        if (name.Length > 64)
        {
            error = $"instance name '{name}' is too long (64 characters maximum)";
            return false;
        }

        foreach (char c in name)
        {
            bool ok = char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.';
            if (!ok)
            {
                error = $"instance name '{name}' may only contain letters, digits, '-', '_' and '.'";
                return false;
            }
        }

        error = null;
        return true;
    }

    private static void WriteEntry(Stream stream, string name, int port, string[] arguments, bool claimOnly)
    {
        var entry = new
        {
            name,
            pid = Environment.ProcessId,
            port,
            started = DateTime.UtcNow.ToString("o"),
            args = string.Join(' ', arguments),
            state = claimOnly ? "claiming" : "listening",
        };

        using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(System.Text.Json.JsonSerializer.Serialize(entry));
        writer.Flush();
    }
}

/// <summary>Options parsed from the desktop host's command line.</summary>
public sealed class DesktopStartupOptions
{
    /// <summary>Automation port (0 = ephemeral).</summary>
    public int Port { get; set; } = 5099;

    /// <summary>Whether <c>--port</c> appeared on the command line.</summary>
    public bool PortExplicit { get; set; }

    /// <summary>
    /// Named instance: bind an arbitrary port and publish it under
    /// <c>%APPDATA%\VCCad\instances\&lt;name&gt;.json</c>. Two live instances
    /// cannot share a name.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// A command-line value that cannot be honoured. The host reports it and exits
    /// non-zero rather than starting with something other than what was asked for.
    /// </summary>
    public string? ArgumentError { get; set; }

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

    /// <summary>
    /// Do not offer to recover a session left behind by a run that ended badly.
    ///
    /// The prompt is a modal covering the whole editing area and it swallows pointer events, so
    /// a driver's gestures hit the overlay and the canvas never takes focus. A headless run that
    /// does not dismiss it first does nothing at all, silently, which is a bad way to fail.
    /// </summary>
    public bool NoRecovery { get; set; }

    /// <summary>Where the diary is stored; defaults to the per-user application data.</summary>
    public string? HistoryDirectory { get; set; }

    /// <summary>The command-line arguments, recorded with the session header.</summary>
    public string[]? OriginalArguments { get; set; }

    /// <summary>Do not dock the window to the right half of the screen.</summary>
    public bool NoDock { get; set; }

    /// <summary>
    /// Development mode: an uncaught crash is filed as a GitHub issue against
    /// <c>darrenstarr/cavedraw</c>. Off unless the caller asks for it — a crash
    /// carries the user's paths, and a public issue is forever.
    /// </summary>
    public bool DevMode { get; set; }

    /// <summary>
    /// Deliberately throw an uncaught exception on the UI thread shortly after
    /// startup, to prove the crash reporter end to end. A diagnostic, never a
    /// default; nothing else in the program sets it.
    /// </summary>
    public bool CrashTest { get; set; }

    /// <summary>Print usage and exit.</summary>
    public bool ShowHelp { get; set; }

    /// <summary>Model/endpoint settings.</summary>
    public LlmOptions Llm { get; } = new();

    /// <summary>Parses the host's arguments.</summary>
    public static DesktopStartupOptions Parse(string[] args)
    {
        var options = new DesktopStartupOptions { OriginalArguments = args };

        // Environment first, command line second: a flag on this run overrides a
        // setting left in the environment.
        options.DevMode = IsTruthy(Environment.GetEnvironmentVariable("VCCAD_DEV"));
        options.CrashTest = IsTruthy(Environment.GetEnvironmentVariable("VCCAD_CRASH_TEST"));

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
                // --no-recover is the documented spelling; --no-recovery is kept because it
                // was in use for a day and a flag that stops working is worse than two names.
                case "--no-recover":
                case "--no-recovery":
                    options.NoRecovery = true;
                    break;

                case "--no-recording":
                case "--no-diary":
                    options.NoRecording = true;
                    break;
                case "--history-dir":
                    options.HistoryDirectory = Next();
                    break;
                case "--port":
                    string? portText = Next();
                    if (int.TryParse(portText, out int port) && port is >= 0 and <= 65535)
                    {
                        options.Port = port;
                        options.PortExplicit = true;
                    }
                    else
                    {
                        options.ArgumentError =
                            $"--port needs a number between 0 and 65535, got '{portText ?? "(nothing)"}'";
                    }

                    break;

                // The sanctioned ways to get an arbitrary port. --port-any is the
                // older spelling of --port 0 and is kept so a driver that learned it
                // does not start failing.
                case "--port-any":
                    options.Port = 0;
                    options.PortExplicit = true;
                    break;

                case "--name":
                    options.Name = Next();
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
                case "--dev":
                    options.DevMode = true;
                    break;
                case "--no-dev":
                    options.DevMode = false;
                    break;
                case "--crash-test":
                    options.CrashTest = true;
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

        // A name is about *which* instance, not which port: unless the caller also
        // pinned a port, bind whatever is free and publish it. Hunting for 5099 and
        // then moving would rebuild the race the name exists to remove.
        if (!string.IsNullOrWhiteSpace(options.Name))
        {
            if (!options.PortExplicit)
            {
                options.Port = 0;
            }

            if (!AutomationInstanceRegistry.IsValidName(options.Name, out string? nameError))
            {
                options.ArgumentError = nameError;
            }
        }

        return options;
    }

    /// <summary>True for the usual "yes" spellings of an environment switch.</summary>
    private static bool IsTruthy(string? value)
        => value is not null &&
           value.Trim() is { Length: > 0 } trimmed &&
           !trimmed.Equals("0", StringComparison.Ordinal) &&
           !trimmed.Equals("false", StringComparison.OrdinalIgnoreCase) &&
           !trimmed.Equals("no", StringComparison.OrdinalIgnoreCase);

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
          --dev               development mode: file an uncaught crash as a GitHub
                              issue against darrenstarr/cavedraw (off by default;
                              env VCCAD_DEV=1)
          --crash-test        deliberately throw an uncaught exception on the UI
                              thread after startup, to prove the crash reporter
                              (env VCCAD_CRASH_TEST=1)
          --name NAME         name this instance and publish its endpoint to
                              %APPDATA%\VCCad\instances\NAME.json
                              (binds an arbitrary free port unless --port pins one;
                               a second live instance with the same name is an error)
          --port N            automation endpoint port, default 5099
                              (N != 0 is a promise: if N cannot be bound the app
                               fails naming N rather than moving elsewhere)
          --port 0            bind an arbitrary free port and report it
          --port-any          synonym for --port 0
          --no-server         do not start the automation endpoint
          --no-screenshot     do not attach a workspace screenshot to --chat
          --model NAME        model name (default qwen3.8-27b)
          --llm-url URL       OpenAI-compatible base URL
          --api-key KEY       bearer token for the model endpoint
          --help              show this help

        Environment: VCCAD_LLM_MODEL, VCCAD_LLM_BASE, VCCAD_LLM_KEY,
                     VCCAD_HISTORY_DIR, VCCAD_INSTANCE_DIR,
                     VCCAD_DEV, VCCAD_CRASH_TEST, VCCAD_CRASH_DIR
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
