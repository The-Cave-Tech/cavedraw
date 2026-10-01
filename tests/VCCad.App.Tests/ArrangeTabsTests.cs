using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using VCCad.App.Views;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// Transform, Pathfinder and Align as **tabs of one panel**.
///
/// They are three views of the same thing - what to do to the selection - and they are used one at a time. Stacked
/// as three panels they took half the dock between them and pushed the list of objects off the bottom, so this is
/// tested on the shell itself: how many panels there are, which tabs each holds, and which one is showing.
/// </summary>
public class ArrangeTabsTests
{
    private static (EditorView View, Window Window) Host()
    {
        var view = new EditorView();
        var window = new Window { Width = 900, Height = 700, Content = view };
        window.Show();
        Settle();
        return (view, window);
    }

    private static void Settle()
    {
        for (int i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>
    /// **One panel, three tabs.** The count is the point: three panels is exactly what this issue is about, and an
    /// assertion that the tabs exist somewhere would pass with them in three panels.
    /// </summary>
    [AvaloniaFact]
    public void TheThreePanelsAreOnePanelWithThreeTabs()
    {
        (EditorView view, Window window) = Host();

        // Found by id rather than as "the pane holding several tabs": the colour panel holds five, so that filter
        // matched two panes - which is how this test was wrong the first time.
        var arrange = view.Panes.Where(p => p.Id == "arrange").ToArray();

        Assert.Single(arrange);
        Assert.Equal(new[] { "transform", "pathfinder", "align" },
            arrange[0].Tabs.Select(t => t.Id).ToArray());
        Assert.Equal(new[] { "Transform", "Pathfinder", "Align" },
            arrange[0].Tabs.Select(t => t.Title).ToArray());

        // And the three are no longer panels of their own.
        Assert.DoesNotContain(view.Panes, p => p.Id == "pathfinder" && p.Tabs.Length == 1);

        window.Close();
    }

    /// <summary>
    /// **The last tab used is the one showing when you come back.** A tab strip that reset to the first every time
    /// would make the two other tabs pointless, which is the whole complaint about them being separate panels.
    /// </summary>
    [AvaloniaFact]
    public void TheLastTabUsedIsTheOneShowing()
    {
        (EditorView view, Window window) = Host();

        view.SetPaneTab("Align");
        Assert.Equal("align", ActiveTab(view));

        view.SetPaneTab("Pathfinder");
        Assert.Equal("pathfinder", ActiveTab(view));

        // Asking for a tab that is already showing leaves it showing, rather than doing nothing visible.
        view.SetPaneTab("Pathfinder");
        Assert.Equal("pathfinder", ActiveTab(view));

        window.Close();
    }

    /// <summary>A tab can be selected by its title as well as its id, which is what a person reads on the strip.</summary>
    [AvaloniaFact]
    public void ATabCanBeSelectedByTitle()
    {
        (EditorView view, Window window) = Host();

        view.SetPaneTab("Align");
        Assert.Equal("align", ActiveTab(view));

        view.SetPaneTab("transform");
        Assert.Equal("transform", ActiveTab(view));

        window.Close();
    }

    /// <summary>**The panel hides and shows as one pane**, which three separate panels could not do.</summary>
    [AvaloniaFact]
    public void ThePanelHidesAndShowsAsOne()
    {
        (EditorView view, Window window) = Host();

        Assert.False(view.SetPaneOpen("arrange", false));
        Assert.DoesNotContain(view.Panes, p => p.Id == "arrange" && p.IsOpen);

        Assert.True(view.SetPaneOpen("arrange", true));

        // And it comes back with all three tabs, not just the one that was showing.
        var arrange = view.Panes.Single(p => p.Id == "arrange");
        Assert.Equal(3, arrange.Tabs.Length);
        Assert.All(arrange.Tabs, tab => Assert.True(tab.IsOpen));

        window.Close();
    }

    /// <summary>An unknown tab is refused with the list of what there is, rather than silently doing nothing.</summary>
    [AvaloniaFact]
    public void AnUnknownTabIsRefused()
    {
        (EditorView view, Window window) = Host();

        ArgumentException error = Assert.Throws<ArgumentException>(() => view.SetPaneTab("Nothing"));

        Assert.Contains("Unknown tab", error.Message, StringComparison.Ordinal);
        window.Close();
    }

    /// <summary>
    /// The tab showing in the **arrange** panel.
    ///
    /// Scoped to it rather than searching every pane: the colour panel has an active tab too, and the first version
    /// of this helper found that one and reported "colors" for every question about Align.
    /// </summary>
    private static string? ActiveTab(EditorView view)
        => view.Panes
            .Where(p => p.Id == "arrange")
            .SelectMany(p => p.Tabs)
            .FirstOrDefault(t => t.Active)?.Id;
}
