using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using VCCad.App.ViewModels;
using VCCad.App.Views.Panes;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// The hatch buttons in the colour pane set a fill, through the same operation a driver would call.
///
/// This is the person's half of the capability-parity rule: if the assistant can set a hatch, a button has to
/// be able to as well, and it has to be the same code path - so the assertion is about the document, not about
/// the button's own state.
/// </summary>
public class ColorsPaneHatchTests : IDisposable
{
    private readonly List<ColorsPane> _panes = new();

    private (ColorsPane Pane, EditorViewModel Vm, PathItem Path) Host()
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
        _panes.Add(pane);
        return (pane, vm, path);
    }

    private static void Click(ColorsPane pane, string name)
    {
        Button button = pane.FindControl<Button>(name) ?? throw new Xunit.Sdk.XunitException($"no {name} button");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    public void Dispose()
    {
        // The pane subscribes to the process-wide colour state and the panel, not the test, owns its
        // lifetime - so a test that builds one has to detach it, or it is still listening to colour
        // changes long after the test that made it has finished.
        foreach (ColorsPane pane in _panes)
        {
            pane.Detach();
        }

        _panes.Clear();
        GC.SuppressFinalize(this);
    }

    [AvaloniaFact]
    public void TheCrossButtonHatchesTheSelection()
    {
        (ColorsPane pane, _, PathItem path) = Host();

        Click(pane, "HatchCross");

        Assert.NotNull(path.Fill.Hatch);
        Assert.Equal(2, path.Fill.Hatch!.Lines.Count);
    }

    [AvaloniaFact]
    public void TheNoneButtonTakesItOffAgain()
    {
        (ColorsPane pane, _, PathItem path) = Host();

        Click(pane, "Hatch45");
        Assert.NotNull(path.Fill.Hatch);

        Click(pane, "HatchNone");
        Assert.Null(path.Fill.Hatch);
    }

    /// <summary>With nothing selected the button does nothing rather than throwing out of a click handler.</summary>
    [AvaloniaFact]
    public void WithNothingSelectedItDoesNothing()
    {
        var pane = new ColorsPane();
        pane.Attach(new EditorViewModel());
        _panes.Add(pane);

        Click(pane, "Hatch45");
    }
}