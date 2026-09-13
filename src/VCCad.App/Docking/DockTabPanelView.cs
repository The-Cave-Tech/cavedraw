using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace VCCad.App.Docking;

/// <summary>
/// Window-like chrome for a dock panel: a drag handle (two slightly contrasting
/// vertical lines) on the left, a tab strip, and a down-arrow overflow menu on
/// the right for closing the active tab or the whole panel.
/// </summary>
public sealed class DockTabPanelView : Border
{
    /// <summary>Raised when the user starts dragging this panel by its handle.</summary>
    public event EventHandler<string>? DragRequested;

    private readonly DockPanelModel _panel;
    private readonly DockManager _manager;
    private readonly ContentControl _content = new();
    private readonly StackPanel _tabStrip = new() { Orientation = Orientation.Horizontal, Spacing = 2 };
    private readonly Dictionary<string, Control> _views = new();

    public DockTabPanelView(DockPanelModel panel, DockManager manager)
    {
        _panel = panel;
        _manager = manager;

        Background = new SolidColorBrush(Color.FromRgb(0x23, 0x23, 0x27));
        BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x42));
        BorderThickness = new Thickness(0, 0, 0, 1);
        CornerRadius = new CornerRadius(6);
        Margin = new Thickness(6, 6, 6, 0);
        ClipToBounds = true;

        // No title text — the tab strip is the identity (saves vertical space).
        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2F)),
        };

        header.Children.Add(BuildDragHandle());

        var tabScroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        };
        tabScroll.Content = _tabStrip;
        Grid.SetColumn(tabScroll, 1);
        header.Children.Add(tabScroll);

        header.Children.Add(BuildOverflow());
        header.Height = 24;

        var root = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(_content);
        Child = root;

        Rebuild();
    }

    private Control BuildDragHandle()
    {
        var lines = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        lines.Children.Add(new Border { Width = 2, Height = 15, Background = new SolidColorBrush(Color.FromRgb(0x6E, 0x6E, 0x78)), CornerRadius = new CornerRadius(1) });
        lines.Children.Add(new Border { Width = 2, Height = 15, Background = new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x52)), CornerRadius = new CornerRadius(1) });

        var handle = new Border
        {
            Padding = new Thickness(8, 0),
            Background = Brushes.Transparent,
            Child = lines,
            Cursor = new Cursor(StandardCursorType.SizeAll),
        };
        handle.PointerPressed += (_, e) =>
        {
            DragRequested?.Invoke(this, _panel.Id);
            e.Handled = true;
        };
        return handle;
    }

    private Control BuildOverflow()
    {
        var button = new Button
        {
            Content = "⌄",
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE9)),
            FontSize = EditorTheme.FontSize + 1,
            Padding = new Thickness(8, 0),
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        var flyout = new MenuFlyout();
        flyout.Items.Add(new MenuItem
        {
            Header = "Close tab",
        });
        flyout.Items.Add(new MenuItem
        {
            Header = "Close panel",
        });
        button.Flyout = flyout;

        // MenuFlyout items are rebuilt on open so they target the current tab.
        flyout.Opening += (_, _) =>
        {
            flyout.Items.Clear();
            var closeTab = new MenuItem { Header = "Close tab", IsEnabled = _panel.ActiveTab is not null };
            closeTab.Click += (_, _) =>
            {
                if (_panel.ActiveTab is { } tab)
                {
                    _manager.SetTabOpen(tab.Id, false);
                }
            };
            flyout.Items.Add(closeTab);

            var closePanel = new MenuItem { Header = "Close panel" };
            closePanel.Click += (_, _) => _manager.ClosePanel(_panel.Id);
            flyout.Items.Add(closePanel);
        };

        Grid.SetColumn(button, 3);
        return button;
    }

    /// <summary>Rebuilds the tab strip and swaps in the active tab's content.</summary>
    public void Rebuild()
    {
        _tabStrip.Children.Clear();
        foreach (DockTab tab in _panel.OpenTabs)
        {
            bool active = tab.Id == _panel.ActiveTab?.Id;
            var button = new Button
            {
                Content = tab.Title,
                FontSize = EditorTheme.FontSize,
                Padding = new Thickness(8, 2),
                Background = active
                    ? new SolidColorBrush(Color.FromRgb(0x2B, 0x4C, 0x7E))
                    : Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = new SolidColorBrush(active
                    ? Color.FromRgb(0xE6, 0xE6, 0xE9)
                    : Color.FromRgb(0x9A, 0x9A, 0xA3)),
                CornerRadius = new CornerRadius(5),
            };
            string id = tab.Id;
            button.Click += (_, _) =>
            {
                _panel.ActiveTabId = id;
                Rebuild();
            };
            _tabStrip.Children.Add(button);
        }

        DockTab? current = _panel.ActiveTab;
        if (current is null)
        {
            _content.Content = null;
            return;
        }

        _panel.ActiveTabId = current.Id;
        if (!_views.TryGetValue(current.Id, out Control? view))
        {
            view = current.ContentFactory();
            _views[current.Id] = view;
        }

        _content.Content = view;
    }
}
