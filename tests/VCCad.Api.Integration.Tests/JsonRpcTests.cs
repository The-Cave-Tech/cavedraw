using System.Text.Json;
using VCCad.Api.JsonRpc;
using VCCad.Api.Services;
using Xunit;

namespace VCCad.Api.Integration.Tests;

/// <summary>
/// Protocol-level tests for the JSON-RPC automation surface. They drive the exact
/// dispatcher the WebSocket endpoint uses, but without sockets — which keeps the
/// "UI == automation == tests" command path deterministic and fast.
/// </summary>
public class JsonRpcTests
{
    private static JsonRpcDispatcher CreateDispatcher()
    {
        var store = new InMemoryDocumentStore();
        var api = new EditorApi(store);
        return new JsonRpcDispatcher(api);
    }

    private static string Call(JsonRpcDispatcher dispatcher, string method, object? parameters, int? id = 1)
    {
        var request = new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id ?? 1,
            ["method"] = method,
        };
        if (parameters is not null)
        {
            request["params"] = parameters;
        }

        string text = JsonSerializer.Serialize(request);
        string? response = dispatcher.Process(text);
        Assert.NotNull(response); // method with an id must always reply
        return response;
    }

    [Fact]
    public void GoldenScriptBuildsAndUndoesADocument()
    {
        JsonRpcDispatcher dispatcher = CreateDispatcher();

        // 1. Create the default A4-landscape document.
        string created = Call(dispatcher, "documents.create", new { name = "golden" });
        string docId = JsonDocument.Parse(created).RootElement.GetProperty("result").GetProperty("id").GetString()!;
        Assert.False(string.IsNullOrEmpty(docId));

        // 2. Add a layer under the default artboard.
        string layerResult = Call(dispatcher, "document.addLayer", new { id = docId, name = "Shapes" });
        string layerId = JsonDocument.Parse(layerResult).RootElement.GetProperty("result")
            .GetProperty("layerId").GetString()!;

        // 3–4. Add two styled shapes.
        string rectResult = Call(dispatcher, "document.addRectangle", new
        {
            id = docId,
            layerId,
            name = "R",
            x = 20.0,
            y = 30.0,
            width = 120.0,
            height = 80.0,
            fillColor = new[] { 200, 30, 30 },
            strokeColor = new[] { 0, 0, 0 },
            strokeWidth = 1.5,
        });
        string rectItemId = JsonDocument.Parse(rectResult).RootElement.GetProperty("result")
            .GetProperty("itemId").GetString()!;

        Call(dispatcher, "document.addEllipse", new
        {
            id = docId,
            layerId,
            cx = 200.0,
            cy = 200.0,
            rx = 60.0,
            ry = 40.0,
            fillColor = new[] { 30, 30, 200 },
        });

        // 5. Verify via the model payload.
        string get = Call(dispatcher, "documents.get", new { id = docId });
        using (JsonDocument model = JsonDocument.Parse(get))
        {
            JsonElement layers = model.RootElement.GetProperty("result")
                .GetProperty("Artboards")[0].GetProperty("Layers");
            JsonElement shapes = layers[layers.GetArrayLength() - 1];
            Assert.Equal(2, shapes.GetProperty("Items").GetArrayLength());
        }

        // 6. Undo removes the last added shape; redo restores it.
        Call(dispatcher, "document.undo", new { id = docId });
        string getAfterUndo = Call(dispatcher, "documents.get", new { id = docId });
        using (JsonDocument model = JsonDocument.Parse(getAfterUndo))
        {
            JsonElement layers = model.RootElement.GetProperty("result")
                .GetProperty("Artboards")[0].GetProperty("Layers");
            JsonElement shapes = layers[layers.GetArrayLength() - 1];
            Assert.Equal(1, shapes.GetProperty("Items").GetArrayLength());
        }

        Call(dispatcher, "document.redo", new { id = docId });
        string getAfterRedo = Call(dispatcher, "documents.get", new { id = docId });
        using (JsonDocument model = JsonDocument.Parse(getAfterRedo))
        {
            JsonElement layers = model.RootElement.GetProperty("result")
                .GetProperty("Artboards")[0].GetProperty("Layers");
            JsonElement shapes = layers[layers.GetArrayLength() - 1];
            Assert.Equal(2, shapes.GetProperty("Items").GetArrayLength());
        }
    }

    [Fact]
    public void NotificationsReceiveNoReply()
    {
        JsonRpcDispatcher dispatcher = CreateDispatcher();
        string request = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "documents.list", // no "id" → notification
        });
        Assert.Null(dispatcher.Process(request));
    }

    [Fact]
    public void UnknownMethodReturnsStandardErrorCode()
    {
        JsonRpcDispatcher dispatcher = CreateDispatcher();
        string response = Call(dispatcher, "no.such.method", null);
        using JsonDocument json = JsonDocument.Parse(response);
        Assert.Equal(-32601, json.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public void MalformedPayloadYieldsParseError()
    {
        JsonRpcDispatcher dispatcher = CreateDispatcher();
        string? response = dispatcher.Process("{not json!");
        Assert.NotNull(response);
        using JsonDocument json = JsonDocument.Parse(response);
        Assert.Equal(-32700, json.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public void MissingRequiredParamYieldsInvalidParams()
    {
        JsonRpcDispatcher dispatcher = CreateDispatcher();
        // documents.get requires { id }; omit it.
        string response = Call(dispatcher, "documents.get", new { });
        using JsonDocument json = JsonDocument.Parse(response);
        Assert.Equal(-32602, json.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public void ListMethodsReportsCatalog()
    {
        JsonRpcDispatcher dispatcher = CreateDispatcher();
        string response = Call(dispatcher, "documents.list", null);
        using JsonDocument json = JsonDocument.Parse(response);
        Assert.Equal(JsonValueKind.Array, json.RootElement.GetProperty("result").ValueKind);
    }
}
