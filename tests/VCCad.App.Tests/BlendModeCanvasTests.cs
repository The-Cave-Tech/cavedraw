using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using VCCad.App.Controls;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Geometry;
using Xunit;
using Xunit.Abstractions;

namespace VCCad.App.Tests;

/// <summary>
/// **A blend mode set on an item is composited by the canvas, and a document that states none is unchanged.**
///
/// The member is stored, serialised, printed by `ModelDump` and written by the SVG export, so a round-trip test of
/// any of those passes before and after this change - that is the whole shape of the defect. The assertion is
/// therefore the **sampled pixel** of a real rendered frame: two overlapping opaque rectangles with `multiply` must
/// come out black where they cross, where `normal` leaves the upper one's colour untouched.
///
/// The colours are found by scanning for the solid regions rather than by assuming where the canvas put the
/// artboard, because the fit, the offset and the pixel ratio are the canvas's business and a test that hard-coded
/// them would be asserting the layout rather than the blend.
/// </summary>
public class BlendModeCanvasTests
{
    private const int Width = 600, Height = 500;

    private readonly ITestOutputHelper _output;

    public BlendModeCanvasTests(ITestOutputHelper output) => _output = output;

    private static (Window Window, CanvasWorkspace Workspace, EditorViewModel ViewModel) Host()
    {
        var viewModel = new EditorViewModel();
        var workspace = new CanvasWorkspace();
        workspace.AttachEditor(viewModel);

        var window = new Window { Width = Width, Height = Height, Content = workspace };
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

    private static PathItem Rect(EditorViewModel viewModel, string name, Rect2D box, ColorRgb colour)
    {
        Artboard board = viewModel.Document.Artboards[0];
        PathItem rect = PathFactory.CreateRectangle(name, box);
        rect.Fill = FillSpec.Solid(colour);
        rect.Strokes.Clear();
        rect.Strokes.Add(StrokeSpec.None);
        board.Layers[0].AddItem(rect);
        return rect;
    }

    /// <summary>The frame as premultiplied BGRA, with a helper that samples a pixel's colour.</summary>
    private sealed class Frame
    {
        private readonly byte[] _pixels;

        public Frame(Window window, int width, int height)
        {
            Width = width;
            Height = height;

            var target = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
            target.Render(window);

            int stride = width * 4;
            _pixels = new byte[stride * height];
            System.Runtime.InteropServices.GCHandle handle = System.Runtime.InteropServices.GCHandle.Alloc(
                _pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
            try
            {
                target.CopyPixels(
                    new PixelRect(0, 0, width, height),
                    handle.AddrOfPinnedObject(),
                    _pixels.Length,
                    stride);
            }
            finally
            {
                handle.Free();
            }
        }

        public int Width { get; }

        public int Height { get; }

        /// <summary>The colour at a pixel, as straight (un-premultiplied) RGB over the composited frame.</summary>
        public (int R, int G, int B) At(int x, int y)
        {
            int i = ((y * Width) + x) * 4;
            int a = _pixels[i + 3];
            if (a == 0)
            {
                return (0, 0, 0);
            }

            return (
                (int)Math.Round(_pixels[i + 2] * 255.0 / a),
                (int)Math.Round(_pixels[i + 1] * 255.0 / a),
                (int)Math.Round(_pixels[i] * 255.0 / a));
        }

        /// <summary>The centre of the solid run of this colour, or null when no pixel has it.</summary>
        public (int X, int Y)? CentreOf(int r, int g, int b)
        {
            Rect? box = BoundsOf(r, g, b);
            return box is { } b0 ? ((int)(b0.X + (b0.Width / 2)), (int)(b0.Y + (b0.Height / 2))) : null;
        }

        /// <summary>The box a colour's solid pixels occupy, or null when no pixel has it.</summary>
        public Rect? BoundsOf(int r, int g, int b)
        {
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    if (At(x, y) == (r, g, b))
                    {
                        minX = Math.Min(minX, x);
                        minY = Math.Min(minY, y);
                        maxX = Math.Max(maxX, x);
                        maxY = Math.Max(maxY, y);
                    }
                }
            }

            return minX == int.MaxValue ? null : new Rect(minX, minY, maxX - minX + 1, maxY - minY + 1);
        }

        public int CountOf(int r, int g, int b)
        {
            int count = 0;
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    if (At(x, y) == (r, g, b))
                    {
                        count++;
                    }
                }
            }

