using Avalonia.Controls;
using Avalonia.Interactivity;
using VCCad.App.ViewModels;
using VCCad.Core.Model;

namespace VCCad.App.Views;

/// <summary>
/// Code-behind for the editor chrome. All business behaviour lives in the
/// <see cref="EditorViewModel"/>; this class only shuttles between XAML controls
/// and that view model (the workspace canvas holds its own view state such as
/// zoom/pan). Menus and toolbar buttons are wired here for clarity — the long
/// term plan (M5) replaces event handlers with bindings where valuable.
/// </summary>
public partial class EditorView : UserControl
{
    private readonly EditorViewModel _viewModel = new();

    public EditorView()
    {
        InitializeComponent();

        DataContext = _viewModel;
        StatusText.Text = _viewModel.Status;
        Workspace.Document = _viewModel.Document;
        _viewModel.DocumentChanged += OnDocumentChanged;

        RefreshLayers();
        UpdateZoomLabel();
    }

    private void OnDocumentChanged(object? sender, EventArgs e)
    {
        Workspace.Document = _viewModel.Document;
        Workspace.RefreshFromDocument();
        RefreshLayers();
        UpdateZoomLabel();
    }

    private void RefreshLayers()
    {
        LayersList.ItemsSource = _viewModel.Document.Artboards
            .SelectMany(a => a.Layers.Select(layer => $"{a.Name} ▸ {layer.Name} ({layer.Children.Count} items)"))
            .ToList();
    }

    private void UpdateZoomLabel()
    {
        ZoomLabel.Text = $"{Workspace.Zoom * 100:0.##}%";
        StatusText.Text = _viewModel.Status;
    }

    private void OnNew(object? sender, RoutedEventArgs e)
    {
        _viewModel.NewDocument();
        StatusText.Text = _viewModel.Status;
        RefreshLayers();
    }

    private void OnUndo(object? sender, RoutedEventArgs e)
    {
        _viewModel.Undo();
        StatusText.Text = _viewModel.Status;
    }

    private void OnRedo(object? sender, RoutedEventArgs e)
    {
        _viewModel.Redo();
        StatusText.Text = _viewModel.Status;
    }

    private void OnAddRectangle(object? sender, RoutedEventArgs e)
    {
        _viewModel.AddRectangle(60, 60, 260, 180);
        StatusText.Text = _viewModel.Status;
        RefreshLayers();
    }

    private void OnAddEllipse(object? sender, RoutedEventArgs e)
    {
        _viewModel.AddEllipse(420, 150, 140, 90);
        StatusText.Text = _viewModel.Status;
        RefreshLayers();
    }

    private void OnAddLine(object? sender, RoutedEventArgs e)
    {
        _viewModel.AddLine(60, 320, 700, 420);
        StatusText.Text = _viewModel.Status;
        RefreshLayers();
    }

    private void OnZoomIn(object? sender, RoutedEventArgs e)
    {
        Workspace.ZoomIn();
        UpdateZoomLabel();
    }

    private void OnZoomOut(object? sender, RoutedEventArgs e)
    {
        Workspace.ZoomOut();
        UpdateZoomLabel();
    }

    private void OnActualSize(object? sender, RoutedEventArgs e)
    {
        Workspace.ZoomToActualSize();
        UpdateZoomLabel();
    }

    private void OnFitInWindow(object? sender, RoutedEventArgs e)
    {
        Workspace.ZoomToFit();
        UpdateZoomLabel();
    }

    private void OnExportPdf(object? sender, RoutedEventArgs e)
    {
        byte[] pdf = _viewModel.ExportPdf();
        StatusText.Text = _viewModel.Status;
        // A download follows via JS interop in the browser host; the file bytes
        // are produced here so a desktop build can use a SaveFileDialog instead.
        SelectionInfo.Text = $"PDF payload ready: {pdf.Length:N0} bytes";
    }

    private void OnExit(object? sender, RoutedEventArgs e)
    {
        // In the browser this is a no-op (no window manager to close); the item
        // exists to keep the Windows-style menu shape.
        StatusText.Text = "Exit is handled by the browser tab itself.";
    }
}
