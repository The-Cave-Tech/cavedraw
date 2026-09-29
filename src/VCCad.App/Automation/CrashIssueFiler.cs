using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VCCad.App.Automation;

/// <summary>The result of running one command.</summary>
/// <param name="ExitCode">Process exit code.</param>
/// <param name="StandardOutput">Captured stdout.</param>
/// <param name="StandardError">Captured stderr.</param>
public sealed record CrashCommandResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>What happened when a crash was offered to GitHub.</summary>
/// <param name="Attempted">Whether filing was attempted at all.</param>
/// <param name="Created">Whether a new issue was created.</param>
/// <param name="Duplicated">Whether an existing open issue matched the crash id.</param>
/// <param name="Number">The issue number, when one is known.</param>
/// <param name="Reference">A URL or <c>#N</c>, when one is known.</param>
/// <param name="Message">A one-line outcome for the log.</param>
public sealed record CrashIssueOutcome(
    bool Attempted,
    bool Created,
    bool Duplicated,
    int? Number,
    string? Reference,
    string Message);

/// <summary>
/// Files a crash as a GitHub issue, in development mode only.
///
/// Three rules shape it. The local crash file is the record and filing is a
/// convenience, so a failure here is reported and never allowed to become a second
/// crash. Anything filed is redacted first: paths become file names, the document
/// name is dropped, and API keys are removed. And a crash that already has an open
/// issue is commented on, not duplicated — otherwise one bad launch would produce an
/// issue per run.
///
/// On this machine <c>gh</c> lives and is authenticated inside WSL, so on Windows
/// the command is bridged through <c>wsl.exe</c>; a native <c>gh</c> on PATH is
/// preferred when there is one.
/// </summary>
public static class CrashIssueFiler
{
    /// <summary>The repository crash issues are filed against.</summary>
    public const string DefaultRepository = "darrenstarr/cavedraw";

    /// <summary>
    /// Every title starts with this, so the issues this creates can be found and
    /// closed in one search. Development-mode crash issues are test artefacts.
    /// </summary>
    public const string TitlePrefix = "[vccad-crash-test]";

    /// <summary>How long a gh invocation may take before it is abandoned.</summary>
    public static int TimeoutMs { get; set; } = 60_000;

    /// <summary>
    /// Runs one <c>gh</c> invocation. Replaced in tests so nothing touches the
    /// network, and in hosts that need a different bridge.
    /// </summary>
    public static CrashCommandRunner Runner { get; set; } = DefaultRunner;

    /// <summary>Runs <c>gh</c> with the given arguments; <paramref name="standardInput"/> becomes its stdin.</summary>
    public delegate CrashCommandResult CrashCommandRunner(IReadOnlyList<string> arguments, string? standardInput);

    /// <summary>
    /// Offers <paramref name="report"/> to the repository: comments on an existing
    /// open issue for the same crash id, otherwise creates one. Never throws.
    /// </summary>
    public static CrashIssueOutcome File(CrashReport report, string? localPath, string repository)
    {
        try
        {
            string title = BuildTitle(report);
            string body = BuildBody(report, localPath);

            int? duplicate = null;
            string? duplicateReference = null;
            string? dedupNote = null;

            try
            {
                CrashCommandResult list = Runner(new[]
                {
                    "issue", "list", "--repo", repository, "--state", "open",
                    "--limit", "100", "--json", "number,title,url",
                }, null);

                if (list.ExitCode == 0)
                {
                    FindDuplicate(list.StandardOutput, report.Id, out duplicate, out duplicateReference);
                }
                else
                {
                    dedupNote = $"duplicate check failed: {FirstLine(list.StandardError)}";
                }
            }
            catch (Exception dedupFailure)
            {
                // A check that cannot run must not stop the crash being filed; the
                // cost of a duplicate is lower than the cost of silence.
                dedupNote = $"duplicate check unavailable: {dedupFailure.GetType().Name}: {dedupFailure.Message}";
            }

            if (duplicate is int existing)
            {
                CrashCommandResult comment = Runner(new[]
                {
                    "issue", "comment", existing.ToString(CultureInfo.InvariantCulture),
                    "--repo", repository, "--body-file", "-",
                }, body);

                string duplicateRef = duplicateReference ?? $"#{existing}";
                return comment.ExitCode == 0
                    ? new CrashIssueOutcome(true, false, true, existing, duplicateRef,
                        $"crash {report.Id} already has open issue {duplicateRef}; added a comment")
                    : new CrashIssueOutcome(true, false, true, existing, duplicateRef,
                        $"crash {report.Id} matches open issue {duplicateRef}, but the comment failed: " +
                        FirstLine(comment.StandardError));
            }

            CrashCommandResult create = Runner(new[]
            {
                "issue", "create", "--repo", repository,
                "--title", title, "--body-file", "-",
            }, body);

            if (create.ExitCode != 0)
            {
                return new CrashIssueOutcome(true, false, false, null, null,
                    $"the issue was not filed ({FirstLine(create.StandardError)}); crash file kept");
            }

            string output = create.StandardOutput.Trim();
            string? url = output
                .Split('\n')
                .Select(line => line.Trim())
                .LastOrDefault(line => line.Contains("github.com", StringComparison.OrdinalIgnoreCase));
            int? number = ParseIssueNumber(url);
            string? reference = url ?? (number is int n ? $"#{n}" : null);

            return new CrashIssueOutcome(true, true, false, number, reference,
                $"filed crash {report.Id} as {reference ?? "an issue"}" +
                (dedupNote is null ? string.Empty : $" ({dedupNote})"));
        }
        catch (Exception failure)
        {
            // gh missing, no network, no auth, a rate limit: all the same to us. The
            // crash file stays and the caller carries on.
            return new CrashIssueOutcome(true, false, false, null, null,
                $"the issue was not filed ({failure.GetType().Name}: {failure.Message}); crash file kept");
        }
    }