            return count;
        }
    }

    /// <summary>
    /// Two rectangles: an opaque blue one, and an opaque red one overlapping its bottom-right corner.
    ///
    /// `multiply` over those two is black - red x blue is zero on every channel - so the answer is a colour that
    /// appears **nowhere else in the picture**, which is what makes the assertion unambiguous rather than a
    /// threshold on a shade.
    /// </summary>
    private static PathItem OverlappingPair(EditorViewModel viewModel)
    {
        Artboard board = viewModel.Document.Artboards[0];
        Rect(viewModel, "back", new Rect2D(board.X + 60, board.Y + 60, 160, 160), new ColorRgb(0, 0, 255));
        PathItem over = Rect(
            viewModel, "over", new Rect2D(board.X + 140, board.Y + 140, 160, 160), new ColorRgb(255, 0, 0));
        Settle();
        return over;
    }

    /// <summary>Reproduction of the defect, and the assertion that it is fixed.</summary>
    [AvaloniaFact]
    public void MultiplyOverlappingArtworkDarkensTheOverlap()
    {
        (Window window, _, EditorViewModel viewModel) = Host();
        PathItem over = OverlappingPair(viewModel);

        var normal = new Frame(window, Width, Height);
        Rect? red = normal.BoundsOf(255, 0, 0);
        Rect? blue = normal.BoundsOf(0, 0, 255);
        Assert.NotNull(red);
        Assert.NotNull(blue);

        Rect redBox = red!.Value;
        Rect blueBox = blue!.Value;
        Rect overlap = redBox.Intersect(blueBox);
        Assert.True(overlap.Width > 20 && overlap.Height > 20, $"the shapes must cross: {overlap}");

        int probeX = (int)(overlap.X + (overlap.Width / 2));
        int probeY = (int)(overlap.Y + (overlap.Height / 2));

        // Under `normal` - and under the defect, whatever blend was stated - the overlap is the upper shape's own
        // opaque red, pixel for pixel.
        _output.WriteLine($"normal: overlap {overlap}, probe ({probeX},{probeY}) = {normal.At(probeX, probeY)}");
        Assert.Equal((255, 0, 0), normal.At(probeX, probeY));

        over.BlendMode = BlendMode.Multiply;
        Settle();

        var blended = new Frame(window, Width, Height);

        // **The assertion is the pixel.** Red x blue is zero on every channel, so `multiply` over these two shapes
        // is black exactly where they cross - a colour the unblended picture does not contain anywhere.
        _output.WriteLine($"multiply: probe ({probeX},{probeY}) = {blended.At(probeX, probeY)}");
        Assert.Equal((0, 0, 0), blended.At(probeX, probeY));

        // ...and each shape still paints its own colour where they do not cross, so the blend composited the item
        // with the backdrop rather than replacing or erasing it.
        Assert.Equal((0, 0, 255), blended.At((int)blueBox.X + 5, (int)blueBox.Y + 5));
        Assert.Equal((255, 0, 0), blended.At((int)(redBox.Right - 5), (int)(redBox.Bottom - 5)));
    }

    /// <summary>
    /// **The stroke half of the same defect.** A stroke states its own blend mode and it is combined with the
    /// picture already on the page - which at that point is the fill and every stroke below it in the stack.
    ///
    /// The stroke here is opaque red and crosses an opaque blue shape, so `multiply` is black exactly where the
    /// two cross.
    /// </summary>
    [AvaloniaFact]
    public void AStrokeBlendModeIsCompositedOverWhatIsBeneathIt()
    {
        (Window window, _, EditorViewModel viewModel) = Host();
        Artboard board = viewModel.Document.Artboards[0];

        Rect(viewModel, "back", new Rect2D(board.X + 80, board.Y + 80, 200, 200), new ColorRgb(0, 0, 255));

        // A horizontal red bar across the middle of the blue shape, drawn as a stroked open path.
        var bar = new PathItem { Name = "bar" };
        SubPath line = bar.AddSubPath(closed: false);
        line.Nodes.Add(new PathNode(new Point2D(board.X + 80, board.Y + 180)));
        line.Nodes.Add(new PathNode(new Point2D(board.X + 280, board.Y + 180)));
        bar.Fill = FillSpec.None;
        bar.Strokes.Clear();
        bar.Strokes.Add(new StrokeSpec(true, new ColorRgb(255, 0, 0), 24, StrokeCap.Butt, StrokeJoin.Miter, 4.0));
        board.Layers[0].AddItem(bar);
        Settle();

        var normal = new Frame(window, Width, Height);
        Rect? blue = normal.BoundsOf(0, 0, 255);
        Rect? red = normal.BoundsOf(255, 0, 0);
        Assert.NotNull(blue);
        Assert.NotNull(red);

        Rect overlap = blue!.Value.Intersect(red!.Value);
        Assert.True(overlap.Width > 20 && overlap.Height > 4, $"the bar must cross the shape: {overlap}");

        int probeX = (int)(overlap.X + (overlap.Width / 2));
        int probeY = (int)(overlap.Y + (overlap.Height / 2));
        _output.WriteLine($"normal: overlap {overlap}, probe ({probeX},{probeY}) = {normal.At(probeX, probeY)}");
        Assert.Equal((255, 0, 0), normal.At(probeX, probeY));

        // Blend is an init-only member, so the state the file states is built rather than assigned - which is also
        // the honest way to say it: a stroke's blend mode is part of the stroke, not something done to it later.
        bar.Strokes[0] = new StrokeSpec(true, new ColorRgb(255, 0, 0), 24, StrokeCap.Butt, StrokeJoin.Miter, 4.0, Blend: BlendMode.Multiply);
        Settle();

        var blended = new Frame(window, Width, Height);
        _output.WriteLine($"multiply: probe ({probeX},{probeY}) = {blended.At(probeX, probeY)}");
        Assert.Equal((0, 0, 0), blended.At(probeX, probeY));

        // The blue shape is untouched where the bar does not cross it.
        Assert.Equal((0, 0, 255), blended.At((int)blue.Value.X + 5, (int)blue.Value.Y + 20));
    }

    /// <summary>
    /// **Absent at default.** A document that states no blend mode draws exactly what it drew before: the upper
    /// shape's own opaque red over the blue, with no black anywhere.
    /// </summary>
    [AvaloniaFact]
    public void NoBlendModeLeavesThePictureAlone()
    {
        (Window window, _, EditorViewModel viewModel) = Host();
        PathItem over = OverlappingPair(viewModel);

        Assert.Equal(BlendMode.Normal, over.BlendMode);

        var frame = new Frame(window, Width, Height);

        Assert.Equal(0, frame.CountOf(0, 0, 0));
        Assert.NotNull(frame.CentreOf(255, 0, 0));
        Assert.NotNull(frame.CentreOf(0, 0, 255));
    }
}

