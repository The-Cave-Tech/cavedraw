using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using VCCad.App.Controls;
using VCCad.App.ViewModels;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// Wheel zoom and pan, sized for a **notched** wheel.
///
/// The bug this pins: a click on a notched wheel arrives as a *burst* of whole-notch events - the
/// diary from a real mouse shows six inside 100 ms, with 300 ms or more between clicks - and the
/// canvas applied a full step to every one of them. One click therefore zoomed by 1.1^6 and panned
/// six times as far, so the view flew and precision was impossible.
/// </summary>
public class WheelZoomTests
{
    /// <summary>The same event the automation layer raises, with a modifier.</summary>
    private static void Wheel(CanvasWorkspace workspace, double delta, KeyModifiers modifiers = KeyModifiers.None)
        => workspace.RaiseEvent(new PointerWheelEventArgs(
            workspace, null, workspace, new Point(200, 200), 0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.Other),
            modifiers, new Vector(0, delta)));

    private static (CanvasWorkspace Workspace, Window Window) Open()
    {
        var viewModel = new EditorViewModel();
        var workspace = new CanvasWorkspace();
        workspace.AttachEditor(viewModel);

        var window = new Window { Width = 900, Height = 700, Content = workspace };
        window.Show();
        Settle();
        return (workspace, window);
    }

    private static void Settle()
    {
        for (int i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    /// <summary>One click, however many events the driver reports it as, is one step.</summary>
    [AvaloniaFact]
    public void AClickOfTheWheelZoomsOnceHoweverManyEventsItArrivesAs()
    {
        (CanvasWorkspace workspace, Window window) = Open();
        double before = workspace.Zoom;

        for (int i = 0; i < 6; i++)
        {
            Wheel(workspace, 1.0, KeyModifiers.Control);
        }

        Assert.Equal(before * 1.1, workspace.Zoom, 3);
        window.Close();
    }

    /// <summary>
    /// A trackpad reports fractional deltas, one per frame, and those are meant to add up smoothly.
    /// Collapsing them the way a notched click is collapsed would break the smooth case the fix must
    /// not cost.
    /// </summary>
    [AvaloniaFact]
    public void ATrackpadsFractionalDeltasStillAccumulate()
    {
        (CanvasWorkspace workspace, Window window) = Open();
        double before = workspace.Zoom;

        for (int i = 0; i < 6; i++)
        {
            Wheel(workspace, 0.1, KeyModifiers.Control);
        }

        // Six tenths of a notch, so 1.1^0.6 - not six wholes, and not nothing.
        Assert.Equal(before * Math.Pow(1.1, 0.6), workspace.Zoom, 3);
        window.Close();
    }

    /// <summary>
    /// The pan half: a burst must move the page once, not six times.
    ///
    /// Measured against a single event rather than against an absolute number, because how far the
    /// view may travel is clamped by the artboard - so the question is not "where did it end up" but
    /// "did six events land in the same place as one", which holds whatever the clamp does.
    /// </summary>
    [AvaloniaFact]
    public void AClickOfTheWheelPansOnceHoweverManyEventsItArrivesAs()
    {
        double oneEventShift = PanShift(events: 1);
        double sixEventShift = PanShift(events: 6);

        Assert.NotEqual(0.0, oneEventShift);
        Assert.Equal(oneEventShift, sixEventShift, 1);
    }

    /// <summary>How far the view moves for <paramref name="events"/> whole-notch wheel events.</summary>
    private static double PanShift(int events)
    {
        (CanvasWorkspace workspace, Window window) = Open();
        workspace.WheelNotches.BurstGapMs = 60_000;

        // Where the model origin sits on screen is the view's scroll position, read the public way.
        Point before = workspace.ModelToWindow(new Point2D(0, 0));
        for (int i = 0; i < events; i++)
        {
            Wheel(workspace, -1.0);
        }

        Point after = workspace.ModelToWindow(new Point2D(0, 0));
        window.Close();
        return after.Y - before.Y;
    }

    /// <summary>Separate clicks are separate steps, which is what a deliberate spin has to give.</summary>
    [Fact]
    public void ClicksFurtherApartThanTheBurstGapEachCount()
    {
        var notches = new WheelNotches(burstGapMs: 60);

        Assert.True(notches.BeginsClick(1000));   // the first event of a click
        Assert.False(notches.BeginsClick(1010));  // and the burst it arrives as
        Assert.False(notches.BeginsClick(1032));
        Assert.False(notches.BeginsClick(1044));

        // A hand can turn a wheel about ten notches a second, and every one of those is its own.
        Assert.True(notches.BeginsClick(1350));
        Assert.False(notches.BeginsClick(1358));
        Assert.True(notches.BeginsClick(1450));
    }

    [Fact]
    public void TheBurstWindowIsWhatSeparatesOneClickFromTheNext()
    {
        var notches = new WheelNotches(burstGapMs: 60);

        Assert.True(notches.BeginsClick(0));
        Assert.False(notches.BeginsClick(60));   // exactly at the window: still the same click
        Assert.True(notches.BeginsClick(121));   // past it: a new one
    }

    [Fact]
    public void ResettingForgetsTheLastClick()
    {
        var notches = new WheelNotches(burstGapMs: 60);

        Assert.True(notches.BeginsClick(0));
        Assert.False(notches.BeginsClick(10));

        notches.Reset();

        Assert.True(notches.BeginsClick(15));
    }
}
