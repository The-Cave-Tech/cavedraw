using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using System.Text.Json;
using VCCad.App.Automation;
using VCCad.App.Controls;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Core.Svg;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// A filtered object, drawn by the real canvas.
///
/// Filters are raster operations, so this is where the engine stops being a library and becomes a picture: the
/// canvas rasterises the object, the filter runs over those pixels, and the result is drawn in its place. The test
/// renders the actual <see cref="CanvasWorkspace"/> and reads the pixels back, because "a filter was applied" is
/// not something a model assertion can show - the engine's own tests cover the arithmetic, and what is in question
/// here is whether anything calls it.
///
/// The measurement is **mid-tones**, not ink. The canvas paints a pasteboard and a white page over the whole
/// window, so every pixel has alpha and a count of "pixels with ink" says nothing; what a blur does that a sharp
/// edge does not is lay down a **ramp**, so the number of partly-dark pixels is the signal.
/// </summary>
public class FilterCanvasTests
{
    private static (Window Window, CanvasWorkspace Workspace, EditorViewModel ViewModel) Host()
    {
        var viewModel = new EditorViewModel();
        var workspace = new CanvasWorkspace();
        workspace.AttachEditor(viewModel);

        var window = new Window { Width = 600, Height = 500, Content = workspace };
        window.Show();
        Settle();
        return (window, workspace, viewModel);
    }

