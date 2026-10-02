using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VCCad.App.Automation;
using VCCad.App.Views;
using VCCad.App.Views.Panes;
using VCCad.Core.Model;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// How long the colour pane's subscription to the colour state lasts.
///
/// <see cref="EditorColorState.Shared"/> is process-wide and its <c>Changed</c> event is raised from
/// anywhere - a driver's <c>color.set</c>, the ring, the eyedropper. The pane listens to it so that a
/// colour set from outside the panel still reaches the picker and the document, and that subscription
/// used to be taken on <c>Attach</c> and never given up: the panel's teardown was an explicit
/// <c>Detach</c> that no product path called, so every pane built over the life of the process stayed
/// on the list and kept working. This is the lifetime half of the defect whose thread half was #178.
///
/// Both halves of the behaviour are pinned here, because either one alone can be satisfied by a
/// subscription that does not work:
///
/// - a pane that has been put away must have **stopped acting** - the assertion a <c>Detach</c> that
///   nothing calls cannot satisfy; and
/// - a pane that is brought back must be **live again**. Unsubscribing without re-subscribing would
///   close the leak by quietly killing the picker, which is worse than the leak.
/// </summary>
public class ColorsPaneLifetimeTests : IDisposable
{
    private readonly ColorRgb _wasColour = EditorColorState.Shared.Color;
    private readonly ColorRgb? _wasPicked = EditorColorState.Shared.LastPicked;

    private static (EditorView View, Window Window) Host()
    {
        var view = new EditorView();
        var window = new Window { Width = 1100, Height = 800, Content = view };
        window.Show();
        Settle();
        return (view, window);
    }

    /// <summary>
    /// The dock attaches the tab's view when the tab becomes the showing one and drops the previous
    /// one, which is the gesture a person makes on the tab strip. That is the product path this test
    /// removes the pane by - not a method on the pane.
    /// </summary>
    [AvaloniaFact]
    public void APanePutAwayStopsFollowingTheSharedColourState()
    {
        (EditorView view, Window window) = Host();

        try
        {
            // The premise: the pane is in the shell and listening, so an external colour set reaches
            // the document through it. Without this, "it did not act afterwards" would also be true of
            // a pane that was never there.
            Assert.NotNull(LivePane(view));
            EditorColorState.Shared.SetColor(ColorRgb.FromBytes(200, 40, 60));
            AssertClose(ColorRgb.FromBytes(200, 40, 60), view.ViewModel.CurrentFill.Color);

            // Away from the Color tab. The pane is out of the visual tree; its subscription must have
            // gone with it, because the state it listens to belongs to the process, not to the pane.
            view.SetPaneTab("stroke");
            Settle();

            ColorRgb before = view.ViewModel.CurrentFill.Color;

            EditorColorState.Shared.SetColor(ColorRgb.FromBytes(10, 200, 30));

            AssertClose(before, view.ViewModel.CurrentFill.Color);
        }
        finally
        {
            window.Close();
            Settle();
        }
    }

    /// <summary>
    /// The behaviour a person depends on, and the trap in the fix: a pane taken out of the tab strip
    /// and brought back must follow the colour state again. A release that is not undone on re-attach
    /// leaves a picker that looks live and answers nothing.
    /// </summary>
    [AvaloniaFact]
    public void APaneBroughtBackStillFollowsTheSharedColourState()
    {
        (EditorView view, Window window) = Host();

        try
        {
            view.SetPaneTab("stroke");
            Settle();
            view.SetPaneTab("colors");
            Settle();

            EditorColorState.Shared.SetColor(ColorRgb.FromBytes(12, 34, 56));

            AssertClose(ColorRgb.FromBytes(12, 34, 56), view.ViewModel.CurrentFill.Color);
        }
        finally
        {
            window.Close();
            Settle();
        }
    }

    /// <summary>The colour pane showing in the shell, or null when it is not in the visual tree.</summary>
    private static ColorsPane? LivePane(EditorView view)
        => view.GetVisualDescendants().OfType<ColorsPane>().FirstOrDefault();

    /// <summary>
    /// The colour is stored as 0..1 doubles, so a byte round-trip is exact only to a few places.
    /// </summary>
    private static void AssertClose(ColorRgb expected, ColorRgb actual)
    {
        Assert.Equal(expected.R, actual.R, 3);
        Assert.Equal(expected.G, actual.G, 3);
        Assert.Equal(expected.B, actual.B, 3);
    }

    private static void Settle()
    {
        for (int i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }

    public void Dispose()
    {
        // The colour state is process-wide, so a test that writes it puts it back.
        EditorColorState.Shared.RestorePicked(_wasPicked);
        EditorColorState.Shared.SetColor(_wasColour);
        GC.SuppressFinalize(this);
    }
}
