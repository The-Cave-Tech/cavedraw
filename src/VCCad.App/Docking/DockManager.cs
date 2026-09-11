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

    public DockManager(StackPanel leftHost, StackPanel rightHost, StackPanel topHost, StackPanel bottomHost)
    {
        LeftHost = leftHost;
        RightHost = rightHost;
        TopHost = topHost;
        BottomHost = bottomHost;
    }

    public StackPanel LeftHost { get; }

    public StackPanel RightHost { get; }

    public StackPanel TopHost { get; }

    public StackPanel BottomHost { get; }

    /// <summary>Raised whenever the layout changes (open/close/dock).</summary>
    public event EventHandler? LayoutChanged;

    /// <summary>Raised when a panel handle drag starts (id of the panel).</summary>
    public event EventHandler<string>? PanelDragRequested;

    /// <summary>Raised when a toolbar handle drag starts (id of the toolbar).</summary>
    public event EventHandler<string>? ToolbarDragRequested;

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

    /// <summary>Recreates the host contents from the current model.</summary>
    public void Build()
    {
        LeftHost.Children.Clear();
        RightHost.Children.Clear();
        TopHost.Children.Clear();
        BottomHost.Children.Clear();

        foreach (DockPanelModel panel in _panels.Values)
        {
            if (!panel.IsVisible)
            {
                continue;
            }

            StackPanel host = panel.Side == DockSide.Right ? RightHost : LeftHost;
            if (!_panelViews.TryGetValue(panel.Id, out DockTabPanelView? view))
            {
                view = new DockTabPanelView(panel, this);
                view.DragRequested += (_, id) => PanelDragRequested?.Invoke(this, id);
                _panelViews[panel.Id] = view;
            }

            view.Rebuild();
            host.Children.Add(view);
        }

        foreach (ToolbarModel toolbar in _toolbars.Values)
        {
            StackPanel host = toolbar.Side switch
            {
                DockSide.Right => RightHost,
                DockSide.Left => LeftHost,
                DockSide.Bottom => BottomHost,
                _ => TopHost,
            };
            var view = new DockToolbarView(toolbar);
            view.DragRequested += (_, id) => ToolbarDragRequested?.Invoke(this, id);
            host.Children.Add(view);
        }

        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }
}
