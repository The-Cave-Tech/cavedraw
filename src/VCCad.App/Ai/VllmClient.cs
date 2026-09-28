using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using VCCad.App.Automation;

namespace VCCad.App.Ai;

/// <summary>Connection settings for the language/vision model endpoint.</summary>
public sealed class LlmOptions
{
    /// <summary>Default vLLM endpoint used by this build.</summary>
    public const string DefaultBaseUrl = "https://your-endpoint.example/v1";

    /// <summary>Default model served by the endpoint.</summary>
    public const string DefaultModel = "qwen3.8-27b";

    /// <summary>Default API key for the endpoint.</summary>
    public const string DefaultApiKey = "<your-api-key>";

    /// <summary>OpenAI-compatible base URL, without a trailing slash.</summary>
    public string BaseUrl { get; set; } = Environment.GetEnvironmentVariable("VCCAD_LLM_BASE") ?? DefaultBaseUrl;

    /// <summary>Model name.</summary>
    public string Model { get; set; } = Environment.GetEnvironmentVariable("VCCAD_LLM_MODEL") ?? DefaultModel;

    /// <summary>Bearer token.</summary>
    public string ApiKey { get; set; } = Environment.GetEnvironmentVariable("VCCAD_LLM_KEY") ?? DefaultApiKey;

    /// <summary>Sampling temperature; low because the model emits tool calls.</summary>
    public double Temperature { get; set; } = 0.1;

    /// <summary>Cap on generated tokens per turn.</summary>
    public int MaxTokens { get; set; } = 2000;

    /// <summary>Per-request timeout.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(3);
}

/// <summary>A tool (function) offered to the model.</summary>
/// <param name="Name">Function name.</param>
/// <param name="Description">What it does; carries the operation catalog.</param>
/// <param name="ParametersJson">JSON Schema for the arguments object.</param>
public sealed record LlmTool(string Name, string Description, string ParametersJson);

/// <summary>A tool call the model asked for.</summary>
/// <param name="Id">Provider call id, echoed back with the result.</param>
/// <param name="Name">Function name.</param>
/// <param name="Arguments">Raw JSON arguments string.</param>
public sealed record LlmToolCall(string Id, string Name, string Arguments);

/// <summary>One message in a chat conversation.</summary>
/// <param name="Role">system | user | assistant | tool.</param>
/// <param name="Text">Text content.</param>
/// <param name="ImagePng">Optional PNG attached for a vision-capable model.</param>
/// <param name="ToolCalls">Tool calls for an assistant message.</param>
/// <param name="ToolCallId">Call id this message answers, for role=tool.</param>
public sealed record LlmMessage(
    string Role,
    string Text,
    byte[]? ImagePng = null,
    IReadOnlyList<LlmToolCall>? ToolCalls = null,
    string? ToolCallId = null);

/// <summary>The model's reply for one turn.</summary>
/// <param name="Text">Assistant text (empty when it only asked for tools).</param>
/// <param name="ToolCalls">Tool calls requested this turn, if any.</param>
/// <param name="FinishReason">Provider finish reason, for diagnostics.</param>
/// <param name="PromptTokens">Prompt token count when reported.</param>
/// <param name="CompletionTokens">Completion token count when reported.</param>
public sealed record LlmReply(
    string Text,
    IReadOnlyList<LlmToolCall> ToolCalls,
    string? FinishReason,
    int? PromptTokens,
    int? CompletionTokens);

