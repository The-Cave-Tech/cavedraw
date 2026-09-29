using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using VCCad.App.Ai;
using VCCad.App.Automation;
using VCCad.App.ViewModels;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// Tests for issue #8: a second instance used to bind a random port in silence,
/// so a driver that launched it and called 127.0.0.1:5099 reached the other
/// process and could not tell.
///
/// Three promises are pinned here:
/// <list type="bullet">
///   <item>an explicit port is a promise — if it cannot be bound the start fails
///   naming it, and never quietly lands somewhere else;</item>
///   <item>a name is a promise — two live instances cannot share one, the app
///   publishes the port it actually bound to a discoverable file, and a file left
///   by a dead process does not block the name;</item>
///   <item>the port that was requested and the port that was bound are both
///   recorded, so "what did I actually get" has an answer that is not a guess.</item>
/// </list>
/// </summary>
public class AutomationPortTests : IDisposable
{
    private readonly string _instanceDir;

    public AutomationPortTests()
    {
        _instanceDir = Path.Combine(Path.GetTempPath(), "vccad-porttests-" + Guid.NewGuid().ToString("N")[..10]);
        Environment.SetEnvironmentVariable(AutomationInstanceRegistry.DirectoryVariable, _instanceDir);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        AutomationInstanceRegistry.Release(AutomationInstanceRegistry.Current);
        Environment.SetEnvironmentVariable(AutomationInstanceRegistry.DirectoryVariable, null);

        try
        {
            if (Directory.Exists(_instanceDir))
            {
                Directory.Delete(_instanceDir, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temp directory is not a test failure.
        }
    }

    // ------------------------------------------------------------------
    // 1. An explicit port is a promise
    // ------------------------------------------------------------------

    [Fact]
    public void AnExplicitPortThatCannotBeBoundFailsNamingItInsteadOfMoving()
    {
        int taken = OccupyAPort(out TcpListener occupied);
        try
        {
            AutomationPortUnavailableException error = Assert.Throws<AutomationPortUnavailableException>(
                () => AutomationHost.Create(
                    new EditorViewModel(),
                    () => null,
                    () => null,
                    new LlmOptions { ApiKey = "test" },
                    port: taken,
                    startServer: true));

            // The message has to be actionable, and it has to name the port that failed.
            Assert.Equal(taken, error.RequestedPort);
            Assert.True(error.AddressInUse, "a port held by a live listener is AddressAlreadyInUse");
            Assert.Contains(taken.ToString(), error.Message, StringComparison.Ordinal);

            // Throwing *is* the pin: the old code caught the SocketException and
            // started on an ephemeral port instead, so this call returned a live host
            // and the caller's next call to `taken` reached the *other* process.
        }
        finally
        {
            occupied.Stop();
        }
    }

    [Fact]
    public void AnArbitraryPortIsTheDeclaredOptIn()
    {
        // Both spellings of the opt-in resolve to "bind whatever is free".
        Assert.Equal(0, DesktopStartupOptions.Parse(new[] { "--port", "0" }).Port);
        Assert.Equal(0, DesktopStartupOptions.Parse(new[] { "--port-any" }).Port);

        AutomationHost host = AutomationHost.Create(
            new EditorViewModel(),
            () => null,
            () => null,
            new LlmOptions { ApiKey = "test" },
            port: 0,
            startServer: true);

        try
        {
            Assert.True(host.Port > 0, "an arbitrary port must still be a real one");
        }
        finally
        {
            host.Server?.Dispose();
        }
    }

    [Fact]
    public void ASocketErrorThatIsNotAddressInUseIsNotTreatedAsAReasonToMove()
    {
        // Only "somebody else has it" may be answered with another port. Every other
        // socket error is a transport problem and must surface.
        Assert.True(new AutomationPortUnavailableException(
            5099, new SocketException((int)SocketError.AddressAlreadyInUse)).AddressInUse);

        Assert.False(new AutomationPortUnavailableException(
            5099, new SocketException((int)SocketError.ConnectionRefused)).AddressInUse);
    }

    [Fact]
    public void TheRequestedPortIsReservedBeforeStartupAndFailsWhileALauncherIsWatching()
    {
        int taken = OccupyAPort(out TcpListener occupied);
        try
        {
            string? problem = AutomationHost.PrepareAutomationPort(
                new DesktopStartupOptions { Port = taken, OriginalArguments = new[] { "--port", taken.ToString() } });

            Assert.NotNull(problem);
            Assert.Contains(taken.ToString(), problem!, StringComparison.Ordinal);
            Assert.Contains("--port 0", problem!, StringComparison.Ordinal);
        }
        finally
        {
            occupied.Stop();
        }

        // A free explicit port is secured and then adopted, so it is held from
        // before the UI starts.
        int free = AFreshPort();
        string? none = AutomationHost.PrepareAutomationPort(
            new DesktopStartupOptions { Port = free, OriginalArguments = new[] { "--port", free.ToString() } });
        Assert.Null(none);

        AutomationHost host = AutomationHost.Create(
            new EditorViewModel(),
            () => null,
            () => null,
            new LlmOptions { ApiKey = "test" },
            port: free,
            startServer: true);

        try
        {
            Assert.Equal(free, host.Port);
        }
        finally
        {
            host.Server?.Dispose();
        }
    }

    // ------------------------------------------------------------------
    // 2. The truth is reported
    // ------------------------------------------------------------------

    [Fact]
    public void TheBoundAndRequestedPortAreBothRecordedAtStartup()
    {
        long before = DiagnosticsLog.Sequence;

        AutomationHost host = AutomationHost.Create(
            new EditorViewModel(),
            () => null,
            () => null,
            new LlmOptions { ApiKey = "test" },
            port: 0,
            startServer: true);

        try
        {
            // Other test classes can create a host concurrently, so find the record
            // for *this* host rather than assuming it is the only one.
            ApiCallRecord? ready = DiagnosticsLog
                .Since(before)
                .LastOrDefault(r => r.Operation == "host.ready"
                                    && (r.Parameters ?? string.Empty).Contains(
                                        $"\"port\":{host.Port}", StringComparison.Ordinal));

            Assert.NotNull(ready);
            Assert.True(ready!.Success);

            // "requested 0" is the honest record: the caller asked for any port.
            Assert.Contains("\"requested\":0", ready.Parameters, StringComparison.Ordinal);
        }
        finally
        {
            host.Server?.Dispose();
        }
    }

    // ------------------------------------------------------------------
    // 3. A name is a promise
    // ------------------------------------------------------------------

    [Fact]
    public void ANamedInstancePublishesThePortItActuallyBound()
    {
        // The name is passed explicitly, the way the launcher's claim does it; a
        // name means "an arbitrary port, and I will tell you which one".
        AutomationHost host = AutomationHost.Create(
            new EditorViewModel(),
            () => null,
            () => null,
            new LlmOptions { ApiKey = "test" },
            port: 0,
            startServer: true,
            instanceName: "transform-panel");

        try
        {
            Assert.True(host.Port > 0);
            Assert.Equal("transform-panel", host.Server!.InstanceName);

            string path = AutomationInstanceRegistry.FileFor("transform-panel");
            Assert.True(File.Exists(path), $"the discovery file {path} was not written");

            using JsonDocument json = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = json.RootElement;

            Assert.Equal("transform-panel", root.GetProperty("name").GetString());
            Assert.Equal(Environment.ProcessId, root.GetProperty("pid").GetInt32());
            Assert.Equal(host.Port, root.GetProperty("port").GetInt32());
            Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("started").GetString()));
            Assert.True(root.TryGetProperty("args", out _), "the file must record the command line");
        }
        finally
        {
            host.Server?.Dispose();
        }

        // A clean shutdown frees the name for the next run immediately.
        Assert.False(File.Exists(AutomationInstanceRegistry.FileFor("transform-panel")));
    }

