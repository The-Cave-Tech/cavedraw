using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using VCCad.App.Automation;
using VCCad.App.Controls;
using VCCad.App.ViewModels;
using VCCad.App.Views;
using VCCad.Core.Model;
using VCCad.Core.Selection;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// The canvas's own half of #168: a rotated group on an artboard that is **not** at the document origin is drawn
/// where <see cref="SelectionEngine.ToWorld"/> says, and picked there by a real pointer.
///
/// The canvas is the witness that settles which composition of the artboard origin is right.
/// <c>CanvasWorkspace.PaintLayers</c> starts every artboard's items at <c>T(artboard.X, artboard.Y)</c> and
/// descends with <c>toWorld.Compose(group.Transform)</c> - the origin on the **outside** - so
/// <c>PdfDocumentExporter.WorldTransform</c>, which composed it on the inside, was the one that had to move.
/// These tests do not take the drawing's word for it either: the ink is measured in the rendered page, at one
/// pixel per point, and compared with the box the model's own arithmetic produces.
///
/// Every figure is a second state compared against the first, never a brightness threshold: the page renders at
/// 224, so "darker than some number" is true of paper too.
/// </summary>
public class ArtboardOriginCanvasTests
{
    private const double Ax = 300;
    private const double Ay = 200;

    /// <summary>
    /// The square's page box: the world box (480,350)..(520,390) with the artboard origin taken off, because a
    /// page is rendered as the artboard's own rectangle - the grid position is the canvas's layout, not the page's.
    /// </summary>
    private static readonly Rect2D OnPage = new(180, 150, 40, 40);

    /// <summary>
    /// A 400x400 artboard at (300,200) - page two of a grid - one group turned a quarter turn about the
    /// artboard's own centre, holding one 40x40 filled square at artboard-local (150,180).
    /// </summary>
    private static (CadDocument Document, PathItem Path) Document()
    {
        var document = new CadDocument();
        var board = new Artboard(new Size2D(400, 400), new Point2D(Ax, Ay)) { Name = "Page 2" };
        Layer layer = board.AddLayer("Artwork");
        document.AddArtboard(board);

        var group = new ArtGroup
        {
            Name = "turned",
            Transform = AffineTransform.CreateRotationAround(new Point2D(200, 200), Math.PI / 2),
        };

        layer.AddItem(group);

        var path = new PathItem { Name = "square", Fill = FillSpec.Solid(ColorRgb.Black) };
        SubPath sub = path.AddSubPath(closed: true);
        sub.Nodes.Add(new PathNode(new Point2D(150, 180)));
        sub.Nodes.Add(new PathNode(new Point2D(190, 180)));
        sub.Nodes.Add(new PathNode(new Point2D(190, 220)));
        sub.Nodes.Add(new PathNode(new Point2D(150, 220)));
        path.Strokes.Clear();
        path.Strokes.Add(StrokeSpec.None);
        group.AddItem(path);

        return (document, path);
    }

    private static (Window Window, CanvasWorkspace Workspace, EditorViewModel ViewModel) Host(CadDocument document)
    {
        var viewModel = new EditorViewModel();
        viewModel.ImportDocument(document);

        var workspace = new CanvasWorkspace();
        workspace.AttachEditor(viewModel);
        viewModel.Tool = EditorTool.Select;

        var window = new Window { Width = 900, Height = 700, Content = workspace };
        window.Show();
        Settle();
        return (window, workspace, viewModel);
    }

