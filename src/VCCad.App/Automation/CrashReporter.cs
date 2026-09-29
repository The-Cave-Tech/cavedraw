using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Threading;
using VCCad.Core.Model;

namespace VCCad.App.Automation;

/// <summary>
/// Settings for <see cref="CrashReporter.Install"/>: whether crashes may be filed
/// as GitHub issues (development mode only), where the local crash files go, and
/// what the process was started with.
/// </summary>
public sealed class CrashReporterOptions
{
    /// <summary>
    /// Development mode: file a GitHub issue from a crash. Off by default, and it
    /// must stay off unless the person asked for it — a crash carries paths, and a
    /// public issue is forever.
    /// </summary>
    public bool DevMode { get; set; }

    /// <summary>
    /// Where crash files are written. Null means the usual place
    /// (<c>%APPDATA%\VCCad\crashes</c>, or <c>VCCAD_CRASH_DIR</c>).
    /// </summary>
    public string? DirectoryOverride { get; set; }

    /// <summary>The command line the process was started with, for the report.</summary>
    public string[]? Arguments { get; set; }

    /// <summary>The repository dev-mode crashes are filed against.</summary>
    public string Repository { get; set; } = CrashIssueFiler.DefaultRepository;
}

/// <summary>One frame of an exception's inner chain, as captured.</summary>
public sealed class CrashExceptionDetail
{
    /// <summary>The exception's full type name.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>The exception message, verbatim (local file only).</summary>
    public string? Message { get; set; }

    /// <summary>The exception's <c>Source</c>, when set.</summary>
    public string? Source { get; set; }

    /// <summary>The full stack trace, when one was captured.</summary>
    public string? StackTrace { get; set; }

    /// <summary>The HRESULT.</summary>
    public int HResult { get; set; }

    /// <summary>The exception's own <c>ToString()</c>, which includes the chain.</summary>
    public string? Detail { get; set; }

    /// <summary>Wrapped exceptions, outermost first.</summary>
    public List<CrashExceptionDetail> Inner { get; set; } = new();
}

