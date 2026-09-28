using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace VCCad.App.Docking;

/// <summary>
/// Lays stacked dock panels out as sized grid rows with a draggable separator between
/// neighbours, and makes each separator say what dragging it will actually do.
///
/// Three arrangements are possible between adjacent panels, and they behave differently:
///
///  * **Both stretchable** — neither has a height of its own; they share the leftover
///    space. Dragging rebalances the shares, so the boundary moves and both panels change.
///  * **One fixed, one stretchable** — the fixed panel has a height in pixels and the
///    stretchable one takes the remainder. Dragging changes the fixed panel's height.
///  * **Both fixed** — there is nothing left to give, so there is no separator at all.
///
/// The separator's appearance and tooltip differ in each case, because a separator that
/// looks the same while doing two different things is worse than none.
///
/// The drag is handled here rather than by <see cref="GridSplitter"/> so the gesture is
/// deterministic and can be driven through the automation API — a resize a person can
/// perform and a driver cannot is a capability-parity defect.
/// </summary>
public static class DockStackLayout
{
    /// <summary>How tall the separator itself is, in pixels.</summary>
    public const double GripSize = 6;

    /// <summary>Fills a panel host with rows for the given panels, in order.</summary>
    public static void Build(Grid host, IReadOnlyList<DockPanelModel> panels,
        Func<DockPanelModel, Control> viewFor, Action changed)
    {
        host.Children.Clear();
        host.RowDefinitions.Clear();
        host.ColumnDefinitions.Clear();
        host.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

        if (panels.Count == 0)
        {
            return;
        }

        bool anyStretchable = panels.Any(p => p.IsStretchable);
        var panelRows = new List<int>();

        for (int i = 0; i < panels.Count; i++)
        {
            DockPanelModel panel = panels[i];

            var row = new RowDefinition
            {
                MinHeight = DockPanelModel.MinimumHeight,
                Height = panel.IsStretchable
                    ? new GridLength(Math.Max(0.01, panel.Weight), GridUnitType.Star)
                    : new GridLength(Math.Max(DockPanelModel.MinimumHeight, panel.Height)),
            };

            host.RowDefinitions.Add(row);
            panelRows.Add(host.RowDefinitions.Count - 1);

            Control view = viewFor(panel);
            Grid.SetRow(view, panelRows[i]);
            host.Children.Add(view);

            if (i == panels.Count - 1)
            {
                break;
            }

            DockPanelModel next = panels[i + 1];

            // Both ends pinned means a separator here could not move anything, so none is
            // offered rather than one being offered that does nothing.
            if (!panel.IsStretchable && !next.IsStretchable)
            {
                continue;
            }

            host.RowDefinitions.Add(new RowDefinition(new GridLength(GripSize)));
            int splitterRow = host.RowDefinitions.Count - 1;
            int nextPanelRow = splitterRow + 1;

            Control separator = MakeSeparator(host, panel, next, panelRows[i], nextPanelRow, changed);
            Grid.SetRow(separator, splitterRow);
            host.Children.Add(separator);
        }

        // Nothing stretchable means the leftover space has nowhere to go; a trailing
        // star row keeps the panels packed at the top instead of stretched out.
        if (!anyStretchable)
        {
            host.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        }
    }

