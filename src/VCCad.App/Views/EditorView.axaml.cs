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
using VCCad.App.Automation;
using VCCad.App.Fonts;
using VCCad.App.ViewModels;
using VCCad.App.Views.Panes;
using VCCad.Core.Model;
using VCCad.Pdf;
using Avalonia.Platform.Storage;

namespace VCCad.App.Views;

using Mark = Avalonia.Controls.Shapes.Path;
using MarkGeometry = Avalonia.Media.Geometry;


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

    /// <summary>
    /// The editor's view-model. Exposed so the desktop host can attach the
    /// automation/assistant services to the same live document the person sees.
    /// </summary>
    public EditorViewModel ViewModel => _viewModel;

    /// <summary>
    /// The workspace canvas. Exposed so the host can fit the artboard to the
    /// window and reserve space for the diagnostics overlay, and so the viewport
    /// operations (<c>view.fit</c>, <c>view.zoom</c>) can drive the same control
    /// the toolbar buttons do.
    /// </summary>
    public Controls.CanvasWorkspace WorkspaceControl => Workspace;

    /// <summary>
    /// The dockable panes and whether each is open — the Windows menu's content.
    /// Exposed so pane visibility is an operation rather than a menu-only action.
    /// </summary>
    public IReadOnlyList<(string Id, string Title, bool IsOpen)> Panes
        => _tabDefs.Select(kv => (kv.Key, kv.Value.Title, _manager.IsTabOpen(kv.Key))).ToArray();

    /// <summary>
    /// The panels that can be resized, with their current arrangement. A panel is either
    /// fixed (it has a height in pixels) or stretchable (it shares the slack with its
    /// neighbours), and the separator between two panels behaves according to which.
    /// </summary>
    public IReadOnlyList<(string Id, string Title, bool Stretchable, double Height, double Weight)> PanelSizes
        => _manager.PanelSizes();

    /// <summary>Makes a panel fixed at a height, or stretchable so it shares the slack.</summary>
    public bool SetPaneStretchable(string pane, bool stretchable, double? height)
        => _manager.SetPanelStretchable(pane, stretchable, height);

    /// <summary>Sets a fixed panel's height, or a stretchable panel's share of the slack.</summary>
    public bool SetPaneSize(string pane, double size)
        => _manager.SetPanelSize(pane, size);

    /// <summary>
    /// Shows or hides a pane by id or title. Passing <c>null</c> toggles it.
    /// Returns the pane's new visibility.
    /// </summary>
    public bool SetPaneOpen(string pane, bool? visible)
    {
        string? id = _tabDefs.Keys.FirstOrDefault(k => string.Equals(k, pane, StringComparison.OrdinalIgnoreCase));
        if (id is null)
        {
            id = _tabDefs.FirstOrDefault(kv =>
                string.Equals(kv.Value.Title, pane, StringComparison.OrdinalIgnoreCase)).Key;
        }

        if (id is null)
        {
            throw new ArgumentException(
                $"Unknown pane '{pane}'. Known panes: {string.Join(", ", _tabDefs.Select(kv => kv.Value.Title))}.");
        }

        bool open = visible ?? !_manager.IsTabOpen(id);
        _manager.SetTabOpen(id, open);
        RefreshWindowMenu();
        return open;
    }

    private Func<bool>? _diagnosticsVisible;
    private Action? _toggleDiagnostics;

    /// <summary>
    /// Adds the diagnostics overlay to the Windows menu, so it is toggled the same
    /// way the panes are rather than only by keyboard shortcut.
    /// </summary>
    public void AttachDiagnosticsToggle(Func<bool> isVisible, Action toggle)
    {
        _diagnosticsVisible = isVisible;
        _toggleDiagnostics = toggle;
        RefreshWindowMenu();
    }

    /// <summary>Re-reads the diagnostics state into the Windows menu.</summary>
    public void RefreshDiagnosticsMenu() => RefreshWindowMenu();

    // Drag/drop state.
    private bool _dragging;
    private string? _dragId;
    private bool _dragIsPanel;
    private DockSide _dropSide = DockSide.Left;
    private Control? _dragView;

    private static readonly IBrush ActiveBrush = new SolidColorBrush(Color.FromRgb(0x2B, 0x4C, 0x7E));

    private double _leftWidth = 240;
    private double _rightWidth = 272;

    /// <summary>Collapses a docking column (and its splitter) when it has no
    /// panels/toolbars, restoring its last width when content returns.</summary>
    private void UpdateColumnVisibility()
    {
        var columns = DockArea.ColumnDefinitions;

        // Column 1 = left panel column; column 5 = right panel column.
        if (LeftPanelHost.Children.Count == 0)
        {
            if (columns[1].Width.IsAbsolute && columns[1].Width.Value > 0)
            {
                _leftWidth = columns[1].Width.Value;
            }

            columns[1].Width = new GridLength(0);
            LeftSplitter.IsVisible = false;
        }
        else
        {
            if (columns[1].Width.Value <= 0)
            {
                columns[1].Width = new GridLength(_leftWidth > 0 ? _leftWidth : 240);
            }

            LeftSplitter.IsVisible = true;
        }

        if (RightPanelHost.Children.Count == 0)
        {
            if (columns[5].Width.IsAbsolute && columns[5].Width.Value > 0)
            {
                _rightWidth = columns[5].Width.Value;
            }

            columns[5].Width = new GridLength(0);
            RightSplitter.IsVisible = false;
        }
        else
        {
            if (columns[5].Width.Value <= 0)
            {
                columns[5].Width = new GridLength(_rightWidth > 0 ? _rightWidth : 272);
            }

            RightSplitter.IsVisible = true;
        }
    }

    public EditorView()
    {
        InitializeComponent();

        DataContext = _viewModel;
        Workspace.AttachEditor(_viewModel);

        // Keep the status bar honest when the view changes from anywhere — the
        // toolbar, a keyboard shortcut, or the automation API.
        Workspace.ViewChanged += (_, _) => UpdateStatus();

        _manager = new DockManager(LeftPanelHost, RightPanelHost, LeftToolbarHost, RightToolbarHost, TopHost, BottomHost);
        _manager.LayoutChanged += (_, _) =>
        {
            RefreshWindowMenu();
            UpdateStatus();
            UpdateColumnVisibility();
        };
        _manager.PanelDragRequested += (_, id) => StartDrag(id, isPanel: true);
        _manager.ToolbarDragRequested += (_, id) => StartDrag(id, isPanel: false);

        BuildPanels();
        BuildToolbars();
        _manager.Build();
        RefreshWindowMenu();
        RebuildDocumentTabs();

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.Sessions.CollectionChanged += (_, _) => RebuildDocumentTabs();
        _viewModel.DocumentChanged += (_, _) =>
        {
            UpdateStatus();
            PromptForMissingFonts(force: true);
        };
        _viewModel.ArtboardDeletionRequested += OnArtboardDeletionRequested;
        UpdateStatus();
        PromptForMissingFonts(force: true);
        KeyDown += OnOverlayKey;
        _textToolbar = new TextToolbar(this);
        _textToolbar.Sync();
        RebuildRecentMenu();
    }

    /// <summary>The contextual type controls, shown while a text block is edited.</summary>
    private TextToolbar? _textToolbar;

    /// <summary>Re-reads the text toolbar from the edit state. Called after any edit.</summary>
    private void SyncTextToolbar() => _textToolbar?.Sync();

    // ------------------------------------------------------------------
    // Missing standard fonts
    // ------------------------------------------------------------------

    /// <summary>Faces the person has already declined for the current document.</summary>
    private string? _fontsDeclined;

    private object? _fontsCheckedFor;

    /// <summary>
    /// Offers to install any standard font this document needs that the computer cannot
    /// supply. A PDF may name Helvetica and embed nothing at all, so the face has to
    /// come from somewhere: we ask first, rather than silently substituting a different
    /// design or shipping fonts we may not redistribute.
    /// </summary>
    private void PromptForMissingFonts(bool force = false)
    {
        CadDocument document = _viewModel.Document;
        if (!force && ReferenceEquals(document, _fontsCheckedFor))
        {
            return;
        }

        _fontsCheckedFor = document;
        IReadOnlyList<string> missing = StandardFontResolver.Installable(document);
        string key = string.Join("|", missing);

        if (missing.Count == 0 || key == _fontsDeclined)
        {
            FontOverlay.IsVisible = false;
            return;
        }

        FontOverlayMessage.Text = missing.Count == 1
            ? "This document uses a font that is not installed on this computer:"
            : $"This document uses {missing.Count} fonts that are not installed on this computer:";
        FontOverlayDetail.Text = string.Join("\n", missing) +
            "\n\nThe URW base-35 fonts supply these faces with the original metrics — the same " +
            "fonts Ghostscript and Inkscape use. They are downloaded into your own font folder " +
            "and are not part of the application.";
        FontOverlay.IsVisible = true;
    }

    private void OnFontInstallLater(object? sender, RoutedEventArgs e)
    {
        _fontsDeclined = string.Join("|", StandardFontResolver.Installable(_viewModel.Document));
        FontOverlay.IsVisible = false;
        UpdateStatus();
    }

    private async void OnFontInstallAgree(object? sender, RoutedEventArgs e)
    {
        FontOverlay.IsVisible = false;
        StatusText.Text = "Installing fonts…";

        try
        {
            IReadOnlyList<string> installed = await StandardFontResolver.InstallAsync();
            _fontsDeclined = null;
            _fontsCheckedFor = null;
            StatusText.Text = installed.Count == 0
                ? "Fonts already installed"
                : $"Installed {installed.Count} font file(s) into {StandardFontFiles.UserFontDirectory}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Font install failed: {ex.Message}";
        }

        Workspace.InvalidateVisual();
        PromptForMissingFonts(force: true);
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
        var text = new TextPane();
        text.Attach(_viewModel);
        var fonts = new FontsPane();
        fonts.Attach(_viewModel);

        var appearance = new DockPanelModel { Id = "appearance", Title = "Appearance", Side = DockSide.Right };
        appearance.Tabs.Add(new DockTab { Id = "colors", Title = "Color", PanelId = "appearance", DefaultSide = DockSide.Right, ContentFactory = () => colors, IsOpen = true });
        appearance.Tabs.Add(new DockTab { Id = "swatches", Title = "Swatches", PanelId = "appearance", DefaultSide = DockSide.Right, ContentFactory = () => swatches });
        appearance.Tabs.Add(new DockTab { Id = "stroke", Title = "Stroke", PanelId = "appearance", DefaultSide = DockSide.Right, ContentFactory = () => stroke, IsOpen = true });
        appearance.Tabs.Add(new DockTab { Id = "text", Title = "Text", PanelId = "appearance", DefaultSide = DockSide.Right, ContentFactory = () => text });
        // The chooser is a tab of its own so it can be left open beside the canvas while
        // somebody works through families, which a dropdown cannot.
        appearance.Tabs.Add(new DockTab { Id = "fonts", Title = "Fonts", PanelId = "appearance", DefaultSide = DockSide.Right, ContentFactory = () => fonts });
        appearance.ActiveTabId = "colors";

        // The colour panel is only as tall as its contents; the Layers panel takes everything
        // else. Left stretchable, all three split the column into equal thirds, so a compact
        // picker floated in a third of the screen while the list of objects - the thing people
        // actually scroll - got the same third. The picker has no scroller, so this has to be
        // enough to show it whole with a little padding.
        appearance.IsStretchable = false;
        appearance.Height = 220;

        var objectsPanel = new DockPanelModel { Id = "objects", Title = "Layers", Side = DockSide.Right };
        objectsPanel.Tabs.Add(new DockTab { Id = "objects", Title = "Layers", PanelId = "objects", DefaultSide = DockSide.Right, ContentFactory = () => objects, IsOpen = true });
        objectsPanel.ActiveTabId = "objects";

        var transformPanel = new DockPanelModel { Id = "transform", Title = "Transform", Side = DockSide.Right };
        transformPanel.Tabs.Add(new DockTab { Id = "transform", Title = "Transform", PanelId = "transform", DefaultSide = DockSide.Right, ContentFactory = () => transform, IsOpen = true });
        transformPanel.ActiveTabId = "transform";

        // Likewise: Transform is a handful of numeric fields, so it takes what it needs and
        // leaves the rest to Layers.
        transformPanel.IsStretchable = false;
        transformPanel.Height = 142;

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
            Content = Icon("magnet", 18),
            IsChecked = _viewModel.OrthogonalSnapEnabled,
            Padding = new Thickness(6, 2),
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
        bar.Children.Add(ToolButton(EditorTool.Lasso, "lasso", "Freehand selection (Q)", LassoMark()));
        bar.Children.Add(ToolButton(EditorTool.Pen, "pen", "Pen (P)"));
        bar.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x42)), Margin = new Thickness(4, 6) });
        bar.Children.Add(ToolButton(EditorTool.Rectangle, "rectangle", "Rectangle (M)"));
        bar.Children.Add(ToolButton(EditorTool.Ellipse, "ellipse", "Ellipse (L)"));
        bar.Children.Add(ToolButton(EditorTool.Artboard, "artboard", "Artboard (O)"));
        bar.Children.Add(ToolButton(EditorTool.Text, "text", "Text (T)"));
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
            MinWidth = 30,
            Padding = new Thickness(6, 2),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(6),
        };
        ToolTip.SetTip(button, tip);
        button.Click += handler;
        return button;
    }

    private Button ToolButton(EditorTool tool, string icon, string tip, Control? mark = null)
    {
        // A drawn mark replaces the icon entirely rather than being put over one: asking for a
        // lasso.png that does not exist throws at startup, which the desktop bootstrap test
        // caught the moment the button was added.
        Button button = mark is null
            ? IconButton(icon, tip, (_, _) => _viewModel.Tool = tool)
            : MarkButton(mark, tip, (_, _) => _viewModel.Tool = tool);

        _toolButtons[tool] = button;
        return button;
    }

    /// <summary>A toolbar button whose mark is drawn rather than loaded from an icon file.</summary>
    private static Button MarkButton(
        Control mark, string tip, EventHandler<Avalonia.Interactivity.RoutedEventArgs> onClick)
    {
        mark.Width = 20;
        mark.Height = 20;

        var button = new Button
        {
            Content = mark,
            Width = 34,
            Height = 34,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            [ToolTip.TipProperty] = tip,
        };

        button.Click += onClick;
        return button;
    }

    /// <summary>
    /// The lasso's toolbar mark, drawn rather than loaded.
    ///
    /// There is no lasso icon in Assets/Icons and pointing the button at a different one would
    /// tell the person it does something it does not. A dashed loop with a tail is what the
    /// tool is, so the mark is the shape.
    /// </summary>
    private static Control LassoMark()
    {
        var stroke = new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xEC));

        var path = new Mark
        {
            Stroke = stroke,
            StrokeThickness = 1.6,
                      Data = MarkGeometry.Parse("M 10,2 C 15,2 18,5 18,9 C 18,14 14,17 10,17 C 5,17 2,14 2,9 C 2,5 5,2 10,2 Z"),
        };


        var tail = new Mark
        {
            Stroke = stroke,
            StrokeThickness = 1.6,
            Data = MarkGeometry.Parse("M 10,17 L 13,21"),
        };

        var canvas = new Canvas();
        canvas.Children.Add(path);
        canvas.Children.Add(tail);
        return canvas;
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

        // The diagnostics overlay is a window-level panel too, so it belongs here.
        if (_toggleDiagnostics is not null)
        {
            WindowsMenu.Items.Add(new Separator());
            var diagnostics = new MenuItem
            {
                Header = "Diagnostics overlay",
                IsChecked = _diagnosticsVisible?.Invoke() ?? false,
            };
            diagnostics.Click += (_, _) =>
            {
                _toggleDiagnostics();
                RefreshWindowMenu();
            };
            WindowsMenu.Items.Add(diagnostics);
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
        if (e.PropertyName == nameof(EditorViewModel.ActiveSession))
        {
            RebuildDocumentTabs();
        }
        else if (e.PropertyName == nameof(EditorViewModel.Tool))
        {
            HighlightActiveTool();
        }
        else if (e.PropertyName == nameof(EditorViewModel.Status))
        {
            UpdateStatus();
        }
        else if (e.PropertyName == nameof(EditorViewModel.IsEditingText))
        {
            // Entering or leaving text edit is what shows and hides the type controls.
            SyncTextToolbar();
        }
    }

    private void UpdateStatus()
    {
        // Entering or leaving text edit changes which controls apply.
        SyncTextToolbar();

        // Tell the person when a font had to be substituted. The document not embedding
        // a font is a fidelity limit, not something they should have to spot from the
        // rendering — and an embedded font that failed to load is a defect.
        string? fontWarning = Fonts.FontUsage.Warning(_viewModel.Document);
        string status = _viewModel.Status;
        if (fontWarning is not null)
        {
            status = status.Length == 0 ? fontWarning : $"{status}   {fontWarning}";
        }

        StatusText.Text = status;
        var origin = _viewModel.Document.ContentOrigin();
        ZoomLabel.Text = $"origin {origin.X:0.#}, {origin.Y:0.#}   ·   {Workspace.Zoom * 100:0.##}%";
    }

    private void OnNew(object? sender, RoutedEventArgs e) => _viewModel.NewDocument();
    private void OnNewTab(object? sender, RoutedEventArgs e) => _viewModel.NewDocument();

    /// <summary>Rebuilds the document tab strip from the open sessions.</summary>
    private void RebuildDocumentTabs()
    {
        var items = new List<Control>();
        foreach (DocumentSession session in _viewModel.Sessions)
        {
            bool active = ReferenceEquals(session, _viewModel.ActiveSession);
            DocumentSession captured = session;

            var title = new Button
            {
                Content = session.Document.Name,
                FontSize = EditorTheme.FontSize,
                Padding = new Thickness(10, 2),
                Background = active
                    ? new SolidColorBrush(Color.FromRgb(0x2B, 0x4C, 0x7E))
                    : Brushes.Transparent,
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(5),
                Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE9)),
            };
            title.Click += (_, _) => _viewModel.ActiveSession = captured;

            var close = new Button
            {
                Content = "✕",
                FontSize = EditorTheme.FontSize - 1,
                Padding = new Thickness(5, 2),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(5),
                Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xA3)),
                IsVisible = _viewModel.Sessions.Count > 1,
            };
            close.Click += (_, _) => _viewModel.CloseSession(captured);

            var tab = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0 };
            tab.Children.Add(title);
            tab.Children.Add(close);
            items.Add(tab);
        }

        DocTabsHost.ItemsSource = items;
    }
    private void OnNewArtboard(object? sender, RoutedEventArgs e) { _viewModel.AddNewArtboard(); UpdateStatus(); }

    private async void OnImportPdf(object? sender, RoutedEventArgs e)
    {
        TopLevel? top = TopLevel.GetTopLevel(this);
        if (top?.StorageProvider is not { } storage)
        {
            StatusText.Text = "File picking is unavailable in this environment";
            return;
        }

        var options = new FilePickerOpenOptions
        {
            Title = "Import PDF",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("PDF") { Patterns = new[] { "*.pdf" } } },
        };

        IReadOnlyList<IStorageFile> files = await storage.OpenFilePickerAsync(options);
        if (files.Count == 0)
        {
            return;
        }

        try
        {
            await using Stream stream = await files[0].OpenReadAsync();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);

            string? path = files[0].TryGetLocalPath();
            _viewModel.ImportPdf(buffer.ToArray(), path);

            if (!string.IsNullOrWhiteSpace(path))
            {
                RecentFiles.Add(path);
                RebuildRecentMenu();
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Import failed: {ex.Message}";
        }

        UpdateStatus();
    }

    private void OnOpen(object? sender, RoutedEventArgs e) => FireAndForget(_viewModel.LoadFromServerAsync(), "Opening…");

    // ------------------------------------------------------------------
    // Recent files, closing, and asking before discarding work
    // ------------------------------------------------------------------

    /// <summary>Where the pending confirmation should go once the person answers.</summary>
    private Action? _pendingConfirm;
    private Action? _pendingDecline;
    private bool _confirmIsRecovery;

    /// <summary>Rebuilds File → Open Recent from the remembered paths.</summary>
    private void RebuildRecentMenu()
    {
        MenuItem? menu = this.FindControl<MenuItem>("OpenRecentMenu");
        if (menu is null)
        {
            return;
        }

        menu.Items.Clear();
        IReadOnlyList<string> paths = RecentFiles.Load();

        if (paths.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "(nothing yet)", IsEnabled = false });
            return;
        }

        foreach (string path in paths)
        {
            var item = new MenuItem { Header = Path.GetFileName(path) };
            ToolTip.SetTip(item, path);
            string captured = path;

            item.Click += async (_, _) =>
            {
                try
                {
                    byte[] bytes = await File.ReadAllBytesAsync(captured);
                    _viewModel.ImportPdf(bytes, captured);
                    RebuildRecentMenu();
                }
                catch (Exception ex)
                {
                    StatusText.Text = $"Could not open {captured}: {ex.Message}";

                    // A path that no longer opens should stop being offered.
                    RecentFiles.Remove(captured);
                    RebuildRecentMenu();
                }

                UpdateStatus();
            };

            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());
        var clear = new MenuItem { Header = "Clear the list" };
        clear.Click += (_, _) =>
        {
            foreach (string path in RecentFiles.Load())
            {
                RecentFiles.Remove(path);
            }

            RebuildRecentMenu();
        };
        menu.Items.Add(clear);
    }

    /// <summary>
    /// Asks before doing something that would lose unsaved changes. The action runs only
    /// if the person chooses to save it first, or to discard it deliberately.
    /// </summary>
    private void AskBeforeDiscarding(string title, string message, Action proceed,
        Action? decline = null)
    {
        if (!_viewModel.ActiveSession.IsModified)
        {
            proceed();
            return;
        }

        ShowConfirm(title, message, proceed, decline ?? proceed, isRecovery: false);
    }

    /// <summary>Shows the shared prompt. Confirm and Discard do different things.</summary>
    private void ShowConfirm(string title, string message, Action confirm, Action decline,
        bool isRecovery)
    {
        _pendingConfirm = confirm;
        _pendingDecline = decline;
        _confirmIsRecovery = isRecovery;

        this.FindControl<TextBlock>("ConfirmTitle")!.Text = title;
        this.FindControl<TextBlock>("ConfirmMessage")!.Text = message;
        this.FindControl<Grid>("ConfirmOverlay")!.IsVisible = true;
    }

    /// <summary>Escape dismisses whatever prompt is showing, so the window is never trapped.</summary>
    private void OnOverlayKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DismissOverlay())
        {
            e.Handled = true;
        }
    }

    private void CloseConfirmOverlay()
    {
        this.FindControl<Grid>("ConfirmOverlay")!.IsVisible = false;
        _pendingConfirm = null;
        _pendingDecline = null;
        _confirmIsRecovery = false;

        // The overlay is shared, so its buttons go back to their usual labels.
        this.FindControl<Button>("ConfirmSave")!.Content = "Save";
        this.FindControl<Button>("ConfirmDiscard")!.Content = "Discard";
    }

    private void OnConfirmSave(object? sender, RoutedEventArgs e)
    {
        Action? proceed = _pendingConfirm;
        bool recovery = _confirmIsRecovery;
        CloseConfirmOverlay();

        // Saving something is not what a recovery prompt is asking about; writing the
        // current document to the server there would be an unrelated side effect.
        if (!recovery)
        {
            _viewModel.SaveActiveToServer();
        }

        proceed?.Invoke();
    }

    private void OnConfirmDiscard(object? sender, RoutedEventArgs e)
    {
        Action? decline = _pendingDecline;
        CloseConfirmOverlay();
        decline?.Invoke();
    }

    private void OnConfirmCancel(object? sender, RoutedEventArgs e) => CloseConfirmOverlay();

    /// <summary>
    /// Offers work left behind by a run that ended badly. Recovery is the person's choice,
    /// not automatic: the snapshot may be a second or two behind, and silently replacing
    /// whatever is open with it would be a worse surprise than asking.
    /// </summary>
    public void OfferRecovery(VCCad.Core.Model.CadDocument recovered, int commands)
    {
        _recovered = recovered;

        ShowConfirm(
            "Recover unsaved work?",
            $"\u0022{recovered.Name}\u0022 was left behind when the last session ended " +
            $"unexpectedly, with {commands} command(s) since its last save.",
            confirm: () =>
            {
                _viewModel.AddDocument(recovered);
                SessionJournal.Clear();
                UpdateStatus();
            },
            // Discarding has to actually discard, or the prompt simply returns on the
            // next launch and the person is never rid of it.
            decline: () => SessionJournal.Clear(),
            isRecovery: true);

        this.FindControl<Button>("ConfirmSave")!.Content = "Recover";
        this.FindControl<Button>("ConfirmDiscard")!.Content = "Discard";
    }

    /// <summary>
    /// Dismisses the prompt if one is showing. Escape closes it, because a modal with no
    /// keyboard way out traps anyone who does not want to answer yet.
    /// </summary>
    public bool DismissOverlay()
    {
        if (!this.FindControl<Grid>("ConfirmOverlay")!.IsVisible &&
            !this.FindControl<Grid>("FontOverlay")!.IsVisible)
        {
            return false;
        }

        CloseConfirmOverlay();
        this.FindControl<Grid>("FontOverlay")!.IsVisible = false;
        return true;
    }

    /// <summary>The document offered for recovery, so a discard can drop it.</summary>
    private VCCad.Core.Model.CadDocument? _recovered;

    /// <summary>Whether anything open has changes that are not on disk.</summary>
    public bool HasUnsavedChanges => _viewModel.Sessions.Any(s => s.IsModified);

    /// <summary>
    /// Asks about unsaved work before the application exits. Quitting is the one action
    /// that can lose everything at once, so it has to be the one that asks.
    /// </summary>
    public void PromptBeforeExit(Action proceed)
    {
        List<DocumentSession> dirty = _viewModel.Sessions.Where(s => s.IsModified).ToList();
        if (dirty.Count == 0)
        {
            proceed();
            return;
        }

        string subject = dirty.Count == 1
            ? $"\u0022{dirty[0].Document.Name}\u0022 has unsaved changes"
            : $"{dirty.Count} documents have unsaved changes";

        AskBeforeDiscarding(
            "Save changes before quitting?", subject + ", and quitting will lose them.", proceed);
    }

    private void OnCloseDocument(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.Sessions.Count == 0)
        {
            return;
        }

        string name = _viewModel.ActiveSession.Document.Name;
        AskBeforeDiscarding(
            "Save changes?",
            $"\"{name}\" has unsaved changes, and closing it will lose them.",
            () => _viewModel.CloseActiveDocument());
    }

    private void OnSave(object? sender, RoutedEventArgs e) => FireAndForget(_viewModel.SaveToServerAsync(), "Saving…");
    private void OnUndo(object? sender, RoutedEventArgs e) => _viewModel.Undo();
    private void OnRedo(object? sender, RoutedEventArgs e) => _viewModel.Redo();
    private void OnDelete(object? sender, RoutedEventArgs e) => _viewModel.RequestDeleteSelection();

    private Artboard? _pendingArtboard;

    private void OnArtboardDeletionRequested(object? sender, ArtboardDeletionRequest e)
    {
        _pendingArtboard = e.Artboard;
        ModalMessage.Text = $"\"{e.Artboard.Name}\" contains {e.ChildCount} object(s). " +
                            "Keep them (orphaned at their current position), delete them, or cancel?";
        ModalOverlay.IsVisible = true;
    }

    private void OnDeleteKeepObjects(object? sender, RoutedEventArgs e)
        => ResolveArtboardDeletion(ArtboardDeletionChoice.KeepObjects);

    private void OnDeleteAllObjects(object? sender, RoutedEventArgs e)
        => ResolveArtboardDeletion(ArtboardDeletionChoice.DeleteObjects);

    private void OnDeleteCancel(object? sender, RoutedEventArgs e)
        => ResolveArtboardDeletion(ArtboardDeletionChoice.Cancel);

    private void ResolveArtboardDeletion(ArtboardDeletionChoice choice)
    {
        ModalOverlay.IsVisible = false;
        if (_pendingArtboard is { } artboard)
        {
            _viewModel.DeleteArtboard(artboard, choice);
            _pendingArtboard = null;
        }

        UpdateStatus();
    }
    private void OnGroup(object? sender, RoutedEventArgs e) => _viewModel.GroupSelection();
    private void OnUngroup(object? sender, RoutedEventArgs e) => _viewModel.UngroupSelection();
    private void OnClosePath(object? sender, RoutedEventArgs e) => _viewModel.CloseSelectedPaths();
    private void OnJoinPaths(object? sender, RoutedEventArgs e) => _viewModel.JoinSelection();

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