/// <summary>One operation from the diagnostics log, as captured at crash time.</summary>
public sealed class CrashOperationDetail
{
    public long Sequence { get; set; }
    public string Timestamp { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public string? Parameters { get; set; }
    public bool Success { get; set; }
    public double DurationMs { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Everything a fixer needs about one uncaught exception, in a shape that
/// serialises straight to the crash file and renders as a GitHub issue body.
/// </summary>
public sealed class CrashReport
{
    /// <summary>Format version, so a reader can tell an old file from a new one.</summary>
    public int Schema { get; set; } = 1;

    /// <summary>Stable short id derived from the exception type and top stack frame.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Which hook saw it first: <c>ui-dispatcher</c>, <c>appdomain-unhandled</c>, <c>unobserved-task</c>.</summary>
    public string Channel { get; set; } = string.Empty;

    /// <summary>Every channel that saw this same exception, in order.</summary>
    public string[] Channels { get; set; } = Array.Empty<string>();

    /// <summary>Whether the runtime said it was terminating the process.</summary>
    public bool Terminating { get; set; }

    /// <summary>When the crash was captured.</summary>
    public DateTimeOffset TimestampUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Descriptive version of the application assembly.</summary>
    public string? AppVersion { get; set; }

    /// <summary>Informational version of the application assembly.</summary>
    public string? AppInformationalVersion { get; set; }

    /// <summary>.NET runtime version.</summary>
    public string? Runtime { get; set; }

    /// <summary>Operating system description.</summary>
    public string? OperatingSystem { get; set; }

    /// <summary>Process architecture.</summary>
    public string? Architecture { get; set; }

    /// <summary>Process id.</summary>
    public int ProcessId { get; set; }

    /// <summary>Whether dev mode (and therefore issue filing) was on.</summary>
    public bool DevMode { get; set; }

    /// <summary>The diary session id, when a diary is running.</summary>
    public string? SessionId { get; set; }

    /// <summary>The process image path (local file only).</summary>
    public string? ProcessPath { get; set; }

    /// <summary>The working directory (local file only).</summary>
    public string? CurrentDirectory { get; set; }

    /// <summary>The arguments the process was started with.</summary>
    public string[] Arguments { get; set; } = Array.Empty<string>();

    /// <summary>The exception and its inner chain.</summary>
    public CrashExceptionDetail? Exception { get; set; }

    /// <summary>The most recent operation that had completed when the crash happened.</summary>
    public string? LastOperation { get; set; }

    /// <summary>The tail of the diagnostics log, oldest first.</summary>
    public CrashOperationDetail[] RecentOperations { get; set; } = Array.Empty<CrashOperationDetail>();

    /// <summary>A summary of the live document, when one is open.</summary>
    public string? Document { get; set; }
}

/// <summary>
/// The application's crash recorder.
///
/// An unhandled exception on the UI thread used to take the process down with no
/// trace at all: the desktop host is a WinExe, so there is no console for it to
/// appear on, and nothing was written down. Two separate "the app died with no
/// output" reports were never diagnosable because of it.
///
/// This hooks every channel the runtime offers — <see cref="AppDomain.UnhandledException"/>,
/// <see cref="TaskScheduler.UnobservedTaskException"/> and Avalonia's dispatcher
/// exception path — captures the full detail a fixer needs, and writes it to disk
/// <em>synchronously, before the process dies</em>. In development mode it then
/// files a GitHub issue through <see cref="CrashIssueFiler"/>.
///
/// It records; it does not recover. Nothing here sets <c>Handled</c>, swallows an
/// exception, or changes what the application does next — the only difference on a
/// fatal error is that there is now evidence.
/// </summary>
public static class CrashReporter
{
    /// <summary>Folder name under the application data root.</summary>
    public const string FolderName = "crashes";

    private static readonly object Gate = new();

    // One report per exception object: the dispatcher path and the AppDomain path
    // both fire for the same UI-thread exception, and it must not be filed twice.
    private static readonly Dictionary<Exception, CrashReport> Seen = new(ReferenceEqualityComparer.Instance);

    private static bool _installed;
    private static bool _devMode;
    private static string? _directoryOverride;
    private static string _repository = CrashIssueFiler.DefaultRepository;
    private static InteractionLog? _diary;
    private static Func<string?>? _document;
    private static string[] _arguments = Array.Empty<string>();
    private static volatile string? _lastReportPath;
    private static volatile string? _lastIssue;

    /// <summary>Whether development mode (automatic issue filing) is on.</summary>
    public static bool DevMode
    {
        get { lock (Gate) { return _devMode; } }
    }

    /// <summary>Whether the crash hooks are installed.</summary>
    public static bool Installed
    {
        get { lock (Gate) { return _installed; } }
    }

    /// <summary>The repository dev-mode issues are filed against.</summary>
    public static string Repository
    {
        get { lock (Gate) { return _repository; } }
    }

    /// <summary>The path of the most recently written crash file, if any.</summary>
    public static string? LastReportPath => _lastReportPath;

    /// <summary>The URL/number of the most recently filed issue, if any.</summary>
    public static string? LastIssue => _lastIssue;

    /// <summary>Where crash files are written.</summary>
    public static string Directory
    {
        get
        {
            lock (Gate)
            {
                return _directoryOverride ?? DefaultDirectory();
            }
        }
    }

    /// <summary>
    /// The crash folder: <c>VCCAD_CRASH_DIR</c> when set, else
    /// <c>%APPDATA%\VCCad\crashes</c>, else a temp fallback for hosts with no
    /// application-data folder (the browser build).
    /// </summary>
    public static string DefaultDirectory()
    {
        string? custom = Environment.GetEnvironmentVariable("VCCAD_CRASH_DIR");
        if (!string.IsNullOrWhiteSpace(custom))
        {
            return custom;
        }

        string root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(root))
        {
            root = Path.Combine(Path.GetTempPath(), "vccad");
        }

        return Path.Combine(root, "VCCad", FolderName);
    }

    /// <summary>
    /// Installs the hooks, once per process. Safe to call from every host: the
    /// desktop entry point calls it before Avalonia starts (so an early crash is
    /// still recorded), and <see cref="App"/> calls it again for the browser host.
    /// </summary>
    public static void Install(CrashReporterOptions? options = null, bool force = false)
    {
        lock (Gate)
        {
            // Only a caller that actually supplied options may change them, so the
            // shell's "make sure the hooks are up" call cannot turn dev mode off
            // after the entry point asked for it.
            if (options is not null)
            {
                _devMode = options.DevMode;
                _repository = options.Repository;
                if (options.DirectoryOverride is not null)
                {
                    _directoryOverride = options.DirectoryOverride;
                }

                if (options.Arguments is { Length: > 0 })
                {
                    _arguments = options.Arguments;
                }
            }

            if (_installed && !force)
            {
                return;
            }

            _installed = true;
            AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandled;
            TaskScheduler.UnobservedTaskException += OnUnobservedTask;
        }

        try
        {
            // Avalonia's dispatcher path. An exception raised on the UI thread is
            // caught by the dispatcher loop and raised here; without a subscriber it
            // is invisible.
            Dispatcher.UIThread.UnhandledException += OnDispatcherUnhandled;
        }
        catch (Exception)
        {
            // No dispatcher on this host (or it is not available yet). The runtime
            // hooks above still cover a fatal crash.
        }
    }

    /// <summary>Installs the hooks without changing any setting. Used by the shell.</summary>
    public static void EnsureInstalled() => Install(options: null);

    /// <summary>Hands the reporter the diary, so a crash records its session id.</summary>
    public static void RegisterDiary(InteractionLog? diary)
    {
        lock (Gate)
        {
            _diary = diary;
        }
    }

    /// <summary>
    /// Hands the reporter a way to summarise the live document at crash time. The
    /// provider is called on the crashing thread and must be cheap and safe.
    /// </summary>
    public static void RegisterDocument(Func<string?>? summary)
    {
        lock (Gate)
        {
            _document = summary;
        }
    }

    /// <summary>Removes the hooks and clears all state. For tests only.</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            AppDomain.CurrentDomain.UnhandledException -= OnAppDomainUnhandled;
            TaskScheduler.UnobservedTaskException -= OnUnobservedTask;
            Seen.Clear();
            _installed = false;
            _devMode = false;
            _directoryOverride = null;
            _repository = CrashIssueFiler.DefaultRepository;
            _diary = null;
            _document = null;
            _arguments = Array.Empty<string>();
            _lastReportPath = null;
            _lastIssue = null;
        }

        try
        {
            Dispatcher.UIThread.UnhandledException -= OnDispatcherUnhandled;
        }
        catch (Exception)
        {
            // No dispatcher; nothing to unsubscribe.
        }
    }

