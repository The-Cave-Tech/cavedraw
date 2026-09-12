using System.Text.Json;
using VCCad.Core.Commands;
using VCCad.Core.Model;
using VCCad.Core.Serialization;
using VCCad.Geometry;
using VCCad.Pdf;

namespace VCCad.Api.Services;

/// <summary>
/// Raised for user-facing API errors; the JSON-RPC dispatcher and the REST layer
/// translate <see cref="Code"/> + <see cref="Message"/> into protocol errors.
/// </summary>
public sealed class RpcException : Exception
{
    public int Code { get; }

    public RpcException(int code, string message)
        : base(message)
    {
        Code = code;
    }
}

/// <summary>
/// The automation surface of the editor: every operation the UI can perform (via
/// the command bus) is also callable here, plus document lifecycle (ADR-04/05).
///
/// Params arrive as a JSON object; results are plain CLR objects that the
/// transport layer serialises. Because documents are addressed by Guid and
/// commands are deterministic, a sequence of RPC calls is a replayable script.
/// </summary>
public sealed class EditorApi
{
    private readonly IDocumentStore _store;

    /// <summary>Document default name used when a create request omits one.</summary>
    public const string DefaultDocumentName = "Untitled";

    public EditorApi(IDocumentStore store)
    {
        _store = store;
    }

    /// <summary>Resolves the store session for a document id, or throws an RPC error.</summary>
    private DocumentSession Session(JsonElement p)
    {
        Guid id = p.RequireGuid("id");
        CadDocument? doc = _store.Find(id);
        if (doc is null)
        {
            throw new RpcException(-32602, $"Document '{id}' does not exist.");
        }

        return new DocumentSession { Document = doc, Stack = SessionStack(id) };
    }

    // ------------------------------------------------------------------
    // The RPC API. Public methods named "<group>.<verb>" map 1:1 to JSON-RPC.
    // ------------------------------------------------------------------

    /// <summary>Lists the stored documents: [{ id, name, artboards }].</summary>
    public object ListDocuments(JsonElement p) => _store.List()
        .Select(d => new { id = d.Id, name = d.Name, artboards = d.Artboards.Count })
        .ToArray();

    /// <summary>Creates a fresh default document (A4 landscape + one layer).</summary>
    public object CreateDocument(JsonElement p)
    {
        string name = p.GetString("name") ?? DefaultDocumentName;
        CadDocument doc = CadDocument.CreateDefault(name);
        _store.Add(new DocumentSession { Document = doc, Stack = new CommandStack() });
        return new { id = doc.Id, name = doc.Name };
    }

    /// <summary>Returns the full lossless model for a document.</summary>
    public object GetDocument(JsonElement p)
    {
        Session(p);
        Guid id = p.RequireGuid("id");
        CadDocument doc = _store.Find(id)!;
        // Parse the canonical serialization back into a JSON value so structured
        // clients get a proper object (not an escaped string).
        using JsonDocument model = JsonDocument.Parse(VccadDocumentSerializer.Serialize(doc));
        return model.RootElement.Clone();
    }

    /// <summary>Removes a stored document.</summary>
    public object RemoveDocument(JsonElement p)
    {
        Guid id = p.RequireGuid("id");
        CadDocument? removed = _store.Remove(id);
        if (removed is null)
        {
            throw new RpcException(-32602, $"Document '{id}' does not exist.");
        }

        return new { removed = true, id };
    }

    /// <summary>Imports a PDF (base64) as a new document. Pages become artboards.</summary>
    public object ImportPdf(JsonElement p)
    {
        string base64 = p.GetString("pdfBase64")
            ?? throw new RpcException(-32602, "Parameter 'pdfBase64' is required.");
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            throw new RpcException(-32602, "Parameter 'pdfBase64' is not valid base64.");
        }

