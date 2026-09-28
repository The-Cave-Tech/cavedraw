using Avalonia.Controls;
using Avalonia.Layout;

namespace VCCad.App.Docking;

/// <summary>
/// Owns the dock layout: which panels/toolbars exist, which side they are on,
/// which tabs are open, and rebuilds the host containers. Docking rules:
/// panels may dock left/right only; toolbars may dock on any edge.
/// </summary>
public sealed class DockManager
{
    private readonly Dictionary<string, DockPanelModel> _panels = new();
    private readonly Dictionary<string, ToolbarModel> _toolbars = new();
    private readonly Dictionary<string, DockTab> _tabs = new();
    private readonly Dictionary<string, DockTabPanelView> _panelViews = new();
    private readonly Dictionary<string, DockToolbarView> _toolbarViews = new();
    private readonly Dictionary<string, DockSide> _toolbarSides = new();

    public DockManager(
        Grid leftPanelHost, Grid rightPanelHost,
        StackPanel leftToolbarHost, StackPanel rightToolbarHost,
        StackPanel topToolbarHost, StackPanel bottomToolbarHost)
    {
        LeftPanelHost = leftPanelHost;
        RightPanelHost = rightPanelHost;
        LeftToolbarHost = leftToolbarHost;
        RightToolbarHost = rightToolbarHost;
        TopToolbarHost = topToolbarHost;
        BottomToolbarHost = bottomToolbarHost;
    }

    // Panels and toolbars are separate kinds and never share a zone.
    public Grid LeftPanelHost { get; }

    public Grid RightPanelHost { get; }

    public StackPanel LeftToolbarHost { get; }

    public StackPanel RightToolbarHost { get; }

    public StackPanel TopToolbarHost { get; }

    public StackPanel BottomToolbarHost { get; }

    /// <summary>Raised whenever the layout changes (open/close/dock).</summary>
    public event EventHandler? LayoutChanged;

    /// <summary>Raised when a panel handle drag starts (id of the panel).</summary>
    public event EventHandler<string>? PanelDragRequested;

    /// <summary>Raised when a toolbar handle drag starts (id of the toolbar).</summary>
    public event EventHandler<string>? ToolbarDragRequested;

    /// <summary>The view for a panel, created once and reused across rebuilds.</summary>
    private DockTabPanelView PanelView(DockPanelModel panel)
    {
        if (!_panelViews.TryGetValue(panel.Id, out DockTabPanelView? view))
        {
            view = new DockTabPanelView(panel, this);
            view.DragRequested += (_, id) => PanelDragRequested?.Invoke(this, id);
            _panelViews[panel.Id] = view;
        }

        view.Rebuild();
        return view;
    }

    public void RegisterPanel(DockPanelModel panel)
    {
        _panels[panel.Id] = panel;
        foreach (DockTab tab in panel.Tabs)
        {
            _tabs[tab.Id] = tab;
        }
    }

    public void RegisterToolbar(ToolbarModel toolbar) => _toolbars[toolbar.Id] = toolbar;

    public bool IsTabOpen(string tabId) => _tabs.TryGetValue(tabId, out DockTab? tab) && tab.IsOpen;

    public void SetTabOpen(string tabId, bool open)
    {
        if (!_tabs.TryGetValue(tabId, out DockTab? tab))
        {
            return;
        }

        tab.IsOpen = open;
        if (open)
        {
            _panels[tab.PanelId].ActiveTabId = tab.Id;
        }

        Build();
    }

    public void ToggleTab(string tabId)
    {
        if (_tabs.TryGetValue(tabId, out DockTab? tab))
        {
            SetTabOpen(tabId, !tab.IsOpen);
        }
    }

    public void ClosePanel(string panelId)
    {
        if (!_panels.TryGetValue(panelId, out DockPanelModel? panel))
        {
            return;
        }

        foreach (DockTab tab in panel.Tabs)
        {
            tab.IsOpen = false;
        }

        Build();
    }

    public void DockPanel(string panelId, DockSide side)
    {
        if (!_panels.TryGetValue(panelId, out DockPanelModel? panel))
        {
            return;
        }

        panel.Side = side is DockSide.Top or DockSide.Bottom ? DockSide.Left : side;
        Build();
    }

    public void DockToolbar(string toolbarId, DockSide side)
    {
        if (_toolbars.TryGetValue(toolbarId, out ToolbarModel? toolbar))
        {
            toolbar.Side = side;
            Build();
        }
    }

