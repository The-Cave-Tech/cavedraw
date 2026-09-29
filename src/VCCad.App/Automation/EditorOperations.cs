using System.Globalization;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.VisualTree;
using Avalonia.Threading;
using VCCad.App.Controls;
using VCCad.App.Fonts;
using VCCad.Pdf;
using VCCad.App.ViewModels;
using VCCad.App.Views.Panes;
using VCCad.Core.Commands;
using VCCad.Core.Model;
using VCCad.Core.Color;
using VCCad.Core.Input;
using VCCad.Core.Selection;
using VCCad.Core.Serialization;
using VCCad.Geometry;

namespace VCCad.App.Automation;

/// <summary>Everything an operation handler needs: the live editor, and app hooks.</summary>
public sealed class AutomationContext
{
    /// <summary>Asks the vision-capable model to describe something.</summary>
    /// <param name="prompt">The question and context.</param>
    /// <param name="image">Optional PNG to look at.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public delegate Task<string> VisionDescribe(string prompt, byte[]? image, CancellationToken cancellationToken);

    /// <summary>The live editor view-model.</summary>
    public required EditorViewModel ViewModel { get; init; }

    /// <summary>The active document session.</summary>
    public DocumentSession Session => ViewModel.ActiveSession;

    /// <summary>The active document.</summary>
    public CadDocument Document => ViewModel.Document;

    /// <summary>Renders the workspace to PNG bytes, when a window is available.</summary>
    public Func<byte[]?>? Screenshot { get; init; }

    /// <summary>The window's visual tree, so a driver can inject pointer and key events.</summary>
    public Func<Avalonia.Visual?>? InputRoot { get; init; }

    /// <summary>Serialises the window's visual tree to text (the "what is on screen" dump).</summary>
    public Func<int, string>? UiTreeDump { get; init; }

    /// <summary>Vision helper: describes a region or the whole view using the model.</summary>
    public VisionDescribe? Describe { get; init; }

    /// <summary>Sends a prompt to the in-app chatbot; wired by the agent.</summary>
    public Func<string, bool, Task<object?>>? Chat { get; init; }

    /// <summary>
    /// Supplies the token that cancels the current long-running operation. Slow
    /// operations observe it so the person can abort from the diagnostics overlay.
    /// </summary>
    public Func<CancellationToken>? Cancellation { get; init; }

    /// <summary>Aborts the current long-running operation, if any.</summary>
    public Action? Cancel { get; init; }

    /// <summary>Viewport controls, so zoom/fit are operations like everything else.</summary>
    public ViewportActions? Viewport { get; init; }

    /// <summary>
    /// The root control of the running window (null in headless hosts). Used by the
    /// point-and-click operations, because the menus, toolbar and panes are part of
    /// the product surface and must be automatable too.
    /// </summary>
    public Func<Avalonia.Controls.Control?>? UiRoot { get; init; }

    /// <summary>Window-level actions: dockable panes and application exit.</summary>
    public HostActions? Host { get; init; }

    /// <summary>
    /// The application diary: every UI event, automation call and model request, plus
    /// learned skills. Null only in hosts that deliberately run without one.
    /// </summary>
    public InteractionLog? History { get; init; }
}

/// <summary>One dockable pane/tab of the editor shell.</summary>
/// <param name="Id">Stable id used by the dock manager.</param>
/// <param name="Title">Title shown in the Windows menu and on the tab.</param>
/// <param name="IsOpen">Whether it is currently shown.</param>
public sealed record PaneInfo(string Id, string Title, bool IsOpen);

/// <summary>How a docked panel is sized: fixed by pixel height, or sharing the slack.</summary>
/// <param name="Id">Panel id.</param>
/// <param name="Title">Title shown on the tab strip.</param>
/// <param name="Stretchable">True when it shares the leftover space with its neighbours.</param>
/// <param name="Height">Its height in pixels when fixed.</param>
/// <param name="Weight">Its share of the slack when stretchable.</param>
public sealed record PaneSize(string Id, string Title, bool Stretchable, double Height, double Weight);

/// <summary>Shell-level actions the automation surface exposes.</summary>
/// <param name="Panes">Lists the dockable panes.</param>
/// <param name="SetPaneOpen">Shows/hides a pane by id or title; returns the new state.</param>
/// <param name="PanelSizes">How each open panel is currently sized.</param>
/// <param name="SetPaneStretchable">Makes a panel fixed or stretchable; false if unknown.</param>
/// <param name="SetPaneSize">Sizes a panel; false if unknown.</param>
/// <param name="Exit">Closes the application.</param>
public sealed record HostActions(
    Func<IReadOnlyList<PaneInfo>> Panes,
    Func<string, bool?, bool> SetPaneOpen,
    Func<IReadOnlyList<PaneSize>> PanelSizes,
    Func<string, bool, double?, bool> SetPaneStretchable,
    Func<string, double, bool> SetPaneSize,
    Action Exit);

/// <summary>Viewport actions exposed to automation (the toolbar's zoom controls).</summary>
/// <param name="Fit">Fit the document into the visible viewport.</param>
/// <param name="ActualSize">Zoom to 100%.</param>
/// <param name="Zoom">Set an absolute zoom factor.</param>
/// <param name="GetZoom">Read the current zoom factor.</param>
/// <param name="IsAutoFit">True while the view still fits automatically.</param>
public sealed record ViewportActions(
    Action Fit,
    Action ActualSize,
    Action<double> Zoom,
    Func<double> GetZoom,
    Func<bool> IsAutoFit,
    Action ZoomIn,
    Action ZoomOut,
    Func<bool> GetClipToArtboard,
    Action<bool> SetClipToArtboard,
    Action<double, double> CenterOn,
    Func<(double X, double Y)> GetViewCenter);

/// <summary>
/// One automation operation: its name, a human/model readable summary, the JSON
/// shape of its parameters, and the handler.
///
/// Exactly one of <paramref name="Handler"/> and <paramref name="AsyncHandler"/> is
/// set. Slow operations (anything that calls the model) MUST use the async form:
/// the sync form runs on the UI thread, and blocking there freezes the editor.
/// </summary>
/// <param name="Name">Dotted operation name, e.g. <c>object.create</c>.</param>
/// <param name="Summary">One-line description, used in the model's tool catalog.</param>
/// <param name="Parameters">Compact description of the JSON parameters.</param>
/// <param name="Handler">Runs the operation synchronously (on the UI thread).</param>
/// <param name="AsyncHandler">Runs the operation asynchronously, off the UI thread.</param>
public sealed record EditorOperation(
    string Name,
    string Summary,
    string Parameters,
    Func<AutomationContext, JsonElement, object?>? Handler = null,
    Func<AutomationContext, JsonElement, CancellationToken, Task<object?>>? AsyncHandler = null);

/// <summary>
/// The complete automation surface of the editor.
///
/// Every operation a person can perform must exist here: the HTTP automation
/// server, the JSON-RPC dispatcher and the in-app chatbot all execute through
/// this one registry, and every call is recorded in <see cref="DiagnosticsLog"/>.
/// That is what makes the application 100% scriptable and keeps the person and
/// the model looking at the same capabilities.
/// </summary>
public static class EditorOperations
{
    private static readonly Dictionary<string, EditorOperation> Registry = Build();

    /// <summary>All registered operations, ordered by name.</summary>
    public static IReadOnlyList<EditorOperation> All
        => Registry.Values.OrderBy(o => o.Name, StringComparer.Ordinal).ToArray();

    /// <summary>Looks up an operation by name.</summary>
    public static bool TryGet(string name, out EditorOperation operation)
        => Registry.TryGetValue(name, out operation!);

