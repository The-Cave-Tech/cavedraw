using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using VCCad.App.Automation;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// The crash recorder and the dev-mode issue filer.
///
/// These pin the parts that matter when the process is already dying: the report
/// is on disk before the handler returns, the recorded exception is never marked
/// handled, nothing is filed unless development mode was asked for, what is filed
/// is redacted, and no failure anywhere in the reporter is allowed to escape.
/// </summary>
public class CrashReporterTests
{
    // ------------------------------------------------------------------
    // Detect
    // ------------------------------------------------------------------

    [Fact]
    public void CapturesEverythingAFixerNeedsAndWritesItBeforeReturning()
    {
        string directory = NewDirectory();
        string history = NewDirectory();

        try
        {
            CrashReporter.Reset();
            CrashReporter.Install(new CrashReporterOptions
            {
                DirectoryOverride = directory,
                Arguments = new[] { "--chat", "draw a circle" },
            });

            var diary = new InteractionLog(history);
            diary.StartSession("test");
            CrashReporter.RegisterDiary(diary);
            CrashReporter.RegisterDocument(() => "name=secret-pattern.pdf; artboards=1; objects=3");

            DiagnosticsLog.Clear();
            DiagnosticsLog.Add(ApiCallSource.Api, "object.create", "{\"type\":\"ellipse\"}", "ok", true, 1.5);

            Exception boom = Thrown("the canvas exploded");

            CrashReporter.Handle(boom, "ui-dispatcher", terminating: false);

            string path = Assert.IsType<string>(CrashReporter.LastReportPath);
            Assert.True(File.Exists(path), $"expected a crash file at {path}");

            using JsonDocument report = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = report.RootElement;

            Assert.Equal("ui-dispatcher", root.GetProperty("Channel").GetString());
            Assert.False(root.GetProperty("Terminating").GetBoolean());
            Assert.False(root.GetProperty("DevMode").GetBoolean());
            Assert.Equal(diary.SessionId, root.GetProperty("SessionId").GetString());
            Assert.Equal("name=secret-pattern.pdf; artboards=1; objects=3", root.GetProperty("Document").GetString());
            Assert.Contains("--chat", root.GetProperty("Arguments").EnumerateArray().Select(a => a.GetString()));
            Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("Runtime").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("OperatingSystem").GetString()));

            JsonElement exception = root.GetProperty("Exception");
            Assert.Equal("System.InvalidOperationException", exception.GetProperty("Type").GetString());
            Assert.Equal("the canvas exploded", exception.GetProperty("Message").GetString());
            Assert.Contains("Thrown", exception.GetProperty("StackTrace").GetString());

