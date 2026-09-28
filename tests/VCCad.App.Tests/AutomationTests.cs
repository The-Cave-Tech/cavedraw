using System.Text.Json;
using Avalonia.Headless.XUnit;
using VCCad.App.Automation;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// Tests for the automation layer: the operation registry, the audit trail, the
/// parameter readers and the no-vision screen/document dump.
///
/// These guard the project's central rule — the person and the assistant have the
/// same powers — so the checks are about coverage of the registry and the shape of
/// what a blind driver can see, not about any single handler.
/// </summary>
public class AutomationTests
{
    [Fact]
    public void SlowOperationsAreAsynchronousSoTheyCannotFreezeTheEditor()
    {
        // Anything that talks to the model must be async: a synchronous handler runs
        // on the UI thread, which froze the window while the assistant was thinking.
        Assert.True(EditorOperations.TryGet("ui.describe", out EditorOperation describe));
        Assert.Null(describe.Handler);
        Assert.NotNull(describe.AsyncHandler);

        // And a synchronous call must refuse rather than silently block the UI.
        var context = new AutomationContext { ViewModel = new EditorViewModel() };
        EditorOperationException error = Assert.Throws<EditorOperationException>(
            () => EditorOperations.Invoke(context, "ui.describe", default));
        Assert.Contains("asynchronous", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheHostReportsBusyStateAndCancelsCleanlyWhenIdle()
    {
        AutomationHost host = AutomationHost.Create(
            new EditorViewModel(),
            () => null,
            () => null,
            new VCCad.App.Ai.LlmOptions { ApiKey = "test" },
            port: 0,
            startServer: true);

        try
        {
            // Idle: not busy, and cancelling is harmless.
            Assert.False(host.IsBusy);
            Assert.False(host.CurrentToken().IsCancellationRequested);
            host.CancelCurrent();
            Assert.False(host.IsBusy);

            // The automation context exposes the same token/cancel surface, so the
            // HTTP route and the overlay abort the same operation.
            Assert.NotNull(host.Context.Cancellation);
            Assert.NotNull(host.Context.Cancel);
            Assert.False(host.Context.Cancellation!().IsCancellationRequested);
        }
        finally
        {
            host.Server?.Dispose();
        }
    }

    [Fact]
    public void TheDescribeRequestIsBuiltFromTheDocumentWithoutAWindow()
    {
        // The prompt/context assembly must not require a window: an automation-only
        // host (tests, headless runs) still has to be able to describe the document.
        CadDocument document = CadDocument.CreateDefault("d");
        Layer layer = document.Artboards[0].AddLayer("UK 14");
        var text = new TextItem { Name = "Bodice", Origin = new Point2D(5, 5) };
        text.Runs.Add(new TextRun { Text = "size 14", FontSize = 10 });
        layer.AddItem(text);

        // The dump is what feeds the prompt; assert the layer filter survives it.
        string dump = VisualTreeDump.Document(document, "UK 14", includeHidden: true);
        Assert.Contains("Bodice", dump, StringComparison.Ordinal);
        Assert.Contains("size 14", dump, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void ViewportOperationsDriveTheSameControlTheToolbarDoes()
    {
        double zoom = 1.0;
        bool autoFit = true;
        var calls = new List<string>();
        var viewport = new ViewportActions(
            Fit: () => { calls.Add("fit"); autoFit = true; zoom = 0.8; },
            ActualSize: () => { calls.Add("actual"); zoom = 1.0; },
            Zoom: z => { calls.Add("zoom"); zoom = z; },
            GetZoom: () => zoom,
            IsAutoFit: () => autoFit,
            ZoomIn: () => { calls.Add("zoomIn"); zoom *= 1.25; },
            ZoomOut: () => { calls.Add("zoomOut"); zoom /= 1.25; },
            GetClipToArtboard: () => true,
            SetClipToArtboard: _ => { },
            CenterOn: (_, _) => { },
            GetViewCenter: () => (0, 0));

        var context = new AutomationContext { ViewModel = new EditorViewModel(), Viewport = viewport };

        EditorOperations.Invoke(context, "view.fit", default);
        Assert.Equal(new[] { "fit" }, calls);

        EditorOperations.Invoke(context, "view.zoom", JsonSerializer.SerializeToElement(new { factor = 2.5 }));
        Assert.Equal(2.5, zoom, 3);

        object? status = EditorOperations.Invoke(context, "view.status", default);
        Assert.NotNull(status);
        Assert.Contains("2.5", System.Text.Json.JsonSerializer.Serialize(status), StringComparison.Ordinal);

        // A nonsense zoom must be rejected rather than applied.
        Assert.Throws<EditorOperationException>(() => EditorOperations.Invoke(
            context, "view.zoom", JsonSerializer.SerializeToElement(new { factor = 0 })));
    }

    [Fact]
    public void PointAndClickAutomationIsPartOfTheSurface()
    {
        // A capability that only exists in a control's event handler is a defect;
        // menus, toolbar buttons and panes must be drivable too.
        foreach (string op in new[] { "ui.find", "ui.click", "ui.setValue", "ui.keys", "ui.dump", "ui.describe" })
        {
            Assert.True(EditorOperations.TryGet(op, out EditorOperation found), $"{op} is not registered");
            Assert.NotNull(found.Handler ?? (object?)found.AsyncHandler);
        }

        // …and the file dialogs, which cannot be driven from a headless run, have
        // dialog-free equivalents for their effect.
        Assert.True(EditorOperations.TryGet("document.openFile", out _));
        Assert.True(EditorOperations.TryGet("document.savePdfToFile", out _));
    }

    [AvaloniaFact]
    public void ChangingAFilesEffectFailsClearlyWhenTheFileIsMissing()
    {
        var context = new AutomationContext { ViewModel = new EditorViewModel() };

        EditorOperationException error = Assert.Throws<EditorOperationException>(() => EditorOperations.Invoke(
            context, "document.openFile",
            JsonSerializer.SerializeToElement(new { path = Path.Combine(Path.GetTempPath(), "definitely-missing.pdf") })));

        Assert.Contains("not found", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------
    // Bulk / lookup operations used by compound requests
    // ------------------------------------------------------------------

    [AvaloniaFact]
    public void SelectingTextThroughTheApiWorks()
    {
        // SelectRange used to accept only paths and groups, which silently dropped
        // text — so "select this text and edit it" failed with "no text is selected".
        var viewModel = new EditorViewModel();
        Layer layer = viewModel.Document.Artboards[0].Layers[0];
        var text = new TextItem { Origin = new Point2D(10, 10) };
        text.Runs.Add(new TextRun { Text = "UPPER CUP", FontSize = 12 });
        layer.AddItem(text);

        var context = new AutomationContext { ViewModel = viewModel };
        EditorOperations.Invoke(context, "selection.set",
            JsonSerializer.SerializeToElement(new { itemIds = new[] { text.Id } }));

        Assert.Contains(text, viewModel.ActiveSession.SelectedTextItems());
        Assert.True(viewModel.HasTransformableSelection);
    }

    [AvaloniaFact]
    public void CentreInFindsTheEnclosingRectangleWithoutBeingToldWhichOne()
    {
        var viewModel = new EditorViewModel();
        Layer layer = viewModel.Document.Artboards[0].Layers[0];

        // A pattern piece with a label sitting inside it.
        PathItem piece = PathFactory.CreateRectangle("Piece", new Rect2D(100, 100, 200, 120));
        layer.AddItem(piece);

        var text = new TextItem { Origin = new Point2D(120, 112) };
        text.Runs.Add(new TextRun { Text = "UPPER CUP", FontSize = 12 });
        layer.AddItem(text);

        var context = new AutomationContext { ViewModel = viewModel };
        EditorOperations.Invoke(context, "text.centerIn",
            JsonSerializer.SerializeToElement(new { itemId = text.Id }));

        Rect2D bounds = text.BoundingBox();
        Assert.Equal(200.0, bounds.Center.X, 0); // piece centre x
        Assert.Equal(160.0, bounds.Center.Y, 0); // piece centre y
        Assert.Equal(TextAlignment.Center, text.Alignment);
    }

    [AvaloniaFact]
    public void OnlyVisibleTouchesOnlyTheMatchingLayers()
    {
        var viewModel = new EditorViewModel();
        Artboard artboard = viewModel.Document.Artboards[0];
        Layer uk4 = artboard.AddLayer("UK 4");
        Layer uk6 = artboard.AddLayer("UK 6");
        Layer labels = artboard.AddLayer("Labels");

        var context = new AutomationContext { ViewModel = viewModel };
        EditorOperations.Invoke(context, "layer.onlyVisible",
            JsonSerializer.SerializeToElement(new { keep = new[] { "UK 6" }, match = "UK" }));

        Assert.False(uk4.IsVisible);
        Assert.True(uk6.IsVisible);
        Assert.True(labels.IsVisible, "layers outside 'match' must keep their state");
    }

    [AvaloniaFact]
    public void FindTurnsADescriptionIntoAnId()
    {
        var viewModel = new EditorViewModel();
        Layer layer = viewModel.Document.Artboards[0].Layers[0];
        var wanted = new TextItem { Name = "Label" };
        wanted.Runs.Add(new TextRun { Text = "UPPER CUP" });
        layer.AddItem(wanted);
        var other = new TextItem { Name = "Other" };
        other.Runs.Add(new TextRun { Text = "LOWER CUP" });
        layer.AddItem(other);

        var context = new AutomationContext { ViewModel = viewModel };
        object? result = EditorOperations.Invoke(context, "object.find",
            JsonSerializer.SerializeToElement(new { text = "UPPER", type = "text" }));

        string json = JsonSerializer.Serialize(result);
        Assert.Contains(wanted.Id.ToString(), json, StringComparison.Ordinal);
        Assert.DoesNotContain(other.Id.ToString(), json, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void ObjectListIsPagedSoLargeDocumentsDoNotFloodTheModel()
    {
        var viewModel = new EditorViewModel();
        Layer layer = viewModel.Document.Artboards[0].Layers[0];
        for (int i = 0; i < 5; i++)
        {
            layer.AddItem(PathFactory.CreateRectangle($"R{i}", new Rect2D(i * 10, 0, 5, 5)));
        }

        var context = new AutomationContext { ViewModel = viewModel };
        object? result = EditorOperations.Invoke(context, "object.list",
            JsonSerializer.SerializeToElement(new { max = 2 }));

        string json = JsonSerializer.Serialize(result);
        Assert.Contains("\"count\":5", json, StringComparison.Ordinal);
        Assert.Contains("\"returned\":2", json, StringComparison.Ordinal);
        Assert.Contains("\"truncated\":true", json, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryOperationIsSelfDescribing()
    {
        IReadOnlyList<EditorOperation> operations = EditorOperations.All;

        // The registry is the product surface; a collapse means capabilities were
        // lost or moved back into UI-only event handlers.
        Assert.True(operations.Count >= 45, $"only {operations.Count} operations registered");

        foreach (EditorOperation op in operations)
        {
            Assert.False(string.IsNullOrWhiteSpace(op.Name), "an operation has no name");
            Assert.False(string.IsNullOrWhiteSpace(op.Summary), $"{op.Name} has no summary");
            Assert.Contains('.', op.Name);

            // Exactly one execution form: sync handlers run on the UI thread and
            // must stay fast; anything slow is async.
            Assert.True(
                (op.Handler is null) ^ (op.AsyncHandler is null),
                $"{op.Name} must have exactly one of Handler/AsyncHandler");
        }

        Assert.Equal(operations.Count, operations.Select(o => o.Name).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void TheSurfaceCoversTheThingsAPersonDoes()
    {
        string[] required =
        {
            "object.create", "object.list", "object.delete", "object.move", "object.transform",
            "object.group", "object.ungroup", "object.arrange",
            "selection.set", "selection.clear", "selection.selectAll",
            "style.setFill", "style.setStroke", "style.clearFill", "style.clearStroke",
            "text.create", "text.update",
            "layer.add", "layer.list", "layer.rename", "layer.delete",
            "artboard.add", "artboard.list", "artboard.setBounds", "artboard.delete",
            "path.close", "path.join", "path.insertNode", "path.moveNode",
            "document.new", "document.summary", "document.exportPdf", "document.undo", "document.redo",
            "capture.screenshot", "ui.dump", "ui.describe",
        };

        var names = EditorOperations.All.Select(o => o.Name).ToHashSet(StringComparer.Ordinal);
        var missing = required.Where(r => !names.Contains(r)).ToArray();

        Assert.True(missing.Length == 0,
            "operations a person can perform but the assistant cannot: " + string.Join(", ", missing));
    }

    [Fact]
    public void TheCatalogIsUsableAsAModelToolDescription()
    {
        string catalog = EditorOperations.Catalog();

        Assert.Contains("object.create", catalog, StringComparison.Ordinal);
        Assert.Contains("ui.describe", catalog, StringComparison.Ordinal);

        // Every operation must appear, or the model cannot call it.
        foreach (EditorOperation op in EditorOperations.All)
        {
            Assert.Contains(op.Name, catalog, StringComparison.Ordinal);
        }
    }

    [AvaloniaFact]
    public void UnknownOperationFailsWithAGuidanceMessage()
    {
        var context = new AutomationContext { ViewModel = new EditorViewModel() };

        EditorOperationException error = Assert.Throws<EditorOperationException>(
            () => EditorOperations.Invoke(context, "nope.notReal", default));

        // The message must tell the model how to recover, not just fail.
        Assert.Contains("app.operations", error.Message, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void OperationsAreRecordedInTheAuditTrail()
    {
        DiagnosticsLog.Clear();
        var context = new AutomationContext { ViewModel = new EditorViewModel() };

        long before = DiagnosticsLog.Sequence;
        EditorOperations.Invoke(context, "document.summary", default);

        IReadOnlyList<ApiCallRecord> since = DiagnosticsLog.Since(before);
        ApiCallRecord record = Assert.Single(since);
        Assert.Equal("document.summary", record.Operation);
        Assert.True(record.Success);
        Assert.Equal(ApiCallSource.Api, record.Source);
    }

    [AvaloniaFact]
    public void FailedOperationsAreRecordedWithTheirError()
    {
        DiagnosticsLog.Clear();
        var context = new AutomationContext { ViewModel = new EditorViewModel() };

        Assert.Throws<EditorOperationException>(
            () => EditorOperations.Invoke(context, "object.setVisible",
                JsonSerializer.SerializeToElement(new { itemId = Guid.NewGuid(), visible = true })));

        ApiCallRecord record = Assert.Single(DiagnosticsLog.Recent(5));
        Assert.Equal("object.setVisible", record.Operation);
        Assert.False(record.Success);
        Assert.False(string.IsNullOrWhiteSpace(record.Error));
    }

    // ------------------------------------------------------------------
    // Parameter readers: a model will send aliases, strings and omissions.
    // ------------------------------------------------------------------

    [Fact]
    public void ColourParametersAcceptRgbAndCmyk()
    {
        JsonElement rgb = JsonSerializer.Deserialize<JsonElement>("""{"fillColor":[255,128,0]}""");
        Assert.True(rgb.TryGetColorArray("fillColor", out ColorRgb fromRgb));
        // ColorRgb components are 0..1 doubles; the wire format is 0..255 bytes.
        Assert.Equal(1.0, fromRgb.R, 3);
        Assert.Equal(128 / 255.0, fromRgb.G, 3);
        Assert.Equal(0.0, fromRgb.B, 3);

        JsonElement cmyk = JsonSerializer.Deserialize<JsonElement>("""{"color":[0,1,1,0]}""");
        Assert.True(cmyk.TryGetColorArray("color", out ColorRgb fromCmyk));
        Assert.Equal(1.0, fromCmyk.R, 3);
        Assert.Equal(0.0, fromCmyk.G, 3);
        Assert.Equal(0.0, fromCmyk.B, 3);
    }

    [Fact]
    public void MissingParametersFallBackInsteadOfThrowing()
    {
        JsonElement empty = JsonSerializer.Deserialize<JsonElement>("{}");
        Assert.Equal(0.0, empty.GetDouble("x"));
        Assert.Equal(7.0, empty.GetDouble("x", 7));
        Assert.Null(empty.GetString("name"));
        Assert.False(empty.GetBool("visible"));
        Assert.True(empty.GetBool("visible", true));
        Assert.False(empty.TryGetGuid("itemId", out _));
    }

    [Fact]
    public void GuidArraysAreReadFromJsonStrings()
    {
        Guid a = Guid.NewGuid();
        Guid b = Guid.NewGuid();
        JsonElement p = JsonSerializer.Deserialize<JsonElement>(
            $$"""{"itemIds":["{{a}}","{{b}}","not-a-guid"]}""");

        Assert.True(p.TryGetGuidArray("itemIds", out Guid[] ids));
        Assert.Equal(new[] { a, b }, ids);
    }

    [Fact]
    public void UndefinedParametersBehaveLikeAnEmptyObject()
    {
        // A call with no arguments at all must not throw.
        JsonElement none = default;
        Assert.Equal(0.0, none.GetDouble("x"));
        Assert.Null(none.GetString("x"));
    }

    // ------------------------------------------------------------------
    // No-vision dumps
    // ------------------------------------------------------------------

    [Fact]
    public void DocumentDumpCanFocusOnASingleNamedLayer()
    {
        CadDocument document = CadDocument.CreateDefault("dressing");
        Artboard artboard = document.Artboards[0];
        Layer uk = artboard.AddLayer("UK 14");
        Layer other = artboard.AddLayer("Other");

        var inUk = new TextItem { Name = "Bodice", Origin = new Point2D(10, 20) };
        inUk.Runs.Add(new TextRun { Text = "size 14 bodice", FontSize = 12 });
        uk.AddItem(inUk);

        var inOther = new PathItem { Name = "Seam" };
        other.AddItem(inOther);

        string dump = VisualTreeDump.Document(document, "UK 14");

        Assert.Contains("UK 14", dump, StringComparison.Ordinal);
        Assert.Contains("Bodice", dump, StringComparison.Ordinal);
        Assert.Contains("size 14 bodice", dump, StringComparison.Ordinal);

        // The point of the filter: nothing from the other layer leaks in.
        Assert.DoesNotContain("Seam", dump, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownLayerIsReportedWithTheLayersThatDoExist()
    {
        CadDocument document = CadDocument.CreateDefault("dressing");
        document.Artboards[0].AddLayer("UK 14");

        string dump = VisualTreeDump.Document(document, "UK 16");

        Assert.Contains("no layer named", dump, StringComparison.Ordinal);
        Assert.Contains("UK 14", dump, StringComparison.Ordinal);
    }

    [Fact]
    public void DocumentDumpListsObjectsWithIdsAndGeometry()
    {
        CadDocument document = CadDocument.CreateDefault("d");
        Layer layer = document.Artboards[0].Layers[0];
        PathItem rect = PathFactory.CreateRectangle("Box", new Rect2D(10, 20, 30, 40));
        layer.AddItem(rect);

        string dump = VisualTreeDump.Document(document);

        Assert.Contains("DOCUMENT DUMP", dump, StringComparison.Ordinal);
        Assert.Contains("Box", dump, StringComparison.Ordinal);
        Assert.Contains(rect.Id.ToString(), dump, StringComparison.Ordinal);
        Assert.Contains("ARTBOARD", dump, StringComparison.Ordinal);
        Assert.Contains("LAYER", dump, StringComparison.Ordinal);
    }
}
