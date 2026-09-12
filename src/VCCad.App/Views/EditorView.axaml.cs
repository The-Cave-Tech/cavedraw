using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using VCCad.App.Docking;
using VCCad.App.ViewModels;
using VCCad.App.Views.Panes;

namespace VCCad.App.Views;

/// <summary>
/// The editor shell: a menu bar, a docking area (left/right panel columns, top/
/// bottom toolbars, central canvas), a drop overlay for docking, and a status
/// bar. All content lives in dockable panels/toolbars managed by DockManager.
/// </summary>
public partial class EditorView : UserControl
{
    private readonly EditorViewModel _viewModel = new();
    private DockManager _manager = null!;
    private readonly Dictionary<string, (string Title, string PanelId)> _tabDefs = new();
    private readonly Dictionary<EditorTool, Button> _toolButtons = new();

    // Drag/drop state.
    private bool _dragging;
    private string? _dragId;
    private bool _dragIsPanel;
    private DockSide _dropSide = DockSide.Left;
    private Control? _dragView;

    private static readonly IBrush ActiveBrush = new SolidColorBrush(Color.FromRgb(0x2B, 0x4C, 0x7E));

    public EditorView()
    {
        InitializeComponent();

        DataContext = _viewModel;
        Workspace.AttachEditor(_viewModel);

        _manager = new DockManager(LeftHost, RightHost, TopHost, BottomHost);
        _manager.LayoutChanged += (_, _) => { RefreshWindowMenu(); UpdateStatus(); };
        _manager.PanelDragRequested += (_, id) => StartDrag(id, isPanel: true);
        _manager.ToolbarDragRequested += (_, id) => StartDrag(id, isPanel: false);

        BuildPanels();
        BuildToolbars();
        _manager.Build();
        RefreshWindowMenu();

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.DocumentChanged += (_, _) => UpdateStatus();
        UpdateStatus();
    }

    // ------------------------------------------------------------------
    // Panels & toolbars
    // ------------------------------------------------------------------

    private void BuildPanels()
    {
        var transform = new TransformPane();
        transform.Attach(_viewModel);
        var colors = new ColorsPane();
        colors.Attach(_viewModel);
        var stroke = new StrokePane();
        stroke.Attach(_viewModel);
        var swatches = new SwatchesPane();
        swatches.Attach(_viewModel);
        var objects = new ObjectsPane();
        objects.Attach(_viewModel);

        var appearance = new DockPanelModel { Id = "appearance", Title = "Appearance", Side = DockSide.Right };
        appearance.Tabs.Add(new DockTab { Id = "colors", Title = "Color", PanelId = "appearance", DefaultSide = DockSide.Right, ContentFactory = () => colors, IsOpen = true });
        appearance.Tabs.Add(new DockTab { Id = "swatches", Title = "Swatches", PanelId = "appearance", DefaultSide = DockSide.Right, ContentFactory = () => swatches });
        appearance.Tabs.Add(new DockTab { Id = "stroke", Title = "Stroke", PanelId = "appearance", DefaultSide = DockSide.Right, ContentFactory = () => stroke, IsOpen = true });
        appearance.ActiveTabId = "colors";

        var objectsPanel = new DockPanelModel { Id = "objects", Title = "Objects", Side = DockSide.Right };
        objectsPanel.Tabs.Add(new DockTab { Id = "objects", Title = "Objects", PanelId = "objects", DefaultSide = DockSide.Right, ContentFactory = () => objects, IsOpen = true });
        objectsPanel.ActiveTabId = "objects";

        var transformPanel = new DockPanelModel { Id = "transform", Title = "Transform", Side = DockSide.Right };
        transformPanel.Tabs.Add(new DockTab { Id = "transform", Title = "Transform", PanelId = "transform", DefaultSide = DockSide.Right, ContentFactory = () => transform, IsOpen = true });
        transformPanel.ActiveTabId = "transform";

        foreach (DockPanelModel panel in new[] { appearance, objectsPanel, transformPanel })
        {
            _manager.RegisterPanel(panel);
            foreach (DockTab tab in panel.Tabs)
            {
                _tabDefs[tab.Id] = (tab.Title, tab.PanelId);
            }
        }
    }

    private void BuildToolbars()
    {
        _manager.RegisterToolbar(new ToolbarModel
        {
            Id = "main",
            Title = "Main",
            Side = DockSide.Top,
            ContentFactory = BuildMainToolbar,
        });

        _manager.RegisterToolbar(new ToolbarModel
        {
            Id = "tools",
            Title = "Tools",
            Side = DockSide.Left,
            ContentFactory = BuildToolsToolbar,
        });
    }

