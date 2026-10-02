using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Avalonia;
using Avalonia.VisualTree;
using Avalonia.Threading;
using VCCad.App.Commands;
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
using VCCad.Core.Units;
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
    /// Runs the screen eyedropper: shows the full-screen overlay, waits for the click, and returns what it
    /// picked, or null for a cancel. Wired by the shell, which is the layer that can own a window; null in a
    /// headless host, where <c>color.pickAt</c> is the way in.
    /// </summary>
    public Func<Task<(ColorRgb? Colour, string? Refusal)>>? PickFromScreenAsync { get; init; }

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
/// <param name="Tabs">The tabs it holds, in order, and which of them is showing.</param>
public sealed record PaneInfo(string Id, string Title, bool IsOpen, IReadOnlyList<PaneTabInfo> Tabs);

/// <summary>One tab inside a pane, and whether it is the one showing.</summary>
/// <param name="Id">Tab id, which is what a caller names to select it.</param>
/// <param name="Title">Title shown on the tab.</param>
/// <param name="IsOpen">Whether the tab is open at all.</param>
/// <param name="Active">Whether it is the tab currently showing.</param>
public sealed record PaneTabInfo(string Id, string Title, bool IsOpen, bool Active);

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
    Func<string, bool> SetPaneTab,
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
    // Gradient helpers
    // ------------------------------------------------------------------

    /// <summary>The path a gradient operation acts on: an explicit itemId, else the selection.</summary>
    private static PathItem GradientTarget(AutomationContext ctx, JsonElement p)
    {
        if (p.TryGetGuid("itemId", out Guid id))
        {
            return RequirePath(ctx.Document, id);
        }

        return ctx.Session.SelectedPaths().FirstOrDefault()
            ?? throw new EditorOperationException(
                "No path is selected. Select one first, or pass itemId.");
    }

    /// <summary>
    /// The path's gradient, or the one its solid fill would become. A solid visibly becomes a
    /// ramp from its own colour rather than an unrelated default picture, and an invisible fill
    /// (no colour at all) becomes the default ramp.
    /// </summary>
    private static GradientSpec GradientOf(PathItem path)
    {
        if (path.Fill.Gradient is { } gradient)
        {
            return gradient;
        }

        return path.Fill.IsVisible
            ? GradientSpec.Default with
            {
                Stops = new[]
                {
                    new GradientStop(0.0, path.Fill.Color),
                    new GradientStop(1.0, ColorRgb.Black),
                },
            }
            : GradientSpec.Default;
    }

    /// <summary>Writes a gradient onto a path as one undo step, keeping the fill's rule.</summary>
    private static void SetGradient(AutomationContext ctx, PathItem path, GradientSpec gradient)
    {
        FillSpec next = FillSpec.WithGradient(gradient, path.Fill.Rule, path.Fill.Color);
        ctx.Session.Execute(new SetFillCommand(path, next, path.Fill));
    }

    private static object ColourJson(ColorRgb colour) => new
    {
        r = Math.Round(colour.R, 6),
        g = Math.Round(colour.G, 6),
        b = Math.Round(colour.B, 6),
        a = Math.Round(colour.A, 6),
    };

    private static object PointJson(Point2D point) => new
    {
        x = Math.Round(point.X, 6),
        y = Math.Round(point.Y, 6),
    };

    private static object StopJson(GradientStop stop) => new
    {
        position = Math.Round(stop.Position, 6),
        color = ColourJson(stop.Color),
        opacity = Math.Round(stop.Opacity, 6),
        midpoint = Math.Round(stop.Midpoint, 6),
        name = stop.Name,
    };

    /// <summary>The whole gradient, in the shape a driver can send straight back.</summary>
    private static object GradientJson(GradientSpec g) => new
    {
        kind = g.Kind.ToString().ToLowerInvariant(),
        spread = g.Spread.ToString().ToLowerInvariant(),
        stops = g.Normalised().Select(StopJson).ToArray(),
        linear = new { start = PointJson(g.Start), end = PointJson(g.End) },
        radial = new
        {
            centre = PointJson(g.Center),
            radiusX = Math.Round(g.RadiusX, 6),
            radiusY = Math.Round(g.RadiusY, 6),
            rotation = Math.Round(g.Rotation, 6),

            // Null when the gradient has no focus, which is a state of its own: it paints exactly
            // what a focus on the centre paints, so a reader must be able to tell the two apart.
            focalPoint = g.FocalPoint is { } focus ? PointJson(focus) : null,
        },
        angle = Math.Round(g.Angle, 6),
        freeform = new
        {
            mode = g.FreeformMode.ToString().ToLowerInvariant(),
            points = g.Points.Select(fp => new
            {
                position = PointJson(fp.Position),
                color = ColourJson(fp.Color),
                opacity = Math.Round(fp.Opacity, 6),
            }).ToArray(),
            lines = g.Lines.Select(l => new { from = l.From, to = l.To }).ToArray(),
        },
    };

    private static GradientKind ParseKind(JsonElement p)
    {
        string text = (p.GetString("kind") ?? string.Empty).Trim().ToLowerInvariant();
        return text switch
        {
            "linear" => GradientKind.Linear,
            "radial" => GradientKind.Radial,
            "freeform" => GradientKind.Freeform,
            "conical" => GradientKind.Conical,
            _ => throw new EditorOperationException(
                $"Unknown gradient kind '{text}'. Use linear, radial, freeform or conical."),
        };
    }

    private static GradientSpread ParseSpread(JsonElement p)
    {
        string text = (p.GetString("spread") ?? string.Empty).Trim().ToLowerInvariant();
        return text switch
        {
            "pad" => GradientSpread.Pad,
            "reflect" => GradientSpread.Reflect,
            "repeat" => GradientSpread.Repeat,
            _ => throw new EditorOperationException(
                $"Unknown gradient spread '{text}'. Use pad, reflect or repeat."),
        };
    }

    /// <summary>One stop from JSON: position and colour are required, opacity and midpoint optional.</summary>
    private static GradientStop ParseStop(JsonElement stop)
    {
        ColorRgb colour = ParseColorElement(stop, "color");

        return new GradientStop(
            stop.GetDouble("position", 0),
            colour,
            stop.GetDouble("opacity", 1),
            stop.GetDouble("midpoint", 0.5),
            stop.GetString("name")).Clamped();
    }

    /// <summary>The colour at a named member, which may be [r,g,b] or {r,g,b}.</summary>
    private static ColorRgb ParseColorElement(JsonElement owner, string name)
    {
        if (!owner.TryGetProperty(name, out JsonElement value))
        {
            throw new EditorOperationException($"'{name}' is required.");
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            double[] parts = value.EnumerateArray().Select(e => e.GetDouble()).ToArray();
            if (parts.Length < 3)
            {
                throw new EditorOperationException($"'{name}' needs at least [r,g,b].");
            }

            return new ColorRgb(parts[0], parts[1], parts[2], parts.Length > 3 ? parts[3] : 1.0);
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            return new ColorRgb(
                value.GetDouble("r", 0), value.GetDouble("g", 0), value.GetDouble("b", 0),
                value.GetDouble("a", 1));
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return HexColor.Parse(value.GetString()!);
        }

        throw new EditorOperationException($"'{name}' must be [r,g,b], {{r,g,b}} or a hex string.");
    }

    private static IReadOnlyList<GradientStop> ParseStops(JsonElement p)
    {
        if (!p.TryGetProperty("stops", out JsonElement stops) || stops.ValueKind != JsonValueKind.Array)
        {
            throw new EditorOperationException("'stops' must be an array of {position, color, ...}.");
        }

        return stops.EnumerateArray().Select(ParseStop).ToList();
    }

    /// <summary>Replaces one stop in a gradient, leaving the rest alone.</summary>
    private static GradientSpec WithStop(GradientSpec gradient, int index, Func<GradientStop, GradientStop> edit)
    {
        List<GradientStop> stops = gradient.Normalised().ToList();
        if (index < 0 || index >= stops.Count)
        {
            throw new EditorOperationException(
                $"No stop at index {index}; the ramp has {stops.Count}.");
        }

        stops[index] = edit(stops[index]).Clamped();
        return gradient with { Stops = stops.OrderBy(s => s.Position).ToList() };
    }

    /// <summary>Reads an optional <c>{x,y}</c> member into a point.</summary>
    private static bool TryPoint(JsonElement owner, string name, out Point2D point)
    {
        point = default;
        if (!owner.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        point = new Point2D(value.GetDouble("x", 0), value.GetDouble("y", 0));
        return true;
    }

    private static FreeformMode ParseFreeformMode(JsonElement p)
    {
        string text = (p.GetString("freeformMode") ?? string.Empty).Trim().ToLowerInvariant();
        return text switch
        {
            "points" => FreeformMode.Points,
            "lines" => FreeformMode.Lines,
            _ => throw new EditorOperationException(
                $"Unknown freeform mode '{text}'. Use points or lines."),
        };
    }

    private static IReadOnlyList<FreeformPoint> ParseFreeformPoints(JsonElement p)
    {
        if (!p.TryGetProperty("points", out JsonElement points) || points.ValueKind != JsonValueKind.Array)
        {
            throw new EditorOperationException("'points' must be an array of {x,y,color}.");
        }

        var parsed = new List<FreeformPoint>();
        foreach (JsonElement point in points.EnumerateArray())
        {
            parsed.Add(new FreeformPoint(
                new Point2D(point.GetDouble("x", 0), point.GetDouble("y", 0)),
                ParseColorElement(point, "color"),
                Math.Clamp(point.GetDouble("opacity", 1), 0.0, 1.0)));
        }

        return parsed;
    }

    private static IReadOnlyList<(int From, int To)> ParseLines(JsonElement p)
    {
        if (!p.TryGetProperty("lines", out JsonElement lines) || lines.ValueKind != JsonValueKind.Array)
        {
            throw new EditorOperationException("'lines' must be an array of {from,to} point indices.");
        }

        return lines.EnumerateArray()
            .Select(line => ((int)line.GetLong("from", 0), (int)line.GetLong("to", 0)))
            .ToList();
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

        Add("shape.create",
            "Create a paint shape as a closed path: rectangle, rounded-rectangle, star, polygon, " +
            "trapezoid, cloud, callout, heart or arrow, with the parameters that shape has. The " +
            "parameters are kept, so the shape stays adjustable rather than becoming an outline.",
            "kind:string, x?,y?,width?,height?,rotation?,cornerRadius?,points?,innerRatio?,topRatio?," +
            "headLength?,headWidth?,shaftWidth?,tailX?,tailY?,layerId?,fillColor?,strokeColor?," +
            "strokeWidth?,name?",
            (ctx, p) =>
            {
                string kindName = p.GetString("kind")
                    ?? throw new EditorOperationException("Parameter 'kind' is required.");

                ShapeKind? kind = null;
                foreach (ShapeKind candidate in ShapeLibrary.All)
                {
                    if (string.Equals(ShapeLibrary.Name(candidate), kindName, StringComparison.OrdinalIgnoreCase))
                    {
                        kind = candidate;
                    }
                }

                if (kind is null)
                {
                    throw new EditorOperationException(
                        $"Unknown shape '{kindName}'. Use " +
                        string.Join("|", ShapeLibrary.All.Select(ShapeLibrary.Name)) + ".");
                }

                var centre = new Point2D(p.GetDouble("x", 100), p.GetDouble("y", 100));
                (Layer layer, Vector2D offset) = ctx.Session.TargetFor(centre);

                PathItem item = ShapeLibrary.Create(kind.Value, ReadShapeParameters(p, offset));
                if (p.GetString("name") is { Length: > 0 } given)
                {
                    item.Name = given;
                }

                if (p.TryGetColorArray("fillColor", out ColorRgb fill) ||
                    p.TryGetColorArray("color", out fill))
                {
                    item.Fill = FillSpec.Solid(fill);
                }
                else
                {
                    // A shape with no fill is invisible, which reads as a failure rather than as a
                    // choice: give it something to see and let the caller change it.
                    item.Fill = FillSpec.Solid(new ColorRgb(0.2, 0.2, 0.25));
                }

                if (p.TryGetColorArray("strokeColor", out ColorRgb stroke) ||
                    p.TryGetColorArray("stroke", out stroke))
                {
                    item.Stroke = new StrokeSpec(true, stroke, p.GetDouble("strokeWidth", 1),
                        StrokeCap.Butt, StrokeJoin.Miter, 4);
                }

                if (p.TryGetGuid("layerId", out Guid targetLayer))
                {
                    layer = RequireLayer(ctx.Document, targetLayer);
                }

                ctx.Session.Execute(new AddItemCommand(layer, item));
                ctx.Session.SelectObject(item);
                ctx.ViewModel.NotifyDocumentChanged();
                return DescribeShape(item);
            });

        Add("shape.get",
            "What a shape is: its kind, its parameters, its segment count and how many segments its " +
            "symmetry ties together. Reports shape:false for a path that is not one.",
            "itemId?:guid",
            (ctx, p) =>
            {
                PathItem? path = FindSelectedPath(ctx, p);
                return path?.Shape is null ? new { shape = false } : DescribeShape(path);
            });

        Add("shape.setParameters",
            "Change a shape's parameters and rebuild its geometry, in one undo step. Only the members " +
            "sent change, and coordinates are in document space as shape.create takes them.",
            "itemId?:guid, x?,y?,width?,height?,rotation?,cornerRadius?,points?,innerRatio?,topRatio?," +
            "headLength?,headWidth?,shaftWidth?,tailX?,tailY?",
            (ctx, p) =>
            {
                PathItem path = RequireShape(ctx, p, out ShapeDefinition shape);
                ShapeParameters current = shape.Parameters;

                // The operation takes **document** coordinates and a shape stores its parameters in the
                // frame it is placed in, so the point is carried across by the composition #165 stated once
                // rather than by subtracting the artboard origin. Inside a group the origin-only conversion
                // put the centre at the group transform applied to the point that was asked for (#172).
                AffineTransform fromWorld = SelectionEngine.FromWorld(path)
                    ?? throw new EditorOperationException(
                        "the group this shape is in collapses the plane, so a document point has no " +
                        "position in the shape's own frame");

                Point2D centre = p.TryGetProperty("x", out _) || p.TryGetProperty("y", out _)
                    ? fromWorld.Transform(new Point2D(
                        p.GetDouble("x", SelectionEngine.ToWorld(path).Transform(current.Centre).X),
                        p.GetDouble("y", SelectionEngine.ToWorld(path).Transform(current.Centre).Y)))
                    : current.Centre;

                ShapeParameters merged = current with
                {
                    Centre = centre,
                    Width = p.GetDouble("width", current.Width),
                    Height = p.GetDouble("height", current.Height),
                    Rotation = p.GetDouble("rotation", current.Rotation),
                    CornerRadius = p.GetDouble("cornerRadius", current.CornerRadius),
                    Points = (int)Math.Round(p.GetDouble("points", current.Points)),
                    InnerRatio = p.GetDouble("innerRatio", current.InnerRatio),
                    TopRatio = p.GetDouble("topRatio", current.TopRatio),
                    HeadLength = p.GetDouble("headLength", current.HeadLength),
                    HeadWidth = p.GetDouble("headWidth", current.HeadWidth),
                    ShaftWidth = p.GetDouble("shaftWidth", current.ShaftWidth),
                };

                bool hasTail = p.TryGetProperty("tailX", out JsonElement tailX);
                p.TryGetProperty("tailY", out JsonElement tailY);
                if (hasTail)
                {
                    merged = merged with
                    {
                        HasTail = true,
                        Tail = fromWorld.Transform(new Point2D(tailX.GetDouble(), tailY.GetDouble())),
                    };
                }

                var before = path.GeometrySnapshot();
                new ShapeDefinition(shape.Kind, merged).ApplyTo(path);
                ctx.Session.Execute(new GeometryReplaceCommand(path, before, path, "Change shape"));

                ctx.ViewModel.NotifyDocumentChanged();
                return DescribeShape(path);
            });

        Add("shape.detach",
            "Stop a path being a shape, keeping its outline exactly as it is. How a person says keep " +
            "this shape and let me edit it freely - its segments then move independently.",
            "itemId?:guid",
            (ctx, p) =>
            {
                PathItem path = RequireShape(ctx, p, out ShapeDefinition shape);
                ShapeDefinition was = path.DetachShape()!;
                ctx.Session.Execute(new GeometryReplaceCommand(
                    path, path.GeometrySnapshot(), path, "Detach shape"));

                ctx.ViewModel.NotifyDocumentChanged();
                return new { detached = true, was = ShapeLibrary.Name(was.Kind), itemId = path.Id };
            });

        Add("shape.bowSegment",
            "Bow one segment of a shape, and every equivalent segment with it. Positive is away from " +
            "the shape's centre and negative is toward it, so making a star's segment concave makes " +
            "all ten of them concave.",
            "itemId?:guid, segment:number, amount:number",
            (ctx, p) =>
            {
                PathItem path = RequireShape(ctx, p, out ShapeDefinition shape);
                int segment = (int)Math.Round(p.GetDouble("segment", 0));
                double amount = p.GetDouble("amount", 0);

                if (amount == 0)
                {
                    throw new EditorOperationException("Parameter 'amount' is required and must not be 0.");
                }

                IReadOnlyList<int> orbit = ShapeSymmetry.Orbit(path, segment);
                if (orbit.Count == 0)
                {
                    throw new EditorOperationException(
                        $"Segment {segment} is not on '{path.Name}'.");
                }

                var before = path.GeometrySnapshot();
                ShapeSymmetry.Bow(path, segment, amount);
                ctx.Session.Execute(new GeometryReplaceCommand(path, before, path, "Bow shape segments"));

                ctx.ViewModel.NotifyDocumentChanged();
                return new
                {
                    itemId = path.Id,
                    kind = ShapeLibrary.Name(shape.Kind),
                    segment,
                    amount,
                    moved = orbit,
                    symmetric = orbit.Count,
                };
            });

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

                // Where the art is **drawn** does not change: the command converts the geometry into the
                // destination layer's frame, so filing an object onto a page that sits elsewhere in the
                // sheet - or into a transformed group - leaves it where the driver put it (#174). A frame
                // change that cannot be made honestly is refused and reported rather than stored wrong.
                // Where the art is **drawn** does not change: the command converts the geometry into the
                // destination layer's frame, so filing an object onto a page that sits elsewhere in the
                // sheet - or into a transformed group - leaves it where the driver put it (#174). A frame
                // change that cannot be made honestly is refused and reported rather than stored wrong.
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

                // Measured in **document/world** coordinates, because that is the frame `x`/`y` are given in
                // and the frame the resulting translation is converted from. `SelectionBounds` only adds
                // the artboard origin, so on an object inside a group it answered in the object's own
                // frame and the top-left landed at the group transform applied to the point asked for
                // (#172); the composed bounds are the ones a person sees.
                Rect2D bounds = SelectionEngine.WorldBounds(ctx.Session.SelectedObjects.ToArray());
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

        Add("object.flip",
            "Mirror the selection across its own centre: horizontal swaps left and right, vertical " +
            "swaps top and bottom. A path is mirrored as geometry; text and images record the mirror " +
            "instead, so a run keeps its string and its glyph ids and an image keeps the file's own " +
            "samples - and a flipped text block stays editable. Flipping twice is the identity.",
            "axis?:horizontal|vertical|both (default horizontal)",
            (ctx, p) =>
            {
                string axis = (p.GetString("axis") ?? "horizontal").Trim().ToLowerInvariant();
                bool horizontal = axis is "horizontal" or "both" or "h";
                bool vertical = axis is "vertical" or "both" or "v";
                if (!horizontal && !vertical)
                {
                    throw new EditorOperationException(
                        "axis must be horizontal, vertical or both.");
                }

                ctx.Session.FlipSelection(horizontal, vertical);
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

        Add("style.setHatch",
            "Fill the selected paths with a hatch: families of parallel lines, drawn in the object's stroke " +
            "colour and clipped to the path's own outline - holes and concavities included. One family is an " +
            "angle and a spacing; `cross` adds the same family at right angles. `clear` takes the hatch off " +
            "again without disturbing the colour behind it.",
            "angle?:number (default 45), spacing?:number (default 4), width?:number (default 1), " +
            "cross?:bool, dashes?:number[] (default solid), rule?:nonzero|evenodd, clear?:bool",
            (ctx, p) =>
            {
                bool clear = p.GetBool("clear", false);
                double angle = p.GetDouble("angle", 45);
                double spacing = p.GetDouble("spacing", 4);
                double width = p.GetDouble("width", 1);
                bool cross = p.GetBool("cross", false);

                DashPattern dash = p.TryGetProperty("dashes", out JsonElement d) && d.ValueKind == JsonValueKind.Array
                    ? new DashPattern(d.EnumerateArray().Select(e => e.GetDouble()).ToArray(), 0)
                    : default;

                FillRule rule = string.Equals(p.GetString("rule"), "evenodd", StringComparison.OrdinalIgnoreCase)
                    ? FillRule.EvenOdd
                    : FillRule.NonZero;

                HatchSpec? hatch = clear
                    ? null
                    : cross
                        ? HatchSpec.Cross(spacing)
                        : HatchSpec.Single(angle, spacing, width, dash);

                if (hatch is not null && cross)
                {
                    // The cross factory does not carry the weight or the dash, so the drawn width is applied
                    // to both families here rather than leaving `width` silently ignored on that path.
                    hatch = new HatchSpec(hatch.Lines
                        .Select(l => l with { Width = width, Dash = dash })
                        .ToArray());
                }

                int changed = 0;
                foreach (PathItem path in ctx.Session.SelectedPaths())
                {
                    FillSpec next = hatch is null
                        ? path.Fill with { Hatch = null }
                        : new FillSpec(true, path.Fill.Color, rule, null, hatch);
                    ctx.Session.Execute(new SetFillCommand(path, next, path.Fill));
                    changed++;
                }

                return new
                {
                    changed,
                    hatched = hatch is not null,
                    families = hatch?.Lines.Count ?? 0,
                };
            });

        Add("style.setFillRule",
            "Set how the selected paths' fills decide what is inside their outline, leaving each " +
            "path's colour and gradient alone. style.setFill can do this too, but only by also " +
            "setting a solid colour, which would drop a gradient.",
            "rule:nonzero|evenodd",
            (ctx, p) =>
            {
                FillRule rule = string.Equals(p.GetString("rule"), "evenodd", StringComparison.OrdinalIgnoreCase)
                    ? FillRule.EvenOdd
                    : FillRule.NonZero;

                var edits = new List<IUndoableCommand>();
                foreach (PathItem path in ctx.Session.SelectedPaths())
                {
                    if (path.Fill.Rule != rule)
                    {
                        edits.Add(new SetFillCommand(path, path.Fill with { Rule = rule }, path.Fill));
                    }
                }

                ExecuteAll(ctx, edits, "Fill rule");

                return Summary(ctx);
            });

        Add("style.setGradientFocalPoint",
            "Move the highlight of the selected paths' radial gradients off centre, or take it away " +
            "again. The point is normalised to each object's bounds, which is the space " +
            "gradient.get reports `radial.focalPoint` in. A point outside the radial's ellipse is " +
            "CLAMPED onto its edge along the ray from the centre - the same rule the SVG reader " +
            "applies to a file that names one - so a driver and a file cannot disagree about where " +
            "an outside highlight lands. `clear` removes the focus, and so does a point on the " +
            "centre: a gradient with no focus paints exactly what one naming its centre paints.",
            "focalPoint?:{x,y} (normalised to the object's bounds), clear?:bool",
            (ctx, p) =>
            {
                bool clear = p.GetBool("clear", false);
                Point2D? requested = TryPoint(p, "focalPoint", out Point2D given) ? given : null;

                if (!clear && requested is null)
                {
                    throw new EditorOperationException(
                        "Give focalPoint:{x,y} to set a focus, or clear:true to remove one.");
                }

                var edits = new List<IUndoableCommand>();
                int radials = 0;
                int changed = 0;
                Point2D? result = null;

                foreach (PathItem path in ctx.Session.SelectedPaths().ToList())
                {
                    if (path.Fill.Gradient is not { Kind: GradientKind.Radial } gradient)
                    {
                        continue;
                    }

                    radials++;
                    GradientSpec next = (gradient with { FocalPoint = clear ? null : requested })
                        .WithClampedFocalPoint();
                    result = next.FocalPoint;

                    if (next.FocalPoint == gradient.FocalPoint)
                    {
                        continue;
                    }

                    edits.Add(new SetFillCommand(
                        path, FillSpec.WithGradient(next, path.Fill.Rule, path.Fill.Color), path.Fill));
                    changed++;
                }

                if (radials == 0)
                {
                    throw new EditorOperationException(
                        "None of the selected paths has a radial gradient. Use gradient.setKind to " +
                        "give one a radial ramp first.");
                }

                ExecuteAll(ctx, edits, "Gradient focal point");

                return new
                {
                    changed,
                    cleared = clear,
                    clamping = "a focus outside the ellipse is moved onto its edge, not refused",
                    focalPoint = result is { } point ? PointJson(point) : null,
                };
            });

        Add("style.setStroke", "Stroke the selected paths.",
            "color:[r,g,b], width:number, cap?:butt|round|square, join?:miter|round|bevel, miterLimit?, " +
            "alignment?:center|inside|outside, dash?:number[], index?:number, opacity?:number, " +
            "blend?:normal|multiply|screen|darken|lighten|overlay|color-dodge|color-burn|hard-light|soft-light|" +
            "difference|exclusion|hue|saturation|color|luminosity. index edits one stroke of the stack " +
            "and an omitted member is left as that stroke has it; without index the whole path is restroked and an " +
            "omitted member takes the default named above. An empty dash - dash:[] - is a request for a solid " +
            "line, not a member that was left out, so it is how a driver takes a dashed stroke back to Solid. " +
            "opacity and blend are per stroke and need an index: " +
            "they are what makes a path a thin line under a translucent highlight rather than one stroke.",
            (ctx, p) =>
            {
                // Only when it was actually given: ParseColor reports its fallback for a parameter that is
                // absent, so "no colour" would arrive as black and overwrite a path's own colour.
                ColorRgb? color = p.TryGetProperty("color", out _) ? p.ParseColor("color", ColorRgb.Black) : null;
                StrokeCap cap = ParseEnum(p.GetString("cap"), StrokeCap.Butt);
                StrokeJoin join = ParseEnum(p.GetString("join"), StrokeJoin.Miter);
                StrokeAlignment alignment = ParseEnum(p.GetString("alignment"), StrokeAlignment.Center);
                // **A given-but-empty dash is a request, not an absence.** `dash:[4,3]` asks for a pattern and
                // `dash:[]` asks for no pattern at all - the same "empty means none" reading `style.setWidthProfile`
                // takes of its points list. Reading both as "not given" left the one dash state a driver could not
                // state: the Dash combo can go back to Solid and a dashed stroke could never be made solid through
                // the registry. The absent member is still "leave it as the stroke has it", which is what `Given`
                // is for; `clearDash` carries the deliberate empty through to the session, because an empty
                // DashPattern and an unstated one are the same value and cannot be told apart there.
                bool clearDash = p.ValueKind == JsonValueKind.Object
                    && p.TryGetProperty("dash", out JsonElement askedForDash)
                    && askedForDash.ValueKind == JsonValueKind.Array
                    && askedForDash.GetArrayLength() == 0;
                DashPattern? dash = null;
                if (p.TryGetProperty("dash", out JsonElement d) && d.ValueKind == JsonValueKind.Array)
                {
                    double[] pattern = d.EnumerateArray().Select(e => e.GetDouble()).ToArray();
                    if (pattern.Length > 0)
                    {
                        dash = new DashPattern(pattern);
                    }
                }

                // A blend mode this build does not know is **refused by name**, not read as Normal: silently
                // painting a stroke over its backdrop when the caller asked for multiply gives a picture nobody
                // asked for and says nothing about it. The same reading the SVG reader takes of the same names.
                BlendMode? blend = null;
                if (Given(p, "blend"))
                {
                    blend = BlendModes.Parse(p.GetString("blend"))
                        ?? throw new EditorOperationException(
                            $"Unknown blend mode '{p.GetString("blend")}'. Known: " +
                            string.Join(", ", Enum.GetValues<BlendMode>().Select(m => m.ToSvgName())));
                }

                // index edits that stroke of the stack rather than the top one, which is what the stroke
                // inspector does when a person picks a stroke. Without it a driver could not reach the same
                // stroke a person can, and a capability that exists only in the UI is a defect here.
                int? index = p.ValueKind == JsonValueKind.Object && p.TryGetProperty("index", out _)
                    ? (int)p.GetLong("index", 0)
                    : null;

                if (index is { } at)
                {
                    // **An omitted member is left as the stroke has it.** Naming one member of one stroke must not
                    // reset the others to this operation's defaults: a width of 8 becoming 1 because the caller only
                    // asked for a round cap is the driver having to supply every member a person never touched. The
                    // un-indexed call below is deliberately different, because there the defaults *are* the request -
                    // it replaces the stack rather than editing a member of it.
                    ctx.Session.ApplyStrokeFieldsAt(
                        at,
                        Given(p, "width") ? p.GetDouble("width") : null,
                        Given(p, "cap") ? cap : null,
                        Given(p, "join") ? join : null,
                        Given(p, "miterLimit") ? p.GetDouble("miterLimit") : null,
                        Given(p, "alignment") ? alignment : null,
                        dash,
                        color,
                        Given(p, "opacity") ? p.GetDouble("opacity") : null,
                        blend,
                        clearDash);
                }
                else
                {
                    // The per-stroke paint members are refused without an index rather than being applied to the
                    // top stroke, because there is no honest way to choose: "set the opacity" on a path with three
                    // strokes could mean the first, the last, or all three, and each is a different document.
                    if (Given(p, "opacity") || blend is not null)
                    {
                        throw new EditorOperationException(
                            "opacity and blend belong to one stroke of the stack, so they need an index. " +
                            "Without it there is no way to say which stroke was meant.");
                    }

                    ctx.Session.ApplyStroke(
                        p.GetDouble("width", 1), cap, join, p.GetDouble("miterLimit", 4), alignment, dash, color);
                }
                return Summary(ctx);
            });

        Add("style.clearStroke", "Remove the stroke from the selected paths.", "", (ctx, _) =>
        {
            ctx.Session.ClearStroke();
            return Summary(ctx);
        });

        Add("style.setWidthProfile",
            "Give the selected paths' strokes a width profile - a stroke whose width changes along its length, " +
            "and can change differently on each side. points is [{position, left, right, interpolation?}], where " +
            "position runs 0 at the start of the path to 1 at the end and left/right are the widths on each " +
            "side. A negative width is clamped at zero, the way the canvas clamps a dragged grip: a negative " +
            "half-width would put the offset edge across the centreline and draw the stroke inside out. " +
            "An empty points list clears the profile and leaves the stroke's own width, which is what " +
            "removing a profile does. strokeIndex picks one stroke of the stack, counted from the bottom, and " +
            "defaults to every stroke; a path whose stack is shorter is skipped. One undo step per path.",
            "points:[{position:number, left:number, right:number, interpolation?:linear|cubic}], name?:string, " +
            "strokeIndex?:number",
            (ctx, p) =>
            {
                List<WidthPoint> points = ReadWidthPoints(p);
                string name = p.GetString("name") is { Length: > 0 } given ? given : "Profile";

                // Through the session, so the stroke pane's type box and this operation write one profile the same
                // way rather than two ways that agree until somebody changes one of them.
                if (OptionalStrokeIndex(p) is { } at)
                {
                    WidthProfileSpec? profile = points.Count == 0 ? null : new WidthProfileSpec(name, points);
                    return new
                    {
                        changed = ctx.Session.SetWidthProfileAt(at, profile),
                        points = points.Count,
                        strokeIndex = at,
                    };
                }

                int changed = 0;

                foreach (PathItem path in ctx.Session.SelectedPaths().ToList())
                {
                    var stack = path.Strokes.ToList();
                    for (int i = 0; i < stack.Count; i++)
                    {
                        // An empty list clears rather than setting an empty profile, so "no profile" and "a
                        // profile that says nothing" are one state rather than two that behave the same.
                        stack[i] = points.Count == 0
                            ? stack[i] with { WidthProfile = null }
                            : stack[i] with { WidthProfile = new WidthProfileSpec(name, points) };
                    }

                    ctx.Session.Execute(new SetStrokesCommand(path, stack, "Width profile"));
                    changed++;
                }

                return new { changed, points = points.Count };
            });

        Add("pathEffect.list",
            "The live path effects this build implements, and the live path effect each selected path says it has. " +
            "A path's live path effect is a reference to an element the file defined elsewhere, and this build has " +
            "no place to keep that element, so the reference travels with the path as one of the foreign attributes " +
            "it carries and is reported here by id. sourcePathData is what the file wrote in inkscape:original-d - " +
            "the path the effect was applied to, as opposed to the effect's output that the path's own geometry " +
            "holds - which is what a translation is built from.",
            "",
            (ctx, _) => new
            {
                implemented = PathEffects.Implemented,
                items = ctx.Session.SelectedObjects
                    .Select(item => new
                    {
                        itemId = item.Id,
                        name = item.Name,
                        effect = PathEffects.ReferenceOn(item),
                        sourcePathData = PathEffects.SourcePathData(item),
                    })
                    .ToArray(),
            });

        Add("pathEffect.translate",
            "Read a live path effect the way a file spells it and report what it becomes in this model, without " +
            "changing anything. effect is the effect's own name ('powerstroke', 'bend_path', ...) and parameters is " +
            "the effect element's other attributes, keyed as the file spells them - so a driver can hand over an " +
            "element exactly as it was read. An effect this build does not implement comes back with supported " +
            "false, the reason naming it, and no profile: the geometry is left alone rather than redrawn without " +
            "the effect. The profile is built against the selected path, because Inkscape stores a powerstroke's " +
            "knots as a segment index and the same knots sit in different places on a longer path.",
            "effect:string, id?:string, version?:string, parameters?:{string:string}",
            (ctx, p) =>
            {
                PathEffectSpec effect = ReadPathEffect(p);
                PathItem path = ctx.Session.SelectedPaths().FirstOrDefault()
                    ?? throw new EditorOperationException(
                        "pathEffect.translate needs the path the effect is on, because a powerstroke's knots are " +
                        "positions along it");

                StrokeSpec stroke = path.Strokes.FirstOrDefault() ?? StrokeSpec.None;
                PathEffectTranslation translation = PathEffects.Translate(effect, path, stroke);

                return new
                {
                    effect = effect.Effect,
                    id = effect.Id,
                    segments = PathEffects.CurveCount(path),
                    supported = translation.IsSupported,
                    refusal = translation.Refusal,
                    notes = translation.Notes,
                    join = translation.Stroke?.Join.ToString().ToLowerInvariant(),
                    cap = translation.Stroke?.Cap.ToString().ToLowerInvariant(),
                    profile = translation.Stroke?.WidthProfile is { } profile
                        ? new
                        {
                            name = profile.Name,
                            points = DescribeWidthPoints(profile),
                        }
                        : null,
                };
            });

        Add("pathEffect.apply",
            "Translate a live path effect and give the selected paths' strokes the width profile it describes, as " +
            "the file intends: the effect is converted for drawing and the file's own description is left where it " +
            "is, on the path, so the export still says what the artwork is. The profile is registered in the " +
            "document's library, so a converted powerstroke is a reusable asset rather than a stroke naming one " +
            "that does not exist. An effect this build does not implement changes nothing and is reported in " +
            "refused, by name. strokeIndex picks one stroke of the stack, counted from the bottom, and defaults to " +
            "every stroke; a path whose stack is shorter is skipped. One undo step.",
            "effect:string, id?:string, version?:string, parameters?:{string:string}, name?:string, strokeIndex?:number",
            (ctx, p) =>
            {
                PathEffectSpec effect = ReadPathEffect(p);
                List<PathItem> paths = ctx.Session.SelectedPaths().ToList();
                if (paths.Count == 0)
                {
                    throw new EditorOperationException("pathEffect.apply needs at least one path selected");
                }

                CadDocument document = ctx.Document;
                var library = document.WidthProfiles.ToList();
                var edits = new List<EditWidthProfilesCommand.StrokeEdit>();
                var refused = new List<object>();
                var profiles = new List<string>();
                int? only = OptionalStrokeIndex(p);
                string baseName = p.GetString("name") is { Length: > 0 } given
                    ? given
                    : effect.Id is { Length: > 0 } ? effect.Id : "Power stroke";

                foreach (PathItem path in paths)
                {
                    var stack = path.Strokes.ToList();
                    for (int i = 0; i < stack.Count; i++)
                    {
                        if (only is { } at && at != i)
                        {
                            continue;
                        }

                        PathEffectTranslation translation = PathEffects.Translate(effect, path, stack[i]);
                        if (translation.Stroke is null)
                        {
                            refused.Add(new { itemId = path.Id, name = path.Name, reason = translation.Refusal });
                            continue;
                        }

                        // One asset per distinct profile: two paths of different length carry different knots for
                        // the same effect, so a shared name would put one path's widths on the other.
                        WidthProfileSpec profile = translation.Stroke.WidthProfile!;
                        if (library.FirstOrDefault(existing => existing.Name == baseName) is { } taken &&
                            taken.Points.SequenceEqual(profile.Points))
                        {
                            profile = taken;
                        }
                        else if (library.Any(existing => existing.Name == baseName))
                        {
                            int suffix = 2;
                            while (library.Any(existing => existing.Name == $"{baseName} {suffix}"))
                            {
                                suffix++;
                            }

                            profile = profile with { Name = $"{baseName} {suffix}" };
                            library.Add(profile);
                        }
                        else
                        {
                            profile = profile with { Name = baseName };
                            library.Add(profile);
                        }

                        if (!profiles.Contains(profile.Name))
                        {
                            profiles.Add(profile.Name);
                        }

                        edits.Add(new EditWidthProfilesCommand.StrokeEdit(
                            path, i, stack[i], translation.Stroke with { WidthProfile = profile }));
                    }
                }

                if (edits.Count > 0)
                {
                    ctx.Session.Execute(new EditWidthProfilesCommand(
                        document, library, edits, "Apply live path effect"));
                }

                return new
                {
                    effect = effect.Effect,
                    strokes = edits.Count,
                    profiles = profiles.ToArray(),
                    refused = refused.ToArray(),
                };
            });

        Add("style.addStrokeEffect",
            "Add an outline effect to the selected paths' strokes, on top of the ones they have. kind is one of " +
            "offsetPath, roughen, zigZag or scribble. size is how far a point may move (or how far an offset path " +
            "moves the edges), detail is how many passes a scribble draws, and seed is what makes a random-looking " +
            "effect the same every time it is drawn - the same document must render and export identically. Every " +
            "other parameter the kind declares (see style.effectParameters) is taken from the request under that " +
            "name, so an effect can be added with its settings in one call. strokeIndex picks one stroke of the " +
            "stack, counted from the bottom, and defaults to every stroke; a path whose stack is shorter is skipped. " +
            "One undo step per path.",
            "kind:string, size?:number, detail?:number, seed?:number, strokeIndex?:number, " +
            "ridges?:number, smooth?:number, join?:number, density?:number, overlap?:number, width?:number, " +
            "curviness?:number, scatter?:number",
            (ctx, p) =>
            {
                string kind = p.GetString("kind") ?? string.Empty;

                // The registry decides what a kind is and what it takes, so this operation cannot drift from the
                // panel's editors - and a new effect is accepted here by being declared there, not by being added
                // to a switch in each place.
                EffectDefinition? definition = EffectRegistry.Find(kind);
                if (definition is null || definition.Raster || definition.OutlineKind is not { } parsed)
                {
                    throw new EditorOperationException(
                        $"'{kind}' is not an outline effect; use " +
                        string.Join(", ", EffectRegistry.All.Where(e => !e.Raster).Select(e => e.Kind)));
                }

                var effect = new OutlineEffectSpec(
                    parsed,
                    p.GetDouble("size", 2.0),
                    p.GetDouble("detail", 1.0),
                    (int)p.GetLong("seed", 1));

                // Every **other** parameter the kind declares is read from the request under the name the
                // declaration gives it. Hand-building the record above meant the registry could declare a parameter
                // this operation silently ignored: a driver had to add the effect and then set the value in a
                // second call, while a person could do it in one - the parity defect this project treats as a bug.
                // Reading them from the declaration is what makes the comment above true rather than aspirational.
                effect = ApplyDeclaredEffectParameters(effect, definition, p);

                // Through the session, so the stroke pane's Add button and this run one implementation.
                int? strokeIndex = OptionalStrokeIndex(p);
                return new
                {
                    effect = parsed.ToString(),
                    strokeIndex,
                    changed = ctx.Session.AddOutlineEffect(effect, strokeIndex),
                };
            });

        Add("style.clearStrokeEffects",
            "Remove every outline effect from the selected paths' strokes. One undo step per path.",
            "",
            (ctx, _) =>
            {
                int changed = 0;
                foreach (PathItem path in ctx.Session.SelectedPaths().ToList())
                {
                    var stack = path.Strokes.ToList();
                    for (int i = 0; i < stack.Count; i++)
                    {
                        stack[i] = stack[i] with { Effects = null };
                    }

                    ctx.Session.Execute(new SetStrokesCommand(path, stack, "Clear stroke effects"));
                    changed++;
                }

                return new { changed };
            });

        Add("style.addRasterEffect",
            "Add a raster effect to the selected paths' strokes. kind is blur, dropShadow, innerGlow or " +
            "outerGlow. radius is how far it spreads, offsetX/offsetY displace a drop shadow, opacity is the " +
            "effect's own, and tint is its colour - omitted means the stroke's own colour, which is the usual " +
            "answer for a glow. strokeIndex picks one stroke of the stack, counted from the bottom, and defaults " +
            "to every stroke; a path whose stack is shorter is skipped. One undo step per path.",
            "kind:string, radius?:number, offsetX?:number, offsetY?:number, opacity?:number, tint?:[r,g,b], " +
            "strokeIndex?:number",
            (ctx, p) =>
            {
                string kind = p.GetString("kind") ?? string.Empty;

                // Through the registry, for the same reason the outline effects are: one declaration of what a
                // kind is, so the operation and the panel cannot disagree.
                EffectDefinition? definition = EffectRegistry.Find(kind);
                if (definition is null || !definition.Raster || definition.RasterKind is not { } parsed)
                {
                    throw new EditorOperationException(
                        $"'{kind}' is not a raster effect; use " +
                        string.Join(", ", EffectRegistry.All.Where(e => e.Raster).Select(e => e.Kind)));
                }

                // Presence-checked like every other colour: ParseColor reports its fallback for an absent
                // parameter, so an omitted tint would arrive as black and stop meaning "the stroke's own colour".
                ColorRgb? tint = p.ValueKind == JsonValueKind.Object && p.TryGetProperty("tint", out _)
                    ? p.ParseColor("tint", ColorRgb.Black)
                    : null;

                var effect = new RasterEffectSpec(
                    parsed,
                    p.GetDouble("radius", 4.0),
                    p.GetDouble("offsetX", 0.0),
                    p.GetDouble("offsetY", 0.0),
                    p.GetDouble("opacity", 1.0),
                    tint);

                // Through the session, for the same reason: one implementation, called by the pane and by this.
                int? strokeIndex = OptionalStrokeIndex(p);
                return new
                {
                    effect = parsed.ToString(),
                    tinted = tint is not null,
                    strokeIndex,
                    changed = ctx.Session.AddRasterEffect(effect, strokeIndex),
                };
            });

        Add("style.clearRasterEffects",
            "Remove every raster effect from the selected paths' strokes. One undo step per path.",
            "",
            (ctx, _) =>
            {
                int changed = 0;
                foreach (PathItem path in ctx.Session.SelectedPaths().ToList())
                {
                    var stack = path.Strokes.ToList();
                    for (int i = 0; i < stack.Count; i++)
                    {
                        stack[i] = stack[i] with { RasterEffects = null };
                    }

                    ctx.Session.Execute(new SetStrokesCommand(path, stack, "Clear raster effects"));
                    changed++;
                }

                return new { changed };
            });

        Add("style.setDynamics",
            "Give the selected paths' strokes a tablet response. target is width (the default), opacity, " +
            "scatterScale, calligraphicAngle or smoothing. preset is linear, soft, hard or exponential, or pass " +
            "curve:[x1,y1,x2,y2] for a custom one - the two control points of a curve from (0,0) to (1,1), the " +
            "same four numbers a curve editor drags. enabled defaults to true, which is what switching a target " +
            "on is; enabled:false switches that target off and **leaves every other target as it was**, which is " +
            "what the pane's checkbox does and what style.clearDynamics cannot say - clearing removes the whole " +
            "response rather than recording that one target is off. strokeIndex picks one stroke of the stack, " +
            "counted from the bottom, and defaults to every stroke; a path whose stack is shorter is skipped. " +
            "One undo step per path.",
            "target?:string, preset?:string, curve?:[x1,y1,x2,y2], enabled?:boolean, strokeIndex?:number",
            (ctx, p) =>
            {
                // Read rather than hard-coded: `enabled:false` was silently ignored, and a target that ends up
                // **on** when the caller asked for off is worse than a refusal, because nothing is reported. The
                // other targets are carried over by the session, so switching width off does not disturb opacity.
                bool enabled = p.ValueKind == JsonValueKind.Object ? p.GetBool("enabled", true) : true;
                string targetName = p.GetString("target") ?? "width";
                DynamicsTarget target = targetName.ToLowerInvariant() switch
                {
                    "width" => DynamicsTarget.Width,
                    "opacity" => DynamicsTarget.Opacity,
                    "scatterscale" or "scatter_scale" => DynamicsTarget.ScatterScale,
                    "calligraphicangle" or "calligraphic_angle" or "angle" => DynamicsTarget.CalligraphicAngle,
                    "smoothing" or "speed" => DynamicsTarget.Smoothing,
                    _ => throw new EditorOperationException(
                        $"'{targetName}' is not a dynamics target; use width, opacity, scatterScale, " +
                        "calligraphicAngle or smoothing"),
                };

                DynamicsCurve curve = ReadDynamicsCurve(p);

                // Through the session, so a panel's checkbox and this operation run one implementation - and the
                // other targets are carried over there, which is what makes this an edit of one target rather than
                // a rebuild of the whole response.
                if (OptionalStrokeIndex(p) is { } at)
                {
                    return new
                    {
                        target = target.ToString(),
                        changed = ctx.Session.SetDynamicsAt(at, target, enabled, curve),
                        strokeIndex = at,
                    };
                }

                int changed = 0;
                foreach (PathItem path in ctx.Session.SelectedPaths().ToList())
                {
                    var stack = path.Strokes.ToList();
                    for (int i = 0; i < stack.Count; i++)
                    {
                        // The other targets keep what they had: setting width dynamics must not switch off opacity
                        // dynamics, which rebuilding the spec from scratch would quietly do.
                        StrokeSpec existingStroke = stack[i];
                        var spec = new DynamicsSpec(Enum.GetValues<DynamicsTarget>().Select(existing =>
                            existing == target
                                ? new DynamicsTargetSpec(enabled, curve)
                                : existingStroke.Dynamics?.For(existing) ?? DynamicsTargetSpec.Off));

                        stack[i] = existingStroke with { Dynamics = spec };
                    }

                    ctx.Session.Execute(new SetStrokesCommand(path, stack, "Set tablet dynamics"));
                    changed++;
                }

                return new { target = target.ToString(), changed };
            });

        Add("style.clearDynamics",
            "Remove the tablet response from the selected paths' strokes. strokeIndex picks one stroke of the stack, " +
            "counted from the bottom, and defaults to every stroke; a path whose stack is shorter is skipped. One " +
            "undo step per path.",
            "strokeIndex?:number",
            (ctx, p) =>
            {
                // Through the session, so the pane's button and this clear the same way - and so one stroke can be
                // named instead of every stroke of the selection.
                if (OptionalStrokeIndex(p) is { } at)
                {
                    return new { changed = ctx.Session.ClearDynamicsAt(at), strokeIndex = at };
                }

                int changed = 0;
                foreach (PathItem path in ctx.Session.SelectedPaths().ToList())
                {
                    var stack = path.Strokes.ToList();
                    for (int i = 0; i < stack.Count; i++)
                    {
                        stack[i] = stack[i] with { Dynamics = null };
                    }

                    ctx.Session.Execute(new SetStrokesCommand(path, stack, "Clear tablet dynamics"));
                    changed++;
                }

                return new { changed };
            });

        Add("object.setBlendMode",
            "How the selected objects combine with what is drawn beneath them. mode is a CSS mix-blend-mode name " +
            "(normal, multiply, screen, darken, lighten, overlay, difference and the rest); an empty or 'normal' " +
            "mode clears it. The exported SVG carries the property, so a file written here composites the same way " +
            "in another viewer.",
            "mode:string",
            (ctx, p) =>
            {
                string name = p.GetString("mode") ?? string.Empty;
                BlendMode mode = BlendModes.Parse(name) ?? BlendMode.Normal;
                if (name.Length > 0 && !name.Equals("normal", StringComparison.OrdinalIgnoreCase) &&
                    BlendModes.Parse(name) is null)
                {
                    throw new EditorOperationException($"'{name}' is not a blend mode this build knows");
                }

                var items = ctx.Session.SelectedObjects.ToList();
                foreach (LayerItem item in items)
                {
                    item.BlendMode = mode;
                }

                ctx.ViewModel.NotifyDocumentChanged();
                return new { mode = mode.ToSvgName(), items = items.Count };
            });

        Add("document.exportSupport",
            "What the PDF export carries and what it does not, from the **one** list the exporter and any warning " +
            "are drawn from. A feature that is not written exports as the artwork without it: a legal file, and not " +
            "the picture the author drew - which is why it is worth saying before somebody opens the export and " +
            "finds the effect missing.",
            "",
            (ctx, _) => new
            {
                carries = PdfExportSupport.All.Select(feature => new
                {
                    feature = feature.Name,
                    written = feature.Written,
                    note = feature.Note,
                }).ToArray(),
                lossy = PdfExportSupport.Lossy.Select(feature => feature.Name).ToArray(),
            });

        Add("document.metadata",
            "The namespaced data the file carried that the model has no meaning for: the root-level elements kept " +
            "verbatim (Inkscape's named view, the RDF), the namespace prefixes declared, and, for the selection, " +
            "the foreign attributes and child elements on its items. Read-only - the model carries these rather " +
            "than interpreting them, and they go back out with the same names they came in with. The foreign " +
            "path effects no path refers to are listed by the id and the effect name the stored element " +
            "carries, beside the count - so a driver can tell which entry survived and not only how many.",
            "",
            (ctx, _) => new
            {
                extras = ctx.Document.SvgExtras.Count,
                foreignPathEffects = ctx.Document.ForeignPathEffects.Count,

                // The count's names beside it, in the shape `namespaces` uses, so a driver can tell **which**
                // library entry survived rather than only that one did (issue #163). The count is kept as it was:
                // it is not wrong, only thin, and a reply a test already pins does not move.
                foreignPathEffectNames = KeptForeignPathEffects(ctx.Document).Names,
                namespaces = ctx.Document.SvgNamespaces
                    .Select(entry => new { prefix = entry.Key, uri = entry.Value })
                    .ToArray(),
                selection = ctx.Session.SelectedObjects.Select(item => new
                {
                    itemId = item.Id,
                    name = item.Name,
                    attributes = item.ForeignAttributes
                        .Select(entry => new { name = entry.Key, value = entry.Value })
                        .ToArray(),
                    elements = item.ForeignElements.Count,
                }).ToArray(),
            });

        // Filters are a document asset a driver can **edit**, not only replace: the graph is the filter, so a
        // primitive is added, wired, retuned and removed in place. Every kind and every parameter is declared once,
        // in FilterPrimitiveRegistry, and these operations refuse anything that declaration does not have rather
        // than guessing - an unknown kind, a parameter the kind does not take, a buffer nothing produces.
        //
        // **A filter edit is one undo step.** The library and every item that refers to it move together through
        // `FilterEditCommand`, on the same command stack every other edit uses, so a driver's Undo is the person's -
        // and an edit that changes nothing, such as setting a parameter to the value it already had, takes no step
        // at all rather than making someone press Undo twice for a change they never made.
        //
        // **The PDF export writes a filter.** The filtered object is rasterised into its region, the graph runs over
        // those pixels and the answer is placed as an image, which is what `PdfExportSupport` records. A graph that
        // reads the backdrop cannot be evaluated by an exporter that draws one item at a time, so that case and an
        // unallocatable region export unfiltered and say so in the export notes.
        Add("filter.list",
            "The document's filters, with the primitives each holds and the wiring between them. A filter is a " +
            "directed graph rather than a list of effects, so what is reported is what each step reads and what it " +
            "calls its answer, not just the order. Every kind's parameters are declared in filter.kinds, which is " +
            "what a panel is drawn from. The filter's own settings are reported too: `units` is the region's " +
            "coordinate system, `primitiveUnits` is what a primitive's own lengths are measured in, and `filterRes` " +
            "is the pixel resolution the filter is evaluated at (null when the caller's scale decides), so a reader " +
            "can see both settings and send them straight back through filter.create or filter.setRegion.",
            "",
            (ctx, _) => ctx.Document.Filters.Select(DescribeFilter).ToArray());

        Add("filter.kinds",
            "Every primitive this build has, with the parameters each one takes, which of them are required and what " +
            "each means. This is the declaration the other filter operations validate against, so a panel or a " +
            "driver can be generated from it rather than from a hand-written list that falls out of step; a " +
            "parameter added to a kind appears here by existing.",
            "",
            (ctx, _) => FilterPrimitiveRegistry.All.Select(definition => new
            {
                kind = definition.Kind,
                element = definition.Element,
                meaning = definition.Meaning,
                parameters = definition.Parameters.Select(parameter => new
                {
                    name = parameter.Name,
                    kind = parameter.Kind.ToString().ToLowerInvariant(),
                    meaning = parameter.Meaning,
                    required = parameter.Required,
                    @default = parameter.Default,
                    minimum = double.IsNegativeInfinity(parameter.Minimum) ? (double?)null : parameter.Minimum,
                    maximum = double.IsPositiveInfinity(parameter.Maximum) ? (double?)null : parameter.Maximum,
                    choices = parameter.Choices,
                }).ToArray(),
            }).ToArray());

        Add("filter.create",
            "Create or replace a filter. primitives is [{kind, in?, in2?, result?, ...}] where kind names any " +
            "primitive filter.kinds lists - the kind name or the element name, so `morphology` and `feMorphology` " +
            "are one step - and every other member is a parameter that kind declares, under the name the " +
            "declaration gives it. in/in2 name the buffers a step " +
            "reads: SourceGraphic, SourceAlpha, or another step's result. The wiring is the filter: a step that reads " +
            "a named buffer gets that buffer, not whatever happened to run before it. output names the buffer the " +
            "filter answers with, which may be an intermediate step's; omitted means the last primitive's. An unknown " +
            "kind, a parameter the kind does not take, a name nothing produces and a graph that reads its own output " +
            "are all refused rather than guessed. `primitiveUnits` is what a primitive's own lengths are measured " +
            "in - one model unit under SVG's default userSpaceOnUse, or the shape's box under objectBoundingBox - " +
            "and `filterRes` is the resolution the filter is evaluated at: one number for both axes or a pair of " +
            "them, and null to let the caller's own scale decide. A resolution this build will not allocate is " +
            "refused here rather than stored, because the engine refuses it at draw time and a filter that throws " +
            "while painting takes the artwork with it. The library is document state, so creating or replacing a " +
            "filter is one undo step like any other edit.",
            "name:string, primitives:[{kind,in?,in2?,result?,...}], x?, y?, width?, height?, userSpace?:bool, " +
            "primitiveUnits?:string, filterRes?:number|[x,y], output?:string",
            (ctx, p) =>
            {
                string name = p.GetString("name")
                    ?? throw new EditorOperationException("Parameter 'name' is required.");

                var primitives = new List<FilterPrimitive>();
                if (p.TryGetProperty("primitives", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement entry in list.EnumerateArray())
                    {
                        primitives.Add(ReadPrimitive(entry));
                    }
                }

                if (primitives.Count == 0)
                {
                    throw new EditorOperationException(
                        "a filter needs at least one primitive; one with none paints nothing at all");
                }

                var filter = new FilterSpec(name, primitives)
                {
                    X = p.GetDouble("x", -0.1),
                    Y = p.GetDouble("y", -0.1),
                    Width = p.GetDouble("width", 1.2),
                    Height = p.GetDouble("height", 1.2),
                    ObjectBoundingBox = !p.GetBool("userSpace", false),
                    PrimitiveUnitsObjectBoundingBox = OptionalPrimitiveUnits(p) ?? false,
                    Output = p.GetString("output") ?? string.Empty,
                };

                if (Given(p, "filterRes"))
                {
                    (int X, int Y)? resolution = ReadFilterRes(p);
                    RequireAllocatableFilterRes(resolution);
                    filter = filter with
                    {
                        FilterResolutionX = resolution?.X,
                        FilterResolutionY = resolution?.Y,
                    };
                }

                ValidateGraph(filter);
                ReplaceFilter(ctx, filter, "Create filter");
                return new
                {
                    created = name,
                    primitives = primitives.Count,
                    output = filter.Output,
                    units = filter.ObjectBoundingBox ? "objectBoundingBox" : "userSpaceOnUse",
                    primitiveUnits = filter.PrimitiveUnitsObjectBoundingBox ? "objectBoundingBox" : "userSpaceOnUse",
                    filterRes = FilterResPair(filter),
                };
            });

        Add("filter.setRegion",
            "Where a filter is evaluated, what a primitive's own lengths are measured in, and what the filter answers " +
            "with - the filter's own settings rather than a step's. The region decides whether a blur near an edge " +
            "grows into the margin or is cut off, so it is not decoration, and `primitiveUnits` decides whether a " +
            "blur's radius is a length in the document or a fraction of the shape's box. Only the members given " +
            "change; an empty output means the last primitive's result, which is SVG's own rule. `filterRes` is one " +
            "number for both axes or a pair, and null to clear it; a resolution this build will not allocate is " +
            "refused rather than stored, because the engine refuses it at draw time and a filter that throws while " +
            "painting takes the artwork with it.",
            "name:string, x?:number, y?:number, width?:number, height?:number, userSpace?:bool, " +
            "primitiveUnits?:string, filterRes?:number|[x,y]|null, output?:string",
            (ctx, p) =>
            {
                FilterSpec filter = RequireFilter(ctx, p.GetString("name"));
                bool given = p.ValueKind == JsonValueKind.Object;

                double width = given && p.TryGetProperty("width", out _) ? p.GetDouble("width", filter.Width) : filter.Width;
                double height = given && p.TryGetProperty("height", out _) ? p.GetDouble("height", filter.Height) : filter.Height;
                if (width <= 0 || height <= 0)
                {
                    throw new EditorOperationException(
                        $"a region of {width} by {height} has no area, so nothing would be evaluated - a filter " +
                        "that paints nothing is not a filter");
                }

                string output = given && p.TryGetProperty("output", out _)
                    ? p.GetString("output") ?? string.Empty
                    : filter.Output;

                bool hasPrimitiveUnits = given && p.TryGetProperty("primitiveUnits", out _);
                bool hasResolution = given && p.TryGetProperty("filterRes", out _);
                (int X, int Y)? resolution = hasResolution ? ReadFilterRes(p) : null;
                if (hasResolution)
                {
                    RequireAllocatableFilterRes(resolution);
                }

                FilterSpec edited = filter with
                {
                    X = given && p.TryGetProperty("x", out _) ? p.GetDouble("x", filter.X) : filter.X,
                    Y = given && p.TryGetProperty("y", out _) ? p.GetDouble("y", filter.Y) : filter.Y,
                    Width = width,
                    Height = height,
                    ObjectBoundingBox = given && p.TryGetProperty("userSpace", out _)
                        ? !p.GetBool("userSpace", false)
                        : filter.ObjectBoundingBox,
                    PrimitiveUnitsObjectBoundingBox = hasPrimitiveUnits
                        ? ReadPrimitiveUnits(p)
                        : filter.PrimitiveUnitsObjectBoundingBox,
                    FilterResolutionX = hasResolution ? resolution?.X : filter.FilterResolutionX,
                    FilterResolutionY = hasResolution ? resolution?.Y : filter.FilterResolutionY,
                    Output = output,
                };

                ValidateGraph(edited);
                ReplaceFilter(ctx, edited, "Set filter region");
                return DescribeFilter(edited);
            });

        Add("filter.delete",
            "Delete a filter and clear it from every item that drew through it. The items keep their geometry and " +
            "their appearance - only the filter goes - which is what makes deleting an asset safe rather than " +
            "destructive, and matching profile.delete. One undo step restores both halves: the filter and the " +
            "references to it, which is why they are captured together rather than as two edits.",
            "name:string",
            (ctx, p) =>
            {
                CadDocument document = ctx.Document;
                string name = p.GetString("name") ?? string.Empty;
                if (document.FindFilter(name) is null)
                {
                    throw new EditorOperationException($"there is no filter called '{name}'");
                }

                var cleared = new List<FilterEditCommand.ItemEdit>();
                foreach (LayerItem item in document.AllItems().ToList())
                {
                    if (item.FilterId == name)
                    {
                        cleared.Add(new FilterEditCommand.ItemEdit(item, name, null));
                    }
                }

                IReadOnlyList<FilterSpec> library =
                    document.Filters.Where(entry => entry.Name != name).ToList();
                EditFilters(ctx, library, cleared, "Delete filter");
                return new { deleted = name, cleared = cleared.Count };
            });

        Add("filter.addPrimitive",
            "Add one step to a filter, at index (the end by default), with the parameters that step's kind takes - " +
            "the same names filter.kinds declares. The step lands unwired unless in/in2/result are given: a buffer " +
            "left out is the previous step's result, and filter.connectPrimitive is what names one explicitly. A " +
            "kind this build does not have, a parameter the kind does not take, a name nothing produces, a result " +
            "another step already claims and a wiring that runs in a circle are all refused.",
            "name:string, kind:string, index?:number, in?:string, in2?:string, result?:string, <the kind's own " +
            "parameters>",
            (ctx, p) =>
            {
                FilterSpec filter = RequireFilter(ctx, p.GetString("name"));
                FilterPrimitive primitive = ReadPrimitive(p, "name", "index");

                int index = p.ValueKind == JsonValueKind.Object && p.TryGetProperty("index", out _)
                    ? (int)p.GetLong("index", filter.Primitives.Count)
                    : filter.Primitives.Count;
                if (index < 0 || index > filter.Primitives.Count)
                {
                    throw new EditorOperationException(
                        $"'{filter.Name}' has {filter.Primitives.Count} primitives, so there is nowhere to insert " +
                        $"one at {index}");
                }

                List<FilterPrimitive> primitives = filter.Primitives.ToList();
                primitives.Insert(index, primitive);

                FilterSpec edited = filter with { Primitives = primitives };
                ValidateGraph(edited);
                ReplaceFilter(ctx, edited, "Add filter primitive");
                return new { name = edited.Name, index, kind = primitive.Kind.ToString(), primitives = primitives.Count };
            });

        Add("filter.removePrimitive",
            "Remove one step from a filter. A step another step still reads is refused rather than removed, because " +
            "its consumer would be left reading a name nothing produces and would silently receive a transparent " +
            "buffer - rewire the consumer with filter.connectPrimitive first, or the picture changes somewhere the " +
            "edit does not point at. A filter cannot lose its last primitive: one with none paints nothing, so " +
            "delete the filter instead.",
            "name:string, index:number",
            (ctx, p) =>
            {
                FilterSpec filter = RequireFilter(ctx, p.GetString("name"));
                int index = PrimitiveIndex(p, filter);
                FilterPrimitive removed = filter.Primitives[index];

                List<string> readers = filter.ConsumersOf(removed.Result)
                    .Where(consumer => !ReferenceEquals(consumer, removed))
                    .Select(consumer => consumer.Kind.ToString())
                    .ToList();
                if (removed.Result.Length > 0 && readers.Count > 0)
                {
                    throw new EditorOperationException(
                        $"'{removed.Result}' is read by {string.Join(", ", readers)}, so removing its producer would " +
                        "leave them reading a buffer nothing makes");
                }

                if (removed.Result.Length > 0 && filter.Output == removed.Result)
                {
                    throw new EditorOperationException(
                        $"'{removed.Result}' is the filter's own output, so removing its producer would leave the " +
                        "filter with nothing to answer with");
                }

                if (filter.Primitives.Count <= 1)
                {
                    throw new EditorOperationException(
                        $"'{filter.Name}' would be left with no primitives, which paints nothing - delete the filter " +
                        "instead");
                }

                List<FilterPrimitive> primitives = filter.Primitives.ToList();
                primitives.RemoveAt(index);

                FilterSpec edited = filter with { Primitives = primitives };
                ValidateGraph(edited);
                ReplaceFilter(ctx, edited, "Remove filter primitive");
                return new { name = edited.Name, removed = removed.Kind.ToString(), remaining = primitives.Count };
            });

        Add("filter.connectPrimitive",
            "Name the buffers one step reads and the buffer it produces - the wiring, which is what makes a filter a " +
            "graph rather than a list. An input may be SourceGraphic, SourceAlpha, BackgroundImage, FillPaint or " +
            "StrokePaint, or the result of any step in this filter; only the members given change. A name nothing " +
            "produces, a result another step already claims, a connection that would make the graph read its own " +
            "output, and an input the step's kind does not take (feFlood reads nothing) are each refused, because " +
            "each of them changes what every later step receives without saying so.",
            "name:string, index:number, in?:string, in2?:string, result?:string",
            (ctx, p) =>
            {
                FilterSpec filter = RequireFilter(ctx, p.GetString("name"));
                int index = PrimitiveIndex(p, filter);
                FilterPrimitive primitive = filter.Primitives[index];
                FilterPrimitiveDefinition definition = FilterPrimitiveRegistry.Find(primitive.Kind.ToString())
                    ?? throw new EditorOperationException(
                        $"{primitive.Kind} is not a primitive filter.kinds declares");

                bool given = p.ValueKind == JsonValueKind.Object;
                bool has(string member) => given && p.TryGetProperty(member, out _);

                foreach (string member in new[] { "in", "in2", "result" })
                {
                    if (has(member) && definition.Parameter(member) is null)
                    {
                        throw new EditorOperationException(
                            $"{definition.Element} has no '{member}'; it takes " +
                            $"{string.Join(", ", definition.Parameters.Select(parameter => parameter.Name))}");
                    }
                }

                FilterPrimitive wired = primitive with
                {
                    Input = has("in") ? NullIfEmpty(p.GetString("in")) : primitive.Input,
                    Input2 = has("in2") ? NullIfEmpty(p.GetString("in2")) : primitive.Input2,
                    Result = has("result") ? p.GetString("result") ?? string.Empty : primitive.Result,
                };

                List<FilterPrimitive> primitives = filter.Primitives.ToList();
                primitives[index] = wired;

                FilterSpec edited = filter with { Primitives = primitives };
                ValidateGraph(edited);
                ReplaceFilter(ctx, edited, "Connect filter primitive");
                return new
                {
                    name = edited.Name,
                    index,
                    input = wired.Input,
                    input2 = wired.Input2,
                    result = wired.Result.Length == 0 ? null : wired.Result,
                };
            });

        Add("filter.setPrimitiveParameter",
            "Change one value of one step. Every parameter the kind declares in filter.kinds can be set by the " +
            "name it is declared with - a blur's radius, a colour matrix's type and values, a turbulence's seed, a " +
            "light's azimuth - and a name that kind does not take is refused rather than ignored. Colours are " +
            "[r,g,b] with components 0-255; `values` is the twenty numbers of the colour matrix, or the single " +
            "amount a shorthand takes. Inputs and results are wiring rather than values, so they belong to " +
            "filter.connectPrimitive.",
            "name:string, index:number, parameter:string, value:number|string|array",
            (ctx, p) =>
            {
                FilterSpec filter = RequireFilter(ctx, p.GetString("name"));
                int index = PrimitiveIndex(p, filter);
                FilterPrimitive primitive = filter.Primitives[index];
                FilterPrimitiveDefinition definition = FilterPrimitiveRegistry.Find(primitive.Kind.ToString())
                    ?? throw new EditorOperationException(
                        $"{primitive.Kind} is not a primitive filter.kinds declares");

                string parameterName = p.GetString("parameter")
                    ?? throw new EditorOperationException("filter.setPrimitiveParameter needs 'parameter'");
                FilterParameter parameter = definition.Parameter(parameterName)
                    ?? throw new EditorOperationException(
                        $"{definition.Element} has no parameter '{parameterName}'; it takes " +
                        $"{string.Join(", ", definition.Parameters.Select(entry => entry.Name))}");

                if (parameter.Kind == FilterParameterKind.Buffer)
                {
                    throw new EditorOperationException(
                        $"'{parameter.Name}' is wiring rather than a value - use filter.connectPrimitive");
                }

                if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty("value", out JsonElement value))
                {
                    throw new EditorOperationException("filter.setPrimitiveParameter needs 'value'");
                }

                ValidateValue(definition, parameter, value);
                FilterPrimitive changed = WithParameter(primitive, parameter.Name, value);

                List<FilterPrimitive> primitives = filter.Primitives.ToList();
                primitives[index] = changed;

                FilterSpec edited = filter with { Primitives = primitives };
                ValidateGraph(edited);
                ReplaceFilter(ctx, edited, "Set filter primitive parameter");
                return new
                {
                    name = edited.Name,
                    index,
                    parameter = parameter.Name,
                    primitive = DescribePrimitive(changed),
                };
            });

        Add("filter.apply",
            "Draw the selected items through a filter. An empty name removes the filter. This is an appearance edit " +
            "like any other, so it is one undo step, and the step remembers what each item was drawing through " +
            "before - which is what makes Undo restore the previous reference rather than merely clearing it.",
            "name:string",
            (ctx, p) =>
            {
                string name = p.GetString("name") ?? string.Empty;
                if (name.Length > 0 && ctx.Document.FindFilter(name) is null)
                {
                    throw new EditorOperationException($"there is no filter called '{name}'");
                }

                string? applied = name.Length == 0 ? null : name;
                var items = ctx.Session.SelectedObjects
                    .Where(item => item.FilterId != applied)
                    .Select(item => new FilterEditCommand.ItemEdit(item, item.FilterId, applied))
                    .ToList();

                EditFilters(ctx, ctx.Document.Filters, items, "Apply filter");
                return new { applied = name, items = items.Count };
            });

        Add("filter.read",
            "The filters on the selection, and the items that refer to a filter the document does not have. The " +
            "second list is why this is worth asking: an item asking for a filter that is not there is drawn " +
            "unfiltered, which looks like a design decision rather than a lost asset.",
            "",
            (ctx, _) => new
            {
                items = ctx.Session.SelectedObjects
                    .Select(item => new { itemId = item.Id, name = item.Name, filter = item.FilterId })
                    .ToArray(),
                missing = ctx.Document.MissingFilters()
                    .Select(missing => new { itemId = missing.Item.Id, filter = missing.Name })
                    .ToArray(),
            });

        Add("profile.create",
            "Create a reusable width profile in the document. points is [{position, left, right, interpolation?}], " +
            "the same shape style.setWidthProfile takes. The name has to be free: two profiles with one name would " +
            "make 'the profile called X' ambiguous, and it is the name that strokes refer to. One undo step.",
            "name:string, points:[{position,left,right,interpolation?}]",
            (ctx, p) =>
            {
                string name = p.GetString("name") ?? string.Empty;
                if (name.Length == 0)
                {
                    throw new EditorOperationException("profile.create needs a name");
                }

                CadDocument document = ctx.Document;
                if (document.FindProfile(name) is not null)
                {
                    throw new EditorOperationException($"there is already a profile called '{name}'");
                }

                List<WidthPoint> points = ReadWidthPoints(p);
                if (points.Count == 0)
                {
                    throw new EditorOperationException("a profile needs at least one width point");
                }

                var library = document.WidthProfiles.ToList();
                library.Add(new WidthProfileSpec(name, points));
                ctx.Session.Execute(new EditWidthProfilesCommand(
                    document, library, Array.Empty<EditWidthProfilesCommand.StrokeEdit>(), "Create width profile"));

                return new { created = name, points = points.Count };
            });

        Add("profile.list", "Every reusable width profile in the document, with its width points.", "", (ctx, _) =>
            ctx.Document.WidthProfiles.Select(profile => new
            {
                name = profile.Name,
                points = profile.Points.Select(point => new
                {
                    position = Math.Round(point.Position, 6),
                    left = Math.Round(point.LeftWidth, 4),
                    right = Math.Round(point.RightWidth, 4),
                    interpolation = point.Interpolation.ToString().ToLowerInvariant(),
                }).ToArray(),
            }).ToArray());

        Add("profile.rename",
            "Rename a profile. Strokes refer to a profile by name, so this renames them with it - a rename that " +
            "left the strokes naming the old name would leave them pointing at nothing. One undo step.",
            "from:string, to:string",
            (ctx, p) =>
            {
                string from = p.GetString("from") ?? string.Empty;
                string to = p.GetString("to") ?? string.Empty;
                CadDocument document = ctx.Document;

                WidthProfileSpec existing = document.FindProfile(from)
                    ?? throw new EditorOperationException($"there is no profile called '{from}'");
                if (to.Length == 0)
                {
                    throw new EditorOperationException("profile.rename needs a new name");
                }

                if (document.FindProfile(to) is not null)
                {
                    throw new EditorOperationException($"there is already a profile called '{to}'");
                }

                var library = document.WidthProfiles
                    .Select(profile => profile.Name == from ? profile with { Name = to } : profile)
                    .ToList();
                List<EditWidthProfilesCommand.StrokeEdit> edits = StrokesNaming(
                    document, from, stroke => stroke with { WidthProfile = stroke.WidthProfile! with { Name = to } });

                ctx.Session.Execute(new EditWidthProfilesCommand(document, library, edits, "Rename width profile"));

                // The points are carried across untouched, which is the whole promise of a rename: the strokes
                // that used it look the same afterwards.
                return new { renamed = from, to, strokes = edits.Count, points = existing.Points.Count };
            });

        Add("profile.delete",
            "Delete a profile from the document and clear it from every stroke that used it. The strokes keep " +
            "their own widths - only the profile goes - which is what makes deleting an asset a safe thing to do. " +
            "One undo step.",
            "name:string",
            (ctx, p) =>
            {
                string name = p.GetString("name") ?? string.Empty;
                CadDocument document = ctx.Document;
                if (document.FindProfile(name) is null)
                {
                    throw new EditorOperationException($"there is no profile called '{name}'");
                }

                var library = document.WidthProfiles.Where(profile => profile.Name != name).ToList();
                List<EditWidthProfilesCommand.StrokeEdit> edits = StrokesNaming(
                    document, name, stroke => stroke with { WidthProfile = null });

                ctx.Session.Execute(new EditWidthProfilesCommand(document, library, edits, "Delete width profile"));
                return new { deleted = name, cleared = edits.Count };
            });

        Add("profile.apply",
            "Apply a stored profile to the selected paths' strokes. strokeIndex picks one stroke of the stack, " +
            "counted from the bottom, and defaults to every stroke; a path whose stack is shorter is skipped. One " +
            "undo step per path.",
            "name:string, strokeIndex?:number",
            (ctx, p) =>
            {
                string name = p.GetString("name") ?? string.Empty;
                WidthProfileSpec profile = ctx.Document.FindProfile(name)
                    ?? throw new EditorOperationException($"there is no profile called '{name}'");

                // Through the session, so a panel applying a library profile and this run one implementation.
                if (OptionalStrokeIndex(p) is { } at)
                {
                    return new
                    {
                        applied = name,
                        paths = ctx.Session.SetWidthProfileAt(at, profile),
                        strokeIndex = at,
                    };
                }

                int changed = 0;
                foreach (PathItem path in ctx.Session.SelectedPaths().ToList())
                {
                    var stack = path.Strokes.ToList();
                    for (int i = 0; i < stack.Count; i++)
                    {
                        stack[i] = stack[i] with { WidthProfile = profile };
                    }

                    ctx.Session.Execute(new SetStrokesCommand(path, stack, "Apply width profile"));
                    changed++;
                }

                return new { applied = name, paths = changed };
            });

        Add("profile.setPoint",
            "Change one width point of a stored profile - its position, either width, or its interpolation. " +
            "Only the members given change, and a negative width is clamped at zero the way a dragged grip is. " +
            "The strokes that use the profile are edited with it, because that is " +
            "what makes it an asset rather than a copy. One undo step.",
            "name:string, index:number, position?:number, left?:number, right?:number, interpolation?:linear|cubic",
            (ctx, p) =>
            {
                string name = p.GetString("name") ?? string.Empty;
                CadDocument document = ctx.Document;
                WidthProfileSpec profile = document.FindProfile(name)
                    ?? throw new EditorOperationException($"there is no profile called '{name}'");

                int index = (int)p.GetLong("index", -1);
                if (index < 0 || index >= profile.Points.Count)
                {
                    throw new EditorOperationException(
                        $"'{name}' has {profile.Points.Count} width points, so there is no point {index}");
                }

                bool given = p.ValueKind == JsonValueKind.Object;
                WidthPoint point = profile.Points[index];
                WidthPoint changed = point with
                {
                    Position = given && p.TryGetProperty("position", out _) ? p.GetDouble("position", point.Position) : point.Position,
                    LeftWidth = given && p.TryGetProperty("left", out _)
                        ? ClampedWidth(p.GetDouble("left", point.LeftWidth))
                        : point.LeftWidth,
                    RightWidth = given && p.TryGetProperty("right", out _)
                        ? ClampedWidth(p.GetDouble("right", point.RightWidth))
                        : point.RightWidth,
                    Interpolation = given && p.TryGetProperty("interpolation", out _)
                        ? ParseEnum(p.GetString("interpolation"), point.Interpolation)
                        : point.Interpolation,
                };

                return ApplyProfileEdit(ctx, document, profile, name, points =>
                {
                    List<WidthPoint> edited = points.ToList();
                    edited[index] = changed;
                    return edited;
                }, "Edit width point");
            });

        Add("profile.removePoint",
            "Remove one width point from a stored profile, and from the strokes that use it. A profile cannot lose " +
            "its last point - one that says nothing about width is not a profile - so removing the last one is " +
            "refused rather than turning the profile into an empty one. One undo step.",
            "name:string, index:number",
            (ctx, p) =>
            {
                string name = p.GetString("name") ?? string.Empty;
                CadDocument document = ctx.Document;
                WidthProfileSpec profile = document.FindProfile(name)
                    ?? throw new EditorOperationException($"there is no profile called '{name}'");

                int index = (int)p.GetLong("index", -1);
                if (index < 0 || index >= profile.Points.Count)
                {
                    throw new EditorOperationException(
                        $"'{name}' has {profile.Points.Count} width points, so there is no point {index}");
                }

                if (profile.Points.Count <= 1)
                {
                    throw new EditorOperationException(
                        $"'{name}' would be left with no width points, which is not a profile - delete it instead");
                }

                return ApplyProfileEdit(ctx, document, profile, name, points =>
                {
                    List<WidthPoint> edited = points.ToList();
                    edited.RemoveAt(index);
                    return edited;
                }, "Remove width point");
            });

        Add("profile.missing",
            "Every stroke in the document that names a width profile the document does not have, with the path it " +
            "is on and the name it asked for. A stroke holds its profile as a value, so it still draws - which is " +
            "exactly why this is worth asking: a name that resolves to nothing means an asset was lost in an edit " +
            "or a merge, and reporting it is the difference between a known gap and a drawing that quietly looks " +
            "like someone meant it.",
            "",
            (ctx, _) => ctx.Document.MissingWidthProfiles()
                .Select(missing => new
                {
                    itemId = missing.Path.Id,
                    name = missing.Path.Name,
                    profile = missing.Name,
                })
                .ToArray());

        Add("brush.create",
            "Create a reusable brush in the document. Two kinds: 'calligraphic', an elliptical nib with an angle, " +
            "a roundness and a diameter; and 'art', which maps a piece of the document's own artwork along the " +
            "stroke instead of stroking a line. For a nib, angle is the direction the nib's long axis points, in " +
            "degrees from the +X axis towards +Y, the same sense a path direction is measured in; roundness is " +
            "the nib's short axis as a fraction of its long one, so 1 is a circular pen and a small number is a " +
            "flat nib. For an art brush, 'asset' is the id of the document item whose artwork is mapped - a group, " +
            "a path or an embedded image - and 'size' (or 'diameter') is how wide that artwork is drawn across " +
            "the stroke; 'stretch' says whether it spans the path once (stretchToFit), keeps its proportions and " +
            "is drawn once (scaleProportionally) or repeats along the path (repeat). The name has to be free: two " +
            "brushes with one name would make 'the brush called X' ambiguous, and it is the name that strokes " +
            "refer to. Creating a brush does not apply it - an asset sits in the document until something uses " +
            "it. One undo step.",
            "name:string, kind?:calligraphic|art (default calligraphic), angle?:number, roundness?:number, " +
            "diameter?:number, asset?:guid, size?:number (an art brush's diameter), " +
            "stretch?:stretchToFit|scaleProportionally|repeat, flipAcross?:bool, flipAlong?:bool, " +
            "colourisation?:none|tint|tintAndShade, shadeColour?:[r,g,b]",
            (ctx, p) =>
            {
                string name = p.GetString("name") ?? string.Empty;
                if (name.Length == 0)
                {
                    throw new EditorOperationException("brush.create needs a name");
                }

                CadDocument document = ctx.Document;
                if (document.FindBrush(name) is not null)
                {
                    throw new EditorOperationException($"there is already a brush called '{name}'");
                }

                BrushSpec brush = ReadBrush(p, name, document);
                var library = document.Brushes.ToList();
                library.Add(brush);
                ctx.Session.Execute(new EditBrushesCommand(
                    document, library, Array.Empty<EditBrushesCommand.StrokeEdit>(), "Create brush"));

                return DescribeBrush(brush);
            });

        Add("brush.list",
            "Every reusable brush in the document, with the nib parameters that decide what it draws. What a " +
            "person reads in the brush picker and a driver reads to name one for brush.apply.",
            "",
            (ctx, _) => ctx.Document.Brushes.Select(DescribeBrush).ToArray());

        Add("brush.apply",
            "Apply a stored brush to the selected paths' strokes, so they are swept with its nib. strokeIndex " +
            "picks one stroke of the stack, counted from the bottom, and defaults to every stroke; a path whose " +
            "stack is shorter is skipped. Applying a brush replaces the width the stroke would otherwise draw " +
            "with the nib's, because the width a nib lays down depends on the direction of travel - a width " +
            "profile alongside it is not consulted. One undo step for the whole gesture, however many paths it " +
            "touches: choosing a brush over a two-path selection is one click, so one Undo has to take it back " +
            "off both - the same composition `style.setStroke` and `style.setWidthProfile` already do.",
            "name:string, strokeIndex?:number",
            (ctx, p) =>
            {
                string name = p.GetString("name") ?? string.Empty;
                BrushSpec brush = ctx.Document.FindBrush(name)
                    ?? throw new EditorOperationException($"there is no brush called '{name}'");

                int? at = OptionalStrokeIndex(p);
                var edits = new List<IUndoableCommand>();
                int strokes = 0;

                foreach (PathItem path in ctx.Session.SelectedPaths().ToList())
                {
                    var stack = path.Strokes.ToList();
                    bool changed = false;
                    for (int i = 0; i < stack.Count; i++)
                    {
                        if (at is { } index && i != index)
                        {
                            continue;
                        }

                        stack[i] = stack[i] with { Brush = brush };
                        changed = true;
                        strokes++;
                    }

                    if (changed)
                    {
                        edits.Add(new SetStrokesCommand(path, stack, "Apply brush"));
                    }
                }

                // Composite rather than one Execute per path, which is what made a two-path selection cost two
                // undo steps. A gesture is one entry on the stack or the undo button takes half of it back.
                ExecuteAll(ctx, edits, "Apply brush");
                return new { applied = name, paths = edits.Count, strokes };
            });

        Add("brush.clear",
            "Take the brush off the selected paths' strokes, which leaves the stroke's own width and profile - " +
            "what the stroke drew before a brush was applied. strokeIndex picks one stroke of the stack, counted " +
            "from the bottom, and defaults to every stroke. One undo step for the whole gesture, for the reason " +
            "brush.apply is: clearing a brush over a selection is one act, not one per path it lands on.",
            "strokeIndex?:number",
            (ctx, p) =>
            {
                int? at = OptionalStrokeIndex(p);
                var edits = new List<IUndoableCommand>();

                foreach (PathItem path in ctx.Session.SelectedPaths().ToList())
                {
                    var stack = path.Strokes.ToList();
                    bool changed = false;
                    for (int i = 0; i < stack.Count; i++)
                    {
                        if ((at is { } index && i != index) || stack[i].Brush is null)
                        {
                            continue;
                        }

                        stack[i] = stack[i] with { Brush = null };
                        changed = true;
                    }

                    if (changed)
                    {
                        edits.Add(new SetStrokesCommand(path, stack, "Clear brush"));
                    }
                }

                ExecuteAll(ctx, edits, "Clear brush");
                return new { cleared = edits.Count, strokeIndex = at };
            });

        Add("brush.rename",
            "Rename a brush. Strokes refer to a brush by name, so this renames them with it - a rename that " +
            "left the strokes naming the old name would leave them pointing at nothing. One undo step.",
            "from:string, to:string",
            (ctx, p) =>
            {
                string from = p.GetString("from") ?? string.Empty;
                string to = p.GetString("to") ?? string.Empty;
                CadDocument document = ctx.Document;

                BrushSpec existing = document.FindBrush(from)
                    ?? throw new EditorOperationException($"there is no brush called '{from}'");
                if (to.Length == 0)
                {
                    throw new EditorOperationException("brush.rename needs a new name");
                }

                if (document.FindBrush(to) is not null)
                {
                    throw new EditorOperationException($"there is already a brush called '{to}'");
                }

                var library = document.Brushes
                    .Select(brush => brush.Name == from ? brush with { Name = to } : brush)
                    .ToList();
                List<EditBrushesCommand.StrokeEdit> edits = StrokesNamingBrush(
                    document, from, stroke => stroke with { Brush = stroke.Brush! with { Name = to } });

                ctx.Session.Execute(new EditBrushesCommand(document, library, edits, "Rename brush"));

                // The nib itself is carried across untouched, which is the promise of a rename: the strokes
                // that used it look the same afterwards.
                return new { renamed = from, to, strokes = edits.Count, diameter = existing.Diameter };
            });

        Add("brush.delete",
            "Delete a brush from the document and clear it from every stroke that used it. The strokes keep " +
            "their own widths and profiles - only the brush goes - which is what makes deleting an asset a safe " +
            "thing to do. One undo step.",
            "name:string",
            (ctx, p) =>
            {
                string name = p.GetString("name") ?? string.Empty;
                CadDocument document = ctx.Document;
                if (document.FindBrush(name) is null)
                {
                    throw new EditorOperationException($"there is no brush called '{name}'");
                }

                var library = document.Brushes.Where(brush => brush.Name != name).ToList();
                List<EditBrushesCommand.StrokeEdit> edits = StrokesNamingBrush(
                    document, name, stroke => stroke with { Brush = null });

                ctx.Session.Execute(new EditBrushesCommand(document, library, edits, "Delete brush"));
                return new { deleted = name, cleared = edits.Count };
            });

        Add("brush.set",
            "Change a stored brush. A nib's angle, roundness or diameter; an art brush's asset, size, stretch, " +
            "flips and colourisation. Only the members given change. Setting 'asset' on an art brush re-points it " +
            "at another item of the document rather than copying its artwork, which is what keeps the brush a " +
            "reference. The strokes that use the brush are re-pointed with it, because that is what makes it an " +
            "asset rather than a copy. One undo step.",
            "name:string, angle?:number, roundness?:number, diameter?:number, asset?:guid, size?:number, " +
            "stretch?:stretchToFit|scaleProportionally|repeat, flipAcross?:bool, flipAlong?:bool, " +
            "colourisation?:none|tint|tintAndShade, shadeColour?:[r,g,b]",
            (ctx, p) =>
            {
                string name = p.GetString("name") ?? string.Empty;
                CadDocument document = ctx.Document;
                BrushSpec brush = document.FindBrush(name)
                    ?? throw new EditorOperationException($"there is no brush called '{name}'");

                var updated = brush with
                {
                    AngleDegrees = Given(p, "angle") ? p.GetDouble("angle", brush.AngleDegrees) : brush.AngleDegrees,
                    Roundness = Given(p, "roundness") ? ClampedRoundness(p.GetDouble("roundness", brush.Roundness)) : brush.Roundness,
                    Diameter = Given(p, "diameter")
                        ? Math.Max(0.0, p.GetDouble("diameter", brush.Diameter))
                        : Given(p, "size")
                            ? Math.Max(0.0, p.GetDouble("size", brush.Diameter))
                            : brush.Diameter,
                    ArtAsset = Given(p, "asset") ? ReadAsset(p, document) : brush.ArtAsset,
                    Stretch = Given(p, "stretch") ? ReadStretch(p) : brush.Stretch,
                    FlipAcross = Given(p, "flipAcross") ? p.GetBool("flipAcross", brush.FlipAcross) : brush.FlipAcross,
                    FlipAlong = Given(p, "flipAlong") ? p.GetBool("flipAlong", brush.FlipAlong) : brush.FlipAlong,
                    Colourisation = Given(p, "colourisation") ? ReadColourisation(p) : brush.Colourisation,
                    ShadeColour = Given(p, "shadeColour") ? ReadShadeColour(p) : brush.ShadeColour,
                };

                return ApplyBrushEdit(ctx, document, brush, name, updated, "Edit brush");
            });

        Add("brush.missing",
            "Every stroke in the document that names a brush the document does not have, with the path it is on " +
            "and the name it asked for. A stroke holds its brush as a value, so it still draws - which is exactly " +
            "why this is worth asking: a name that resolves to nothing means an asset was lost in an edit or a " +
            "merge, and reporting it is the difference between a known gap and a drawing that quietly looks like " +
            "someone meant it.",
            "",
            (ctx, _) => ctx.Document.MissingBrushes()
                .Select(missing => new
                {
                    itemId = missing.Path.Id,
                    name = missing.Path.Name,
                    brush = missing.Name,
                })
                .ToArray());

        Add("brush.placements",
            "Where an art brush's artwork is placed along a path - the read half of an art brush, and the only way " +
            "to learn what the art is drawn as without seeing it. Each placement names the arc length it starts " +
            "at, the path point there, the direction of travel in degrees, how much of the path it covers and the " +
            "six numbers of the affine transform carrying the asset's own coordinates onto the path. " +
            "**Reported rather than held**: the model has no member that says art is drawn at a place - a stroke's " +
            "render plan is widths and outlines - so the placements are computed from the path every time they are " +
            "asked for, which is why editing the path moves the art with no brush re-applied. A nib places no art, " +
            "and a brush whose asset the document does not have is refused by name rather than answered with an " +
            "empty list.",
            "name:string, itemId?:guid (default: the selected paths)",
            (ctx, p) =>
            {
                string name = p.GetString("name") ?? string.Empty;
                CadDocument document = ctx.Document;
                BrushSpec brush = document.FindBrush(name)
                    ?? throw new EditorOperationException($"there is no brush called '{name}'");

                if (!brush.IsArt)
                {
                    throw new EditorOperationException(
                        $"'{name}' is a {brush.Kind.ToString().ToLowerInvariant()} brush, which sweeps a nib along " +
                        "the path; it places no artwork");
                }

                if (brush.ArtAsset is not { } assetId)
                {
                    throw new EditorOperationException(
                        $"the art brush '{name}' names no asset, so there is no artwork to place");
                }

                LayerItem asset = document.FindItem(assetId)
                    ?? throw new EditorOperationException(
                        $"the art brush '{name}' maps the item {assetId}, and this document has no such item");

                Rect2D bounds = ItemBounds.Of(asset);
                IEnumerable<PathItem> paths = p.TryGetGuid("itemId", out Guid id)
                    ? new[] { RequirePath(document, id) }
                    : ctx.Session.SelectedPaths();

                return paths.Select(path => new
                {
                    itemId = path.Id,
                    name = path.Name,
                    brush = name,
                    assetId,
                    assetName = asset.Name,
                    assetWidth = Math.Round(bounds.Width, 4),
                    assetHeight = Math.Round(bounds.Height, 4),
                    placements = ArtBrushPath.Placements(path, brush, bounds).Select(placement => new
                    {
                        position = Math.Round(placement.Position, 4),
                        x = Math.Round(placement.Point.X, 4),
                        y = Math.Round(placement.Point.Y, 4),
                        tangentDegrees = Math.Round(placement.TangentRadians * 180.0 / Math.PI, 4),
                        length = Math.Round(placement.Length, 4),
                        transform = new[]
                        {
                            placement.Transform.A, placement.Transform.B, placement.Transform.C,
                            placement.Transform.D, placement.Transform.E, placement.Transform.F,
                        },
                    }).ToArray(),
                }).ToArray();
            });

        Add("brush.missingAssets",
            "Every art brush in the document whose asset is not there - the item its artwork lives on was deleted, " +
            "or a file arrived without it. An art brush names its artwork rather than copying it, so the reference " +
            "can come apart, and a brush that quietly maps nothing is exactly the sort of gap worth naming. " +
            "Reported with the brush's name and the id it asked for; nothing is substituted for the missing art.",
            "",
            (ctx, _) => ctx.Document.MissingBrushAssets()
                .Select(missing => new
                {
                    brush = missing.Brush.Name,
                    assetId = missing.Asset,
                })
                .ToArray());

        Add("profile.editMode",
            "Open or close the on-canvas width-profile editor for a path: the mode in which a handle is " +
            "shown at each of the profile's width points, at the stroke's own width, and a grip dragged " +
            "across the stroke sets that point's width. The handles come back with their world positions, " +
            "so a driver that cannot see can aim its pointer at them, and dragging a grip is the same " +
            "edit as profile.setPoint - the canvas invokes that operation, so a person and a driver do " +
            "one thing rather than two. Nothing in the document changes by opening or closing the mode; " +
            "a path with no profile opens a mode with no handles to drag. With no 'on', the mode toggles.",
            "on?:bool, itemId?:guid (default: the selected path)",
            (ctx, p) =>
            {
                VCCad.App.Controls.CanvasWorkspace canvas = Workspace(ctx)
                    ?? throw new EditorOperationException(
                        "There is no canvas to edit a width profile on; profile.setPoint edits one without a window.");

                bool on = p.ValueKind == JsonValueKind.Object &&
                          p.TryGetProperty("on", out JsonElement wanted) &&
                          wanted.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? wanted.GetBoolean()
                    : canvas.WidthProfileTarget is null;

                if (!on)
                {
                    canvas.EditWidthProfile(null);

                    // An empty list rather than a count, because the member has to be the same shape
                    // whichever way the mode went - a caller that gets a number here and an array there
                    // has to guess which one it was handed.
                    return new
                    {
                        editing = false,
                        itemId = (Guid?)null,
                        profile = (string?)null,
                        handles = Array.Empty<object>(),
                    };
                }

                PathItem path = p.TryGetGuid("itemId", out Guid id)
                    ? RequirePath(ctx.Document, id)
                    : ctx.Session.SelectedPaths().FirstOrDefault()
                        ?? throw new EditorOperationException(
                            "No path is selected. Select the stroke whose profile to edit, or pass itemId.");

                canvas.EditWidthProfile(path);

                // Reported, not implied: the mode is the canvas's, and a caller that has to guess
                // whether it took has no way to tell "no handles" from "no mode".
                return new
                {
                    editing = canvas.IsEditingWidthProfile,
                    itemId = path.Id,
                    profile = path.Strokes.LastOrDefault(stroke => stroke.HasWidthProfile)?.WidthProfile?.Name,
                    handles = canvas.WidthProfileHandles()
                        .Select(handle => new
                        {
                            index = handle.Index,
                            position = Math.Round(handle.Position, 6),
                            left = new { x = Math.Round(handle.Left.Point.X, 4), y = Math.Round(handle.Left.Point.Y, 4), width = Math.Round(handle.Left.Width, 4) },
                            right = new { x = Math.Round(handle.Right.Point.X, 4), y = Math.Round(handle.Right.Point.Y, 4), width = Math.Round(handle.Right.Width, 4) },
                        })
                        .ToArray(),
                };
            });

        Add("style.strokes",
            "The stroke stack on each selected path, bottom to top: every stroke's colour, width, cap, join, " +
            "miter limit, alignment and dash. What a driver reads to check a path that has more than one " +
            "stroke, and what the appearance panel shows.",
            "",
            (ctx, _) => ctx.Session.SelectedPaths().Select(path => new
            {
                itemId = path.Id,
                name = path.Name,
                count = path.Strokes.Count,
                strokes = path.Strokes.Select(DescribeStroke).ToArray(),
            }).ToArray());

        Add("style.addStroke",
            "Add a stroke to the selected paths, on top of the ones they have. With no parameters it copies the " +
            "current top stroke - what pressing add gives a person, a copy they then edit - and any parameter " +
            "given overrides the copy. One undo step.",
            "color?:[r,g,b], width?:number, cap?:butt|round|square, join?:miter|round|bevel, miterLimit?, " +
            "alignment?:center|inside|outside, dash?:number[]",
            (ctx, p) =>
            {
                // Through the session, so the appearance panel's add button and this run one implementation
                // rather than two that have to be kept in step.
                StrokeSpec? requested = p.ValueKind == JsonValueKind.Object
                    ? ReadStroke(p, ctx.Session.SelectedPaths().FirstOrDefault()?.Strokes.LastOrDefault()
                        ?? StrokeSpec.Hairline(ColorRgb.Black))
                    : null;

                return new { changed = ctx.Session.AddStroke(requested) };
            });

        Add("style.removeStroke",
            "Remove a stroke from the selected paths. index counts from the bottom and defaults to the top " +
            "one. Removing the last stroke leaves the path with a single invisible stroke rather than none, so " +
            "the stack a caller reads is always there.",
            "index?:number",
            (ctx, p) =>
            {
                // The index counts from the bottom and defaults to the top one: "remove the stroke" on a path with
                // a stack means the last one added. Presence-checked, because an undefined element throws on a
                // property read rather than answering "not given".
                int? index = p.ValueKind == JsonValueKind.Object && p.TryGetProperty("index", out _)
                    ? (int)p.GetLong("index", 0)
                    : null;
                return new { changed = ctx.Session.RemoveStroke(index) };
            });

        Add("style.commonStroke",
            "What the selection's strokes agree on, and what they do not: each member is either the common value " +
            "or explicitly mixed. A panel editing a selection has to show one value per member, and showing the " +
            "first path's value as though it were everyone's is how a person types a number and believes it " +
            "describes what they selected. index picks the stroke in the stack, counted from the bottom, and " +
            "defaults to the top. Width, cap, join, miter limit, alignment, dash, width profile, tablet " +
            "dynamics and the per-stroke paint (opacity, blend) are each reported with their own mixed flag, so a " +
            "member one path disagrees about does not " +
            "hide the members the rest agree on; a path with no stroke at that index is a gap, not a disagreement.",
            "index?:number",
            (ctx, p) =>
            {
                int? index = p.ValueKind == JsonValueKind.Object && p.TryGetProperty("index", out _)
                    ? (int)p.GetLong("index", 0)
                    : null;

                var paths = ctx.Session.SelectedPaths().ToList();
                StrokeSummary summary = StrokeSummary.Of(
                    paths, index ?? Math.Max(0, (paths.FirstOrDefault()?.Strokes.Count ?? 1) - 1));

                return new
                {
                    paths = summary.Paths,
                    strokes = summary.Strokes,
                    empty = summary.IsEmpty,
                    mixed = summary.IsMixed,
                    width = summary.Width,
                    widthMixed = summary.WidthMixed,
                    cap = summary.Cap?.ToString(),
                    capMixed = summary.CapMixed,
                    join = summary.Join?.ToString(),
                    joinMixed = summary.JoinMixed,
                    miterLimit = summary.MiterLimit,
                    miterMixed = summary.MiterMixed,
                    alignment = summary.Alignment?.ToString(),
                    alignmentMixed = summary.AlignmentMixed,
                    dash = summary.Dash is { IsEmpty: false } dash ? dash.Segments.ToArray() : null,
                    dashMixed = summary.DashMixed,
                    profile = DescribeWidthProfile(summary.WidthProfile),
                    profileMixed = summary.WidthProfileMixed,
                    dynamics = DescribeDynamics(summary.Dynamics),
                    dynamicsMixed = summary.DynamicsMixed,

                    // Null when the selection agrees that no stroke states one, which a panel shows as a blank
                    // field rather than as "1" - and null with the mixed flag set is a genuine disagreement.
                    opacity = summary.Opacity is { } statedOpacity ? Math.Round(statedOpacity, 6) : (double?)null,
                    opacityMixed = summary.OpacityMixed,
                    blend = summary.Blend?.ToSvgName(),
                    blendMixed = summary.BlendMixed,
                };
            });

        Add("style.setEffectParameter",
            "Set one parameter of one effect, by the name the registry declares it under. raster says which list " +
            "the effect is in; index counts from the start of it; name is one of the parameters " +
            "`style.effectParameters` reports for that kind. strokeIndex picks one stroke of the stack, counted " +
            "from the bottom, and defaults to the first stroke carrying the effect. A name the effect does not " +
            "take is refused, because the alternative is a typo quietly setting something else. One undo step per " +
            "path.",
            "name:string, value:number, index:number, raster?:bool, strokeIndex?:number",
            (ctx, p) =>
            {
                string name = p.GetString("name")
                    ?? throw new EditorOperationException("Parameter 'name' is required.");
                int index = (int)p.GetLong("index", 0);
                double value = p.GetDouble("value", 0.0);
                bool raster = p.GetBool("raster", false);
                int? strokeIndex = OptionalStrokeIndex(p);

                return new
                {
                    name,
                    index,
                    raster,
                    strokeIndex,
                    changed = ctx.Session.SetEffectParameter(raster, index, name, value, strokeIndex),
                };
            });

        Add("style.removeStrokeEffect",
            "Remove one outline effect from the selected paths' strokes. index counts from the start of the effect " +
            "list, which is the order they apply in. strokeIndex picks one stroke of the stack, counted from the " +
            "bottom, and defaults to the first stroke carrying that index. One undo step per path.",
            "index:number, strokeIndex?:number",
            (ctx, p) =>
            {
                int index = (int)p.GetLong("index", 0);
                int? strokeIndex = OptionalStrokeIndex(p);
                return new { index, strokeIndex, changed = ctx.Session.RemoveStrokeEffect(index, strokeIndex) };
            });

        Add("style.removeRasterEffect",
            "Remove one raster effect from the selected paths' strokes. index counts from the start of that list, " +
            "which is kept separately from the outline effects. strokeIndex picks one stroke of the stack, counted " +
            "from the bottom, and defaults to the first stroke carrying that index. One undo step per path.",
            "index:number, strokeIndex?:number",
            (ctx, p) =>
            {
                int index = (int)p.GetLong("index", 0);
                int? strokeIndex = OptionalStrokeIndex(p);
                return new { index, strokeIndex, changed = ctx.Session.RemoveStrokeRasterEffect(index, strokeIndex) };
            });

        Add("style.reorderStrokeEffect",
            "Move an outline effect within the selected paths' strokes - how a person changes the order they apply " +
            "in. from and to count from the start of the effect list, and to may be one past the last to put it at " +
            "the end. The order is the picture: roughen inside an offset does not look like an offset inside a " +
            "roughen. strokeIndex picks one stroke of the stack, counted from the bottom, and defaults to the first " +
            "stroke carrying that index. One undo step per path.",
            "from:number, to:number, strokeIndex?:number",
            (ctx, p) =>
            {
                int from = (int)p.GetLong("from", 0);
                int to = (int)p.GetLong("to", 0);
                int? strokeIndex = OptionalStrokeIndex(p);
                return new { from, to, strokeIndex, changed = ctx.Session.MoveStrokeEffect(from, to, strokeIndex) };
            });

        Add("style.reorderRasterEffect",
            "Move a raster effect within the selected paths' strokes, the same way and for the same reason: they " +
            "compose in order. strokeIndex picks one stroke of the stack, counted from the bottom, and defaults to " +
            "the first stroke carrying that index. One undo step per path.",
            "from:number, to:number, strokeIndex?:number",
            (ctx, p) =>
            {
                int from = (int)p.GetLong("from", 0);
                int to = (int)p.GetLong("to", 0);
                int? strokeIndex = OptionalStrokeIndex(p);
                return new
                {
                    from,
                    to,
                    strokeIndex,
                    changed = ctx.Session.MoveStrokeRasterEffect(from, to, strokeIndex),
                };
            });

        Add("style.effectParameters",
            "Every effect a stroke can carry, with the parameters each one takes: name, sort, meaning and defaults. " +
            "This is the **declaration** an effects panel builds its editors from and an operation validates " +
            "against, so the two cannot fall out of step about what an effect takes.",
            "",
            (ctx, _) => EffectRegistry.All.Select(effect => new
            {
                kind = effect.Kind,
                raster = effect.Raster,
                meaning = effect.Meaning,
                parameters = effect.Parameters.Select(p => new
                {
                    name = p.Name,
                    type = p.Kind.ToString().ToLowerInvariant(),
                    meaning = p.Meaning,
                    @default = p.Kind == EffectParameterKind.Color ? null : (double?)p.Default,
                }).ToArray(),
            }).ToArray());

        Add("style.inspectStroke",
            "Which stroke of the selection is being inspected, counted from the bottom - the state the stroke " +
            "inspector and the appearance panel share, so both are describing the same one rather than each holding " +
            "its own idea. Without index it reports what is being inspected. Index is clamped to the stack, so a " +
            "selection change cannot leave it pointing at a stroke that does not exist.",
            "index?:number",
            (ctx, p) =>
            {
                if (p.ValueKind == JsonValueKind.Object && p.TryGetProperty("index", out _))
                {
                    ctx.ViewModel.InspectedStroke = (int)p.GetLong("index", -1);
                }

                return new
                {
                    index = ctx.ViewModel.InspectedStroke,
                    label = ctx.ViewModel.InspectedStrokeLabel,
                    strokes = ctx.Session.SelectedPaths().FirstOrDefault()?.Strokes.Count ?? 0,
                };
            });

        Add("style.setStrokeVisible",
            "Show or hide one stroke on the selected paths. index counts from the bottom and defaults to the top " +
            "one. A hidden stroke keeps its width, caps, joins, miter limit and dash: it is a stroke somebody is " +
            "about to switch back on, not one to throw away. One undo step.",
            "visible:bool, index?:number",
            (ctx, p) =>
            {
                bool visible = p.GetBool("visible", true);
                int? index = p.ValueKind == JsonValueKind.Object && p.TryGetProperty("index", out _)
                    ? (int)p.GetLong("index", 0)
                    : null;

                return new { changed = ctx.Session.SetStrokeVisible(index, visible), visible };
            });

        Add("style.reorderStroke",
            "Move a stroke within the stack on the selected paths - how a person changes which one is on top. " +
            "from and to count from the bottom; to may be one past the last to put a stroke on top. One undo " +
            "step.",
            "from:number, to:number",
            (ctx, p) =>
            {
                int from = (int)p.GetLong("from", 0);
                int to = (int)p.GetLong("to", 0);
                return new { from, to, changed = ctx.Session.MoveStroke(from, to) };
            });

        // ---- text --------------------------------------------------------
        Add("text.create",
            "Create a text object at (x, y). With no layerId it goes on the active artboard, as typing does; " +
            "with one it goes on that layer, which is how a scene puts its words on a second page.",
            "x:number, y:number, text:string, family?, fontSize?, color?:[r,g,b], layerId?:guid",
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

                if (p.TryGetGuid("layerId", out Guid targetLayer))
                {
                    // The same thing object.create does with its layerId, so the two create operations place
                    // an object the same way: coordinates are in the document's own space and the move re-bases
                    // them onto the page.
                    Layer layer = RequireLayer(ctx.Document, targetLayer);
                    ctx.ViewModel.MoveItems(new[] { (LayerItem)item }, layer, layer.Children.Count);
                }

                ctx.ViewModel.NotifyDocumentChanged();
                return DescribeOne(item);
            });

        Add("text.update",
            "Update the selected text objects **member by member**. Each member is written only where it is given, " +
            "and an omitted member is left exactly as each block has it - so a mixed selection can have its size " +
            "changed without the block whose words or colour differ having them written over. One content string and " +
            "one colour are block members; family, size, weight and slant are per run, and runIndex names the run " +
            "they land on, with a block that has no run there skipped rather than counted as a disagreement. With no " +
            "runIndex the face members style every run, which is the uniform edit a whole-block request means. " +
            "Reports how many blocks changed and what the selection now reads, so a caller can tell which members " +
            "were altered and which are still mixed. One undo step.",
            "text?:string, family?:string, fontSize?:number, bold?:bool, italic?:bool, color?:[r,g,b], " +
            "runIndex?:number",
            (ctx, p) =>
            {
                // A member is written only where it is **given**: the presence of the key, not its value, is what
                // makes an edit. Reading a missing bold as `false` would make every request clear the weight of
                // everything it touched, which is the all-or-nothing defect this replaced.
                string? content = p.ValueKind == JsonValueKind.Object && p.TryGetProperty("text", out _)
                    ? p.GetString("text")
                    : null;
                string? family = p.GetString("family");
                double? size = OptionalNumber(p, "fontSize");
                bool? bold = OptionalBool(p, "bold");
                bool? italic = OptionalBool(p, "italic");
                ColorRgb? color = p.TryGetColorArray("color", out ColorRgb wanted) ? wanted : null;

                // Null styles every run, which is what a whole-block face edit means; an index names one.
                int? runIndex = OptionalNumber(p, "runIndex") is { } at ? (int)at : null;

                int changed = ctx.Session.ApplyTextFieldsAt(runIndex, content, family, size, bold, italic, color);

                // Choosing a font is what makes it recent, so the picker's "recent" list is a
                // record of what was actually used rather than of what was scrolled past.
                if (family is { Length: > 0 })
                {
                    FontFavourites.Shared.Used(family);
                }

                return TextCommonReport(ctx, runIndex is { } named ? named : ctx.ViewModel.InspectedRun, changed);
            });

        Add("text.setAlignment", "Set text alignment: left|center|right.", "alignment:string",
            (ctx, p) =>
            {
                ctx.Session.SetTextAlignment(ParseEnum(p.GetString("alignment"), TextAlignment.Left));
                return Summary(ctx);
            });

        Add("text.common",
            "What the selected text blocks agree on, and what they do not: each member is either the common value " +
            "or explicitly mixed. This is the reading the Text panel shows, built from the same summary, so a person " +
            "and a driver cannot be told different things about the same selection. A panel editing a selection has " +
            "to show one value per member, and showing the first block's words, colour or size as though they were " +
            "everyone's is how a person types a number and believes it describes what they selected. runIndex names " +
            "the run the face members (family, size, weight, slant) are read from, and a block with no run there is a " +
            "gap rather than a disagreement - blocks carry different numbers of runs, and counting a shorter one as " +
            "\"different\" would make every selection of unequal blocks report every face member as mixed. Without " +
            "runIndex the shared inspected run is read, which is the run the panel's face fields describe.",
            "runIndex?:number",
            (ctx, p) =>
            {
                int? runIndex = OptionalNumber(p, "runIndex") is { } at ? (int)at : null;
                return TextCommonReport(
                    ctx, runIndex ?? ctx.ViewModel.InspectedRun, ctx.Session.SelectedTextItems().Count());
            });

        Add("text.inspectRun",
            "Which run of the selected text blocks is being inspected - the state the Text panel and a driver share, " +
            "so both describe the same run rather than each holding its own idea. Face is per run in this model, so " +
            "with a multi-run block there is no such thing as \"the font\"; without index it reports which run is " +
            "inspected. Index is clamped to the selection's run list, so a selection change cannot leave it pointing " +
            "at a run that does not exist.",
            "index?:number",
            (ctx, p) =>
            {
                if (p.ValueKind == JsonValueKind.Object && p.TryGetProperty("index", out _))
                {
                    ctx.ViewModel.InspectedRun = (int)p.GetLong("index", 0);
                }

                return new
                {
                    index = ctx.ViewModel.InspectedRun,
                    label = ctx.ViewModel.InspectedRunLabel,
                    runs = ctx.Session.SelectedTextItems().FirstOrDefault()?.Runs.Count ?? 0,
                };
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
                    caret = ctx.ViewModel.TextCaretOffset,
                    caretRun = ctx.ViewModel.TextCaretRunIndex,
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
                    // Window pixels in, canvas-relative point out: WindowToModel is measured from the
                    // canvas, and a caller aims with window coordinates. The translation starts at the
                    // window, not at the canvas - translating a control to itself is the identity, which
                    // is how the first attempt at this kept the bug it was meant to fix.
                    Avalonia.Point local = Avalonia.Controls.TopLevel.GetTopLevel(canvas) is { } fromWindow
                        ? fromWindow.TranslatePoint(new Avalonia.Point(x, y), canvas) ?? new Avalonia.Point(x, y)
                        : new Avalonia.Point(x, y);
                    var model = canvas.WindowToModel(local);
                    return new { x = model.X, y = model.Y };
                }

                // ...and the other way about. ModelToWindow is measured from the canvas too, so a point
                // handed straight to `input.pointer` was off by wherever the canvas sits in the window -
                // which is into a neighbouring panel, where the click lands on something else and quietly
                // selects nothing. The operation promises window pixels, so it has to deliver them.
                Avalonia.Point screen = canvas.ModelToWindow(new VCCad.Geometry.Point2D(x, y));
                if (Avalonia.Controls.TopLevel.GetTopLevel(canvas) is { } top)
                {
                    screen = canvas.TranslatePoint(screen, top) ?? screen;
                }

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

        Add("object.editAt",
            "Do what a double-click at a point does: open the text block there for editing with the " +
            "caret where the click landed, or select the path there and arm the node tool. This is " +
            "the gesture a person uses to get into editing, reachable without a double-click.",
            "x:number, y:number",
            (ctx, p) =>
            {
                VCCad.App.Controls.CanvasWorkspace? canvas = Workspace(ctx);
                if (canvas is null)
                {
                    throw new EditorOperationException("No canvas is attached.");
                }

                var point = new VCCad.Geometry.Point2D(p.GetDouble("x"), p.GetDouble("y"));
                VCCad.App.Controls.EditTarget opened = canvas.EditAt(point);
                ctx.ViewModel.NotifyDocumentChanged();

                return new
                {
                    opened = opened.ToString().ToLowerInvariant(),
                    tool = ctx.ViewModel.Tool.ToString().ToLowerInvariant(),
                    editing = ctx.ViewModel.IsEditingText,
                    selectionStart = ctx.ViewModel.TextSelectionStart,
                    selectionEnd = ctx.ViewModel.TextSelectionEnd,
                    selected = ctx.ViewModel.SelectedObjects.Count,
                };
            });

        Add("text.styleSelection",
            "Style the selected range of the open text block - the face, size, weight and slant - as " +
            "the Text pane and Ctrl+B do. text.update restyles the whole object; this changes part " +
            "of one, which is what rich text is.",
            "family?:string, fontSize?:number, bold?:bool, italic?:bool",
            (ctx, p) =>
            {
                VCCad.App.Controls.CanvasWorkspace? canvas = Workspace(ctx);
                if (canvas is null)
                {
                    throw new EditorOperationException("No canvas is attached.");
                }

                string? family = p.GetString("family");
                double? size = p.TryGetProperty("fontSize", out JsonElement sv) ? sv.GetDouble() : null;
                bool? bold = p.TryGetProperty("bold", out JsonElement bv) ? bv.GetBoolean() : null;
                bool? italic = p.TryGetProperty("italic", out JsonElement iv) ? iv.GetBoolean() : null;

                if (!canvas.StyleSelection(run =>
                    {
                        if (family is not null) run.FontFamily = family;
                        if (size is { } s) run.FontSize = s;
                        if (bold is { } b) run.Bold = b;
                        if (italic is { } i) run.Italic = i;
                    }))
                {
                    throw new EditorOperationException(
                        "No text is selected in an open block; call text.edit and text.select first.");
                }

                ctx.ViewModel.NotifyDocumentChanged();
                return new
                {
                    selectionStart = ctx.ViewModel.TextSelectionStart,
                    selectionEnd = ctx.ViewModel.TextSelectionEnd,
                };
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

        Add("artboard.add",
            "Add an artboard. With no x, the new page is placed clear of the right-hand edge of the pages " +
            "already there, so a second page is beside the first rather than on top of it.",
            "width?, height?, x?, y?, name?",
            (ctx, p) =>
            {
                double width = p.GetDouble("width", PageSizes.A4Landscape.Width);
                double height = p.GetDouble("height", PageSizes.A4Landscape.Height);

                // **Beside the pages already there, not on top of them.** A page added at the origin lands
                // exactly over the last one, and since `view.fit` then fits what it can see, the page that
                // disappears is the one that was there first. The gap is a tenth of a page, which reads as two
                // pages at any zoom rather than as one wide one.
                double suggestedX = ctx.Document.Artboards.Count == 0
                    ? 0
                    : ctx.Document.Artboards.Max(a => a.Bounds.Right) + (width * 0.1);

                var rect = new Rect2D(
                    p.GetDouble("x", suggestedX),
                    p.GetDouble("y", 0),
                    width,
                    height);
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
        Add("path.close",
            "Close the selected paths. Ends within 0.05 mm are snapped together - moving the point that " +
            "is not on a whole millimetre onto the one that is - and ends further apart gain a closing " +
            "segment whose handles continue the neighbouring ones in proportion to their length.",
            "",
            (ctx, _) =>
            {
                IReadOnlyList<PathCloseResult> closes = ctx.Session.CloseSelectedPaths();
                return new
                {
                    closed = closes.Count(r => r.Changed),
                    snapped = closes.Count(r => r.Mode == PathCloseMode.Snapped),
                    segmentsAdded = closes.Count(r => r.Mode == PathCloseMode.SegmentAdded),
                    detail = closes.Select(r => new
                    {
                        mode = r.Mode.ToString(),
                        movedTo = r.MovedTo is { } to ? new { x = to.X, y = to.Y } : null,
                        movedFrom = r.MovedFrom is { } from ? new { x = from.X, y = from.Y } : null,
                        gapMm = Math.Round(r.Gap, 4),
                    }),
                };
            });

        Add("path.union", "Merge the selected paths into one region.", "",
            (ctx, _) => BooleanPaths(ctx, BooleanOp.Union));

        Add("path.subtract",
            "Remove every other selected path from the back-most one, which survives - Illustrator's " +
            "Minus Front. The result is one object, with holes if the removed paths were inside it.",
            "", (ctx, _) => BooleanPaths(ctx, BooleanOp.Subtract));

        Add("path.intersect", "Keep only what every selected path covers.", "",
            (ctx, _) => BooleanPaths(ctx, BooleanOp.Intersect));

        Add("path.exclude", "Keep what an odd number of the selected paths cover.", "",
            (ctx, _) => BooleanPaths(ctx, BooleanOp.Exclude));

        Add("path.divide",
            "Cut the selected paths into their separate regions, one object each rather than one merged " +
            "object.", "", (ctx, _) => DividePaths(ctx));

        Add("path.makeCompound",
            "Fill the selected paths as one object with holes where they overlap, without cutting " +
            "anything. This is how separate outlines become a letter with a counter in it.",
            "", (ctx, _) => MakeCompound(ctx));

        Add("path.releaseCompound",
            "Take the selected compound paths apart into one object per outline - a letter with a " +
            "counter becomes two objects that can be moved independently.",
            "", (ctx, _) => ReleaseCompound(ctx));

        Add("path.reverseSubpath",
            "Turn one outline of a path inside out: a hole becomes an island and an island a hole. " +
            "Under the nonzero rule this is the whole difference between the two.",
            "itemId?:guid, index?:number",
            (ctx, p) => ReverseSubpath(ctx, p));

        Add("path.expandStroke",
            "Turn the selected objects' strokes into filled outlines - Illustrator's Outline Stroke. " +
            "Open paths expand with their caps, closed paths to both sides, and a compound path's holes " +
            "to the inside of each hole. The outline gets its own stroke width by the rule: below 4pt " +
            "the original over four, otherwise 1pt.",
            "",
            (ctx, _) =>
            {
                // **Refused rather than half-done.** Stroke expansion produces one filled path per stroke, and
                // the expander still works a stroke at a time - so on a path with a stack it would expand the
                // bottom stroke and silently drop the rest, which is the failure this whole issue is about.
                // Refusing leaves the path and every stroke on it exactly as it was, and says why.
                if (ctx.Session.SelectedPaths()
                        .Any(p => p.Strokes.Count(s => s.HasVisibleOutline) > 1))
                {
                    throw new EditorOperationException(
                        "path.expandStroke works on one stroke at a time, and something selected has more than " +
                        "one. Expanding it would drop the others, so nothing has been changed.");
                }

                int count = ctx.Session.ExpandSelectedStrokes();
                ctx.ViewModel.NotifyDocumentChanged();
                return new { expanded = count };
            });
        Add("arrange.align",
            "Align the selected objects on one axis: axis is horizontal|vertical and edge is " +
            "start|centre|end (left|middle|right, or top|middle|bottom). The line is the selection's own " +
            "extent. One undo step.",
            "axis:string, edge:string",
            (ctx, p) =>
            {
                (ArrangeAxis axis, ArrangeEdge edge) = ReadAlign(p);
                int moved = ctx.Session.AlignSelection(axis, edge);
                ctx.ViewModel.NotifyDocumentChanged();
                return new { axis = axis.ToString(), edge = edge.ToString(), moved };
            });

        Add("arrange.distribute",
            "Spread the selected objects out evenly along an axis, in stack order. Gaps are equalised " +
            "when there is room; when the objects overlap there is none - their widths exceed the span " +
            "they occupy - so their centres are spread evenly instead, leaving sizes, order and the " +
            "overlap alone. from is start (the first object is placed first) or end (the last is).",
            "axis:string, from?:string",
            (ctx, p) =>
            {
                ArrangeAxis axis = ReadAxis(p.GetString("axis"));
                string? from = p.GetString("from");
                ArrangeAnchor anchor = string.Equals(from, "end", StringComparison.OrdinalIgnoreCase)
                    ? ArrangeAnchor.End
                    : ArrangeAnchor.Start;

                bool overlapped = VCCad.Core.Model.Arrange.OverlapsTooMuchForGaps(
                    ctx.Session.SelectedObjects.ToList(), axis);

                int moved = ctx.Session.DistributeSelection(axis, anchor);
                ctx.ViewModel.NotifyDocumentChanged();

                return new
                {
                    axis = axis.ToString(),
                    from = anchor.ToString(),
                    moved,
                    byCentres = overlapped,
                };
            });
        Add("path.roundCorner",
            "Round one corner of the selected path: the point where two segments meet becomes an arc. " +
            "The radius is the distance from the corner, which is what a drag sets; if it is larger than " +
            "the neighbouring segments allow, the largest arc that fits is used and the result says so.",
            "itemId?:guid, subPath?:number, node:number, radius:number",
            (ctx, p) =>
            {
                PathItem path = FindSelectedPath(ctx, p)
                    ?? throw new EditorOperationException("Select a path, or pass itemId.");

                int subPath = (int)Math.Round(p.GetDouble("subPath", 0));
                int node = (int)Math.Round(p.GetDouble("node", 0));
                if (!p.TryGetProperty("radius", out JsonElement rv) || rv.ValueKind != JsonValueKind.Number)
                {
                    throw new EditorOperationException("Parameter 'radius' is required.");
                }

                CornerRoundResult result = ctx.Session.RoundCorner(path, subPath, node, rv.GetDouble());
                ctx.ViewModel.NotifyDocumentChanged();

                return new
                {
                    rounded = result.Rounded,
                    radius = result.Radius,
                    requested = result.Requested,
                    clamped = result.Clamped,
                    reason = result.Reason,
                    itemId = path.Id,
                };
            });
        Add("path.drawFreehand",
            "Draw a freehand stroke: the points are fitted **once** to cubic Bezier segments and become " +
            "an open path. The tolerance is how far the curve may sit from the points drawn - tight by " +
            "default, because this follows a hand rather than smoothing it.",
            "points:[x,y][], tolerance?:number",
            (ctx, p) =>
            {
                if (!p.TryGetProperty("points", out JsonElement list) || list.ValueKind != JsonValueKind.Array)
                {
                    throw new EditorOperationException("Parameter 'points' is required: [[x,y],...].");
                }

                var points = new List<Point2D>();
                foreach (JsonElement entry in list.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Array || entry.GetArrayLength() < 2)
                    {
                        throw new EditorOperationException("Each point must be [x, y].");
                    }

                    points.Add(new Point2D(entry[0].GetDouble(), entry[1].GetDouble()));
                }

                double? tolerance = p.TryGetProperty("tolerance", out JsonElement tv) &&
                                    tv.ValueKind == JsonValueKind.Number
                    ? tv.GetDouble()
                    : null;

                PathItem? path = ctx.Session.DrawFreehand(points, tolerance);
                ctx.ViewModel.NotifyDocumentChanged();

                return new
                {
                    drawn = path is not null,
                    itemId = path?.Id,
                    points = points.Count,
                    nodes = path?.SubPaths[0].Nodes.Count ?? 0,
                };
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
        Add("units.get",
            "The unit every measurement is displayed in, and the units available. Fields and " +
            "dialogs show this unit, so what is returned here is what a person is reading.",
            "",
            (ctx, _) => new
            {
                unit = UnitSettings.Current.Unit.ToString().ToLowerInvariant(),
                abbreviation = LengthUnits.Abbreviation(UnitSettings.Current.Unit),
                available = LengthUnits.All.Select(u => new
                {
                    unit = u.ToString().ToLowerInvariant(),
                    abbreviation = LengthUnits.Abbreviation(u),
                }).ToArray(),
            });

        Add("units.set",
            "Change the unit measurements are displayed in. Every readout follows immediately.",
            "unit:string (mm|cm|in|pt|pc, or the full name)",
            (ctx, p) =>
            {
                string name = p.GetString("unit")
                    ?? throw new EditorOperationException("Parameter 'unit' is required.");

                if (!LengthUnits.TryParse(name, out LengthUnit unit))
                {
                    throw new EditorOperationException(
                        $"Unknown unit '{name}'. Use one of: " +
                        string.Join(", ", LengthUnits.All.Select(u => LengthUnits.Abbreviation(u))));
                }

                UnitSettings.Current.Unit = unit;
                return new
                {
                    unit = unit.ToString().ToLowerInvariant(),
                    abbreviation = LengthUnits.Abbreviation(unit),
                };
            });

        Add("units.evaluate",
            "Evaluate a measurement typed into a field, exactly as the panel would: an " +
            "expression with units, arithmetic and parentheses. Returns the length in the " +
            "configured unit and in millimetres, or an error naming what was wrong.",
            "expression:string",
            (ctx, p) =>
            {
                string text = p.GetString("expression") ?? string.Empty;

                if (!LengthExpression.TryEvaluate(
                        text, UnitSettings.Current.Unit, out Length length, out string? error))
                {
                    throw new EditorOperationException($"'{text}' is not a measurement: {error}");
                }

                return new
                {
                    millimetres = Math.Round(length.Millimetres, 9),
                    display = UnitSettings.Current.FormatWithUnit(length),
                    unit = UnitSettings.Current.Unit.ToString().ToLowerInvariant(),
                };
            });

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

        // ---- gradients ---------------------------------------------------
        // Everything a person can do to a gradient in the Gradient panel, so the panel and a
        // driver reach the same model through the same registry (AGENTS.md §1.1).

        Add("gradient.get",
            "Read the gradient on a path: kind, spread, every stop (position, colour, opacity, " +
            "midpoint), the linear and radial geometry and the freeform points. The shape is the " +
            "one gradient.setStops and gradient.setGeometry accept, so a driver can read, change " +
            "and write it back.",
            "itemId?:guid (default: the selected path)",
            (ctx, p) =>
            {
                PathItem path = GradientTarget(ctx, p);
                return new
                {
                    itemId = path.Id,
                    hasGradient = path.Fill.HasGradient,
                    fill = new { visible = path.Fill.IsVisible, color = ColourJson(path.Fill.Color) },
                    gradient = GradientJson(GradientOf(path)),
                };
            });

        Add("gradient.setKind",
            "Switch a gradient's type while keeping its stops and geometry - what Illustrator does " +
            "when a person clicks Radial on a gradient they already have.",
            "kind:linear|radial|freeform|conical, itemId?:guid",
            (ctx, p) =>
            {
                PathItem path = GradientTarget(ctx, p);
                GradientSpec gradient = GradientOf(path) with { Kind = ParseKind(p) };
                SetGradient(ctx, path, gradient);
                return GradientJson(gradient);
            });

        Add("gradient.setSpread",
            "Set what happens outside the 0..1 ramp: pad, reflect or repeat.",
            "spread:pad|reflect|repeat, itemId?:guid",
            (ctx, p) =>
            {
                PathItem path = GradientTarget(ctx, p);
                GradientSpec gradient = GradientOf(path) with { Spread = ParseSpread(p) };
                SetGradient(ctx, path, gradient);
                return GradientJson(gradient);
            });

        Add("gradient.setStops",
            "Replace every stop on a gradient in one undo step. Positions are 0..1; stops are " +
            "clamped and ordered, and two stops sharing a position are kept, which is what makes " +
            "a hard edge hard.",
            "stops:[{position:number, color:[r,g,b]|hex, opacity?:number, midpoint?:number}], itemId?:guid",
            (ctx, p) =>
            {
                PathItem path = GradientTarget(ctx, p);
                GradientSpec gradient = GradientOf(path) with { Stops = ParseStops(p) };
                SetGradient(ctx, path, gradient);
                return GradientJson(gradient);
            });

        Add("gradient.addStop",
            "Add a stop to a gradient and return its index in the ordered ramp, which is what the " +
            "other stop operations address.",
            "position:number (0..1), color:[r,g,b]|hex, opacity?:number, midpoint?:number, itemId?:guid",
            (ctx, p) =>
            {
                PathItem path = GradientTarget(ctx, p);
                GradientSpec gradient = GradientOf(path);
                GradientStop added = ParseStop(p);

                List<GradientStop> stops = gradient.Normalised().ToList();
                stops.Add(added);
                stops = stops.OrderBy(s => s.Position).ToList();

                gradient = gradient with { Stops = stops };
                SetGradient(ctx, path, gradient);

                return new
                {
                    index = stops.FindIndex(s => ReferenceEquals(s, added)),
                    gradient = GradientJson(gradient),
                };
            });

        Add("gradient.removeStop",
            "Remove a stop by its index in the ordered ramp. The last stop cannot go: a gradient " +
            "with no stops has nothing to paint.",
            "index:number, itemId?:guid",
            (ctx, p) =>
            {
                PathItem path = GradientTarget(ctx, p);
                GradientSpec gradient = GradientOf(path);

                List<GradientStop> stops = gradient.Normalised().ToList();
                int index = (int)p.GetLong("index", -1);
                if (index < 0 || index >= stops.Count)
                {
                    throw new EditorOperationException(
                        $"No stop at index {index}; the ramp has {stops.Count}.");
                }

                if (stops.Count <= 1)
                {
                    throw new EditorOperationException("A gradient must keep at least one stop.");
                }

                stops.RemoveAt(index);
                gradient = gradient with { Stops = stops };
                SetGradient(ctx, path, gradient);
                return GradientJson(gradient);
            });

        Add("gradient.moveStop",
            "Move a stop along the ramp. The stops are re-ordered, so indices are the ordered ones.",
            "index:number, position:number (0..1), itemId?:guid",
            (ctx, p) =>
            {
                PathItem path = GradientTarget(ctx, p);
                GradientSpec gradient = WithStop(
                    GradientOf(path), (int)p.GetLong("index", -1),
                    stop => stop with { Position = p.GetDouble("position", stop.Position) });
                SetGradient(ctx, path, gradient);
                return GradientJson(gradient);
            });

        Add("gradient.setStop",
            "Change one stop: its position, colour, opacity or blend midpoint. Only the members " +
            "that are sent change.",
            "index:number, position?, color?, opacity?, midpoint?, itemId?:guid",
            (ctx, p) =>
            {
                PathItem path = GradientTarget(ctx, p);
                GradientSpec gradient = WithStop(
                    GradientOf(path), (int)p.GetLong("index", -1),
                    stop => stop with
                    {
                        Position = p.TryGetProperty("position", out _)
                            ? p.GetDouble("position", stop.Position)
                            : stop.Position,
                        Color = p.TryGetProperty("color", out _)
                            ? ParseColorElement(p, "color")
                            : stop.Color,
                        Opacity = p.TryGetProperty("opacity", out _)
                            ? p.GetDouble("opacity", stop.Opacity)
                            : stop.Opacity,
                        Midpoint = p.TryGetProperty("midpoint", out _)
                            ? p.GetDouble("midpoint", stop.Midpoint)
                            : stop.Midpoint,
                    });
                SetGradient(ctx, path, gradient);
                return GradientJson(gradient);
            });

        Add("gradient.reverse",
            "Reverse a gradient: every stop mirrors across the middle of the ramp, so the picture " +
            "flips without the colours changing.",
            "itemId?:guid",
            (ctx, p) =>
            {
                PathItem path = GradientTarget(ctx, p);
                GradientSpec gradient = GradientOf(path) with
                {
                    Stops = GradientOf(path).Normalised()
                        .Select(s => s with { Position = 1.0 - s.Position })
                        .OrderBy(s => s.Position)
                        .ToList(),
                };
                SetGradient(ctx, path, gradient);
                return GradientJson(gradient);
            });

        Add("gradient.setGeometry",
            "Place the ramp on the object: the linear start and end, the radial centre, radii and " +
            "rotation, the conical angle, or the freeform points and mode. Linear and radial " +
            "geometry is normalised to the object's bounds, which is how it survives a resize.",
            "start?:{x,y}, end?:{x,y}, centre?:{x,y}, radiusX?:number, radiusY?:number, " +
            "rotation?:number (degrees), angle?:number (degrees), " +
            "freeformMode?:points|lines, points?:[{x,y,color,opacity?}], lines?:[{from,to}], itemId?:guid",
            (ctx, p) =>
            {
                PathItem path = GradientTarget(ctx, p);
                GradientSpec gradient = GradientOf(path);

                if (TryPoint(p, "start", out Point2D start))
                {
                    gradient = gradient with { Start = start };
                }

                if (TryPoint(p, "end", out Point2D end))
                {
                    gradient = gradient with { End = end };
                }

                if (TryPoint(p, "centre", out Point2D centre))
                {
                    gradient = gradient with { Center = centre };
                }

                if (p.TryGetProperty("radiusX", out _))
                {
                    gradient = gradient with { RadiusX = p.GetDouble("radiusX", gradient.RadiusX) };
                }

                if (p.TryGetProperty("radiusY", out _))
                {
                    gradient = gradient with { RadiusY = p.GetDouble("radiusY", gradient.RadiusY) };
                }

                if (p.TryGetProperty("rotation", out _))
                {
                    gradient = gradient with { Rotation = p.GetDouble("rotation", gradient.Rotation) };
                }

                if (p.TryGetProperty("angle", out _))
                {
                    gradient = gradient with { Angle = p.GetDouble("angle", gradient.Angle) };
                }

                if (p.TryGetProperty("freeformMode", out _))
                {
                    gradient = gradient with { FreeformMode = ParseFreeformMode(p) };
                }

                if (p.TryGetProperty("points", out _))
                {
                    gradient = gradient with { Points = ParseFreeformPoints(p) };
                }

                if (p.TryGetProperty("lines", out _))
                {
                    gradient = gradient with { Lines = ParseLines(p) };
                }

                SetGradient(ctx, path, gradient);
                return GradientJson(gradient);
            });

        Add("gradient.solid",
            "Replace a gradient with a solid fill. The colour defaults to the object's flattened " +
            "fill colour, so removing a gradient never leaves it colourless.",
            "color?:[r,g,b]|hex, itemId?:guid",
            (ctx, p) =>
            {
                PathItem path = GradientTarget(ctx, p);
                ColorRgb colour = p.TryGetProperty("color", out _)
                    ? ParseColorElement(p, "color")
                    : path.Fill.Color;
                ctx.Session.Execute(new SetFillCommand(
                    path, FillSpec.Solid(colour, path.Fill.Rule), path.Fill));
                return new { itemId = path.Id, hasGradient = false, color = ColourJson(colour) };
            });

        Add("gradient.remove",
            "Remove the fill entirely, gradient and all. Use gradient.solid to go back to a solid " +
            "colour instead.",
            "itemId?:guid",
            (ctx, p) =>
            {
                PathItem path = GradientTarget(ctx, p);
                ctx.Session.Execute(new SetFillCommand(path, FillSpec.None, path.Fill));
                return new { itemId = path.Id, hasGradient = false, visible = false };
            });

        Add("gradient.sample",
            "Sample the ramp at a position, with the spread applied, so a driver can check what is " +
            "actually painted instead of trusting the stop list.",
            "position:number (0..1; outside it the spread applies), itemId?:guid",
            (ctx, p) =>
            {
                PathItem path = GradientTarget(ctx, p);
                GradientSpec gradient = GradientOf(path);
                double position = p.GetDouble("position", 0.5);
                (ColorRgb colour, double opacity) = gradient.SampleWithSpread(position);

                return new
                {
                    position,
                    color = ColourJson(colour),
                    opacity = Math.Round(opacity, 6),
                };
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

        Add("color.pickAt",
            "Pick the colour of the screen at a point, the way the eyedropper does. The point is in screen " +
            "pixels, and the sampled pixel comes from the screen rather than from this application's " +
            "rendering, so a colour in another window is picked correctly. Reads what the platform can see and " +
            "says so when it cannot.",
            "x:number, y:number",
            (ctx, p) =>
            {
                int x = (int)Math.Round(p.GetDouble("x", 0));
                int y = (int)Math.Round(p.GetDouble("y", 0));
                bool supported = Picking.ScreenColour.Sampler.IsSupported;

                if (!Picking.ScreenColour.TrySample(x, y, out ColorRgb colour))
                {
                    return new
                    {
                        picked = false,
                        supported,
                        x,
                        y,
                        reason = supported
                            ? "the point is outside every display"
                            : "this platform cannot read the screen",
                    };
                }

                EditorColorState.Shared.SetPicked(colour);
                return new
                {
                    picked = true,
                    supported,
                    x,
                    y,
                    r = Math.Round(colour.R, 6),
                    g = Math.Round(colour.G, 6),
                    b = Math.Round(colour.B, 6),
                    hex = HexColor.Format(colour),
                };
            });

        Add("color.picked",
            "The last colour the screen eyedropper chose, and whether this platform can read the screen at " +
            "all. This is the small circle beside the eyedropper.",
            "",
            (ctx, _) =>
            {
                ColorRgb? picked = EditorColorState.Shared.LastPicked;
                return new
                {
                    supported = Picking.ScreenColour.Sampler.IsSupported,
                    hasPicked = picked is not null,
                    hex = picked is { } c ? HexColor.Format(c) : null,
                    r = picked is { } rc ? Math.Round(rc.R, 6) : (double?)null,
                    g = picked is { } gc ? Math.Round(gc.G, 6) : (double?)null,
                    b = picked is { } bc ? Math.Round(bc.B, 6) : (double?)null,
                };
            });

        AddAsync("color.pickScreen",
            "Start the screen eyedropper: click anywhere on the screen and the colour under the cursor is " +
            "chosen, Escape cancels. What is sampled is the screen pixel, not this application's rendering of " +
            "it, so a colour in another window is picked correctly. Refused with its reason where the platform " +
            "cannot do it; a headless host has no window to pick with and should use color.pickAt.",
            "",
            async (ctx, _, ct) =>
            {
                if (ctx.PickFromScreenAsync is null)
                {
                    return new
                    {
                        picked = false,
                        supported = Picking.ScreenColour.Sampler.IsSupported,
                        reason = "no window to pick with - use color.pickAt",
                    };
                }

                (ColorRgb? colour, string? refusal) = await ctx.PickFromScreenAsync().ConfigureAwait(false);

                if (refusal is { } why)
                {
                    return new { picked = false, supported = false, reason = why };
                }

                if (colour is not { } chosen)
                {
                    return new { picked = false, cancelled = true };
                }

                EditorColorState.Shared.SetPicked(chosen);
                return new
                {
                    picked = true,
                    r = Math.Round(chosen.R, 6),
                    g = Math.Round(chosen.G, 6),
                    b = Math.Round(chosen.B, 6),
                    hex = HexColor.Format(chosen),
                };
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
            "touch-up. x and y are WINDOW pixels, NOT model points: convert with view.toScreen " +
            "before aiming a gesture. A batch focuses the canvas before its key events, so a " +
            "shortcut and a drag can go in one call without a separate click.",
            "events:[{kind,x,y,deltaMs,extend?,modifier?,button?,modifiers?,device?,pressure?," +
            "tiltX?,tiltY?,key?,text?,pointerId?,wheelDelta?}], fast?:bool (skip the waits)",
            (ctx, p) =>
            {
                Avalonia.Visual root = Root(ctx);

                var batch = new InputBatch { Events = ReadInputEvents(p) };
                return ReplayInputBatch(
                    ctx, root, batch, p.GetBool("fast", false), p.GetString("save"));
            });

        Add("input.batchFile",
            "Load a batch file and replay it through the same input path a person's events " +
            "take, optionally recording it again and saving the recording somewhere else. A " +
            "session captured in one run replays in another with no window logic of its own.",
            "path:string, fast?:bool, save?:string",
            (ctx, p) =>
            {
                string path = RequireExistingFile(p, "path");
                InputBatch batch = InputBatch.Load(path);
                return ReplayInputBatch(
                    ctx, Root(ctx), batch, p.GetBool("fast", false), p.GetString("save"));
            });

        Add("input.save",
            "Write a batch to its shared gesture file without playing it, so a gesture can be " +
            "prepared and stored, then replayed by input.batchFile. The same file is readable as " +
            "a selection fixture: one file format for a gesture, whatever produced it.",
            "path:string, name?:string, events:[{kind,x,y,deltaMs,...}]",
            (ctx, p) =>
            {
                string path = p.GetString("path")
                    ?? throw new EditorOperationException("Parameter 'path' is required.");

                var batch = new InputBatch
                {
                    Name = p.GetString("name"),
                    Events = ReadInputEvents(p),
                };

                // Validate before writing: a file that names the offending index is worth more
                // than one that replays nothing later.
                batch.Validate();

                string full = Path.GetFullPath(path);
                string? directory = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                batch.Save(full);
                return new { saved = full, events = batch.Events.Count };
            });

        Add("input.last",
            "What was replayed last: the events in order with the deltas the replay used. A " +
            "batch that was played is a batch that can be recorded, and this is that recording.",
            "",
            (ctx, p) =>
            {
                InputBatch? last = LastInputBatch;
                return last is null
                    ? new { count = 0, events = Array.Empty<InputEvent>() }
                    : (object)new { count = last.Events.Count, events = last.Events };
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
        Add("document.openFile", "Open a PDF from disk as a new document (no dialog). A password opens a protected file.", "path:string, password?:string",
            (ctx, p) =>
            {
                string path = RequireExistingFile(p, "path");

                // A protected file parses perfectly and yields nothing without this; the password is what
                // turns it back into a drawing.
                ctx.ViewModel.ImportPdf(File.ReadAllBytes(path), password: p.GetString("password"));
                return new { opened = path, document = ctx.Document.Name, artboards = ctx.Document.Artboards.Count };
            });

        Add("document.importSvg",
            "Import SVG as a new document. The SVG travels as base64 so a driver can hand over a file it built " +
            "rather than one it had to save first. Returns how many objects were imported, by element.",
            "svgBase64:string, name?:string",
            (ctx, p) =>
            {
                string encoded = p.GetString("svgBase64")
                    ?? throw new EditorOperationException("Parameter 'svgBase64' is required.");

                string svg;
                try
                {
                    svg = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
                }
                catch (FormatException exception)
                {
                    throw new EditorOperationException($"svgBase64 is not base64: {exception.Message}");
                }

                VCCad.Core.Svg.SvgImportResult result = VCCad.Core.Svg.SvgReader.Read(svg);
                string? name = p.GetString("name");
                if (!string.IsNullOrWhiteSpace(name))
                {
                    result.Document.Name = name;
                }

                ctx.ViewModel.ImportDocument(result.Document);

                // What the import leaves behind is said here rather than only on a later document.metadata call:
                // the reply's warning surface is where a person and a driver both look (issue #163).
                string[] keptWarnings = KeptForeignPathEffects(result.Document).Warnings;

                return new
                {
                    document = result.Document.Name,
                    artboards = result.Document.Artboards.Count,
                    objects = result.Objects,
                    byElement = result.ByElement,
                    missing = result.Missing,
                    warnings = result.Warnings
                        .Concat(UnreadLivePathEffects(result.Document).Warnings)
                        .Concat(keptWarnings)
                        .ToArray(),
                    livePathEffects = UnreadLivePathEffects(result.Document).Effects,
                };
            });

        Add("document.importSvgFile", "Import an SVG from disk as a new document (no dialog).",
            "path:string",
            (ctx, p) =>
            {
                string path = RequireExistingFile(p, "path");
                VCCad.Core.Svg.SvgImportResult result = VCCad.Core.Svg.SvgReader.ReadFile(path);
                result.Document.Name = Path.GetFileNameWithoutExtension(path);

                ctx.ViewModel.ImportDocument(result.Document);

                // The same report on the file half of the import (issue #163).
                string[] keptWarnings = KeptForeignPathEffects(result.Document).Warnings;

                return new
                {
                    opened = path,
                    document = result.Document.Name,
                    objects = result.Objects,
                    byElement = result.ByElement,
                    missing = result.Missing,
                    warnings = result.Warnings
                        .Concat(UnreadLivePathEffects(result.Document).Warnings)
                        .Concat(keptWarnings)
                        .ToArray(),
                    livePathEffects = UnreadLivePathEffects(result.Document).Effects,
                };
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
        Add("document.exportSvg",
            "Export the active document as SVG. page selects one artboard, counted from zero, and without it the " +
            "whole document is written with each artboard in its place. Returns the SVG as base64, so a driver can " +
            "send it on or write it itself. 'missing' names every object the writer could not put in the file - " +
            "today that is text blocks and rasters - so a driver can tell an incomplete export from a complete one; " +
            "it is empty when nothing was dropped.",
            "page?:number",
            (ctx, p) =>
            {
                int? page = p.ValueKind == JsonValueKind.Object && p.TryGetProperty("page", out _)
                    ? (int)p.GetLong("page", 0)
                    : null;

                VCCad.Core.Svg.SvgWriteResult result = VCCad.Core.Svg.SvgWriter.WriteResult(ctx.Document, page);
                return new
                {
                    svgBase64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(result.Svg)),
                    characters = result.Svg.Length,
                    page,
                    written = result.ByElement,
                    missing = result.Missing,
                };
            });

        Add("document.saveSvgToFile",
            "Write the active document's SVG to disk (no dialog).",
            "path:string, page?:number",
            (ctx, p) =>
            {
                string path = p.GetString("path")
                    ?? throw new EditorOperationException("Parameter 'path' is required.");
                int? page = p.ValueKind == JsonValueKind.Object && p.TryGetProperty("page", out _)
                    ? (int)p.GetLong("page", 0)
                    : null;

                string svg = ctx.ViewModel.ExportSvg(page);
                string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(path, svg);
                return new { saved = path, characters = svg.Length };
            });

        // The status bar shows a padlock for a protected file; this is the same facts without the
        // chrome, because the assistant has to be able to read what the person can see (§1.1).
        Add("document.security", "What the open document's file was protected with, and what it permits.", "",
            (ctx, _) =>
            {
                VCCad.Core.Model.DocumentSecurity? security = ctx.Document.Security;
                if (security is null)
                {
                    return new { encrypted = false };
                }

                return new
                {
                    encrypted = true,
                    cipher = security.Cipher,
                    opened = security.Opened,
                    openedWithOwnerPassword = security.OpenedWithOwnerPassword,
                    permissions = security.Listed()
                        .Select(p => new { name = p.Name, allowed = p.Allowed })
                        .ToList(),
                };
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

        Add("capture.start",
            "Start recording the canvas to an FFV1 video file, for reviewing what a run actually did. " +
            "FFV1 is lossless, so thin lines and small text survive review - a lossy codec would make a " +
            "rendering fault indistinguishable from a compression artefact.",
            "path?:string, fps?:number (default 10), maxWidth?:number (default 1280), encoder?:string",
            (ctx, p) =>
            {
                static string DefaultPath() => System.IO.Path.Combine(
                    "artifacts", $"capture-{DateTime.UtcNow:yyyyMMdd-HHmmss}.mkv");

                Avalonia.Controls.Window? window = ctx.UiRoot?.Invoke() as Avalonia.Controls.Window;
                if (window is null)
                {
                    throw new EditorOperationException("No window to record.");
                }

                string path = p.TryGetProperty("path", out JsonElement pv) && pv.ValueKind == JsonValueKind.String
                    ? pv.GetString() ?? DefaultPath()
                    : DefaultPath();
                int fps = p.TryGetProperty("fps", out JsonElement fv) && fv.ValueKind == JsonValueKind.Number
                    ? fv.GetInt32()
                    : 10;
                double maxWidth = p.TryGetProperty("maxWidth", out JsonElement mv) && mv.ValueKind == JsonValueKind.Number
                    ? mv.GetDouble()
                    : 1280;
                string? encoder = p.TryGetProperty("encoder", out JsonElement ev) && ev.ValueKind == JsonValueKind.String
                    ? ev.GetString()
                    : null;

                return VCCad.App.Capture.CanvasRecording.Start(
                    () => VCCad.App.Views.ScreenCapture.CaptureBgra(window, maxWidth), path, fps, encoder);
            });

        Add("capture.stop",
            "Stop recording and report what was written: the path, the frames, the duration and any frames " +
            "dropped because the encoder could not keep up.",
            "",
            (ctx, p) =>
            {
                VCCad.App.Capture.CaptureResult result = VCCad.App.Capture.CanvasRecording.Stop();
                return new
                {
                    path = result.Path,
                    frames = result.Frames,
                    dropped = result.Dropped,
                    seconds = Math.Round(result.Seconds, 2),
                    bytes = result.Bytes,
                    codec = "ffv1",
                    encoderExitCode = result.EncoderExitCode,
                    encoderOutput = result.EncoderOutput,
                };
            });

        Add("capture.status",
            "Whether a recording is running, where it is going and how far it has got.",
            "",
            (ctx, p) => VCCad.App.Capture.CanvasRecording.Status());

        Add("transform.scaleOptions",
            "What is scaled when an object is scaled: its line weights, its shape corners, the radii of any " +
            "layer effect, and the type inside a text frame. These are document preferences rather than " +
            "properties of one object - they decide what a scale means.",
            "lineWeights?:bool, shapeCorners?:bool, layerEffectRadii?:bool, textFrameContents?:bool",
            (ctx, p) =>
            {
                VCCad.Core.Commands.ScaleWithObject o = ctx.Session.ScaleOptions;
                if (p.TryGetProperty("lineWeights", out JsonElement lw) && lw.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    o.LineWeights = lw.GetBoolean();
                }

                if (p.TryGetProperty("shapeCorners", out JsonElement sc) && sc.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    o.ShapeCorners = sc.GetBoolean();
                }

                if (p.TryGetProperty("layerEffectRadii", out JsonElement le) && le.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    o.LayerEffectRadii = le.GetBoolean();
                }

                if (p.TryGetProperty("textFrameContents", out JsonElement tf) && tf.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    o.TextFrameContents = tf.GetBoolean();
                }

                return new
                {
                    lineWeights = o.LineWeights,
                    shapeCorners = o.ShapeCorners,
                    layerEffectRadii = o.LayerEffectRadii,
                    textFrameContents = o.TextFrameContents,
                };
            });

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
        Add("tool.list",
            "Every tool that can be set, including the nine shapes the compound shape button holds - they " +
            "are tools a person picks, so they are names a driver can set.", "",
            (_, _) => Enum.GetNames<EditorTool>()
                .Select(t => t.ToLowerInvariant())
                .Concat(ShapeLibrary.All.Select(ShapeLibrary.Name))
                .ToArray());

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

        Add("tool.toggle",
            "Toggle to a tool, or back to the one before it when it is already active - what the A " +
            "key does between the pen and the node tools. Coming back is an ordinary switch, so the " +
            "tool gone back to is the one a further toggle returns from.",
            "tool?:string (default node)",
            (ctx, p) =>
            {
                string name = p.GetString("tool") ?? "node";

                // A shape name is a tool: the compound button's nine shapes are what a person picks, so
                // `tool.set star` and clicking Star in the flyout leave the same state - and `tool.get`
                // reports "star" rather than "shape", because that is what was chosen.
                if (!EditorToolNames.TryResolve(name, out EditorTool tool, out ShapeKind? chosen))
                {
                    throw new EditorOperationException(
                        $"Unknown tool '{name}'. Use one of: {string.Join(", ", EditorToolNames.AllNames)}.");
                }

                if (chosen is not null)
                {
                    ctx.ViewModel.CurrentShape = chosen.Value;
                    ctx.ViewModel.Tool = EditorTool.Shape;
                    return new { tool = "shape", shape = ShapeLibrary.Name(chosen.Value) };
                }

                EditorTool was = ctx.ViewModel.Tool;
                ctx.ViewModel.ToggleTool(tool);
                return new
                {
                    tool = ctx.ViewModel.Tool.ToString().ToLowerInvariant(),
                    from = was.ToString().ToLowerInvariant(),
                };
            });

        Add("tool.get", "The active tool, and the one it would toggle back to. When the shape tool is " +
            "active this also reports which of the nine shapes is armed, because that is the tool a " +
            "person actually chose.", "",
            (ctx, _) => new
            {
                tool = ctx.ViewModel.Tool.ToString().ToLowerInvariant(),
                previous = ctx.ViewModel.PreviousTool.ToString().ToLowerInvariant(),
                shape = ctx.ViewModel.Tool == EditorTool.Shape
                    ? ShapeLibrary.Name(ctx.ViewModel.CurrentShape)
                    : null,
            });

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
                        activeTab = p.Tabs.FirstOrDefault(t => t.Active)?.Id,
                        tabs = p.Tabs.Select(t => new
                        {
                            id = t.Id,
                            title = t.Title,
                            open = t.IsOpen,
                            active = t.Active,
                        }).ToArray(),
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

        Add("pane.setTab",
            "Show a tab inside a docked panel and make it the one showing, by tab id or title. `pane.set` opens a " +
            "pane; this selects a tab **within** one, which is the difference between finding the Arrange panel and " +
            "looking at its Align tab. Reports the pane and the tab that ended up showing.",
            "tab:string",
            (ctx, p) =>
            {
                string tab = p.GetString("tab")
                    ?? throw new EditorOperationException("Parameter 'tab' is required.");

                RequireHost(ctx).SetPaneTab(tab);

                return RequireHost(ctx).Panes()
                    .SelectMany(pane => pane.Tabs
                        .Where(t => t.Active)
                        .Select(t => new { pane = pane.Id, tab = t.Id, title = t.Title }))
                    .FirstOrDefault(t => string.Equals(t.tab, tab, StringComparison.OrdinalIgnoreCase))
                    ?? new { pane = string.Empty, tab, title = string.Empty };
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

        Add("history.exportBatch",
            "Write a session's own input - the pointer, wheel and key events the diary recorded - as " +
            "an input batch that input.batchFile replays. This is what turns a session a person " +
            "performed into a repeatable gesture.",
            "path:string, sessionId?:string (default: one session), max?:number (default 20000)",
            (ctx, p) =>
            {
                string path = p.GetString("path")
                    ?? throw new EditorOperationException("Parameter 'path' is required.");

                InteractionLog diary = RequireHistory(ctx);
                InputBatch batch = DiaryBatchExport.From(
                    diary, p.GetString("sessionId"), (int)p.GetLong("max", 20000));

                if (batch.Events.Count == 0)
                {
                    throw new EditorOperationException(
                        "That session holds no input events to export. Record some pointer or key " +
                        "activity first, or pass a sessionId that has some.");
                }

                string full = Path.GetFullPath(path);
                string? directory = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                batch.Save(full);
                return new
                {
                    saved = full,
                    events = batch.Events.Count,
                    session = p.GetString("sessionId") ?? diary.SessionId,
                    replay = $"input.batchFile {{ \"path\": \"{full}\" }}",
                };
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

    /// <summary>
    /// Reads an "events" array into input events, tolerating the case a driver writes.
    ///
    /// The binding is case-insensitive because a JSON author writes "kind" while the record
    /// calls it Kind; an element that will not bind is reported with its index rather than
    /// silently dropped.
    /// </summary>
    private static List<InputEvent> ReadInputEvents(JsonElement p)
    {
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
            InputEvent? one = element.Deserialize<InputEvent>(InputJson);
            if (one is null)
            {
                throw new EditorOperationException($"Event at index {index} is not an event.");
            }

            parsed.Add(one);
            index++;
        }

        return parsed;
    }

    /// <summary>
    /// Replays a batch into the window while recording it, so a batch that was played is also a
    /// batch that can be saved and replayed.
    ///
    /// The recorder and the replay share one clock, so the deltas that come out are the deltas
    /// that were waited. <paramref name="fast"/> keeps those deltas but does not wait. The sink
    /// is the existing injection path, not a second implementation of input; a malformed batch
    /// is rejected by index before anything is played.
    /// </summary>
    private static object ReplayInputBatch(
        AutomationContext ctx, Avalonia.Visual root, InputBatch batch, bool fast, string? savePath)
    {
        var clock = new SystemInputClock();
        var recorder = new InputRecorder(clock);
        InputReplayResult done;

        try
        {
            done = batch.Replay(
                new TeeInputSink(recorder, InjectionInputSink.For(root)),
                fast ? InputTiming.AsFastAsPossible : InputTiming.RealTime,
                clock);
        }
        catch (InputBatchException bad)
        {
            // The index is the whole value of this: a driver has to be told which event was
            // wrong, not merely that the batch was.
            throw new EditorOperationException(bad.Message);
        }

        LastInputBatch = recorder.Finish(
            batch.Name, ctx.Document, batch.FocusedArtboard, batch.Expected);

        string? file = null;
        if (!string.IsNullOrWhiteSpace(savePath))
        {
            file = Path.GetFullPath(savePath);
            string? directory = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            LastInputBatch.Save(file);
        }

        return new
        {
            delivered = done.Delivered,
            milliseconds = Math.Round(done.Duration.TotalMilliseconds, 1),
            timing = done.Timing.ToString(),
            recorded = LastInputBatch.Events.Count,
            file,
        };
    }

    /// <summary>The last batch played through the registry, for input.last.</summary>
    private static InputBatch? LastInputBatch { get; set; }

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
        // …pdf") instead of requiring an absolute path. The samples are copyrighted and live in a private
        // checkout, so the search is SampleLibrary's rather than a directory guess made here.
        if (VCCad.Core.Samples.SampleLibrary.FindLoose(path) is { } sample)
        {
            return sample;
        }

        throw new EditorOperationException(
            $"File not found: {path} (also searched the samples folder for \"{Path.GetFileName(path)}\").");
    }

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

    // ---- shapes ---------------------------------------------------------
    // A shape is a closed path that knows what it is, so the parameters the handles move are the
    // parameters the geometry came from. Everything here goes through the command stack, for the same
    // reason object.create does: an edit that bypasses it cannot be undone and never marks the
    // document as having unsaved changes.

    private static ShapeParameters ReadShapeParameters(JsonElement p, Vector2D offset)
    {
        var centre = new Point2D(p.GetDouble("x", 100) - offset.X, p.GetDouble("y", 100) - offset.Y);
        bool hasTail = p.TryGetProperty("tailX", out JsonElement tailX);
        p.TryGetProperty("tailY", out JsonElement tailY);

        return new ShapeParameters
        {
            Centre = centre,
            Width = p.GetDouble("width", 100),
            Height = p.GetDouble("height", 100),
            Rotation = p.GetDouble("rotation", 0),
            CornerRadius = p.GetDouble("cornerRadius", 12),
            Points = (int)Math.Round(p.GetDouble("points", 5)),
            InnerRatio = p.GetDouble("innerRatio", 0.45),
            TopRatio = p.GetDouble("topRatio", 0.55),
            HeadLength = p.GetDouble("headLength", 0.35),
            HeadWidth = p.GetDouble("headWidth", 1.0),
            ShaftWidth = p.GetDouble("shaftWidth", 0.32),
            HasTail = hasTail,
            Tail = hasTail
                ? new Point2D(tailX.GetDouble() - offset.X, tailY.GetDouble() - offset.Y)
                : centre,
        };
    }

    /// <summary>
    /// Every stroke in the document whose profile has this name, with where it is and what it should become.
    ///
    /// A profile is referred to by name, so an edit to the asset has to reach the strokes that named it wherever
    /// they are - including inside groups, and including in the pasteboard. Missing one would leave a stroke
    /// drawn from a profile the document no longer has.
    /// </summary>
    private static List<EditWidthProfilesCommand.StrokeEdit> StrokesNaming(
        CadDocument document, string name, Func<StrokeSpec, StrokeSpec> map)
    {
        var edits = new List<EditWidthProfilesCommand.StrokeEdit>();

        foreach (PathItem path in document.AllPaths())
        {
            for (int i = 0; i < path.Strokes.Count; i++)
            {
                if (path.Strokes[i].WidthProfile?.Name == name)
                {
                    edits.Add(new EditWidthProfilesCommand.StrokeEdit(
                        path, i, path.Strokes[i], map(path.Strokes[i])));
                }
            }
        }

        return edits;
    }

    /// <summary>
    /// Applies a change to a stored profile's points, and to every stroke that uses it, as one undo step.
    ///
    /// The re-pointed strokes are **fresh copies** of the profile rather than the library's own instance: a
    /// stroke holds a value, so handing every stroke the same instance would make one stroke's later edit an
    /// edit to all of them, which is not what the model says.
    /// </summary>
    private static object ApplyProfileEdit(
        AutomationContext ctx,
        CadDocument document,
        WidthProfileSpec profile,
        string name,
        Func<IReadOnlyList<WidthPoint>, IReadOnlyList<WidthPoint>> edit,
        string description)
    {
        IReadOnlyList<WidthPoint> points = edit(profile.Points);
        var updated = profile with { Points = points };

        var library = document.WidthProfiles
            .Select(existing => existing.Name == name ? updated : existing)
            .ToList();
        List<EditWidthProfilesCommand.StrokeEdit> edits = StrokesNaming(
            document, name, stroke => stroke with { WidthProfile = updated });

        ctx.Session.Execute(new EditWidthProfilesCommand(document, library, edits, description));
        return new { profile = name, points = points.Count, strokes = edits.Count };
    }

    /// <summary>
    /// A brush built from the parameters a caller gave, holding this build's defaults for the ones they did not.
    ///
    /// The **kind decides which members mean anything**, and is read first: a nib has an angle, a roundness and a
    /// diameter, and an art brush maps an asset of a stated size with a stretch, two flips and a colourisation. A
    /// kind this build does not make is refused by name rather than falling back to a nib, because a caller that
    /// asked for a brush this build cannot make would otherwise get a plausible line and no warning.
    ///
    /// Roundness is clamped into 0..1 and the diameter at zero rather than refused: they describe a shape, and a
    /// caller computing one can arrive a hair outside the range the same way a dragged slider can. A diameter
    /// below zero is not a nib, so it becomes the smallest one there is rather than a brush that draws inside out.
    /// </summary>
    private static BrushSpec ReadBrush(JsonElement p, string name, CadDocument document)
    {
        string kind = (p.GetString("kind") ?? "calligraphic").Trim().ToLowerInvariant();
        switch (kind)
        {
            case "calligraphic" or "nib":
                return BrushSpec.Calligraphic(
                    name,
                    p.GetDouble("angle", 0.0),
                    ClampedRoundness(p.GetDouble("roundness", 1.0)),
                    BrushSize(p, p.GetDouble("diameter", 1.0)));

            case "art":
                return BrushSpec.Art(
                    name,
                    Given(p, "asset") ? ReadAsset(p, document) : null,
                    BrushSize(p, p.GetDouble("diameter", p.GetDouble("size", 1.0))),
                    Given(p, "stretch") ? ReadStretch(p) : ArtStretch.Repeat,
                    p.GetBool("flipAcross", false),
                    p.GetBool("flipAlong", false),
                    Given(p, "colourisation") ? ReadColourisation(p) : ArtColourisation.None,
                    Given(p, "shadeColour") ? ReadShadeColour(p) : null);

            default:
                throw new EditorOperationException(
                    $"'{kind}' is not a brush kind this build makes; use calligraphic or art");
        }
    }

    /// <summary>
    /// A brush's size: the nib's long axis, or the width an art asset is drawn across the path.
    ///
    /// One reader for both, because <c>size</c> is the word an art brush's caller reaches for and
    /// <c>diameter</c> is the word the model and the nib already use, and a driver that guessed the other one
    /// should get its brush rather than a size of nothing.
    /// </summary>
    private static double BrushSize(JsonElement p, double value)
        => Math.Max(0.0, Given(p, "size") ? p.GetDouble("size", value) : value);

    /// <summary>
    /// The item an art brush maps, refused by name when the document does not have it.
    ///
    /// The reference is to the document's own artwork, so a brush pointing at an id nothing answers to is a
    /// brush that would draw nothing - and creating one is the moment to say so, when the caller can still pass
    /// the right id, rather than at render time when nothing can.
    /// </summary>
    private static Guid ReadAsset(JsonElement p, CadDocument document)
    {
        if (!p.TryGetGuid("asset", out Guid asset))
        {
            throw new EditorOperationException("'asset' has to be the id of an item in this document");
        }

        if (document.FindItem(asset) is null)
        {
            throw new EditorOperationException(
                $"the document has no item {asset} for an art brush to map; name an item that is in the drawing");
        }

        return asset;
    }

    private static ArtStretch ReadStretch(JsonElement p)
        => (p.GetString("stretch") ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "stretchtofit" or "stretch-to-fit" or "fit" => ArtStretch.StretchToFit,
            "scaleproportionally" or "scale-proportionally" or "proportional" => ArtStretch.ScaleProportionally,
            "repeat" => ArtStretch.Repeat,
            var other => throw new EditorOperationException(
                $"'{other}' is not an art brush stretch; use stretchToFit, scaleProportionally or repeat"),
        };

    private static ArtColourisation ReadColourisation(JsonElement p)
        => (p.GetString("colourisation") ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "none" => ArtColourisation.None,
            "tint" => ArtColourisation.Tint,
            "tintandshade" or "tint-and-shade" or "tint & shade" => ArtColourisation.TintAndShade,
            var other => throw new EditorOperationException(
                $"'{other}' is not an art brush colourisation; use none, tint or tintAndShade"),
        };

    private static ColorRgb ReadShadeColour(JsonElement p)
        => p.TryGetColorArray("shadeColour", out ColorRgb shade)
            ? shade
            : throw new EditorOperationException("'shadeColour' has to be an [r,g,b] colour");

    private static double ClampedRoundness(double roundness) => Math.Clamp(roundness, 0.0, 1.0);

    /// <summary>
    /// Every stroke in the document that names this brush, with where it is and what it should become.
    ///
    /// A brush is referred to by name, so an edit to the asset has to reach the strokes that named it wherever
    /// they are - including inside groups, and including in the pasteboard. Missing one would leave a stroke
    /// drawn from a brush the document no longer has.
    /// </summary>
    private static List<EditBrushesCommand.StrokeEdit> StrokesNamingBrush(
        CadDocument document, string name, Func<StrokeSpec, StrokeSpec> map)
    {
        var edits = new List<EditBrushesCommand.StrokeEdit>();

        foreach (PathItem path in document.AllPaths())
        {
            for (int i = 0; i < path.Strokes.Count; i++)
            {
                if (path.Strokes[i].Brush?.Name == name)
                {
                    edits.Add(new EditBrushesCommand.StrokeEdit(
                        path, i, path.Strokes[i], map(path.Strokes[i])));
                }
            }
        }

        return edits;
    }

    /// <summary>
    /// Applies a change to a stored brush, and to every stroke that uses it, as one undo step.
    ///
    /// The re-pointed strokes are **fresh copies** of the brush rather than the library's own instance: a stroke
    /// holds a value, so handing every stroke the same instance would make one stroke's later edit an edit to all
    /// of them, which is not what the model says.
    /// </summary>
    private static object ApplyBrushEdit(
        AutomationContext ctx,
        CadDocument document,
        BrushSpec brush,
        string name,
        BrushSpec updated,
        string description)
    {
        var library = document.Brushes
            .Select(existing => existing.Name == name ? updated : existing)
            .ToList();
        List<EditBrushesCommand.StrokeEdit> edits = StrokesNamingBrush(
            document, name, stroke => stroke with { Brush = updated });

        ctx.Session.Execute(new EditBrushesCommand(document, library, edits, description));
        return new
        {
            brush = name,
            angle = Math.Round(updated.AngleDegrees, 4),
            roundness = Math.Round(updated.Roundness, 6),
            diameter = Math.Round(updated.Diameter, 4),
            changed = updated != brush,
            strokes = edits.Count,
        };
    }

    /// <summary>
    /// The dynamics curve a caller asked for: their own control points when they gave any, otherwise the preset.
    ///
    /// A curve given with the wrong number of numbers is refused rather than padded, because a curve is two points
    /// and a three-number one is a mistake about which four they are - the same mistake that would silently make
    /// the curve do something else.
    /// </summary>
    private static DynamicsCurve ReadDynamicsCurve(JsonElement p)
    {
        if (p.ValueKind == JsonValueKind.Object &&
            p.TryGetProperty("curve", out JsonElement curve) &&
            curve.ValueKind == JsonValueKind.Array)
        {
            double[] values = curve.EnumerateArray().Select(e => e.GetDouble()).ToArray();
            if (values.Length != 4)
            {
                throw new EditorOperationException(
                    $"a dynamics curve is four numbers - x1, y1, x2, y2 - and {values.Length} were given");
            }

            return new DynamicsCurve(values[0], values[1], values[2], values[3]);
        }

        string presetName = p.GetString("preset") ?? "soft";
        DynamicsPreset preset = presetName.ToLowerInvariant() switch
        {
            "linear" => DynamicsPreset.Linear,
            "soft" => DynamicsPreset.Soft,
            "hard" => DynamicsPreset.Hard,
            "exponential" or "exp" => DynamicsPreset.Exponential,
            _ => throw new EditorOperationException(
                $"'{presetName}' is not a dynamics preset; use linear, soft, hard or exponential"),
        };

        return DynamicsCurve.FromPreset(preset);
    }

    /// <summary>One filter primitive as a caller reads it: its kind, its wiring and its parameters.</summary>
    private static object DescribePrimitive(FilterPrimitive primitive) => new
    {
        kind = primitive.Kind.ToString(),
        input = primitive.Input,
        input2 = primitive.Input2,
        result = primitive.Result.Length == 0 ? null : primitive.Result,
        radius = Math.Round(primitive.Radius, 4),
        dx = Math.Round(primitive.Dx, 4),
        dy = Math.Round(primitive.Dy, 4),
        // 0-255, because that is how every operation takes a colour - so what filter.list reports can be sent
        // straight back to filter.setPrimitiveParameter without a caller having to know the model stores 0-1.
        floodColor = primitive.FloodColor is { } colour ? ColourBytes(colour) : null,
        floodOpacity = Math.Round(primitive.FloodOpacity, 4),
        op = primitive.Operator,
        mode = primitive.Mode,
    };

    private static int[] ColourBytes(ColorRgb colour) => new[]
    {
        (int)Math.Round(Math.Clamp(colour.R, 0.0, 1.0) * 255),
        (int)Math.Round(Math.Clamp(colour.G, 0.0, 1.0) * 255),
        (int)Math.Round(Math.Clamp(colour.B, 0.0, 1.0) * 255),
    };

    /// <summary>
    /// A primitive from the parameters a caller sent, validated against the declaration for its kind.
    ///
    /// **The declaration builds the step.** The kind is taken from it rather than chosen by a switch, and every
    /// parameter is read by the name the declaration gives it - so a primitive added to the registry is buildable
    /// here without this method learning a branch per parameter, and no arm of anything can answer with a
    /// different kind.
    ///
    /// A kind this build does not have, a parameter the kind does not take, a value outside what the kind allows, a
    /// required parameter that is missing and a declared parameter this build has no member for are each
    /// **refused**: a filter is a graph, and a step that silently does nothing changes what every step after it
    /// receives. <paramref name="ignored"/> names the members of the caller's own envelope - `name`, `index` -
    /// which are not the primitive's.
    /// </summary>
    private static FilterPrimitive ReadPrimitive(JsonElement entry, params string[] ignored)
    {
        if (entry.ValueKind != JsonValueKind.Object)
        {
            throw new EditorOperationException("a primitive must be an object like {kind, in?, in2?, result?, ...}");
        }

        string kind = entry.GetString("kind") ?? string.Empty;
        FilterPrimitiveDefinition definition = FilterPrimitiveRegistry.Find(kind)
            ?? throw new EditorOperationException(
                $"'{kind}' is not a filter primitive this build has; filter.kinds lists " +
                string.Join(", ", FilterPrimitiveRegistry.All.Select(known => known.Kind)));

        // A parameter the declaration names and this build cannot set is refused **before anything is built**,
        // rather than being ignored when it happens to be absent: a step missing a value its kind declares is not
        // the step filter.kinds describes, and the one thing a registry is for is that the two cannot come apart
        // in silence.
        foreach (FilterParameter parameter in definition.Parameters)
        {
            if (parameter.Kind != FilterParameterKind.Buffer && !Setters.ContainsKey(parameter.Name))
            {
                throw new EditorOperationException(
                    $"{definition.Element} declares '{parameter.Name}', which this build has no way to set, so a " +
                    "step built here would not be the one filter.kinds describes");
            }
        }

        // **The kind comes from the declaration; nothing switches on it.** There is therefore no arm that can
        // answer with a different primitive - a kind the registry does not have was refused above, and a kind it
        // does have is the kind that lands.
        var primitive = new FilterPrimitive(definition.ModelKind)
        {
            // `in`, `in2` and `result` are the wiring rather than values, and a buffer left out is the previous
            // step's result - except on the steps that take two, where an unnamed first input reads the shape,
            // which is what this operation has meant by a composite with no `in` from the start.
            Input = entry.GetString("in") ?? (definition.Parameter("in2") is not null ? "SourceGraphic" : null),
            Input2 = definition.Parameter("in2") is not null ? entry.GetString("in2") ?? string.Empty : null,
            Result = entry.GetString("result") ?? string.Empty,
        };

        var supplied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty member in entry.EnumerateObject())
        {
            if (member.NameEquals("kind") || ignored.Contains(member.Name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            FilterParameter? parameter = definition.Parameter(member.Name);
            if (parameter is null)
            {
                throw new EditorOperationException(
                    $"{definition.Element} has no parameter '{member.Name}'; it takes " +
                    string.Join(", ", definition.Parameters.Select(p => p.Name)));
            }

            ValidateValue(definition, parameter, member.Value);

            // A member the declaration matched is a member that is **applied**, however it was spelled: a name the
            // declaration accepted and the build then failed to find would be a value dropped without saying so.
            supplied.Add(parameter.Name);
            if (parameter.Kind != FilterParameterKind.Buffer)
            {
                primitive = WithParameter(primitive, parameter.Name, member.Value);
            }
        }

        foreach (FilterParameter parameter in definition.Required)
        {
            if (!supplied.Contains(parameter.Name))
            {
                throw new EditorOperationException($"{definition.Element} needs '{parameter.Name}': {parameter.Meaning}");
            }
        }

        // What the request does not say, the **declaration** says: a morphology's operator is `erode` and a colour
        // matrix's type is `matrix`, and one record cannot start life holding both kinds' defaults at once. The
        // declaration can, and it is what a panel and a driver read, so a step built here is the step they describe.
        foreach (FilterParameter parameter in definition.Parameters)
        {
            if (!supplied.Contains(parameter.Name) && TryDeclaredDefault(parameter, out JsonElement fallback))
            {
                primitive = WithParameter(primitive, parameter.Name, fallback);
            }
        }

        return primitive;
    }

    /// <summary>
    /// The declaration's own default for a parameter, as the value a request would have carried for it.
    ///
    /// A colour's default is one of the format's words - `black`, `white` - and both readers of a colour already
    /// treat an absent one as exactly that, so a colour has nothing to set here and says so by answering false.
    /// </summary>
    private static bool TryDeclaredDefault(FilterParameter parameter, out JsonElement value)
    {
        value = default;
        if (parameter.Default is not { Length: > 0 } text)
        {
            return false;
        }

        if (parameter.Kind == FilterParameterKind.Number &&
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
        {
            value = JsonSerializer.SerializeToElement(number);
            return true;
        }

        if (parameter.Kind == FilterParameterKind.Choice)
        {
            value = JsonSerializer.SerializeToElement(text);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Whether a value is one the declaration allows for a parameter - the range of a number, the words of a
    /// choice, the shape of a colour.
    ///
    /// Refusing here rather than clamping is the point: an operator or a radius outside what the kind declares is a
    /// caller who believed it meant something, and a value silently moved is a picture that does not match the
    /// request with nothing to explain it.
    /// </summary>
    private static void ValidateValue(
        FilterPrimitiveDefinition definition, FilterParameter parameter, JsonElement value)
    {
        string where = $"{definition.Element} '{parameter.Name}'";

        switch (parameter.Kind)
        {
            case FilterParameterKind.Number:
            {
                // `values` is the one declared Number that is really a sequence: a colour matrix is twenty numbers,
                // or the single amount one of the shorthands takes. Keyed on the name rather than the kind, because
                // a name means one thing to every kind that declares it.
                if (parameter.Name.Equals("values", StringComparison.OrdinalIgnoreCase) &&
                    value.ValueKind == JsonValueKind.Array)
                {
                    int given = value.GetArrayLength();
                    bool allNumbers = value.EnumerateArray().All(entry => entry.ValueKind == JsonValueKind.Number);
                    if (allNumbers && given is 1 or 16 or 20)
                    {
                        break;
                    }

                    throw new EditorOperationException(
                        $"{where} is the twenty numbers of the colour matrix, or the single value a shorthand " +
                        $"takes; it was given {given}");
                }

                if (value.ValueKind != JsonValueKind.Number)
                {
                    throw new EditorOperationException($"{where} takes a number");
                }

                double number = value.GetDouble();
                if (number >= parameter.Minimum && number <= parameter.Maximum)
                {
                    break;
                }

                string bound =
                    parameter.Minimum > double.NegativeInfinity && parameter.Maximum < double.PositiveInfinity
                        ? $"between {parameter.Minimum} and {parameter.Maximum}"
                        : parameter.Minimum > double.NegativeInfinity
                            ? $"at least {parameter.Minimum}"
                            : $"at most {parameter.Maximum}";
                throw new EditorOperationException($"{where} must be {bound}; {number} is not");
            }

            case FilterParameterKind.Color:
                if (!TryColour(value, out _))
                {
                    throw new EditorOperationException(
                        $"{where} takes a colour as [r,g,b] with components 0-255, or a hex string");
                }

                break;

            case FilterParameterKind.Choice:
            {
                string? text = value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() : null;
                if (text is null || (parameter.Choices is { Length: > 0 } choices &&
                                     !choices.Contains(text, StringComparer.OrdinalIgnoreCase)))
                {
                    throw new EditorOperationException(
                        $"{where} is not one of {string.Join(", ", parameter.Choices ?? Array.Empty<string>())}");
                }

                break;
            }

            case FilterParameterKind.Buffer:
                if (value.ValueKind != JsonValueKind.String)
                {
                    throw new EditorOperationException($"{where} takes a buffer name");
                }

                break;
        }
    }

    /// <summary>
    /// A colour the way every other operation takes one: [r,g,b] with components 0-255, or a hex string.
    ///
    /// The model's own channels are 0-1, which is the one place in this build where a colour is not a byte - so
    /// this is where the two meet, rather than each operation converting on its own.
    /// </summary>
    private static bool TryColour(JsonElement value, out ColorRgb colour)
    {
        colour = ColorRgb.Black;

        if (value.ValueKind == JsonValueKind.Array)
        {
            double[] parts = value.EnumerateArray()
                .Where(part => part.ValueKind == JsonValueKind.Number)
                .Select(part => part.GetDouble())
                .ToArray();
            if (parts.Length < 3)
            {
                return false;
            }

            static byte Channel(double component) => (byte)Math.Clamp((int)Math.Round(component), 0, 255);
            colour = ColorRgb.FromBytes(Channel(parts[0]), Channel(parts[1]), Channel(parts[2]));
            return true;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            try
            {
                colour = HexColor.Parse(value.GetString()!);
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// Every parameter this build can set, by the name the declaration gives it, and the record member each one
    /// writes.
    ///
    /// A name and a member are the same thing written twice in C# - `numOctaves` is `Octaves`, `values` is
    /// `Matrix` - and this is the only place the two are written together. It is a map rather than a switch so that
    /// "which names can be set" is a question with an answer: a kind that declares a parameter with no entry here
    /// is refused outright rather than built with a value nothing applied.
    /// </summary>
    private static readonly Dictionary<string, Func<FilterPrimitive, JsonElement, FilterPrimitive>> Setters =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["radius"] = (primitive, value) => primitive with { Radius = value.GetDouble() },
            ["dx"] = (primitive, value) => primitive with { Dx = value.GetDouble() },
            ["dy"] = (primitive, value) => primitive with { Dy = value.GetDouble() },
            ["floodColor"] = (primitive, value) =>
                primitive with { FloodColor = TryColour(value, out ColorRgb ink) ? ink : ColorRgb.Black },
            ["floodOpacity"] = (primitive, value) => primitive with { FloodOpacity = value.GetDouble() },
            ["operator"] = (primitive, value) => primitive with { Operator = value.GetString()!.Trim() },
            ["mode"] = (primitive, value) => primitive with { Mode = value.GetString()!.Trim() },
            ["type"] = (primitive, value) => primitive with { Type = value.GetString()!.Trim() },
            ["values"] = (primitive, value) => primitive with { Matrix = MatrixValue(value) },
            ["scale"] = (primitive, value) => primitive with { Scale = value.GetDouble() },
            ["xChannel"] = (primitive, value) => primitive with { XChannel = value.GetString()!.Trim() },
            ["yChannel"] = (primitive, value) => primitive with { YChannel = value.GetString()!.Trim() },
            ["baseFrequency"] = (primitive, value) => primitive with { BaseFrequency = value.GetDouble() },
            ["numOctaves"] = (primitive, value) => primitive with { Octaves = (int)Math.Round(value.GetDouble()) },
            ["seed"] = (primitive, value) => primitive with { Seed = (int)Math.Round(value.GetDouble()) },
            ["surfaceScale"] = (primitive, value) => primitive with { SurfaceScale = value.GetDouble() },
            ["diffuseConstant"] = (primitive, value) => primitive with { DiffuseConstant = value.GetDouble() },
            ["specularConstant"] = (primitive, value) => primitive with { SpecularConstant = value.GetDouble() },
            ["specularExponent"] = (primitive, value) => primitive with { SpecularExponent = value.GetDouble() },
            ["lightingColor"] = (primitive, value) =>
                primitive with { LightingColor = TryColour(value, out ColorRgb light) ? light : ColorRgb.White },
            ["azimuth"] = (primitive, value) => primitive with { Azimuth = value.GetDouble() },
            ["elevation"] = (primitive, value) => primitive with { Elevation = value.GetDouble() },
        };

    /// <summary>One value of one primitive, set by the name the declaration gives it.</summary>
    private static FilterPrimitive WithParameter(FilterPrimitive primitive, string parameter, JsonElement value)
        => Setters.TryGetValue(parameter, out Func<FilterPrimitive, JsonElement, FilterPrimitive>? set)
            ? set(primitive, value)
            // A parameter the registry declares but this build cannot set is a defect, not a caller error: it says
            // the declaration grew a parameter without the engine learning it. Failing loudly is how it is found.
            : throw new EditorOperationException(
                $"'{parameter}' is declared for {primitive.Kind} but this build has no way to set it");

    /// <summary>
    /// A colour matrix's `values`: the twenty numbers of the 4x5 matrix, or the single amount a shorthand takes.
    ///
    /// The declaration calls it a Number because a shorthand is one number, and the model holds both spellings in
    /// its one `Matrix` member - so both are read here rather than a caller spelling the matrix out being told it
    /// "takes a number".
    /// </summary>
    private static double[] MatrixValue(JsonElement value)
        => value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(entry => entry.GetDouble()).ToArray()
            : new[] { value.GetDouble() };

    /// <summary>The filter a caller named, or an error naming what is missing.</summary>
    private static FilterSpec RequireFilter(AutomationContext ctx, string? name)
    {
        name ??= string.Empty;
        return ctx.Document.FindFilter(name)
            ?? throw new EditorOperationException($"there is no filter called '{name}'");
    }

    /// <summary>The primitive a caller named by position, or an error naming how many there are.</summary>
    private static int PrimitiveIndex(JsonElement p, FilterSpec filter)
    {
        int index = (int)p.GetLong("index", -1);
        if (index < 0 || index >= filter.Primitives.Count)
        {
            throw new EditorOperationException(
                $"'{filter.Name}' has {filter.Primitives.Count} primitives, so there is no primitive {index}");
        }

        return index;
    }

    /// <summary>
    /// Puts an edited filter back, as **one** undo step.
    ///
    /// Filters are document state rather than a property of an item, so an edit **replaces the whole spec**: the
    /// canvas looks the filter up as it paints, and there is no list element or plain property to assign to here
    /// that anything would hear.
    /// </summary>
    private static void ReplaceFilter(AutomationContext ctx, FilterSpec filter, string description)
        => EditFilters(
            ctx,
            LibraryWith(ctx.Document, filter),
            Array.Empty<FilterEditCommand.ItemEdit>(),
            description);

    /// <summary>
    /// Applies an edit to the filter library and to the items that refer to it, as one undo step.
    ///
    /// This is the filter half of "the person and the assistant have exactly the same powers": writing the document
    /// directly would change the picture without putting anything on the stack, so a driver's Undo would not be the
    /// Undo a person gets from the same edit. Going through <see cref="DocumentSession.Execute"/> is what makes one
    /// operation one step.
    ///
    /// An edit that changes nothing - a parameter set to the value it already had, a filter applied to a selection
    /// that already had it - takes **no** step, because a person pressing Undo after a no-op should get their
    /// previous edit back rather than watch nothing happen.
    /// </summary>
    private static void EditFilters(
        AutomationContext ctx,
        IReadOnlyList<FilterSpec> library,
        IReadOnlyList<FilterEditCommand.ItemEdit> items,
        string description)
    {
        if (items.Count == 0 && SameLibrary(ctx.Document.Filters, library))
        {
            return;
        }

        ctx.Session.Execute(new FilterEditCommand(ctx.Document, library, items, description));
        ctx.ViewModel.NotifyDocumentChanged();
    }

    /// <summary>The library with one filter added or replaced, keeping the order the document already has.</summary>
    private static IReadOnlyList<FilterSpec> LibraryWith(CadDocument document, FilterSpec filter)
    {
        var library = document.Filters.ToList();
        int existing = library.FindIndex(entry => entry.Name == filter.Name);
        if (existing >= 0)
        {
            library[existing] = filter;
        }
        else
        {
            library.Add(filter);
        }

        return library;
    }

    /// <summary>
    /// Whether two versions of the filter library are the same filters **in the same order**.
    ///
    /// The order is part of the answer rather than an implementation detail: it is the order the serializer writes
    /// them in, so the same filters in a different order are a different file.
    /// </summary>
    private static bool SameLibrary(IReadOnlyList<FilterSpec> a, IReadOnlyList<FilterSpec> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Count; i++)
        {
            if (a[i] != b[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether a graph can be evaluated at all: one producer per name, every buffer either supplied by the renderer
    /// or produced by a step, an output that exists, and no wiring that runs in a circle.
    ///
    /// These are the rules that make "the wiring is the filter" survivable for an editor. Each failure is a
    /// *silent* one at draw time - the engine hands back a transparent buffer - so the picture changes without
    /// anything pointing at the edit that changed it, and refusing where the reason can be said is the only place
    /// it can be said.
    /// </summary>
    private static void ValidateGraph(FilterSpec filter)
    {
        var named = new HashSet<string>(StringComparer.Ordinal);
        foreach (FilterPrimitive primitive in filter.Primitives)
        {
            if (primitive.Result.Length > 0 && !named.Add(primitive.Result))
            {
                throw new EditorOperationException(
                    $"two steps both call their answer '{primitive.Result}'; a buffer name has to have one producer, " +
                    "or a step reading it gets whichever of them is evaluated last");
            }

            foreach (string? name in new[] { primitive.Input, primitive.Input2 })
            {
                if (name is not { Length: > 0 } || FilterSpec.IsSourceInput(name) || filter.ProducerOf(name) is not null)
                {
                    continue;
                }

                throw new EditorOperationException(
                    $"'{name}' is not a buffer anything produces; a step reads SourceGraphic, SourceAlpha, " +
                    "BackgroundImage, FillPaint, StrokePaint, or the result of another step");
            }
        }

        if (filter.Output.Length > 0 && filter.ProducerOf(filter.Output) is null)
        {
            throw new EditorOperationException(
                $"'{filter.Output}' is not a result any step names, so the filter would have no answer");
        }

        if (filter.HasCycle)
        {
            throw new EditorOperationException(
                "the wiring runs in a circle - a step ends up reading the buffer it produces, which has no answer " +
                "to evaluate");
        }
    }

    private static string? NullIfEmpty(string? name)
        => string.IsNullOrEmpty(name) ? null : name;

    /// <summary>
    /// A filter as an operation reports it, in one place.
    ///
    /// `filter.list` and `filter.setRegion` answer with the same description, so the settings a driver can read are
    /// exactly the ones it can write - a member added to one and not the other is how `primitiveUnits` and
    /// `filterRes` came to be carried by the model and reachable by nobody.
    /// </summary>
    private static object DescribeFilter(FilterSpec filter) => new
    {
        name = filter.Name,
        x = filter.X,
        y = filter.Y,
        width = filter.Width,
        height = filter.Height,
        units = filter.ObjectBoundingBox ? "objectBoundingBox" : "userSpaceOnUse",
        primitiveUnits = filter.PrimitiveUnitsObjectBoundingBox ? "objectBoundingBox" : "userSpaceOnUse",
        filterRes = FilterResPair(filter),
        output = filter.Output.Length == 0 ? null : filter.Output,
        primitives = filter.Primitives.Select(DescribePrimitive).ToArray(),
    };

    /// <summary>
    /// A filter's resolution as the pair a caller sends back, or null when it named none.
    ///
    /// Always the pair, even when the two axes agree: SVG lets the attribute be a single number, and a reader asked
    /// to tell "one number" from "the same number twice" would be reading a distinction the model does not keep.
    /// </summary>
    private static int[]? FilterResPair(FilterSpec filter)
        => filter.HasFilterResolution
            ? new[] { filter.FilterResolutionX!.Value, filter.FilterResolutionY!.Value }
            : null;

    /// <summary>
    /// The `primitiveUnits` a caller named, or null when they named none.
    ///
    /// SVG's own two spellings rather than a boolean, because that is the attribute the value comes from and it is
    /// how filter.list reports it - a boolean would make every reader translate it back.
    /// </summary>
    private static bool? OptionalPrimitiveUnits(JsonElement p)
        => Given(p, "primitiveUnits") ? ReadPrimitiveUnits(p) : null;

    private static bool ReadPrimitiveUnits(JsonElement p)
    {
        string? value = p.GetString("primitiveUnits");
        return value switch
        {
            "objectBoundingBox" => true,
            "userSpaceOnUse" => false,
            _ => throw new EditorOperationException(
                $"primitiveUnits is '{value ?? "not a string"}'; it is objectBoundingBox or userSpaceOnUse, which " +
                "are SVG's own two"),
        };
    }

    /// <summary>
    /// The `filterRes` a caller named: one number for both axes, or a pair of them, or null to clear it.
    ///
    /// Anything else is refused rather than rounded to a number the caller did not ask for, because the resolution
    /// is part of the picture rather than an implementation detail.
    /// </summary>
    private static (int X, int Y)? ReadFilterRes(JsonElement p)
    {
        JsonElement value = p.GetProperty("filterRes");
        switch (value.ValueKind)
        {
            case JsonValueKind.Null:
                return null;
            case JsonValueKind.Number:
            {
                int both = (int)Math.Round(value.GetDouble());
                return (both, both);
            }
            case JsonValueKind.Array:
            {
                double[] parts = value.EnumerateArray()
                    .Select(entry => entry.ValueKind == JsonValueKind.Number ? entry.GetDouble() : double.NaN)
                    .ToArray();
                if (parts.Length is 1 or 2 && parts.All(part => !double.IsNaN(part)))
                {
                    int x = (int)Math.Round(parts[0]);
                    return (x, parts.Length == 2 ? (int)Math.Round(parts[1]) : x);
                }

                break;
            }
        }

        throw new EditorOperationException(
            "filterRes is one number for both axes or a pair of them, as SVG writes it, or null for the caller's " +
            "own scale");
    }

    /// <summary>
    /// Refuses a resolution this build will not allocate, **here** rather than at draw time.
    ///
    /// A resolution is a request to allocate the region at that size, and the engine refuses one it cannot make -
    /// but a filter is evaluated while painting, so a model carrying one throws inside the renderer and the artwork
    /// disappears instead of the call failing. The bound is the model's own, so an operation and the reader agree
    /// about what is storable.
    /// </summary>
    private static void RequireAllocatableFilterRes((int X, int Y)? resolution)
    {
        if (resolution is not { } pair || FilterSpec.AcceptsFilterResolution(pair.X, pair.Y))
        {
            return;
        }

        throw new EditorOperationException(
            $"filterRes {pair.X} by {pair.Y} is not one this build will allocate: a filter is evaluated at between " +
            $"1 and {FilterSpec.MaximumFilterResolution} pixels across, because it allocates the region at that size");
    }

    /// <summary>
    /// Whether the caller named this parameter at all.
    ///
    /// The distinction a default cannot carry: `GetDouble("width", 1)` answers 1 for a request that gave no width,
    /// and for a request that gave a width of 1 - so an edit that must leave an unnamed member alone has to ask
    /// whether the member was there rather than what it reads as.
    /// </summary>
    private static bool Given(JsonElement p, string name)
        => p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out _);

    /// <summary>
    /// Runs a set of per-path edits as **one** undo step, or nothing at all when there are none.
    ///
    /// The composition is the point, not a convenience. A gesture that lands on a selection - choosing a brush,
    /// switching a fill rule, dragging a gradient focus - is one act to the person who made it, so it has to be one
    /// entry on the stack or Undo takes half of it back. `CompositeCommand` is exactly that batch, and the loop is
    /// the same one <c>DocumentSession.ExecuteIfAny</c> builds, so a driver and a pane cannot end up with different
    /// undo depths for the same edit. An empty list adds no command: a step that undoes to where it started reads as
    /// "undo did nothing".
    /// </summary>
    private static void ExecuteAll(AutomationContext ctx, List<IUndoableCommand> edits, string label)
    {
        if (edits.Count == 0)
        {
            return;
        }

        ctx.Session.Execute(edits.Count == 1 ? edits[0] : new CompositeCommand(label, edits));
    }

    /// <summary>
    /// The live path effects an imported document carries that this build did not read, and the warning that says
    /// so.
    ///
    /// **Reported rather than ignored.** The SVG reader keeps the reference a path carries to its effect
    /// (<c>inkscape:path-effect="#id"</c>) and the path the effect was applied to (<c>inkscape:original-d</c>),
    /// but only for the ones that were **not** read. The reader now keeps the <c>inkscape:path-effect</c> element from
    /// <c>defs</c> on the path and translates a powerstroke into a width profile, so a path that carries a
    /// reference is not by itself evidence of anything: what is reported here is a live path effect that produced
    /// no profile - an effect this build does not implement, or one it refused. Reporting a translated effect as
    /// unread would be worse than saying nothing, because it is exactly the failure this family is about: a person
    /// sees the effect applied and a driver is told it was ignored.
    ///
    /// The **name** is the id the file referred to it by. The effect's own name is on the element the reader kept,
    /// so naming the id is the most this surface can honestly say.
    /// </summary>
    private static (object[] Effects, string[] Warnings) UnreadLivePathEffects(CadDocument document)
    {
        var effects = new List<object>();
        var ids = new List<string>();

        foreach (PathItem path in document.AllPaths())
        {
            if (PathEffects.ReferenceOn(path) is not { } id)
            {
                continue;
            }

            // A profile on the stroke is the reader saying it read the effect and converted it. Only what it could
            // not convert is unread, which is the claim this surface makes.
            if (path.Stroke.HasWidthProfile)
            {
                continue;
            }

            effects.Add(new
            {
                itemId = path.Id,
                name = path.Name,
                effect = id,
                sourcePathData = PathEffects.SourcePathData(path),
            });

            if (!ids.Contains(id, StringComparer.Ordinal))
            {
                ids.Add(id);
            }
        }

        if (ids.Count == 0)
        {
            return (effects.ToArray(), Array.Empty<string>());
        }

        return (effects.ToArray(), new[]
        {
            $"{effects.Count} path{(effects.Count == 1 ? "" : "s")} carr" +
            $"{(effects.Count == 1 ? "ies" : "y")} a live path effect this build did not read " +
            $"({string.Join(", ", ids)}), so the geometry the file drew is kept and the effect is not applied. " +
            "pathEffect.list names them and pathEffect.apply converts one that is handed over.",
        });
    }

    /// <summary>
    /// The foreign definitions the document keeps that no path refers to, as a reply can name them, and what an
    /// import has to say about them.
    ///
    /// The stored text is the element **verbatim**, so the name is read from the XML the way the reader read it:
    /// the <c>id</c> the file gave it and the <c>effect</c> name the element carries. That is the half issue #163
    /// says a count cannot supply - <c>foreignPathEffects: 1</c> says something survived and not what, while the
    /// referenced effects have been identifiable by id through <c>pathEffect.list</c> all along.
    ///
    /// **What cannot be read is left out, never invented.** A definition with no <c>id</c>, an element with an
    /// <c>id</c> and no <c>effect</c> attribute, and text that does not parse at all (a prefix it never declared)
    /// each appear as an entry with the half that could not be read left null, so the list still matches the count
    /// beside it and a driver is told the document holds something whose name it cannot be given. The effect name
    /// is never defaulted from the id and the id is never defaulted from the effect name - the rule that produced
    /// #140, #143, #144, #150, #151, #152, #155, #158, #160 and #162.
    ///
    /// The **warning** is the same report on the import reply's existing surface, which is where a person and a
    /// driver both look: an import that leaves foreign definitions in the document says so at the time rather than
    /// leaving it to be found by a later <c>document.metadata</c> call. It names what it can and never announces a
    /// referenced effect, which travels on the path that points at it.
    /// </summary>
    private static (object[] Names, string[] Warnings) KeptForeignPathEffects(CadDocument document)
    {
        var names = new List<object>();
        var described = new List<string>();
        int unnameable = 0;

        foreach (string xml in document.ForeignPathEffects)
        {
            string? id = null;
            string? effect = null;

            if (!string.IsNullOrWhiteSpace(xml))
            {
                try
                {
                    XElement element = XElement.Parse(xml);
                    id = NonEmpty(element.Attribute("id")?.Value);
                    effect = NonEmpty(element.Attribute("effect")?.Value);
                }
                catch (System.Xml.XmlException)
                {
                    // Not an element this build can read. The entry is still listed - dropping it would make the
                    // list disagree with the count - with neither half claimed.
                }
            }

            names.Add(new { id, effect });
            described.Add(
                id is null
                    ? effect is null
                        ? "a definition with no readable id or effect name"
                        : $"an effect named '{effect}' with no id"
                    : effect is null
                        ? $"'{id}'"
                        : $"'{id}' (effect '{effect}')");

            if (id is null)
            {
                unnameable++;
            }
        }

        if (names.Count == 0)
        {
            return (Array.Empty<object>(), Array.Empty<string>());
        }

        string warning =
            $"{names.Count} foreign path effect{(names.Count == 1 ? "" : "s")} that no path refers to " +
            $"({string.Join("; ", described)}) {(names.Count == 1 ? "is" : "are")} kept on the document, so an " +
            $"export writes {(names.Count == 1 ? "it" : "them")} back into defs. document.metadata names " +
            $"{(names.Count == 1 ? "it" : "them")}.";

        if (unnameable > 0)
        {
            warning +=
                $" {unnameable} of them could not be named: the stored element carries no readable id, so none is " +
                "reported for it.";
        }

        return (names.ToArray(), new[] { warning });
    }

    /// <summary>A string that says something, or null - the rule that keeps an unread half out of a reply.</summary>
    private static string? NonEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    /// <summary>
    /// A live path effect as a request spells it.
    ///
    /// The parameters are taken as **text**, not as numbers, because the effect element's attributes are text in
    /// the file and several of them (an interpolator's name, a cap's name) are not numbers at all. Reading them
    /// here as doubles would silently drop the half of an effect that says how to shape it.
    /// </summary>
    private static PathEffectSpec ReadPathEffect(JsonElement p)
    {
        string effect = p.GetString("effect") ?? string.Empty;
        if (effect.Length == 0)
        {
            throw new EditorOperationException("pathEffect operations need the effect's own name, e.g. 'powerstroke'");
        }

        var parameters = new List<KeyValuePair<string, string>>();
        if (p.ValueKind == JsonValueKind.Object &&
            p.TryGetProperty("parameters", out JsonElement given) &&
            given.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty parameter in given.EnumerateObject())
            {
                parameters.Add(new KeyValuePair<string, string>(
                    parameter.Name,
                    parameter.Value.ValueKind == JsonValueKind.String
                        ? parameter.Value.GetString() ?? string.Empty
                        : parameter.Value.ToString()));
            }
        }

        return new PathEffectSpec(effect, p.GetString("id") ?? string.Empty, p.GetString("version") ?? string.Empty, parameters);
    }

    /// <summary>The width points of a profile, in the shape every profile-reporting operation prints them.</summary>
    private static object[] DescribeWidthPoints(WidthProfileSpec profile)
        => profile.Points.Select(point => (object)new
        {
            position = Math.Round(point.Position, 6),
            left = Math.Round(point.LeftWidth, 4),
            right = Math.Round(point.RightWidth, 4),
            interpolation = point.Interpolation.ToString().ToLowerInvariant(),
        }).ToArray();

    /// <summary>
    /// The stroke a caller named, or null when they named none.
    ///
    /// The two are genuinely different requests: none means every stroke of the stack, which is what these
    /// operations did before the stack was addressable, and one means exactly that member on every selected path -
    /// with a path whose stack is shorter skipped as a gap rather than clamped onto a stroke nobody named.
    /// </summary>
    private static int? OptionalStrokeIndex(JsonElement p)
        => Given(p, "strokeIndex") ? (int)p.GetLong("strokeIndex", 0) : null;

    /// <summary>
    /// Reads the parameters an effect kind declares out of the request, by the names the declaration gives them.
    ///
    /// The declaration decides **which** names to look for, so a parameter added to the registry is reachable here
    /// without this method changing; the switch below only says which property each name sets, because a name and a
    /// member are the same thing written twice in C#. A name the request does not carry is left at its default, and
    /// a name the declaration does not have is never read - so nothing here can invent a parameter.
    ///
    /// `size`, `detail` and `seed` are handled by the caller, because they are the arguments of the record's own
    /// constructor rather than optional settings on it.
    /// </summary>
    private static OutlineEffectSpec ApplyDeclaredEffectParameters(
        OutlineEffectSpec effect, EffectDefinition definition, JsonElement p)
    {
        foreach (EffectParameter parameter in definition.Parameters)
        {
            if (parameter.Name is "size" or "detail" or "seed")
            {
                continue;
            }

            if (p.ValueKind != JsonValueKind.Object ||
                !p.TryGetProperty(parameter.Name, out JsonElement value) ||
                value.ValueKind != JsonValueKind.Number)
            {
                continue;
            }

            double number = value.GetDouble();
            effect = parameter.Name switch
            {
                "ridges" => effect with { Ridges = (int)number },
                "smooth" => effect with { Smooth = number != 0 },
                "join" => effect with { Join = (OutlineJoin)(int)number },
                "density" => effect with { Density = number },
                "overlap" => effect with { Overlap = number },
                "width" => effect with { Width = number },
                "curviness" => effect with { Curviness = number },
                "scatter" => effect with { Scatter = number },
                _ => effect,
            };
        }

        return effect;
    }

    /// <summary>
    /// The width points a caller sent, or an empty list when they sent none.
    ///
    /// A missing or malformed point is skipped rather than defaulting to a width of zero: a point at position
    /// with no width is a request to *pin the stroke shut* at that spot, and inventing one from a typo would
    /// pinch the drawing. Fewer points than asked for is a visible, reportable difference; a pinch in the middle
    /// of a line is not.
    /// </summary>
    private static List<WidthPoint> ReadWidthPoints(JsonElement p)
    {
        var points = new List<WidthPoint>();
        if (p.ValueKind != JsonValueKind.Object ||
            !p.TryGetProperty("points", out JsonElement array) ||
            array.ValueKind != JsonValueKind.Array)
        {
            return points;
        }

        foreach (JsonElement entry in array.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object ||
                !entry.TryGetProperty("position", out JsonElement position) ||
                !entry.TryGetProperty("left", out JsonElement left) ||
                !entry.TryGetProperty("right", out JsonElement right))
            {
                continue;
            }

            WidthInterpolation interpolation = entry.TryGetProperty("interpolation", out JsonElement kind) &&
                                              kind.ValueKind == JsonValueKind.String &&
                                              string.Equals(kind.GetString(), "cubic", StringComparison.OrdinalIgnoreCase)
                ? WidthInterpolation.Cubic
                : WidthInterpolation.Linear;

            points.Add(new WidthPoint(
                position.GetDouble(),
                ClampedWidth(left.GetDouble()),
                ClampedWidth(right.GetDouble()),
                interpolation));
        }

        return points;
    }

    /// <summary>
    /// A width as the model is allowed to hold it, which is never negative.
    ///
    /// The canvas clamps a dragged grip at zero (<see cref="Controls.WidthProfileAnnotators.Drag"/>), so a
    /// person cannot produce a negative width - but a driver or a script can, and a negative one is not a
    /// thinner stroke. A point's widths are full widths and the geometry halves them, so a negative half puts
    /// the offset edge on the other side of the centreline and the stroke draws inside out: mirrored rather
    /// than narrowed, and no longer the profile the model says it is.
    ///
    /// **Clamped rather than refused**, because this is where a measurement a driver computed arrives: a width
    /// across a stroke takes its sign from which side of the centreline the sample landed on, so a side being
    /// pinned shut can be sent as a small negative number. Clamping lands it on the model the drag produces;
    /// refusing would throw the edit away and leave the two routes disagreeing about one value.
    /// </summary>
    private static double ClampedWidth(double width) => Math.Max(0.0, width);

    /// <summary>One stroke as a caller reads it: every member that decides what it looks like.</summary>
    private static object DescribeStroke(StrokeSpec stroke) => new
    {
        visible = stroke.IsVisible,
        r = Math.Round(stroke.Color.R, 6),
        g = Math.Round(stroke.Color.G, 6),
        b = Math.Round(stroke.Color.B, 6),
        hex = HexColor.Format(stroke.Color),
        width = Math.Round(stroke.Width, 4),
        cap = stroke.Cap.ToString().ToLowerInvariant(),
        join = stroke.Join.ToString().ToLowerInvariant(),
        miterLimit = Math.Round(stroke.MiterLimit, 4),
        alignment = stroke.Alignment.ToString().ToLowerInvariant(),
        dash = stroke.Dash.IsEmpty ? null : stroke.Dash.Segments.ToArray(),
        dashOffset = Math.Round(stroke.Dash.Offset, 4),

        // Null when the stroke states nothing, rather than 1 and "normal": a caller reading this has to be able
        // to tell "nobody said" from "somebody chose fully opaque", which is the same distinction the model and
        // the sidecar keep. `effectiveOpacity` is the value a renderer uses, so a caller that only wants to know
        // what it looks like does not have to decide what the absence means.
        opacity = stroke.Opacity is { } stated ? Math.Round(stated, 6) : (double?)null,
        effectiveOpacity = Math.Round(stroke.EffectiveOpacity, 6),
        blend = stroke.Blend?.ToSvgName(),
        profile = DescribeWidthProfile(stroke.WidthProfile),
        brush = DescribeBrush(stroke.Brush),
        effects = stroke.HasEffects
            ? stroke.AllEffects.Select(effect => new
            {
                kind = effect.Kind.ToString(),
                size = Math.Round(effect.Size, 4),
                detail = Math.Round(effect.Detail, 4),
                seed = effect.Seed,
            }).ToArray()
            : null,
        rasterEffects = stroke.HasRasterEffects
            ? stroke.AllRasterEffects.Select(effect => new
            {
                kind = effect.Kind.ToString(),
                radius = Math.Round(effect.Radius, 4),
                offsetX = Math.Round(effect.OffsetX, 4),
                offsetY = Math.Round(effect.OffsetY, 4),
                opacity = Math.Round(effect.Opacity, 4),
                tint = effect.Tint is { } tint
                    ? new[] { Math.Round(tint.R, 6), Math.Round(tint.G, 6), Math.Round(tint.B, 6) }
                    : null,
            }).ToArray()
            : null,
        dynamics = DescribeDynamics(stroke.Dynamics),
    };

    /// <summary>
    /// A width profile as a caller reads it, or null when the stroke has none.
    ///
    /// Shared with `style.commonStroke`, so a panel reading one stroke's profile and a driver reading what a
    /// selection agrees on cannot describe the same profile two different ways.
    /// </summary>
    private static object? DescribeWidthProfile(WidthProfileSpec? profile)
        => profile is { IsEmpty: false }
            ? new
            {
                name = profile.Name,
                points = profile.Points.Select(point => new
                {
                    position = Math.Round(point.Position, 6),
                    left = Math.Round(point.LeftWidth, 4),
                    right = Math.Round(point.RightWidth, 4),
                    interpolation = point.Interpolation.ToString().ToLowerInvariant(),
                }).ToArray(),
            }
            : null;

    /// <summary>
    /// A brush as a caller reads it, or null when the stroke has none.
    ///
    /// The nib parameters are rounded the way every other readout in the registry rounds: far enough to hide the
    /// last bit of a floating-point division, near enough that a value a person set comes back as it was typed.
    ///
    /// The **art members are reported only for an art brush**, keyed off the kind rather than off "is the asset
    /// set": a nib written with an asset would be a brush this build cannot draw, and reporting the member would
    /// hide that. What the art brush does *not* report is a claim that anything draws it - the asset and its
    /// placements are as far as the model goes, and which item that is comes back by id and name so a caller can
    /// find it.
    /// </summary>
    private static object? DescribeBrush(BrushSpec? brush)
        => brush is null
            ? null
            : new
            {
                name = brush.Name,
                kind = brush.Kind.ToString().ToLowerInvariant(),
                angle = Math.Round(brush.AngleDegrees, 4),
                roundness = Math.Round(brush.Roundness, 6),
                diameter = Math.Round(brush.Diameter, 4),
                size = Math.Round(brush.Diameter, 4),
                dynamics = DescribeDynamics(brush.Dynamics),
                art = brush.IsArt
                    ? (object?)new
                    {
                        asset = brush.ArtAsset,
                        stretch = brush.Stretch.ToString().ToLowerInvariant(),
                        flipAcross = brush.FlipAcross,
                        flipAlong = brush.FlipAlong,
                        colourisation = brush.Colourisation.ToString().ToLowerInvariant(),
                        shadeColour = brush.ShadeColour is { } shade
                            ? new[] { shade.R, shade.G, shade.B }
                            : (double[]?)null,

                        // Said out loud, because the model holds the mode and not its effect: a caller reading
                        // "tint" must not take the artwork to have been tinted by anything in this build.
                        colourisationApplied = false,
                    }
                    : null,
            };

    /// <summary>
    /// A tablet response as a caller reads it: the targets that are switched on, with their curves - null when
    /// nothing varies, which is the state an ordinary stroke records.
    /// </summary>
    private static object? DescribeDynamics(DynamicsSpec? dynamics)
        => dynamics is { IsEmpty: false }
            ? Enum.GetValues<DynamicsTarget>()
                .Where(target => dynamics.For(target).Enabled)
                .Select(target => new
                {
                    target = target.ToString(),
                    curve = new[]
                    {
                        dynamics.For(target).Curve.X1,
                        dynamics.For(target).Curve.Y1,
                        dynamics.For(target).Curve.X2,
                        dynamics.For(target).Curve.Y2,
                    },
                }).ToArray()
            : null;

    /// <summary>
    /// A stroke built from the parameters given, falling back to <paramref name="basis"/> for the rest.
    ///
    /// The presence checks matter rather than being defensive noise: <c>ParseColor</c> reports its fallback for
    /// a parameter that is **absent**, so a plain "no colour given" would arrive as black and overwrite the
    /// colour of the stroke being copied.
    /// </summary>
    private static StrokeSpec ReadStroke(JsonElement p, StrokeSpec basis)
    {
        // A caller that passes no parameters sends an **undefined** element, and reading a property of one
        // throws rather than answering "not given". `GetDouble` and `GetLong` tolerate it, which is why the
        // operations that only read numbers never had to know.
        bool given = p.ValueKind == JsonValueKind.Object;

        ColorRgb color = given && p.TryGetProperty("color", out _) ? p.ParseColor("color", basis.Color) : basis.Color;
        StrokeCap cap = given && p.TryGetProperty("cap", out _) ? ParseEnum(p.GetString("cap"), basis.Cap) : basis.Cap;
        StrokeJoin join = given && p.TryGetProperty("join", out _) ? ParseEnum(p.GetString("join"), basis.Join) : basis.Join;
        StrokeAlignment alignment = given && p.TryGetProperty("alignment", out _)
            ? ParseEnum(p.GetString("alignment"), basis.Alignment)
            : basis.Alignment;

        DashPattern dash = basis.Dash;
        if (given && p.TryGetProperty("dash", out JsonElement d) && d.ValueKind == JsonValueKind.Array)
        {
            double[] segments = d.EnumerateArray().Select(e => e.GetDouble()).ToArray();
            dash = segments.Length > 0 ? new DashPattern(segments) : default;
        }

        return new StrokeSpec(
            true,
            color,
            p.GetDouble("width", basis.Width),
            cap,
            join,
            p.GetDouble("miterLimit", basis.MiterLimit),
            alignment,
            dash);
    }

    private static object DescribeShape(PathItem path)
    {
        ShapeDefinition shape = path.Shape!;
        ShapeParameters p = shape.Parameters;

        return new
        {
            itemId = path.Id,
            kind = ShapeLibrary.Name(shape.Kind),
            name = path.Name,
            centre = new { x = p.Centre.X, y = p.Centre.Y },
            width = p.Width,
            height = p.Height,
            rotation = p.Rotation,
            cornerRadius = p.CornerRadius,
            points = p.Points,
            innerRatio = p.InnerRatio,
            topRatio = p.TopRatio,
            headLength = p.HeadLength,
            headWidth = p.HeadWidth,
            shaftWidth = p.ShaftWidth,
            hasTail = p.HasTail,
            tail = new { x = p.Tail.X, y = p.Tail.Y },
            segments = path.SubPaths.Count > 0 ? path.SubPaths[0].SegmentCount : 0,

            // How many segments the shape's own symmetry ties together: the number that has to agree
            // when one of them is edited, and the number a driver needs to check the symmetry at all.
            symmetry = ShapeSymmetry.Orbit(path, 0).Count,
            rotationOrder = ShapeSymmetry.RotationOrder(path),
            mirrored = ShapeSymmetry.Mirrors(path),
            bounds = new
            {
                x = path.BoundingBox().X,
                y = path.BoundingBox().Y,
                width = path.BoundingBox().Width,
                height = path.BoundingBox().Height,
            },
        };
    }

    private static PathItem RequireShape(AutomationContext ctx, JsonElement p, out ShapeDefinition shape)
    {
        PathItem? path = FindSelectedPath(ctx, p);
        if (path is null)
        {
            throw new EditorOperationException("Select a path, or pass itemId.");
        }

        shape = path.Shape
            ?? throw new EditorOperationException(
                $"'{path.Name}' is not a shape - it has no parameters to change. Detach it to edit it freely.");

        return path;
    }

    private static PathItem? FindSelectedPath(AutomationContext ctx, JsonElement p)
    {
        if (p.TryGetGuid("itemId", out Guid id))
        {
            return ctx.Document.Artboards
                .SelectMany(a => a.Layers)
                .SelectMany(l => l.Children)
                .OfType<PathItem>()
                .FirstOrDefault(item => item.Id == id);
        }

        return ctx.Session.SelectedPaths().FirstOrDefault();
    }

    // ---- path booleans and compound paths -------------------------------
    // The work is in the session, because the Pathfinder panel is a second caller: the person's button
    // and the assistant's operation must be the same code, not two implementations that agree today.

    private static object RunBoolean(AutomationContext ctx, BooleanOp op, Func<PathBooleanResult> work)
    {
        try
        {
            PathBooleanResult result = work();
            ctx.ViewModel.NotifyDocumentChanged();
            return Summarise(result);
        }
        catch (InvalidOperationException error)
        {
            throw new EditorOperationException(error.Message);
        }
    }

    private static object Summarise(PathBooleanResult result) => new
    {
        operation = result.Operation,
        inputs = result.Inputs,
        objects = result.Objects,
        contours = result.Contours,
        empty = result.IsEmpty,
    };

    private static object BooleanPaths(AutomationContext ctx, BooleanOp op)
        => RunBoolean(ctx, op, () => ctx.Session.BooleanSelection(op));

    private static object DividePaths(AutomationContext ctx)
        => RunBoolean(ctx, BooleanOp.Union, ctx.Session.DivideSelection);

    private static object MakeCompound(AutomationContext ctx)
        => RunBoolean(ctx, BooleanOp.Union, ctx.Session.MakeCompoundSelection);

    private static object ReleaseCompound(AutomationContext ctx)
        => RunBoolean(ctx, BooleanOp.Union, ctx.Session.ReleaseCompoundSelection);

    private static object ReverseSubpath(AutomationContext ctx, JsonElement p)
    {
        PathItem path = FindSelectedPath(ctx, p)
            ?? throw new EditorOperationException("Select a path, or pass itemId.");

        int index = (int)Math.Round(p.GetDouble("index", 0));
        try
        {
            ctx.Session.ReverseSubpathOfSelection(index);
        }
        catch (InvalidOperationException error)
        {
            throw new EditorOperationException(error.Message);
        }

        ctx.ViewModel.NotifyDocumentChanged();
        return new { itemId = path.Id, index, outlines = path.SubPaths.Count };
    }
    private static ArrangeAxis ReadAxis(string? axis) =>
        string.Equals(axis, "vertical", StringComparison.OrdinalIgnoreCase)
            ? ArrangeAxis.Vertical
            : ArrangeAxis.Horizontal;

    private static (ArrangeAxis Axis, ArrangeEdge Edge) ReadAlign(JsonElement p)
    {
        ArrangeAxis axis = ReadAxis(p.GetString("axis"));

        string edge = p.GetString("edge") ?? throw new EditorOperationException("Parameter 'edge' is required.");
        return (axis, edge.ToLowerInvariant() switch
        {
            "start" or "left" or "top" => ArrangeEdge.Start,
            "centre" or "center" or "middle" => ArrangeEdge.Centre,
            "end" or "right" or "bottom" => ArrangeEdge.End,
            _ => throw new EditorOperationException(
                $"Unknown edge '{edge}'. Use start|centre|end."),
        });
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

    // ------------------------------------------------------------------
    // Member-by-member members, which are "leave it alone" when absent
    // ------------------------------------------------------------------

    /// <summary>
    /// A number member, or null when it was not given.
    ///
    /// The **presence** of the key is the edit, not its value: reading an absent size as 0 would make every request
    /// set the size of everything it touched, and a member nobody named is exactly what a mixed selection cannot
    /// have invented for it. A key that is present but not a number is null too, which means "leave it alone" rather
    /// than a silent zero.
    /// </summary>
    private static double? OptionalNumber(JsonElement p, string name)
        => p.ValueKind == JsonValueKind.Object &&
           p.TryGetProperty(name, out JsonElement value) &&
           value.ValueKind == JsonValueKind.Number &&
           value.TryGetDouble(out double parsed)
            ? parsed
            : null;

    /// <summary>A boolean member, or null when it was not given. See <see cref="OptionalNumber"/> for why.</summary>
    private static bool? OptionalBool(JsonElement p, string name)
        => p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out JsonElement value)
            ? value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            }
            : null;

    /// <summary>
    /// What the selection's text blocks agree on, and what they do not - the report `text.common` returns and
    /// `text.update` answers with, built from the same <see cref="TextSummary"/> the Text panel reads.
    ///
    /// One implementation, so a driver and a person cannot be told different things about the same selection: a
    /// second opinion formed here could drift from the panel's, and the whole point of the mixed reading is that it
    /// is believed.
    /// </summary>
    private static object TextCommonReport(AutomationContext ctx, int runIndex, int changed)
    {
        var blocks = ctx.Session.SelectedTextItems().ToList();
        TextSummary summary = TextSummary.Of(blocks, runIndex);

        // `runs` is the number of runs the reported value is **common to**, and that is deliberately not the
        // same thing as `summary.Runs`. The summary counts the runs the members were *read from*: a block with
        // no run at the inspected index is a gap and is skipped rather than counted as disagreeing, so that
        // count stays non-zero even when the runs that are there disagree with each other. This field answers
        // the other question a driver has - is there a value to act on, and how many runs hold it - so a genuine
        // disagreement reports zero and a run the selection agrees on reports one per agreeing block. The
        // `*Mixed` flags above say *that* a member disagrees; `runs` says whether anything common came out of it.
        int commonRuns = summary.FamilyMixed || summary.FontSizeMixed || summary.BoldMixed
            || summary.ItalicMixed || summary.ColorMixed
                ? 0
                : summary.Runs;

        return new
        {
            changed,
            blocks = summary.Blocks,
            runs = commonRuns,
            runIndex,
            empty = summary.IsEmpty,
            mixed = summary.IsMixed,
            content = summary.Content,
            contentMixed = summary.ContentMixed,
            family = summary.Family,
            familyMixed = summary.FamilyMixed,
            size = summary.FontSize,
            sizeMixed = summary.FontSizeMixed,
            bold = summary.Bold,
            boldMixed = summary.BoldMixed,
            italic = summary.Italic,
            italicMixed = summary.ItalicMixed,
            colour = summary.Color is { } colour ? DescribeColorValue(colour) : null,
            colourMixed = summary.ColorMixed,
            alignment = summary.Alignment?.ToString(),
            alignmentMixed = summary.AlignmentMixed,
            leading = summary.LineSpacing,
            lineSpacingMixed = summary.LineSpacingMixed,
            space = summary.ParagraphSpacing,
            paragraphSpacingMixed = summary.ParagraphSpacingMixed,
            turn = summary.RotationDegrees,
            rotationMixed = summary.RotationMixed,
            frame = summary.FrameWidth,
            frameWidthMixed = summary.FrameWidthMixed,
        };
    }

    /// <summary>A colour as the bytes every field and operation speaks, which is where the model's fractions are read from.</summary>
    private static string DescribeColorValue(ColorRgb color)
        => $"{Byte(color.R)},{Byte(color.G)},{Byte(color.B)},{Byte(color.A)}";

    private static int Byte(double value) => (int)Math.Round(Math.Clamp(value, 0, 1) * 255);

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
