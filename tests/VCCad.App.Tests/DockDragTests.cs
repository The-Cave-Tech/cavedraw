using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using VCCad.App.Docking;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// Dragging a separator moves the boundary it is on, and nothing else.
///
/// Star rows divide the slack between them, so resizing a fixed panel used to move every
/// stretchable panel below it as well: dragging the bar above the Objects panel shrank
/// Objects and the panel under it together. Only the two panels either side of the bar
/// should change.
///
/// The host is laid out for real here, because the arithmetic reads row heights and there is
/// nothing to read without them.
/// </summary>
public class DockDragTests
{
    private static DockPanelModel Panel(string id, bool stretchable, double height = 240)
    {
        var panel = new DockPanelModel { Id = id, Title = id, Side = DockSide.Right };
        panel.Tabs.Add(new DockTab
        {
            Id = id,
            Title = id,
            PanelId = id,
            DefaultSide = DockSide.Right,
            ContentFactory = () => new Border(),
            IsOpen = true,
        });

        panel.IsStretchable = stretchable;
        panel.Height = height;
        return panel;
    }

    /// <summary>Fixed, then two stretchable — the arrangement the report came from.</summary>
    private static (Grid Host, DockPanelModel Fixed, DockPanelModel First, DockPanelModel Second) Build()
    {
        DockPanelModel pinned = Panel("pinned", false, height: 200);
        DockPanelModel first = Panel("first", true);
        DockPanelModel second = Panel("second", true);

        var host = new Grid();
        DockStackLayout.Build(host, new[] { pinned, first, second }, _ => new Border(), () => { });

        const double height = 1000;
        host.Width = 272;
        host.Height = height;
        host.Measure(new Size(272, height));
        host.Arrange(new Rect(0, 0, 272, height));

        return (host, pinned, first, second);
    }

    /// <summary>
    /// Runs the layout pass the grid needs before its rows have new heights.
    ///
    /// ApplyDrag writes row definitions; a definition's ActualHeight only exists after the
    /// next measure and arrange. Asserting without this reads the heights from before the
    /// drag and passes whatever it is given.
    /// </summary>
    private static void Lay(Grid host)
    {
        host.Measure(new Size(272, host.Height));
        host.Arrange(new Rect(0, 0, 272, host.Height));
    }

    /// <summary>The rows holding panels, skipping the separators between them.</summary>
    private static double[] PanelRows(Grid host) => host.RowDefinitions
        .Where(r => r.Height.GridUnitType == GridUnitType.Star ||
                    r.Height.GridUnitType == GridUnitType.Pixel)
        .Select(r => r.ActualHeight)
        .ToArray();

    [AvaloniaFact]
    public void TheDragMovesOnlyTheTwoPanelsItIsBetween()
    {
        (Grid host, DockPanelModel pinned, DockPanelModel first, DockPanelModel second) = Build();

        // Rows: pinned(0), separator(1), first(2), separator(3), second(4).
        Assert.True(second.Weight > 0);

        double row0 = host.RowDefinitions[0].ActualHeight;
        double row2 = host.RowDefinitions[2].ActualHeight;
        double row4 = host.RowDefinitions[4].ActualHeight;
        Assert.True(row0 > 0 && row2 > 0 && row4 > 0, $"rows did not lay out: {row0} {row2} {row4}");

        // Drag the bar between the pinned panel and the first stretchable one, downwards:
        // the pinned panel grows and the first absorbs it.
        DockStackLayout.ApplyDrag(host, pinned, first, 0, 2, row0, row2,
            pinned.Weight + first.Weight, DockStackLayout.StarHeights(host), delta: 60);
        Lay(host);

        // The panel two rows below has to be exactly where it was.
        Assert.Equal(row4, host.RowDefinitions[4].ActualHeight, 1);
    }

    [AvaloniaFact]
    public void TheNeighbourTakesTheWholeChange()
    {
        (Grid host, DockPanelModel pinned, DockPanelModel first, _) = Build();

        double row0 = host.RowDefinitions[0].ActualHeight;
        double row2 = host.RowDefinitions[2].ActualHeight;

        DockStackLayout.ApplyDrag(host, pinned, first, 0, 2, row0, row2,
            pinned.Weight + first.Weight, DockStackLayout.StarHeights(host), delta: 60);
        Lay(host);

        // The pinned panel is 60 taller, and its neighbour 60 shorter: the change goes
        // somewhere, and it goes next door.
        Assert.Equal(row0 + 60, host.RowDefinitions[0].ActualHeight, 1);
        Assert.Equal(row2 - 60, host.RowDefinitions[2].ActualHeight, 1);
    }

    [AvaloniaFact]
    public void ADragBetweenTwoStretchablePanelsMovesNeitherOfTheOthers()
    {
        (Grid host, _, DockPanelModel first, DockPanelModel second) = Build();

        double row0 = host.RowDefinitions[0].ActualHeight;
        double row2 = host.RowDefinitions[2].ActualHeight;
        double row4 = host.RowDefinitions[4].ActualHeight;

        DockStackLayout.ApplyDrag(host, first, second, 2, 4, row2, row4,
            first.Weight + second.Weight, DockStackLayout.StarHeights(host), delta: 80);
        Lay(host);

        Assert.Equal(row0, host.RowDefinitions[0].ActualHeight, 1);
        Assert.Equal(row2 + 80, host.RowDefinitions[2].ActualHeight, 1);
        Assert.Equal(row4 - 80, host.RowDefinitions[4].ActualHeight, 1);
    }

    [AvaloniaFact]
    public void ADragCannotSquashAPanelBelowItsMinimum()
    {
        (Grid host, DockPanelModel pinned, DockPanelModel first, _) = Build();

        double row0 = host.RowDefinitions[0].ActualHeight;
        double row2 = host.RowDefinitions[2].ActualHeight;

        // Far more than the neighbour has.
        DockStackLayout.ApplyDrag(host, pinned, first, 0, 2, row0, row2,
            pinned.Weight + first.Weight, DockStackLayout.StarHeights(host), delta: 100000);
        Lay(host);

        Assert.True(host.RowDefinitions[2].ActualHeight >= DockPanelModel.MinimumHeight,
            $"the neighbour was squashed to {host.RowDefinitions[2].ActualHeight}");
    }
}