    private Control BuildMainToolbar()
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        bar.Children.Add(IconButton("new", "New document", OnNew));
        bar.Children.Add(IconButton("open", "Open from server", OnOpen));
        bar.Children.Add(IconButton("save", "Save to server", OnSave));
        bar.Children.Add(new Border { Width = 1, Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x42)), Margin = new Thickness(6, 4) });
        bar.Children.Add(IconButton("undo", "Undo", OnUndo));
        bar.Children.Add(IconButton("redo", "Redo", OnRedo));
        bar.Children.Add(new Border { Width = 1, Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x42)), Margin = new Thickness(6, 4) });
        bar.Children.Add(IconButton("zoom-out", "Zoom out", OnZoomOut));
        bar.Children.Add(IconButton("zoom-in", "Zoom in", OnZoomIn));
        bar.Children.Add(IconButton("fit", "Fit in window", OnFitInWindow));
        bar.Children.Add(new Border { Width = 1, Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x42)), Margin = new Thickness(6, 4) });

        var snap = new ToggleButton
        {
            Content = "Snap",
            IsChecked = _viewModel.OrthogonalSnapEnabled,
            Padding = new Thickness(10, 4),
            CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
        };
        ToolTip.SetTip(snap, "Snap moved points to neighbouring H/V lines on release");
        snap.Checked += (_, _) => _viewModel.OrthogonalSnapEnabled = true;
        snap.Unchecked += (_, _) => _viewModel.OrthogonalSnapEnabled = false;
        bar.Children.Add(snap);
        return bar;
    }

    private Control BuildToolsToolbar()
    {
        _toolButtons.Clear();
        var bar = new StackPanel { Orientation = Orientation.Vertical, Spacing = 2 };
        bar.Children.Add(ToolButton(EditorTool.Select, "select", "Selection (V)"));
        bar.Children.Add(ToolButton(EditorTool.Node, "node", "Nodes / direct selection (A)"));
        bar.Children.Add(ToolButton(EditorTool.Pen, "pen", "Pen (P)"));
        bar.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x42)), Margin = new Thickness(4, 6) });
        bar.Children.Add(ToolButton(EditorTool.Rectangle, "rectangle", "Rectangle (M)"));
        bar.Children.Add(ToolButton(EditorTool.Ellipse, "ellipse", "Ellipse (L)"));
        bar.Children.Add(ToolButton(EditorTool.Artboard, "artboard", "Artboard (O)"));
        HighlightActiveTool();
        return bar;
    }

    private static Image Icon(string name, double size = 20)
    {
        var bitmap = new Bitmap(AssetLoader.Open(new Uri($"avares://VCCad.App/Assets/Icons/{name}.png")));
        return new Image
        {
            Source = bitmap,
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
        };
    }

    private Button IconButton(string icon, string tip, EventHandler<RoutedEventArgs> handler)
    {
        var button = new Button
        {
            Content = Icon(icon),
            MinWidth = 34,
            Padding = new Thickness(7, 4),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(6),
        };
        ToolTip.SetTip(button, tip);
        button.Click += handler;
        return button;
    }

    private Button ToolButton(EditorTool tool, string icon, string tip)
    {
        Button button = IconButton(icon, tip, (_, _) => _viewModel.Tool = tool);
        _toolButtons[tool] = button;
        return button;
    }

    private void HighlightActiveTool()
    {
        foreach ((EditorTool tool, Button button) in _toolButtons)
        {
            button.Background = tool == _viewModel.Tool ? ActiveBrush : Brushes.Transparent;
        }
    }

    // ------------------------------------------------------------------
    // Windows menu
    // ------------------------------------------------------------------

    private void RefreshWindowMenu()
    {
        WindowsMenu.Items.Clear();
        foreach ((string id, (string title, _)) in _tabDefs)
        {
            var item = new MenuItem { Header = title, IsChecked = _manager.IsTabOpen(id) };
            string tabId = id;
            item.Click += (_, _) => _manager.ToggleTab(tabId);
            WindowsMenu.Items.Add(item);
        }
    }

    // ------------------------------------------------------------------
    // Drag / drop docking
    // ------------------------------------------------------------------

    private void StartDrag(string id, bool isPanel)
    {
        _dragging = true;
        _dragId = id;
        _dragIsPanel = isPanel;
        DropOverlay.IsVisible = true;

        // Pull the actual view out of its host and let it follow the pointer.
        _dragView = _manager.Detach(id, isPanel);
        if (_dragView is not null)
        {
            _dragView.Opacity = 0.9;
            _dragView.IsHitTestVisible = false;
            Canvas.SetLeft(_dragView, 0);
            Canvas.SetTop(_dragView, 0);
            DropOverlay.Children.Add(_dragView);
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging)
        {
            return;
        }

        Point p = e.GetPosition(DockArea);
        double w = Math.Max(DockArea.Bounds.Width, 1);
        double h = Math.Max(DockArea.Bounds.Height, 1);

        // Nearest edge wins; panels are restricted to left/right.
        double dl = p.X / w;
        double dr = 1 - p.X / w;
        double dt = p.Y / h;
        double db = 1 - p.Y / h;
        double min = Math.Min(Math.Min(dl, dr), Math.Min(dt, db));

        DockSide side = min switch
        {
            _ when min == dl => DockSide.Left,
            _ when min == dr => DockSide.Right,
            _ when min == dt => DockSide.Top,
            _ => DockSide.Bottom,
        };

        if (_dragIsPanel && side is DockSide.Top or DockSide.Bottom)
        {
            side = p.X < w / 2 ? DockSide.Left : DockSide.Right;
        }

        _dropSide = side;
        ShowDropZone(side, w, h);

        if (_dragView is not null)
        {
            Canvas.SetLeft(_dragView, p.X - 24);
            Canvas.SetTop(_dragView, p.Y - 14);
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging)
        {
            return;
        }

        if (_dragView is not null)
        {
            DropOverlay.Children.Remove(_dragView);
            _dragView.Opacity = 1.0;
            _dragView.IsHitTestVisible = true;
            _dragView = null;
        }

        if (_dragId is not null)
        {
            if (_dragIsPanel)
            {
                _manager.DockPanel(_dragId, _dropSide);
            }
            else
            {
                _manager.DockToolbar(_dragId, _dropSide);
            }
        }

        _dragging = false;
        _dragId = null;
        DropOverlay.IsVisible = false;
    }

    private void ShowDropZone(DockSide side, double w, double h)
    {
        double bw = Math.Max(60, w * 0.22);
        double bh = Math.Max(60, h * 0.22);

        DropLeft.IsVisible = side == DockSide.Left;
        DropRight.IsVisible = side == DockSide.Right;
        DropTop.IsVisible = side == DockSide.Top;
        DropBottom.IsVisible = side == DockSide.Bottom;

        Canvas.SetLeft(DropLeft, 0);
        Canvas.SetTop(DropLeft, 0);
        DropLeft.Width = bw;
        DropLeft.Height = h;

        Canvas.SetLeft(DropRight, w - bw);
        Canvas.SetTop(DropRight, 0);
        DropRight.Width = bw;
        DropRight.Height = h;

        Canvas.SetLeft(DropTop, 0);
        Canvas.SetTop(DropTop, 0);
        DropTop.Width = w;
        DropTop.Height = bh;

        Canvas.SetLeft(DropBottom, 0);
        Canvas.SetTop(DropBottom, h - bh);
        DropBottom.Width = w;
        DropBottom.Height = bh;
    }

    // ------------------------------------------------------------------
    // Status / menu handlers
    // ------------------------------------------------------------------

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EditorViewModel.Tool))
        {
            HighlightActiveTool();
        }
        else if (e.PropertyName == nameof(EditorViewModel.Status))
        {
            UpdateStatus();
        }
    }

    private void UpdateStatus()
    {
        StatusText.Text = _viewModel.Status;
        ZoomLabel.Text = $"{Workspace.Zoom * 100:0.##}%";
    }

    private void OnNew(object? sender, RoutedEventArgs e) => _viewModel.NewDocument();
    private void OnNewArtboard(object? sender, RoutedEventArgs e) { _viewModel.AddNewArtboard(); UpdateStatus(); }

    private void OnOpen(object? sender, RoutedEventArgs e) => FireAndForget(_viewModel.LoadFromServerAsync(), "Opening…");
    private void OnSave(object? sender, RoutedEventArgs e) => FireAndForget(_viewModel.SaveToServerAsync(), "Saving…");
    private void OnUndo(object? sender, RoutedEventArgs e) => _viewModel.Undo();
    private void OnRedo(object? sender, RoutedEventArgs e) => _viewModel.Redo();
    private void OnDelete(object? sender, RoutedEventArgs e) => _viewModel.DeleteSelection();
    private void OnGroup(object? sender, RoutedEventArgs e) => _viewModel.GroupSelection();
    private void OnUngroup(object? sender, RoutedEventArgs e) => _viewModel.UngroupSelection();

    private void OnZoomIn(object? sender, RoutedEventArgs e) { Workspace.ZoomIn(); UpdateStatus(); }
    private void OnZoomOut(object? sender, RoutedEventArgs e) { Workspace.ZoomOut(); UpdateStatus(); }
    private void OnActualSize(object? sender, RoutedEventArgs e) { Workspace.ZoomToActualSize(); UpdateStatus(); }
    private void OnFitInWindow(object? sender, RoutedEventArgs e) { Workspace.ZoomToFit(); UpdateStatus(); }

    private void OnExportPdf(object? sender, RoutedEventArgs e)
    {
        _ = _viewModel.ExportPdf();
        UpdateStatus();
    }

    private void OnExit(object? sender, RoutedEventArgs e)
    {
        StatusText.Text = "Exit is handled by the browser tab itself.";
    }

    private async void FireAndForget(Task task, string busy)
    {
        StatusText.Text = busy;
        try
        {
            await task;
        }
        finally
        {
            UpdateStatus();
        }
    }
}
