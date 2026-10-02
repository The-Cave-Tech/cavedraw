using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using VCCad.App.Controls;
using VCCad.App.ViewModels;
using VCCad.Core.Model;
using VCCad.Core.Svg;
using VCCad.Geometry;
using Xunit;

namespace VCCad.App.Tests;

/// <summary>
/// **A `dy` list reaches the canvas, not only the layout.**
///
/// Found by taking a picture of the fix: the model carried the offsets, the layout applied them, the geometry test
/// passed - and the canvas still drew the three characters on one line, because it draws a run as a whole. That is
/// this project's recurring defect shape, and only a pixel frame could see it.
/// </summary>
public class PositionOffsetCanvasTests
{
    private const int Width = 760, Height = 480;

    private static void Settle()
    {
        for (int i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    /// <summary>The ink's height in pixels for a text block, drawn at a known zoom.</summary>
    private static int InkHeight(string attributes)
    {
        var viewModel = new EditorViewModel();
        var workspace = new CanvasWorkspace();
        workspace.AttachEditor(viewModel);

        var window = new Window { Width = Width, Height = Height, Content = workspace };
        window.Show();
        Settle();

        SvgImportResult result = SvgReader.Read(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"400\" height=\"400\">" +
            $"<text x=\"60\" y=\"120\" font-size=\"48\" fill=\"#ff0000\" {attributes}>abc</text></svg>");

        TextItem text = result.Document.AllItems().OfType<TextItem>().Single();
        viewModel.Document.Artboards[0].Layers[0].AddItem(text);

        Rect2D bounds = text.LocalBounds();
        workspace.CenterOn(new Point2D(bounds.X + (bounds.Width / 2), bounds.Y + (bounds.Height / 2)));
        workspace.InvalidateVisual();
        Settle();

        var target = new RenderTargetBitmap(new PixelSize(Width, Height), new Vector(96, 96));
        target.Render(window);

        int stride = Width * 4;
        byte[] pixels = new byte[stride * Height];
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(
            pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            target.CopyPixels(new PixelRect(0, 0, Width, Height), handle.AddrOfPinnedObject(),
                pixels.Length, stride);
        }
        finally
        {
            handle.Free();
        }

        int minY = Height, maxY = -1;
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int i = (y * stride) + (x * 4);

                // **The text is red so it can be found in a frame that holds more than the artboard.** Keying on
                // "dark" caught the window's own chrome and measured the whole frame; nothing else here is red.
                if (pixels[i + 2] > 200 && pixels[i + 1] < 80 && pixels[i] < 80)
                {
                    if (y < minY) { minY = y; }
                    if (y > maxY) { maxY = y; }
                }
            }
        }

        return maxY < 0 ? 0 : maxY - minY + 1;
    }

    /// <summary>
    /// **The acceptance.** Three characters with `dy="0 40 80"` cover far more rows than the same text with no
    /// list, because each one sits lower than the last. Against a canvas that draws the run as a whole both measure
    /// one line.
    /// </summary>
    [AvaloniaFact]
    public void ADyListSpreadsTheInkDownThePage()
    {
        int plain = InkHeight(string.Empty);
        int stepped = InkHeight("dy=\"0 40 80\"");

        Assert.True(plain > 0, "the fixture must draw something");
        Assert.True(stepped > plain + 40,
            $"a dy list must step the characters down the page: plain {plain}px, stepped {stepped}px");
    }
}