    /// <summary>The issue title: exception, short message, and the crash id used for dedup.</summary>
    public static string BuildTitle(CrashReport report)
    {
        string type = report.Exception?.Type ?? "UnknownException";
        int lastDot = type.LastIndexOf('.');
        string shortType = lastDot >= 0 ? type[(lastDot + 1)..] : type;

        string message = OneLine(Redact(report.Exception?.Message));
        if (message.Length > 70)
        {
            message = message[..67] + "...";
        }

        return message.Length == 0
            ? $"{TitlePrefix} {shortType} (crash {report.Id})"
            : $"{TitlePrefix} {shortType}: {message} (crash {report.Id})";
    }

    /// <summary>The issue body: the captured detail, redacted for a public tracker.</summary>
    public static string BuildBody(CrashReport report, string? localPath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Automatic crash report (development mode)");
        sb.AppendLine();
        sb.AppendLine("Filed automatically by the VCCad crash reporter because this build was started");
        sb.AppendLine("in development mode. **Paths were reduced to their file names, the document name");
        sb.AppendLine("was removed, and API keys were stripped before filing.** The complete report, with");
        sb.AppendLine("paths, remains on the machine that crashed.");
        sb.AppendLine();
        sb.AppendLine("| field | value |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| crash id | `{report.Id}` |");
        sb.AppendLine($"| channel | `{report.Channel}` |");
        sb.AppendLine($"| channels | `{string.Join(", ", report.Channels)}` |");
        sb.AppendLine($"| terminating | {report.Terminating} |");
        sb.AppendLine($"| time (UTC) | {report.TimestampUtc:u} |");
        sb.AppendLine($"| app version | {report.AppVersion ?? "unknown"} ({report.AppInformationalVersion ?? "no informational version"}) |");
        sb.AppendLine($"| runtime | {report.Runtime ?? "unknown"} |");
        sb.AppendLine($"| OS | {report.OperatingSystem ?? "unknown"} |");
        sb.AppendLine($"| architecture | {report.Architecture ?? "unknown"} |");
        sb.AppendLine($"| session | `{report.SessionId ?? "unknown"}` |");
        sb.AppendLine($"| dev mode | {report.DevMode} |");
        sb.AppendLine($"| local crash file | `{Path.GetFileName(localPath ?? string.Empty)}` |");
        sb.AppendLine();

        sb.AppendLine("### Exception");
        sb.AppendLine();
        AppendException(sb, report.Exception, depth: 0);
        sb.AppendLine();

        sb.AppendLine("### Recent operations (oldest first)");
        sb.AppendLine();
        if (report.RecentOperations.Length == 0)
        {
            sb.AppendLine("_The diagnostics log was empty._");
        }
        else
        {
            sb.AppendLine("```");
            foreach (CrashOperationDetail operation in report.RecentOperations)
            {
                sb.AppendLine(
                    $"[{operation.Sequence}] {operation.Timestamp} {operation.Source} {operation.Operation} " +
                    $"{(operation.Success ? "ok" : "FAIL")} {operation.DurationMs:F1}ms" +
                    (operation.Success ? string.Empty : $": {OneLine(Redact(operation.Error))}"));
            }

            sb.AppendLine("```");
        }

        sb.AppendLine();
        sb.AppendLine("### Document");
        sb.AppendLine();
        sb.AppendLine("```");
        sb.AppendLine(RedactDocument(report.Document));
        sb.AppendLine("```");
        sb.AppendLine();

        sb.AppendLine("### Process");
        sb.AppendLine();
        sb.AppendLine("```");
        sb.AppendLine($"arguments: {string.Join(' ', report.Arguments.Select(RedactArgument))}");
        sb.AppendLine("```");

        return sb.ToString();
    }

