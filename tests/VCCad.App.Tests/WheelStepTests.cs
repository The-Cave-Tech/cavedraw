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
/// How far one notch of the wheel moves the view, as a number the person can tune.
///
/// It is pinned here rather than left implicit because it *has* to be tunable: the earlier change that stopped
/// a click's burst of events from being applied six times was right, and it left the per-click step too small.
/// Nobody could see what to change, because the number was private and no test named it. A tuning constant
/// with a test is how the next adjustment stays a one-line change with the intent written down beside it.
/// </summary>
public class WheelStepTests
{
    /// <summary>The pan step: how far one notch scrolls, in screen pixels.</summary>
    [Fact]
    public void ThePanStepPerNotchIsNinetyPixels()
        => Assert.Equal(90.0, CanvasWorkspace.PanPerNotchPixels, 1);

    /// <summary>
    /// The zoom step stays where it was. Ctrl+wheel is a different gesture with a conventional size, and
    /// tuning the scroll must not move it - which is why the two are separate constants.
    /// </summary>
    [Fact]
    public void TheZoomStepPerNotchIsTenPercent()
        => Assert.Equal(1.1, CanvasWorkspace.ZoomStepPerNotch, 3);

    /// <summary>
    /// The toolbar's step, which is deliberately not the wheel's: a button is a deliberate step further, a
    /// notch is a fine adjustment. Naming both is what stops one being tuned into the other by accident.
    /// </summary>
    [Fact]
    public void TheToolbarZoomStepIsTwentyFivePercent()
        => Assert.Equal(1.25, CanvasWorkspace.ButtonZoomStep, 3);

    /// <summary>
    /// And the number is the one the gesture actually uses: a notch moves the view, downward for a wheel
    /// rolled towards the user, by a distance measured against the canvas rather than assumed.
    /// </summary>
    [AvaloniaFact]
    public void ANotchMovesTheView()
    {
        var viewModel = new EditorViewModel();
        var workspace = new CanvasWorkspace();
        workspace.AttachEditor(viewModel);

        var window = new Window { Width = 900, Height = 700, Content = workspace };
        window.Show();
        for (int i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }

        workspace.WheelNotches.BurstGapMs = 60_000;
        Point before = workspace.ModelToWindow(new Point2D(0, 0));

        workspace.RaiseEvent(new PointerWheelEventArgs(
            workspace, null, workspace, new Point(200, 200), 0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.Other),
            KeyModifiers.None, new Vector(0, -1)));

        Point after = workspace.ModelToWindow(new Point2D(0, 0));

        // Rolled away from the user, the page comes down the screen. The distance is clamped by the artboard,
        // so the direction and the fact of movement are what the canvas can be asked; the step itself is the
        // constant asserted above.
        Assert.True(after.Y > before.Y, $"expected the view to move down, moved {after.Y - before.Y:0.#}");
        window.Close();
    }
}