    private static void Settle()
    {
        for (int i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    /// <summary>A solid black rectangle in the middle of the artboard - the clearest thing to blur.</summary>
    private static PathItem Block(EditorViewModel viewModel, double size = 120)
    {
        Artboard board = viewModel.Document.Artboards[0];

        PathItem rect = PathFactory.CreateRectangle("block", new Rect2D(board.X + 80, board.Y + 80, size, size));
        rect.Fill = FillSpec.Solid(ColorRgb.Black);
        rect.Strokes.Clear();
        rect.Strokes.Add(StrokeSpec.None);
        board.Layers[0].AddItem(rect);

        Settle();
        return rect;
    }

    /// <summary>What the canvas looks like: how many pixels are dark, how many are part-dark, and the darkest.</summary>
    private static (int Dark, int Mid, double Darkest) Measure(Window window)
    {
        var target = new RenderTargetBitmap(new PixelSize(600, 500), new Vector(96, 96));
        target.Render(window);

        const int Width = 600, Height = 500;
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

        int dark = 0, mid = 0;
        double darkest = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            // Premultiplied BGRA, so a black pixel is zero on every channel and the white page is full scale: the
            // luminance below is "how much light comes out", which is what a blur redistributes.
            double luminance = ((pixels[i] / 255.0) + (pixels[i + 1] / 255.0) + (pixels[i + 2] / 255.0)) / 3.0;
            if (luminance < 0.5)
            {
                dark++;
            }

            if (luminance is > 0.15 and < 0.85)
            {
                mid++;
            }

            darkest = Math.Max(darkest, 1.0 - luminance);
        }

        return (dark, mid, darkest);
    }

    [AvaloniaFact]
    public void AnUnfilteredShapeHasAHardEdge()
    {
        (Window window, _, EditorViewModel viewModel) = Host();
        Block(viewModel);

        (int dark, int mid, double darkest) = Measure(window);

        Assert.True(dark > 5000, $"the rectangle should have drawn: {dark} dark pixels");
        Assert.Equal(1.0, darkest, 2);

        // A hard edge is a couple of pixels of antialiasing, nothing like a ramp.
        Assert.True(mid < dark / 4, $"an unfiltered edge should not be a ramp: {mid} mid-tones against {dark} dark");
    }

    /// <summary>
    /// **A blur filter spreads the shape on the canvas.** A block small enough to be swallowed by the blur loses its
    /// solid core and becomes a ramp - many more partly-dark pixels, and no fully black one - which is what a blur
    /// is and what no model assertion could show.
    /// </summary>
    [AvaloniaFact]
    public void ABlurFilterSpreadsTheShapeOnTheCanvas()
    {
        (Window window, _, EditorViewModel viewModel) = Host();
        PathItem rect = Block(viewModel, size: 40);

        (int darkBefore, int midBefore, double darkestBefore) = Measure(window);
        Assert.Equal(1.0, darkestBefore, 2);

        viewModel.Document.AddFilter(new FilterSpec("soft", new[]
        {
            FilterPrimitive.Blur(15.0, input: "SourceGraphic"),
        }));

        rect.FilterId = "soft";
        Settle();

        (int darkAfter, int midAfter, double darkestAfter) = Measure(window);

        // Two signals, both of which only a blur can produce: the solid core is eaten into, and no pixel is fully
        // black any more. The mid-tone band is reported in the message because it is the intuitive one, but it is
        // not asserted - a blurred small block can end up light enough to leave the band entirely, so the count
        // moves either way and a test on it would be pinning the band rather than the blur.
        Assert.True(darkAfter < darkBefore,
            $"a blur should eat into the solid core: {darkBefore} dark before, {darkAfter} after ({midBefore} mid before, {midAfter} after)");
        Assert.True(darkestAfter < darkestBefore - 0.02,
            $"the core should be softer than it was: {darkestBefore} before, {darkestAfter} after");
    }

    /// <summary>
    /// And it is the filter that did it: clearing the reference restores the sharp picture, which is what keeps the
    /// assertions above from passing for some other reason.
    /// </summary>
    [AvaloniaFact]
    public void ClearingTheFilterRestoresTheSharpShape()
    {
        (Window window, _, EditorViewModel viewModel) = Host();
        PathItem rect = Block(viewModel, size: 40);

        (int dark, int mid, double darkest) = Measure(window);

        viewModel.Document.AddFilter(new FilterSpec("soft", new[]
        {
            FilterPrimitive.Blur(15.0, input: "SourceGraphic"),
        }));
        rect.FilterId = "soft";
        Settle();
        (int blurredDark, _, _) = Measure(window);
        Assert.True(blurredDark < dark);

        rect.FilterId = null;
        Settle();

        (int darkAfter, int midAfter, double darkestAfter) = Measure(window);
        Assert.Equal(dark, darkAfter);
        Assert.Equal(mid, midAfter);
        Assert.Equal(darkest, darkestAfter, 3);
    }

    /// <summary>
    /// A composite of two named buffers runs as a graph on the canvas too: a red flood kept inside the shape's own
    /// coverage, which is neither input on its own.
    /// </summary>
    [AvaloniaFact]
    public void ACompositeGraphRunsOnTheCanvas()
    {
        (Window window, _, EditorViewModel viewModel) = Host();
        PathItem rect = Block(viewModel);

        viewModel.Document.AddFilter(new FilterSpec("tinted", new[]
        {
            FilterPrimitive.Solid(new ColorRgb(1, 0, 0), 1.0, "red"),
            FilterPrimitive.Combine("in", "red", "SourceAlpha", "tinted"),
        })
        {
            Output = "tinted",
        });

        rect.FilterId = "tinted";
        Settle();

        Assert.True(HasOpaqueRed(window), "the composite should have painted the shape red");
    }

    /// <summary>
    /// The engine is reached through the same model an SVG file imports into, so an imported filter blurs the
    /// canvas - the two halves are wired to one asset rather than to two ideas of what a filter is.
    /// </summary>
    [AvaloniaFact]
    public void AnImportedSvgFilterBlursTheCanvas()
    {
        const string Svg =
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"200\" height=\"150\" viewBox=\"0 0 200 150\">" +
            "<defs><filter id=\"f\" x=\"-0.5\" y=\"-0.5\" width=\"2\" height=\"2\">" +
            "<feGaussianBlur in=\"SourceGraphic\" stdDeviation=\"6\"/></filter></defs>" +
            "<rect x=\"40\" y=\"40\" width=\"120\" height=\"70\" fill=\"#000000\" filter=\"url(#f)\"/>" +
            "</svg>";

        (Window window, _, EditorViewModel viewModel) = Host();
        viewModel.ImportDocument(SvgReader.Read(Svg).Document);
        Settle();

        PathItem imported = viewModel.Document.AllPaths().First(p => p.FilterId is not null);
        Assert.Equal("f", imported.FilterId);
        Assert.NotNull(viewModel.Document.FindFilter("f"));

        (int dark, int mid, _) = Measure(window);

        Assert.True(dark > 1000, $"the imported rectangle should have drawn: {dark} dark pixels");

        // The edge is a ramp, because the file said to blur it.
        Assert.True(mid > 100, $"the imported blur should have laid down a ramp: {mid} mid-tones");
    }

    /// <summary>
    /// Whether the rendered canvas has an opaque red pixel anywhere - premultiplied BGRA, so red has a
    /// full alpha and red channel with nothing in green or blue.
    /// </summary>
    private static bool HasOpaqueRed(Window window)
    {
        byte[] pixels = ReadPixels(window);
        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i + 3] > 200 && pixels[i + 2] > 180 && pixels[i + 1] < 60 && pixels[i] < 60)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The window's pixels, premultiplied BGRA, which is what both the canvas and the readback use.</summary>
    private static byte[] ReadPixels(Window window)
    {
        var target = new RenderTargetBitmap(new PixelSize(600, 500), new Vector(96, 96));
        target.Render(window);

        const int Width = 600, Height = 500;
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

    /// <summary>One pixel of that readback as premultiplied (b, g, r, a).</summary>
    private static (byte B, byte G, byte R, byte A) PixelAt(Window window, int x, int y)
    {
        byte[] pixels = ReadPixels(window);
        int i = ((y * 600) + x) * 4;
        return (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]);
    }

