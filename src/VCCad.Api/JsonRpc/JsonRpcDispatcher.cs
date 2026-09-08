using System.Text.Json;
using VCCad.Api.Services;

namespace VCCad.Api.JsonRpc;

/// <summary>
/// A JSON-RPC 2.0 engine over the <see cref="EditorApi"/>. It is transport
/// agnostic: the REST-free method <see cref="ProcessAsync"/> turns a request text
/// into a response text, so unit tests and the WebSocket endpoint share one code
/// path (the WebSocket handler is a thin receive/dispatch/send loop).
///
/// Conformance notes (https://www.jsonrpc.org/specification):
/// <list type="bullet">
/// <item>Requests carry <c>jsonrpc</c>, <c>method</c>, optional <c>params</c> and an
/// <c>id</c>; notifications omit the id and receive no reply.</item>
/// <item>Errors use the reserved codes: parse −32700, invalid request −32600,
/// method not found −32601, invalid params −32602, internal −32603 / −32000.</item>
/// <item>Batching (array payloads) is acknowledged but not yet implemented
/// (project plan M3 task 5).</item>
/// </list>
/// </summary>
public sealed class JsonRpcDispatcher
{
    private readonly EditorApi _api;
    private readonly Dictionary<string, Func<JsonElement, object?>> _methods;

    public JsonRpcDispatcher(EditorApi api)
    {
        _api = api;

        _methods = new Dictionary<string, Func<JsonElement, object?>>(StringComparer.Ordinal)
        {
            ["documents.list"] = p => _api.ListDocuments(p),
            ["documents.create"] = p => _api.CreateDocument(p),
            ["documents.get"] = p => _api.GetDocument(p),
            ["documents.remove"] = p => _api.RemoveDocument(p),
            ["documents.pdf"] = p => _api.GetPdf(p),
            ["document.addArtboard"] = p => _api.AddArtboard(p),
            ["document.addLayer"] = p => _api.AddLayer(p),
            ["document.addRectangle"] = p => _api.AddRectangle(p),
            ["document.addEllipse"] = p => _api.AddEllipse(p),
            ["document.addLine"] = p => _api.AddLine(p),
            ["document.setFill"] = p => _api.SetFill(p),
            ["document.undo"] = p => _api.Undo(p),
            ["document.redo"] = p => _api.Redo(p),
        };
    }

    /// <summary>Method names exposed for <c>documents.list</c>-style discovery.</summary>
    public IReadOnlyCollection<string> MethodNames => _methods.Keys;

    /// <summary>
    /// Processes one JSON-RPC message and returns the response text to send back,
    /// or <c>null</c> when the message was a notification (no reply expected).
    /// </summary>
    public string? Process(string requestText)
    {
        JsonDocument request;
        try
        {
            request = JsonDocument.Parse(requestText);
        }
        catch (JsonException)
        {
            // A malformed payload cannot carry an id, so reply with null id.
            return Error(null, -32700, "Parse error");
        }

        using (request)
        {
            JsonElement root = request.RootElement;

            // Single object only for now (batch arrays return an explicit error).
            if (root.ValueKind == JsonValueKind.Array)
            {
                return Error(null, -32600, "Batch requests are not supported yet.");
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                return Error(null, -32600, "Request must be a JSON object.");
            }

            bool isNotification = !root.TryGetProperty("id", out _);
            object? id = null;
            if (!isNotification)
            {
                JsonElement idValue = root.GetProperty("id");
                id = idValue.ValueKind switch
                {
                    JsonValueKind.String => idValue.GetString(),
                    JsonValueKind.Number => idValue.GetDouble(),
                    _ => null,
                };
            }

            if (!root.TryGetProperty("method", out JsonElement methodElement) ||
                methodElement.ValueKind != JsonValueKind.String)
            {
                return Error(id, -32600, "Request must contain a string 'method'.");
            }

            string method = methodElement.GetString()!;
            if (!_methods.TryGetValue(method, out Func<JsonElement, object?>? handler))
            {
                return Error(id, -32601, $"Method '{method}' not found.");
            }

            JsonElement empty = default;
            JsonElement parameters = root.TryGetProperty("params", out JsonElement p) ? p : empty;
            if (parameters.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined or JsonValueKind.Null))
            {
                return Error(id, -32602, "Parameters must be a JSON object.");
            }

            object? result;
            try
            {
                result = handler(parameters);
            }
            catch (RpcException ex)
            {
                return Error(id, ex.Code, ex.Message);
            }
            catch (Exception ex)
            {
                // Any unexpected failure is reported without leaking stack details.
                return Error(id, -32603, $"Internal error: {ex.GetType().Name}: {ex.Message}");
            }

            return isNotification ? null : Result(id, result);
        }
    }

    private static string Result(object? id, object? result)
    {
        var envelope = new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
        return JsonSerializer.Serialize(envelope);
    }

    private static string Error(object? id, int code, string message)
    {
        var envelope = new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["error"] = new Dictionary<string, object?> { ["code"] = code, ["message"] = message },
        };
        return JsonSerializer.Serialize(envelope);
    }
}
