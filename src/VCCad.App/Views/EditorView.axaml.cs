using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Geometry;

namespace VCCad.App.Views;

/// <summary>
/// Code-behind for the editor chrome. The <see cref="EditorViewModel"/> owns all
/// state; this class shuttles between XAML and that view model, builds the
/// hierarchical object tree, and reflects tool changes back onto the buttons.
/// </summary>
public partial class EditorView : UserControl
{
    private readonly EditorViewModel _viewModel = new();

    public EditorView()
    {
        InitializeComponent();

        DataContext = _viewModel;
        _viewModel.DocumentChanged += OnDocumentChanged;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        Workspace.AttachEditor(_viewModel);

        UpdateStatusAndZoom();
        RefreshTree();
        HighlightActiveTool();
    }

    // ------------------------------------------------------------------
    // View-model plumbing
    // ------------------------------------------------------------------

    private void OnDocumentChanged(object? sender, EventArgs e)
    {
        UpdateStatusAndZoom();
        RefreshTree();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EditorViewModel.Status))
        {
            UpdateStatusAndZoom();
        }
        else if (e.PropertyName == nameof(EditorViewModel.Tool))
        {
            HighlightActiveTool();
            UpdateStatusAndZoom();
        }
    }

    private void UpdateStatusAndZoom()
    {
        StatusText.Text = _viewModel.Status;
        ZoomLabel.Text = $"{Workspace.Zoom * 100:0.##}%";
        SelectionInfo.Text = DescribeSelection(_viewModel.Selection);
    }

    private static string DescribeSelection(LayerItem? item)
    {
        if (item is null)
        {
            return "(no selection)";
        }

        if (item is PathItem path)
        {
            int segments = path.SubPaths.Sum(sp => sp.SegmentCount);
            Rect2D b = path.BoundingBox();
            string fill = path.Fill.IsVisible ? "fill" : "no-fill";
            string stroke = path.Stroke.HasVisibleOutline
                ? $"stroke {path.Stroke.Width:0.##}pt"
                : "no-stroke";
            return $"Path '{item.Name}'\n{segments} segment(s), {fill}, {stroke}\n" +
                   $"Bounds: {b.X:0.##}, {b.Y:0.##} → {b.Right:0.##}, {b.Bottom:0.##}pt";
        }

        if (item is ArtGroup group)
        {
            return $"Group '{item.Name}'\n{CountItems(group)} item(s)";
        }

        return item.Name;
    }

    private static int CountItems(ArtGroup group)
    {
        int count = 0;
        foreach (LayerItem child in group.Children)
        {
            count += child is ArtGroup nested ? 1 + CountItems(nested) : 1;
        }

        return count;
    }

    // ------------------------------------------------------------------
    // Object tree (hierarchical, click to select)
    // ------------------------------------------------------------------

    private void RefreshTree()
    {
        if (ObjectTree.Items is not null)
        {
            ObjectTree.Items.Clear();
        }

        foreach (Artboard artboard in _viewModel.Document.Artboards)
        {
            var boardNode = MakeNode($"{artboard.Name}  ({artboard.Layers.Count} layer{(artboard.Layers.Count == 1 ? "" : "s")})", artboard);
            foreach (Layer layer in artboard.Layers)
            {
                var layerNode = MakeNode(FormatLayer(layer), layer);
                foreach (LayerItem child in layer.Children)
                {
                    AddItemNode(layerNode, child, 0);
                }

                boardNode.Items.Add(layerNode);
            }

            ObjectTree.Items.Add(boardNode);
        }
    }

    private static string FormatLayer(Layer layer)
    {
        string flags = layer.IsVisible ? string.Empty : " · hidden";
        if (layer.IsLocked)
        {
            flags += " · locked";
        }

        return $"{layer.Name}{flags}";
    }

    private void AddItemNode(TreeViewItem parent, LayerItem item, int depth)
    {
        var node = MakeNode(DescribeItem(item), item);
        if (item is ArtGroup group)
        {
            foreach (LayerItem child in group.Children)
            {
                AddItemNode(node, child, depth + 1);
            }
        }

        parent.Items.Add(node);
    }

    private static string DescribeItem(LayerItem item)
    {
        return item switch
        {
            PathItem path => DescribePath(path),
            ArtGroup group => $"▸ {NameOf(group.Name, "Group")}",
            _ => item.Name,
        };
    }

    private static string DescribePath(PathItem path)
    {
        string label = NameOf(path.Name, "path");
        string kind = path.IsFullyClosed ? "◼" : "◻";
        return $"{kind} {label}";
    }

    private static string NameOf(string name, string fallback)
        => string.IsNullOrWhiteSpace(name) ? fallback : name;

    private static TreeViewItem MakeNode(string text, object? tag)
        => new() { Header = text, Tag = tag };

    private void OnTreeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ObjectTree.SelectedItem is TreeViewItem node &&
            node.Tag is LayerItem { } item)
        {
            _viewModel.Selection = item;
            UpdateStatusAndZoom();
        }
    }

    // ------------------------------------------------------------------
    // Tool buttons + highlighting
    // ------------------------------------------------------------------

    private IBrush _inactiveBrush = Brushes.Transparent;
    private static readonly IBrush ActiveBrush = new SolidColorBrush(Color.FromRgb(0xBD, 0xDD, 0xF7));

    private void HighlightActiveTool()
    {
        SetActive(ToolSelectButton, _viewModel.Tool == EditorTool.Select);
        SetActive(ToolNodeButton, _viewModel.Tool == EditorTool.Node);
        SetActive(ToolPenButton, _viewModel.Tool == EditorTool.Pen);
    }

    private static void SetActive(Button button, bool active)
        => button.Background = active ? ActiveBrush : Brushes.Transparent;

    // ------------------------------------------------------------------
    // Event handlers (File / Edit / Tools / Object / View)
    // ------------------------------------------------------------------

    private void OnNew(object? sender, RoutedEventArgs e)
    {
        _viewModel.NewDocument();
        UpdateStatusAndZoom();
    }

    private async void OnOpen(object? sender, RoutedEventArgs e)
    {
        StatusText.Text = "Opening from server…";
        await _viewModel.LoadFromServerAsync();
        UpdateStatusAndZoom();
    }

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        StatusText.Text = "Saving to server…";
        await _viewModel.SaveToServerAsync();
        UpdateStatusAndZoom();
    }

    private void OnUndo(object? sender, RoutedEventArgs e) => _viewModel.Undo();

    private void OnRedo(object? sender, RoutedEventArgs e) => _viewModel.Redo();

    private void OnDelete(object? sender, RoutedEventArgs e)
    {
        _viewModel.DeleteSelection();
        UpdateStatusAndZoom();
    }

    private void OnToolSelect(object? sender, RoutedEventArgs e) => SetTool(EditorTool.Select);
    private void OnToolNode(object? sender, RoutedEventArgs e) => SetTool(EditorTool.Node);
    private void OnToolPen(object? sender, RoutedEventArgs e) => SetTool(EditorTool.Pen);

    private void SetTool(EditorTool tool)
    {
        _viewModel.Tool = tool;
        _viewModel.Status = tool switch
        {
            EditorTool.Select => "Selection tool (V): click to pick a path, drag to move it",
            EditorTool.Node => "Node tool (A): drag anchors to move, drag handles to shape curves",
            EditorTool.Pen => "Pen (P): click adds anchors, drag pulls a handle, click the start anchor to close",
            _ => _viewModel.Status,
        };
        HighlightActiveTool();
        UpdateStatusAndZoom();
    }

    private void OnAddRectangle(object? sender, RoutedEventArgs e)
    {
        _viewModel.AddRectangle(80, 60, 240, 160);
        UpdateStatusAndZoom();
    }

    private void OnAddEllipse(object? sender, RoutedEventArgs e)
    {
        _viewModel.AddEllipse(430, 140, 130, 90);
        UpdateStatusAndZoom();
    }

    private void OnAddLine(object? sender, RoutedEventArgs e)
    {
        _viewModel.AddLine(80, 320, 720, 420);
        UpdateStatusAndZoom();
    }

    private void OnZoomIn(object? sender, RoutedEventArgs e)
    {
        Workspace.ZoomIn();
        UpdateStatusAndZoom();
    }

    private void OnZoomOut(object? sender, RoutedEventArgs e)
    {
        Workspace.ZoomOut();
        UpdateStatusAndZoom();
    }

    private void OnActualSize(object? sender, RoutedEventArgs e)
    {
        Workspace.ZoomToActualSize();
        UpdateStatusAndZoom();
    }

    private void OnFitInWindow(object? sender, RoutedEventArgs e)
    {
        Workspace.ZoomToFit();
        UpdateStatusAndZoom();
    }

    private void OnExportPdf(object? sender, RoutedEventArgs e)
    {
        byte[] pdf = _viewModel.ExportPdf();
        UpdateStatusAndZoom();
        SelectionInfo.Text = $"PDF payload ready: {pdf.Length:N0} bytes";
    }

    private void OnExit(object? sender, RoutedEventArgs e)
    {
        StatusText.Text = "Exit is handled by the browser tab itself.";
    }
}