    /// <summary>
    /// **A graph reading `FillPaint` draws the shape's own fill colour, and the same filter set through the
    /// operations is what reaches the canvas.**
    ///
    /// `FillPaint` is the shape painted in its fill alone, which the pixels of fill and stroke together cannot be
    /// taken apart into - so it is the renderer's to supply, and before it did the input read as transparent and
    /// this composite painted **nothing at all**. The expected value is the shape's own colour, read at a point
    /// inside it rather than at an edge, so a partly-covered pixel cannot be mistaken for the answer.
    /// </summary>
    [AvaloniaFact]
    public void AFilterReadingFillPaintDrawsTheShapesFillColour()
    {
        (Window window, CanvasWorkspace workspace, EditorViewModel viewModel) = Host();

        Artboard board = viewModel.Document.Artboards[0];
        PathItem rect = PathFactory.CreateRectangle("tinted", new Rect2D(board.X + 80, board.Y + 80, 120, 120));
        rect.Fill = FillSpec.Solid(new ColorRgb(1, 0, 0));
        rect.Strokes.Clear();
        rect.Strokes.Add(StrokeSpec.None);
        board.Layers[0].AddItem(rect);
        viewModel.SelectObject(rect);
        Settle();

        // Through the session, so the canvas is told to repaint - assigning the reference alone notifies nothing.
        var context = new AutomationContext { ViewModel = viewModel };
        Edit(context, "filter.create",
            """{"name":"filling","primitives":[{"kind":"composite","in":"FillPaint","in2":"SourceAlpha","operator":"in"}]}""");
        Edit(context, "filter.apply", """{"name":"filling"}""");
        Settle();

        Geometry.Rect2D world = rect.WorldBounds();
        Point centre = workspace.ModelToWindow(new Point2D(world.X + (world.Width / 2), world.Y + (world.Height / 2)));
        (byte b, byte g, byte r, byte a) = PixelAt(window, (int)Math.Round(centre.X), (int)Math.Round(centre.Y));

        Assert.Equal(255, a);
        Assert.Equal(255, r);
        Assert.Equal(0, g);
        Assert.Equal(0, b);
    }

