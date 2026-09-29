using Avalonia.Headless.XUnit;
using VCCad.App.Views.Panes;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;
using GradientStop = VCCad.Core.Model.GradientStop;

namespace VCCad.App.Tests;

/// <summary>
/// The Gradient pane bound to a real selection.
///
/// The claim that matters is that a gradient edit is an ordinary style edit: it goes through
/// <see cref="VCCad.Core.Commands.SetFillCommand"/> like the Color pane's does, and one undo puts
/// the object back exactly as it was.
/// </summary>
public class GradientPaneTests
{
    private static PathItem Box(string name = "Box")
    {
        var path = new PathItem { Name = name, Fill = FillSpec.Solid(ColorRgb.Red) };
        SubPath sub = path.AddSubPath(closed: true);
        sub.Nodes.Add(new PathNode(new Point2D(0, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(40, 0)));
        sub.Nodes.Add(new PathNode(new Point2D(40, 40)));
        sub.Nodes.Add(new PathNode(new Point2D(0, 40)));
        return path;
    }

    private static (EditorViewModel Vm, PathItem Path) Selected(PathItem path)
    {
        var vm = new EditorViewModel();
        vm.Document.Artboards[0].Layers[0].AddItem(path);
        vm.SelectObject(path);
        return (vm, path);
    }

    [AvaloniaFact]
    public void AddingAStopIsOneUndoStepAndUndoRestoresTheFill()
    {
        PathItem path = Box();
        (EditorViewModel vm, PathItem item) = Selected(path);
        var pane = new GradientPane();
        pane.Attach(vm);
        try
        {
            FillSpec before = item.Fill;
            Assert.False(before.HasGradient);

            // A stop added on the ramp is one edited gradient, committed as one command.
            pane.Ramp.AddStop(0.5);

            Assert.True(item.Fill.HasGradient, "the object should now carry the gradient");
            Assert.Equal(3, item.Fill.Gradient!.Stops.Count);

            vm.Undo();

            Assert.False(item.Fill.HasGradient, "one undo must take the gradient off again");
            Assert.Equal(before.Color, item.Fill.Color);
            Assert.Equal(before.IsVisible, item.Fill.IsVisible);
        }
        finally
        {
            pane.Detach();
        }
    }

    [AvaloniaFact]
    public void ThePaneShowsTheSelectedObjectsGradient()
    {
        var spec = new GradientSpec
        {
            Kind = GradientKind.Radial,
            Spread = GradientSpread.Reflect,
            RadiusX = 0.4,
            RadiusY = 0.2,
            Stops = new[]
            {
                new GradientStop(0.0, ColorRgb.Green, Opacity: 0.25),
                new GradientStop(0.6, ColorRgb.Blue),
                new GradientStop(1.0, ColorRgb.Black),
            },
        };

        PathItem path = Box();
        path.Fill = FillSpec.WithGradient(spec);
        (EditorViewModel vm, _) = Selected(path);

        var pane = new GradientPane();
        pane.Attach(vm);
        try
        {
            Assert.Equal(GradientKind.Radial, pane.Ramp.Spec.Kind);
            Assert.Equal(GradientSpread.Reflect, pane.Ramp.Spec.Spread);
            Assert.Equal(3, pane.Ramp.Spec.Stops.Count);
            Assert.Equal(0, pane.Ramp.SelectedIndex);

            // The stop fields follow the selected stop.
            Assert.Equal("0.25", pane.StopOpacityBox.Text);
            Assert.Equal("0", pane.PositionBox.Text);
        }
        finally
        {
            pane.Detach();
        }
    }

    [AvaloniaFact]
    public void ThePaneFollowsTheSelectionFromOneObjectToAnother()
    {
        PathItem first = Box("First");
        first.Fill = FillSpec.WithGradient(new GradientSpec
        {
            Stops = new[]
            {
                new GradientStop(0.0, ColorRgb.Red),
                new GradientStop(1.0, ColorRgb.Blue),
            },
        });

        PathItem second = Box("Second");
        second.Fill = FillSpec.Solid(ColorRgb.Green);

        (EditorViewModel vm, _) = Selected(first);
        vm.Document.Artboards[0].Layers[0].AddItem(second);
        var pane = new GradientPane();
        pane.Attach(vm);
        try
        {
            Assert.Equal(2, pane.Ramp.Spec.Stops.Count);

            vm.SelectObject(second);

            // The second object has no gradient, so the panel offers to make one from its colour
            // rather than showing the previous object's stops.
            Assert.True(pane.ApplyButton.IsEnabled);
        }
        finally
        {
            pane.Detach();
        }
    }
}
