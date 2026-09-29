using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using VCCad.App.Automation;
using VCCad.App.Controls;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;
using GradientStop = VCCad.Core.Model.GradientStop;

namespace VCCad.App.Tests;

/// <summary>
/// The gradient annotators on the canvas: a real pointer grabs a handle, the ramp moves, the whole
/// gesture is one undo step, and the handles are actually drawn.
///
/// The arithmetic lives in <see cref="GradientAnnotators"/> and is pinned separately; this is the
/// layer that proves the canvas is wired to it.
/// </summary>
public class GradientAnnotatorCanvasTests
{
    private static (Window Window, CanvasWorkspace Workspace, EditorViewModel ViewModel) Host()
    {
        var viewModel = new EditorViewModel();
        var workspace = new CanvasWorkspace();
        workspace.AttachEditor(viewModel);

        var window = new Window { Width = 900, Height = 700, Content = workspace };
        window.Show();
        Settle();
        return (window, workspace, viewModel);
    }

    private static void Drag(Window window, CanvasWorkspace workspace, Point2D from, Point2D to)
    {
        Point a = workspace.ModelToWindow(from);
        Point b = workspace.ModelToWindow(to);

        InputInjection.Press(window, a.X, a.Y, shift: false);
        InputInjection.Move(window, (a.X + b.X) / 2.0, (a.Y + b.Y) / 2.0, leftDown: true);
        InputInjection.Move(window, b.X, b.Y, leftDown: true);
        InputInjection.Release(window, b.X, b.Y);
        Settle();
    }

    private static void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>A selected rectangle carrying a linear gradient, and the box its geometry maps to.</summary>
    private static (PathItem Path, Rect Box) GradientRect(
        EditorViewModel viewModel, GradientSpec spec, double width = 200, double height = 100)
    {
        Layer layer = viewModel.Document.Artboards[0].Layers[0];
        PathItem rect = PathFactory.CreateRectangle("rect", new Rect2D(100, 100, width, height));
        rect.Fill = FillSpec.WithGradient(spec, flattened: ColorRgb.Black);
        layer.AddItem(rect);
        viewModel.SelectObject(rect);

        Rect2D world = rect.WorldBounds();
        return (rect, new Rect(world.X, world.Y, world.Width, world.Height));
    }

    private static GradientSpec LinearSpec() => new()
    {
        Kind = GradientKind.Linear,
        Start = new Point2D(0, 0.5),
        End = new Point2D(1, 0.5),
        Stops = new[]
        {
            new GradientStop(0.0, ColorRgb.White),
            new GradientStop(1.0, ColorRgb.Black),
        },
    };

    [AvaloniaFact]
    public void DraggingTheRampEndOnCanvasPlacesTheGradientAndUndoesInOneStep()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel) = Host();
        try
        {
            (PathItem rect, Rect box) = GradientRect(viewModel, LinearSpec());

            IReadOnlyList<(GradientHandle Handle, Point2D Point)> handles =
                GradientAnnotators.Handles(rect.Fill.Gradient!, box);
            Point2D endHandle = handles[1].Point;

            Drag(window, workspace, endHandle, new Point2D(box.X + 120, box.Y + 30));

            Assert.NotEqual(new Point2D(1.0, 0.5), rect.Fill.Gradient!.End);

            viewModel.Undo();

            Assert.Equal(new Point2D(1.0, 0.5), rect.Fill.Gradient!.End);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void DraggingTheRadialRadiusHandleScalesTheEllipse()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel) = Host();
        try
        {
            GradientSpec spec = new()
            {
                Kind = GradientKind.Radial,
                Center = new Point2D(0.5, 0.5),
                RadiusX = 0.25,
                RadiusY = 0.5,
                Stops = new[]
                {
                    new GradientStop(0.0, ColorRgb.White),
                    new GradientStop(1.0, ColorRgb.Black),
                },
            };

            (PathItem rect, Rect box) = GradientRect(viewModel, spec);

            IReadOnlyList<(GradientHandle Handle, Point2D Point)> handles =
                GradientAnnotators.Handles(rect.Fill.Gradient!, box);
            (GradientHandle handle, Point2D point) = handles.First(h => h.Handle == GradientHandle.RadialRadiusX);

            Drag(window, workspace, point, new Point2D(point.X + 40, point.Y));

            Assert.True(rect.Fill.Gradient!.RadiusX > 0.25, "the radius must grow with the drag");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The handles are drawn where the model puts them. The ramp's end here is deliberately off
    /// the selection box's own handles, so a white pixel at that point can only be the annotator.
    /// </summary>
    [AvaloniaFact]
    public void TheAnnotatorsAreDrawnWhereTheModelPutsThem()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel) = Host();
        try
        {
            GradientSpec spec = LinearSpec() with { End = new Point2D(0.6, 0.3) };
            (PathItem rect, Rect box) = GradientRect(viewModel, spec);

            Point2D endHandle = GradientAnnotators.Handles(rect.Fill.Gradient!, box)[1].Point;
            Point screen = workspace.ModelToWindow(endHandle);

            Assert.True(BrightnessAt(workspace, (int)Math.Round(screen.X), (int)Math.Round(screen.Y)) > 180,
                "the ramp's end handle should be painted as a white disc");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The brightness (0-255) of one rendered pixel of the control.</summary>
    private static int BrightnessAt(Visual visual, int x, int y)
    {
        var target = new RenderTargetBitmap(new PixelSize(900, 700), new Vector(96, 96));
        target.Render(visual);

        var pixel = new byte[4];
        System.Runtime.InteropServices.GCHandle handle = System.Runtime.InteropServices.GCHandle.Alloc(
            pixel, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            target.CopyPixels(new PixelRect(x, y, 1, 1), handle.AddrOfPinnedObject(), 4, 4);
        }
        finally
        {
            handle.Free();
        }

        return (pixel[0] + pixel[1] + pixel[2]) / 3;
    }
}
