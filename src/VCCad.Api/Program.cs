using System.Net.WebSockets;
using Microsoft.AspNetCore.StaticFiles;
using System.Text;
using System.Text.Json;
using VCCad.Api.JsonRpc;
using VCCad.Api.Services;
using VCCad.Core.Model;
using VCCad.Core.Serialization;
using VCCad.Pdf;

// =============================================================================
// VCCad automation host.
//
// Serves three things from one Kestrel process (see the container layout in the
// project plan §4):
//   1. REST   /api/v1/*    — document lifecycle + PDF produce/consume.
//   2. WS     /ws/rpc      — JSON-RPC 2.0 command channel (the full editor API).
//   3. Static /            — the published Avalonia WebAssembly editor.
// =============================================================================

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<IDocumentStore, InMemoryDocumentStore>();
builder.Services.AddSingleton<EditorApi>();
builder.Services.AddSingleton<JsonRpcDispatcher>();

var app = builder.Build();

// --- REST lifecycle -----------------------------------------------------------
var api = app.MapGroup("/api/v1");

api.MapGet("/health", () => Results.Ok(new { status = "ok", service = "vccad-api", version = "0.1.0" }))
    .WithName("Health");

api.MapGet("/documents", (IDocumentStore store) => Results.Ok(
        store.List().Select(d => new { id = d.Id, name = d.Name, artboards = d.Artboards.Count })))
    .WithName("ListDocuments");

api.MapPost("/documents", (CreateDocumentRequest body, IDocumentStore store, EditorApi apiService) =>
    {
        CadDocument doc = CadDocument.CreateDefault(body.Name ?? "Untitled");
        store.Add(new DocumentSession { Document = doc, Stack = new VCCad.Core.Commands.CommandStack() });
        return Results.Created($"/api/v1/documents/{doc.Id}", new { id = doc.Id, name = doc.Name });
    })
    .WithName("CreateDocument");

api.MapGet("/documents/{id:guid}", (Guid id, IDocumentStore store) =>
    {
        CadDocument? doc = store.Find(id);
        if (doc is null)
        {
            return Results.NotFound(new { error = $"Document '{id}' does not exist." });
        }

        // The canonical lossless payload — the same bytes the PDF sidecar embeds.
        return Results.Text(VccadDocumentSerializer.Serialize(doc), "application/json");
    })
    .WithName("GetDocument");

api.MapGet("/documents/{id:guid}/pdf", (Guid id, IDocumentStore store) =>
    {
        CadDocument? doc = store.Find(id);
        if (doc is null)
        {
            return Results.NotFound(new { error = $"Document '{id}' does not exist." });
        }

        byte[] pdf = PdfDocumentExporter.Export(doc);
        string fileName = $"{Sanitize(doc.Name)}.pdf";
        return Results.File(pdf, "application/pdf", fileName);
    })
    .WithName("GetPdf");

// Import a PDF (multipage → artboards). Accepts application/pdf or a JSON
// body {"pdfBase64":"..."}; returns the created document id.
api.MapPost("/documents/import", async (HttpRequest request, IDocumentStore store) =>
    {
        byte[] bytes;
        if (request.ContentType?.Contains("application/pdf", StringComparison.OrdinalIgnoreCase) == true)
        {
            using var buffer = new MemoryStream();
            await request.Body.CopyToAsync(buffer);
            bytes = buffer.ToArray();
        }
        else
        {
            using var reader = new StreamReader(request.Body);
            string body = await reader.ReadToEndAsync();
            try
            {
                using JsonDocument json = JsonDocument.Parse(body);
                bytes = Convert.FromBase64String(json.RootElement.GetProperty("pdfBase64").GetString() ?? string.Empty);
            }
            catch (Exception ex) when (ex is JsonException or FormatException or KeyNotFoundException)
            {
                return Results.BadRequest(new { error = $"Invalid import payload: {ex.Message}" });
            }
        }

        if (bytes.Length == 0)
        {
            return Results.BadRequest(new { error = "Empty PDF payload." });
        }

        // The importer refuses input that is not a PDF, and a refusal is the caller's fault, not
        // ours: it is a 400 with the reason, not a 500. This call used to sit outside the try
        // above, so once the importer started refusing rather than fabricating a blank page, a
        // non-PDF became a server error - which tells the caller the same nothing it was told
        // before.
        CadDocument imported;
        try
        {
            imported = PdfImporter.Import(bytes);
        }
        catch (InvalidDataException ex)
        {
            return Results.BadRequest(new { error = $"Not a usable PDF: {ex.Message}" });
        }

        store.Add(new DocumentSession { Document = imported, Stack = new VCCad.Core.Commands.CommandStack() });
        return Results.Created($"/api/v1/documents/{imported.Id}",
            new { id = imported.Id, name = imported.Name, artboards = imported.Artboards.Count });
    })
    .WithName("ImportPdf");