    /// <summary>Removes a panel/toolbar view from its host so the caller can drag
    /// it (the view is re-added by the next <see cref="Build"/> on drop).</summary>
    public Control? Detach(string id, bool isPanel)
    {
        Control? view = null;
        if (isPanel && _panelViews.TryGetValue(id, out DockTabPanelView? panelView))
        {
            view = panelView;
        }
        else if (!isPanel && _toolbarViews.TryGetValue(id, out DockToolbarView? toolbarView))
        {
            view = toolbarView;
        }

        if (view?.Parent is Panel parent)
        {
            parent.Children.Remove(view);
        }

        return view;
    }

    /// <summary>
    /// Every dockable panel with its current arrangement: whether it is stretchable and
    /// the numbers the layout uses for it.
    /// </summary>
    public IReadOnlyList<(string Id, string Title, bool Stretchable, double Height, double Weight)> PanelSizes()
        => _panels.Values
            .Where(p => p.IsVisible)
            .Select(p => (p.Id, p.Title, p.IsStretchable, p.Height, p.Weight))
            .ToArray();

    /// <summary>Makes a panel fixed at a pixel height, or stretchable again.</summary>
    public bool SetPanelStretchable(string pane, bool stretchable, double? height)
    {
        DockPanelModel? panel = FindPanel(pane);
        if (panel is null)
        {
            return false;
        }

        panel.IsStretchable = stretchable;
        if (height is { } h)
        {
            panel.Height = Math.Max(DockPanelModel.MinimumHeight, h);
        }

        Build();
        return true;
    }

    /// <summary>
    /// Sizes a panel: the pixel height when it is fixed, or its share of the slack when it
    /// is stretchable.
    /// </summary>
    public bool SetPanelSize(string pane, double size)
    {
        DockPanelModel? panel = FindPanel(pane);
        if (panel is null)
        {
            return false;
        }

        if (panel.IsStretchable)
        {
            panel.Weight = Math.Max(0.01, size);
        }
        else
        {
            panel.Height = Math.Max(DockPanelModel.MinimumHeight, size);
        }

        Build();
        return true;
    }

    private DockPanelModel? FindPanel(string pane)
        => _panels.Values.FirstOrDefault(p =>
               string.Equals(p.Id, pane, StringComparison.OrdinalIgnoreCase)) ??
           _panels.Values.FirstOrDefault(p =>
               string.Equals(p.Title, pane, StringComparison.OrdinalIgnoreCase)) ??
           _panels.Values.FirstOrDefault(p => p.Tabs.Any(t =>
               string.Equals(t.Id, pane, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(t.Title, pane, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Recreates the host contents from the current model.</summary>
    public void Build()
    {
        LeftPanelHost.Children.Clear();
        RightPanelHost.Children.Clear();
        LeftToolbarHost.Children.Clear();
        RightToolbarHost.Children.Clear();
        TopToolbarHost.Children.Clear();
        BottomToolbarHost.Children.Clear();

        // Panels are stacked as sized rows with a draggable separator between neighbours,
        // so a person can make one taller or shorter. The separator says whether dragging
        // it rebalances two stretchable panels or resizes a fixed one.
        DockStackLayout.Build(RightPanelHost,
            _panels.Values.Where(p => p.IsVisible && p.Side == DockSide.Right).ToList(),
            PanelView, Build);

        DockStackLayout.Build(LeftPanelHost,
            _panels.Values.Where(p => p.IsVisible && p.Side != DockSide.Right).ToList(),
            PanelView, Build);

        foreach (ToolbarModel toolbar in _toolbars.Values)
        {
            StackPanel host = toolbar.Side switch
            {
                DockSide.Right => RightToolbarHost,
                DockSide.Left => LeftToolbarHost,
                DockSide.Bottom => BottomToolbarHost,
                _ => TopToolbarHost,
            };

            // Reuse the view unless the side changed (orientation differs).
            if (!_toolbarViews.TryGetValue(toolbar.Id, out DockToolbarView? view)
                || _toolbarSides[toolbar.Id] != toolbar.Side)
            {
                view = new DockToolbarView(toolbar);
                view.DragRequested += (_, id) => ToolbarDragRequested?.Invoke(this, id);
                _toolbarViews[toolbar.Id] = view;
                _toolbarSides[toolbar.Id] = toolbar.Side;
            }

            host.Children.Add(view);
        }

        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }
}
