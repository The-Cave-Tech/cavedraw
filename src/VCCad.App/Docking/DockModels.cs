using Avalonia.Controls;

namespace VCCad.App.Docking;

/// <summary>
/// A tab that can live inside a dock panel. Content is created lazily so a closed
/// tab holds no UI state; opening it builds a fresh view bound to the editor.
/// </summary>
public sealed class DockTab
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    /// <summary>Panel this tab belongs to by default (Windows menu reopens here).</summary>
    public required string PanelId { get; init; }

    public required DockSide DefaultSide { get; init; }

    public required Func<Control> ContentFactory { get; init; }

    /// <summary>Whether the tab is currently open (visible in its panel).</summary>
    public bool IsOpen { get; set; }
}

/// <summary>A window-like panel containing one or more tabs, docked left/right.</summary>
public sealed class DockPanelModel
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public DockSide Side { get; set; } = DockSide.Left;

    public List<DockTab> Tabs { get; } = new();

    public string? ActiveTabId { get; set; }

    public bool IsVisible => Tabs.Any(t => t.IsOpen);

    public DockTab? ActiveTab
        => Tabs.FirstOrDefault(t => t.Id == ActiveTabId && t.IsOpen)
           ?? Tabs.FirstOrDefault(t => t.IsOpen);

    public IEnumerable<DockTab> OpenTabs => Tabs.Where(t => t.IsOpen);
}

/// <summary>A dockable toolbar (horizontal on top/bottom, vertical on left/right).</summary>
public sealed class ToolbarModel
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public DockSide Side { get; set; } = DockSide.Top;

    public required Func<Control> ContentFactory { get; init; }
}
