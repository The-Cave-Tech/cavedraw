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
/// **The honouring step of a scatter brush: the canvas draws the copies it places** (issue #102).
///
/// The model, the geometry, the serializer and the operations can all land and be green while nothing draws the
/// copies - a stroke's render plan is a width or an outline, and a scatter brush is neither. A round-trip test
/// cannot see that, because every round trip is correct.
///
/// These tests render the real canvas and read the pixels. The probe points are chosen from
/// `ScatterBrushPath.Placements` - the same answer `brush.scatter` hands a driver - so what is asserted is that ink
/// lands **where the model says a copy goes**.
///
/// **Each test pairs two probes, because a single dark pixel is not the claim.** A copy drawn at the fixed value
/// instead of its own draw puts ink on the centreline and none where the copy was moved to; a renderer that drew
/// nothing puts ink nowhere; a renderer that ignored the opacity paints a faint copy at full strength. Every test
/// below asserts the pair, so "the copy's own draw reached the pixels" is told apart from each of those.
/// </summary>
public class ScatterBrushCanvasTests
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
    /// The model point a copy's own asset coordinate ends up at: the placement carries the asset's frame onto the
    /// path, and the artboard's origin is what the canvas paints the whole page under.
    /// </summary>
    private static Point2D Model(ScatterBrushPlacement copy, PathItem asset, double ownX, double ownY)
        => copy.Transform.Transform(new Point2D(ownX, ownY)) + asset.ArtboardOffset();

    private static BrushSpec Spray(
        Guid asset, double spacing, ScatterParameter offset, ScatterParameter? opacity = null)
        => BrushSpec.Scatter("Spray", asset, size: 20, spacing: new ScatterParameter(spacing), offset: offset,
            opacity: opacity);

    /// <summary>
    /// **A stroke carrying a scatter brush puts its copies on the canvas where the placements say.**
    ///
    /// The copies are offset thirty points off the line, so the pair of probes is: the copy's own centre, which is
    /// ink only if the copy was moved there, and a point five points across - inside a copy drawn at the **fixed
    /// value** and well outside the stroke's own four-point width. A renderer that dropped the offset, or drew
    /// nothing, fails one half or the other.
    /// </summary>
    [AvaloniaFact]
    public void AScatterBrushPutsItsCopiesOnTheCanvasWhereTheSeamSays()
    {
        (Window window, EditorViewModel viewModel, CanvasWorkspace workspace) = Host();
        PathItem asset = Square(viewModel);
        PathItem path = Line(viewModel, new Point2D(100, 300), new Point2D(300, 300));

        byte[] plain = Pixels(window);

        BrushSpec brush = Spray(asset.Id, spacing: 60, offset: new ScatterParameter(30));
        path.Stroke = path.Stroke with { Brush = brush };
        Settle();
        byte[] painted = Pixels(window);

        IReadOnlyList<ScatterBrushPlacement> copies =
            ScatterBrushPath.Placements(path, brush, _ => ItemBounds.Of(asset));

        // A 200pt line at a 60pt pitch is four copies: 0, 60, 120 and 180. Repeated here so the probes below are
        // visibly derived from the model rather than invented.
        Assert.Equal(4, copies.Count);

        foreach (ScatterBrushPlacement copy in copies)
        {
            Point2D centre = Model(copy, asset, 5, 5);
            Assert.Equal(30.0, copy.Offset, 9);

            // Five points across the line: inside a copy left at the fixed value (twenty across), outside the
            // stroke's own four-point width, and twenty-five points from where this copy actually is.
            Point2D fixedValue = new(centre.X, copy.Point.Y + asset.ArtboardOffset().Y - 5);

            Assert.False(Dark(plain, workspace.ModelToWindow(centre)),
                "nothing should be drawn thirty points off the line before the brush is applied");

            Assert.True(Dark(painted, workspace.ModelToWindow(centre)),
                $"a copy should be drawn at {centre.X},{centre.Y}, where its placement puts its centre");

            Assert.False(Dark(painted, workspace.ModelToWindow(fixedValue)),
                "the copy was moved thirty across the path, so the point it would cover at the fixed value is bare");
        }
    }

    /// <summary>
    /// **The range reaches the pixels, not just the model.** The same document is rendered twice with one control
    /// changed: a zero range, which draws every copy on the line, and a stated range, which draws them across it.
    /// A copy the draw moved is then ink in the second render and blank at exactly that point in the first - which
    /// is what tells "the randomness is drawn" from "a copy exists".
    /// </summary>
    [AvaloniaFact]
    public void TheRandomnessReachesTheCanvasNotJustTheModel()
    {
        (Window window, EditorViewModel viewModel, CanvasWorkspace workspace) = Host();
        PathItem asset = Square(viewModel);
        PathItem path = Line(viewModel, new Point2D(100, 300), new Point2D(300, 300));

        BrushSpec pinned = Spray(asset.Id, spacing: 60, offset: new ScatterParameter(0, 0));
        path.Stroke = path.Stroke with { Brush = pinned };
        Settle();
        byte[] held = Pixels(window);

        BrushSpec ranged = Spray(asset.Id, spacing: 60, offset: new ScatterParameter(0, 25));
        path.Stroke = path.Stroke with { Brush = ranged };
        Settle();
        byte[] scattered = Pixels(window);

        IReadOnlyList<ScatterBrushPlacement> copies =
            ScatterBrushPath.Placements(path, ranged, _ => ItemBounds.Of(asset));

        // At least one copy the draw moved clear of the copies the pinned render draws, which span ten points
        // either side of the line. Without such a copy the two renders could not be told apart and this test would
        // prove nothing - so it is asserted rather than assumed.
        List<ScatterBrushPlacement> moved = copies.Where(c => Math.Abs(c.Offset) > 15.0).ToList();
        Assert.NotEmpty(moved);

        // The copies really do differ from one another, which is the feature: a range of zero would leave every
        // copy at the same offset and this list would be the whole set or none of it.
        Assert.True(copies.Select(c => Math.Round(c.Offset, 6)).Distinct().Count() > 1);

        foreach (ScatterBrushPlacement copy in moved)
        {
            Point2D centre = Model(copy, asset, 5, 5);
            Assert.False(Dark(held, workspace.ModelToWindow(centre)),
                "with a range of zero every copy is on the line, so this point is bare");
            Assert.True(Dark(scattered, workspace.ModelToWindow(centre)),
                "with the range stated a copy is drawn at the point its own draw moved it to");
        }
    }

    /// <summary>
    /// **A copy's own opacity reaches the pixels.** Two renders of one document differ only in the opacity a copy
    /// is drawn at, and the ink at the copy's own centre is measured: the faint copy is lighter than the solid one,
    /// at the same place and the same size. Geometry alone cannot be the claim here, so the probe is fixed by a
    /// placement and the **luminance** is compared rather than a dark/light verdict.
    /// </summary>
    [AvaloniaFact]
    public void ACopyOpacityReachesTheCanvas()
    {
        (Window window, EditorViewModel viewModel, CanvasWorkspace workspace) = Host();
        PathItem asset = Square(viewModel);
        PathItem path = Line(viewModel, new Point2D(100, 300), new Point2D(300, 300));

        // Twenty across and twenty off the line, so the probe sits well clear of the stroke's own four-point width
        // and the measurement is of the copy alone. The pitch is longer than the path, so there is exactly one copy
        // and the two renders differ in nothing but the opacity it is drawn at.
        BrushSpec faint = Spray(asset.Id, spacing: 250, offset: new ScatterParameter(20), opacity: new ScatterParameter(0.15));
        BrushSpec solid = Spray(asset.Id, spacing: 250, offset: new ScatterParameter(20), opacity: new ScatterParameter(0.9));

        path.Stroke = path.Stroke with { Brush = faint };
        Settle();
        byte[] faded = Pixels(window);

        path.Stroke = path.Stroke with { Brush = solid };
        Settle();
        byte[] inked = Pixels(window);

        ScatterBrushPlacement copy = Assert.Single(
            ScatterBrushPath.Placements(path, solid, _ => ItemBounds.Of(asset)));
        Assert.Equal(0.15, ScatterBrushPath.Placements(path, faint, _ => ItemBounds.Of(asset))[0].Opacity, 9);

        Point window123 = workspace.ModelToWindow(Model(copy, asset, 5, 5));

        double light = Luminance(faded, window123);
        double dark = Luminance(inked, window123);

        Assert.True(light > dark,
            $"a copy at 15% should be lighter than one at 90% at the same point ({light} against {dark})");
        Assert.True(light > 0.5, "a copy at 15% is not solid ink");
        Assert.True(dark < 0.5, "a copy at 90% is solid ink");
    }
}