    private static void Edit(AutomationContext context, string op, string parameters)
        => EditorOperations.Invoke(context, op, JsonSerializer.Deserialize<JsonElement>(parameters));

    /// <summary>
    /// **A bounding-box primitive length is measured from the shape's box, not from the alpha's extent.**
    ///
    /// The bar below is a fifth of the box it belongs to, and a blur of half a box is a very different number of
    /// pixels under the two readings. Graph read from the owner's box, the blur is the box's; measured from the
    /// alpha, it is the bar's. Nothing on the canvas can tell the two apart for a shape that fills its own box -
    /// which is why this drives the renderer directly with a shape that does not.
    /// </summary>
    [AvaloniaFact]
    public void ABoundingBoxLengthIsMeasuredFromTheShapesBoxNotItsAlphaExtent()
    {
        var filter = new FilterSpec("wide", new[] { FilterPrimitive.Blur(0.5, input: "SourceAlpha") })
        {
            PrimitiveUnitsObjectBoundingBox = true,
            X = -1,
            Y = -1,
            Width = 3,
            Height = 3,
        };

        var box = new Rect(0, 0, 200, 100);
        void Paint(DrawingContext ctx) => ctx.FillRectangle(Brushes.Black, new Rect(90, 0, 20, 100));

        FilterRenderer.Result? fromBox = FilterRenderer.Render(
            new[] { filter }, box, Matrix.Identity, 1.0, Paint, objectBounds: box);

        FilterRenderer.Result? fromAlpha = FilterRenderer.Render(
            new[] { filter }, box, Matrix.Identity, 1.0, Paint);

        Assert.NotNull(fromBox);
        Assert.NotNull(fromAlpha);

        // Measured as the alpha-weighted spread of the result rather than as a count of lit pixels: a wider blur
        // lays the same ink over more pixels, so the count depends on where the faint end is cut off and the spread
        // does not.
        double fromTheBox = SpreadX(fromBox!.Value.Bitmap);
        double fromTheAlpha = SpreadX(fromAlpha!.Value.Bitmap);

        Assert.True(fromTheBox > fromTheAlpha * 1.5,
            $"blurring half the box should reach further than blurring half the bar: " +
            $"{fromTheBox:0.0} against {fromTheAlpha:0.0} pixels of spread");
    }

    /// <summary>
    /// How far the result spreads sideways, as the standard deviation of its alpha about its own centre of mass.
    ///
    /// A Gaussian convolution adds its own variance to the source's, so this is sigma plus the bar's own width and
    /// does not depend on where a faint tail is called nothing.
    /// </summary>
    private static double SpreadX(Avalonia.Media.Imaging.WriteableBitmap bitmap)
    {
        using Avalonia.Platform.ILockedFramebuffer locked = bitmap.Lock();

        double total = 0, sum = 0;
        for (int y = 0; y < locked.Size.Height; y++)
        {
            for (int x = 0; x < locked.Size.Width; x++)
            {
                double alpha = AlphaAt(locked, x, y);
                total += alpha;
                sum += alpha * x;
            }
        }

        if (total <= 0)
        {
            return 0;
        }

        double mean = sum / total;
        double second = 0;
        for (int y = 0; y < locked.Size.Height; y++)
        {
            for (int x = 0; x < locked.Size.Width; x++)
            {
                double offset = x - mean;
                second += AlphaAt(locked, x, y) * offset * offset;
            }
        }

        return Math.Sqrt(second / total);
    }

    private static double AlphaAt(Avalonia.Platform.ILockedFramebuffer locked, int x, int y)
        => System.Runtime.InteropServices.Marshal.ReadByte(
            locked.Address, (y * locked.RowBytes) + (x * 4) + 3) / 255.0;
}