    private static void Settle()
    {
        for (int i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    /// <summary>Click through the real pointer path, so the frame the canvas draws in is the frame that is picked.</summary>
    private static void Click(Window window, CanvasWorkspace workspace, Point2D world)
    {
        Point p = workspace.ModelToWindow(world);
        InputInjection.Click(window, p.X, p.Y, clickCount: 1, shift: false);
        Settle();
    }

    /// <summary>
    /// The canvas draws the turned square where the world frame puts it - page two's own coordinates - and the
    /// same point through the real pointer selects the artwork.
    ///
    /// The click and the ink are the two halves of the same claim, taken apart on purpose: the ink alone would pass
    /// for a canvas that drew the square but picked somewhere else, and the click alone would pass for one that
    /// picked a picture it never painted.
    /// </summary>
    [AvaloniaFact]
    public void TheCanvasDrawsAndPicksARotatedGroupOnAnOffOriginArtboardWhereTheWorldFrameSays()
    {
        (CadDocument document, PathItem path) = Document();
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel) = Host(document);
        try
        {
            // The box the canvas measures a selection with: the artboard origin outside the group transform.
            Rect2D world = SelectionEngine.WorldBounds(new[] { path });
            Assert.Equal(480, world.Left, 6);
            Assert.Equal(350, world.Top, 6);
            Assert.Equal(40, world.Width, 6);
            Assert.Equal(40, world.Height, 6);

            // Drawn there, at one pixel per point, on a page whose own frame is the artboard.
            (int left, int top, int right, int bottom, _) = Ink(document, workspace);
            AssertDrawnAt((left, top, right, bottom), OnPage);

            // And picked there by a pointer: a single click selects the group the square lives in; a second one
            // drills to the square itself (CanvasDrillTests owns the drill, this is about the frame).
            viewModel.ClearSelection();
            Click(window, workspace, new Point2D(500, 370));
            LayerItem selected = Assert.Single(viewModel.SelectedObjects);
            Assert.True(
                ReferenceEquals(selected, path) || SelectionEngine.Descendants(selected).Contains(path),
                $"the click at the world position selected '{selected.Name}', which does not hold the square");

            // And a click where the square would sit if the group's transform were ignored - artboard-local
            // (150,180)..(190,220), i.e. world (450,380)..(490,420) - selects nothing, which is the half that
            // says the pick follows the frame rather than the stored numbers.
            viewModel.ClearSelection();
            Click(window, workspace, new Point2D(470, 400));
            Assert.Empty(viewModel.SelectedObjects);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// The bounding box of the ink the canvas actually drew, in points, by rendering page 0 at one pixel per point
    /// - the renderer the editor shows is the renderer under test, not a second one.
    /// </summary>
    private static (int Left, int Top, int Right, int Bottom, int Count) Ink(
        CadDocument document, CanvasWorkspace workspace)
    {
        workspace.InvalidateVisual();
        PageRenderer.Workspace = workspace;
        byte[]? png = PageRenderer.Render(document, 0, 72);
        Assert.NotNull(png);

        using var stream = new MemoryStream(png!);
        using var bitmap = new Bitmap(stream);
        byte[] pixels = Read(bitmap, out int stride);
        int width = bitmap.PixelSize.Width;
        int height = bitmap.PixelSize.Height;

        int left = width, top = height, right = -1, bottom = -1;
        int count = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (!IsInk(pixels, stride, x, y))
                {
                    continue;
                }

                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
                count++;
            }
        }

        Assert.True(right >= 0, "nothing was drawn at all");
        return (left, top, right, bottom, count);
    }

    private static byte[] Read(Bitmap bitmap, out int stride)
    {
        stride = bitmap.PixelSize.Width * 4;
        var buffer = new byte[stride * bitmap.PixelSize.Height];
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(
            buffer, System.Runtime.InteropServices.GCHandleType.Pinned);
        bitmap.CopyPixels(
            new PixelRect(0, 0, bitmap.PixelSize.Width, bitmap.PixelSize.Height),
            handle.AddrOfPinnedObject(), buffer.Length, stride);
        handle.Free();
        return buffer;
    }

    /// <summary>The black artwork - much darker than the paper the page renders at.</summary>
    private static bool IsInk(byte[] pixels, int stride, int x, int y)
    {
        int at = (y * stride) + (x * 4);
        return pixels[at] < 60 && pixels[at + 1] < 60 && pixels[at + 2] < 60; // BGRA
    }

    private static void AssertDrawnAt(
        (int Left, int Top, int Right, int Bottom) ink, Rect2D expected, string what = "the turned square")
    {
        Assert.True(
            Math.Abs(ink.Left - expected.Left) <= 2 && Math.Abs(ink.Top - expected.Top) <= 2 &&
            Math.Abs(ink.Right - expected.Right) <= 2 && Math.Abs(ink.Bottom - expected.Bottom) <= 2,
            $"{what} should be drawn at {expected.Left},{expected.Top} to {expected.Right},{expected.Bottom} " +
            $"but was drawn at {ink.Left},{ink.Top} to {ink.Right},{ink.Bottom}");
    }
}
