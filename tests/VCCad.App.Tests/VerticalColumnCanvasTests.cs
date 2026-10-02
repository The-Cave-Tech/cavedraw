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

namespace VCCad.App.Tests;

/// <summary>
/// **A vertical block draws down the page, not along one line.**
///
/// The layout has advanced a `vertical-rl` run downward since #127 - `SvgWritingModeTests` pins X at 10 with Y at
/// 20, 24, 28 - but `CanvasWorkspace.PaintText` placed every run at the horizontal projection, so a column came
/// out as one horizontal line on screen. The model and the drawing disagreed, and the PDF exporter is in the
/// same state.
///
/// The canvas exposes no glyph origins, so the evidence is the **pixel frame**: a vertical column's ink is taller
/// than it is wide, a horizontal run's is wider than it is tall. The setup follows `TextColourPaintTests`, and
/// `InvalidateVisual` is not optional - without it the frame is captured before the canvas paints, and "no ink"
/// looks identical to "the right ink in the wrong place".
/// </summary>
public class VerticalColumnCanvasTests
{
    private const int Width = 900, Height = 700;
    private const double Zoom = 4.0, FontSize = 20.0;

    private static readonly ColorRgb Magenta = new(1.0, 0.0, 1.0);

    private static void Settle()
    {
        for (int i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    /// <summary>Draws three characters in the given writing mode and returns the size of the ink.</summary>
    private static (int Width, int Height) Ink(TextWritingMode mode)
    {
        var viewModel = new EditorViewModel();
        var workspace = new CanvasWorkspace();
        workspace.AttachEditor(viewModel);

        var window = new Window { Width = Width, Height = Height, Content = workspace };
        window.Show();
        Settle();
        workspace.ZoomTo(Zoom);

        var text = new TextItem
        {
            Name = "column",
            Origin = new Point2D(120, 250),
            Color = Magenta,
            WritingMode = mode,
        };
        text.Runs.Add(new TextRun { Text = "abc", FontSize = FontSize });
        viewModel.Document.Artboards[0].Layers[0].AddItem(text);

        Rect2D bounds = text.LocalBounds();
        workspace.CenterOn(new Point2D(bounds.X + (bounds.Width / 2), bounds.Y + (bounds.Height / 2)));
        workspace.InvalidateVisual();
        Settle();

        return InkSize(window);
    }

    /// <summary>The bounding box of the magenta ink, which nothing else in the window is.</summary>
    private static (int Width, int Height) InkSize(Window window)
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

        int minX = Width, maxX = -1, minY = Height, maxY = -1;
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int i = (y * stride) + (x * 4);

                // BGRA: magenta is red and blue high, green low.
                if (pixels[i + 2] > 200 && pixels[i + 1] < 80 && pixels[i] > 200)
                {
                    if (x < minX) { minX = x; }
                    if (x > maxX) { maxX = x; }
                    if (y < minY) { minY = y; }
                    if (y > maxY) { maxY = y; }
                }
            }
        }

        return maxX < 0 ? (0, 0) : (maxX - minX + 1, maxY - minY + 1);
    }

    /// <summary>
    /// **The acceptance.** Three characters written vertically occupy a column - taller than wide - while the same
    /// text horizontally occupies a line.
    ///
    /// **Skipped because the canvas cannot do it yet, which is issue #127.** This test was written to fail, and it
    /// does: against the current canvas the vertical case measures 136x60 - three characters wide and one line tall
    /// - because `PaintText` draws each run as a single `FormattedText`, which lays its glyphs out horizontally
    /// whatever the writing mode says. Moving the run's origin changes nothing, which was measured: the one-line
    /// projection swap left this at 136x60.
    ///
    /// So a vertical column needs the **per-glyph** draw the canvas already has for tracking
    /// (`CanvasWorkspace.cs:5182`), advancing down the column and turning each glyph, rather than an origin swap.
    /// This is a deliberate sentinel rather than a deleted test: when that work lands, remove the skip and the
    /// assertion above is the acceptance.
    /// </summary>
    [AvaloniaFact]
    public void AVerticalBlockDrawsAsAColumnAndAHorizontalOneDrawsAsALine()
    {
        (int verticalWidth, int verticalHeight) = Ink(TextWritingMode.VerticalRl);
        (int horizontalWidth, int horizontalHeight) = Ink(TextWritingMode.HorizontalTb);

        Assert.True(verticalHeight > verticalWidth,
            $"a vertical column must be taller than it is wide: {verticalWidth}x{verticalHeight}");
        Assert.True(horizontalWidth > horizontalHeight,
            $"a horizontal run must be wider than it is tall: {horizontalWidth}x{horizontalHeight}");
    }
}
