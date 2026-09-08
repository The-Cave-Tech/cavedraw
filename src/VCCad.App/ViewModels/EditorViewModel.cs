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

/// <summary>The active editing tool.</summary>
public enum EditorTool
{
    /// <summary>Selection &amp; move (V). Shift adds to the selection.</summary>
    Select,

    /// <summary>Direct selection (A): drag nodes and handles, click/Shift-click to
    /// select individual segments.</summary>
    Node,

    /// <summary>Pen: click to add anchors, drag for smooth handles, click the start
    /// anchor to close (P).</summary>
    Pen,

    /// <summary>Drag out a closed rectangle between two corner points.</summary>
    Rectangle,

    /// <summary>Drag out a closed ellipse between two bounding-box corners.</summary>
    Ellipse,
}

/// <summary>
/// Editor view-model: owns the live <see cref="CadDocument"/>, its command stack,
/// the current selection (objects + per-path segments) and the active tool.
/// All edits funnel through undoable commands (same set the automation API uses).
/// </summary>
public sealed class EditorViewModel : INotifyPropertyChanged
{
    private static readonly HttpClient Http = new();

    private CadDocument _document = CadDocument.CreateDefault("Untitled");
    private readonly CommandStack _stack = new();
    private readonly List<LayerItem> _selectedObjects = new();
    private readonly Dictionary<PathItem, List<(int Sub, int Seg)>> _selectedSegments = new();
    private EditorTool _tool = EditorTool.Select;
    private string _status = "Ready";

    /// <summary>Base URL of the automation host. The browser bootstrap sets this
    /// from window.location; the desktop default targets a local host.</summary>
    public static string ServerBase { get; set; } = "http://127.0.0.1:5099";

    /// <summary>Raised after any change that must trigger a workspace repaint or a
    /// tree refresh (document edits, selection changes, tool switches).</summary>
    public event EventHandler? DocumentChanged;

    public CadDocument Document
    {
        get => _document;
        private set
        {
            _document = value;
            _selectedObjects.Clear();
            _selectedSegments.Clear();
            DocumentChanged?.Invoke(this, EventArgs.Empty);
            OnPropertyChanged();
        }
    }

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

    // ------------------------------------------------------------------
    // Selection model
    // ------------------------------------------------------------------

    /// <summary>Selected objects (paths and groups), in picking order.</summary>
    public IReadOnlyList<LayerItem> SelectedObjects => _selectedObjects;

    /// <summary>The last selected object (used for Properties and rotation centre).</summary>
    public LayerItem? PrimarySelection => _selectedObjects.Count > 0 ? _selectedObjects[^1] : null;

    /// <summary>True when more than one object is selected.</summary>
    public bool HasMultiSelection => _selectedObjects.Count > 1;

    public bool IsObjectSelected(LayerItem item) => _selectedObjects.Contains(item);

    /// <summary>All selected path segments as (path, subpath index, segment index).</summary>
    public IEnumerable<(PathItem Path, int Sub, int Seg)> SelectedSegments()
    {
        foreach (KeyValuePair<PathItem, List<(int Sub, int Seg)>> entry in _selectedSegments)
        {
            foreach ((int sub, int seg) in entry.Value)
            {
                yield return (entry.Key, sub, seg);
            }
        }
    }

    public bool HasSegmentSelection => _selectedSegments.Count > 0;

    /// <summary>Whether a given segment of a path is in the segment selection.</summary>
    public bool IsSegmentSelected(PathItem path, int sub, int seg)
        => _selectedSegments.TryGetValue(path, out List<(int Sub, int Seg)>? list) && list.Contains((sub, seg));

    /// <summary>Replaces the whole selection with a single object (or clears it).</summary>
    public void SelectObject(LayerItem? item)
    {
        _selectedObjects.Clear();
        _selectedSegments.Clear();
        if (item is not null)
        {
            _selectedObjects.Add(item);
        }

        NotifySelectionChanged();
    }

    /// <summary>Shift-click object selection: toggle membership, keep everything else.</summary>
    public void ToggleObjectSelection(LayerItem item)
    {
        if (_selectedObjects.Remove(item))
        {
            _selectedSegments.Remove(item as PathItem);
        }
        else
        {
            _selectedObjects.Add(item);
        }

        NotifySelectionChanged();
    }

    /// <summary>
    /// Adds a set of objects to the selection (after an object drag on a shared
    /// element) without disturbing segment selections.
    /// </summary>
    public void AddObjects(IEnumerable<LayerItem> items)
    {
        bool changed = false;
        foreach (LayerItem item in items)
        {
            if (!_selectedObjects.Contains(item))
            {
                _selectedObjects.Add(item);
                changed = true;
            }
        }

        if (changed)
        {
            NotifySelectionChanged();
        }
    }

