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

            // Every switch remembers where it came from, so a toggle key can go back. Stepping
            // aside to the node tool to nudge a node and returning to the pen is one keystroke
            // rather than a trip to the toolbar.
            PreviousTool = _tool;
            _tool = value;
            OnPropertyChanged();
            RaiseDocumentChanged();
        }
    }

    /// <summary>
    /// The tool that was active before this one, for <see cref="ToggleTool"/>.
    ///
    /// Starts as Select so the first toggle has somewhere to return to.
    /// </summary>
    public EditorTool PreviousTool { get; private set; } = EditorTool.Select;

    /// <summary>
    /// Switch to <paramref name="tool"/>, or back to the tool before it when it is already active.
    ///
    /// This is the keyboard toggle: from the pen, A gives the node tool, and A again gives the pen
    /// back - the middle of a drawing is no place to be sent to the toolbar. Coming back is an
    /// ordinary switch, so the tool gone back to becomes the one a third press returns from.
    /// </summary>
    public void ToggleTool(EditorTool tool) => Tool = Tool == tool ? PreviousTool : tool;
    /// <summary>
    /// Which of the nine shapes the shape tool draws. Changing it changes what a drag produces, and the
    /// toolbar button draws the shape itself, so the armed shape is visible rather than remembered.
    ///
    /// The notification is not decoration. `tool.set star` sets this property alone, and the compound button
    /// learns what to draw by listening for the change - so as a plain auto-property it kept drawing a
    /// rectangle while a driver had armed a star. A tool that can be set but not shown is a state the person
    /// cannot see.
    /// </summary>
    public ShapeKind CurrentShape
    {
        get => _currentShape;
        set
        {
            if (_currentShape != value)
            {
                _currentShape = value;
                OnPropertyChanged();
            }
        }
    }

    private ShapeKind _currentShape = ShapeKind.Rectangle;

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

    /// <summary>
    /// Announces a document mutation made outside the command bus (by the
    /// automation/chatbot layer) so the canvas, panes and diagnostics refresh.
    /// </summary>
    public void NotifyDocumentChanged() => RaiseDocumentChanged();

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

    /// <summary>Imports a PDF (vector content) as a new document tab.</summary>
    /// <param name="password">The password, for a file that needs one to open at all.</param>
    public DocumentSession ImportPdf(byte[] pdfBytes, string? path = null, string? password = null)
    {
        CadDocument document = VCCad.Pdf.PdfImporter.Import(pdfBytes, password);
        if (!string.IsNullOrWhiteSpace(path))
        {
            document.Name = System.IO.Path.GetFileNameWithoutExtension(path);
        }

        Status = $"Opened {document.Name} ({document.Artboards.Count} page(s))";

        DocumentSession session = AddDocument(document);

        // An opened document matches what is on disk, so nothing needs saving yet.
        session.MarkSaved();
        return session;
    }

    /// <summary>Whether the active document has changes that are not on disk.</summary>
    public bool IsActiveModified => _active.IsModified;

    /// <summary>Closes the active document, keeping at least one open.</summary>
    public void CloseActiveDocument() => CloseSession(_active);

    /// <summary>Saves the active document to the server as a background action.</summary>
    public void SaveActiveToServer()
    {
        _ = SaveToServerAsync();
        _active.MarkSaved();
    }

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
// The command queue travels with the file as our own private data, so the
        // document remembers how it was made as well as what it looks like.
        byte[] pdf = VCCad.Pdf.PdfDocumentExporter.Export(
            Document, VCCad.App.Automation.SessionJournal.ReadQueue());
        Status = $"Exported {pdf.Length:N0} bytes of PDF";

        // Writing the document out is what "saved" means, so nothing is pending afterwards.
        _active.MarkSaved();
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
    /// <summary>
    /// The face new text is created with. Chosen from the font picker when no block is
    /// selected, so picking a font always means something.
    /// </summary>
    public string DefaultFontFamily
    {
        get => _active.DefaultFontFamily;
        set => _active.DefaultFontFamily = value;
    }

    /// <summary>The text block being edited, if any; the target of styling operations.</summary>
    public TextItem? EditingText
    {
        get => _active.EditingText;
        set => _active.EditingText = value;
    }

    public bool IsEditingText
    {
        get => _active.IsEditingText;
        set
        {
            if (_active.IsEditingText == value)
            {
                return;
            }

            _active.IsEditingText = value;
            // The text toolbar shows and hides on this, so it has to announce itself.
            OnPropertyChanged();
        }
    }

    public int TextCaretRunIndex
    {
        get => _active.TextCaretRunIndex;
        set => _active.TextCaretRunIndex = value;
    }

    public int TextSelectionStart
    {
        get => _active.TextSelectionStart;
        set => _active.TextSelectionStart = value;
    }

    public int TextSelectionEnd
    {
        get => _active.TextSelectionEnd;
        set => _active.TextSelectionEnd = value;
    }

    public void SetTextAlignment(TextAlignment alignment) => _active.SetTextAlignment(alignment);

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
    public IReadOnlyList<ColorRgb> UsedColors() => _active.UsedColors();
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
    public void MoveItems(IReadOnlyList<LayerItem> items, IItemContainer target, int index)
        => _active.MoveItems(items, target, index);
    public void UngroupSelection() => _active.UngroupSelection();

    /// <summary>
    /// Mirrors the selection across its own centre; see <see cref="DocumentSession.FlipSelection"/>.
    /// </summary>
    public void FlipSelection(bool horizontal, bool vertical) => _active.FlipSelection(horizontal, vertical);
    public void CloseSelectedPaths() => _active.CloseSelectedPaths();

    // ---- arranging, for the Align panel --------------------------------
    // Delegated to the session, which the operations also call: a person's button and the assistant's
    // operation must be one implementation.

    public int AlignSelection(ArrangeAxis axis, ArrangeEdge edge) => _active.AlignSelection(axis, edge);

    public int DistributeSelection(ArrangeAxis axis, ArrangeAnchor anchor)
        => _active.DistributeSelection(axis, anchor);

    // ---- path booleans, for the Pathfinder panel ------------------------
    // Delegated to the session, which the operations also call: a person's button and the assistant's
    // operation must be one implementation, not two that agree today.

    public PathBooleanResult BooleanSelection(BooleanOp op) => _active.BooleanSelection(op);

    public PathBooleanResult DivideSelection() => _active.DivideSelection();

    public PathBooleanResult MakeCompoundSelection() => _active.MakeCompoundSelection();

    public PathBooleanResult ReleaseCompoundSelection() => _active.ReleaseCompoundSelection();

    public bool ReverseSubpath(int index) => _active.ReverseSubpathOfSelection(index);

    /// <summary>Puts a message in the status bar - how a panel reports something it could not do.</summary>
    public void ReportStatus(string message) => Status = message;

    // ---- the corner tool ------------------------------------------------
    // Delegated to the session, which the operation also calls: the drag and `path.roundCorner` must be
    // one implementation, or a person and the assistant get different shapes from the same request.

    /// <summary>Rounds a corner by dragging: the radius is how far the pointer is from the corner.</summary>
    public void RoundCornerByDrag(PathItem path, int subPath, int node, double radius)
        => _active.RoundCorner(path, subPath, node, radius);

    /// <summary>Draws a freehand stroke from the points the pointer visited, in document space.</summary>
    public PathItem? DrawFreehand(IReadOnlyList<Point2D> points) => _active.DrawFreehand(points);
    /// <summary>The corner of the selection nearest a point, or null when none is within reach.</summary>
    public (PathItem Path, int SubPath, int Node, double Distance)? NearestCorner(Point2D point, double within)
        => _active.NearestCorner(point, within);

    /// <summary>Commits a geometry change that was previewed live, as one undo step.</summary>
    public void CommitGeometry(PathItem path, PathItem before, string description)
        => _active.CommitGeometry(path, before, description);

    public void JoinSelection() => _active.JoinSelection();

    public void ApplyFill(ColorRgb color, FillRule rule) => _active.ApplyFill(color, rule);
    public void ClearFill() => _active.ClearFill();
    public void ClearStroke() => _active.ClearStroke();
    public void ApplyStrokeColor(ColorRgb color) => _active.ApplyStrokeColor(color);
    public void ApplyStroke(double width, StrokeCap cap, StrokeJoin join, double miter, StrokeAlignment align,
        DashPattern? dash = null)
        => _active.ApplyStroke(width, cap, join, miter, align, dash);
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


    /// <summary>Paragraph style and orientation on the selected text, one undo step.</summary>
    public void ApplyTextStyle(double? lineSpacing = null, double? paragraphSpacing = null,
        double? rotationDegrees = null, double? frameWidth = null, TextAlignment? alignment = null)
        => _active.ApplyTextStyle(lineSpacing, paragraphSpacing, rotationDegrees, frameWidth, alignment);


    private void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
