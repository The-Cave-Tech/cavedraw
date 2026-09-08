using System.ComponentModel;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using VCCad.Core.Commands;
using VCCad.Core.Model;
using VCCad.Core.Serialization;
using VCCad.Geometry;

namespace VCCad.App.ViewModels;

/// <summary>The active editing tool (Illustrator letters: V/A/P).</summary>
public enum EditorTool
{
    /// <summary>Selection &amp; move (V).</summary>
    Select,

    /// <summary>Direct selection: drag nodes and Bézier handles (A).</summary>
    Node,

    /// <summary>Pen: click to add anchors, drag for smooth handles, click the start
    /// anchor to close (P).</summary>
    Pen,
}

/// <summary>
/// Editor view-model: owns the live <see cref="CadDocument"/>, its command stack,
/// the current <see cref="Selection"/> and the active <see cref="Tool"/>.
///
/// Everything a tool or the toolbar does funnels through undoable commands (the
/// same commands the automation API executes — ADR-04). Pointer gestures that
/// need live feedback mutate geometry during the drag and are committed on
/// release via <see cref="GeometryReplaceCommand"/>.
/// </summary>
public sealed class EditorViewModel : INotifyPropertyChanged
{
    private static readonly HttpClient Http = new();

    private CadDocument _document = CadDocument.CreateDefault("Untitled");
    private readonly CommandStack _stack = new();
    private LayerItem? _selection;
    private EditorTool _tool = EditorTool.Select;
    private string _status = "Ready";

    /// <summary>Base URL of the automation host. The browser bootstrap sets this
    /// from window.location; the desktop default targets a local host.</summary>
    public static string ServerBase { get; set; } = "http://127.0.0.1:5099";

    /// <summary>Raised after any change that must trigger a workspace repaint or
    /// a tree refresh (document edits, selection, tool switch).</summary>
    public event EventHandler? DocumentChanged;

    /// <summary>The live document rendered by the workspace.</summary>
    public CadDocument Document
    {
        get => _document;
        private set
        {
            _document = value;
            DocumentChanged?.Invoke(this, EventArgs.Empty);
            OnPropertyChanged();
        }
    }

    /// <summary>Currently selected layer item, or null.</summary>
    public LayerItem? Selection
    {
        get => _selection;
        set
        {
            if (ReferenceEquals(_selection, value))
            {
                return;
            }

            _selection = value;
            OnPropertyChanged();
            DocumentChanged?.Invoke(this, EventArgs.Empty); // redraw highlight + tree
        }
    }