            JsonElement operations = root.GetProperty("RecentOperations");
            Assert.Contains(operations.EnumerateArray(), o => o.GetProperty("Operation").GetString() == "object.create");
            Assert.Contains("object.create", root.GetProperty("LastOperation").GetString());
        }
        finally
        {
            CrashReporter.Reset();
            Cleanup(directory, history);
        }
    }

    [Fact]
    public void InnerExceptionsAreCaptured()
    {
        string directory = NewDirectory();
        try
        {
            CrashReporter.Reset();
            CrashReporter.Install(new CrashReporterOptions { DirectoryOverride = directory });

            var inner = new ArgumentNullException("document");
            Exception outer = Thrown("import failed", inner);

            CrashReporter.Handle(outer, "appdomain-unhandled", terminating: true);

            using JsonDocument report = JsonDocument.Parse(File.ReadAllText(CrashReporter.LastReportPath!));
            JsonElement innerJson = report.RootElement.GetProperty("Exception").GetProperty("Inner")[0];

            Assert.Equal("System.ArgumentNullException", innerJson.GetProperty("Type").GetString());
            Assert.True(report.RootElement.GetProperty("Terminating").GetBoolean());
        }
        finally
        {
            CrashReporter.Reset();
            Cleanup(directory);
        }
    }

    /// <summary>
    /// The UI dispatcher is the channel that used to die in silence. An exception
    /// posted to it is recorded, and — this is the point — it is still thrown out of
    /// the job loop: the reporter records and does not catch.
    /// </summary>
    [AvaloniaFact]
    public void UiDispatcherCrashIsRecordedAndStillPropagates()
    {
        string directory = NewDirectory();
        try
        {
            CrashReporter.Reset();
            CrashReporter.Install(new CrashReporterOptions { DirectoryOverride = directory });

            var marker = new InvalidOperationException("deliberate headless dispatcher crash");
            Dispatcher.UIThread.Post(() => throw marker, DispatcherPriority.Background);

            Assert.Throws<InvalidOperationException>(() => Dispatcher.UIThread.RunJobs());

            string[] files = Directory.GetFiles(directory);
            Assert.Single(files);

            using JsonDocument report = JsonDocument.Parse(File.ReadAllText(files[0]));
            Assert.Equal("ui-dispatcher", report.RootElement.GetProperty("Channel").GetString());
            Assert.Equal("deliberate headless dispatcher crash",
                report.RootElement.GetProperty("Exception").GetProperty("Message").GetString());
        }
        finally
        {
            CrashReporter.Reset();
            Cleanup(directory);
        }
    }

    /// <summary>
    /// The same exception reaches the dispatcher hook and then the AppDomain hook on
    /// the way to process death. It must be one crash file and one issue, not two.
    /// </summary>
    [Fact]
    public void TheSameExceptionOnTwoChannelsIsOneReportAndOneFiling()
    {
        string directory = NewDirectory();
        var runnerCalls = new List<IReadOnlyList<string>>();
        CrashIssueFiler.CrashCommandRunner original = CrashIssueFiler.Runner;

        try
        {
            CrashReporter.Reset();
            CrashReporter.Install(new CrashReporterOptions { DevMode = true, DirectoryOverride = directory });
            CrashIssueFiler.Runner = (arguments, _) =>
            {
                runnerCalls.Add(arguments);
                return arguments[1] == "list"
                    ? new CrashCommandResult(0, "[]", string.Empty)
                    : new CrashCommandResult(0, "https://github.com/darrenstarr/cavedraw/issues/999", string.Empty);
            };

            Exception boom = Thrown("one crash, two hooks");
            CrashReporter.Handle(boom, "ui-dispatcher", terminating: false);
            CrashReporter.Handle(boom, "appdomain-unhandled", terminating: true);

            Assert.Single(Directory.GetFiles(directory));
            Assert.Single(runnerCalls, a => a[1] == "create");

            using JsonDocument report = JsonDocument.Parse(File.ReadAllText(CrashReporter.LastReportPath!));
            string[] channels = report.RootElement.GetProperty("Channels")
                .EnumerateArray().Select(c => c.GetString()!).ToArray();
            Assert.Equal(new[] { "ui-dispatcher", "appdomain-unhandled" }, channels);
        }
        finally
        {
            CrashReporter.Reset();
            CrashIssueFiler.Runner = original;
            Cleanup(directory);
        }
    }

    // ------------------------------------------------------------------
    // File: development mode only
    // ------------------------------------------------------------------

    [Fact]
    public void DevModeOffFilesNothing()
    {
        string directory = NewDirectory();
        int calls = 0;
        CrashIssueFiler.CrashCommandRunner original = CrashIssueFiler.Runner;

        try
        {
            CrashReporter.Reset();
            CrashReporter.Install(new CrashReporterOptions { DevMode = false, DirectoryOverride = directory });
            CrashIssueFiler.Runner = (_, _) =>
            {
                calls++;
                throw new InvalidOperationException("gh must not be reached when dev mode is off");
            };

            CrashReporter.Handle(Thrown("quiet failure"), "appdomain-unhandled", terminating: true);

            Assert.Equal(0, calls);
            Assert.Null(CrashReporter.LastIssue);
            Assert.Single(Directory.GetFiles(directory));
        }
        finally
        {
            CrashReporter.Reset();
            CrashIssueFiler.Runner = original;
            Cleanup(directory);
        }
    }

    [Fact]
    public void DevModeOnFilesARedactedIssue()
    {
        string directory = NewDirectory();
        var calls = new List<(IReadOnlyList<string> Arguments, string? StandardInput)>();
        CrashIssueFiler.CrashCommandRunner original = CrashIssueFiler.Runner;

        try
        {
            CrashReporter.Reset();
            CrashReporter.Install(new CrashReporterOptions { DevMode = true, DirectoryOverride = directory });
            CrashReporter.RegisterDocument(() => "name=Acme Secret Contract.pdf; artboards=1; objects=2");
            CrashIssueFiler.Runner = (arguments, stdin) =>
            {
                calls.Add((arguments, stdin));
                return arguments[1] == "list"
                    ? new CrashCommandResult(0, "[]", string.Empty)
                    : new CrashCommandResult(0, "https://github.com/darrenstarr/cavedraw/issues/4242", string.Empty);
            };

            Exception boom = Thrown(
                @"cannot open C:\Users\someone\Documents\confidential\secret-plan.pdf --api-key sk-live-abcdef");
            CrashReporter.Handle(boom, "ui-dispatcher", terminating: false);

            (IReadOnlyList<string> arguments, string? standardInput) =
                calls.Single(c => c.Arguments[1] == "create");
            string title = arguments[arguments.ToList().IndexOf("--title") + 1];
            string body = Assert.IsType<string>(standardInput);

            Assert.StartsWith(CrashIssueFiler.TitlePrefix, title);
            Assert.DoesNotContain("sk-live-abcdef", title);
            Assert.DoesNotContain(@"C:\Users", body);
            Assert.DoesNotContain("confidential", body);
            Assert.DoesNotContain("Acme Secret Contract", body);
            Assert.DoesNotContain("sk-live-abcdef", body);
            Assert.Contains("secret-plan.pdf", body);
            Assert.Contains("name=(redacted)", body);
            Assert.Contains("Paths were reduced to their file names", body);
            Assert.Contains("issues/4242", CrashReporter.LastIssue);
        }
        finally
        {
            CrashReporter.Reset();
            CrashIssueFiler.Runner = original;
            Cleanup(directory);
        }
    }

    [Fact]
    public void AnOpenIssueForTheSameCrashIsCommentedOnNotDuplicated()
    {
        string directory = NewDirectory();
        var calls = new List<IReadOnlyList<string>>();
        CrashIssueFiler.CrashCommandRunner original = CrashIssueFiler.Runner;

        try
        {
            CrashReporter.Reset();
            CrashReporter.Install(new CrashReporterOptions { DevMode = true, DirectoryOverride = directory });

            Exception boom = Thrown("repeat offender");
            string crashId = CrashReporter.Identify(boom);

            CrashIssueFiler.Runner = (arguments, _) =>
            {
                calls.Add(arguments);
                return arguments[1] == "list"
                    ? new CrashCommandResult(0,
                        $"[{{\"number\":77,\"title\":\"[vccad-crash-test] repeat ({crashId})\"," +
                        "\"url\":\"https://github.com/darrenstarr/cavedraw/issues/77\"}]", string.Empty)
                    : new CrashCommandResult(0, string.Empty, string.Empty);
            };

            CrashReporter.Handle(boom, "appdomain-unhandled", terminating: true);

            Assert.DoesNotContain(calls, a => a[1] == "create");
            IReadOnlyList<string> comment = calls.Single(a => a[1] == "comment");
            Assert.Equal("77", comment[2]);
            Assert.Contains("issues/77", CrashReporter.LastIssue);
        }
        finally
        {
            CrashReporter.Reset();
            CrashIssueFiler.Runner = original;
            Cleanup(directory);
        }
    }

    [Fact]
    public void AFailingFilingKeepsTheLocalReport()
    {
        string directory = NewDirectory();
        CrashIssueFiler.CrashCommandRunner original = CrashIssueFiler.Runner;

        try
        {
            CrashReporter.Reset();
            CrashReporter.Install(new CrashReporterOptions { DevMode = true, DirectoryOverride = directory });
            CrashIssueFiler.Runner = (_, _) => throw new InvalidOperationException("gh: not found");

            CrashReporter.Handle(Thrown("offline crash"), "appdomain-unhandled", terminating: true);

            Assert.NotNull(CrashReporter.LastReportPath);
            Assert.True(File.Exists(CrashReporter.LastReportPath));
            Assert.Null(CrashReporter.LastIssue);
        }
        finally
        {
            CrashReporter.Reset();
            CrashIssueFiler.Runner = original;
            Cleanup(directory);
        }
    }

    // ------------------------------------------------------------------
    // The reporter must not become a crash
    // ------------------------------------------------------------------

    [Fact]
    public void WhenTheCrashFileCannotBeWrittenACopyIsStillProduced()
    {
        // A file where the crash folder should be: CreateDirectory must fail.
        string blocker = Path.Combine(Path.GetTempPath(), "vccad-crash-blocker-" + Guid.NewGuid().ToString("N")[..8]);
        File.WriteAllText(blocker, "not a directory");
        string primary = Path.Combine(blocker, "crashes");

        try
        {
            CrashReporter.Reset();
            CrashReporter.Install(new CrashReporterOptions { DirectoryOverride = primary });

            CrashReporter.Handle(Thrown("writer went wrong"), "appdomain-unhandled", terminating: true);

            string path = Assert.IsType<string>(CrashReporter.LastReportPath);
            Assert.True(File.Exists(path));
            Assert.False(Directory.Exists(primary));
            Assert.Contains("writeFailure", File.ReadAllText(path));
        }
        finally
        {
            CrashReporter.Reset();
            File.Delete(blocker);
        }
    }

    // ------------------------------------------------------------------
    // The pieces on their own
    // ------------------------------------------------------------------

    [Fact]
    public void RedactionKeepsFileNamesAndRemovesSecrets()
    {
        string redacted = CrashIssueFiler.Redact(
            @"failed at C:\Users\someone\Documents\confidential\plan.pdf and /home/someone/secret/fonts/x.ttf " +
            "with --api-key sk-live-abcdef and Bearer tok_123456");

        Assert.DoesNotContain("someone", redacted);
        Assert.DoesNotContain("confidential", redacted);
        Assert.DoesNotContain("sk-live-abcdef", redacted);
        Assert.DoesNotContain("tok_123456", redacted);
        Assert.Contains("plan.pdf", redacted);
        Assert.Contains("x.ttf", redacted);
        Assert.Contains("--api-key <redacted>", redacted);
        Assert.Contains("Bearer <redacted>", redacted);
    }

    [Fact]
    public void TheSameFaultHasTheSameId()
    {
        Exception first = Thrown("same");
        Exception second = Thrown("same");

        Assert.Equal(CrashReporter.Identify(first), CrashReporter.Identify(second));
        Assert.Equal(10, CrashReporter.Identify(first).Length);
    }

    [Fact]
    public void DocumentSummaryCountsObjects()
    {
        CadDocument document = CadDocument.CreateDefault("private-pattern.pdf");
        Artboard artboard = document.Artboards[0];
        Layer layer = artboard.Layers[0];
        layer.AddItem(PathFactory.CreatePolyline("line", new[] { new Point2D(0, 0), new Point2D(10, 10) }));

        string summary = CrashReporter.SummariseDocument(document);

        Assert.Contains("name=private-pattern.pdf", summary);
        Assert.Contains("artboards=1", summary);
        Assert.Contains("objects=1", summary);
    }

    // ------------------------------------------------------------------

    private static Exception Thrown(string message, Exception? inner = null)
    {
        try
        {
            throw inner is null
                ? new InvalidOperationException(message)
                : new InvalidOperationException(message, inner);
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static string NewDirectory()
    {
        string directory = Path.Combine(
            Path.GetTempPath(), "vccad-crash-tests", Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void Cleanup(params string[] directories)
    {
        foreach (string directory in directories)
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (Exception)
            {
                // A leftover temp folder is not worth failing a test over.
            }
        }
    }
}