    [Fact]
    public void AClaimedInstanceRecordsTheCommandLineInItsDiscoveryFile()
    {
        Assert.Null(AutomationInstanceRegistry.Claim(
            "with-args", new[] { "--name", "with-args", "--no-dock" }));

        AutomationInstanceRegistry.Publish("with-args", 1234);

        AutomationInstanceRegistry.InstanceEntry? entry =
            AutomationInstanceRegistry.Read(AutomationInstanceRegistry.FileFor("with-args"));

        Assert.NotNull(entry);
        Assert.Equal("with-args", entry!.Name);
        Assert.Equal(1234, entry.Port);
        Assert.Contains("--no-dock", entry.Args, StringComparison.Ordinal);

        AutomationInstanceRegistry.Release("with-args");
        Assert.False(File.Exists(AutomationInstanceRegistry.FileFor("with-args")));
    }

    [Fact]
    public void ASecondInstanceWithTheSameNameIsAnErrorNamingTheHolder()
    {
        Assert.Null(AutomationInstanceRegistry.Claim("transform-panel", Array.Empty<string>()));

        string? second = AutomationInstanceRegistry.Claim("transform-panel", Array.Empty<string>());

        Assert.NotNull(second);
        Assert.Contains("transform-panel", second!, StringComparison.Ordinal);
        Assert.Contains("already in use", second!, StringComparison.Ordinal);
        Assert.Contains(Environment.ProcessId.ToString(), second!, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileLeftByADeadProcessDoesNotBlockTheName()
    {
        Directory.CreateDirectory(_instanceDir);
        int dead = 2147483647; // no process has this id; the liveness check must say so
        Assert.False(AutomationInstanceRegistry.IsAlive(dead));

        File.WriteAllText(
            AutomationInstanceRegistry.FileFor("crashed"),
            $$"""{"name":"crashed","pid":{{dead}},"port":5000,"started":"2020-01-01T00:00:00Z","args":""}""");

        // A crash must not hold the name for ever: the stale file is taken over.
        Assert.Null(AutomationInstanceRegistry.Claim("crashed", Array.Empty<string>()));

        AutomationInstanceRegistry.InstanceEntry? entry =
            AutomationInstanceRegistry.Read(AutomationInstanceRegistry.FileFor("crashed"));
        Assert.NotNull(entry);
        Assert.Equal(Environment.ProcessId, entry!.Pid);
    }

    [Fact]
    public void ANameThatWouldEscapeTheInstanceDirectoryIsRejected()
    {
        DesktopStartupOptions options = DesktopStartupOptions.Parse(new[] { "--name", "../evil" });

        Assert.NotNull(options.ArgumentError);
        Assert.False(AutomationInstanceRegistry.IsValidName("../evil", out _));
        Assert.False(AutomationInstanceRegistry.IsValidName("has space", out _));
        Assert.True(AutomationInstanceRegistry.IsValidName("transform-panel", out _));
    }

    // ------------------------------------------------------------------
    // 4. The command line says what it does
    // ------------------------------------------------------------------

    [Fact]
    public void TheNameAndPortFlagsComposeWithoutFighting()
    {
        // A name on its own is "any port, published under my name".
        DesktopStartupOptions named = DesktopStartupOptions.Parse(new[] { "--name", "transform-panel" });
        Assert.Equal("transform-panel", named.Name);
        Assert.Equal(0, named.Port);

        // A name with an explicit port keeps the port promise too: the name does not
        // rescue a port that is already taken.
        DesktopStartupOptions pinned = DesktopStartupOptions.Parse(
            new[] { "--name", "transform-panel", "--port", "7000" });
        Assert.Equal(7000, pinned.Port);
        Assert.True(pinned.PortExplicit);

        // A value that cannot be honoured is reported, not quietly replaced.
        Assert.NotNull(DesktopStartupOptions.Parse(new[] { "--port", "not-a-number" }).ArgumentError);
        Assert.NotNull(DesktopStartupOptions.Parse(new[] { "--port", "70000" }).ArgumentError);

        // The documented opt-ins are the only way to an arbitrary port; without one
        // the default 5099 is still asked for, and promised.
        Assert.Equal(5099, DesktopStartupOptions.Parse(Array.Empty<string>()).Port);
        Assert.False(DesktopStartupOptions.Parse(Array.Empty<string>()).PortExplicit);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    /// <summary>Binds a port and hands it back, still held.</summary>
    private static int OccupyAPort(out TcpListener listener)
    {
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>A port number that was free a moment ago.</summary>
    private static int AFreshPort()
    {
        int port = OccupyAPort(out TcpListener listener);
        listener.Stop();
        return port;
    }
}