    private static void AppendException(StringBuilder sb, CrashExceptionDetail? detail, int depth)
    {
        if (detail is null)
        {
            sb.AppendLine("_No exception detail was captured._");
            return;
        }

        string indent = new(' ', depth * 2);
        sb.AppendLine($"{indent}**{Redact(detail.Type)}**: {OneLine(Redact(detail.Message))}");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(detail.StackTrace))
        {
            sb.AppendLine($"{indent}```");
            sb.AppendLine(Redact(detail.StackTrace));
            sb.AppendLine($"{indent}```");
        }

        foreach (CrashExceptionDetail inner in detail.Inner)
        {
            sb.AppendLine();
            sb.AppendLine($"{indent}Inner exception:");
            sb.AppendLine();
            AppendException(sb, inner, depth + 1);
        }
    }

    // ------------------------------------------------------------------
    // Redaction
    // ------------------------------------------------------------------

    private static readonly Regex WindowsPath = new(
        @"(?<![\w])([A-Za-z]:\\[^\s""'<>|?*\r\n:]+)", RegexOptions.Compiled);

    private static readonly Regex UncPath = new(
        @"(?<![\w])(\\\\[^\s""'<>|?*\r\n:]+)", RegexOptions.Compiled);

    private static readonly Regex UnixPath = new(
        @"(?<![\w:/])/(?:[^/\s""'<>|?*\r\n]+/)*[^/\s""'<>|?*\r\n:]+", RegexOptions.Compiled);

    private static readonly Regex ApiKeyArgument = new(@"(?i)(--api-key)\s+\S+", RegexOptions.Compiled);
    private static readonly Regex BearerToken = new(@"(?i)(bearer)\s+\S+", RegexOptions.Compiled);

    /// <summary>
    /// Reduces absolute paths to their file name and removes credentials, so a
    /// report can be filed publicly. The local crash file keeps the original text.
    /// </summary>
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        string result = WindowsPath.Replace(text, match => OnlyName(match.Value));
        result = UncPath.Replace(result, match => OnlyName(match.Value));
        result = UnixPath.Replace(result, match => OnlyName(match.Value));
        result = ApiKeyArgument.Replace(result, "$1 <redacted>");
        result = BearerToken.Replace(result, "$1 <redacted>");
        return result;
    }

    /// <summary>Redacts one command-line argument, keeping the flag and dropping the value.</summary>
    public static string RedactArgument(string argument)
    {
        if (argument.StartsWith("--api-key", StringComparison.OrdinalIgnoreCase))
        {
            int equals = argument.IndexOf('=');
            return equals >= 0 ? argument[..equals] + "=<redacted>" : argument;
        }

        return Redact(argument);
    }

    /// <summary>Drops the document's name but keeps its shape, which is the useful part.</summary>
    public static string RedactDocument(string? summary)
        => summary is null ? "(no document)" : Regex.Replace(Redact(summary), @"\bname=[^;]*", "name=(redacted)");

    private static string OnlyName(string path)
    {
        string trimmed = path.TrimEnd('\\', '/');
        if (trimmed.Length == 0)
        {
            return path;
        }

        int slash = trimmed.LastIndexOfAny(new[] { '\\', '/' });
        string name = slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
        return name.Length == 0 ? "..." : name;
    }

    // ------------------------------------------------------------------
    // Duplicate detection
    // ------------------------------------------------------------------

    private static bool FindDuplicate(string json, string crashId, out int? number, out string? url)
    {
        number = null;
        url = null;

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (JsonElement issue in document.RootElement.EnumerateArray())
            {
                string title = issue.TryGetProperty("title", out JsonElement t) ? t.GetString() ?? string.Empty : string.Empty;
                if (!title.Contains(crashId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (issue.TryGetProperty("number", out JsonElement n) && n.TryGetInt32(out int value))
                {
                    number = value;
                }

                if (issue.TryGetProperty("url", out JsonElement u))
                {
                    url = u.GetString();
                }

                return number is not null;
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }

    private static int? ParseIssueNumber(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return null;
        }

        int slash = url.LastIndexOf('/');
        return slash >= 0 && int.TryParse(url[(slash + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)
            ? number
            : null;
    }

    private static string OneLine(string? text)
        => text is null ? string.Empty : string.Join(' ', text.Split('\r', '\n').Select(l => l.Trim()).Where(l => l.Length > 0));

    private static string FirstLine(string? text)
    {
        string line = OneLine(text);
        return line.Length > 300 ? line[..300] + "..." : line;
    }

    // ------------------------------------------------------------------
    // Running gh
    // ------------------------------------------------------------------

    /// <summary>
    /// Runs <c>gh</c> on this machine, or through WSL when this is Windows and
    /// there is no native copy. The WSL route writes the arguments to a file and
    /// runs a tiny script, because wsl.exe reassembles its command line and would
    /// otherwise mangle a quoted title.
    /// </summary>
    private static CrashCommandResult DefaultRunner(IReadOnlyList<string> arguments, string? standardInput)
    {
        try
        {
            return RunProcess("gh", arguments, standardInput, workingDirectory: null);
        }
        catch (Exception directFailure)
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new InvalidOperationException(
                    "the GitHub CLI (gh) is not available", directFailure);
            }

            try
            {
                return RunInWsl(arguments, standardInput);
            }
            catch (Exception wslFailure)
            {
                throw new InvalidOperationException(
                    "the GitHub CLI (gh) is not available on Windows and could not be reached through WSL",
                    new AggregateException(directFailure, wslFailure));
            }
        }
    }

    private static CrashCommandResult RunInWsl(IReadOnlyList<string> arguments, string? standardInput)
    {
        string directory = Path.Combine(Path.GetTempPath(), "vccad-gh-" + Guid.NewGuid().ToString("N")[..8]);
        System.IO.Directory.CreateDirectory(directory);

        try
        {
            // One argument per line, because an argument (the issue title) can
            // contain spaces, quotes and punctuation that no command-line round trip
            // through wsl.exe will preserve.
            System.IO.File.WriteAllText(Path.Combine(directory, "args.txt"), string.Join('\n', arguments) + "\n", Utf8NoBom);
            System.IO.File.WriteAllText(Path.Combine(directory, "run.sh"),
                "#!/usr/bin/env bash\n" +
                "cd \"$(dirname \"$0\")\"\n" +
                "mapfile -t a < args.txt\n" +
                "exec gh \"${a[@]}\"\n",
                Utf8NoBom);

            string script = WslPath(Path.Combine(directory, "run.sh"));
            return RunProcess("wsl", new[] { "-e", "bash", script }, standardInput, workingDirectory: null);
        }
        finally
        {
            try
            {
                System.IO.Directory.Delete(directory, recursive: true);
            }
            catch (Exception)
            {
                // A leftover temp folder is not worth a second failure.
            }
        }
    }

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// A Windows path in the form the WSL side sees it. Only drive-letter paths
    /// occur here (the temp folder), and anything else is refused rather than
    /// guessed at.
    /// </summary>
    private static string WslPath(string windowsPath)
    {
        string full = Path.GetFullPath(windowsPath);
        if (full.Length >= 2 && full[1] == ':')
        {
            return "/mnt/" + char.ToLowerInvariant(full[0]) + full[2..].Replace('\\', '/');
        }

        throw new InvalidOperationException($"cannot reach '{full}' from WSL");
    }

    private static CrashCommandResult RunProcess(
        string fileName, IReadOnlyList<string> arguments, string? standardInput, string? workingDirectory)
    {
        var start = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null,
        };

        if (workingDirectory is not null)
        {
            start.WorkingDirectory = workingDirectory;
        }

        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process? process = Process.Start(start)
            ?? throw new InvalidOperationException($"could not start {fileName}");

        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();

        if (standardInput is not null)
        {
            process.StandardInput.Write(standardInput);
            process.StandardInput.Close();
        }

        if (!process.WaitForExit(TimeoutMs))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception)
            {
                // It may have exited between the check and the kill.
            }

            throw new TimeoutException($"{fileName} did not finish within {TimeoutMs / 1000} seconds");
        }

        return new CrashCommandResult(
            process.ExitCode,
            stdout.GetAwaiter().GetResult(),
            stderr.GetAwaiter().GetResult());
    }
}
