using System.Text.Json;
using VCCad.App.Automation;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// The rules that stop two editors claiming to be the same one.
///
/// A name is exclusive, a record whose process is gone is stale and reclaimable, and a **live** record is
/// respected - which is what makes it safe for a driver to launch an instance, read its file, and talk to
/// the port it names.
///
/// The failure these guard is subtle: if a stale record were treated as live, a launch would refuse for no
/// reason; if a live one were treated as stale, two editors would answer to one name and a driver would
/// reach whichever it happened to hit.
/// </summary>
public class InstanceRegistryTests
{
    private static string UniqueName() => "test-" + Guid.NewGuid().ToString("N")[..12];

    private static void Cleanup(string name)
    {
        try
        {
            File.Delete(AutomationInstanceRegistry.FileFor(name));
        }
        catch (IOException)
        {
            // Nothing to clean: the test is finished with it either way.
        }
    }

    /// <summary>
    /// Writes a record directly, which is how a stale or live record from another process is simulated.
    ///
    /// The directory is created first, and that is not ceremony: `Claim` creates it, but these tests write a
    /// file *before* claiming - and on a machine that has never run the editor the directory does not exist
    /// yet. It does exist on a development box, from all the instances that have run there, so the omission
    /// passed locally and failed on CI with a `DirectoryNotFoundException`.
    /// </summary>
    private static void WriteRecord(string name, int pid, int port, string state)
    {
        Directory.CreateDirectory(AutomationInstanceRegistry.Directory);
        File.WriteAllText(
            AutomationInstanceRegistry.FileFor(name),
            $"{{\"name\":\"{name}\",\"pid\":{pid},\"port\":{port}," +
            $"\"started\":\"2026-01-01T00:00:00Z\",\"args\":\"\",\"state\":\"{state}\"}}");
    }

    /// <summary>A claimed name is written immediately, marked as claiming rather than listening.</summary>
    [Fact]
    public void ClaimingWritesAClaimingRecord()
    {
        string name = UniqueName();
        try
        {
            Assert.Null(AutomationInstanceRegistry.Claim(name, new[] { "--name", name }));

            string json = File.ReadAllText(AutomationInstanceRegistry.FileFor(name));
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement entry = document.RootElement;

            Assert.Equal(name, entry.GetProperty("name").GetString());
            Assert.Equal(Environment.ProcessId, entry.GetProperty("pid").GetInt32());

            // Not `listening`: the endpoint does not exist yet, and a caller polling for a port must not be
            // told that it does.
            Assert.Equal("claiming", entry.GetProperty("state").GetString());
        }
        finally
        {
            Cleanup(name);
        }
    }

    /// <summary>
    /// A record held by a **live** process is respected: the claim is refused and names the holder, and the
    /// holder's record is left alone.
    /// </summary>
    [Fact]
    public void ALiveRecordIsRespected()
    {
        string name = UniqueName();
        try
        {
            // The process holding it is this one, so it is genuinely alive.
            WriteRecord(name, Environment.ProcessId, port: 5123, state: "listening");

            string? error = AutomationInstanceRegistry.Claim(name, Array.Empty<string>());

            Assert.NotNull(error);
            Assert.Contains(name, error);
            Assert.Contains(Environment.ProcessId.ToString(), error);

            // And the holder's record still says what it said.
            using JsonDocument document = JsonDocument.Parse(
                File.ReadAllText(AutomationInstanceRegistry.FileFor(name)));
            Assert.Equal("listening", document.RootElement.GetProperty("state").GetString());
        }
        finally
        {
            Cleanup(name);
        }
    }

    /// <summary>
    /// A record whose process is gone is **stale**: the claim succeeds, which is what makes a crashed
    /// instance recoverable without a person deleting a file by hand.
    /// </summary>
    [Fact]
    public void AStaleRecordIsReclaimed()
    {
        string name = UniqueName();
        try
        {
            // A pid that cannot be running: the maximum on this platform, offset so it is certainly unused.
            WriteRecord(name, int.MaxValue - 1, port: 5124, state: "listening");

            Assert.Null(AutomationInstanceRegistry.Claim(name, Array.Empty<string>()));

            using JsonDocument document = JsonDocument.Parse(
                File.ReadAllText(AutomationInstanceRegistry.FileFor(name)));
            Assert.Equal(Environment.ProcessId, document.RootElement.GetProperty("pid").GetInt32());
            Assert.Equal("claiming", document.RootElement.GetProperty("state").GetString());
        }
        finally
        {
            Cleanup(name);
        }
    }

    /// <summary>An invalid name is refused before anything is written.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("has/slash")]
    [InlineData("has\\backslash")]
    public void AnInvalidNameIsRefused(string name)
    {
        Assert.NotNull(AutomationInstanceRegistry.Claim(name, Array.Empty<string>()));
    }

    /// <summary>
    /// **No name can put its record outside the instance directory.** A name like `..` is odd but harmless
    /// here - the `.json` suffix makes it a file called `...json` inside the directory rather than a parent
    /// traversal - and this asserts the property that matters rather than guessing at a blocklist. My first
    /// version listed `..` as invalid, and it is not: it is contained, which is the thing to check.
    /// </summary>
    [Theory]
    [InlineData("..")]
    [InlineData("...")]
    [InlineData("a.b")]
    public void NoNameEscapesTheInstanceDirectory(string name)
    {
        string directory = Path.GetFullPath(AutomationInstanceRegistry.Directory);
        string file = Path.GetFullPath(AutomationInstanceRegistry.FileFor(name));

        Assert.StartsWith(directory + Path.DirectorySeparatorChar, file, StringComparison.Ordinal);
    }
}