    /// <summary>
    /// Runs an operation, recording it in the diagnostics log. Throws
    /// <see cref="EditorOperationException"/> for user-facing failures.
    ///
    /// Synchronous operations are executed on the UI thread, because they read and
    /// mutate the live document. Async operations run off it and marshal only their
    /// own document access, so a slow model call never freezes the editor.
    /// </summary>
    public static async Task<object?> InvokeAsync(
        AutomationContext context,
        string name,
        JsonElement parameters,
        ApiCallSource source = ApiCallSource.Api,
        CancellationToken cancellationToken = default)
    {
        string json = parameters.ValueKind == JsonValueKind.Undefined ? "{}" : parameters.GetRawText();
        long start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        try
        {
            if (!TryGet(name, out EditorOperation operation))
            {
                throw new EditorOperationException($"Unknown operation '{name}'. Call app.operations to list them.");
            }

            object? result = operation.AsyncHandler is { } asyncHandler
                ? await asyncHandler(context, parameters, cancellationToken).ConfigureAwait(false)
                : await Dispatcher.UIThread.InvokeAsync(() => operation.Handler!(context, parameters));

            // Journal the command, not just its effect. A list of operation calls is a
            // complete description of an edit because every edit goes through this one
            // registry, and it is what lets a crash be recovered rather than merely
            // noticed.
            if (SessionJournal.IsMutation(name))
            {
                SessionJournal.Record(name, json, context.Document);
            }

            DiagnosticsLog.Add(source, name, json, result, success: true,
                durationMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - start);
            return result;
        }
        catch (OperationCanceledException)
        {
            long ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - start;
            DiagnosticsLog.Add(source, name, json, null, success: false, durationMs: ms, error: "cancelled");
            throw;
        }
        catch (EditorOperationException ex)
        {
            DiagnosticsLog.AddFailure(source, name, json,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - start, ex);
            throw;
        }
        catch (Exception ex)
        {
            DiagnosticsLog.AddFailure(source, name, json,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - start, ex);
            throw new EditorOperationException($"{name} failed: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Runs a synchronous operation on the calling thread. Only valid for
    /// operations with a synchronous handler; async-only operations must go through
    /// <see cref="InvokeAsync"/>.
    /// </summary>
    public static object? Invoke(
        AutomationContext context, string name, JsonElement parameters, ApiCallSource source = ApiCallSource.Api)
    {
        if (!TryGet(name, out EditorOperation operation))
        {
            DiagnosticsLog.AddFailure(source, name, null, 0,
                new EditorOperationException($"Unknown operation '{name}'."));
            throw new EditorOperationException($"Unknown operation '{name}'. Call app.operations to list them.");
        }

        if (operation.Handler is null)
        {
            throw new EditorOperationException(
                $"Operation '{name}' is asynchronous; call it through the API or the assistant.");
        }

        string json = parameters.ValueKind == JsonValueKind.Undefined ? "{}" : parameters.GetRawText();
        long start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        try
        {
            object? result = operation.Handler(context, parameters);
            // Journal the command, not just its effect. A list of operation calls is a
            // complete description of an edit because every edit goes through this one
            // registry, and it is what lets a crash be recovered rather than merely
            // noticed.
            if (SessionJournal.IsMutation(name))
            {
                SessionJournal.Record(name, json, context.Document);
            }

            DiagnosticsLog.Add(source, name, json, result, success: true,
                durationMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - start);
            return result;
        }
        catch (EditorOperationException ex)
        {
            DiagnosticsLog.AddFailure(source, name, json,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - start, ex);
            throw;
        }
        catch (Exception ex)
        {
            DiagnosticsLog.AddFailure(source, name, json,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - start, ex);
            throw new EditorOperationException($"{name} failed: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// The catalog the model is given as its tool list: name, summary, parameters.
    /// Kept compact — it goes into every system prompt.
    /// </summary>
    public static string Catalog()
    {
        var sb = new StringBuilder();
        foreach (EditorOperation op in All)
        {
            sb.Append("- ").Append(op.Name).Append(": ").Append(op.Summary);
            if (!string.IsNullOrWhiteSpace(op.Parameters))
            {
                sb.Append("  params: ").Append(op.Parameters);
            }

            sb.Append('\n');
        }

        return sb.ToString();
    }

    // ------------------------------------------------------------------
    // Registry
    // ------------------------------------------------------------------

    private static Dictionary<string, EditorOperation> Build()
    {
        var ops = new List<EditorOperation>();
        void Add(string name, string summary, string parameters, Func<AutomationContext, JsonElement, object?> handler)
            => ops.Add(new EditorOperation(name, summary, parameters, handler));

        void AddAsync(string name, string summary, string parameters,
            Func<AutomationContext, JsonElement, CancellationToken, Task<object?>> handler)
            => ops.Add(new EditorOperation(name, summary, parameters, AsyncHandler: handler));

        // ---- application -------------------------------------------------
        Add("app.ping", "Liveness probe.", "", (ctx, p) => new
        {
            ok = true,
            app = "VCCad",
            document = ctx.Document.Name,
            artboards = ctx.Document.Artboards.Count,
        });

        Add("app.operations", "List every operation, with its parameters.", "", (_, _) => All
            .Select(o => new { op = o.Name, summary = o.Summary, parameters = o.Parameters })
            .ToArray());

        Add("app.diagnostics", "Recent automation calls (audit trail).", "since:number? max:number?",
            (_, p) => DiagnosticsLog.Since(p.GetLong("since", 0)).TakeLast((int)p.GetLong("max", 200))
                .Select(Record).ToArray());

        // ---- document ----------------------------------------------------
        Add("document.new", "Create a fresh document and make it active.", "name:string?",
            (ctx, p) =>
            {
                ctx.ViewModel.NewDocument(p.GetString("name"));
                return Summary(ctx);
            });

        Add("document.dump",
            "A canonical, complete text dump of the document model: every id, flag, " +
            "coordinate, style, text run and image sample fingerprint. Two documents with " +
            "equal dumps are identical in every value the model holds, so this is how to " +
            "check a save-and-reload rather than believe it.",
            "scope?:all|active (default all open documents)",
            (ctx, p) =>
            {
                string scope = p.TryGetProperty("scope", out JsonElement sv)
                    ? sv.GetString() ?? "all"
                    : "all";

                if (scope.Equals("active", StringComparison.OrdinalIgnoreCase))
                {
                    return new { dump = ModelDump.Of(ctx.Document) };
                }

                var dumps = ctx.ViewModel.Sessions
                    .Select(s => new { document = s.Document.Name, dump = ModelDump.Of(s.Document) })
                    .ToArray();

                return new { documents = dumps };
            });

        Add("document.verifyRoundTrip",
            "Save the active document, load it straight back, and compare the two model " +
            "dumps field by field. Returns whether they match and, when they do not, the " +
            "first differing line from each side - which is the point of having a dump " +
            "rather than a boolean.",
            "save?:bool (default true; false only round-trips in memory)",
            (ctx, p) =>
            {
                CadDocument original = ctx.Document;
                string before = ModelDump.Of(original);

                byte[] saved = VccadDocumentSerializer.SerializeToBytes(original);
                CadDocument reloaded = VccadDocumentSerializer.Deserialize(saved);
                string after = ModelDump.Of(reloaded);

                if (before == after)
                {
                    return new
                    {
                        match = true,
                        bytes = saved.Length,
                        lines = before.Split('\n').Length,
                    };
                }

                string[] a = before.Split('\n');
                string[] b = after.Split('\n');
                int limit = Math.Min(a.Length, b.Length);
                int at = 0;
                while (at < limit && a[at] == b[at])
                {
                    at++;
                }

                return new
                {
                    match = false,
                    line = at,
                    before = at < a.Length ? a[at] : "<missing>",
                    after = at < b.Length ? b[at] : "<missing>",
                    beforeLines = a.Length,
                    afterLines = b.Length,
                };
            });

        Add("document.summary", "Document structure and current selection.", "",
            (ctx, _) => Summary(ctx));

        Add("document.model", "The full lossless document model as JSON.", "",
            (ctx, _) => JsonSerializer.Deserialize<JsonElement>(VCCad.Core.Serialization.VccadDocumentSerializer.Serialize(ctx.Document)));

        Add("document.importPdf", "Import a PDF (base64) as a new document.", "pdfBase64:string",
            (ctx, p) =>
            {
                byte[] bytes = DecodeBase64(p, "pdfBase64");
                ctx.ViewModel.ImportPdf(bytes);
                return Summary(ctx);
            });

        Add("document.exportPdf", "Export the active document to PDF (base64).", "",
            (ctx, _) => new
            {
                pdfBase64 = Convert.ToBase64String(ctx.ViewModel.ExportPdf()),
                mediaType = "application/pdf",
            });

        Add("document.undo", "Undo the last change.", "", (ctx, _) =>
        {
            ctx.ViewModel.Undo();
            return Summary(ctx);
        });

        Add("document.redo", "Redo the last undone change.", "", (ctx, _) =>
        {
            ctx.ViewModel.Redo();
            return Summary(ctx);
        });

        // ---- selection ---------------------------------------------------
        Add("selection.get", "Currently selected objects.", "",
            (ctx, _) => Describe(ctx.Session.SelectedObjects).ToArray());

        Add("selection.clear", "Deselect everything.", "", (ctx, _) =>
        {
            ctx.Session.ClearSelection();
            return Summary(ctx);
        });

        Add("selection.selectAll", "Select every object in the document.", "",
            (ctx, _) =>
            {
                ctx.Session.SelectRange(AllItems(ctx.Document).ToArray(), additive: false);
                return Summary(ctx);
            });

        Add("selection.set", "Replace the selection by item ids.", "itemIds:guid[]",
            (ctx, p) =>
            {
                var items = p.GetGuidArray("itemIds").Select(id => RequireItem(ctx.Document, id)).ToArray();
                ctx.Session.SelectRange(items, additive: false);
                return Summary(ctx);
            });

        Add("selection.add", "Add item ids to the selection.", "itemIds:guid[]",
            (ctx, p) =>
            {
                var items = p.GetGuidArray("itemIds").Select(id => RequireItem(ctx.Document, id)).ToArray();
                ctx.Session.SelectRange(items, additive: true);
                return Summary(ctx);
            });

        // ---- objects -----------------------------------------------------
        Add("image.insert",
            "Place an image file (PNG, JPEG, BMP, GIF) into the document. It becomes an " +
            "ordinary image object: selectable, movable, scalable, and re-embedded on " +
            "export in the same colour space.",
            "path:string, x?,y?,width?,height? (defaults to the pixel size at the document origin), " +
            "artboardId?:guid",
            (ctx, p) =>
            {
                string path = p.GetString("path")
                    ?? throw new EditorOperationException("Parameter 'path' is required.");
                if (!File.Exists(path))
                {
                    throw new EditorOperationException($"No file at '{path}'.");
                }

                VCCad.Core.Model.ImageItem image = ImageLoader.Load(path);

                if (p.TryGetProperty("x", out JsonElement xv) && xv.ValueKind == JsonValueKind.Number)
                {
                    double w = p.TryGetProperty("width", out JsonElement wv) && wv.ValueKind == JsonValueKind.Number
                        ? wv.GetDouble()
                        : image.PixelWidth;
                    double h = p.TryGetProperty("height", out JsonElement hv) && hv.ValueKind == JsonValueKind.Number
                        ? hv.GetDouble()
                        : image.PixelHeight * (w / Math.Max(1, image.PixelWidth));

                    image.Placement = new Rect2D(xv.GetDouble(), p.GetDouble("y"), w, h);
                }

                (Layer layer, Vector2D offset) = ctx.Session.TargetFor(image.Placement.Center);
                var local = new Rect2D(
                    image.Placement.X - offset.X, image.Placement.Y - offset.Y,
                    image.Placement.Width, image.Placement.Height);
                image.Placement = local;

                ctx.Session.Execute(new AddItemCommand(layer, image));
                ctx.Session.SelectRange(new[] { image }, additive: false);

                return new
                {
                    itemId = image.Id,
                    name = image.Name,
                    pixelWidth = image.PixelWidth,
                    pixelHeight = image.PixelHeight,
                    hasAlpha = image.HasMask,
                    placement = new
                    {
                        x = Math.Round(image.Placement.X, 3),
                        y = Math.Round(image.Placement.Y, 3),
                        width = Math.Round(image.Placement.Width, 3),
                        height = Math.Round(image.Placement.Height, 3),
                    },
                };
            });

        Add("image.exportPng",
            "Write an embedded raster image to a PNG file at its natural pixel size. " +
            "How to look at what was imported without the canvas in the way, and how to " +
            "get a scan back out of a document.",
            "itemId?:guid (default: selected image), path:string",
            (ctx, p) =>
            {
                LayerItem? target = p.TryGetProperty("itemId", out JsonElement iv) &&
                                    Guid.TryParse(iv.GetString(), out Guid id)
                    ? FindItem(ctx.Document, id)
                    : ctx.ViewModel.SelectedObjects.FirstOrDefault();

                if (target is not ImageItem image)
                {
                    throw new EditorOperationException("No image object is selected.");
                }

                string path = p.GetString("path")
                    ?? throw new EditorOperationException("A path is required.");

                using Avalonia.Media.Imaging.Bitmap bitmap = ImageRenderer.BitmapFor(image);
                bitmap.Save(path);

                return new
                {
                    path,
                    pixelWidth = image.PixelWidth,
                    pixelHeight = image.PixelHeight,
                    colorSpace = image.ColorSpace.ToString(),
                    hasMask = image.HasMask,
                };
            });

        Add("object.list", "Every object with id, type, parent, bounds and flags. Paged: a real-world " +
                           "document can hold thousands of objects, so prefer object.find when you know " +
                           "what you are looking for.",
            "includeHidden?:bool, max?:number (default 200), offset?:number",
            (ctx, p) =>
            {
                bool hidden = p.GetBool("includeHidden", false);
                LayerItem[] all = AllItems(ctx.Document)
                    .Where(i => hidden || i.IsEffectivelyVisible())
                    .ToArray();

                int max = (int)Math.Clamp(p.GetLong("max", 200), 1, 2000);
                int offset = (int)Math.Clamp(p.GetLong("offset", 0), 0, Math.Max(0, all.Length));
                var page = all.Skip(offset).Take(max).Select(DescribeOne).ToArray();

                return new
                {
                    count = all.Length,
                    returned = page.Length,
                    offset,
                    truncated = offset + page.Length < all.Length,
                    hint = all.Length > page.Length ? "use object.find or page with offset/max" : null,
                    items = page,
                };
            });

        Add("object.find",
            "Find objects by name, text content, type and/or layer — the way to turn a description " +
            "like 'the text UPPER CUP' into an id.",
            "name?:string, text?:string, type?:path|text|group|image, layerName?:string, max?:number",
            (ctx, p) =>
            {
                string? name = p.GetString("name");
                string? text = p.GetString("text");
                string? type = p.GetString("type");
                string? layerName = p.GetString("layerName");
                int max = (int)Math.Clamp(p.GetLong("max", 50), 1, 500);

                var matches = AllItems(ctx.Document)
                    .Where(i => name is null || i.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
                    .Where(i => layerName is null ||
                                (i.OwningLayer()?.Name.Contains(layerName, StringComparison.OrdinalIgnoreCase) ?? false))
                    .Where(i => type is null || ItemKind(i).Equals(type, StringComparison.OrdinalIgnoreCase))
                    .Where(i => text is null ||
                                (i is TextItem t && t.PlainText.Contains(text, StringComparison.OrdinalIgnoreCase)))
                    .Take(max)
                    .Select(DescribeOne)
                    .ToArray();

                return new { count = matches.Length, items = matches };
            });

        Add("object.create",
            "Create a shape. type is rectangle|ellipse|line|polygon|polyline. Coordinates are in document space.",
            "type:string, x?,y?,width?,height?,cx?,cy?,rx?,ry?,x1?,y1?,x2?,y2?,points?:[x,y][], name?, fillColor?:[r,g,b], strokeColor?:[r,g,b], strokeWidth?:number, layerId?:string",
            CreateObject);

        Add("object.delete", "Delete the selected objects.", "", (ctx, _) =>
        {
            ctx.Session.DeleteSelection();
            return Summary(ctx);
        });

        Add("object.deleteIds", "Delete objects by id.", "itemIds:guid[]",
            (ctx, p) =>
            {
                var items = p.GetGuidArray("itemIds").Select(id => RequireItem(ctx.Document, id)).ToArray();
                ctx.Session.SelectRange(items, additive: false);
                ctx.Session.DeleteSelection();
                return Summary(ctx);
            });

        Add("object.moveToLayer",
            "Move objects onto another layer, as dragging them in the Layers panel does. This " +
            "is how an object comes to belong to a different artboard, and without it a driver " +
            "could not put an object on a second page at all - which is why the cross-page " +
            "selection rules could not be checked in the running application.",
            "layerId:guid, itemIds?:guid[] (defaults to the selection)",
            (ctx, p) =>
            {
                Guid layerId = p.RequireGuid("layerId");
                Layer? target = ctx.Document.Artboards
                    .SelectMany(a => a.Layers)
                    .FirstOrDefault(l => l.Id == layerId)
                    ?? throw new EditorOperationException($"No layer with id '{layerId}'.");

                IReadOnlyList<LayerItem> items = p.TryGetGuidArray("itemIds", out Guid[] ids)
                    ? ids.Select(id => RequireItem(ctx.Document, id)).ToArray()
                    : ctx.Session.SelectedObjects.ToArray();

                if (items.Count == 0)
                {
                    throw new EditorOperationException(
                        "Nothing to move: pass itemIds, or select something first.");
                }

                ctx.ViewModel.MoveItems(items, target, target.Children.Count);
                return new
                {
                    layer = target.Name,
                    artboard = target.Artboard?.Name,
                    moved = items.Count,
                    itemIds = items.Select(i => i.Id).ToArray(),
                };
            });

        Add("object.move", "Move the selection (or given items) by a delta. Objects moved off " +
            "the page they were on are rehomed to the page that now holds them, or to the " +
            "pasteboard; with no pointer to hover with, the END of the translation vector " +
            "decides, measured from the centre of the selection.",
            "dx:number, dy:number, itemIds?:guid[]",
            (ctx, p) =>
            {
                if (p.TryGetGuidArray("itemIds", out Guid[] ids))
                {
                    ctx.Session.SelectRange(ids.Select(id => RequireItem(ctx.Document, id)).ToArray(), additive: false);
                }

                LayerItem[] moving = ctx.Session.SelectedObjects.ToArray();
                Point2D centreBefore = SelectionEngine.CentreOf(moving);
                var delta = new Vector2D(p.GetDouble("dx", 0), p.GetDouble("dy", 0));

                ApplyTransform(ctx.Session, delta, 1, 1, 0);

                IItemContainer? target = SelectionEngine.RehomeTarget(
                    ctx.Document, moving, null, centreBefore, delta);

                if (target is not null)
                {
                    ctx.ViewModel.MoveItems(
                        moving.Where(i => !ReferenceEquals(i.Container, target)).ToArray(),
                        target,
                        target.Children.Count);
                }

                return Summary(ctx);
            });

        Add("object.setPosition", "Move the selection's top-left to (x, y) in document space.",
            "x:number, y:number, itemIds?:guid[]",
            (ctx, p) =>
            {
                if (p.TryGetGuidArray("itemIds", out Guid[] ids))
                {
                    ctx.Session.SelectRange(ids.Select(id => RequireItem(ctx.Document, id)).ToArray(), additive: false);
                }

                Rect2D bounds = ctx.Session.SelectionBounds();
                if (bounds.IsEmpty)
                {
                    throw new EditorOperationException("Nothing is selected.");
                }

                ApplyTransform(ctx.Session,
                    new Vector2D(p.GetDouble("x", bounds.X) - bounds.X, p.GetDouble("y", bounds.Y) - bounds.Y), 1, 1, 0);
                return Summary(ctx);
            });

        Add("object.transform",
            "Transform the selection about a pivot. Angles in degrees.",
            "pivotX?,pivotY?,translateX?,translateY?,scaleX?,scaleY?,rotationDegrees?,ownOnly?:bool " +
            "(true acts on the selected objects alone, as the platform modifier does)",
            (ctx, p) =>
            {
                Rect2D bounds = ctx.Session.SelectionBounds();
                double px = p.GetDouble("pivotX", bounds.IsEmpty ? 0 : bounds.Center.X);
                double py = p.GetDouble("pivotY", bounds.IsEmpty ? 0 : bounds.Center.Y);
                ApplyTransform(
                    ctx.Session,
                    new Vector2D(p.GetDouble("translateX", 0), p.GetDouble("translateY", 0)),
                    p.GetDouble("scaleX", 1),
                    p.GetDouble("scaleY", 1),
                    p.GetDouble("rotationDegrees", 0),
                    new Point2D(px, py),
                    p.GetBool("ownOnly", false));
                return Summary(ctx);
            });

        Add("object.group", "Group the selected objects.", "", (ctx, _) =>
        {
            ctx.Session.GroupSelection();
            return Summary(ctx);
        });

        Add("object.ungroup", "Ungroup the selected groups.", "", (ctx, _) =>
        {
            ctx.Session.UngroupSelection();
            return Summary(ctx);
        });

        Add("object.arrange", "Reorder by z-order: front|back|forward|backward.",
            "action:string, itemIds?:guid[]",
            (ctx, p) => Arrange(ctx, p));

        Add("object.setVisible", "Show or hide an object.", "itemId:guid, visible:bool",
            (ctx, p) =>
            {
                LayerItem item = RequireItem(ctx.Document, p.RequireGuid("itemId"));
                item.IsVisible = p.GetBool("visible", true);
                ctx.ViewModel.NotifyDocumentChanged();
                return DescribeOne(item);
            });

        Add("ui.list",
            "The entries a list-backed control is showing: a combo box, a list box, a tree. A " +
            "dropdown's rows live in a Popup, which is outside the window's visual tree, so " +
            "ui.find and a screenshot both miss them - and a list nobody can read is a list " +
            "nobody can check.",
            "name?:string, type?:string, handle?:number, max?:number (default 200)",
            (ctx, p) =>
            {
                Avalonia.Visual root = Root(ctx);
                var target = ResolveControl(ctx, (Avalonia.Controls.Control)root, p) as Avalonia.Controls.Control
                    ?? throw new EditorOperationException("No list control matched.");

                int max = p.TryGetProperty("max", out JsonElement mv) && mv.TryGetInt32(out int m)
                    ? Math.Clamp(m, 1, 5000)
                    : 200;

                object[] items = target switch
                {
                    Avalonia.Controls.ComboBox combo => combo.ItemsSource?.Cast<object>()
                        .Select(DescribeChoice)
                        .ToArray() ?? Array.Empty<object>(),

                    Avalonia.Controls.ListBox list => list.ItemsSource?.Cast<object>()
                        .Select(DescribeChoice)
                        .ToArray() ?? Array.Empty<object>(),

                    _ => throw new EditorOperationException(
                        $"{target.GetType().Name} is not a list control."),
                };

                return new
                {
                    type = target.GetType().Name,
                    name = target.Name,
                    count = items.Length,
                    shown = Math.Min(items.Length, max),
                    open = (target as Avalonia.Controls.ComboBox)?.IsDropDownOpen ?? false,
                    items = items.Take(max).ToArray(),
                };
            });

        Add("ui.menu",
            "Open the context menu a control carries, as right-clicking it does. Synthetic " +
            "pointer events do not reach the handlers Avalonia opens menus from - a right " +
            "click is delivered but the menu stays shut - so this is how a driver opens the " +
            "menu a person would. Use ui.menuItem to choose from it.",
            "handle?:number, name?:string",
            (ctx, p) =>
            {
                Avalonia.Visual root = Root(ctx);
                var target = ResolveControl(ctx, (Avalonia.Controls.Control)root, p)
                    as Avalonia.Controls.Control
                    ?? throw new EditorOperationException("No control matched.");

                Avalonia.Controls.ContextMenu? menu = target.ContextMenu;
                if (menu is null)
                {
                    throw new EditorOperationException(
                        $"{target.GetType().Name} carries no context menu.");
                }

                menu.Open(target);

                // Kept, because a ContextMenu lives in a popup host rather than the window's
                // visual tree: it cannot be found by walking the tree, so the menu that was
                // opened is the only handle on it.
                OpenMenu = menu;

                return new
                {
                    opened = menu.IsOpen,
                    target = target.Name ?? target.GetType().Name,
                    items = menu.Items.OfType<Avalonia.Controls.MenuItem>()
                        .Select(i => i.Header?.ToString())
                        .Where(h => h is not null)
                        .ToArray(),
                };
            });

        Add("ui.menuItem",
            "Choose an item from the open context menu by its header, as clicking it does.",
            "text:string",
            (ctx, p) =>
            {
                string text = p.GetString("text")
                    ?? throw new EditorOperationException("Parameter 'text' is required.");

                Avalonia.Controls.MenuItem? found = OpenMenu is { IsOpen: true } menu
                    ? menu.Items.OfType<Avalonia.Controls.MenuItem>()
                        .FirstOrDefault(i => (i.Header?.ToString() ?? string.Empty).StartsWith(
                            text, StringComparison.OrdinalIgnoreCase))
                    : null;

                if (found is null)
                {
                    throw new EditorOperationException(
                        $"No open menu has an item starting \u201c{text}\u201d. Open one with ui.menu.");
                }

                found.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(
                    Avalonia.Controls.MenuItem.ClickEvent));

                return new { clicked = found.Header?.ToString() };
            });

        Add("ui.popups",
            "Every popup in the window and whether it is open. A menu, a tooltip and a combo " +
            "box dropdown are Popups rather than Windows: they are outside the window's visual " +
            "tree, so ui.find cannot read their contents and a screenshot cannot see them at " +
            "all. Whether one is open is still answerable, which is the difference between " +
            "knowing a menu opened and guessing that it did.",
            "",
            (ctx, _) =>
            {
                Avalonia.Visual root = Root(ctx);

                var popups = UiAutomation.Flatten(root)
                    .OfType<Avalonia.Controls.Primitives.Popup>()
                    .Select(p => new
                    {
                        name = p.Name,
                        open = p.IsOpen,
                        child = p.Child?.GetType().Name,
                        // How many controls the popup holds, so an empty menu can be told from
                        // a populated one without being able to read it.
                        items = p.Child is null
                            ? 0
                            : UiAutomation.Flatten(p.Child)
                                .OfType<Avalonia.Controls.Control>().Count(),
                    })
                    .ToArray();

                return new
                {
                    count = popups.Length,
                    open = popups.Count(p => p.open),
                    popups,
                };
            });

        Add("ui.windows",
            "Every top-level window the application has open, with its title and kind. Menus, " +
            "tooltips and combo-box dropdowns are separate top-level windows in Avalonia, so " +
            "they are not in the main window's visual tree and neither ui.find nor a " +
            "screenshot of the window can see them. This is what makes a popup checkable.",
            "",
            (ctx, _) =>
            {
                var windows = new List<object>();

                if (Avalonia.Application.Current?.ApplicationLifetime
                    is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
                {
                    foreach (Avalonia.Controls.Window window in desktop.Windows)
                    {
                        windows.Add(new
                        {
                            title = window.Title,
                            type = window.GetType().Name,
                            visible = window.IsVisible,
                            active = window.IsActive,
                            x = Math.Round((double)window.Position.X),
                            y = Math.Round((double)window.Position.Y),
                            width = Math.Round(window.Bounds.Width),
                            height = Math.Round(window.Bounds.Height),
                        });
                    }
                }

                return new { count = windows.Count, windows };
            });

        Add("object.setLocked", "Lock or unlock an object.", "itemId:guid, locked:bool",
            (ctx, p) =>
            {
                LayerItem item = RequireItem(ctx.Document, p.RequireGuid("itemId"));
                item.IsLocked = p.GetBool("locked", true);
                ctx.ViewModel.NotifyDocumentChanged();
                return DescribeOne(item);
            });

        Add("object.explorer",
            "Every row the Layers panel shows, in the order it shows them, with the depth the " +
            "panel draws its vertical rules from. This is the same traversal the panel uses, so " +
            "what a driver reads here is what a person sees - a panel that cannot be read " +
            "without pixels cannot be checked.",
            "max?:number (default 500), group?:string (a page or layer name)",
            (ctx, p) =>
            {
                int max = p.TryGetProperty("max", out JsonElement mv) && mv.TryGetInt32(out int m)
                    ? Math.Clamp(m, 1, 20000)
                    : 500;

                string? group = p.GetString("group");
                IReadOnlyList<LayerRow> all = LayerTree.Rows(ctx.Document);
                List<LayerRow> rows = all.ToList();

                if (group is not null)
                {
                    rows = SubTree(all, group)
                        ?? throw new EditorOperationException(
                            $"No page or layer named '{group}'.");
                }

                var listed = rows.Take(max).Select(r => new
                {
                    depth = r.Depth,
                    kind = r.Kind.ToString().ToLowerInvariant(),
                    label = r.Label,
                    itemId = r.ItemId,
                    userNamed = r.IsUserNamed,
                    visible = r.Visible,
                    expandable = r.Expandable,
                    children = r.ChildCount,
                }).ToArray();

                return new
                {
                    rows = listed,
                    count = rows.Count,
                    total = all.Count,
                    shown = listed.Length,
                    truncated = rows.Count > listed.Length,
                };
            });

        Add("object.rename",
            "Rename an object, as the Layers panel's context menu does. The name is then the " +
            "person's: it is shown instead of the one derived from the object's geometry, and " +
            "it stays put when that geometry changes.",
            "itemId:guid, name:string",
            (ctx, p) =>
            {
                LayerItem item = RequireItem(ctx.Document, p.RequireGuid("itemId"));
                string? name = p.GetString("name");

                if (name is not null && !string.Equals(name, item.Name, StringComparison.Ordinal))
                {
                    // Through the command stack, so a rename can be undone like any other edit.
                    ctx.ViewModel.Execute(new RenameItemCommand(item, name));
                }

                ctx.ViewModel.NotifyDocumentChanged();
                return DescribeOne(item);
            });

        // ---- styling -----------------------------------------------------
        Add("style.setFill", "Fill the selected paths.", "color:[r,g,b] (0-255), rule?:nonzero|evenodd",
            (ctx, p) =>
            {
                ColorRgb color = p.ParseColor("color", ColorRgb.Black);
                FillRule rule = string.Equals(p.GetString("rule"), "evenodd", StringComparison.OrdinalIgnoreCase)
                    ? FillRule.EvenOdd
                    : FillRule.NonZero;
                ctx.Session.ApplyFill(color, rule);
                return Summary(ctx);
            });

        Add("style.clearFill", "Remove the fill from the selected paths.", "", (ctx, _) =>
        {
            ctx.Session.ClearFill();
            return Summary(ctx);
        });

        Add("style.setStroke", "Stroke the selected paths.",
            "color:[r,g,b], width:number, cap?:butt|round|square, join?:miter|round|bevel, miterLimit?, alignment?:center|inside|outside, dash?:number[]",
            (ctx, p) =>
            {
                ColorRgb color = p.ParseColor("color", ColorRgb.Black);
                StrokeCap cap = ParseEnum(p.GetString("cap"), StrokeCap.Butt);
                StrokeJoin join = ParseEnum(p.GetString("join"), StrokeJoin.Miter);
                StrokeAlignment alignment = ParseEnum(p.GetString("alignment"), StrokeAlignment.Center);
                DashPattern? dash = null;
                if (p.TryGetProperty("dash", out JsonElement d) && d.ValueKind == JsonValueKind.Array)
                {
                    double[] pattern = d.EnumerateArray().Select(e => e.GetDouble()).ToArray();
                    if (pattern.Length > 0)
                    {
                        dash = new DashPattern(pattern);
                    }
                }

                ctx.Session.ApplyStroke(p.GetDouble("width", 1), cap, join, p.GetDouble("miterLimit", 4), alignment, dash);
                return Summary(ctx);
            });

        Add("style.clearStroke", "Remove the stroke from the selected paths.", "", (ctx, _) =>
        {
            ctx.Session.ClearStroke();
            return Summary(ctx);
        });

        // ---- text --------------------------------------------------------
        Add("text.create", "Create a text object at (x, y).", "x:number, y:number, text:string, family?, fontSize?, color?:[r,g,b]",
            (ctx, p) =>
            {
                var point = new Point2D(p.GetDouble("x"), p.GetDouble("y"));
                TextItem item = ctx.Session.CreateTextAt(point, p.GetString("family") ?? TextItem.DefaultFontFamily,
                    p.GetDouble("fontSize", 12));
                item.PlainText = p.GetString("text") ?? string.Empty;
                if (p.TryGetColorArray("color", out ColorRgb color))
                {
                    item.Color = color;
                }

                ctx.ViewModel.NotifyDocumentChanged();
                return DescribeOne(item);
            });

        Add("text.update", "Update the selected text objects.",
            "text?:string, family?, fontSize?, bold?:bool, italic?:bool, color?:[r,g,b]",
            (ctx, p) =>
            {
                TextItem? first = ctx.Session.SelectedTextItems().FirstOrDefault();
                if (first is null)
                {
                    throw new EditorOperationException("No text object is selected.");
                }

                TextRun run = first.Runs.FirstOrDefault() ?? new TextRun();
                string family = p.GetString("family") ?? run.FontFamily;

                ctx.Session.UpdateSelectedText(
                    p.GetString("text") ?? first.PlainText,
                    family,
                    p.GetDouble("fontSize", run.FontSize),
                    p.GetBool("bold", run.Bold),
                    p.GetBool("italic", run.Italic),
                    p.TryGetColorArray("color", out ColorRgb color) ? color : first.Color);

                // Choosing a font is what makes it recent, so the picker's "recent" list is a
                // record of what was actually used rather than of what was scrolled past.
                FontFavourites.Shared.Used(family);
                return Summary(ctx);
            });

        Add("text.setAlignment", "Set text alignment: left|center|right.", "alignment:string",
            (ctx, p) =>
            {
                ctx.Session.SetTextAlignment(ParseEnum(p.GetString("alignment"), TextAlignment.Left));
                return Summary(ctx);
            });

        Add("text.runs",
            "The text runs of the selected text object: each run's characters, face, " +
            "size, weight, slant and colour. How to see whether styling part of a " +
            "selection changed only that part, or reflowed the whole block.",
            "itemId?:guid (default: selected text)",
            (ctx, p) =>
            {
                LayerItem? target = p.TryGetProperty("itemId", out JsonElement iv) &&
                                    Guid.TryParse(iv.GetString(), out Guid id)
                    ? FindItem(ctx.Document, id)
                    : ctx.ViewModel.SelectedTextItems().FirstOrDefault();

                if (target is not TextItem text)
                {
                    throw new EditorOperationException("No text object is selected.");
                }

                // The measured width is reported, not just the characters, so whether text
                // is actually being measured can be checked from outside rather than
                // taken on trust. A guessed width would show as measured=false.
                return new
                {
                    measured = VCCad.Core.Text.TextMeasurement.IsReal,
                    runs = text.Runs.Select(r => new
                    {
                        text = r.Text,
                        family = r.FontFamily,
                        size = r.FontSize,
                        bold = r.Bold,
                        italic = r.Italic,
                        advance = Math.Round(
                            VCCad.Core.Text.TextMeasurement.Advances(r).Sum(), 4),
                        ascent = Math.Round(VCCad.Core.Text.TextMeasurement.Ascent(r), 4),
                        descent = Math.Round(VCCad.Core.Text.TextMeasurement.Descent(r), 4),
                    }).ToArray(),
                    plain = text.PlainText,
                    caret = ctx.ViewModel.TextCaretRunIndex,
                    selectionStart = ctx.ViewModel.TextSelectionStart,
                    selectionEnd = ctx.ViewModel.TextSelectionEnd,
                };
            });

        Add("text.caret",
            "Where the caret and selection are in the text block being edited: the run " +
            "index and the selected character range. How to tell that clicking placed the " +
            "caret where it was aimed and that dragging selected something.",
            "",
            (ctx, _) => new
            {
                editing = ctx.ViewModel.IsEditingText,
                runIndex = ctx.ViewModel.TextCaretRunIndex,
                selectionStart = ctx.ViewModel.TextSelectionStart,
                selectionEnd = ctx.ViewModel.TextSelectionEnd,
                selected = ctx.ViewModel.TextSelectionEnd - ctx.ViewModel.TextSelectionStart,
            });

        Add("view.toScreen",
            "Convert between model and window coordinates through the live canvas. A " +
            "driver that cannot see needs this to aim the pointer: guessing the viewport " +
            "centre puts clicks well off target.",
            "x:number, y:number, direction?:modelToScreen|screenToModel (default modelToScreen)",
            (ctx, p) =>
            {
                VCCad.App.Controls.CanvasWorkspace? canvas = Workspace(ctx);
                if (canvas is null)
                {
                    throw new EditorOperationException("No canvas is attached.");
                }

                double x = p.TryGetProperty("x", out JsonElement xv) ? xv.GetDouble() : 0;
                double y = p.TryGetProperty("y", out JsonElement yv) ? yv.GetDouble() : 0;
                string direction = p.TryGetProperty("direction", out JsonElement dv)
                    ? dv.GetString() ?? "modelToScreen"
                    : "modelToScreen";

                if (direction == "screenToModel")
                {
                    var model = canvas.WindowToModel(new Avalonia.Point(x, y));
                    return new { x = model.X, y = model.Y };
                }

                Avalonia.Point screen = canvas.ModelToWindow(new VCCad.Geometry.Point2D(x, y));
                return new { x = screen.X, y = screen.Y };
            });

        Add("text.style",
            "Set paragraph style and orientation on the selected text: leading, space " +
            "between paragraphs, the angle the block sits at, the width it wraps in, and " +
            "alignment. One undo step. These change how the block is set, not what it says.",
            "lineSpacing?:number (multiple of font size), paragraphSpacing?:number, " +
            "rotationDegrees?:number, frameWidth?:number (0 = auto), alignment?:left|center|right",
            (ctx, p) =>
            {
                static double? Number(JsonElement parent, string name)
                    => parent.TryGetProperty(name, out JsonElement value) &&
                       value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double parsed)
                        ? parsed
                        : null;

                ctx.Session.ApplyTextStyle(
                    Number(p, "lineSpacing"),
                    Number(p, "paragraphSpacing"),
                    Number(p, "rotationDegrees"),
                    Number(p, "frameWidth"),
                    p.TryGetProperty("alignment", out JsonElement al) && al.ValueKind == JsonValueKind.String
                        ? ParseEnum(al.GetString(), TextAlignment.Left)
                        : null);
                return Summary(ctx);
            });

        Add("text.edit",
            "Open a text block for editing, as double-clicking into it does. Needed before " +
            "text.select, and therefore before styling part of a selection: the range is " +
            "only meaningful while the block is open.",
            "itemId?:guid (default: selected text)",
            (ctx, p) =>
            {
                LayerItem? found = p.TryGetProperty("itemId", out JsonElement iv) &&
                                   Guid.TryParse(iv.GetString(), out Guid id)
                    ? FindItem(ctx.Document, id)
                    : ctx.ViewModel.SelectedTextItems().FirstOrDefault();

                if (found is not TextItem target)
                {
                    throw new EditorOperationException("No text object is selected.");
                }

                VCCad.App.Controls.CanvasWorkspace? canvas = Workspace(ctx);
                if (canvas is null)
                {
                    throw new EditorOperationException("No canvas is attached.");
                }

                if (!canvas.BeginTextEdit(target))
                {
                    throw new EditorOperationException("The canvas could not open the text block.");
                }

                ctx.ViewModel.NotifyDocumentChanged();
                return new
                {
                    editing = ctx.ViewModel.IsEditingText,
                    length = VCCad.Core.Model.TextEditing.Length(target),
                    runs = target.Runs.Count,
                };
            });

        Add("text.select",
            "Place the caret and selection inside the open text block, as dragging across " +
            "the text does. The range is in flattened characters, so a caller can select " +
            "exactly what it measured and then style that part alone.",
            "start:number, end:number",
            (ctx, p) =>
            {
                VCCad.App.Controls.CanvasWorkspace? canvas = Workspace(ctx);
                if (canvas is null)
                {
                    throw new EditorOperationException("No canvas is attached.");
                }

                int start = (int)p.GetDouble("start", 0);
                int end = (int)p.GetDouble("end", start);
                if (!canvas.SetTextSelection(start, end))
                {
                    throw new EditorOperationException(
                        "No text block is open; call text.edit first.");
                }

                return new
                {
                    editing = ctx.ViewModel.IsEditingText,
                    selectionStart = ctx.ViewModel.TextSelectionStart,
                    selectionEnd = ctx.ViewModel.TextSelectionEnd,
                    selected = ctx.ViewModel.TextSelectionEnd - ctx.ViewModel.TextSelectionStart,
                };
            });

        Add("text.centerIn",
            "Centre a text block inside a rectangle. With no target it finds the enclosing artwork " +
            "automatically (the pattern piece a label belongs to), falling back to the artboard — so " +
            "centre a label with just { itemId }. Other targets: item (targetItemId), rect (x/y/width/height).",
            "itemId?:guid (default: selected text), target?:enclosing|artboard|item|rect, targetItemId?:guid, " +
            "x?,y?,width?,height?",
            (ctx, p) => CenterText(ctx, p));

        // ---- layers ------------------------------------------------------
        Add("layer.list", "Layers per artboard.", "", (ctx, _) => ctx.Document.Artboards
            .Select(a => new
            {
                artboardId = a.Id,
                artboard = a.Name,
                layers = a.Layers.Select(l => new
                {
                    layerId = l.Id,
                    name = l.Name,
                    visible = l.IsVisible,
                    locked = l.IsLocked,
                    items = l.Children.Count,
                }).ToArray(),
            }).ToArray());

        Add("layer.add", "Add a layer to an artboard.", "artboardId?:guid, name?:string",
            (ctx, p) =>
            {
                Artboard artboard = p.TryGetGuid("artboardId", out Guid id)
                    ? RequireArtboard(ctx.Document, id)
                    : ctx.Document.Artboards.FirstOrDefault()
                      ?? throw new EditorOperationException("The document has no artboards.");
                Layer layer = artboard.AddLayer(p.GetString("name"));
                ctx.ViewModel.NotifyDocumentChanged();
                return new { layerId = layer.Id, name = layer.Name, artboardId = artboard.Id };
            });

        Add("layer.rename", "Rename a layer.", "layerId:guid, name:string",
            (ctx, p) =>
            {
                Layer layer = RequireLayer(ctx.Document, p.RequireGuid("layerId"));
                layer.Name = p.GetString("name") ?? layer.Name;
                ctx.ViewModel.NotifyDocumentChanged();
                return new { layerId = layer.Id, name = layer.Name };
            });

        Add("layer.setVisible", "Show or hide a layer.", "layerId:guid, visible:bool",
            (ctx, p) =>
            {
                Layer layer = RequireLayer(ctx.Document, p.RequireGuid("layerId"));
                layer.IsVisible = p.GetBool("visible", true);
                ctx.ViewModel.NotifyDocumentChanged();
                return new { layerId = layer.Id, visible = layer.IsVisible };
            });

        Add("layer.setLocked", "Lock or unlock a layer.", "layerId:guid, locked:bool",
            (ctx, p) =>
            {
                Layer layer = RequireLayer(ctx.Document, p.RequireGuid("layerId"));
                layer.IsLocked = p.GetBool("locked", true);
                ctx.ViewModel.NotifyDocumentChanged();
                return new { layerId = layer.Id, locked = layer.IsLocked };
            });

        Add("layer.onlyVisible",
            "Show only the named layers and hide every other layer — the bulk form of " +
            "\"hide all size layers except UK 6\". 'match' narrows which layers are touched " +
            "(e.g. \"UK\"), so unrelated layers such as Labels keep their state.",
            "keep:string[], match?:string",
            (ctx, p) =>
            {
                string[] keep = p.TryGetProperty("keep", out JsonElement keepElement) &&
                                keepElement.ValueKind == JsonValueKind.Array
                    ? keepElement.EnumerateArray()
                        .Where(e => e.ValueKind == JsonValueKind.String)
                        .Select(e => e.GetString()!)
                        .ToArray()
                    : throw new EditorOperationException("Parameter 'keep' must be an array of layer names.");

                string? match = p.GetString("match");
                var changed = new List<object>();

                foreach (Layer layer in ctx.Document.Artboards.SelectMany(a => a.Layers))
                {
                    if (match is not null && !layer.Name.Contains(match, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    bool visible = keep.Any(k => layer.Name.Equals(k, StringComparison.OrdinalIgnoreCase));
                    if (layer.IsVisible != visible)
                    {
                        layer.IsVisible = visible;
                    }

                    changed.Add(new { layerId = layer.Id, name = layer.Name, visible });
                }

                ctx.ViewModel.NotifyDocumentChanged();
                return new { layers = changed };
            });

        Add("layer.delete", "Delete a layer.", "layerId:guid",
            (ctx, p) =>
            {
                Guid id = p.RequireGuid("layerId");
                Layer layer = RequireLayer(ctx.Document, id);
                Artboard? artboard = ctx.Document.Artboards.FirstOrDefault(a => a.Layers.Contains(layer));
                if (artboard is null || !artboard.RemoveLayer(layer))
                {
                    throw new EditorOperationException($"Layer '{id}' could not be removed.");
                }

                ctx.ViewModel.NotifyDocumentChanged();
                return new { removed = true, layerId = id };
            });

        // ---- artboards ---------------------------------------------------
        Add("artboard.list", "Artboards with size, position and object counts.", "", (ctx, _) =>
            ctx.Document.Artboards.Select(a => new
            {
                artboardId = a.Id,
                name = a.Name,
                x = a.X,
                y = a.Y,
                width = a.Width,
                height = a.Height,
                layers = a.Layers.Count,
                items = a.Layers.Sum(l => l.Children.Count),
            }).ToArray());

        Add("artboard.add", "Add an artboard.", "width?, height?, x?, y?, name?",
            (ctx, p) =>
            {
                double width = p.GetDouble("width", PageSizes.A4Landscape.Width);
                double height = p.GetDouble("height", PageSizes.A4Landscape.Height);
                var rect = new Rect2D(p.GetDouble("x", 0), p.GetDouble("y", 0), width, height);
                Artboard artboard = ctx.Document.AddArtboard(new Size2D(width, height),
                    p.GetString("name"), new Point2D(rect.X, rect.Y));
                artboard.AddLayer("Layer 1");
                ctx.ViewModel.NotifyDocumentChanged();
                return new { artboardId = artboard.Id, name = artboard.Name, x = artboard.X, y = artboard.Y };
            });

        Add("artboard.setBounds", "Move/resize an artboard.", "artboardId:guid, x:number, y:number, width:number, height:number",
            (ctx, p) =>
            {
                Artboard artboard = RequireArtboard(ctx.Document, p.RequireGuid("artboardId"));
                Rect2D before = artboard.Bounds;
                var after = new Rect2D(
                    p.GetDouble("x", before.X),
                    p.GetDouble("y", before.Y),
                    Math.Max(1, p.GetDouble("width", before.Width)),
                    Math.Max(1, p.GetDouble("height", before.Height)));
                ctx.Session.SetArtboardBounds(artboard, before, after);
                return new { artboardId = artboard.Id, x = artboard.X, y = artboard.Y, width = artboard.Width, height = artboard.Height };
            });

        Add("artboard.delete", "Delete an artboard; keepObjects decides what happens to its content.",
            "artboardId:guid, keepObjects?:bool",
            (ctx, p) =>
            {
                Artboard artboard = RequireArtboard(ctx.Document, p.RequireGuid("artboardId"));
                ctx.Session.DeleteArtboard(artboard, p.GetBool("keepObjects", true)
                    ? ArtboardDeletionChoice.KeepObjects
                    : ArtboardDeletionChoice.DeleteObjects);
                return Summary(ctx);
            });

        // ---- paths -------------------------------------------------------
        Add("path.close", "Close the selected paths.", "", (ctx, _) =>
        {
            ctx.Session.CloseSelectedPaths();
            return Summary(ctx);
        });

        Add("path.join", "Join two selected open paths at a shared endpoint.", "", (ctx, _) =>
        {
            ctx.Session.JoinSelection();
            return Summary(ctx);
        });

        Add("path.insertNode", "Insert a node on a segment, near a point.",
            "itemId:guid, sub:number, segment:number, x:number, y:number",
            (ctx, p) =>
            {
                PathItem path = RequirePath(ctx.Document, p.RequireGuid("itemId"));
                ctx.Session.InsertPointOnSegment(path, (int)p.GetLong("sub", 0), (int)p.GetLong("segment", 0),
                    new Point2D(p.GetDouble("x"), p.GetDouble("y")));
                return DescribeOne(path);
            });

        Add("path.moveNode", "Move a path node to a point.",
            "itemId:guid, sub:number, node:number, x:number, y:number",
            (ctx, p) =>
            {
                PathItem path = RequirePath(ctx.Document, p.RequireGuid("itemId"));
                ctx.Session.SelectPoint(path, (int)p.GetLong("sub", 0), (int)p.GetLong("node", 0));
                ctx.Session.MovePointTo(new Point2D(p.GetDouble("x"), p.GetDouble("y")));
                ctx.Session.ClearPointSelection();
                return DescribeOne(path);
            });

        // ---- view / capture ---------------------------------------------
        Add("document.renderPage",
            "Render one page to PNG at a requested resolution, using the editor's own renderer. " +
            "Unlike a screen shot this depends on nothing but the page and the dpi, so it can be " +
            "diffed against another PDF engine's raster of the same page, repeatably.",
            "page:number (zero-based), dpi:number (72 = one pixel per point), path?:string",
            (ctx, p) =>
            {
                int page = p.TryGetProperty("page", out JsonElement pageValue) &&
                           pageValue.TryGetInt32(out int parsed)
                    ? parsed
                    : 0;
                double dpi = p.TryGetProperty("dpi", out JsonElement dpiValue) &&
                             dpiValue.TryGetDouble(out double parsedDpi)
                    ? parsedDpi
                    : 150.0;

                byte[]? png = Views.PageRenderer.Render(ctx.Document, page, dpi);
                if (png is null)
                {
                    throw new EditorOperationException(
                        $"No page {page} could be rendered (the document has " +
                        $"{ctx.Document.Artboards.Count} pages).");
                }

                string? path = p.TryGetProperty("path", out JsonElement pathValue)
                    ? pathValue.GetString()
                    : null;
                if (!string.IsNullOrWhiteSpace(path))
                {
                    File.WriteAllBytes(path!, png);
                }

                return new
                {
                    page,
                    dpi,
                    width = (int)Math.Round(ctx.Document.Artboards[page].Width * dpi / 72.0),
                    height = (int)Math.Round(ctx.Document.Artboards[page].Height * dpi / 72.0),
                    path,
                    pngBase64 = string.IsNullOrWhiteSpace(path) ? Convert.ToBase64String(png) : null,
                };
            });

        Add("capture.screenshot", "Render the workspace to PNG (base64) for visual inspection.", "",
            (ctx, _) =>
            {
                byte[]? png = ctx.Screenshot?.Invoke();
                if (png is null)
                {
                    throw new EditorOperationException("No window is available to render.");
                }

                return new { pngBase64 = Convert.ToBase64String(png), mediaType = "image/png" };
            });

        // ---- point-and-click automation (menus, toolbar, panes) ----------
        // Nothing may be reachable only through the UI: these drive the real
        // controls, so "File → Import" is scriptable just like object.create.
        Add("ui.find",
            "Find controls by type, name or displayed text. Returns handles to pass to ui.click/ui.setValue.",
            "type?:string, name?:string, text?:string, includeHidden?:bool, max?:number",
            (ctx, p) => FindControls(ctx, p));

        Add("ui.click",
            "Click a control: opens a menu, invokes a menu item or button, flips a toggle. " +
            "Select it with handle (from ui.find), or name/text/type.",
            "handle?:number, name?:string, text?:string, type?:string, index?:number",
            (ctx, p) => ActOnControl(ctx, p, "click"));

        Add("ui.setValue",
            "Set an input control's value as typing/choosing would. Same selectors as ui.click.",
            "value:string, handle?:number, name?:string, text?:string, type?:string, index?:number",
            (ctx, p) => ActOnControl(ctx, p, "set"));

        // ---- real input injection -------------------------------------------
        // Gestures cannot be cloned by calling an operation: the behaviour under test
        // *is* the gesture. These deliver actual pointer and keyboard events so text
        // editing - placing a caret, double-clicking a word, typing into a selection -
        // is exercised the way a person exercises it.
        Add("color.get",
            "The colour the editor is currently working in, in every form the picker shows: " +
            "RGB, hex, HSL and opacity, plus where the ring and the small white marker sit. " +
            "This is the same state the panel reads, so a colour set here is the colour a " +
            "person sees there.",
            "",
            (ctx, _) =>
            {
                ColorPickerSnapshot snap = EditorColorState.Shared.Model.Snapshot();

                return new
                {
                    r = Math.Round(snap.Color.R, 6),
                    g = Math.Round(snap.Color.G, 6),
                    b = Math.Round(snap.Color.B, 6),
                    hex = snap.Hex,
                    alpha = Math.Round(snap.Alpha, 4),
                    h = Math.Round(snap.Hue, 4),
                    s = Math.Round(snap.Saturation, 6),
                    l = Math.Round(snap.Lightness, 6),
                    angleDegrees = Math.Round(snap.AngleDegrees, 4),
                    markerX = Math.Round(snap.MarkerPoint.X, 3),
                    markerY = Math.Round(snap.MarkerPoint.Y, 3),
                };
            });

        Add("color.set",
            "Set the working colour from any one of its forms: hex, rgb, hsl or a ring angle. " +
            "Whatever a person can set in the picker, a driver can set here.",
            "hex?:string, r?,g?,b?:number (0..1), h?,s?,l? (degrees and 0..1), angle? (degrees), " +
            "alpha?:number (0..1)",
            (ctx, p) =>
            {
                EditorColorState state = EditorColorState.Shared;

                if (p.GetString("hex") is { Length: > 0 } hex)
                {
                    state.SetColor(HexColor.Parse(hex).WithAlpha(state.Alpha));
                }
                else if (p.TryGetProperty("r", out _) || p.TryGetProperty("g", out _) ||
                         p.TryGetProperty("b", out _))
                {
                    state.SetColor(new ColorRgb(
                        p.GetDouble("r", 0), p.GetDouble("g", 0), p.GetDouble("b", 0)));
                }
                else if (p.TryGetProperty("h", out _) || p.TryGetProperty("s", out _) ||
                         p.TryGetProperty("l", out _))
                {
                    state.SetColor(new HslColor(
                        p.GetDouble("h", 0), p.GetDouble("s", 0), p.GetDouble("l", 0))
                        .ToRgb(state.Alpha));
                }
                else if (p.TryGetProperty("angle", out _))
                {
                    state.SelectAngle(p.GetDouble("angle", 0));
                }

                if (p.TryGetProperty("alpha", out _))
                {
                    state.SetAlpha(p.GetDouble("alpha", 1));
                }

                return DescribeColor();
            });

        Add("color.pickInTriangle",
            "Click inside the triangle: selects the colour at that point and moves the marker " +
            "there. A point outside is pulled onto the nearest edge rather than ignored.",
            "x:number, y:number (relative to the ring's centre)",
            (ctx, p) =>
            {
                EditorColorState.Shared.SelectTrianglePoint(new VCCad.Geometry.Point2D(
                    p.GetDouble("x", 0), p.GetDouble("y", 0)));

                return DescribeColor();
            });

        Add("color.recent",
            "The recently used colours, newest first, or clears them. This is the two-column " +
            "swatch pad beside the ring.",
            "clear?:bool",
            (ctx, p) =>
            {
                EditorColorState state = EditorColorState.Shared;

                if (p.GetBool("clear", false))
                {
                    state.ClearRecent();
                }

                return new
                {
                    colours = state.Recent.Select(c => new
                    {
                        r = Math.Round(c.R, 6),
                        g = Math.Round(c.G, 6),
                        b = Math.Round(c.B, 6),
                        hex = HexColor.Format(c),
                    }).ToArray(),
                };
            });

        Add("color.remember",
            "Add a colour to the recently used swatches, as picking one does.",
            "r:number, g:number, b:number (0..1)",
            (ctx, p) =>
            {
                var color = new ColorRgb(
                    p.GetDouble("r", 0), p.GetDouble("g", 0), p.GetDouble("b", 0));

                EditorColorState.Shared.Remember(color);
                return new { hex = HexColor.Format(color), count = EditorColorState.Shared.Recent.Count };
            });

        Add("color.apply",
            "Apply the working colour to the selection as a fill or a stroke, so the colour " +
            "chosen in the picker reaches the artwork without clicking the fields.",
            "target:string (fill|stroke, default fill)",
            (ctx, p) =>
            {
                ColorRgb color = EditorColorState.Shared.Color;

                if (string.Equals(p.GetString("target"), "stroke", StringComparison.OrdinalIgnoreCase))
                {
                    ctx.Session.ApplyStrokeColor(color);
                }
                else
                {
                    ctx.Session.ApplyFill(color, FillRule.NonZero);
                }

                return Summary(ctx);
            });

        Add("input.batch",
            "Replay a whole session of input in one call: an ordered list of events, each with " +
            "a delta in milliseconds from the one before it. Every event goes through the same " +
            "injection path a person's events take, so a batch and a hand cannot diverge. This " +
            "is how a task like drawing by mouse and keyboard is carried out as gestures rather " +
            "than as operations. kind is one of down, move, up, wheel, hover, hover-out, enter, " +
            "leave, keydown, keyup, text, pen-down, pen-move, pen-up, touch-down, touch-move, " +
            "touch-up.",
            "events:[{kind,x,y,deltaMs,extend?,modifier?,button?,modifiers?,device?,pressure?," +
            "tiltX?,tiltY?,key?,text?,pointerId?,wheelDelta?}], fast?:bool (skip the waits)",
            (ctx, p) =>
            {
                Avalonia.Visual root = Root(ctx);

                if (!p.TryGetProperty("events", out JsonElement events) ||
                    events.ValueKind != JsonValueKind.Array)
                {
                    throw new EditorOperationException(
                        "Parameter 'events' is required and must be an array.");
                }

                var parsed = new List<InputEvent>();
                int index = 0;

                foreach (JsonElement element in events.EnumerateArray())
                {
                    // Case-insensitive: a driver writes "kind", the record calls it Kind, and
                    // the default options would not bind it - which the batch's own validation
                    // caught by naming every index rather than silently replaying nothing.
                    InputEvent? one = element.Deserialize<InputEvent>(InputJson);
                    if (one is null)
                    {
                        throw new EditorOperationException($"Event at index {index} is not an event.");
                    }

                    parsed.Add(one);
                    index++;
                }

                var batch = new InputBatch { Events = parsed };

                bool fast = p.GetBool("fast", false);
                InputReplayResult done;

                try
                {
                    done = batch.Replay(
                        InjectionInputSink.For(root),
                        fast ? InputTiming.AsFastAsPossible : InputTiming.RealTime);
                }
                catch (InputBatchException bad)
                {
                    // The index is the whole value of this: a driver has to be told which event
                    // was wrong, not merely that the batch was.
                    throw new EditorOperationException(bad.Message);
                }

                return new
                {
                    delivered = done.Delivered,
                    milliseconds = Math.Round(done.Duration.TotalMilliseconds, 1),
                    timing = done.Timing.ToString(),
                };
            });

        Add("input.pointer",
            "Click at a window coordinate with a real pointer event (x, y in window pixels). " +
            "Use clickCount 2 to double-click, which is how a word is selected for editing.",
            "x:number, y:number, clickCount?:number (default 1), shift?:bool, " +
            "button?:left|right (default left)",
            (ctx, p) =>
            {
                Avalonia.Visual root = Root(ctx);
                string mode = p.TryGetProperty("action", out JsonElement av)
                    ? av.GetString() ?? "click"
                    : "click";
                double px = p.TryGetProperty("x", out JsonElement xv) && xv.TryGetDouble(out double x) ? x : 0;
                double py = p.TryGetProperty("y", out JsonElement yv) && yv.TryGetDouble(out double y) ? y : 0;
                bool shift = p.TryGetProperty("shift", out JsonElement sv) && sv.ValueKind == JsonValueKind.True;

                // A right click is how a person opens a context menu, so the automation
                // surface has to be able to send one. A left-only injector made the whole
                // class of right-click actions person-only, which is a parity defect.
                bool right = string.Equals(p.GetString("button"), "right",
                    StringComparison.OrdinalIgnoreCase);

                string outcome = mode.ToLowerInvariant() switch
                {
                    "press" => InputInjection.Press(root, px, py, shift, right),
                    "move" => InputInjection.Move(root, px, py,
                        p.TryGetProperty("leftDown", out JsonElement ld) && ld.ValueKind == JsonValueKind.True),
                    "release" => InputInjection.Release(root, px, py, right),
                    _ => InputInjection.Click(root, px, py,
                        p.TryGetProperty("clickCount", out JsonElement cv) && cv.TryGetInt32(out int c) ? c : 1,
                        shift, right),
                };

                return new { action = outcome, mode };
            });

        Add("input.type",
            "Type text into the focused control, as the keyboard would. This is how text " +
            "gets into a text object once it is being edited.",
            "text:string, wpm?:number (0 = paste instantly, 50 = watch it type)",
            (ctx, p) =>
            {
                Avalonia.Visual root = Root(ctx);
                string text = p.TryGetProperty("text", out JsonElement tv) ? tv.GetString() ?? "" : "";

                // A wpm paces the typing character by character so it can be watched;
                // without it the whole string arrives in one event.
                if (p.TryGetProperty("wpm", out JsonElement wv) && wv.ValueKind == JsonValueKind.Number &&
                    wv.TryGetDouble(out double wpm) && wpm > 0)
                {
                    return new { action = InputInjection.TypePaced(root, text, wpm), text, wpm };
                }

                return new { action = InputInjection.Type(root, text), text };
            });

        Add("input.wheel",
            "Scroll at a window coordinate with a real wheel event.",
            "x:number, y:number, delta:number",
            (ctx, p) =>
            {
                Avalonia.Visual root = Root(ctx);
                return new
                {
                    action = InputInjection.Wheel(
                        root,
                        p.TryGetProperty("x", out JsonElement xv) && xv.TryGetDouble(out double x) ? x : 0,
                        p.TryGetProperty("y", out JsonElement yv) && yv.TryGetDouble(out double y) ? y : 0,
                        p.TryGetProperty("delta", out JsonElement dv) && dv.TryGetDouble(out double d) ? d : -1),
                };
            });

        Add("ui.keys", "Press a keyboard shortcut on the window, e.g. \"F12\" or \"Ctrl+S\".", "keys:string",
            (ctx, p) =>
            {
                Avalonia.Controls.Control root = RequireUiRoot(ctx);
                string keys = p.GetString("keys")
                    ?? throw new EditorOperationException("Parameter 'keys' is required.");
                return new { result = UiAutomation.PressKeys(root, keys) };
            });

        // ---- files without a dialog -------------------------------------
        // The native file picker is the one thing a headless driver cannot operate,
        // so the import/export *effects* are exposed directly as well.
        Add("document.openFile", "Open a PDF from disk as a new document (no dialog).", "path:string",
            (ctx, p) =>
            {
                string path = RequireExistingFile(p, "path");
                ctx.ViewModel.ImportPdf(File.ReadAllBytes(path));
                return new { opened = path, document = ctx.Document.Name, artboards = ctx.Document.Artboards.Count };
            });

        Add("document.savePdfToFile", "Write the active document's PDF to disk (no dialog).", "path:string",
            (ctx, p) =>
            {
                string path = p.GetString("path")
                    ?? throw new EditorOperationException("Parameter 'path' is required.");
                byte[] pdf = ctx.ViewModel.ExportPdf();
                string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllBytes(path, pdf);
                return new { saved = path, bytes = pdf.Length };
            });

        // ---- viewport ----------------------------------------------------
        Add("view.fit", "Fit the artboard(s) into the visible workspace.", "",
            (ctx, _) =>
            {
                RequireViewport(ctx).Fit();
                return ViewStatus(ctx);
            });

        Add("view.actualSize", "Zoom to 100%.", "",
            (ctx, _) =>
            {
                RequireViewport(ctx).ActualSize();
                return ViewStatus(ctx);
            });

        Add("view.zoom", "Set an absolute zoom factor (1.0 = 100%).", "factor:number",
            (ctx, p) =>
            {
                double factor = p.GetDouble("factor", 1.0);
                if (factor is <= 0 or > 64)
                {
                    throw new EditorOperationException("factor must be between 0 and 64.");
                }

                RequireViewport(ctx).Zoom(factor);
                return ViewStatus(ctx);
            });

        Add("view.status", "Current zoom and whether the view is auto-fitting.", "",
            (ctx, _) => ViewStatus(ctx));

        Add("view.zoomIn", "Zoom in one step (the toolbar's + button).", "",
            (ctx, _) =>
            {
                RequireViewport(ctx).ZoomIn();
                return ViewStatus(ctx);
            });

        Add("view.zoomOut", "Zoom out one step (the toolbar's − button).", "",
            (ctx, _) =>
            {
                RequireViewport(ctx).ZoomOut();
                return ViewStatus(ctx);
            });

        Add("view.clipToArtboard",
            "Whether each page clips its own content to the page box, as a PDF viewer does. " +
            "On by default: an imported tiled document draws full-size artwork on every sheet and " +
            "relies on the page edge to cut it. Turn it off to see the overflow.",
            "enabled?:bool (omit to toggle)",
            (ctx, p) =>
            {
                ViewportActions viewport = RequireViewport(ctx);
                bool current = viewport.GetClipToArtboard();
                bool enabled = p.TryGetProperty("enabled", out JsonElement e) &&
                               e.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? e.GetBoolean()
                    : !current;

                viewport.SetClipToArtboard(enabled);
                return new { clipToArtboard = enabled };
            });

        Add("view.centerOn",
            "Scroll the view so a model point (in PDF points, document space) is in the " +
            "middle of the visible area — the API equivalent of dragging the scrollbars. " +
            "Combine with view.zoom to inspect a detail.",
            "x:number, y:number",
            (ctx, p) =>
            {
                ViewportActions viewport = RequireViewport(ctx);
                double x = p.GetDouble("x");
                double y = p.GetDouble("y");
                viewport.CenterOn(x, y);
                return new
                {
                    centered = new { x, y },
                    zoom = Math.Round(viewport.GetZoom(), 4),
                };
            });

        Add("view.center", "The model point currently in the middle of the view.", "",
            (ctx, _) =>
            {
                (double x, double y) = RequireViewport(ctx).GetViewCenter();
                return new { x = Math.Round(x, 2), y = Math.Round(y, 2) };
            });

        // ---- tools and the shell -----------------------------------------
        Add("tool.list", "The editor tools (select, node, pen, rectangle, ellipse, artboard, text).", "",
            (_, _) => Enum.GetNames<EditorTool>().Select(t => t.ToLowerInvariant()).ToArray());

        AddAsync("fonts.installStandard",
            "Download and install the URW base-35 fonts into this user's font directory. They supply " +
            "the standard PDF fonts (Helvetica, Times, Courier, Symbol, ZapfDingbats) with their original " +
            "metrics, and are installed for this user rather than shipped with the application because " +
            "their licence permits embedding in documents, not redistribution in a product.",
            "faces?:string[] (e.g. [\"sans\",\"serif\",\"mono\"], default all)",
            async (ctx, p, ct) =>
            {
                string[] wanted = p.TryGetProperty("faces", out JsonElement faces) &&
                                  faces.ValueKind == JsonValueKind.Array
                    ? faces.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String)
                        .Select(e => e.GetString()!).ToArray()
                    : Array.Empty<string>();

                IEnumerable<StandardFace> selection = wanted.Length == 0
                    ? StandardFontResolver.AllFaces()
                    : StandardFontResolver.AllFaces().Where(f => wanted.Any(w => MatchesKind(f.Kind, w)))
                        .ToArray();

                if (!selection.Any())
                {
                    throw new ArgumentException(
                        $"faces must name a family: sans, serif, mono, symbol or dingbats (got " +
                        $"{string.Join(", ", wanted)}).");
                }

                IReadOnlyList<string> installed = await StandardFontResolver
                    .InstallAsync(selection, cancellationToken: ct).ConfigureAwait(false);

                // Registering a programme touches Avalonia's font manager, which belongs
                // to the UI thread; the download above deliberately does not.
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    StandardFontResolver.Invalidate();
                    StandardFontResolver.RegisterAvailable();
                    ctx.ViewModel.NotifyDocumentChanged();
                });
                return new
                {
                    installed,
                    directory = StandardFontFiles.UserFontDirectory,
                    missingAfterInstall = StandardFontResolver.Missing(ctx.Document),
                };
            });

        Add("fonts.list",
            "Every font the active document uses, whether its programme is embedded in the file, " +
            "and whether the canvas is drawing with it. A font that is embedded but not resolved, " +
            "or a font that had to be substituted, is a fidelity problem the driver should know about.",
            "",
            (ctx, _) => FontReport(ctx));

        Add("fonts.families",
            "The families the font chooser offers, each with the faces it really has and how " +
            "many there are. This is the picker's own list, so a driver can check what the " +
            "chooser shows without seeing it - every row is drawn in its own face, and a face " +
            "is listed only when the font itself provides it rather than one the renderer " +
            "would fake.",
            "category?:all|recent|used|favourites (default all), max?:number (default 500), search?:string",
            (ctx, p) =>
            {
                int max = p.TryGetProperty("max", out JsonElement mv) && mv.TryGetInt32(out int m)
                    ? Math.Clamp(m, 1, 5000)
                    : 500;

                // Through FontChooser, which the popup also uses: what a driver reads here and
                // what a person sees in the dropdown are the same answer.
                FontCategory category = FontChooser.Parse(p.GetString("category"));
                string? search = p.GetString("search");
                IReadOnlyList<FontFamilyEntry> list =
                    FontChooser.Select(ctx.Document, category, search);

                return new
                {
                    count = list.Count,
                    shown = Math.Min(list.Count, max),
                    category = FontChooser.NameOf(category),
                    favouriteCount = FontFavourites.Shared.Count,
                    recentCount = FontFavourites.Shared.Recents.Count,
                    preview = FontChooser.PreviewText,
                    families = list.Take(max).Select(f => new
                    {
                        name = f.Name,
                        label = f.Label,
                        faceCount = f.FaceCount,
                        standard = f.IsStandard,
                        favourite = FontFavourites.Shared.IsFavourite(f.Name),
                        drawable = FontChooser.RowFace(f) is not null,
                        faces = f.Faces.Select(face => new
                        {
                            style = face.Style,
                            bold = face.Bold,
                            italic = face.Italic,
                        }).ToArray(),
                    }).ToArray(),
                };
            });

        Add("fonts.faces",
            "The faces one family really has, as the chooser lists them when the family is " +
            "opened.",
            "family:string",
            (ctx, p) =>
            {
                string family = p.GetString("family")
                    ?? throw new EditorOperationException("Parameter 'family' is required.");

                IReadOnlyList<FontFace> faces = FontCatalog.FacesOf(family);

                return new
                {
                    family,
                    faceCount = faces.Count,
                    favourite = FontFavourites.Shared.IsFavourite(family),
                    faces = faces.Select(f => new
                    {
                        style = f.Style,
                        bold = f.Bold,
                        italic = f.Italic,
                    }).ToArray(),
                };
            });

        Add("fonts.favourite",
            "Star a font family, or unstar it. Favourites last between sessions, which is the " +
            "point of them: a list of two hundred families is not one anybody reads to the end.",
            "family:string, favourite?:bool (omit to toggle)",
            (ctx, p) =>
            {
                string family = p.GetString("family")
                    ?? throw new EditorOperationException("Parameter 'family' is required.");

                bool? wanted = p.TryGetProperty("favourite", out JsonElement fv) &&
                               fv.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? fv.GetBoolean()
                    : null;

                bool starred = wanted is { } state
                    ? FontFavourites.Shared.Set(family, state)
                    : FontFavourites.Shared.Toggle(family);

                return new { family, favourite = starred, count = FontFavourites.Shared.Count };
            });

        Add("tool.set", "Select the active tool, as clicking it in the toolbar would.",
            "tool:string (select|node|pen|rectangle|ellipse|artboard|text)",
            (ctx, p) =>
            {
                string name = p.GetString("tool")
                    ?? throw new EditorOperationException("Parameter 'tool' is required.");
                if (!Enum.TryParse(name, ignoreCase: true, out EditorTool tool))
                {
                    throw new EditorOperationException(
                        $"Unknown tool '{name}'. Use one of: {string.Join(", ", Enum.GetNames<EditorTool>())}.");
                }

                ctx.ViewModel.Tool = tool;
                return new { tool = tool.ToString().ToLowerInvariant() };
            });

        Add("tool.get", "The active tool.", "",
            (ctx, _) => new { tool = ctx.ViewModel.Tool.ToString().ToLowerInvariant() });

        Add("pane.list",
            "Dockable panes: whether each is open, and how the stacked ones are sized. A " +
            "panel is either fixed (a height in pixels) or stretchable (it shares the " +
            "slack with its neighbours), which is what the separator between two panels " +
            "reflects.",
            "",
            (ctx, _) =>
            {
                var sizes = RequireHost(ctx).PanelSizes().ToDictionary(s => s.Id, s => s);

                return RequireHost(ctx).Panes()
                    .Select(p => new
                    {
                        id = p.Id,
                        title = p.Title,
                        open = p.IsOpen,
                        stretchable = sizes.TryGetValue(p.Id, out var s) ? s.Stretchable : (bool?)null,
                        height = sizes.TryGetValue(p.Id, out var h) ? Math.Round(h.Height, 1) : (double?)null,
                        weight = sizes.TryGetValue(p.Id, out var w) ? Math.Round(w.Weight, 1) : (double?)null,
                    })
                    .ToArray();
            });

        Add("pane.setStretch",
            "Make a docked panel fixed or stretchable. A fixed pane keeps a height in " +
            "pixels and its neighbour absorbs the slack; stretchable panes share it. The " +
            "separator between two panels changes appearance to show which is in force.",
            "pane:string, stretchable:bool, height?:number",
            (ctx, p) =>
            {
                string pane = p.GetString("pane")
                    ?? throw new EditorOperationException("Parameter 'pane' is required.");
                bool stretchable = p.TryGetProperty("stretchable", out JsonElement sv) &&
                                   sv.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? sv.GetBoolean()
                    : true;
                double? height = p.TryGetProperty("height", out JsonElement hv) &&
                                 hv.ValueKind == JsonValueKind.Number
                    ? hv.GetDouble()
                    : null;

                bool ok = RequireHost(ctx).SetPaneStretchable(pane, stretchable, height);
                return new { pane, stretchable, height, applied = ok };
            });

        Add("pane.setSize",
            "Size a docked panel: its height in pixels when fixed, or its share of the " +
            "slack when stretchable. The equivalent of dragging the separator.",
            "pane:string, size:number",
            (ctx, p) =>
            {
                string pane = p.GetString("pane")
                    ?? throw new EditorOperationException("Parameter 'pane' is required.");
                double size = p.TryGetProperty("size", out JsonElement v) &&
                              v.ValueKind == JsonValueKind.Number
                    ? v.GetDouble()
                    : throw new EditorOperationException("Parameter 'size' is required.");

                bool ok = RequireHost(ctx).SetPaneSize(pane, size);
                return new { pane, size, applied = ok };
            });

        Add("pane.set", "Show or hide a dockable pane by id or title; omit 'visible' to toggle.",
            "pane:string, visible?:bool",
            (ctx, p) =>
            {
                string pane = p.GetString("pane")
                    ?? throw new EditorOperationException("Parameter 'pane' is required.");
                bool? visible = p.TryGetProperty("visible", out JsonElement v) &&
                                v.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? v.GetBoolean()
                    : null;

                bool open = RequireHost(ctx).SetPaneOpen(pane, visible);
                return new { pane, open };
            });

        Add("app.exit", "Close the application, as File → Exit would.", "",
            (ctx, _) =>
            {
                RequireHost(ctx).Exit();
                return new { exiting = true };
            });

        // ---- open documents ----------------------------------------------
        Add("document.list", "Open document tabs.", "", (ctx, _) => ctx.ViewModel.Sessions
            .Select((s, i) => new
            {
                index = i,
                id = s.Document.Id,
                name = s.Document.Name,
                active = ReferenceEquals(s, ctx.ViewModel.ActiveSession),
                artboards = s.Document.Artboards.Count,
            }).ToArray());

        Add("document.select", "Activate an open document tab by index or id.", "index?:number, id?:guid",
            (ctx, p) =>
            {
                DocumentSession session = ResolveSession(ctx, p);
                ctx.ViewModel.ActiveSession = session;
                return Summary(ctx);
            });

        Add("document.close", "Close a document tab (defaults to the active one).", "index?:number, id?:guid",
            (ctx, p) =>
            {
                bool targeted = p.ValueKind == JsonValueKind.Object &&
                                (p.TryGetProperty("index", out _) || p.TryGetProperty("id", out _));
                DocumentSession session = targeted ? ResolveSession(ctx, p) : ctx.ViewModel.ActiveSession;

                if (ctx.ViewModel.Sessions.Count <= 1)
                {
                    throw new EditorOperationException("The last document cannot be closed.");
                }

                ctx.ViewModel.CloseSession(session);
                return Summary(ctx);
            });

        // ---- server round-trip (the File menu's server items) ------------
        AddAsync("document.saveToServer", "Upload the active document to the automation server (File → Save to Server).",
            "", async (ctx, _, _) => new
            {
                saved = await ctx.ViewModel.SaveToServerAsync().ConfigureAwait(false),
                server = EditorViewModel.ServerBase,
            });

        AddAsync("document.openFromServer", "Load a document from the automation server (File → Open from Server).",
            "", async (ctx, _, _) => new
            {
                loaded = await ctx.ViewModel.LoadFromServerAsync().ConfigureAwait(false),
                documents = ctx.ViewModel.Sessions.Count,
            });

        // ---- the diary (searchable history + learned skills) -------------
        Add("history.stats", "Size, session and skill counts of the application diary.", "",
            (ctx, _) => RequireHistory(ctx).Stats());

        Add("history.tail", "The most recent diary entries, oldest first.", "count?:number, sessionId?:string",
            (ctx, p) => RequireHistory(ctx).Tail(
                    (int)Math.Clamp(p.GetLong("count", 50), 1, 2000),
                    p.GetString("sessionId"))
                .Select(DescribeInteraction).ToArray());

        Add("history.search",
            "Search the whole diary (this session and all previous ones) for anything relevant to a " +
            "query — what was done, how, and by whom. This is how past work is recalled.",
            "query:string, limit?:number, kind?:ui|api|llm|system|skill, sessionId?:string",
            (ctx, p) =>
            {
                string query = p.GetString("query") ?? string.Empty;
                string? rawKind = p.GetString("kind");
                InteractionKind? kind = rawKind is not null &&
                                        Enum.TryParse(rawKind, ignoreCase: true, out InteractionKind parsed)
                    ? parsed
                    : null;

                return RequireHistory(ctx)
                    .Search(query, (int)Math.Clamp(p.GetLong("limit", 20), 1, 200), kind, p.GetString("sessionId"))
                    .Select(DescribeInteraction).ToArray();
            });

        Add("history.sessions", "Every recorded session, most recent first.", "",
            (ctx, _) => RequireHistory(ctx).Sessions().Select(s => new
            {
                sessionId = s.SessionId,
                startedUtc = s.StartedUtc,
                endedUtc = s.EndedUtc,
                minutes = Math.Round((s.EndedUtc - s.StartedUtc).TotalMinutes, 1),
                entries = s.Entries,
                skills = s.Skills,
            }).ToArray());

        Add("history.session", "Every entry of one session, in order.", "sessionId:string, max?:number",
            (ctx, p) =>
            {
                string session = p.GetString("sessionId")
                    ?? throw new EditorOperationException("Parameter 'sessionId' is required.");
                return RequireHistory(ctx)
                    .Session(session, (int)Math.Clamp(p.GetLong("max", 500), 1, 5000))
                    .Select(DescribeInteraction).ToArray();
            });

        Add("history.skills", "Skills learned from completed work; query to find one to reuse.",
            "query?:string, limit?:number",
            (ctx, p) => RequireHistory(ctx)
                .Skills(p.GetString("query"), (int)Math.Clamp(p.GetLong("limit", 20), 1, 100))
                .Select(DescribeInteraction).ToArray());

        Add("history.learn",
            "Learn a skill from what was just done: store a reusable description plus the operations " +
            "that achieved it, so future sessions can find and repeat the approach. Use this when the " +
            "user says something like \"learn this as a skill\".",
            "title:string, description?:string, sessionId?:string, tags?:string[]",
            (ctx, p) =>
            {
                string title = p.GetString("title")
                    ?? throw new EditorOperationException("Parameter 'title' is required.");
                string[] tags = p.TryGetProperty("tags", out JsonElement tagElement) &&
                                tagElement.ValueKind == JsonValueKind.Array
                    ? tagElement.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String)
                        .Select(e => e.GetString()!).ToArray()
                    : Array.Empty<string>();

                InteractionRecord learned = RequireHistory(ctx).Learn(
                    title, p.GetString("description"), p.GetString("sessionId"), tags);

                return new
                {
                    learned = true,
                    skill = learned.Target,
                    sessionId = learned.SessionId,
                    tags = learned.Tags,
                    details = learned.Details,
                };
            });

        Add("history.note",
            "Add a note to the diary (for example why a decision was made).", "text:string, target?:string",
            (ctx, p) =>
            {
                string text = p.GetString("text")
                    ?? throw new EditorOperationException("Parameter 'text' is required.");
                InteractionRecord note = RequireHistory(ctx).Note(text, p.GetString("target"));
                return new { recorded = true, sequence = note.Sequence };
            });

        Add("history.export", "Write the whole diary to a file for analysis.", "path:string",
            (ctx, p) =>
            {
                string path = p.GetString("path")
                    ?? throw new EditorOperationException("Parameter 'path' is required.");
                string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                return new { exported = path, entries = RequireHistory(ctx).Export(path) };
            });

        // ---- what is on screen (no vision required) ----------------------
        Add("ui.dump",
            "Massive text dump of what is on screen: the whole Avalonia visual tree with each " +
            "control's type, name, text, geometry and state; and/or the document tree with objects, " +
            "ids, bounds and text. Use this when you cannot see a screenshot.",
            "scope?:window|document|layer|all (default all), layerName?:string, maxNodes?:number, includeHidden?:bool",
            (ctx, p) => UiDump(ctx, p));

        AddAsync("ui.describe",
            "Ask the vision model what is visible, optionally focused on a region, a document layer or " +
            "the selection. Returns a natural-language description. Runs off the UI thread and is " +
            "cancellable. Example: a person can ask \"describe what is on layer 'UK 14'\" and this answers it.",
            "target?:window|document|layer|selection (default window), layerName?:string, " +
            "region?:{x,y,width,height,space?:window|document}, question?:string, withImage?:bool",
            UiDescribeAsync);

        return ops.ToDictionary(o => o.Name, StringComparer.Ordinal);
    }

    // ------------------------------------------------------------------
    // Screen/document introspection
    // ------------------------------------------------------------------

    private static object UiDump(AutomationContext ctx, JsonElement p)
    {
        string scope = (p.GetString("scope") ?? "all").ToLowerInvariant();
        string? layerName = p.GetString("layerName");
        int maxNodes = (int)p.GetLong("maxNodes", VisualTreeDump.DefaultMaxNodes);
        bool includeHidden = p.GetBool("includeHidden", true);

        var parts = new List<string>();
        if (scope is "window" or "ui")
        {
            parts.Add(ctx.UiTreeDump?.Invoke(maxNodes) ?? "(no window available)");
        }
        else if (scope is "document" or "layer")
        {
            parts.Add(VisualTreeDump.Document(ctx.Document, layerName, includeHidden));
        }
        else if (scope == "all")
        {
            parts.Add(ctx.UiTreeDump?.Invoke(maxNodes) ?? "(no window available)");
            parts.Add(VisualTreeDump.Document(ctx.Document, layerName, includeHidden));
        }
        else
        {
            throw new EditorOperationException(
                $"Unknown scope '{scope}'. Use window|document|layer|all.");
        }

        string text = string.Join("\n", parts);
        return new { scope, characters = text.Length, text };
    }

    /// <summary>
    /// Builds the describe request on the UI thread (the visual tree read and the
    /// screenshot need it), then awaits the model off the UI thread so the editor —
    /// and this overlay — stay responsive and the call can be cancelled.
    /// </summary>
    private static async Task<object?> UiDescribeAsync(
        AutomationContext ctx, JsonElement p, CancellationToken cancellationToken)
    {
        if (ctx.Describe is null)
        {
            throw new EditorOperationException("No vision model is configured for this build.");
        }

        (string prompt, byte[]? image, string target, string? layerName) =
            await Dispatcher.UIThread.InvokeAsync(() => BuildDescribeRequest(ctx, p));

        string description = await ctx.Describe(prompt, image, cancellationToken).ConfigureAwait(false);

        return new
        {
            target,
            layerName,
            description,
            imageBytes = image?.Length ?? 0,
            promptCharacters = prompt.Length,
        };
    }

    /// <summary>Assembles the prompt and optional screenshot; must run on the UI thread.</summary>
    private static (string Prompt, byte[]? Image, string Target, string? LayerName) BuildDescribeRequest(
        AutomationContext ctx, JsonElement p)
    {
        string target = (p.GetString("target") ?? "window").ToLowerInvariant();
        string? layerName = p.GetString("layerName");
        bool withImage = p.GetBool("withImage", true);
        string question = p.GetString("question") ??
                          $"Describe what is visible on the {target}. Mention layout, colours, text and anything unusual.";

        var prompt = new StringBuilder();
        prompt.AppendLine(question);
        prompt.AppendLine();

        switch (target)
        {
            case "window":
            case "ui":
                prompt.AppendLine("CONTEXT — the application's visual tree:");
                prompt.AppendLine(Truncate(ctx.UiTreeDump?.Invoke(4000) ?? "(no window)", 12000));
                break;

            case "document":
                prompt.AppendLine("CONTEXT — the document tree:");
                prompt.AppendLine(Truncate(VisualTreeDump.Document(ctx.Document, null, true), 12000));
                break;

            case "layer":
                if (string.IsNullOrWhiteSpace(layerName))
                {
                    throw new EditorOperationException("target=layer requires 'layerName'.");
                }

                prompt.AppendLine($"CONTEXT — layer \"{layerName}\" of the document:");
                prompt.AppendLine(Truncate(VisualTreeDump.Document(ctx.Document, layerName, true), 12000));
                break;

            case "selection":
                prompt.AppendLine("CONTEXT — the current selection:");
                prompt.AppendLine(JsonSerializer.Serialize(
                    ctx.Session.SelectedObjects.Select(DescribeOne),
                    new JsonSerializerOptions { WriteIndented = true }));
                break;

            default:
                throw new EditorOperationException(
                    $"Unknown target '{target}'. Use window|document|layer|selection.");
        }

        if (p.TryGetProperty("region", out JsonElement region) && region.ValueKind == JsonValueKind.Object)
        {
            string space = region.TryGetProperty("space", out JsonElement spaceElement) &&
                           spaceElement.ValueKind == JsonValueKind.String
                ? spaceElement.GetString() ?? "window"
                : "window";
            prompt.AppendLine();
            prompt.AppendLine($"Focus on the region x={region.GetProperty("x").GetDouble():F0} " +
                              $"y={region.GetProperty("y").GetDouble():F0} " +
                              $"w={region.GetProperty("width").GetDouble():F0} " +
                              $"h={region.GetProperty("height").GetDouble():F0} " +
                              $"in {space} coordinates, and ignore the rest.");
        }

        byte[]? image = withImage ? ctx.Screenshot?.Invoke() : null;
        return (prompt.ToString(), image, target, layerName);
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text[..max] + "\n…(truncated)";

    private static ViewportActions RequireViewport(AutomationContext ctx)
        => ctx.Viewport ?? throw new EditorOperationException("No viewport is available in this host.");

    /// <summary>
    /// Reports every font the document uses and what is actually being drawn with it.
    ///
    /// "When a font is embedded we must not substitute it" is a project rule, so a
    /// substitution is something the person — and a driver — must be told about rather
    /// than left to infer from the rendering.
    /// </summary>
    /// <summary>True when a requested family word names a font kind, singular or plural.</summary>
    private static bool MatchesKind(StandardFontKind kind, string wanted)
    {
        string name = kind.ToString();
        return name.Equals(wanted, StringComparison.OrdinalIgnoreCase) ||
               (name + "s").Equals(wanted, StringComparison.OrdinalIgnoreCase);
    }
    /// <summary>The window's visual tree, required by the input-injection operations.</summary>
    /// <summary>The canvas control, found from the window the automation root exposes.</summary>
    private static VCCad.App.Controls.CanvasWorkspace? Workspace(AutomationContext ctx)
    {
        Avalonia.Visual? root = ctx.InputRoot?.Invoke();
        if (root is null)
        {
            return null;
        }

        if (root is VCCad.App.Controls.CanvasWorkspace direct)
        {
            return direct;
        }

        return root.GetVisualDescendants().OfType<VCCad.App.Controls.CanvasWorkspace>().FirstOrDefault();
    }
    /// <summary>JSON options for reading an input event: names are matched loosely.</summary>
    private static readonly JsonSerializerOptions InputJson = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>The working colour, described the way color.get describes it.</summary>
    private static object DescribeColor()
    {
        ColorPickerSnapshot snap = EditorColorState.Shared.Model.Snapshot();

        return new
        {
            r = Math.Round(snap.Color.R, 6),
            g = Math.Round(snap.Color.G, 6),
            b = Math.Round(snap.Color.B, 6),
            hex = snap.Hex,
            alpha = Math.Round(snap.Alpha, 4),
            h = Math.Round(snap.Hue, 4),
            s = Math.Round(snap.Saturation, 6),
            l = Math.Round(snap.Lightness, 6),
            angleDegrees = Math.Round(snap.AngleDegrees, 4),
        };
    }

    /// <summary>The canvas, or a failure saying why there is none.</summary>
    private static CanvasWorkspace RequireCanvas(AutomationContext ctx)
        => Workspace(ctx) ?? throw new EditorOperationException("No canvas is available.");

    private static Avalonia.Visual Root(AutomationContext ctx)
        => ctx.InputRoot?.Invoke() ?? throw new EditorOperationException("No window is available.");

    /// <summary>
    /// Every control in the application, the main window first and then any other window.
    ///
    /// A dialog is a window of its own, so ui.find - which walked only the main one - could not
    /// see the rename prompt's text box or its button, and a driver could open the prompt and
    /// not fill it in. Handles index into this list, and ui.click and ui.setValue resolve
    /// against the same one, so a handle taken from a dialog means the same thing everywhere.
    /// </summary>
    private static IReadOnlyList<Avalonia.Visual> AllVisuals(AutomationContext ctx)
    {
        var all = new List<Avalonia.Visual>();
        var seen = new HashSet<Avalonia.Visual>(ReferenceEqualityComparer.Instance);

        void Take(Avalonia.Visual root)
        {
            foreach (Avalonia.Visual visual in UiAutomation.Flatten(root))
            {
                if (seen.Add(visual))
                {
                    all.Add(visual);
                }
            }
        }

        Take(Root(ctx));

        if (Avalonia.Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            foreach (Avalonia.Controls.Window window in desktop.Windows)
            {
                Take(window);
            }
        }

        return all;
    }

    /// <summary>
    /// A named page or layer and everything beneath it, or null when nothing has that name.
    ///
    /// Taken by depth rather than by tracking parents: a row is inside the named one exactly
    /// while the rows after it are deeper than it.
    /// </summary>
    private static List<LayerRow>? SubTree(IReadOnlyList<LayerRow> rows, string name)
    {
        for (int i = 0; i < rows.Count; i++)
        {
            LayerRow head = rows[i];
            if (head.Kind is not (LayerRowKind.Artboard or LayerRowKind.Layer or LayerRowKind.Pasteboard) ||
                !string.Equals(head.Label, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var taken = new List<LayerRow> { head };

            for (int j = i + 1; j < rows.Count && rows[j].Depth > head.Depth; j++)
            {
                taken.Add(rows[j]);
            }

            return taken;
        }

        return null;
    }

    /// <summary>The context menu ui.menu opened, which cannot be found by walking.</summary>
    private static Avalonia.Controls.ContextMenu? OpenMenu { get; set; }

    private static object FontReport(AutomationContext ctx)
    {
        IReadOnlyList<(FontUsageEntry Font, string Source)> detail = FontUsage.Detail(ctx.Document);
        IReadOnlyList<string> missing = StandardFontResolver.Missing(ctx.Document);
        IReadOnlyList<string> unresolved = FontUsage.Unresolved(ctx.Document);

        // The picker's own list, reported here so a driver can check it without seeing the
        // screen. The font chooser is not an operation and its contents are not a control a
        // dump can read, so without this "the picker offers the machine's fonts" would be
        // an assertion about a dropdown nobody has looked inside.
        IReadOnlyList<string> offered = StandardFontResolver.OfferedFamilies();
        IReadOnlyList<string> standard = StandardFontResolver.StandardFamilyNames();

        return new
        {
            fonts = detail.Select(d => new
            {
                font = d.Font.BaseFont,
                embedded = d.Font.Embedded,
                drawingWith = d.Source,
            }).ToArray(),
            picker = new
            {
                offered = offered.Count,
                standardFaces = standard.Count,
                systemFonts = Math.Max(0, offered.Count - standard.Count),
                families = offered,
            },
            missingStandardFonts = missing,
            embeddedNotResolved = unresolved,
            note = missing.Count > 0
                ? "No font is installed for these standard faces. Install the URW base-35 fonts " +
                  "(fonts-urw-base35, or Ghostscript), or run fonts.installStandard."
                : unresolved.Count > 0
                    ? "An embedded font failed to load. This is a defect: the file carries what we need."
                    : null,
        };
    }

    /// <summary>
    /// One entry of a list-backed control, as text plus what it says about itself.
    ///
    /// A font entry is described by the family it applies and the face it is drawn in, which
    /// is what tells a reader that the row really is a specimen and not just a name.
    /// </summary>
    private static object DescribeChoice(object? item) => item switch
    {
        FontChoice choice => new
        {
            text = choice.Label,
            family = choice.Name,
            face = choice.Face?.Name,
            faces = choice.FaceCount,
            standard = choice.IsStandard,
            drawable = choice.Drawable,
        },
        _ => (object)new { text = item?.ToString() },
    };

    /// <summary>The model-facing kind of an item (path/text/group).</summary>
    private static string ItemKind(LayerItem item) => item switch
    {
        PathItem => "path",
        TextItem => "text",
        ArtGroup => "group",
        ImageItem => "image",
        _ => item.GetType().Name,
    };

    private static HostActions RequireHost(AutomationContext ctx)
        => ctx.Host ?? throw new EditorOperationException("No window is available in this host.");

    private static InteractionLog RequireHistory(AutomationContext ctx)
        => ctx.History ?? throw new EditorOperationException("No diary is available in this host.");

    /// <summary>Projects a diary entry for a driver: compact, but with the detail kept.</summary>
    private static object DescribeInteraction(InteractionRecord record) => new
    {
        sequence = record.Sequence,
        at = record.TimestampUtc,
        sessionId = record.SessionId,
        kind = record.Kind.ToString().ToLowerInvariant(),
        category = record.Category.ToString().ToLowerInvariant(),
        name = record.Name,
        target = record.Target,
        details = record.Details,
        success = record.Success,
        durationMs = Math.Round(record.DurationMs, 2),
        tags = record.Tags,
    };

    /// <summary>Finds an open document tab by index or id (defaulting to the active one).</summary>
    private static DocumentSession ResolveSession(AutomationContext ctx, JsonElement p)
    {
        IReadOnlyList<DocumentSession> sessions = ctx.ViewModel.Sessions;

        if (p.TryGetGuid("id", out Guid id))
        {
            DocumentSession? byId = sessions.FirstOrDefault(s => s.Document.Id == id);
            return byId ?? throw new EditorOperationException($"No open document with id '{id}'.");
        }

        if (p.TryGetProperty("index", out JsonElement indexElement) && indexElement.ValueKind == JsonValueKind.Number)
        {
            int index = indexElement.GetInt32();
            if (index < 0 || index >= sessions.Count)
            {
                throw new EditorOperationException(
                    $"Tab index {index} is out of range; {sessions.Count} document(s) are open.");
            }

            return sessions[index];
        }

        return ctx.ViewModel.ActiveSession;
    }

    private static Avalonia.Controls.Control RequireUiRoot(AutomationContext ctx)
        => ctx.UiRoot?.Invoke() ?? throw new EditorOperationException("No window is available in this host.");

    /// <summary>Where a visual sits in a flattened list, or -1.</summary>
    private static int IndexOf(IReadOnlyList<Visual> all, Visual visual)
    {
        for (int i = 0; i < all.Count; i++)
        {
            if (ReferenceEquals(all[i], visual))
            {
                return i;
            }
        }

        return -1;
    }

    private static object FindControls(AutomationContext ctx, JsonElement p)
    {
        string? type = p.GetString("type");
        string? name = p.GetString("name");
        string? text = p.GetString("text");
        bool includeHidden = p.GetBool("includeHidden", false);
        int max = (int)Math.Clamp(p.GetLong("max", 50), 1, 20000);

        // Search every window, not just the main one, and give handles that are indices into
        // the same list ui.click resolves against - otherwise a handle found in a dialog would
        // mean a different control when it was used.
        IReadOnlyList<Visual> all = AllVisuals(ctx);
        var roots = new List<Visual> { Root(ctx) };

        if (Avalonia.Application.Current?.ApplicationLifetime
            is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
        {
            roots.AddRange(desktop.Windows);
        }

        var seen = new HashSet<Visual>(ReferenceEqualityComparer.Instance);
        var matches = new List<(Visual Visual, UiControlRef Ref)>();

        foreach (Visual root in roots)
        {
            if (root is not Avalonia.Controls.Control control)
            {
                continue;
            }

            foreach ((Visual visual, UiControlRef reference) in UiAutomation.Find(
                         control, type, name, text, includeHidden, max))
            {
                if (!seen.Add(visual))
                {
                    continue;
                }

                int index = IndexOf(all, visual);
                if (index >= 0)
                {
                    matches.Add((visual, reference with { Index = index }));
                }
            }
        }

        return new
        {
            count = matches.Count,
            controls = matches.Select(m => new
            {
                handle = m.Ref.Index,
                type = m.Ref.Type,
                name = m.Ref.Name,
                text = m.Ref.Text,
                x = Math.Round(m.Ref.Bounds.X, 1),
                y = Math.Round(m.Ref.Bounds.Y, 1),
                width = Math.Round(m.Ref.Bounds.Width, 1),
                height = Math.Round(m.Ref.Bounds.Height, 1),
                enabled = m.Ref.IsEnabled,
                visible = m.Ref.IsVisible,
            }).ToArray(),
            summary = UiAutomation.Report(matches.Select(m => m.Ref)),
        };
    }

    /// <summary>
    /// Resolves the control named by handle or by type/name/text/index, then either
    /// clicks it or sets its value.
    /// </summary>
    private static object ActOnControl(AutomationContext ctx, JsonElement p, string action)
    {
        Avalonia.Controls.Control root = RequireUiRoot(ctx);
        Visual target = ResolveControl(ctx, root, p);
        UiControlRef reference = UiAutomation.Describe(root, target);

        string outcome = action == "click"
            ? UiAutomation.Click(target)
            : UiAutomation.SetValue(target, p.GetString("value")
                ?? throw new EditorOperationException("Parameter 'value' is required."));

        ctx.ViewModel.NotifyDocumentChanged();
        return new { action, target = reference.Describe(), result = outcome };
    }

    private static Visual ResolveControl(AutomationContext ctx, Avalonia.Controls.Control root, JsonElement p)
    {
        // A handle from ui.find is exact, so prefer it.
        if (p.TryGetProperty("handle", out JsonElement handleElement) &&
            handleElement.ValueKind == JsonValueKind.Number)
        {
            int handle = handleElement.GetInt32();
            IReadOnlyList<Visual> all = AllVisuals(ctx);
            if (handle < 0 || handle >= all.Count)
            {
                throw new EditorOperationException(
                    $"handle {handle} is out of range; call ui.find for current handles.");
            }

            return all[handle];
        }

        IReadOnlyList<(Visual Visual, UiControlRef Ref)> matches = UiAutomation.Find(
            root,
            p.GetString("type"),
            p.GetString("name"),
            p.GetString("text"),
            includeHidden: true,
            max: 100);

        if (matches.Count == 0)
        {
            throw new EditorOperationException(
                "No control matched. Call ui.find to list what is on screen.");
        }

        int index = (int)Math.Clamp(p.GetLong("index", 0), 0, matches.Count - 1);
        return matches[index].Visual;
    }

    private static string RequireExistingFile(JsonElement p, string name)
    {
        string path = p.GetString(name)
            ?? throw new EditorOperationException($"Parameter '{name}' is required.");

        if (File.Exists(path))
        {
            return path;
        }

        // Let a caller name a file the way a person would ("the sample A0 Temi Bow
        // …pdf") instead of requiring an absolute path.
        string wanted = Normalize(Path.GetFileNameWithoutExtension(path));
        foreach (string root in CandidateRoots())
        {
            string samples = Path.Combine(root, "samples");
            if (!Directory.Exists(samples))
            {
                continue;
            }

            string? match = Directory.EnumerateFiles(samples, "*.pdf")
                .FirstOrDefault(f => Normalize(Path.GetFileNameWithoutExtension(f)) == wanted)
                ?? Directory.EnumerateFiles(samples, "*.pdf")
                    .FirstOrDefault(f => Normalize(Path.GetFileNameWithoutExtension(f)).Contains(wanted, StringComparison.Ordinal));
            if (match is not null)
            {
                return match;
            }
        }

        throw new EditorOperationException(
            $"File not found: {path} (also searched the samples folder for \"{Path.GetFileName(path)}\").");
    }

    /// <summary>Directories to search for bundled files: app base and its ancestors.</summary>
    private static IEnumerable<string> CandidateRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (seen.Add(dir.FullName))
            {
                yield return dir.FullName;
            }
        }
    }

    /// <summary>Lower-cases and strips separators so "A0 Temi Bow.pdf" matches the file name.</summary>
    private static string Normalize(string value)
        => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static object ViewStatus(AutomationContext ctx) => ctx.Viewport is null
        ? new { available = false }
        : new
        {
            available = true,
            zoom = Math.Round(ctx.Viewport.GetZoom(), 4),
            percent = Math.Round(ctx.Viewport.GetZoom() * 100, 2),
            autoFit = ctx.Viewport.IsAutoFit(),
        };

    /// <summary>
    /// Centres a text block in a target rectangle. Text lives in artboard-local
    /// coordinates, so document-space targets (an item's bounds, an explicit rect)
    /// are converted through the owning artboard's origin.
    /// </summary>
    private static object CenterText(AutomationContext ctx, JsonElement p)
    {
        TextItem text = p.TryGetGuid("itemId", out Guid id)
            ? AllItems(ctx.Document).OfType<TextItem>().FirstOrDefault(t => t.Id == id)
              ?? throw new EditorOperationException($"Text '{id}' does not exist.")
            : ctx.Session.SelectedTextItems().FirstOrDefault()
              ?? throw new EditorOperationException(
                  "No text is selected; pass 'itemId' or select a text object first.");

        Vector2D offset = text.ArtboardOffset();
        Rect2D before = text.BoundingBox();
        string target = (p.GetString("target") ?? (p.TryGetGuid("targetItemId", out _) ? "item" : "enclosing"))
            .ToLowerInvariant();

        Rect2D box;
        switch (target)
        {
            case "enclosing":
            {
                // "Centre it within the bounding rectangle": the rectangle in
                // question is normally the pattern piece the label belongs to, so
                // find the smallest path whose bounds contain the text's centre.
                Point2D centre = new(before.Center.X, before.Center.Y);
                (LayerItem Item, Rect2D Bounds)? host = AllItems(ctx.Document)
                    .OfType<PathItem>()
                    .Where(path => !ReferenceEquals(path, text))
                    .Select(path => (Item: (LayerItem)path, Bounds: path.BoundingBox()))
                    .Where(candidate => !candidate.Bounds.IsEmpty && candidate.Bounds.Contains(centre))
                    .OrderBy(candidate => candidate.Bounds.Width * candidate.Bounds.Height)
                    .Cast<(LayerItem Item, Rect2D Bounds)?>()
                    .FirstOrDefault();

                if (host is null)
                {
                    Artboard? artboard = text.OwningLayer()?.Artboard
                        ?? throw new EditorOperationException("The text is not on an artboard.");
                    box = new Rect2D(0, 0, artboard.Width, artboard.Height);
                    target = "artboard";
                    break;
                }

                Vector2D hostOffset = host.Value.Item.ArtboardOffset();
                box = new Rect2D(
                    host.Value.Bounds.X + hostOffset.X - offset.X,
                    host.Value.Bounds.Y + hostOffset.Y - offset.Y,
                    host.Value.Bounds.Width,
                    host.Value.Bounds.Height);
                break;
            }

            case "artboard":
            {
                Artboard? artboard = text.OwningLayer()?.Artboard
                    ?? throw new EditorOperationException("The text is not on an artboard.");
                box = new Rect2D(0, 0, artboard.Width, artboard.Height);
                break;
            }

            case "item":
            {
                if (!p.TryGetGuid("targetItemId", out Guid targetId))
                {
                    throw new EditorOperationException("target=item requires 'targetItemId'.");
                }

                LayerItem host = RequireItem(ctx.Document, targetId);
                Rect2D world = host switch
                {
                    PathItem path => path.BoundingBox(),
                    TextItem other => other.BoundingBox(),
                    ArtGroup group => group.BoundingBox(),
                    _ => Rect2D.Empty,
                };

                if (world.IsEmpty)
                {
                    throw new EditorOperationException($"'{host.Name}' has no usable bounds to centre in.");
                }

                // Both are artboard-local already, but a target on a different
                // artboard would be offset; convert through each one's own origin.
                Vector2D hostOffset = host.ArtboardOffset();
                box = new Rect2D(world.X + hostOffset.X - offset.X, world.Y + hostOffset.Y - offset.Y,
                    world.Width, world.Height);
                break;
            }

            case "rect":
            {
                if (!p.TryGetProperty("width", out _) || !p.TryGetProperty("height", out _))
                {
                    throw new EditorOperationException("target=rect requires x, y, width and height.");
                }

                box = new Rect2D(
                    p.GetDouble("x") - offset.X,
                    p.GetDouble("y") - offset.Y,
                    p.GetDouble("width"),
                    p.GetDouble("height"));
                break;
            }

            default:
                throw new EditorOperationException(
                    $"Unknown target '{target}'. Use enclosing|artboard|item|rect.");
        }

        double x = box.X + ((box.Width - before.Width) / 2);
        double y = box.Y + ((box.Height - before.Height) / 2);

        text.Origin = new Point2D(x, y);
        text.Alignment = TextAlignment.Center;
        ctx.Session.SelectObject(text);
        ctx.ViewModel.NotifyDocumentChanged();

        return new
        {
            itemId = text.Id,
            target,
            before = new { x = Math.Round(before.X, 2), y = Math.Round(before.Y, 2) },
            after = new { x = Math.Round(x, 2), y = Math.Round(y, 2) },
            moved = Math.Round(Math.Abs(x - before.X) + Math.Abs(y - before.Y), 3) > 0.01,
            box = new { x = Math.Round(box.X, 2), y = Math.Round(box.Y, 2), width = Math.Round(box.Width, 2), height = Math.Round(box.Height, 2) },
            text = text.PlainText,
            bounds = DescribeOne(text),
        };
    }

    // ------------------------------------------------------------------
    // Handlers that need more than a line
    // ------------------------------------------------------------------

    private static object CreateObject(AutomationContext ctx, JsonElement p)
    {
        string type = (p.GetString("type") ?? "rectangle").ToLowerInvariant();
        string name = p.GetString("name") ?? type;

        // Geometry is given in document space; item coordinates are artboard-local.
        Point2D anchor = type switch
        {
            "ellipse" => new Point2D(p.GetDouble("cx"), p.GetDouble("cy")),
            "line" => new Point2D(p.GetDouble("x1"), p.GetDouble("y1")),
            _ => new Point2D(p.GetDouble("x"), p.GetDouble("y")),
        };

        (Layer layer, Vector2D offset) = ctx.Session.TargetFor(anchor);

        PathItem item = type switch
        {
            "rectangle" => PathFactory.CreateRectangle(name, new Rect2D(
                p.GetDouble("x") - offset.X, p.GetDouble("y") - offset.Y,
                Math.Max(1, p.GetDouble("width", 100)), Math.Max(1, p.GetDouble("height", 100)))),
            "ellipse" => PathFactory.CreateEllipse(name,
                new Point2D(p.GetDouble("cx") - offset.X, p.GetDouble("cy") - offset.Y),
                Math.Max(1, p.GetDouble("rx", 50)), Math.Max(1, p.GetDouble("ry", 50))),
            "line" => PathFactory.CreateLine(name,
                new Point2D(p.GetDouble("x1") - offset.X, p.GetDouble("y1") - offset.Y),
                new Point2D(p.GetDouble("x2") - offset.X, p.GetDouble("y2") - offset.Y)),
            "polygon" => PathFactory.CreatePolygon(name, ReadPoints(p, offset)),
            "polyline" => PathFactory.CreatePolyline(name, ReadPoints(p, offset)),
            _ => throw new EditorOperationException(
                $"Unknown type '{type}'. Use rectangle|ellipse|line|polygon|polyline."),
        };

        if (p.TryGetColorArray("fillColor", out ColorRgb fill))
        {
            item.Fill = FillSpec.Solid(fill);
        }
        else if (p.TryGetColorArray("color", out ColorRgb fillAlias))
        {
            // Models routinely say "color" for "fillColor"; accept it.
            item.Fill = FillSpec.Solid(fillAlias);
        }

        if (p.TryGetColorArray("strokeColor", out ColorRgb stroke))
        {
            item.Stroke = new StrokeSpec(true, stroke, p.GetDouble("strokeWidth", 1),
                StrokeCap.Butt, StrokeJoin.Miter, 4);
        }
        else if (p.TryGetColorArray("stroke", out ColorRgb strokeAlias))
        {
            item.Stroke = new StrokeSpec(true, strokeAlias, p.GetDouble("strokeWidth", 1),
                StrokeCap.Butt, StrokeJoin.Miter, 4);
        }

        if (p.TryGetGuid("layerId", out Guid layerId))
        {
            layer = RequireLayer(ctx.Document, layerId);
        }

        // Through the command stack, not straight onto the layer: a shape a person draws
        // can be undone, so one the assistant creates must be too - and an edit that
        // bypasses the stack never marks the document as having unsaved changes, so the
        // save prompt would not fire for it.
        ctx.Session.Execute(new AddItemCommand(layer, item));
        ctx.Session.SelectObject(item);
        ctx.ViewModel.NotifyDocumentChanged();
        return DescribeOne(item);
    }

    private static List<Point2D> ReadPoints(JsonElement p, Vector2D offset)
    {
        if (!p.TryGetProperty("points", out JsonElement points) || points.ValueKind != JsonValueKind.Array)
        {
            throw new EditorOperationException("polygon/polyline need 'points': [[x,y], ...].");
        }

        return points.EnumerateArray()
            .Select(pt => new Point2D(pt[0].GetDouble() - offset.X, pt[1].GetDouble() - offset.Y))
            .ToList();
    }

    private static object Arrange(AutomationContext ctx, JsonElement p)
    {
        string action = (p.GetString("action") ?? "front").ToLowerInvariant();
        LayerItem[] items = p.TryGetGuidArray("itemIds", out Guid[] ids)
            ? ids.Select(id => RequireItem(ctx.Document, id)).ToArray()
            : ctx.Session.SelectedObjects.ToArray();

        if (items.Length == 0)
        {
            throw new EditorOperationException("Nothing is selected.");
        }

        foreach (LayerItem item in items)
        {
            IItemContainer? container = item.Container;
            if (container is null)
            {
                continue;
            }

            int index = IndexIn(container, item);
            int target = action switch
            {
                "front" => container.Children.Count - 1,
                "back" => 0,
                "forward" => Math.Min(container.Children.Count - 1, index + 1),
                "backward" => Math.Max(0, index - 1),
                _ => throw new EditorOperationException($"Unknown arrange action '{action}'."),
            };

            ctx.Session.MoveItems(new[] { item }, container, target);
        }

        ctx.ViewModel.NotifyDocumentChanged();
        return new { action, items = items.Length };
    }

    private static void ApplyTransform(
        DocumentSession session, Vector2D translation, double scaleX, double scaleY, double rotationDegrees,
        Point2D? pivot = null, bool ownOnly = false)
    {
        Rect2D bounds = session.SelectionBounds();
        Point2D p = pivot ?? (bounds.IsEmpty ? new Point2D(0, 0) : bounds.Center);
        session.ApplyTransform(p, translation, scaleX, scaleY, rotationDegrees, ownOnly);
    }

    private static int IndexIn(IItemContainer container, LayerItem item)
    {
        for (int i = 0; i < container.Children.Count; i++)
        {
            if (ReferenceEquals(container.Children[i], item))
            {
                return i;
            }
        }

        return 0;
    }

    // ------------------------------------------------------------------
    // Lookup / projection helpers
    // ------------------------------------------------------------------

    /// <summary>Every item in the document, depth-first, artboards then pasteboard.</summary>
    public static IEnumerable<LayerItem> AllItems(CadDocument document)
    {
        foreach (Artboard artboard in document.Artboards)
        {
            foreach (Layer layer in artboard.Layers)
            {
                foreach (LayerItem item in Walk(layer.Children))
                {
                    yield return item;
                }
            }
        }

        foreach (LayerItem item in Walk(document.Orphans.Children))
        {
            yield return item;
        }
    }

    private static IEnumerable<LayerItem> Walk(IReadOnlyList<LayerItem> items)
    {
        foreach (LayerItem item in items)
        {
            yield return item;
            if (item is ArtGroup group)
            {
                foreach (LayerItem child in Walk(group.Children))
                {
                    yield return child;
                }
            }
        }
    }

    /// <summary>Finds an item by id anywhere in the document.</summary>
    public static LayerItem? FindItem(CadDocument document, Guid id)
        => AllItems(document).FirstOrDefault(i => i.Id == id);

    /// <summary>Finds a path by id.</summary>
    public static PathItem? FindPath(CadDocument document, Guid id)
        => AllItems(document).OfType<PathItem>().FirstOrDefault(i => i.Id == id);

    /// <summary>Finds a layer by id.</summary>
    public static Layer? FindLayer(CadDocument document, Guid id)
        => document.Artboards.SelectMany(a => a.Layers).FirstOrDefault(l => l.Id == id);

    /// <summary>Finds an artboard by id.</summary>
    public static Artboard? FindArtboard(CadDocument document, Guid id)
        => document.Artboards.FirstOrDefault(a => a.Id == id);

    private static LayerItem RequireItem(CadDocument document, Guid id)
        => FindItem(document, id) ?? throw new EditorOperationException($"Item '{id}' does not exist.");

    private static PathItem RequirePath(CadDocument document, Guid id)
        => FindPath(document, id) ?? throw new EditorOperationException($"Path '{id}' does not exist.");

    private static Layer RequireLayer(CadDocument document, Guid id)
        => FindLayer(document, id) ?? throw new EditorOperationException($"Layer '{id}' does not exist.");

    private static Artboard RequireArtboard(CadDocument document, Guid id)
        => FindArtboard(document, id) ?? throw new EditorOperationException($"Artboard '{id}' does not exist.");

    private static object Summary(AutomationContext ctx) => new
    {
        document = ctx.Document.Name,
        artboards = ctx.Document.Artboards.Count,
        objects = AllItems(ctx.Document).Count(),
        selected = ctx.Session.SelectedObjects.Select(i => i.Id).ToArray(),
        canUndo = true,

        // Unsaved changes decide whether closing or exiting has to ask first, so how much
        // there is to lose should be visible rather than inferred.
        modified = ctx.Session.IsModified,
    };

    private static IEnumerable<object> Describe(IEnumerable<LayerItem> items) => items.Select(DescribeOne);

    /// <summary>Stable, model-facing projection of an item.</summary>
    public static object DescribeOne(LayerItem item)
    {
        Rect2D bounds = item switch
        {
            PathItem path => path.BoundingBox(),
            TextItem text => text.BoundingBox(),
            ArtGroup group => group.BoundingBox(),
            ImageItem image => image.WorldBounds(),
            _ => Rect2D.Empty,
        };

        return new
        {
            itemId = item.Id,
            name = item.Name,
            type = ItemKind(item),
            visible = item.IsVisible,
            locked = item.IsLocked,
            layer = item.OwningLayer()?.Name,
            x = Math.Round(bounds.X, 3),
            y = Math.Round(bounds.Y, 3),
            width = Math.Round(bounds.Width, 3),
            height = Math.Round(bounds.Height, 3),
            text = item is TextItem t ? t.PlainText : null,
            subPaths = item is PathItem p ? p.SubPaths.Count : (int?)null,
        };
    }

    private static object Record(ApiCallRecord r) => new
    {
        sequence = r.Sequence,
        time = r.Timestamp.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
        source = r.Source.ToString().ToLowerInvariant(),
        op = r.Operation,
        success = r.Success,
        durationMs = Math.Round(r.DurationMs, 2),
        parameters = r.Parameters,
        result = r.Result,
        error = r.Error,
    };

    private static T ParseEnum<T>(string? value, T fallback) where T : struct, Enum
        => !string.IsNullOrWhiteSpace(value) && Enum.TryParse(value, ignoreCase: true, out T parsed)
            ? parsed
            : fallback;

    private static byte[] DecodeBase64(JsonElement p, string name)
    {
        string? text = p.GetString(name);
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new EditorOperationException($"Parameter '{name}' is required.");
        }

        try
        {
            return Convert.FromBase64String(text);
        }
        catch (FormatException)
        {
            throw new EditorOperationException($"Parameter '{name}' is not valid base64.");
        }
    }
}

/// <summary>A user-facing automation failure; transports map this to an error response.</summary>
public sealed class EditorOperationException : Exception
{
    public EditorOperationException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
