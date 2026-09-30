using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Avalonia.Threading;

namespace VCCad.App.Automation;

/// <summary>
/// Raised when the automation endpoint cannot bind the port it was asked for.
///
/// It carries the requested port so a launcher can name it, and whether the
/// cause was <see cref="SocketError.AddressAlreadyInUse"/> — the one case where
/// "somebody else has it" is a reason to consider an arbitrary port instead of a
/// reason to fail. Every other socket error is a transport problem that must be
/// surfaced, not papered over with an ephemeral bind.
/// </summary>
public sealed class AutomationPortUnavailableException : Exception
{
    /// <summary>Creates the exception from the underlying bind failure.</summary>
    public AutomationPortUnavailableException(int requestedPort, SocketException cause)
        : base(Describe(requestedPort, cause), cause)
    {
        RequestedPort = requestedPort;
        AddressInUse = cause.SocketErrorCode == SocketError.AddressAlreadyInUse;
    }

    /// <summary>The port that could not be bound.</summary>
    public int RequestedPort { get; }

    /// <summary>True when the port is held by another process.</summary>
    public bool AddressInUse { get; }

    private static string Describe(int port, SocketException cause)
        => cause.SocketErrorCode == SocketError.AddressAlreadyInUse
            ? $"automation port {port} is already in use"
            : $"automation port {port} could not be bound: {cause.SocketErrorCode} ({cause.Message})";
}

/// <summary>
/// The application's automation endpoint: a small JSON HTTP server bound to the
/// loopback interface.
///
/// It exists so the running editor — not a headless reimplementation of it — can
/// be driven by an external client (a script, a test harness, or me). Every
/// request is marshalled onto the Avalonia UI thread and executed through
/// <see cref="EditorOperations"/>, so an automation call is indistinguishable
/// from a person performing the same action, and both appear in the diagnostics
/// log.
///
/// A raw <see cref="TcpListener"/> is used deliberately: <c>HttpListener</c>
/// needs a URL ACL reservation on Windows, which would mean running the editor
/// elevated or pre-registering a prefix. This has no such requirement.
///
/// Routes
/// ------
///   GET  /api/v1/health                → liveness + document summary
///   GET  /api/v1/operations            → operation catalog (JSON)
///   GET  /api/v1/operations.txt        → operation catalog (text, for the model)
///   GET  /api/v1/diagnostics?since=N   → automation audit trail
///   GET  /api/v1/screenshot            → PNG of the workspace (base64)
///   POST /api/v1/invoke                → { "op": "...", "params": { ... } }
///   POST /api/v1/chat                  → { "prompt": "...", "withScreenshot": bool }
///
/// Every response is JSON: <c>{ "ok": true, "result": ... }</c> or
/// <c>{ "ok": false, "error": "..." }</c>.
/// </summary>
public sealed class AutomationServer : IDisposable
{
    private readonly AutomationContext _context;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stopping = new();
    private Thread? _acceptThread;

    private AutomationServer(AutomationContext context, TcpListener listener)
    {
        _context = context;
        _listener = listener;
    }

    /// <summary>The port actually bound (useful when 0 was requested).</summary>
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>
    /// The named instance this endpoint publishes itself under, if any. Set by
    /// <see cref="AutomationHost.Create"/>; a clean shutdown removes the discovery
    /// file so the name is free for the next run.
    /// </summary>
    public string? InstanceName { get; set; }

    /// <summary>
    /// Starts the server on <paramref name="port"/> (0 picks a free port).
    ///
    /// A bind failure surfaces as <see cref="AutomationPortUnavailableException"/>
    /// naming the requested port. It is never swallowed and retried on another
    /// port: a caller that asked for 5099 and silently got 58482 is the defect this
    /// class exists to make impossible.
    /// </summary>
    public static AutomationServer Start(AutomationContext context, int port = 5099)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        try
        {
            listener.Start();
        }
        catch (SocketException ex)
        {
            listener.Dispose();
            throw new AutomationPortUnavailableException(port, ex);
        }