        CadDocument imported = VCCad.Pdf.PdfImporter.Import(bytes);
        _store.Add(new DocumentSession { Document = imported, Stack = new CommandStack() });
        return new { id = imported.Id, name = imported.Name, artboards = imported.Artboards.Count };
    }

    /// <summary>Exports a document to lossless PDF bytes (base64 in the result).</summary>
    public object GetPdf(JsonElement p)
    {
        Session(p);
        Guid id = p.RequireGuid("id");
        byte[] pdf = PdfDocumentExporter.Export(_store.Find(id)!);
        return new { pdfBase64 = Convert.ToBase64String(pdf), mediaType = "application/pdf" };
    }

    // ------------------------------------------------------------------
    // Command-driven mutations (undoable).
    // ------------------------------------------------------------------

    /// <summary>Adds an artboard to a document. Params: size { width, height }.</summary>
    public object AddArtboard(JsonElement p)
    {
        DocumentSession s = Session(p);
        double width = p.GetDouble("width", PageSizes.A4Landscape.Width);
        double height = p.GetDouble("height", PageSizes.A4Landscape.Height);
        string? name = p.GetString("name");

        s.Stack.Execute(new AddArtboardCommand(s.Document, new Size2D(width, height), name));
        Artboard artboard = s.Document.Artboards[^1];
        return Summary(artboard);
    }

    /// <summary>Adds a layer to the first artboard (or the one given by artboardId).</summary>
    public object AddLayer(JsonElement p)
    {
        DocumentSession s = Session(p);
        Artboard artboard = ResolveArtboard(s.Document, p.GetNullableGuid("artboardId"));
        string? name = p.GetString("name");
        s.Stack.Execute(new AddLayerCommand(artboard, name));
        return Summary(artboard.Layers[^1]);
    }

    /// <summary>Adds a closed rectangle path. Geometry keys are model points.</summary>
    public object AddRectangle(JsonElement p)
    {
        PathItem rect = PathFactory.CreateRectangle(
            p.GetString("name") ?? "Rectangle",
            new Rect2D(p.GetDouble("x"), p.GetDouble("y"), p.GetDouble("width"), p.GetDouble("height")));
        return AddItemWithStyle(p, rect);
    }

    /// <summary>Adds a closed ellipse (arc-approximated with four cubics).</summary>
    public object AddEllipse(JsonElement p)
    {
        PathItem ellipse = PathFactory.CreateEllipse(
            p.GetString("name") ?? "Ellipse",
            new Point2D(p.GetDouble("cx"), p.GetDouble("cy")),
            p.GetDouble("rx"),
            p.GetDouble("ry"));
        return AddItemWithStyle(p, ellipse);
    }

    /// <summary>Adds an open straight line between (x1,y1) and (x2,y2).</summary>
    public object AddLine(JsonElement p)
    {
        PathItem line = PathFactory.CreateLine(
            p.GetString("name") ?? "Line",
            new Point2D(p.GetDouble("x1"), p.GetDouble("y1")),
            new Point2D(p.GetDouble("x2"), p.GetDouble("y2")));
        return AddItemWithStyle(p, line);
    }

    /// <summary>Sets the fill of a path item. Params: itemId, color:[r,g,b].</summary>
    public object SetFill(JsonElement p)
    {
        DocumentSession s = Session(p);
        PathItem path = ResolvePathItem(s.Document, p.RequireGuid("itemId"));
        ColorRgb color = p.ParseColor("color", ColorRgb.Black);
        s.Stack.Execute(new SetFillCommand(path, FillSpec.Solid(color)));
        return new { itemId = path.Id, fill = new { visible = true, color = new[] { color.R, color.G, color.B } } };
    }

    /// <summary>Undoes the last executed command for the session's document.</summary>
    public object Undo(JsonElement p)
    {
        DocumentSession s = Session(p);
        bool undone = s.Stack.Undo();
        return new { undone, canUndo = s.Stack.CanUndo, canRedo = s.Stack.CanRedo };
    }

    /// <summary>Re-applies the last undone command for the session's document.</summary>
    public object Redo(JsonElement p)
    {
        DocumentSession s = Session(p);
        bool redone = s.Stack.Redo();
        return new { redone, canUndo = s.Stack.CanUndo, canRedo = s.Stack.CanRedo };
    }

    /// <summary>Lists every JSON-RPC method this API exposes (for discovery).</summary>
    public string[] ListMethods() => new[]
    {
        "documents.list", "documents.create", "documents.get", "documents.remove", "documents.pdf",
        "document.addArtboard", "document.addLayer", "document.addRectangle", "document.addEllipse",
        "document.addLine", "document.setFill", "document.undo", "document.redo",
    };

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    /// <summary>Attaches a layer target plus styling and executes the add command.</summary>
    private object AddItemWithStyle(JsonElement p, PathItem item)
    {
        DocumentSession s = Session(p);
        Layer layer = ResolveLayer(s.Document, p.GetNullableGuid("layerId"));

        // Optional fill: fillColor:[r,g,b] over 0..255. Absent → no fill.
        if (p.TryGetColorArray("fillColor", out ColorRgb fill))
        {
            item.Fill = FillSpec.Solid(fill);
        }

        // Optional stroke: strokeColor + strokeWidth. Absent → no stroke.
        if (p.TryGetColorArray("strokeColor", out ColorRgb stroke))
        {
            double width = p.GetDouble("strokeWidth", 1.0);
            item.Stroke = new StrokeSpec(true, stroke, width, StrokeCap.Butt, StrokeJoin.Miter, 4.0);
        }

        s.Stack.Execute(new AddItemCommand(layer, item));
        return new
        {
            itemId = item.Id,
            name = item.Name,
            type = "path",
            x = item.BoundingBox().X,
            y = item.BoundingBox().Y,
            width = item.BoundingBox().Width,
            height = item.BoundingBox().Height,
        };
    }

    private static object Summary(Artboard artboard)
        => new { artboardId = artboard.Id, name = artboard.Name, layers = artboard.Layers.Count };

    private static object Summary(Layer layer)
        => new { layerId = layer.Id, name = layer.Name, items = layer.Children.Count };

    private Artboard ResolveArtboard(CadDocument doc, Guid? id)
    {
        if (doc.Artboards.Count == 0)
        {
            throw new RpcException(-32602, "Document has no artboards.");
        }

        if (id is null)
        {
            return doc.Artboards[0];
        }

        Artboard? match = doc.Artboards.FirstOrDefault(a => a.Id == id);
        if (match is null)
        {
            throw new RpcException(-32602, $"Artboard '{id}' does not exist.");
        }

        return match;
    }

    private Layer ResolveLayer(CadDocument doc, Guid? id)
    {
        if (id is null)
        {
            return ResolveArtboard(doc, null).Layers[^1];
        }

        foreach (Artboard artboard in doc.Artboards)
        {
            Layer? layer = artboard.Layers.FirstOrDefault(l => l.Id == id);
            if (layer is not null)
            {
                return layer;
            }
        }

        throw new RpcException(-32602, $"Layer '{id}' does not exist.");
    }

    private PathItem ResolvePathItem(CadDocument doc, Guid itemId)
    {
        foreach (Artboard artboard in doc.Artboards)
        {
            foreach (Layer layer in artboard.Layers)
            {
                foreach (LayerItem child in layer.Children)
                {
                    PathItem? found = FindPath(child, itemId);
                    if (found is not null)
                    {
                        return found;
                    }
                }
            }
        }

        throw new RpcException(-32602, $"Item '{itemId}' does not exist.");
    }

    private static PathItem? FindPath(LayerItem host, Guid itemId)
    {
        if (host is PathItem path && path.Id == itemId)
        {
            return path;
        }

        if (host is ArtGroup group)
        {
            foreach (LayerItem child in group.Children)
            {
                PathItem? found = FindPath(child, itemId);
                if (found is not null)
                {
                    return found;
                }
            }
        }

        return null;
    }

    private readonly Dictionary<Guid, CommandStack> _sessionStacks = new();

    /// <summary>
    /// Per-session undo stacks. In-process we key by document id; when multiple
    /// independent sessions exist (WebSocket connections) the key should be the
    /// connection — see project plan M3 task 9. The seed stores one stack per
    /// document, which keeps the demo deterministic for tests.
    /// </summary>
    private CommandStack SessionStack(Guid docId)
    {
        lock (_sessionStacks)
        {
            if (!_sessionStacks.TryGetValue(docId, out CommandStack? stack))
            {
                stack = new CommandStack();
                _sessionStacks[docId] = stack;
            }

            return stack;
        }
    }
}
