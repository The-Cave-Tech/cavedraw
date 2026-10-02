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
/// **The honouring step of an art brush: the canvas draws the asset it maps** (issue #100).
///
/// The model, the geometry and the operations all landed and were green while nothing drew the art: a stroke's
/// render plan was a width or an outline, and the canvas asked it only that. So a stroke carrying an art brush
/// drew its own 4pt line and no artwork at all - and a round-trip test cannot see it, because every round trip
/// was correct.
///
/// These tests render the real canvas and read the pixels, because the gap was in the drawing and not in the
/// arithmetic. The probe points are chosen from `ArtBrushPath.Placements` - the same answer `brush.placements`
/// hands a driver - so what is asserted is that the ink lands **where the model says the art goes**, which is
/// the one thing a renderer that ignored the placements could not satisfy.
/// </summary>
public class ArtBrushCanvasTests
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

    /// <summary>A ten by ten solid black square, which is the artwork the brush maps.</summary>
    private static PathItem Square(EditorViewModel viewModel)
    {
        Artboard board = viewModel.Document.Artboards[0];

        var asset = new PathItem { Name = "tile", Fill = FillSpec.Solid(ColorRgb.Black) };
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

    /// <summary>Whether one window point carries ink, which is the only question a renderer answers.</summary>
    private static bool Dark(byte[] pixels, Point at)
    {
        int x = (int)Math.Round(at.X);
        int y = (int)Math.Round(at.Y);
        if (x < 0 || y < 0 || x >= Width || y >= Height)
        {
            return false;
        }

        int i = ((y * Width) + x) * 4;
        double luminance = ((pixels[i] / 255.0) + (pixels[i + 1] / 255.0) + (pixels[i + 2] / 255.0)) / 3.0;
        return luminance < 0.5;
    }

    /// <summary>
    /// **A stroke carrying an art brush puts its asset's shape on the canvas, where the placements say.**
    ///
    /// The probe sits seven points off the centreline: inside a 20-across piece, and well outside the stroke's
    /// own 4pt width. Before this change the canvas drew the line and nothing else, so every probe was blank.
    /// </summary>
    [AvaloniaFact]
    public void AnArtBrushPutsItsAssetOnTheCanvasWhereThePlacementsSay()
    {
        (Window window, EditorViewModel viewModel, CanvasWorkspace workspace) = Host();
        PathItem asset = Square(viewModel);
        PathItem path = Line(viewModel, new Point2D(100, 300), new Point2D(200, 300));

        byte[] plain = Pixels(window);

        BrushSpec brush = BrushSpec.Art("Vine", asset.Id, size: 20, ArtStretch.Repeat);
        path.Stroke = path.Stroke with { Brush = brush };
        Settle();
        byte[] painted = Pixels(window);

        IReadOnlyList<ArtBrushPlacement> placements =
            ArtBrushPath.Placements(path, brush, ItemBounds.Of(asset));

        // A 100pt line with a 20pt repeat is five pieces, which is the placement half already pinned elsewhere;
        // it is repeated here so the probe points below are visibly derived from the model and not invented.
        Assert.Equal(5, placements.Count);

        foreach (ArtBrushPlacement placement in placements)
        {
            Vector2D offset = asset.ArtboardOffset();
            var probe = new Point2D(
                offset.X + placement.Point.X + 10,
                offset.Y + placement.Point.Y + 7);

            Assert.False(Dark(plain, workspace.ModelToWindow(probe)),
                $"nothing should be drawn {probe.Y - 300}pt off the line before the brush is applied");
            Assert.True(Dark(painted, workspace.ModelToWindow(probe)),
                $"the art should be drawn at {probe.X},{probe.Y}, inside the piece the placement names");
        }

        // And nothing is drawn beyond the pieces: the art is 20 across, so 30 off the centreline is bare.
        Vector2D origin = asset.ArtboardOffset();
        Assert.False(Dark(painted, workspace.ModelToWindow(new Point2D(origin.X + 150, origin.Y + 330))),
            "the art is 20 across the line, so 30 off it is outside every piece");
    }

    /// <summary>
    /// **A turned placement is drawn turned, raster asset included.**
    ///
    /// `ImageItem` states an axis-aligned placement and two mirrors and no rotation, so the item cannot hold a
    /// placement turned to a tangent - which means the renderer has to apply the placement's transform itself.
    /// This is that: a 40 by 20 image on a 45-degree line, with the probe at a point inside the image only when
    /// the turn is applied. Drawn axis-aligned at the placement's origin it would land 28pt away.
    /// </summary>
    [AvaloniaFact]
    public void ATurnedRasterPlacementIsDrawnTurned()
    {
        (Window window, EditorViewModel viewModel, CanvasWorkspace workspace) = Host();
        Artboard board = viewModel.Document.Artboards[0];

        var image = new ImageItem
        {
            Name = "scan",
            Placement = new Rect2D(board.X, board.Y, 40, 20),
            PixelWidth = 2,
            PixelHeight = 2,
            ColorSpace = ImageColorSpace.Rgb,
            Samples = new byte[2 * 2 * 3],
        };
        board.Layers[0].AddItem(image);

        PathItem path = Line(viewModel, new Point2D(100, 100), new Point2D(140, 140));
        byte[] plain = Pixels(window);

        BrushSpec brush = BrushSpec.Art("Scan", image.Id, size: 40, ArtStretch.ScaleProportionally);
        path.Stroke = path.Stroke with { Brush = brush };
        Settle();
        byte[] painted = Pixels(window);

        ArtBrushPlacement placement = Assert.Single(ArtBrushPath.Placements(path, brush, ItemBounds.Of(image)));
        Assert.Equal(Math.Atan2(40, 40), placement.TangentRadians, 9);

        // Two probes, because one of them alone is not the claim. The first is inside the piece the placement
        // states - at (5,10) of the asset's own 40 by 20 box, so five points clear of the turned piece's edge.
        // The second is near the far corner of the box the image would occupy if the turn were dropped and it
        // were drawn axis-aligned from the placement's point: inside that box, and well outside the turned piece.
        // The pair together is "drawn where the placements say" against "drawn where an axis-aligned box would
        // fall", which a renderer cannot satisfy by accident.
        Point2D turned = placement.Transform.Transform(new Point2D(5, 10)) + image.ArtboardOffset();
        Point2D axisAligned = new(
            placement.Point.X + image.Placement.Width - 5 + image.ArtboardOffset().X,
            placement.Point.Y + image.Placement.Height - 5 + image.ArtboardOffset().Y);

        Assert.False(Dark(plain, workspace.ModelToWindow(turned)),
            "the probe is 10pt off the line, outside the stroke's own width");
        Assert.True(Dark(painted, workspace.ModelToWindow(turned)),
            "the raster piece should be drawn turned to the line's tangent");

        Assert.False(Dark(painted, workspace.ModelToWindow(axisAligned)),
            "the piece lies along the tangent, so an axis-aligned box's far corner is bare");
    }
}