    // ------------------------------------------------------------------
    // The runtime hooks
    // ------------------------------------------------------------------

    private static void OnAppDomainUnhandled(object? sender, UnhandledExceptionEventArgs e)
        => Handle(e.ExceptionObject as Exception ?? new Exception($"non-CLR exception: {e.ExceptionObject}"),
            "appdomain-unhandled", e.IsTerminating);

    private static void OnUnobservedTask(object? sender, UnobservedTaskExceptionEventArgs e)
        => Handle(e.Exception, "unobserved-task", terminating: false);

    private static void OnDispatcherUnhandled(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Handled is deliberately left exactly as Avalonia set it: recording a crash
        // must not change whether the app survives it.
        Handle(e.Exception, "ui-dispatcher", terminating: false);
    }

    /// <summary>
    /// Captures, writes and (in development mode) files one uncaught exception.
    /// Never throws: a crash reporter that crashes is worse than no crash reporter.
    /// </summary>
    public static void Handle(Exception exception, string channel, bool terminating)
    {
        if (exception is null)
        {
            return;
        }

        try
        {
            CrashReport report;
            bool first;
            lock (Gate)
            {
                if (Seen.TryGetValue(exception, out CrashReport? existing))
                {
                    first = false;
                    report = existing;
                    if (!report.Channels.Contains(channel, StringComparer.Ordinal))
                    {
                        report.Channels = report.Channels.Append(channel).ToArray();
                    }
                }
                else
                {
                    first = true;
                    report = Capture(exception, channel, terminating);
                    if (Seen.Count > 64)
                    {
                        Seen.Clear();
                    }

                    Seen[exception] = report;
                }
            }

            // The durable half. Everything below is best-effort.
            string? path = null;
            try
            {
                path = Write(report);
            }
            catch (Exception writeFailure)
            {
                path = WriteFallback(report, writeFailure);
            }

            if (path is not null)
            {
                _lastReportPath = path;
                Announce(report, path);
            }

            if (first && report.DevMode)
            {
                File(report, path);
            }
        }
        catch (Exception reporterFailure)
        {
            // The recorder itself failed. Say so on whatever channel exists, and
            // leave the exception alone — it still belongs to the runtime.
            EmergencyNote(reporterFailure);
        }
    }

    // ------------------------------------------------------------------
    // Capture
    // ------------------------------------------------------------------

