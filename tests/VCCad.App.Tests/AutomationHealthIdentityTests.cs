using System.Net.Http;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using VCCad.App.Automation;
using VCCad.App.ViewModels;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// The endpoint says **who it is**, not merely that something answered.
///
/// This is the fix for a real failure of exactly that kind: a driver launched an instance, called its port,
/// and reached a **different** process still running older code. The reply was well-formed and plausible, so
/// nothing about it said "you are talking to the wrong editor" - which is the worst way for automation to
/// go wrong, because it looks like the feature does not work.
///
/// `port`, `pid` and `instance` in the health reply are what make the question answerable from the reply
/// itself. A caller that launched an instance can compare the pid it was handed with the pid that answered.
/// </summary>
public class AutomationHealthIdentityTests
{
    /// <summary>The pid and port in the reply are the ones that are actually serving.</summary>
    [AvaloniaFact]
    public async Task HealthNamesTheProcessThatAnswered()
    {
        var context = new AutomationContext { ViewModel = new EditorViewModel() };
        using AutomationServer server = AutomationServer.Start(context, 0);

        using var client = new HttpClient();
        string body = await client.GetStringAsync($"http://127.0.0.1:{server.Port}/api/v1/health");
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement health = document.RootElement;

        Assert.True(health.GetProperty("ok").GetBoolean());
        Assert.Equal(server.Port, health.GetProperty("port").GetInt32());
        Assert.Equal(Environment.ProcessId, health.GetProperty("pid").GetInt32());
    }

    /// <summary>A named instance reports the name, so a caller can tell two editors apart.</summary>
    [AvaloniaFact]
    public async Task HealthReportsTheInstanceName()
    {
        var context = new AutomationContext { ViewModel = new EditorViewModel() };
        using AutomationServer server = AutomationServer.Start(context, 0);
        server.InstanceName = "health-identity";

        using var client = new HttpClient();
        string body = await client.GetStringAsync($"http://127.0.0.1:{server.Port}/api/v1/health");
        using JsonDocument document = JsonDocument.Parse(body);

        Assert.Equal("health-identity", document.RootElement.GetProperty("instance").GetString());
    }
}
