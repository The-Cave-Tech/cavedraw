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
/// **The honouring step of a pattern brush: the canvas draws the tiles it lays** (issue #101).
///
/// The art brush landed with its model, geometry, serializer and operations all green while nothing drew the
/// artwork, and the lesson recorded for that was to assert the following and not the storing. A pattern brush is
/// the same shape of feature - the model holds a tile set, `brush.tiles` says where each tile goes, and neither
/// of those is a drawing - so this renders the real canvas and reads the pixels.
///
/// The probe points come from `PatternBrushPath.Placements` - the same answer `brush.tiles` hands a driver - so
/// what is asserted is that the ink lands **where the model says the tiles go**, which a renderer that ignored
/// the tile set could not satisfy.
/// </summary>
public class PatternBrushCanvasTests
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

    /// <summary>A stroked path through these model points, added to the first artboard's first layer.</summary>
    private static PathItem Line(EditorViewModel viewModel, params Point2D[] points)
    {
        Artboard board = viewModel.Document.Artboards[0];

        var path = new PathItem { Name = "line", Fill = FillSpec.None };
        SubPath sub = path.AddSubPath(closed: false);
        foreach (Point2D point in points)
        {
            sub.Nodes.Add(new PathNode(new Point2D(board.X + point.X, board.Y + point.Y)));
        }

        path.Strokes.Clear();
        path.Strokes.Add(new StrokeSpec(true, ColorRgb.Black, 4, StrokeCap.Butt, StrokeJoin.Miter, 4));

        board.Layers[0].AddItem(path);
        Settle();
        return path;
    }

    /// <summary>A ten by ten solid black square, which is the artwork a tile is drawn with.</summary>
    private static PathItem Square(EditorViewModel viewModel, string name)
    {
        Artboard board = viewModel.Document.Artboards[0];

        var tile = new PathItem { Name = name, Fill = FillSpec.Solid(ColorRgb.Black) };
        SubPath sub = tile.AddSubPath(closed: true);
        sub.Nodes.Add(new PathNode(new Point2D(board.X, board.Y)));
        sub.Nodes.Add(new PathNode(new Point2D(board.X + 10, board.Y)));
        sub.Nodes.Add(new PathNode(new Point2D(board.X + 10, board.Y + 10)));
        sub.Nodes.Add(new PathNode(new Point2D(board.X, board.Y + 10)));

        board.Layers[0].AddItem(tile);
        return tile;
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
    /// Where a tile's own point lands on the canvas. The placement carries the tile's stored coordinates onto the
    /// path, and the item's own painters add its artboard offset back to them - so the two together are where the
    /// pixel is, which is the arithmetic the canvas itself does.
    /// </summary>
    private static Point2D OnCanvas(PatternTilePlacement tile, LayerItem asset, Point2D own)
    {
        Point2D placed = tile.Transform.Transform(own);
        Vector2D offset = asset.ArtboardOffset();
        return new Point2D(placed.X + offset.X, placed.Y + offset.Y);
    }

    /// <summary>
    /// **A stroke carrying a pattern brush puts its side tiles on the canvas, where the model says.**
    ///
    /// The probe sits seven points off the centreline of each tile: inside a tile 20 across, and well outside the
    /// stroke's own 4pt width. Before the brush is applied every probe is blank.
    /// </summary>
    [AvaloniaFact]
    public void APatternBrushDrawsItsSideTilesWhereTheModelSays()
    {
        (Window window, EditorViewModel viewModel, CanvasWorkspace workspace) = Host();
        PathItem tile = Square(viewModel, "side");
        PathItem path = Line(viewModel, new Point2D(100, 300), new Point2D(200, 300));

        byte[] plain = Pixels(window);

        BrushSpec brush = BrushSpec.Pattern("Rail", 20, side: new PatternTileSpec(tile.Id));
        path.Stroke = path.Stroke with { Brush = brush };
        Settle();
        byte[] painted = Pixels(window);

        IReadOnlyList<PatternTilePlacement> tiles =
            PatternBrushPath.Placements(path, brush, id => id == tile.Id ? ItemBounds.Of(tile) : null);

        // A 100pt line with a 20pt foot is five tiles, which the geometry tests already pin; it is repeated here
        // so the probes below are visibly derived from the model rather than invented.
        Assert.Equal(5, tiles.Count);

        foreach (PatternTilePlacement placement in tiles)
        {
            // The tile's own +X runs **across** the path and its +Y along it, so its own (8.5, 5) is a point
            // inside it and seven points off the centreline once the placement has been applied.
            Point2D probe = OnCanvas(placement, tile, new Point2D(8.5, 5));

            Assert.False(Dark(plain, workspace.ModelToWindow(probe)),
                $"nothing should be drawn {probe.Y - 300}pt off the line before the brush is applied");
            Assert.True(Dark(painted, workspace.ModelToWindow(probe)),
                $"the tile should be drawn at {probe.X},{probe.Y}, inside the foot the placement names");
        }

        // And nothing beyond the tiles: they are 20 across, so 30 off the centreline is bare.
        Assert.False(Dark(painted, workspace.ModelToWindow(new Point2D(150, 330))),
            "the tiles are 20 across the line, so 30 off it is outside every tile");
    }

    /// <summary>
    /// **The corner tile is drawn at the turn, and it is the corner tile that covers it.**
    ///
    /// The corner tile is three times the set's size, so it reaches 30 points across the path at the turn where a
    /// side tile reaches 10. The probe is 25 out along the bisector's perpendicular - covered by the corner tile
    /// and by nothing else - and the same probe is asserted **blank** with the corner slot empty, which is the
    /// documented fallback to the side tile. One assertion alone could not tell "the corner tile is drawn" from
    /// "something is drawn there".
    /// </summary>
    [AvaloniaFact]
    public void APatternBrushDrawsItsCornerTileAtTheTurn()
    {
        (Window window, EditorViewModel viewModel, CanvasWorkspace workspace) = Host();
        PathItem side = Square(viewModel, "side");
        PathItem corner = Square(viewModel, "corner");
        PathItem path = Line(viewModel, new Point2D(100, 300), new Point2D(200, 300), new Point2D(200, 400));

        BrushSpec brush = BrushSpec.Pattern(
            "Rail", 20,
            side: new PatternTileSpec(side.Id),
            outerCorner: new PatternTileSpec(corner.Id, Scale: 3.0));

        // The tile's own +X axis is the direction across the path, so 25 points along it is inside a 60-across
        // corner tile and far outside a 20-across side tile.
        PatternTilePlacement turn = PatternBrushPath
            .Placements(path, brush, id => id == side.Id ? ItemBounds.Of(side) : ItemBounds.Of(corner))
            .Single(t => t.Slot == PatternTileKind.OuterCorner);

        Vector2D across = turn.Transform.Transform(new Vector2D(1, 0));
        across /= across.Length;
        Vector2D offset = corner.ArtboardOffset();
        Point2D probe = new(
            turn.Point.X + (across.X * 25.0) + offset.X,
            turn.Point.Y + (across.Y * 25.0) + offset.Y);

        byte[] plain = Pixels(window);
        Assert.False(Dark(plain, workspace.ModelToWindow(probe)),
            "the probe is 25pt off the path, well outside the stroke's own width");

        path.Stroke = path.Stroke with { Brush = brush };
        Settle();
        byte[] painted = Pixels(window);

        Assert.True(Dark(painted, workspace.ModelToWindow(probe)),
            "the corner tile should be drawn at the turn, 25pt across a path whose side tiles reach 10");

        // With the corner slot empty the same slot falls back to the side tile, which reaches 10 - so the probe
        // goes back to bare, and the ink above is the corner tile's and not a side tile's.
        path.Stroke = path.Stroke with
        {
            Brush = brush with { PatternOuterTile = null },
        };
        Settle();
        byte[] fallback = Pixels(window);

        Assert.False(Dark(fallback, workspace.ModelToWindow(probe)),
            "with no corner tile the slot is filled by the side tile, which is 20 across and does not reach here");
    }
}
