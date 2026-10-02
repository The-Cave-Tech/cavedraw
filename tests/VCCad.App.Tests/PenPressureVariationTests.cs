using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using VCCad.App.Automation;
using VCCad.App.Controls;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// **A driver can draw a stroke whose pressure varies along it.**
///
/// This is the last piece of the pen-parity gap, and it turned out to need no raw input pipeline at all.
/// `PointerPointProperties` has a constructor taking pressure and tilt -
/// `(modifiers, kind, twist, pressure, xTilt, yTilt)` - so a synthetic pen can carry **any** reading, including one
/// that changes between the press and the drags that follow.
///
/// `CanvasWorkspace.PenSample` reads `properties.Pressure` for a pointer whose type is `Pen`, and
/// `PenProfile.FromSamples` stores a sample per point, so a stroke drawn this way records the pressure at each step
/// rather than one constant.
///
/// The assertion is the **model**: the recorded samples' pressures, in order.
/// </summary>
public class PenPressureVariationTests
{
    /// <summary>Tilt readings from the stroke most recently drawn by <see cref="DrawnPressures"/>.</summary>
    [ThreadStatic]
    private static List<double>? _lastTilts;

    private static JsonElement Params(object value) => JsonSerializer.SerializeToElement(value);

    private static void Settle()
    {
        for (int i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    /// <summary>Draws a three-point stroke at the stated pressures and reports what the pen recorded.</summary>
    private static IReadOnlyList<double> DrawnPressures()
    {
        var viewModel = new EditorViewModel();
        var workspace = new CanvasWorkspace();
        workspace.AttachEditor(viewModel);

        var window = new Window { Width = 900, Height = 700, Content = workspace };
        window.Show();
        Settle();

        var context = new AutomationContext { ViewModel = viewModel, InputRoot = () => workspace };
        EditorOperations.Invoke(context, "tool.set", Params(new { tool = "pencil" }));
        Settle();

        Point from = workspace.ModelToWindow(new Point2D(200, 200));
        Point mid = workspace.ModelToWindow(new Point2D(240, 210));
        Point to = workspace.ModelToWindow(new Point2D(280, 220));

        InputInjection.Press(window, from.X, from.Y, shift: false, right: false,
            pointerType: PointerType.Pen, pressure: 0.2f, xTilt: 0f);
        InputInjection.Move(window, mid.X, mid.Y, leftDown: true, pressure: 0.5f, xTilt: 6f);
        InputInjection.Move(window, to.X, to.Y, leftDown: true, pressure: 0.9f, xTilt: 12f);
        InputInjection.Release(window, to.X, to.Y);
        Settle();

        PathItem path = viewModel.Document.AllItems().OfType<PathItem>().First();
        PenProfile pen = path.Strokes
            .Select(stroke => stroke.Pen)
            .FirstOrDefault(candidate => candidate is { IsEmpty: false })
            ?? throw new Xunit.Sdk.XunitException("the drawn stroke recorded no pen");

        _lastTilts = pen.Samples.Select(sample => sample.TiltDegrees).ToList();
        return pen.Samples.Select(sample => sample.Pressure).ToList();
    }

    /// <summary>
    /// **The acceptance.** Each point of the stroke carries the pressure the pen was at when it was made, so a
    /// driver's stroke has a pressure profile rather than a single value.
    /// </summary>
    [AvaloniaFact]
    public void TheRecordedPressureFollowsThePenAlongTheStroke()
    {
        IReadOnlyList<double> pressures = DrawnPressures();

        Assert.True(pressures.Count >= 3,
            $"a three-point stroke must record a pressure per point, recorded {pressures.Count}");

        Assert.Equal(0.2, pressures[0], 2);
        Assert.Equal(0.5, pressures[1], 2);
        Assert.Equal(0.9, pressures[^1], 2);

        // **And the tilt axis, which is the same constructor.** Tilt that varies along the stroke is recorded too,
        // so the parameter is not a claim nobody checks.
        List<double> tilts = _lastTilts!;
        Assert.Equal(tilts.Count, pressures.Count);
        Assert.NotEqual(tilts[0], tilts[^1]);
    }
}
