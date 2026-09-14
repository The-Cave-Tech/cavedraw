using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using VCCad.Core.Commands;
using VCCad.Core.Model;
using VCCad.Core.Serialization;
using VCCad.Geometry;

namespace VCCad.App.ViewModels;

/// <summary>
/// MDI workspace view-model: owns one <see cref="DocumentSession"/> per open
/// document (each with its own command/undo stack) and forwards every operation
/// to the active session. The canvas shows the active document; the tab strip
/// switches sessions.
/// </summary>
public sealed class EditorViewModel : INotifyPropertyChanged
{
    private static readonly HttpClient Http = new();

    private DocumentSession _active;
    private EditorTool _tool = EditorTool.Select;
    private string _status = "Ready";

    public EditorViewModel()
    {
        _active = CreateSession(CadDocument.CreateDefault("Untitled"));
        Sessions.Add(_active);
    }

    /// <summary>Base URL of the automation host (set by the browser bootstrap).</summary>
    public static string ServerBase { get; set; } = "http://127.0.0.1:5099";

    /// <summary>When true, releasing a moved point near a neighbour's H/V line or
    /// another anchor snaps it (UI toggle).</summary>
    public bool OrthogonalSnapEnabled { get; set; } = true;

    /// <summary>Open documents (one tab each).</summary>
    public ObservableCollection<DocumentSession> Sessions { get; } = new();

    public DocumentSession ActiveSession
    {
        get => _active;
        set
        {
            if (ReferenceEquals(_active, value) || !Sessions.Contains(value))
            {
                return;
            }

            _active = value;
            OnPropertyChanged();
            RaiseDocumentChanged();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
            TransformChanged?.Invoke(this, EventArgs.Empty);
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
            RaiseDocumentChanged();
        }
    }

    public string Status
    {
        get => _status;
        internal set
        {
            if (_status != value)
            {
                _status = value;
                OnPropertyChanged();
            }
        }
    }

    public CadDocument Document => _active.Document;

    // ------------------------------------------------------------------
    // Events (forwarded from the active session)
    // ------------------------------------------------------------------

    public event EventHandler? DocumentChanged;
    public event EventHandler? SelectionChanged;
    public event EventHandler? TransformChanged;
    public event EventHandler<ArtboardDeletionRequest>? ArtboardDeletionRequested;
    public event PropertyChangedEventHandler? PropertyChanged;

    internal void RaiseTransformChanged() => TransformChanged?.Invoke(this, EventArgs.Empty);

    private void RaiseDocumentChanged() => DocumentChanged?.Invoke(this, EventArgs.Empty);

    // ------------------------------------------------------------------
    // Session management
    // ------------------------------------------------------------------

    private DocumentSession CreateSession(CadDocument document)
    {
        var session = new DocumentSession { StatusSink = message => Status = message };
        session.Initialize(document);

        session.DocumentChanged += (s, _) => { if (ReferenceEquals(s, _active)) RaiseDocumentChanged(); };
        session.SelectionChanged += (s, _) => { if (ReferenceEquals(s, _active)) SelectionChanged?.Invoke(this, EventArgs.Empty); };
        session.TransformChanged += (s, _) => { if (ReferenceEquals(s, _active)) TransformChanged?.Invoke(this, EventArgs.Empty); };
        session.ArtboardDeletionRequested += (s, e) => { if (ReferenceEquals(s, _active)) ArtboardDeletionRequested?.Invoke(this, e); };
        return session;
    }

    /// <summary>Opens a new document tab.</summary>
    public DocumentSession AddDocument(CadDocument document)
    {
        DocumentSession session = CreateSession(document);
        Sessions.Add(session);
        ActiveSession = session;
        Status = $"Opened {document.Name}";
        return session;
    }

    public void NewDocument(string? name = null) => AddDocument(CadDocument.CreateDefault(name));

    public void CloseSession(DocumentSession session)
    {
        if (Sessions.Count <= 1)
        {
            return; // keep at least one document open
        }

        int index = Sessions.IndexOf(session);
        Sessions.Remove(session);
        if (ReferenceEquals(_active, session))
        {
            ActiveSession = Sessions[Math.Clamp(index, 0, Sessions.Count - 1)];
        }
    }

    // ------------------------------------------------------------------
    // Server-backed Save / Open (operate on the active session)
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

            string target = documents[^1].GetProperty("id").GetString()!;
            using HttpResponseMessage get = await Http.GetAsync($"{ServerBase.TrimEnd('/')}/api/v1/documents/{target}");
            if (!get.IsSuccessStatusCode)
            {
                Status = $"Open failed: HTTP {(int)get.StatusCode}";
                return false;
            }

