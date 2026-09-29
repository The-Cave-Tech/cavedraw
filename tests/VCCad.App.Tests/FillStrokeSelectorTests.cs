using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using VCCad.App.Automation;
using VCCad.App.Controls;
using VCCad.Core.Model;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// The fill/stroke target diagram is a custom control that paints a few shapes and takes
/// clicks on them. Two routing rules keep it honest, and both are easy to regress because
/// neither shows up in a screenshot:
///
/// <list type="bullet">
/// <item>the circles own their hit areas — the swap arc is only considered when the click is
/// outside all three, even though the arc's own hit disc overlaps the fill circle's;</item>
/// <item>the control must accept a click anywhere in its rectangle, including the corners
/// where it paints nothing. A control with no background is only hit where it paints, so the
/// click fell through to the ScrollContentPresenter and did nothing — silently, with no
/// error anywhere.</item>
/// </list>
/// </summary>
public class FillStrokeSelectorTests
{
    private const double Size = 56.0;

    // The control's private layout ratios, restated here on purpose: if the layout moves,
    // these tests should be re-derived rather than silently keep passing against the old
    // numbers.
    private static Point StrokeCentre => new(Size * 0.36, Size * 0.36);
    private static Point FillCentre => new(Size * 0.64, Size * 0.64);
    private static Point SwapCentre => new(Size * 0.78, Size * 0.22);
    private static double CircleHitRadius => Size * 0.30 * 1.2;
    private static double SwapHitRadius => Size * 0.22;

    private static double Distance(Point a, Point b)
    {
        Vector d = a - b;
        return d.Length;
    }

    /// <summary>
    /// Hosts the selector at its real size in a headless window, with a settled layout and
    /// paint pass: the click path needs a TopLevel, laid-out bounds and an applied z-order.
    /// </summary>
    private static Window Host(FillStrokeSelector selector)
    {
        var host = new Panel
        {
            Width = Size,
            Height = Size,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
        };
        host.Children.Add(selector);

        var window = new Window
        {
            Width = 200,
            Height = 120,
            Content = host,
        };

        window.Show();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    /// <summary>
    /// Clicks at a point in the control's own coordinates, through the real hit test, and
    /// returns what the injection says it reached.
    /// </summary>
    private static string ClickLocal(Window window, Control control, double x, double y)
    {
        Point? point = control.TranslatePoint(new Point(x, y), window);
        Assert.NotNull(point);
        return InputInjection.Click(window, point!.Value.X, point.Value.Y, clickCount: 1, shift: false);
    }

    /// <summary>A point inside the fill circle's hit area and inside the arc's hit disc.</summary>
    private static Point OverlapPoint()
    {
        Vector toSwap = SwapCentre - FillCentre;
        double span = toSwap.Length;

        // Just inside the arc's disc, which is still comfortably inside the circle's.
        double fromFill = span - (SwapHitRadius - 1.0);
        return FillCentre + toSwap * (fromFill / span);
    }

    [AvaloniaFact]
    public void AClickInsideTheArcRaisesSwapRequested()
    {
        var selector = new FillStrokeSelector();
        Window window = Host(selector);
        try
        {
            // The arc's centre is clear of every circle, so this click belongs to the arc.
            Assert.True(Distance(SwapCentre, FillCentre) > CircleHitRadius);
            Assert.True(Distance(SwapCentre, StrokeCentre) > CircleHitRadius);

            int swaps = 0;
            selector.SwapRequested += (_, _) => swaps++;

            string reached = ClickLocal(window, selector, SwapCentre.X, SwapCentre.Y);

            Assert.Contains(nameof(FillStrokeSelector), reached, StringComparison.Ordinal);
            Assert.Equal(1, swaps);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void AClickOnACircleInsideTheArcsReachStillPicksTheCircle()
    {
        var selector = new FillStrokeSelector();
        selector.SetState(ColorRgb.White, true, ColorRgb.Black, true, strokeSelected: true);
        Window window = Host(selector);
        try
        {
            Point overlap = OverlapPoint();

            // This is the interesting point: it is inside the arc's hit disc *and* inside the
            // fill circle's hit area. The circle must win.
            Assert.True(Distance(overlap, SwapCentre) <= SwapHitRadius, "test point is not in the arc's reach");
            Assert.True(Distance(overlap, FillCentre) <= CircleHitRadius, "test point is not on the fill circle");

            int swaps = 0;
            selector.SwapRequested += (_, _) => swaps++;

            ClickLocal(window, selector, overlap.X, overlap.Y);

            Assert.Equal(0, swaps);
            Assert.False(selector.StrokeSelected, "the click should have selected the fill, not swapped");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void AClickInAnUnpaintedCornerReachesTheControl()
    {
        var selector = new FillStrokeSelector();
        Window window = Host(selector);
        try
        {
            bool sawPress = false;
            selector.AddHandler(
                InputElement.PointerPressedEvent,
                (EventHandler<PointerPressedEventArgs>)((_, _) => sawPress = true),
                RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
                handledEventsToo: true);

            // (1,1) is outside every drawn shape: without a background nothing is painted
            // there, so the click used to fall straight through to the scroll presenter.
            string reached = ClickLocal(window, selector, 1.0, 1.0);

            Assert.Contains(nameof(FillStrokeSelector), reached, StringComparison.Ordinal);
            Assert.True(sawPress, "a click in the control's own corner must reach the control");
        }
        finally
        {
            window.Close();
        }
    }
}