    /// <summary>
    /// Selects a segment of a path for direct selection. Without
    /// <paramref name="additive"/> the whole selection is replaced by (this path,
    /// this segment); additive (Shift) toggles the segment and keeps other
    /// objects/segments selected.
    /// </summary>
    public void SelectSegment(PathItem path, int sub, int seg, bool additive)
    {
        if (!additive)
        {
            _selectedObjects.Clear();
            _selectedSegments.Clear();
            _selectedObjects.Add(path);
            _selectedSegments[path] = new List<(int, int)> { (sub, seg) };
        }
        else
        {
            if (!_selectedObjects.Contains(path))
            {
                _selectedObjects.Add(path);
            }

            if (!_selectedSegments.TryGetValue(path, out List<(int Sub, int Seg)>? list))
            {
                _selectedSegments[path] = list = new List<(int, int)>();
            }

            (int, int) key = (sub, seg);
            if (list.Contains(key))
            {
                list.Remove(key);
                if (list.Count == 0)
                {
                    _selectedSegments.Remove(path);
                }
            }
            else
            {
                list.Add(key);
            }
        }

        NotifySelectionChanged();
    }

    /// <summary>Clears object and segment selections.</summary>
    public void ClearSelection()
    {
        if (_selectedObjects.Count == 0 && _selectedSegments.Count == 0)
        {
            return;
        }

        _selectedObjects.Clear();
        _selectedSegments.Clear();
        NotifySelectionChanged();
    }

    /// <summary>Removes segment selections whose path object is no longer selected.</summary>
    private void PruneSegmentSelection()
    {
        foreach (PathItem path in _selectedSegments.Keys.Where(p => !_selectedObjects.Contains(p)).ToArray())
        {
            _selectedSegments.Remove(path);
        }
    }

    /// <summary>Selected paths whose geometry can be edited (identity hierarchies).</summary>
    public IEnumerable<PathItem> SelectedPaths() => _selectedObjects.OfType<PathItem>();

    /// <summary>The combined model-space bounds of the selected objects.</summary>
    public Rect2D SelectionBounds()
    {
        Rect2D box = Rect2D.Empty;
        foreach (PathItem path in SelectedPaths())
        {
            box = box.Union(path.BoundingBox());
        }

        return box;
    }

    // ------------------------------------------------------------------
    // Commands / undo / actions
    // ------------------------------------------------------------------

    public void Execute(IUndoableCommand command)
    {
        _stack.Execute(command);
        NotifyCanvas();
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

    /// <summary>Deletes the selected objects (single composite undo step).</summary>
    public void DeleteSelection()
    {
        if (_selectedObjects.Count == 0)
        {
            return;
        }

        var remove = new List<IUndoableCommand>();
        foreach (LayerItem item in _selectedObjects.Where(i => i.Container is not null))
        {
            remove.Add(new RemoveItemCommand(item));
        }

        if (remove.Count == 0)
        {
            return;
        }

        string label = remove.Count == 1 ? "Delete object" : $"Delete {remove.Count} objects";
        Execute(new CompositeCommand(label, remove));
        _selectedObjects.Clear();
        _selectedSegments.Clear();
        NotifySelectionChanged();
        Status = label;
    }

    public void NewDocument(string? name = null)
    {
        Document = CadDocument.CreateDefault(name);
        _stack.Clear();
        Status = "New document created (A4 landscape)";
    }

    public void ReplaceDocument(CadDocument document)
    {
        Document = document;
        _stack.Clear();
        Status = $"Opened {document.Name}";
    }

    public byte[] ExportPdf()
    {
        byte[] pdf = VCCad.Pdf.PdfDocumentExporter.Export(Document);
        Status = $"Exported {pdf.Length:N0} bytes of PDF (lossless sidecar embedded)";
        return pdf;
    }

    /// <summary>The default target layer for tool-created items.</summary>
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

    public async Task<bool> SaveToServerAsync()
    {
        try
        {
            string payload = VccadDocumentSerializer.Serialize(Document);
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await Http.PutAsync(
                $"{ServerBase.TrimEnd('/')}/api/v1/documents/{Document.Id}", content);
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

    // ------------------------------------------------------------------
    // Notifications
    // ------------------------------------------------------------------

    private void NotifySelectionChanged()
    {
        PruneSegmentSelection();
        OnPropertyChanged(nameof(SelectedObjects));
        OnPropertyChanged(nameof(PrimarySelection));
        OnPropertyChanged(nameof(HasMultiSelection));
        DocumentChanged?.Invoke(this, EventArgs.Empty);
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
