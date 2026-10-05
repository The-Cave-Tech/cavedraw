using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using VCCad.App.Capture;
using VCCad.App.Controls;

namespace VCCad.App.Views;

/// <summary>
/// Renders a window to PNG so the assistant (and any automation client) can look
/// at the workspace. The agent is text-only, so this is how it "sees": the PNG is
/// attached to the next model request as an image.
/// </summary>
public static class ScreenCapture
{
    /// <summary>Captures a window's visual tree, or null when it has no size yet.</summary>
    public static byte[]? CaptureWindow(Window? window, double maxWidth = 1600)
    {
        if (window is null)
        {
            return null;
        }

        return Dispatcher.UIThread.CheckAccess()
            ? Capture(window, maxWidth)
            : Dispatcher.UIThread.Invoke(() => Capture(window, maxWidth));
    }

    /// <summary>
    /// The same capture as <see cref="CaptureWindow"/>, as raw pixels instead of a PNG.
    ///
    /// A recording cannot afford a PNG encode per frame, and an encoder wants the pixels anyway. Downscaled the
    /// same way, so a recording and the still that accompanied it show the same thing at the same size.
    /// </summary>
    public static CaptureFrame? CaptureBgra(Avalonia.Controls.Window? window, double maxWidth = 1280)
    {
        if (window is null)
        {
            return null;
        }

        return Dispatcher.UIThread.CheckAccess()
            ? CaptureRaw(window, maxWidth)
            : Dispatcher.UIThread.Invoke(() => CaptureRaw(window, maxWidth));
    }

    private static CaptureFrame? CaptureRaw(Avalonia.Controls.Window window, double maxWidth)
    {
        Size size = window.ClientSize;
        if (size.Width < 1 || size.Height < 1)
        {
            return null;
        }

        double scale = size.Width > maxWidth ? maxWidth / size.Width : 1.0;
        var pixelSize = new PixelSize(
            Math.Max(1, (int)Math.Round(size.Width * scale)),
            Math.Max(1, (int)Math.Round(size.Height * scale)));

        try
        {
            ShowCaret(window);
            using var bitmap = new RenderTargetBitmap(pixelSize, new Vector(96 * scale, 96 * scale));
            bitmap.Render(window);

            int stride = pixelSize.Width * 4;
            var buffer = new byte[stride * pixelSize.Height];
            System.Runtime.InteropServices.GCHandle handle =
                System.Runtime.InteropServices.GCHandle.Alloc(
                    buffer, System.Runtime.InteropServices.GCHandleType.Pinned);
            try
            {
                bitmap.CopyPixels(new PixelRect(pixelSize), handle.AddrOfPinnedObject(), buffer.Length, stride);
            }
            finally
            {
                handle.Free();
            }

            return new CaptureFrame(buffer, pixelSize.Width, pixelSize.Height, stride);
        }
        catch (Exception)
        {
            // A dropped frame must never break a recording or a chat turn.
            return null;
        }
    }

    /// <summary>
    /// **Show any caret before the frame is taken** (issue #249).
    ///
    /// The caret blinks, and the paint is gated on that state, so a capture taken between two ticks had no caret
    /// in it - and a driver cannot tell "the caret is elsewhere" from "the blink is off". A capture shows the
    /// state, so an editing canvas is asked to show its caret first.
    /// </summary>
    private static void ShowCaret(Window window)
    {
        foreach (CanvasWorkspace canvas in window.GetVisualDescendants().OfType<CanvasWorkspace>())
        {
            canvas.ShowCaretForCapture();
        }
    }
    private static byte[]? Capture(Window window, double maxWidth)
    {
        Size size = window.ClientSize;
        if (size.Width < 1 || size.Height < 1)
        {
            return null;
        }

        // Downscale very large windows: the model does not need full resolution and
        // a huge image costs tokens and upload time.
        double scale = size.Width > maxWidth ? maxWidth / size.Width : 1.0;
        var pixelSize = new PixelSize(
            Math.Max(1, (int)Math.Round(size.Width * scale)),
            Math.Max(1, (int)Math.Round(size.Height * scale)));

        try
        {
            ShowCaret(window);
            using var bitmap = new RenderTargetBitmap(pixelSize, new Vector(96 * scale, 96 * scale));
            bitmap.Render(window);
            using var stream = new MemoryStream();
            bitmap.Save(stream);
            return stream.ToArray();
        }
        catch (Exception)
        {
            // A capture must never break a chat turn or an API call.
            return null;
        }
    }
}
