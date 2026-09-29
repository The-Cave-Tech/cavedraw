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
using GradientStop = VCCad.Core.Model.GradientStop;

namespace VCCad.App.Tests;

/// <summary>
/// The gradient ramp's pointer routing, driven by real pointer events through the real hit test.
///
/// The case that matters most is the boring-looking one: the control paints a strip and leaves
/// padding above and below it, and a control with no background is hit only where it paints. The
/// padding click fails silently when the background is missing - no exception, no visual clue -
/// so it is pinned here exactly as it was for the fill/stroke selector.
/// </summary>
public class GradientRampTests
{
    private const double Width = 200.0;
    private const double Height = 44.0;

    private static GradientSpec Ramp() => new()
    {
        Stops = new[]
        {
            new GradientStop(0.0, ColorRgb.Red),
            new GradientStop(1.0, ColorRgb.Blue),
        },
    };

    private static Window Host(GradientRamp ramp)
    {
        var host = new Panel
        {
            Width = Width,
            Height = Height,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
        };
        host.Children.Add(ramp);

        var window = new Window { Width = 320, Height = 160, Content = host };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static Point InWindow(Window window, Control control, double x, double y)
    {
        Point? point = control.TranslatePoint(new Point(x, y), window);
        Assert.NotNull(point);
        return point!.Value;
    }

    [AvaloniaFact]
    public void AClickInTheUnpaintedPaddingReachesTheControl()
    {
        var ramp = new GradientRamp { Spec = Ramp() };
        Window window = Host(ramp);
        try
        {
            bool sawPress = false;
            ramp.AddHandler(
                InputElement.PointerPressedEvent,
                (EventHandler<PointerPressedEventArgs>)((_, _) => sawPress = true),
                RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
                handledEventsToo: true);

            // Two pixels from the top: above the strip, where nothing is painted.
            Point point = InWindow(window, ramp, Width / 2, 2);
            string reached = InputInjection.Click(window, point.X, point.Y, clickCount: 1, shift: false);

            Assert.Contains(nameof(GradientRamp), reached, StringComparison.Ordinal);
            Assert.True(sawPress, "a click in the ramp's own padding must reach the ramp");
            Assert.Equal(3, ramp.Spec.Stops.Count);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void AClickOnTheStripAddsAStopWhereItWasClicked()
    {
        var ramp = new GradientRamp { Spec = Ramp() };
        Window window = Host(ramp);
        try
        {
            Rect strip = ramp.StripBounds;
            var commits = new List<GradientSpec>();
            ramp.SpecCommitted += (_, spec) => commits.Add(spec);

            Point point = InWindow(window, ramp, strip.X + (strip.Width * 0.25), strip.Y + (strip.Height / 2));
            InputInjection.Click(window, point.X, point.Y, clickCount: 1, shift: false);

            Assert.Equal(3, ramp.Spec.Stops.Count);
            GradientStop added = ramp.Spec.Stops[1];
            Assert.InRange(added.Position, 0.24, 0.26);
            Assert.Equal(1, ramp.SelectedIndex);
            Assert.Single(commits);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void AClickOnAMarkerSelectsItInsteadOfAddingAStop()
    {
        var ramp = new GradientRamp { Spec = Ramp() };
        Window window = Host(ramp);
        try
        {
            // The marker for the first stop sits at the left end of the strip.
            Point marker = new(ramp.StripBounds.X, ramp.StripBounds.Y);
            Point point = InWindow(window, ramp, marker.X, marker.Y);
            InputInjection.Click(window, point.X, point.Y, clickCount: 1, shift: false);

            Assert.Equal(2, ramp.Spec.Stops.Count);
            Assert.Equal(0, ramp.SelectedIndex);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void AMarkerAtTheEndOfTheStripIsStillAMarker()
    {
        var ramp = new GradientRamp { Spec = Ramp() };
        Window window = Host(ramp);
        try
        {
            // The last marker is on the strip as well: adding a stop there instead of selecting
            // would make the end stops impossible to grab.
            Point marker = new(ramp.StripBounds.Right, ramp.StripBounds.Y);
            Point point = InWindow(window, ramp, marker.X, marker.Y);
            InputInjection.Click(window, point.X, point.Y, clickCount: 1, shift: false);

            Assert.Equal(2, ramp.Spec.Stops.Count);
            Assert.Equal(1, ramp.SelectedIndex);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void DraggingAMarkerMovesThatStopAndAddsNothing()
    {
        var ramp = new GradientRamp { Spec = Ramp() };
        Window window = Host(ramp);
        try
        {
            Point from = InWindow(window, ramp, ramp.StripBounds.X, ramp.StripBounds.Y);
            Point to = InWindow(window, ramp, ramp.StripBounds.X + (ramp.StripBounds.Width * 0.5), ramp.StripBounds.Y);

            InputInjection.Press(window, from.X, from.Y, shift: false);
            InputInjection.Move(window, to.X, to.Y, leftDown: true);
            InputInjection.Release(window, to.X, to.Y);

            Assert.Equal(2, ramp.Spec.Stops.Count);
            Assert.InRange(ramp.Spec.Stops[0].Position, 0.48, 0.52);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void DoubleClickingAMarkerAsksForTheColourPicker()
    {
        var ramp = new GradientRamp { Spec = Ramp() };
        Window window = Host(ramp);
        try
        {
            int activated = -1;
            ramp.StopActivated += (_, index) => activated = index;

            Point marker = InWindow(window, ramp, ramp.StripBounds.X, ramp.StripBounds.Y);
            InputInjection.Click(window, marker.X, marker.Y, clickCount: 2, shift: false);

            Assert.Equal(0, activated);
            Assert.Equal(2, ramp.Spec.Stops.Count);
        }
        finally
        {
            window.Close();
        }
    }
}