        return Attach(context, listener);
    }

    /// <summary>
    /// Starts the server on a listener the launcher has already bound (see
    /// <see cref="AutomationHost.PrepareAutomationPort"/>). Reusing the caller's
    /// listener is what makes an explicit port a promise: the port is held from
    /// before the UI starts, so there is no window in which it can be taken.
    /// </summary>
    public static AutomationServer Start(AutomationContext context, TcpListener listener)
        => Attach(context, listener);

    private static AutomationServer Attach(AutomationContext context, TcpListener listener)
    {
        var server = new AutomationServer(context, listener);
        server._acceptThread = new Thread(server.AcceptLoop)
        {
            IsBackground = true,
            Name = "vccad-automation",
        };
        server._acceptThread.Start();
        DiagnosticsLog.Add(ApiCallSource.System, "server.start", $"{{\"port\":{server.Port}}}", null, true, 0);
        return server;
    }

    private void AcceptLoop()
    {
        while (!_stopping.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = _listener.AcceptTcpClient();
            }
            catch (SocketException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            _ = Task.Run(() => HandleClient(client));
        }
    }

    private void HandleClient(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                using NetworkStream stream = client.GetStream();
                stream.ReadTimeout = 120_000;
                stream.WriteTimeout = 120_000;

                Request? request = ReadRequest(stream);
                if (request is null)
                {
                    return;
                }

                (int status, string body) = Route(request);
                Write(stream, status, body);
            }
            catch (Exception ex)
            {
                try
                {
                    Write(client.GetStream(), 500, Error($"server error: {ex.Message}"));
                }
                catch (IOException)
                {
                    // The peer went away; nothing useful to do.
                }
            }
        }
    }

    private (int Status, string Body) Route(Request request)
    {
        string path = request.Path;
        try
        {
            switch (path)
            {
                case "/api/v1/health":
                    return (200, Json(new
                    {
                        ok = true,
                        app = "VCCad",

                        // Who is answering, not just that something is. A caller that launched an instance
                        // and then reached a *different* one had no way to tell - the reply looked right
                        // and came from older code. Port plus pid plus name is what makes "am I talking to
                        // the instance I started" answerable from the reply itself.
                        port = Port,
                        pid = Environment.ProcessId,
                        instance = InstanceName,
                        document = _context.Document.Name,
                        operations = EditorOperations.All.Count,
                    }));

                case "/api/v1/operations":
                    return (200, Json(new
                    {
                        ok = true,
                        result = EditorOperations.All.Select(o => new
                        {
                            op = o.Name,
                            summary = o.Summary,
                            parameters = o.Parameters,
                        }),
                    }));

                case "/api/v1/operations.txt":
                    return (200, Text(EditorOperations.Catalog()));

                case "/api/v1/diagnostics":
                    return (200, Json(new
                    {
                        ok = true,
                        result = OnUi(() => DiagnosticsLog
                            .Since(request.GetLong("since", 0))
                            .Select(Record)
                            .ToArray()),
                    }));

                case "/api/v1/screenshot":
                    return (200, Json(new { ok = true, result = OnUi(Screenshot) }));

                case "/api/v1/invoke":
                    return Invoke(request);

                case "/api/v1/chat":
                    return Chat(request);

                case "/api/v1/cancel":
                    _context.Cancel?.Invoke();
                    return (200, Json(new { ok = true, cancelled = true }));

                default:
                    return (404, Error($"unknown route '{path}'"));
            }
        }
        catch (OperationCanceledException)
        {
            return (409, Json(new { ok = false, cancelled = true, error = "operation cancelled" }));
        }
        catch (EditorOperationException ex)
        {
            return (400, Error(ex.Message));
        }
        catch (Exception ex)
        {
            return (500, Error(ex.Message));
        }
    }

    private (int, string) Invoke(Request request)
    {
        JsonElement root = Parse(request.Body);
        string op = root.TryGetProperty("op", out JsonElement opElement) && opElement.ValueKind == JsonValueKind.String
            ? opElement.GetString()!
            : throw new EditorOperationException("Body must contain 'op'.");

        JsonElement parameters = root.TryGetProperty("params", out JsonElement p)
            ? p
            : default;

        // Off the UI thread: synchronous handlers marshal themselves, and async ones
        // (a vision request) must never occupy the dispatcher.
        CancellationToken token = _context.Cancellation?.Invoke() ?? CancellationToken.None;
        object? result = Task.Run(
                () => EditorOperations.InvokeAsync(_context, op, parameters, ApiCallSource.Api, token), token)
            .GetAwaiter().GetResult();

        return (200, Json(new { ok = true, result }));
    }

    private (int, string) Chat(Request request)
    {
        JsonElement root = Parse(request.Body);
        if (!root.TryGetProperty("prompt", out JsonElement promptElement) || promptElement.ValueKind != JsonValueKind.String)
        {
            throw new EditorOperationException("Body must contain 'prompt'.");
        }

        if (_context.Chat is null)
        {
            throw new EditorOperationException("The assistant is not available in this build.");
        }

        bool withScreenshot = root.TryGetProperty("withScreenshot", out JsonElement shot) && shot.ValueKind == JsonValueKind.True;
        string prompt = promptElement.GetString()!;

        // The agent is asynchronous and marshals its own document access onto the
        // UI thread, so it must NOT be started on the UI thread (this handler runs
        // on a pool thread already). Blocking here is safe.
        object? result = Task.Run(() => _context.Chat!(prompt, withScreenshot)).GetAwaiter().GetResult();
        return (200, Json(new { ok = true, result }));
    }

    private object Screenshot()
        => _context.Screenshot is null
            ? new { available = false }
            : new { available = true, pngBase64 = Convert.ToBase64String(_context.Screenshot.Invoke() ?? Array.Empty<byte>()) };

    /// <summary>
    /// Runs <paramref name="work"/> on the UI thread. The HTTP handler runs on a
    /// thread-pool thread, so this is the boundary that keeps document access safe.
    /// </summary>
    private static T OnUi<T>(Func<T> work)
        => Dispatcher.UIThread.CheckAccess() ? work() : Dispatcher.UIThread.Invoke(work);

    private static object Record(ApiCallRecord r) => new
    {
        sequence = r.Sequence,
        time = r.Timestamp.ToString("HH:mm:ss.fff"),
        source = r.Source.ToString().ToLowerInvariant(),
        op = r.Operation,
        success = r.Success,
        durationMs = Math.Round(r.DurationMs, 2),
        parameters = r.Parameters,
        result = r.Result,
        error = r.Error,
    };

    // ------------------------------------------------------------------
    // Minimal HTTP/1.1
    // ------------------------------------------------------------------

    private sealed record Request(string Method, string Path, Dictionary<string, string> Query, string Body)
    {
        public long GetLong(string name, long fallback)
            => Query.TryGetValue(name, out string? value) && long.TryParse(value, out long parsed) ? parsed : fallback;
    }

    private static Request? ReadRequest(NetworkStream stream)
    {
        var buffer = new MemoryStream();
        byte[] chunk = new byte[8192];
        int headerEnd = -1;

        while (headerEnd < 0)
        {
            int read = stream.Read(chunk, 0, chunk.Length);
            if (read <= 0)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
            headerEnd = FindHeaderEnd(buffer.GetBuffer(), (int)buffer.Length);
        }

        byte[] all = buffer.GetBuffer();
        string head = Encoding.ASCII.GetString(all, 0, headerEnd);
        string[] lines = head.Split("\r\n");
        if (lines.Length == 0)
        {
            return null;
        }

        string[] parts = lines[0].Split(' ');
        if (parts.Length < 2)
        {
            return null;
        }

        string method = parts[0];
        string target = parts[1];
        string path = target;
        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int q = target.IndexOf('?');
        if (q >= 0)
        {
            path = target[..q];
            foreach (string pair in target[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] kv = pair.Split('=', 2);
                query[Uri.UnescapeDataString(kv[0])] = kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : string.Empty;
            }
        }

        int contentLength = 0;
        foreach (string line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon > 0 && line[..colon].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                int.TryParse(line[(colon + 1)..].Trim(), out contentLength);
            }
        }

        int bodyStart = headerEnd + 4;
        int have = (int)buffer.Length - bodyStart;
        while (have < contentLength)
        {
            int read = stream.Read(chunk, 0, Math.Min(chunk.Length, contentLength - have));
            if (read <= 0)
            {
                break;
            }

            buffer.Write(chunk, 0, read);
            have += read;
        }

        string body = contentLength > 0 && bodyStart + contentLength <= buffer.Length
            ? Encoding.UTF8.GetString(buffer.GetBuffer(), bodyStart, contentLength)
            : string.Empty;

        return new Request(method, path, query, body);
    }

    private static int FindHeaderEnd(byte[] data, int length)
    {
        for (int i = 0; i + 3 < length; i++)
        {
            if (data[i] == '\r' && data[i + 1] == '\n' && data[i + 2] == '\r' && data[i + 3] == '\n')
            {
                return i;
            }
        }

        return -1;
    }

    private static void Write(NetworkStream stream, int status, string body)
    {
        byte[] payload = Encoding.UTF8.GetBytes(body);
        string header =
            $"HTTP/1.1 {status} {(status == 200 ? "OK" : status == 404 ? "Not Found" : "Error")}\r\n" +
            "Content-Type: application/json; charset=utf-8\r\n" +
            $"Content-Length: {payload.Length}\r\n" +
            "Connection: close\r\n\r\n";
        byte[] head = Encoding.ASCII.GetBytes(header);
        stream.Write(head, 0, head.Length);
        stream.Write(payload, 0, payload.Length);
        stream.Flush();
    }

    private static string Json(object? value)
        => JsonSerializer.Serialize(value, JsonOptions);

    private static string Text(string value)
        => JsonSerializer.Serialize(value, JsonOptions);

    private static string Error(string message)
        => JsonSerializer.Serialize(new { ok = false, error = message }, JsonOptions);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static JsonElement Parse(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return default;
        }

        try
        {
            return JsonSerializer.Deserialize<JsonElement>(body);
        }
        catch (JsonException ex)
        {
            throw new EditorOperationException($"Request body is not valid JSON: {ex.Message}");
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _stopping.Cancel();
        try
        {
            _listener.Stop();
        }
        catch (SocketException)
        {
            // Already stopped.
        }

        _stopping.Dispose();

        // A named instance only holds its name while it is alive; a stale file is
        // also reclaimable by pid, but a clean exit must not make the next run wait.
        AutomationInstanceRegistry.Release(InstanceName);
    }
}