    private static Control MakeSeparator(Grid host, DockPanelModel above, DockPanelModel below,
        int aboveRow, int belowRow, Action changed)
    {
        bool proportional = above.IsStretchable && below.IsStretchable;

        string description = (above.IsStretchable, below.IsStretchable) switch
        {
            (true, true) =>
                $"{above.Title} and {below.Title} share the remaining space - drag to rebalance",
            (false, true) =>
                $"{above.Title} has a fixed height - drag to resize it",
            (true, false) =>
                $"{below.Title} has a fixed height - drag to resize it",
            _ => "Both panels are fixed",
        };

        var separator = new Border
        {
            Name = "DockSeparator",
            Height = GripSize,
            Background = proportional
                ? new SolidColorBrush(Color.FromRgb(0x4C, 0x9A, 0xFF), 0.45)
                : new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A), 0.45),
            Cursor = new Cursor(StandardCursorType.SizeNorthSouth),
        };

        ToolTip.SetTip(separator, description);

        double startY = 0;
        double startAbove = 0;
        double startBelow = 0;
        double startWeightAbove = 0;
        double startWeightBelow = 0;
        bool dragging = false;

        separator.PointerPressed += (_, e) =>
        {
            startY = e.GetPosition(host).Y;
            startAbove = host.RowDefinitions[aboveRow].ActualHeight;
            startBelow = host.RowDefinitions[belowRow].ActualHeight;
            startWeightAbove = above.Weight;
            startWeightBelow = below.Weight;
            dragging = true;
            e.Pointer.Capture(separator);
            e.Handled = true;
        };

        separator.PointerMoved += (_, e) =>
        {
            if (!dragging)
            {
                return;
            }

            ApplyDrag(host, above, below, aboveRow, belowRow, startAbove, startBelow,
                startWeightAbove + startWeightBelow, e.GetPosition(host).Y - startY);
            e.Handled = true;
        };

        separator.PointerReleased += (_, e) =>
        {
            if (!dragging)
            {
                return;
            }

            dragging = false;
            e.Pointer.Capture(null);
            CaptureRowSizes(host, above, below, aboveRow, belowRow);
            changed();
            e.Handled = true;
        };

        return separator;
    }

    /// <summary>
    /// Moves the boundary by <paramref name="delta"/> live, without touching the model
    /// until the drag ends.
    ///
    /// With both ends stretchable the pair is treated as a fixed number of pixels being
    /// divided differently; otherwise only the fixed panel's row changes and the
    /// stretchable neighbour absorbs the difference.
    /// </summary>
    private static void ApplyDrag(Grid host, DockPanelModel above, DockPanelModel below,
        int aboveRow, int belowRow, double startAbove, double startBelow,
        double pairWeight, double delta)
    {
        double total = startAbove + startBelow;
        if (total <= 0)
        {
            return;
        }

        double lowest = DockPanelModel.MinimumHeight;
        double highest = Math.Max(lowest, total - DockPanelModel.MinimumHeight);

        if (above.IsStretchable && below.IsStretchable)
        {
            double newAbove = Math.Clamp(startAbove + delta, lowest, highest);
            double newBelow = total - newAbove;

            // The two panels' combined weight is preserved, so panels further down the
            // stack keep their share. Handing the raw pixel counts in as weights would
            // silently rebalance every other stretchable panel in the host.
            double scale = pairWeight / total;
            above.Weight = Math.Max(0.01, newAbove * scale);
            below.Weight = Math.Max(0.01, newBelow * scale);

            host.RowDefinitions[aboveRow].Height = new GridLength(above.Weight, GridUnitType.Star);
            host.RowDefinitions[belowRow].Height = new GridLength(below.Weight, GridUnitType.Star);
            return;
        }

        if (above.IsStretchable)
        {
            double newBelow = Math.Clamp(startBelow - delta, lowest, highest);
            host.RowDefinitions[belowRow].Height = new GridLength(newBelow);
            return;
        }

        host.RowDefinitions[aboveRow].Height =
            new GridLength(Math.Clamp(startAbove + delta, lowest, highest));
    }

    /// <summary>
    /// Reads the settled row heights back into the models, so the arrangement survives the
    /// next rebuild of the host.
    ///
    /// A proportional split has already written both weights during the drag, so only the
    /// fixed cases need anything here.
    /// </summary>
    private static void CaptureRowSizes(Grid host, DockPanelModel above, DockPanelModel below,
        int aboveRow, int belowRow)
    {
        if (above.IsStretchable && below.IsStretchable)
        {
            return;
        }

        if (aboveRow >= host.RowDefinitions.Count || belowRow >= host.RowDefinitions.Count)
        {
            return;
        }

        double aboveHeight = host.RowDefinitions[aboveRow].ActualHeight;
        double belowHeight = host.RowDefinitions[belowRow].ActualHeight;

        if (aboveHeight <= 0 || belowHeight <= 0)
        {
            return;
        }

        if (above.IsStretchable)
        {
            below.Height = Math.Max(DockPanelModel.MinimumHeight, belowHeight);
            return;
        }

        above.Height = Math.Max(DockPanelModel.MinimumHeight, aboveHeight);
    }
}
