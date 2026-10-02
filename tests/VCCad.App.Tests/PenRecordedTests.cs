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
/// **A driver can raise a pen, and the pen it raises is recorded.**
///
/// `CanvasWorkspace.PenSample` reads pressure only when the pointer's type is `PointerType.Pen`:
///
/// ```csharp
/// bool pen = e.Pointer.Type == PointerType.Pen;
/// ... pen ? properties.Pressure : 1.0, pen ? properties.XTilt : 0.0, pen ? properties.YTilt : 0.0
/// ```
///
/// A person with a tablet takes that branch and a driver could not, because `InputInjection` built every pointer as
/// `Mouse` - a parity defect, and the thing this pins.
///
/// The assertion is the **pair the model documents**, not a pair I chose. `PenProfile.FromSamples` says of itself
/// that "a recording whose every sample is a fully pressed, upright pen is the identity, and is not stored: a mouse
/// reaches here as exactly that". The properties a synthetic event can build default to pressure **0.5**, so:
///
/// - a **pen** pointer records a pen, carrying 0.5;
/// - a **mouse** pointer records **nothing at all**.
///
/// That is observable in both directions and cannot be satisfied by leaving the pointer as a mouse.
/// </summary>
public class PenRecordedTests
{
    private static JsonElement Params(object value) => JsonSerializer.SerializeToElement(value);

    private static void Settle()
    {
        for (int i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    /// <summary>Draws a short pencil stroke with the given pointer kind and reports the pen it recorded.</summary>
    private static PenProfile? DrawnPen(PointerType pointerType)
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
        Point to = workspace.ModelToWindow(new Point2D(260, 220));

        InputInjection.Press(window, from.X, from.Y, shift: false, right: false, pointerType: pointerType);
        InputInjection.Move(window, (from.X + to.X) / 2, (from.Y + to.Y) / 2, leftDown: true);
        InputInjection.Move(window, to.X, to.Y, leftDown: true);
        InputInjection.Release(window, to.X, to.Y);
        Settle();

        PathItem path = viewModel.Document.AllItems().OfType<PathItem>().First();
        return path.Strokes.Select(stroke => stroke.Pen).FirstOrDefault(candidate => candidate is { IsEmpty: false });
    }

    /// <summary>
    /// **The acceptance.** A pen-typed stroke records a pen carrying the pointer's pressure; a mouse-typed stroke
    /// records none, because a fully pressed upright pen is the identity and is deliberately not stored.
    /// </summary>
    [AvaloniaFact]
    public void APenIsRecordedAndAMouseIsNot()
    {
        PenProfile? pen = DrawnPen(PointerType.Pen);
        PenProfile? mouse = DrawnPen(PointerType.Mouse);

        Assert.NotNull(pen);
        Assert.Equal(0.5, pen!.Samples[0].Pressure, 3);
        Assert.Null(mouse);
    }
}