    /// <summary>The active tool.</summary>
    public EditorTool Tool
    {
        get => _tool;
        set
        {
            if (_tool == value)
            {
                return;
            }

            _tool = value;
            OnPropertyChanged();
            DocumentChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Status-bar line (messages, hints, results). Setter is internal so
    /// the view code can push tool hints without exposing general mutation.</summary>
    public string Status
    {
        get => _status;
        internal set
        {
            if (SetField(ref _status, value))
            {
                OnPropertyChanged();
            }
        }
    }

    /// <summary>Creates a fresh A4-landscape document (File → New).</summary>
    public void NewDocument(string? name = null)
    {
        Document = CadDocument.CreateDefault(name);
        _stack.Clear();
        Status = "New document created (A4 landscape)";
    }

    /// <summary>Replaces the whole document (File → Open from server).</summary>
    public void ReplaceDocument(CadDocument document)
    {
        Document = document;
        _stack.Clear();
        Selection = null;
        Status = $"Opened {document.Name}";
    }

    public void Undo()
    {
        if (_stack.Undo())
        {
            Status = $"Undid: {_stack.UndoDescription ?? "action"}";
            NotifyCanvas();
        }
        else
        {
            Status = "Nothing to undo";
        }
    }

    public void Redo()
    {
        if (_stack.Redo())
        {
            Status = "Redone";
            NotifyCanvas();
        }
        else
        {
            Status = "Nothing to redo";
        }
    }

    /// <summary>Executes a command (used by tools and menus) and repaints.</summary>
    public void Execute(IUndoableCommand command)
    {
        _stack.Execute(command);
        NotifyCanvas();
    }

    /// <summary>Deletes the selection (Edit → Delete).</summary>
    public void DeleteSelection()
    {
        if (Selection is not { } item || item.Container is null)
        {
            return;
        }

        Execute(new RemoveItemCommand(item));
        Selection = null;
        Status = $"Deleted {item.Name}";
    }

    /// <summary>Exports the current document to PDF bytes (UI demo path).</summary>
    public byte[] ExportPdf()
    {
        byte[] pdf = VCCad.Pdf.PdfDocumentExporter.Export(Document);
        Status = $"Exported {pdf.Length:N0} bytes of PDF (lossless sidecar embedded)";
        return pdf;
    }

    // ------------------------------------------------------------------
    // Shape creation (Object menu / toolbar)
    // ------------------------------------------------------------------

    public void AddRectangle(double x, double y, double width, double height)
    {
        PathItem rect = PathFactory.CreateRectangle("Rectangle", new Rect2D(x, y, width, height));
        rect.Fill = FillSpec.Solid(ColorRgb.FromBytes(220, 60, 60));
        AddItem(rect);
        Selection = rect;
    }

    public void AddEllipse(double cx, double cy, double rx, double ry)
    {
        PathItem ellipse = PathFactory.CreateEllipse("Ellipse", new Point2D(cx, cy), rx, ry);
        ellipse.Fill = FillSpec.Solid(ColorRgb.FromBytes(60, 140, 220));
        AddItem(ellipse);
        Selection = ellipse;
    }

    public void AddLine(double x1, double y1, double x2, double y2)
    {
        PathItem line = PathFactory.CreateLine("Line", new Point2D(x1, y1), new Point2D(x2, y2));
        line.Stroke = StrokeSpec.Hairline(ColorRgb.Black);
        AddItem(line);
        Selection = line;
    }

    private void AddItem(LayerItem item)
    {
        Artboard artboard = Document.Artboards.Count > 0
            ? Document.Artboards[0]
            : Document.AddArtboard(PageSizes.A4Landscape);
        Layer layer = artboard.Layers.Count > 0
            ? artboard.Layers[0]
            : artboard.AddLayer("Layer 1");

        Execute(new AddItemCommand(layer, item));
        Status = $"Added {item.Name}";
    }

    /// <summary>The default target layer for tool-created items (the top layer of
    /// the first artboard; creates one when the document is empty).</summary>
    public Layer TargetLayer()
    {
        if (Document.Artboards.Count == 0)
        {
            Document.AddArtboard(PageSizes.A4Landscape, "Artboard 1");
        }

        Artboard artboard = Document.Artboards[0];
        if (artboard.Layers.Count == 0)
        {
            artboard.AddLayer("Layer 1");
        }

        return artboard.Layers[^1];
    }

    // ------------------------------------------------------------------
    // Server-backed Save / Open
    // ------------------------------------------------------------------

    /// <summary>Pushes the current document to the host (PUT upsert).</summary>
    public async Task<bool> SaveToServerAsync()
    {
        try
        {
            string payload = VccadDocumentSerializer.Serialize(Document);
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await Http.PutAsync($"{ServerBase.TrimEnd('/')}/api/v1/documents/{Document.Id}", content);
            if (!response.IsSuccessStatusCode)
            {
                Status = $"Save failed: HTTP {(int)response.StatusCode}";
                return false;
            }

            Status = $"Saved {Document.Name} to server";
            return true;
        }
        catch (Exception ex)
        {
            Status = $"Save error: {ex.Message}";
            return false;
        }
    }

    /// <summary>Loads a stored document: the copy of the current id when present,
    /// otherwise the most recently created document on the host.</summary>
    public async Task<bool> LoadFromServerAsync()
    {
        try
        {
            using HttpResponseMessage listResponse = await Http.GetAsync($"{ServerBase.TrimEnd('/')}/api/v1/documents");
            if (!listResponse.IsSuccessStatusCode)
            {
                Status = $"Open failed: HTTP {(int)listResponse.StatusCode}";
                return false;
            }

            JsonElement[] documents;
            using (JsonDocument list = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync()))
            {
                documents = list.RootElement.EnumerateArray().ToArray();
            }

            if (documents.Length == 0)
            {
                Status = "Nothing saved on the server yet";
                return false;
            }

            // Prefer the copy of our own document, then the first stored one.
            string? target = null;
            foreach (JsonElement item in documents)
            {
                if (item.GetProperty("id").GetString() == Document.Id.ToString())
                {
                    target = item.GetProperty("id").GetString();
                    break;
                }
            }

            target ??= documents[^1].GetProperty("id").GetString();

            using HttpResponseMessage get = await Http.GetAsync($"{ServerBase.TrimEnd('/')}/api/v1/documents/{target}");
            if (!get.IsSuccessStatusCode)
            {
                Status = $"Open failed: HTTP {(int)get.StatusCode}";
                return false;
            }

            string payload = await get.Content.ReadAsStringAsync();
            ReplaceDocument(VccadDocumentSerializer.Deserialize(payload));
            return true;
        }
        catch (Exception ex)
        {
            Status = $"Open error: {ex.Message}";
            return false;
        }
    }

    private void NotifyCanvas()
    {
        DocumentChanged?.Invoke(this, EventArgs.Empty);
        OnPropertyChanged(nameof(Document));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
