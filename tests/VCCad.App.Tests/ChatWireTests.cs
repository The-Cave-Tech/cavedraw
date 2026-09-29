using System.Text.Json;
using VCCad.App.Ai;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// Pins what actually goes on the wire. The endpoint sees only this JSON, so a
/// restored conversation that is not in it, or a system prompt that is not first,
/// is the same as not having it at all.
/// </summary>
public class ChatWireTests
{
    private static readonly VllmClient Client = new(new LlmOptions { ApiKey = "test" });

    private static JsonElement Request(params LlmMessage[] messages)
        => JsonSerializer.Deserialize<JsonElement>(Client.BuildRequestBody(messages));

    [Fact]
    public void TheSystemPromptIsFirstAndTheConversationFollowsInOrder()
    {
        // A restored conversation is only "continuous" if it reaches the endpoint.
        JsonElement request = Request(
            new LlmMessage("system", "SYSTEM"),
            new LlmMessage("user", "first"),
            new LlmMessage("assistant", "second"),
            new LlmMessage("user", "third"));

        JsonElement[] messages = request.GetProperty("messages").EnumerateArray().ToArray();

        Assert.Equal(4, messages.Length);
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("SYSTEM", messages[0].GetProperty("content").GetString());
        Assert.Equal("first", messages[1].GetProperty("content").GetString());
        Assert.Equal("second", messages[2].GetProperty("content").GetString());
        Assert.Equal("third", messages[3].GetProperty("content").GetString());
    }

    [Fact]
    public void TheRequestBodyCarriesTheModelSettings()
    {
        JsonElement request = Request(new LlmMessage("user", "hi"));

        Assert.Equal(LlmOptions.DefaultModel, request.GetProperty("model").GetString());
        Assert.True(request.TryGetProperty("temperature", out _));
        Assert.True(request.TryGetProperty("max_tokens", out _));
    }

    [Fact]
    public void AToolResultKeepsTheIdOfTheCallItAnswers()
    {
        JsonElement request = Request(
            new LlmMessage("assistant", string.Empty,
                ToolCalls: new[] { new LlmToolCall("call_7", "vccad_operation", "{\"op\":\"object.list\"}") }),
            new LlmMessage("tool", "{\"ok\":true}", ToolCallId: "call_7"));

        JsonElement[] messages = request.GetProperty("messages").EnumerateArray().ToArray();

        JsonElement[] calls = messages[0].GetProperty("tool_calls").EnumerateArray().ToArray();
        Assert.Equal("vccad_operation", calls[0].GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("call_7", messages[1].GetProperty("tool_call_id").GetString());
        Assert.Equal("tool", messages[1].GetProperty("role").GetString());
    }

    [Fact]
    public void AScreenshotGoesAsAnImagePart()
    {
        JsonElement request = Request(new LlmMessage("user", "look", new byte[] { 1, 2, 3, 4 }));

        JsonElement content = request.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal(JsonValueKind.Array, content.ValueKind);
        Assert.Equal("image_url", content[1].GetProperty("type").GetString());
        Assert.StartsWith("data:image/png;base64,", content[1].GetProperty("image_url").GetProperty("url").GetString());
    }

    [Fact]
    public void TheCatalogIsOfferedAsAToolSoTheModelCanSeeEveryOperation()
    {
        string request = Client.BuildRequestBody(
            new[] { new LlmMessage("user", "hi") },
            new[] { new LlmTool("vccad_operation", "does things", "{\"type\":\"object\"}") });

        JsonElement root = JsonSerializer.Deserialize<JsonElement>(request);
        JsonElement tool = root.GetProperty("tools")[0];

        Assert.Equal("function", tool.GetProperty("type").GetString());
        Assert.Equal("vccad_operation", tool.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("auto", root.GetProperty("tool_choice").GetString());
    }
}
