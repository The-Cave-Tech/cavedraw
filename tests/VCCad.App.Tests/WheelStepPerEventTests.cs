using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using VCCad.App.Controls;
using VCCad.App.ViewModels;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// A wheel event is a step, and one notch pans 60 pixels.
///
/// This is the state the application was in **before** two changes were made for one mouse: a notched wheel
/// whose driver reports a single click as a burst of whole-notch events, and a pan distance raised from 60 to 90
/// so that one click still moved as far as it used to once the burst had been collapsed.
///
/// **The burst is the device.** The person who reported it found the same behaviour in most other programs with
/// the same mouse, so it is not something this application should compensate for - doing so made VCCad move
/// differently from everything else on the machine. Both halves are undone: every event is applied, and the step
/// is 60 pixels again.
/// </summary>
public class WheelStepPerEventTests
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

    /// <summary>
    /// Six notches zoom six steps. The number is the point: this asserted `before * 1.1` while the burst was
    /// collapsed, and the change being undone is exactly the difference between one step and six.
    /// </summary>
    [AvaloniaFact]
    public void SixNotchesZoomSixSteps()
    {
        (CanvasWorkspace workspace, Window window) = Open();
        double before = workspace.Zoom;

        for (int i = 0; i < 6; i++)
        {
            Wheel(workspace, 1.0, KeyModifiers.Control);
        }

        Assert.Equal(before * Math.Pow(CanvasWorkspace.ZoomStepPerNotch, 6), workspace.Zoom, 3);
        window.Close();
    }

    /// <summary>And one notch is one step, with no collapsing left in the path.</summary>
    [AvaloniaFact]
    public void OneNotchZoomsOneStep()
    {
        (CanvasWorkspace workspace, Window window) = Open();
        double before = workspace.Zoom;

        Wheel(workspace, 1.0, KeyModifiers.Control);

        Assert.Equal(before * CanvasWorkspace.ZoomStepPerNotch, workspace.Zoom, 3);
        window.Close();
    }
}