/// <summary>
/// A thin OpenAI-compatible chat client for the vLLM endpoint, including native
/// tool calling and image input.
///
/// Every call is recorded in the diagnostics log (endpoint, model, message and
/// image counts, tool calls — never the key), which is what the "API calls" tab
/// in the diagnostics window shows alongside the editor's own operations.
/// </summary>
public sealed class VllmClient
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    private readonly LlmOptions _options;

    public VllmClient(LlmOptions options) => _options = options;

    /// <summary>Current model name.</summary>
    public string Model => _options.Model;

    /// <summary>Current endpoint.</summary>
    public string BaseUrl => _options.BaseUrl;

    /// <summary>Sends a conversation and returns the assistant's reply.</summary>
    public async Task<LlmReply> CompleteAsync(
        IReadOnlyList<LlmMessage> messages,
        IReadOnlyList<LlmTool>? tools = null,
        CancellationToken cancellationToken = default)
    {
        string url = _options.BaseUrl.TrimEnd('/') + "/chat/completions";
        var payload = new Dictionary<string, object?>
        {
            ["model"] = _options.Model,
            ["temperature"] = _options.Temperature,
            ["max_tokens"] = _options.MaxTokens,
            ["messages"] = messages.Select(ToWire).ToArray(),
        };

        if (tools is { Count: > 0 })
        {
            payload["tools"] = tools.Select(t => new
            {
                type = "function",
                function = new
                {
                    name = t.Name,
                    description = t.Description,
                    parameters = JsonSerializer.Deserialize<JsonElement>(t.ParametersJson),
                },
            }).ToArray();
            payload["tool_choice"] = "auto";
        }

        string json = JsonSerializer.Serialize(payload);
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        long start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_options.Timeout);
            using HttpResponseMessage response = await Http.SendAsync(request, cts.Token).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            double ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - start;

            if (!response.IsSuccessStatusCode)
            {
                string message = $"{(int)response.StatusCode} {response.ReasonPhrase}: {Truncate(body, 300)}";
                DiagnosticsLog.Add(ApiCallSource.Llm, "llm.chat", DescribeRequest(messages, url, tools),
                    null, success: false, durationMs: ms, error: message);
                throw new InvalidOperationException($"Model endpoint error: {message}");
            }

            LlmReply reply = ParseReply(body);
            DiagnosticsLog.Add(ApiCallSource.Llm, "llm.chat", DescribeRequest(messages, url, tools),
                new
                {
                    reply = Truncate(reply.Text, 400),
                    toolCalls = reply.ToolCalls.Select(c => $"{c.Name} {Truncate(c.Arguments, 200)}").ToArray(),
                    reply.FinishReason,
                    reply.PromptTokens,
                    reply.CompletionTokens,
                },
                success: true, durationMs: ms);
            return reply;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The person pressed Cancel (or the caller aborted). This is not an
            // endpoint failure and must not be reported as one: it propagates as
            // cancellation so the UI can say "cancelled" and re-enable the editor.
            DiagnosticsLog.Add(ApiCallSource.Llm, "llm.chat", DescribeRequest(messages, url, tools),
                null, success: false,
                durationMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - start, error: "cancelled");
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            double ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - start;
            DiagnosticsLog.Add(ApiCallSource.Llm, "llm.chat", DescribeRequest(messages, url, tools),
                null, success: false, durationMs: ms, error: ex.Message);
            throw new InvalidOperationException($"Could not reach the model endpoint at {url}: {ex.Message}", ex);
        }
    }

    /// <summary>Lists the models the endpoint serves (diagnostics helper).</summary>
    public async Task<string> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        string url = _options.BaseUrl.TrimEnd('/') + "/models";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        long start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        try
        {
            using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            DiagnosticsLog.Add(ApiCallSource.Llm, "llm.models", url, Truncate(body, 800),
                response.IsSuccessStatusCode,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - start,
                response.IsSuccessStatusCode ? null : response.ReasonPhrase);
            return body;
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Add(ApiCallSource.Llm, "llm.models", url, null, false,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - start, ex.Message);
            throw;
        }
    }

    private static object ToWire(LlmMessage message)
    {
        // A tool result: role=tool with the id it answers.
        if (message.ToolCallId is not null)
        {
            return new { role = "tool", tool_call_id = message.ToolCallId, content = message.Text };
        }

        // An assistant turn that requested tools.
        if (message.ToolCalls is { Count: > 0 })
        {
            return new
            {
                role = "assistant",
                content = string.IsNullOrEmpty(message.Text) ? null : message.Text,
                tool_calls = message.ToolCalls.Select(c => new
                {
                    id = c.Id,
                    type = "function",
                    function = new { name = c.Name, arguments = c.Arguments },
                }).ToArray(),
            };
        }

        if (message.ImagePng is not { Length: > 0 })
        {
            return new { role = message.Role, content = message.Text };
        }

        return new
        {
            role = message.Role,
            content = new object[]
            {
                new { type = "text", text = message.Text },
                new
                {
                    type = "image_url",
                    image_url = new { url = "data:image/png;base64," + Convert.ToBase64String(message.ImagePng) },
                },
            },
        };
    }

    private static LlmReply ParseReply(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("choices", out JsonElement choices) || choices.GetArrayLength() == 0)
            {
                return new LlmReply(body, Array.Empty<LlmToolCall>(), null, null, null);
            }

            JsonElement choice = choices[0];
            string text = string.Empty;
            var calls = new List<LlmToolCall>();

            if (choice.TryGetProperty("message", out JsonElement message))
            {
                if (message.TryGetProperty("content", out JsonElement content) &&
                    content.ValueKind == JsonValueKind.String)
                {
                    text = content.GetString() ?? string.Empty;
                }

                if (message.TryGetProperty("tool_calls", out JsonElement toolCalls) &&
                    toolCalls.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement call in toolCalls.EnumerateArray())
                    {
                        string id = call.TryGetProperty("id", out JsonElement idElement) &&
                                    idElement.ValueKind == JsonValueKind.String
                            ? idElement.GetString() ?? $"call_{calls.Count}"
                            : $"call_{calls.Count}";
                        string name = string.Empty;
                        string arguments = "{}";
                        if (call.TryGetProperty("function", out JsonElement function))
                        {
                            if (function.TryGetProperty("name", out JsonElement n) && n.ValueKind == JsonValueKind.String)
                            {
                                name = n.GetString() ?? string.Empty;
                            }

                            if (function.TryGetProperty("arguments", out JsonElement a))
                            {
                                arguments = a.ValueKind == JsonValueKind.String ? a.GetString() ?? "{}" : a.GetRawText();
                            }
                        }

                        if (name.Length > 0)
                        {
                            calls.Add(new LlmToolCall(id, name, arguments));
                        }
                    }
                }
            }

            // Some deployments still return tool calls in the finish_reason while
            // leaving content empty; treat "null" as empty text.
            if (string.Equals(text.Trim(), "null", StringComparison.OrdinalIgnoreCase))
            {
                text = string.Empty;
            }

            string? finish = choice.TryGetProperty("finish_reason", out JsonElement f) && f.ValueKind == JsonValueKind.String
                ? f.GetString()
                : null;

            int? prompt = null;
            int? completion = null;
            if (root.TryGetProperty("usage", out JsonElement usage))
            {
                if (usage.TryGetProperty("prompt_tokens", out JsonElement pt) && pt.TryGetInt32(out int p))
                {
                    prompt = p;
                }

                if (usage.TryGetProperty("completion_tokens", out JsonElement ct) && ct.TryGetInt32(out int c))
                {
                    completion = c;
                }
            }

            return new LlmReply(text, calls, finish, prompt, completion);
        }
        catch (JsonException)
        {
            return new LlmReply(body, Array.Empty<LlmToolCall>(), null, null, null);
        }
    }

    private static string DescribeRequest(IReadOnlyList<LlmMessage> messages, string url, IReadOnlyList<LlmTool>? tools)
    {
        int chars = messages.Sum(m => m.Text?.Length ?? 0);
        int images = messages.Count(m => m.ImagePng is { Length: > 0 });
        return JsonSerializer.Serialize(new
        {
            url,
            messages = messages.Count,
            characters = chars,
            images,
            tools = tools?.Count ?? 0,
        });
    }

    private static string Truncate(string text, int max)
        => string.IsNullOrEmpty(text) || text.Length <= max ? text : text[..max] + "…";
}