    private static CrashReport Capture(Exception exception, string channel, bool terminating)
    {
        InteractionLog? diary;
        Func<string?>? document;
        string[] arguments;
        bool devMode;
        lock (Gate)
        {
            diary = _diary;
            document = _document;
            arguments = _arguments;
            devMode = _devMode;
        }

        Assembly app = typeof(CrashReporter).Assembly;
        AssemblyName name = app.GetName();

        var report = new CrashReport
        {
            Id = Identify(exception),
            Channel = channel,
            Channels = new[] { channel },
            Terminating = terminating,
            TimestampUtc = DateTimeOffset.UtcNow,
            AppVersion = name.Version?.ToString(),
            AppInformationalVersion = app.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            Runtime = RuntimeInformation.FrameworkDescription,
            OperatingSystem = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            ProcessId = Environment.ProcessId,
            DevMode = devMode,
            SessionId = Safe(() => diary?.SessionId),
            ProcessPath = Safe(() => Environment.ProcessPath),
            CurrentDirectory = Safe(() => Environment.CurrentDirectory),
            Arguments = arguments,
            Exception = Describe(exception),
            Document = document is null ? null : Safe(document),
        };

        try
        {
            IReadOnlyList<ApiCallRecord> recent = DiagnosticsLog.Recent(20);
            report.RecentOperations = recent.Select(r => new CrashOperationDetail
            {
                Sequence = r.Sequence,
                Timestamp = r.Timestamp.ToString("o"),
                Source = r.Source.ToString().ToLowerInvariant(),
                Operation = r.Operation,
                Parameters = r.Parameters,
                Success = r.Success,
                DurationMs = r.DurationMs,
                Error = r.Error,
            }).ToArray();

            if (recent.Count > 0)
            {
                report.LastOperation = recent[^1].Describe();
            }
        }
        catch (Exception)
        {
            // The diagnostics log is static state and may itself be unhappy. The
            // exception detail is the part that matters; keep going without it.
        }

        return report;
    }

    private static CrashExceptionDetail Describe(Exception exception)
    {
        var detail = new CrashExceptionDetail
        {
            Type = exception.GetType().FullName ?? exception.GetType().Name,
            Message = Safe(() => exception.Message),
            Source = Safe(() => exception.Source),
            StackTrace = Safe(() => exception.StackTrace),
            HResult = SafeInt(() => exception.HResult),
            Detail = Safe(() => exception.ToString()),
        };

        try
        {
            if (exception.InnerException is { } inner && inner != exception)
            {
                detail.Inner.Add(Describe(inner));
            }
        }
        catch (Exception)
        {
            // Some exceptions throw from their own properties. The outer one is enough.
        }

        return detail;
    }

