using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

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
