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
        Dictionary<int, double> startStars = new();
        bool dragging = false;

        separator.PointerPressed += (_, e) =>
        {
            startY = e.GetPosition(host).Y;
            startAbove = host.RowDefinitions[aboveRow].ActualHeight;
            startBelow = host.RowDefinitions[belowRow].ActualHeight;
            startWeightAbove = above.Weight;
            startWeightBelow = below.Weight;
            startStars = StarHeights(host);
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
                startWeightAbove + startWeightBelow, startStars, e.GetPosition(host).Y - startY);
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
    /// The height every stretchable row has right now, by row index.
    ///
    /// Taken when the drag begins, because it is what the rows that are <em>not</em> being
    /// dragged have to be put back to.
    /// </summary>
    internal static Dictionary<int, double> StarHeights(Grid host)
    {
        var heights = new Dictionary<int, double>();

        for (int i = 0; i < host.RowDefinitions.Count; i++)
        {
            RowDefinition row = host.RowDefinitions[i];
            if (row.Height.IsStar && row.ActualHeight > 0)
            {
                heights[i] = row.ActualHeight;
            }
        }

        return heights;
    }

    /// <summary>
    /// Gives the stretchable rows the heights they are meant to have, and leaves every other
    /// stretchable row exactly where it was.
    ///
    /// Star rows divide the slack between them, so resizing a fixed panel moves all of them:
    /// dragging the bar above the Objects panel shrank Objects <em>and</em> the panel below
    /// it. Writing each row's own height back as its weight pins the ones the drag does not
    /// touch, which is what dragging a bar between two panels is supposed to do.
    /// </summary>
    internal static void SetStarHeights(Grid host, IReadOnlyDictionary<int, double> desired)
    {
        foreach ((int row, double height) in desired)
        {
            if (row >= 0 && row < host.RowDefinitions.Count)
            {
                host.RowDefinitions[row].Height =
                    new GridLength(Math.Max(0.01, height), GridUnitType.Star);
            }
        }
    }

    /// <summary>
    /// Moves the boundary by <paramref name="delta"/> live, without touching the model
    /// until the drag ends.
    ///
    /// The row either side of the bar takes the whole change and every other stretchable row
    /// keeps its height, so a bar moves its own boundary and nothing else. Only the panels
    /// either side of it change.
    /// </summary>
    internal static void ApplyDrag(Grid host, DockPanelModel above, DockPanelModel below,
        int aboveRow, int belowRow, double startAbove, double startBelow,
        double pairWeight, IReadOnlyDictionary<int, double> startStars, double delta)
    {
        double lowest = DockPanelModel.MinimumHeight;
        var desired = new Dictionary<int, double>(startStars);

        if (above.IsStretchable && below.IsStretchable)
        {
            double total = startAbove + startBelow;
            if (total <= 0)
            {
                return;
            }

            double highest = Math.Max(lowest, total - lowest);
            double newAbove = Math.Clamp(startAbove + delta, lowest, highest);
            double newBelow = total - newAbove;

            // The pair's combined weight is preserved, so panels elsewhere in the stack keep
            // their share. Handing the raw pixel counts in as weights would silently
            // rebalance every other stretchable panel in the host.
            double scale = pairWeight / total;
            above.Weight = Math.Max(0.01, newAbove * scale);
            below.Weight = Math.Max(0.01, newBelow * scale);

            desired[aboveRow] = newAbove;
            desired[belowRow] = newBelow;
            SetStarHeights(host, desired);
            return;
        }

        double span = Math.Max(lowest, startAbove + startBelow - lowest);

        if (above.IsStretchable)
        {
            // The panel below is fixed: it takes the drag, and the panel above absorbs the
            // difference so nothing beyond this bar moves.
            double newBelow = Math.Clamp(startBelow - delta, lowest, span);
            host.RowDefinitions[belowRow].Height = new GridLength(newBelow);
            desired[aboveRow] = Math.Max(lowest, startAbove + (startBelow - newBelow));
            SetStarHeights(host, desired);
            return;
        }

        double newAboveFixed = Math.Clamp(startAbove + delta, lowest, span);
        host.RowDefinitions[aboveRow].Height = new GridLength(newAboveFixed);

        if (startStars.ContainsKey(belowRow))
        {
            desired[belowRow] = Math.Max(lowest, startBelow + (startAbove - newAboveFixed));
            SetStarHeights(host, desired);
        }
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
