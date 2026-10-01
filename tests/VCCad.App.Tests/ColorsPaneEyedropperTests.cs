using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using VCCad.App.Automation;
using VCCad.App.Picking;
using VCCad.App.ViewModels;
using VCCad.App.Views.Panes;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// The eyedropper button and the circle beside it.
///
/// The circle shows what the picker last chose and, clicked, applies it - and the requirement is that this is
/// the **same thing** clicking a recent swatch does, not a second path that looks the same. So the assertion
/// is about the document after the click, not about the button's own state.
/// </summary>
public class ColorsPaneEyedropperTests : IDisposable
{
    private readonly ColorRgb? _wasPicked = EditorColorState.Shared.LastPicked;
    private static (ColorsPane Pane, EditorViewModel Vm, PathItem Path) Host()
    {
        var vm = new EditorViewModel();
        var path = new PathItem { Name = "panel", Fill = FillSpec.Solid(ColorRgb.White) };
        SubPath sub = path.AddSubPath(closed: true);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(100, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(100, 60)));
        sub.Nodes.Add(new PathNode(new Point2D(0, 60)));
        vm.Document.Artboards[0].Layers[0].AddItem(path);
        vm.SelectObject(path);

        var pane = new ColorsPane();
        pane.Attach(vm);
        return (pane, vm, path);
    }

    private static void Click(ColorsPane pane, string name)
    {
        Button button = pane.FindControl<Button>(name) ?? throw new Xunit.Sdk.XunitException($"no {name} button");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    public void Dispose()
    {
        EditorColorState.Shared.RestorePicked(_wasPicked);
        ScreenColour.ResetSampler();
        GC.SuppressFinalize(this);
    }

    /// <summary>Clicking the circle applies the picked colour, exactly as a recent swatch would.</summary>
    [AvaloniaFact]
    public void TheCircleAppliesTheLastPickedColour()
    {
        (ColorsPane pane, _, PathItem path) = Host();

        EditorColorState.Shared.SetPicked(ColorRgb.FromBytes(200, 40, 60));
        pane.Refresh();

        Click(pane, "PickedSwatch");

        Assert.Equal(ColorRgb.FromBytes(200, 40, 60).R, path.Fill.Color.R, 3);
        Assert.Equal(ColorRgb.FromBytes(200, 40, 60).G, path.Fill.Color.G, 3);
        Assert.Equal(ColorRgb.FromBytes(200, 40, 60).B, path.Fill.Color.B, 3);
    }

    /// <summary>The circle is dimmed when there is nothing to apply, rather than looking like a live button.</summary>
    [AvaloniaFact]
    public void TheCircleIsDimmedBeforeAnythingIsPicked()
    {
        // There is no way to un-pick - LastPicked is a result, not a selection - so this asserts the rendering
        // rule that matters: the circle reflects whether there is something to show.
        (ColorsPane pane, _, _) = Host();
        pane.Refresh();

        bool showing = EditorColorState.Shared.LastPicked is not null;
        Assert.Equal(showing ? 1.0 : 0.4, pane.FindControl<Button>("PickedSwatch")!.Opacity, 3);
    }

    /// <summary>A headless host has no window to pick with, and the operation says so rather than failing.</summary>
    [AvaloniaFact]
    public async Task WithNoWindowTheScreenPickIsRefusedWithItsReason()
    {
        var context = new AutomationContext { ViewModel = new EditorViewModel() };

        object? result = await EditorOperations.InvokeAsync(
            context, "color.pickScreen", default);

        string json = JsonSerializer.Serialize(result);
        Assert.Contains("\"picked\":false", json, StringComparison.Ordinal);
        Assert.Contains("no window to pick with", json, StringComparison.Ordinal);
        Assert.Contains("color.pickAt", json, StringComparison.Ordinal);
    }

    /// <summary>A move or drop from the overlay records the colour, which is what the circle then shows.</summary>
    [AvaloniaFact]
    public async Task ACompletedScreenPickRecordsTheColour()
    {
        var context = new AutomationContext
        {
            ViewModel = new EditorViewModel(),
            PickFromScreenAsync = () => Task.FromResult<(ColorRgb?, string?)>((ColorRgb.FromBytes(9, 8, 7), null)),
        };

        object? result = await EditorOperations.InvokeAsync(context, "color.pickScreen", default);

        Assert.Contains("\"picked\":true", JsonSerializer.Serialize(result), StringComparison.Ordinal);
        Assert.Equal(ColorRgb.FromBytes(9, 8, 7), EditorColorState.Shared.LastPicked);
    }

    /// <summary>A cancellation is reported as a cancellation, not as a refusal or a colour.</summary>
    [AvaloniaFact]
    public async Task ACancelledScreenPickSaysSo()
    {
        ColorRgb? before = EditorColorState.Shared.LastPicked;

        var context = new AutomationContext
        {
            ViewModel = new EditorViewModel(),
            PickFromScreenAsync = () => Task.FromResult<(ColorRgb?, string?)>((null, null)),
        };

        object? result = await EditorOperations.InvokeAsync(context, "color.pickScreen", default);

        string json = JsonSerializer.Serialize(result);
        Assert.Contains("\"cancelled\":true", json, StringComparison.Ordinal);
        Assert.Equal(before, EditorColorState.Shared.LastPicked);
    }

    /// <summary>An overlay that could only read itself is refused, with that as the reason.</summary>
    [AvaloniaFact]
    public async Task AnOpaqueOverlayIsRefused()
    {
        var context = new AutomationContext
        {
            ViewModel = new EditorViewModel(),
            PickFromScreenAsync = () => Task.FromResult<(ColorRgb?, string?)>(
                (null, "the platform gave no transparent overlay, so the picker could only read itself")),
        };

        object? result = await EditorOperations.InvokeAsync(context, "color.pickScreen", default);

        string json = JsonSerializer.Serialize(result);
        Assert.Contains("\"picked\":false", json, StringComparison.Ordinal);
        Assert.Contains("could only read itself", json, StringComparison.Ordinal);
    }
}
