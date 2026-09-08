using System.ComponentModel;
using System.Runtime.CompilerServices;
using VCCad.Core.Commands;
using VCCad.Core.Model;
using VCCad.Geometry;

namespace VCCad.App.ViewModels;

/// <summary>
/// Editor view-model: owns the live <see cref="CadDocument"/> and its command
/// stack. Toolbar/menu actions funnel through undoable commands (the same
/// commands the automation API executes — ADR-04), so whatever the user does in
/// the UI is exactly what a scripted JSON-RPC session would produce.
/// </summary>
public sealed class EditorViewModel : INotifyPropertyChanged
{
    private CadDocument _document = CadDocument.CreateDefault("Untitled");
    private readonly CommandStack _stack = new();
    private string _status = "Ready";

    /// <summary>Raised after any change that must trigger a workspace repaint.</summary>
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

    /// <summary>Status-bar line (messages, hints, results).</summary>
    public string Status
    {
        get => _status;
        private set
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

    public void Undo()
    {
        if (_stack.Undo())
        {
            NotifyCanvas();
            Status = $"Undid: {_stack.UndoDescription ?? "action"}";
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
            NotifyCanvas();
            Status = "Redone";
        }
        else
        {
            Status = "Nothing to redo";
        }
    }

    /// <summary>Exports the current document to PDF bytes (UI demo path).</summary>
    public byte[] ExportPdf()
    {
        byte[] pdf = VCCad.Pdf.PdfDocumentExporter.Export(Document);
        Status = $"Exported {pdf.Length:N0} bytes of PDF (lossless sidecar embedded)";
        return pdf;
    }

    public void AddRectangle(double x, double y, double width, double height)
    {
        PathItem rect = PathFactory.CreateRectangle("Rectangle", new Rect2D(x, y, width, height));
        rect.Fill = FillSpec.Solid(ColorRgb.FromBytes(220, 60, 60));
        AddItem(rect);
    }

    public void AddEllipse(double cx, double cy, double rx, double ry)
    {
        PathItem ellipse = PathFactory.CreateEllipse("Ellipse", new Point2D(cx, cy), rx, ry);
        ellipse.Fill = FillSpec.Solid(ColorRgb.FromBytes(60, 140, 220));
        AddItem(ellipse);
    }

    public void AddLine(double x1, double y1, double x2, double y2)
    {
        PathItem line = PathFactory.CreateLine("Line", new Point2D(x1, y1), new Point2D(x2, y2));
        line.Stroke = StrokeSpec.Hairline(ColorRgb.Black);
        AddItem(line);
    }

    private void AddItem(LayerItem item)
    {
        Artboard artboard = _document.Artboards.Count > 0 ? _document.Artboards[0] : _document.AddArtboard(PageSizes.A4Landscape);
        Layer layer = artboard.Layers.Count > 0 ? artboard.Layers[0] : artboard.AddLayer("Layer 1");

        _stack.Execute(new AddItemCommand(layer, item));
        Status = $"Added {item.Name}";
        NotifyCanvas();
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