    /// <summary>
    /// A stable short id for an exception: its type and its top stack frame. This is
    /// the dedup key — the same fault in the same place is one issue, not one per run.
    /// </summary>
    public static string Identify(Exception exception)
    {
        string type = Safe(() => exception.GetType().FullName) ?? exception.GetType().Name;
        string frame = TopFrame(Safe(() => exception.StackTrace));
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(type + "|" + frame));
        return Convert.ToHexString(hash)[..10].ToLowerInvariant();
    }

    private static string TopFrame(string? stackTrace)
    {
        if (string.IsNullOrEmpty(stackTrace))
        {
            return "no-stack";
        }

        foreach (string line in stackTrace.Split('\n'))
        {
            string trimmed = line.Trim();
            if (!trimmed.StartsWith("at ", StringComparison.Ordinal))
            {
                continue;
            }

            string frame = trimmed[3..];
            int inFile = frame.IndexOf(" in ", StringComparison.Ordinal);
            int paren = frame.IndexOf('(');
            int cut = Math.Min(
                inFile >= 0 ? inFile : frame.Length,
                paren >= 0 ? paren : frame.Length);
            return frame[..cut].Trim();
        }

        return "no-stack";
    }

    /// <summary>A one-line summary of the live document, safe to build at crash time.</summary>
    public static string SummariseDocument(CadDocument? document)
    {
        if (document is null)
        {
            return "(no document)";
        }

        int objects = 0;
        foreach (Artboard artboard in document.Artboards)
        {
            foreach (Layer layer in artboard.Layers)
            {
                Count(layer, ref objects);
            }
        }

        // The pasteboard is a layer like any other and can hold real work.
        Count(document.Orphans, ref objects);

        return $"name={document.Name}; artboards={document.Artboards.Count}; objects={objects}; " +
               $"sizes={string.Join(",", document.Artboards.Select(a => $"{a.Width:0.##}x{a.Height:0.##}"))}";
    }

    private static void Count(IItemContainer container, ref int total)
    {
        foreach (LayerItem item in container.Children)
        {
            total++;
            if (item is IItemContainer nested)
            {
                Count(nested, ref total);
            }
        }
    }

    // ------------------------------------------------------------------
    // The durable write
    // ------------------------------------------------------------------

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Writes the report and forces it to disk before returning. The process may
    /// die on the next instruction, so nothing here may be deferred to an exit
    /// handler or a background flush.
    /// </summary>
    public static string Write(CrashReport report)
    {
        string directory = Directory;
        System.IO.Directory.CreateDirectory(directory);

        string path = Path.Combine(directory, FileNameFor(report));
        string json = JsonSerializer.Serialize(report, Json);

        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            writer.Write(json);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        return path;
    }

    private static string FileNameFor(CrashReport report)
        => $"crash-{report.TimestampUtc:yyyyMMdd-HHmmss}-{report.Id}.json";

    /// <summary>
    /// The last-ditch copy, for when the crash folder cannot be written (a full
    /// disk, a read-only profile, a policy). If even this fails the failure is
    /// reported and the exception is still left to the runtime.
    /// </summary>
    private static string? WriteFallback(CrashReport report, Exception writeFailure)
    {
        string detail = $"{writeFailure.GetType().Name}: {writeFailure.Message}";

        try
        {
            string directory = Path.Combine(Path.GetTempPath(), "VCCad", FolderName);
            System.IO.Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, FileNameFor(report));

            string json = JsonSerializer.Serialize(
                new { writeFailure = detail, report },
                Json);

            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            AnnounceText($"crash file could not be written to {directory} ({detail}); " +
                         $"wrote a copy to {path}");
            return path;
        }
        catch (Exception fallbackFailure)
        {
            EmergencyNote(new AggregateException(
                new Exception(detail, writeFailure), fallbackFailure));
            return null;
        }
    }

    // ------------------------------------------------------------------
    // Filing (development mode only)
    // ------------------------------------------------------------------

    private static void File(CrashReport report, string? path)
    {
        try
        {
            CrashIssueOutcome outcome = CrashIssueFiler.File(report, path, Repository);
            if (outcome.Reference is not null)
            {
                _lastIssue = outcome.Reference;
            }

            AnnounceText(outcome.Message);
        }
        catch (Exception filingFailure)
        {
            // Filing is a convenience. The local file is the record, and it is
            // already on disk; a failed filing must not add a second crash.
            AnnounceText($"crash issue was not filed ({filingFailure.GetType().Name}: {filingFailure.Message}); " +
                         "the crash file was kept");
        }
    }

    // ------------------------------------------------------------------
    // Reporting out
    // ------------------------------------------------------------------

    private static void Announce(CrashReport report, string path)
    {
        // The issue, when there is one, is announced by the filer once it exists.
        AnnounceText(
            $"unhandled exception on '{report.Channel}' — {report.Exception?.Type}: {report.Exception?.Message}\n" +
            $"  crash file: {path}");
    }

    /// <summary>
    /// Best-effort console/trace reporting. The desktop host is a WinExe with no
    /// console, so this can go nowhere; it must never be the only record and it
    /// must never be allowed to throw.
    /// </summary>
    private static void AnnounceText(string message)
    {
        try
        {
            Console.Error.WriteLine($"[vccad] {message}");
            Console.Error.Flush();
        }
        catch (Exception)
        {
            // No console, or a closed pipe.
        }

        try
        {
            Trace.WriteLine($"[vccad] {message}");
        }
        catch (Exception)
        {
            // Trace listeners are optional.
        }
    }

    private static void EmergencyNote(Exception failure)
    {
        try
        {
            string text = $"[vccad] crash reporter failed: {failure.GetType().Name}: {failure.Message}";
            Console.Error.WriteLine(text);
            Console.Error.Flush();
            Trace.WriteLine(text);
        }
        catch (Exception)
        {
            // Nothing left to try on this thread.
        }
    }

    /// <summary>Runs a probe, returning null when it throws.</summary>
    private static T? Safe<T>(Func<T?> probe)
    {
        try
        {
            return probe();
        }
        catch (Exception)
        {
            return default;
        }
    }

    /// <summary>Runs a probe, returning 0 when it throws.</summary>
    private static int SafeInt(Func<int> probe)
    {
        try
        {
            return probe();
        }
        catch (Exception)
        {
            return 0;
        }
    }
}