api.MapDelete("/documents/{id:guid}", (Guid id, IDocumentStore store) =>
    {
        if (store.Remove(id) is null)
        {
            return Results.NotFound(new { error = $"Document '{id}' does not exist." });
        }

        return Results.NoContent();
    })
    .WithName("DeleteDocument");

// Upsert: replace (or create) a stored document from its canonical model JSON.
// The body is the exact VccadDocumentSerializer payload the editor saves.
api.MapPut("/documents/{id:guid}", async (Guid id, HttpRequest request, IDocumentStore store) =>
    {
        using var reader = new StreamReader(request.Body);
        string payload = await reader.ReadToEndAsync();
        CadDocument incoming;
        try
        {
            incoming = VccadDocumentSerializer.Deserialize(payload);
        }
        catch (Exception ex) when (ex is FormatException or NotSupportedException or JsonException)
        {
            return Results.BadRequest(new { error = $"Invalid document payload: {ex.Message}" });
        }

        bool replaced = store.Find(id) is not null;
        if (replaced)
        {
            store.Remove(id);
        }

        store.Add(new DocumentSession { Document = incoming, Stack = new VCCad.Core.Commands.CommandStack() });
        return Results.Ok(new { id = incoming.Id, name = incoming.Name, saved = true, replaced });
    })
    .WithName("UpsertDocument");

// --- JSON-RPC over WebSocket --------------------------------------------------
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

app.Map("/ws/rpc", async (HttpContext context, JsonRpcDispatcher dispatcher) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();
    var buffer = new byte[16 * 1024];
    while (socket.State == WebSocketState.Open)
    {
        var incoming = new MemoryStream();
        WebSocketReceiveResult receive;
        do
        {
            receive = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), context.RequestAborted);
            if (receive.MessageType == WebSocketMessageType.Close)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", context.RequestAborted);
                return;
            }

            incoming.Write(buffer, 0, receive.Count);
        }
        while (!receive.EndOfMessage);

        string requestText = Encoding.UTF8.GetString(incoming.ToArray());
        string? responseText = dispatcher.Process(requestText);
        if (responseText is not null)
        {
            byte[] payload = Encoding.UTF8.GetBytes(responseText);
            await socket.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Text, true, context.RequestAborted);
        }
    }
});

// --- Static hosting of the Avalonia WebAssembly editor ------------------------
// The wasm publish output is copied into wwwroot by the Dockerfile. When the
// folder is absent (dev/test without a published editor) the app still serves
// the API so headless automation and integration tests work.
string webRoot = Path.Combine(app.Environment.ContentRootPath, "wwwroot");
bool hasEditor = Directory.Exists(webRoot) && File.Exists(Path.Combine(webRoot, "index.html"));
if (hasEditor)
{
    app.UseDefaultFiles();
    app.UseStaticFiles(new StaticFileOptions
    {
        // The editor shell (index.html / main.js / _framework) must never be
        // served stale after a redeploy, or a browser reload silently keeps the
        // old build. Disable caching for the app assets; the API is dynamic.
        OnPrepareResponse = ctx =>
        {
            ctx.Context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
            ctx.Context.Response.Headers.Pragma = "no-cache";
            ctx.Context.Response.Headers.Expires = "0";
        },
    });

    // Some wasm runtime assets (e.g. icudt_*.dat, *.wasm) are requested with
    // byte-exact paths that the static-file content-type pipeline does not
    // always match. If the request 404s but the file exists on disk, serve it
    // straight from disk so the .NET runtime's integrity checks pass.
    string webRootPath = Path.Combine(app.Environment.ContentRootPath, "wwwroot");
    app.Use(async (context, next) =>
    {
        await next();
        if (context.Response.StatusCode != StatusCodes.Status404NotFound || context.Response.HasStarted)
        {
            return;
        }

        string? relative = context.Request.Path.Value?.TrimStart('/');
        if (string.IsNullOrEmpty(relative) ||
            relative.StartsWith("api/", StringComparison.Ordinal) ||
            relative.StartsWith("ws/", StringComparison.Ordinal))
        {
            return;
        }

        string fullPath = Path.GetFullPath(Path.Combine(webRootPath, relative));
        if (!fullPath.StartsWith(webRootPath, StringComparison.Ordinal) || !File.Exists(fullPath))
        {
            return;
        }

        context.Response.Clear();
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
        await context.Response.SendFileAsync(fullPath);
    });

    app.MapFallbackToFile("index.html");
}

app.Run();

/// <summary>Cleans a document name into something file-system safe.</summary>
static string Sanitize(string name)
{
    foreach (char invalid in Path.GetInvalidFileNameChars())
    {
        name = name.Replace(invalid, '-');
    }

    return string.IsNullOrWhiteSpace(name) ? "document" : name;
}

/// <summary>Request body for POST /documents.</summary>
public sealed record CreateDocumentRequest(string? Name);

// Public so WebApplicationFactory<Program> in the integration tests can host it.
public partial class Program;
