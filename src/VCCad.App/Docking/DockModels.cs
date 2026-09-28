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
    /// <summary>The smallest a panel may be dragged down to, in pixels.</summary>
    public const double MinimumHeight = 48;

    public required string Id { get; init; }

    public required string Title { get; init; }

    public DockSide Side { get; set; } = DockSide.Left;

    public List<DockTab> Tabs { get; } = new();

    public string? ActiveTabId { get; set; }

    /// <summary>
    /// Whether the panel may take up slack left by the others.
    ///
    /// This is what the separator between two panels expresses. A stretchable panel has
    /// no fixed height — it claims a share of whatever is left over, and dragging the
    /// separator rebalances the shares. A fixed panel keeps a height in pixels and
    /// dragging the separator changes *that* number instead. Neither arrangement is
    /// better; the point is that the separator says which one you are looking at, so
    /// dragging it does what it looked like it would do.
    /// </summary>
    public bool IsStretchable { get; set; } = true;

    /// <summary>
    /// The panel's height in pixels. Its actual size when fixed; remembered as the
    /// starting size if it is later made stretchable, and updated when a neighbour's
    /// separator is dragged.
    /// </summary>
    public double Height { get; set; } = 240;

    /// <summary>
    /// The share of the leftover space a stretchable panel takes, relative to the other
    /// stretchable panels beside it. Only meaningful while <see cref="IsStretchable"/>.
    /// </summary>
    public double Weight { get; set; } = 1;

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
