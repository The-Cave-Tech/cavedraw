using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using VCCad.App.Controls;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// **The honouring step of a recorded pen on the canvas: a scatter copy is painted the size the pen gave it**
/// (issue #107).
///
/// The model, the geometry, the operations and the PDF writer can all carry a recorded pen while the canvas keeps
/// asking the seam for a fully pressed one - a renderer that drops a parameter is invisible to every round trip and
/// to every geometry test that calls the seam itself. These tests render the real canvas and read the pixels, and
/// the probe is chosen from `ScatterBrushPath.Placements` - the same answer `brush.scatter` hands a driver - so what
/// is asserted is that the ink covers the copy the record describes.
///
/// Each render is paired with itself under one member changed: the same path, at the same point, with and without
/// the record. A probe inside a full-size copy and outside a quarter-size one cannot be satisfied by "something was
/// drawn" in one of the two.
/// </summary>
public class RecordedPenCanvasTests
{
    private const int Width = 600;
    private const int Height = 500;

    private static (Window Window, EditorViewModel ViewModel, CanvasWorkspace Workspace) Host()
    {
        var viewModel = new EditorViewModel();
        var workspace = new CanvasWorkspace();
        workspace.AttachEditor(viewModel);

        var window = new Window { Width = Width, Height = Height, Content = workspace };
        window.Show();
        Settle();
        return (window, viewModel, workspace);
    }

    private static void Settle()
    {
        for (int i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    /// <summary>A stroked line between two artboard-local points, added to the first artboard's first layer.</summary>
    private static PathItem Line(EditorViewModel viewModel, Point2D from, Point2D to)
    {
        Artboard board = viewModel.Document.Artboards[0];

        var path = new PathItem { Name = "line", Fill = FillSpec.None };
        SubPath sub = path.AddSubPath(closed: false);
        sub.Nodes.Add(new PathNode(new Point2D(board.X + from.X, board.Y + from.Y)));
        sub.Nodes.Add(new PathNode(new Point2D(board.X + to.X, board.Y + to.Y)));
        path.Strokes.Clear();
        path.Strokes.Add(new StrokeSpec(true, ColorRgb.Black, 4, StrokeCap.Butt, StrokeJoin.Miter, 4));

        board.Layers[0].AddItem(path);
        Settle();
        return path;
    }

    /// <summary>A ten by ten solid black square, which is the artwork the brush repeats.</summary>
    private static PathItem Square(EditorViewModel viewModel)
    {
        Artboard board = viewModel.Document.Artboards[0];

        var asset = new PathItem { Name = "copy", Fill = FillSpec.Solid(ColorRgb.Black) };
        SubPath sub = asset.AddSubPath(closed: true);
        sub.Nodes.Add(new PathNode(new Point2D(board.X, board.Y)));
        sub.Nodes.Add(new PathNode(new Point2D(board.X + 10, board.Y)));
        sub.Nodes.Add(new PathNode(new Point2D(board.X + 10, board.Y + 10)));
        sub.Nodes.Add(new PathNode(new Point2D(board.X, board.Y + 10)));

        board.Layers[0].AddItem(asset);
        return asset;
    }

    private static byte[] Pixels(Window window)
    {
        var target = new RenderTargetBitmap(new PixelSize(Width, Height), new Vector(96, 96));
        target.Render(window);

        int stride = Width * 4;
        byte[] pixels = new byte[stride * Height];
        System.Runtime.InteropServices.GCHandle handle = System.Runtime.InteropServices.GCHandle.Alloc(
            pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            target.CopyPixels(new PixelRect(0, 0, Width, Height), handle.AddrOfPinnedObject(), pixels.Length, stride);
        }
        finally
        {
            handle.Free();
        }

        return pixels;
    }

    /// <summary>How light one window point is, in 0..1 - 1 is white paper and 0 is solid ink.</summary>
    private static double Luminance(byte[] pixels, Point at)
    {
        int x = (int)Math.Round(at.X);
        int y = (int)Math.Round(at.Y);
        if (x < 0 || y < 0 || x >= Width || y >= Height)
        {
            return 1.0;
        }

        int i = ((y * Width) + x) * 4;
        return ((pixels[i] / 255.0) + (pixels[i + 1] / 255.0) + (pixels[i + 2] / 255.0)) / 3.0;
    }

    private static bool Dark(byte[] pixels, Point at) => Luminance(pixels, at) < 0.5;

    /// <summary>
    /// **A quarter-pressed pen paints a copy a quarter the size, and a whole-pressed one paints the whole copy.**
    ///
    /// The brush's size is forty and the copy's artwork is ten, so a fully pressed copy reaches twenty points either
    /// side of the path and a quarter-pressed one reaches five. The probe is fifteen points across the line, at the
    /// centre of the first copy: ink under the full pen, paper under the light one. Before the record existed the
    /// canvas called the seam with a fully pressed pen, so both renders covered the probe and this fails.
    /// </summary>
    [AvaloniaFact]
    public void ARecordedPenPaintsTheScatterCopyTheSizeTheSeamPlacesIt()
    {
        (Window window, EditorViewModel viewModel, CanvasWorkspace workspace) = Host();
        PathItem path = Line(viewModel, new Point2D(100, 300), new Point2D(300, 300));
        PathItem asset = Square(viewModel);

        BrushSpec brush = BrushSpec.Scatter(
            "Spray", asset.Id, size: 40.0,
            spacing: new ScatterParameter(60.0),
            dynamics: DynamicsSpec.RespondingTo(DynamicsTarget.ScatterScale));

        // The probe: the centre of the first copy, moved fifteen points across the path. Chosen from the seam's own
        // answer, so it is the copy's own frame that says where to look.
        ScatterBrushPlacement first =
            ScatterBrushPath.Placements(path, brush, _ => ItemBounds.Of(asset))[0];
        Point2D centre = first.Transform.Transform(new Point2D(5, 5));
        Point2D probe = new(centre.X, centre.Y + 15);
        Point windowProbe = workspace.ModelToWindow(probe);

        path.Stroke = path.Stroke with { Brush = brush };
        Settle();
        byte[] pressed = Pixels(window);
        byte[] light;

        path.Stroke = path.Stroke with { Pen = PenProfile.Constant(0.25) };
        Settle();
        light = Pixels(window);

        // The same point is ink under a fully pressed pen and paper under a quarter-pressed one, and the copy's own
        // centre is ink in both - so "paper" cannot mean "the copy was not drawn at all".
        Assert.True(Dark(pressed, windowProbe),
            $"a fully pressed copy reaches twenty points across the path, so {probe.X},{probe.Y} should be inked");
        Assert.False(Dark(light, windowProbe),
            $"a quarter-pressed copy reaches five points, so {probe.X},{probe.Y} should be paper");

        Assert.True(Dark(pressed, workspace.ModelToWindow(centre)), "the full copy covers its own centre");
        Assert.True(Dark(light, workspace.ModelToWindow(centre)), "the light copy still covers its own centre");

        // And the copy really is placed where the seam says: with the record, the placement the canvas consumed is
        // the quarter-size one.
        Assert.Equal(0.25, ScatterBrushPath
            .Placements(path, brush, _ => ItemBounds.Of(asset), 1.0, path.Stroke.Pen)[0].Scale, 9);
    }
}