            AddDocument(VccadDocumentSerializer.Deserialize(await get.Content.ReadAsStringAsync()));
            return true;
        }
        catch (Exception ex)
        {
            Status = $"Open error: {ex.Message}";
            return false;
        }
    }

    public byte[] ExportPdf()
    {
        byte[] pdf = VCCad.Pdf.PdfDocumentExporter.Export(Document);
        Status = $"Exported {pdf.Length:N0} bytes of PDF";
        return pdf;
    }

    // ------------------------------------------------------------------
    // Delegation to the active session
    // ------------------------------------------------------------------

    public IReadOnlyList<LayerItem> SelectedObjects => _active.SelectedObjects;
    public LayerItem? PrimarySelection => _active.PrimarySelection;
    public bool HasMultiSelection => _active.HasMultiSelection;
    public Artboard? SelectedArtboard => _active.SelectedArtboard;
    public double SelectionRotationRadians => _active.SelectionRotationRadians;
    public bool HasPointSelection => _active.HasPointSelection;
    public bool HasSegmentSelection => _active.HasSegmentSelection;
    public bool HasTransformableSelection => _active.HasTransformableSelection;
    public Point2D? PointPosition => _active.PointPosition;
    public bool IsEditingText
    {
        get => _active.IsEditingText;
        set => _active.IsEditingText = value;
    }

    public int TextCaretRunIndex
    {
        get => _active.TextCaretRunIndex;
        set => _active.TextCaretRunIndex = value;
    }

    public FillSpec CurrentFill
    {
        get => _active.CurrentFill;
        set => _active.CurrentFill = value;
    }

    public StrokeSpec CurrentStroke
    {
        get => _active.CurrentStroke;
        set => _active.CurrentStroke = value;
    }

    public IEnumerable<PathItem> SelectedPaths() => _active.SelectedPaths();
    public IEnumerable<TextItem> SelectedTextItems() => _active.SelectedTextItems();
    public IEnumerable<(PathItem Path, int Sub, int Seg)> SelectedSegments() => _active.SelectedSegments();
    public Rect2D SelectionBounds() => _active.SelectionBounds();
    public (Rect2D Bounds, double AngleDeg) TransformReadout() => _active.TransformReadout();
    public bool IsObjectSelected(LayerItem item) => _active.IsObjectSelected(item);
    public bool IsSegmentSelected(PathItem path, int sub, int seg) => _active.IsSegmentSelected(path, sub, seg);

    public void SelectObject(LayerItem? item) => _active.SelectObject(item);
    public void ToggleObjectSelection(LayerItem item) => _active.ToggleObjectSelection(item);
    public void SelectRange(IEnumerable<LayerItem> items, bool additive) => _active.SelectRange(items, additive);
    public void SelectSegment(PathItem path, int sub, int seg, bool additive) => _active.SelectSegment(path, sub, seg, additive);
    public void SelectPoint(PathItem path, int sub, int node) => _active.SelectPoint(path, sub, node);
    public void ClearPointSelection() => _active.ClearPointSelection();
    public void ClearSegmentSelection() => _active.ClearSegmentSelection();
    public void InsertPointOnSegment(PathItem path, int subIndex, int segmentIndex, Point2D near)
        => _active.InsertPointOnSegment(path, subIndex, segmentIndex, near);
    public void ClearSelection() => _active.ClearSelection();
    public void MovePointTo(Point2D target) => _active.MovePointTo(target);
    public void SelectArtboard(Artboard? artboard) => _active.SelectArtboard(artboard);
    public void SetSelectionRotationRadians(double radians) => _active.SetSelectionRotationRadians(radians);

    public void Execute(IUndoableCommand command) => _active.Execute(command);
    public void Undo() => _active.Undo();
    public void Redo() => _active.Redo();
    public void DeleteSelection() => _active.DeleteSelection();
    public void RequestDeleteSelection() => _active.RequestDeleteSelection();
    public void DeleteArtboard(Artboard artboard, ArtboardDeletionChoice choice) => _active.DeleteArtboard(artboard, choice);
    public void GroupSelection() => _active.GroupSelection();
    public void UngroupSelection() => _active.UngroupSelection();
    public void CloseSelectedPaths() => _active.CloseSelectedPaths();
    public void JoinSelection() => _active.JoinSelection();

    public void ApplyFill(ColorRgb color, FillRule rule) => _active.ApplyFill(color, rule);
    public void ClearFill() => _active.ClearFill();
    public void ClearStroke() => _active.ClearStroke();
    public void ApplyStrokeColor(ColorRgb color) => _active.ApplyStrokeColor(color);
    public void ApplyStroke(double width, StrokeCap cap, StrokeJoin join, double miter, StrokeAlignment align)
        => _active.ApplyStroke(width, cap, join, miter, align);
    public void ApplyTransform(Point2D pivot, Vector2D translation, double sx, double sy, double rotationDegrees)
        => _active.ApplyTransform(pivot, translation, sx, sy, rotationDegrees);

    public (Layer Layer, Vector2D Offset) TargetFor(Point2D world) => _active.TargetFor(world);
    public void AddNewArtboard() => _active.AddNewArtboard();
    public void AddArtboardFromRect(Rect2D rect) => _active.AddArtboardFromRect(rect);
    public void ApplyArtboardBounds(Artboard artboard, Rect2D before, Rect2D after) => _active.ApplyArtboardBounds(artboard, before, after);
    public void SetArtboardBounds(Artboard artboard, Rect2D before, Rect2D after) => _active.SetArtboardBounds(artboard, before, after);

    public TextItem CreateTextAt(Point2D world, string family, double fontSize) => _active.CreateTextAt(world, family, fontSize);
    public void UpdateSelectedText(string content, string family, double size, bool bold, bool italic,
        ColorRgb color, int? runIndex = null)
        => _active.UpdateSelectedText(content, family, size, bold, italic, color, runIndex);

    private void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
