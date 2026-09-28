using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using VCCad.App.Docking;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// How stacked dock panels are sized, and what the separator between two of them does.
///
/// The separator's behaviour follows the arrangement, so these pin the row structure and
/// the fact that a separator is not offered where it could not move anything.
/// </summary>
public class DockStackLayoutTests
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

    private static (Grid Host, List<DockPanelModel> Panels) Build(params DockPanelModel[] panels)
    {
        var host = new Grid();
        DockStackLayout.Build(host, panels, _ => new Border(), () => { });
        return (host, panels.ToList());
    }

    [AvaloniaFact]
    public void ThreeStretchablePanelsGetTwoSeparatorsAndNoFiller()
    {
        (Grid host, _) = Build(Panel("a", true), Panel("b", true), Panel("c", true));

        // panel, separator, panel, separator, panel
        Assert.Equal(5, host.RowDefinitions.Count);
        Assert.Equal(5, host.Children.Count);

        // Every other row is a separator.
        Assert.IsType<Border>(host.Children[1]);
        Assert.IsType<Border>(host.Children[3]);
    }

    [AvaloniaFact]
    public void TwoFixedNeighboursGetNoSeparatorBetweenThem()
    {
        (Grid host, _) = Build(Panel("a", false), Panel("b", false));

        // Both are pinned, so a separator could not move anything: it is not offered.
        // Two panel rows and a trailing filler — no separator row between them.
        Assert.Equal(3, host.RowDefinitions.Count);
        Assert.Equal(2, host.Children.Count);

        // The leftover space goes to the filler so the panels stay packed at the top
        // rather than stretching to fill.
        Assert.Equal(GridUnitType.Star, host.RowDefinitions[2].Height.GridUnitType);
    }

    [AvaloniaFact]
    public void AFixedPanelUsesPixelsAndAStretchableOneUsesStars()
    {
        (Grid host, _) = Build(Panel("fixed", false, height: 300), Panel("loose", true));

        RowDefinition fixedRow = host.RowDefinitions[0];
        RowDefinition looseRow = host.RowDefinitions[2];

        Assert.Equal(GridUnitType.Pixel, fixedRow.Height.GridUnitType);
        Assert.Equal(300, fixedRow.Height.Value);
        Assert.Equal(GridUnitType.Star, looseRow.Height.GridUnitType);
    }

    [AvaloniaFact]
    public void ASeparatorIsOfferedBetweenAFixedAndAStretchablePanel()
    {
        (Grid host, _) = Build(Panel("fixed", false), Panel("loose", true));

        // One separator sits between them, because it can move the boundary.
        Assert.Equal(3, host.RowDefinitions.Count);
        Assert.Equal(3, host.Children.Count);
        Assert.IsType<Border>(host.Children[1]);
    }

    [AvaloniaFact]
    public void EveryPanelKeepsItsMinimumHeight()
    {
        (Grid host, _) = Build(Panel("a", true), Panel("b", true));

        Assert.All(host.RowDefinitions, row =>
        {
            if (row.MinHeight > 0)
            {
                Assert.Equal(DockPanelModel.MinimumHeight, row.MinHeight);
            }
        });
    }

    [AvaloniaFact]
    public void ASinglePanelGetsNoSeparator()
    {
        (Grid host, _) = Build(Panel("only", true));

        Assert.Single(host.Children);
        Assert.Single(host.RowDefinitions);
    }

    [AvaloniaFact]
    public void AnEmptyHostIsLeftAlone()
    {
        var host = new Grid();
        DockStackLayout.Build(host, Array.Empty<DockPanelModel>(), _ => new Border(), () => { });

        Assert.Empty(host.Children);
        Assert.Empty(host.RowDefinitions);

        // A single column remains so a later rebuild has something to fill.
        Assert.Single(host.ColumnDefinitions);
    }
}
