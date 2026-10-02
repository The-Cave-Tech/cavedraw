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
/// **The honouring step of a bristle brush: the canvas paints the bristles the seam places** (issue #103).
///
/// The model, the geometry, the serializer and the operations can all land and be green while nothing paints the
/// bundle. A stroke's render plan is a width or an outline, and a bristle brush is a set of strokes that have to
/// become the outline - so if that step never runs, the stroke is drawn as an ordinary line and every round trip is
/// still correct. A round-trip test cannot see it, which is the defect this family has produced seven times.
///
/// These tests render the real canvas and read the pixels, and the probe points are chosen from
/// <see cref="BristleBrushPath.Strokes"/> - the same answer <c>brush.bristles</c> hands a driver - so what is
/// asserted is that ink lands **where the model says a bristle runs**, and that the line the brush replaced is
/// bare. Each test pairs its probes, because a single dark pixel is not the claim.
/// </summary>
public class BristleBrushCanvasTests
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

    /// <summary>A short stroked line, added to the first artboard's first layer.</summary>
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
    /// The model point a bristle's own centreline runs through - the seam's point plus the artboard's origin, which
    /// is the frame the canvas paints the whole page in.
    /// </summary>
    private static Point2D Model(BristleStroke bristle, PathItem path, int at)
    {
        Vector2D origin = path.ArtboardOffset();
        Point2D point = bristle.Points[Math.Clamp(at, 0, bristle.Points.Count - 1)];
        return new Point2D(point.X + origin.X, point.Y + origin.Y);
    }

    /// <summary>
    /// **A stroke carrying a bristle brush puts its bristles on the canvas where the seam says, and the line the
    /// brush replaced is bare.**
    ///
    /// The bundle is forty across with two bristles at its two edges, so the probes are: each bristle's own
    /// centreline, which is ink only if the bundle was drawn, and the path's own centreline, which is bare because
    /// a bristle brush is drawn as its bristles rather than as the stroke's four-point line. A renderer that left
    /// the brush unhonoured draws the line and neither bristle, failing both halves.
    /// </summary>
    [AvaloniaFact]
    public void ABristleBrushPaintsItsBristlesAndNotTheLineItReplaced()
    {
        (Window window, EditorViewModel viewModel, CanvasWorkspace workspace) = Host();
        PathItem path = Line(viewModel, new Point2D(100, 300), new Point2D(300, 300));

        byte[] plain = Pixels(window);

        BrushSpec brush = BrushSpec.Bristle(
            "Scrub", size: 40, new BristleBrushSpec(Count: 2, Spread: 1.0, Randomness: 0.0, Thickness: 3.0, Stiffness: 1.0));
        path.Stroke = path.Stroke with { Brush = brush };
        Settle();
        byte[] painted = Pixels(window);

        IReadOnlyList<BristleStroke> bristles = BristleBrushPath.Strokes(path, brush).Bristles;
        Assert.Equal(2, bristles.Count);

        // A size of forty with a spread of one puts the two bristles twenty either side of the line.
        Assert.Equal(-20.0, bristles[0].Offset, 9);
        Assert.Equal(20.0, bristles[1].Offset, 9);

        foreach (BristleStroke bristle in bristles)
        {
            Point2D centre = Model(bristle, path, bristle.Points.Count / 2);

            Assert.False(Dark(plain, workspace.ModelToWindow(centre)),
                "nothing should be drawn twenty points off the line before the brush is applied");

            Assert.True(Dark(painted, workspace.ModelToWindow(centre)),
                $"a bristle should be painted at {centre.X},{centre.Y}, where its own stroke runs");
        }

        // The middle of the stroke: inside the four-point line the pen would have drawn, and between the two
        // bristles - so a renderer that drew the ordinary stroke as well as the bristles fails here.
        Point2D onTheLine = Model(bristles[0], path, 0);
        Point2D middle = new(onTheLine.X + 100, onTheLine.Y - 20);
        Assert.True(Dark(plain, workspace.ModelToWindow(middle)), "the plain stroke covers its own centreline");
        Assert.False(Dark(painted, workspace.ModelToWindow(middle)),
            "a bristle brush draws the bristles rather than the line the brush replaced");
    }

    /// <summary>
    /// **A colour jitter reaches the pixels, one shade per bristle.**
    ///
    /// The same document is rendered twice with one member changed: the bristles' colour jitter. Each probe is a
    /// bristle's own centreline, and what is asserted is that the **same point** is lighter with the jitter than
    /// without it - which is a comparison a renderer that painted every bristle the stroke's colour cannot satisfy,
    /// because then the two renders are the same picture at that point. Both renders are required to be ink, so
    /// "lighter" cannot mean "nothing was drawn".
    ///
    /// The pair of renders is what makes this honest. An earlier version of this test compared two **different**
    /// bristles of one render, and it passed against a mutation that painted them all the stroke's colour: two
    /// probes at two different places cover different amounts of their neighbours, and the coverage difference
    /// happened to point the same way as the shades. This is the same document at the same point, so there is
    /// nothing but the paint left to differ.
    /// </summary>
    [AvaloniaFact]
    public void AColourJitterReachesTheCanvasOneShadePerBristle()
    {
        (Window window, EditorViewModel viewModel, CanvasWorkspace workspace) = Host();
        PathItem path = Line(viewModel, new Point2D(100, 300), new Point2D(300, 300));

        // A dozen bristles across sixty points with a thickness of four: far enough apart that no probe sits on a
        // neighbour, and enough of them that the sequence's shades cover both sides of the stroke's own colour.
        var spec = new BristleBrushSpec(
            Count: 12, Spread: 1.0, Randomness: 0.0, Thickness: 4.0, Stiffness: 1.0, ColourJitter: 0.8);
        BrushSpec jittered = BrushSpec.Bristle("Scrub", size: 60, spec);
        BrushSpec plain = jittered with { BristleSpec = spec with { ColourJitter = 0.0 } };

        path.Stroke = path.Stroke with { Brush = plain };
        Settle();
        byte[] held = Pixels(window);

        path.Stroke = path.Stroke with { Brush = jittered };
        Settle();
        byte[] shaded = Pixels(window);

        StrokeRenderPlan plan = StrokeOutlineBuilder.Plan(path, path.Stroke);
        IReadOnlyList<ColorRgb> paints = plan.Paints
            ?? throw new InvalidOperationException("a colour jitter has to reach the plan as a paint per bristle");

        IReadOnlyList<BristleStroke> bristles = BristleBrushPath.Strokes(path, jittered).Bristles;
        Assert.Equal(paints.Count, bristles.Count);

        int lightest = 0;
        for (int i = 0; i < paints.Count; i++)
        {
            if (paints[i].R > paints[lightest].R)
            {
                lightest = i;
            }
        }

        Assert.True(
            paints[lightest].R > 0.2,
            $"a jitter at 0.8 has to shade some bristle well towards white; the lightest is {paints[lightest].R:0.###}");

        Point2D probe = Model(bristles[lightest], path, bristles[lightest].Points.Count / 2);
        double light = Luminance(shaded, workspace.ModelToWindow(probe));
        double dark = Luminance(held, workspace.ModelToWindow(probe));

        // The same point, the same document, one member changed: the unjittered bristle is the stroke's black and
        // the jittered one is the shade the plan names. A renderer that painted every bristle the stroke's colour
        // gives the same picture twice and cannot pass.
        Assert.True(dark < 0.5, $"the unjittered bristle should be solid ink at its own centreline ({dark})");
        Assert.True(light > dark,
            $"bristle {lightest} is painted {paints[lightest].R:0.###} and has to be lighter where it lies " +
            $"({light} against {dark})");
    }

    /// <summary>
    /// **Two renders of one document are the same picture.** The bristles are a pure function of the path and the
    /// brush's parameters through a stable sequence, so a bristle brush is safe to export, re-open and print twice
    /// - and a renderer that drew nothing would satisfy the first half while failing the second.
    /// </summary>
    [AvaloniaFact]
    public void TwoRendersOfOneBristleBrushAreIdentical()
    {
        (Window window, EditorViewModel viewModel, _) = Host();
        PathItem path = Line(viewModel, new Point2D(100, 300), new Point2D(320, 320));

        var spec = new BristleBrushSpec(Count: 14, Length: 60, Stiffness: 0.4, Randomness: 0.7, ColourJitter: 0.5);
        path.Stroke = path.Stroke with { Brush = BrushSpec.Bristle("Scrub", size: 30, spec) };
        Settle();

        byte[] first = Pixels(window);
        byte[] second = Pixels(window);

        Assert.True(first.AsSpan().SequenceEqual(second),
            "two renders of one document have to be the same picture, bristles and all");

        // The same document with the bundle collapsed to a single bristle: a different picture, so a renderer that
        // ignored the bundle - or drew nothing - cannot satisfy both halves.
        path.Stroke = path.Stroke with
        {
            Brush = BrushSpec.Bristle("Scrub", size: 30, spec with { Count = 1 }),
        };
        Settle();
        byte[] single = Pixels(window);

        Assert.False(first.AsSpan().SequenceEqual(single),
            "a fourteen-bristle bundle and a one-bristle bundle have to be different pictures");
    }
}
